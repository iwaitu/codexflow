import { app, BrowserWindow, clipboard, dialog, ipcMain, Menu, Notification, shell, type IpcMainInvokeEvent } from 'electron';
import { existsSync, lstatSync, realpathSync } from 'node:fs';
import { join, relative, resolve, sep } from 'node:path';
import {
  APP_METADATA,
  IPC_CHANNELS,
  type AppUpdateState,
  type ClassifiedDroppedFile,
  type ConnectStandalonePayload,
  type CreateBusinessPluginRequest,
  type CredentialSlotKind,
  type DesktopCommandAction,
  type DesktopLanguage,
  type DesktopNotificationPayload,
  type DesktopPreferencesPayload,
  type DesktopTheme,
  type DroppedFileClassifyInput,
  type GenerateSkillCandidateRequest,
  type OpenLocalWorkspacePayload,
  type PlanChatControlPayload,
  type PublishSkillDraftRequest,
  type PutDraftSelectionRequest,
  type SetPlanModePayload,
  type SetPluginCredentialRequest,
  type StartSkillRunRequest,
  type SubmitToolApprovalPayload,
  type UpdateCapabilityRiskRequest,
  type UpdateExecutionSettingsPayload,
  type UpdateModelSettingsPayload,
  type UpdatePluginConnectionRequest,
} from '../../src/shared/ipc';
import { buildApplicationMenuTemplate, buildContextMenuTemplate } from './appMenu';
import { BackendManager } from './backendManager';
import { callControlPlane } from './controlPlaneProxy';
import { LocalBackendProxy } from './localBackendProxy';
import {
  APP_ORIGIN,
  attachCspHeaderHook,
  isPathStrictlyInsideRoot,
  registerAppProtocolHandler,
  registerAppSchemePrivileges,
} from './protocol';
import { isTrustedRendererOrigin, validateIpcCaller } from './securityUtils';
import { TrayManager } from './trayManager';
import { UpdaterManager } from './updaterManager';

registerAppSchemePrivileges();

const isDev = Boolean(process.env.ELECTRON_RENDERER_URL);
let mainWindow: BrowserWindow | null = null;
let backendManager: BackendManager | null = null;
let gatewayProxy: LocalBackendProxy | null = null;
let trayManager: TrayManager | null = null;
let updaterManager: UpdaterManager | null = null;
let isQuitting = false;
const desktopPreferences: {
  theme: DesktopTheme;
  language: DesktopLanguage;
  minimizeToTrayOnClose: boolean;
} = {
  theme: 'dark',
  language: 'zh-CN',
  minimizeToTrayOnClose: false,
};

function getAllowedRendererOrigins(): Set<string> {
  const allowed = new Set<string>([APP_ORIGIN]);
  if (isDev && process.env.ELECTRON_RENDERER_URL) {
    try {
      allowed.add(new URL(process.env.ELECTRON_RENDERER_URL).origin);
    } catch {
      // ignore malformed dev URL
    }
  }
  return allowed;
}

function isTrustedOrigin(rawUrl: string): boolean {
  return isTrustedRendererOrigin(rawUrl, getAllowedRendererOrigins());
}

function assertTrustedIpcSender(event: IpcMainInvokeEvent): void {
  const frame = event.senderFrame;
  validateIpcCaller({
    hasWindow: Boolean(mainWindow),
    windowDestroyed: mainWindow?.isDestroyed() ?? true,
    senderMatchesWindow: Boolean(mainWindow && event.sender === mainWindow.webContents),
    hasSenderFrame: Boolean(frame),
    senderFrameMatchesMainFrame: Boolean(frame && mainWindow && frame === mainWindow.webContents.mainFrame),
    frameUrl: frame?.url ?? '',
    allowedOrigins: getAllowedRendererOrigins(),
  });
}

async function openSafeExternalUrl(rawUrl: string): Promise<boolean> {
  try {
    const parsed = new URL(rawUrl);
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
      return false;
    }
    await shell.openExternal(parsed.toString());
    return true;
  } catch {
    return false;
  }
}

function resolveAppIconPath(): string | undefined {
  const candidates = [
    resolve(__dirname, '../renderer/icon.png'),
    resolve(process.resourcesPath, 'icon.png'),
    resolve(__dirname, '../../build/icon.png'),
  ];
  return candidates.find((p) => existsSync(p));
}

function restoreMainWindow(): void {
  if (!mainWindow || mainWindow.isDestroyed()) {
    mainWindow = createMainWindow();
    return;
  }
  if (mainWindow.isMinimized()) {
    mainWindow.restore();
  }
  mainWindow.show();
  mainWindow.focus();
  trayManager?.refreshMenu();
}

function dispatchCommandAction(action: DesktopCommandAction): void {
  if (!mainWindow || mainWindow.isDestroyed()) {
    restoreMainWindow();
  }
  if (!mainWindow || mainWindow.isDestroyed()) {
    return;
  }
  if (mainWindow.webContents.isLoading()) {
    mainWindow.webContents.once('did-finish-load', () => {
      if (mainWindow && !mainWindow.isDestroyed()) {
        mainWindow.webContents.send(IPC_CHANNELS.commandAction, action);
      }
    });
    return;
  }
  mainWindow.webContents.send(IPC_CHANNELS.commandAction, action);
}

function refreshApplicationMenu(): void {
  if (typeof app.setAboutPanelOptions === 'function') {
    app.setAboutPanelOptions({
      applicationName: APP_METADATA.name,
      applicationVersion: app.getVersion(),
      copyright: APP_METADATA.copyright,
      website: APP_METADATA.projectUrl,
      authors: [APP_METADATA.author],
    });
  }

  const template = buildApplicationMenuTemplate({
    language: desktopPreferences.language,
    isDev,
    onDispatchCommand: (action) => {
      restoreMainWindow();
      dispatchCommandAction(action);
    },
    onOpenExternal: (url) => {
      void openSafeExternalUrl(url);
    },
    onQuit: () => {
      isQuitting = true;
      app.quit();
    },
  });
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

function broadcastUpdateState(state: AppUpdateState): void {
  if (!mainWindow || mainWindow.isDestroyed()) {
    return;
  }
  mainWindow.webContents.send(IPC_CHANNELS.updateStateChanged, state);
}

function broadcastConnectionChange(): void {
  trayManager?.refreshMenu();
  if (!backendManager || !mainWindow || mainWindow.isDestroyed()) {
    return;
  }
  mainWindow.webContents.send(IPC_CHANNELS.backendConnectionChanged, backendManager.getPublicInfo());
}

function createMainWindow(): BrowserWindow {
  const windowIconPath = resolveAppIconPath();
  const win = new BrowserWindow({
    width: 1380,
    height: 900,
    minWidth: 1024,
    minHeight: 680,
    show: false,
    title: 'CodexFlow Desktop',
    ...(windowIconPath ? { icon: windowIconPath } : {}),
    backgroundColor: desktopPreferences.theme === 'light' ? '#f8fafc' : '#0b0f17',
    webPreferences: {
      preload: join(__dirname, '../preload/index.cjs'),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
      webSecurity: true,
      allowRunningInsecureContent: false,
      webviewTag: false,
    },
  });

  win.webContents.setWindowOpenHandler(({ url }) => {
    void openSafeExternalUrl(url);
    return { action: 'deny' };
  });

  win.webContents.on('will-navigate', (event, navigationUrl) => {
    if (isTrustedOrigin(navigationUrl)) {
      return;
    }
    event.preventDefault();
    void openSafeExternalUrl(navigationUrl);
  });

  win.webContents.on('will-attach-webview', (event) => {
    event.preventDefault();
  });

  win.webContents.on('context-menu', (_event, params) => {
    const template = buildContextMenuTemplate(params, desktopPreferences.language, {
      onOpenLink: (url) => {
        void openSafeExternalUrl(url);
      },
      onCopyText: (text) => {
        clipboard.writeText(text);
      },
    });
    if (template.length > 0) {
      Menu.buildFromTemplate(template).popup({ window: win });
    }
  });

  win.once('ready-to-show', () => {
    win.show();
    trayManager?.refreshMenu();
  });

  win.on('show', () => {
    trayManager?.refreshMenu();
  });

  win.on('hide', () => {
    trayManager?.refreshMenu();
  });

  win.on('close', (event) => {
    if (!isQuitting && desktopPreferences.minimizeToTrayOnClose) {
      event.preventDefault();
      win.hide();
      trayManager?.refreshMenu();
    }
  });

  if (isDev && process.env.ELECTRON_RENDERER_URL) {
    void win.loadURL(process.env.ELECTRON_RENDERER_URL);
  } else {
    void win.loadURL(`${APP_ORIGIN}/index.html`);
  }

  return win;
}

function registerIpcHandlers(manager: BackendManager): void {
  ipcMain.handle(IPC_CHANNELS.getBackendConnection, (event) => {
    assertTrustedIpcSender(event);
    return manager.getPublicInfo();
  });

  ipcMain.handle(IPC_CHANNELS.approveToolExecution, async (event, payload: SubmitToolApprovalPayload) => {
    assertTrustedIpcSender(event);
    return callControlPlane(
      manager.getControlPlaneCredentials(),
      'POST',
      `/api/gateway/approvals/${encodeURIComponent(payload.approvalId)}`,
      {
        requestDigest: payload.requestDigest,
        decision: payload.decision,
        note: payload.note,
      },
    );
  });

  ipcMain.handle(IPC_CHANNELS.getApprovalScript, async (event, approvalId: string) => {
    assertTrustedIpcSender(event);
    return callControlPlane(
      manager.getControlPlaneCredentials(),
      'GET',
      `/api/gateway/approvals/${encodeURIComponent(approvalId)}/script`,
    );
  });

  ipcMain.handle(IPC_CHANNELS.setSessionPlanMode, async (event, payload: SetPlanModePayload) => {
    assertTrustedIpcSender(event);
    return callControlPlane(
      manager.getControlPlaneCredentials(),
      'POST',
      `/api/gateway/session/${encodeURIComponent(payload.sessionId)}/plan-mode`,
      {
        active: payload.active,
        expectedPlanModeVersion: payload.expectedPlanModeVersion,
        reason: payload.reason,
        objective: payload.objective,
      },
    );
  });

  ipcMain.handle(IPC_CHANNELS.submitPlanChatAction, async (event, payload: PlanChatControlPayload) => {
    assertTrustedIpcSender(event);
    return callControlPlane(manager.getControlPlaneCredentials(), 'POST', '/api/gateway/chat', payload);
  });

  ipcMain.handle(IPC_CHANNELS.updateExecutionSettings, async (event, payload: UpdateExecutionSettingsPayload) => {
    assertTrustedIpcSender(event);
    return callControlPlane(manager.getControlPlaneCredentials(), 'PUT', '/api/settings/execution', payload);
  });

  ipcMain.handle(IPC_CHANNELS.updateModelSettings, async (event, payload: UpdateModelSettingsPayload) => {
    assertTrustedIpcSender(event);
    return callControlPlane(manager.getControlPlaneCredentials(), 'PUT', '/api/settings/model', payload);
  });

  ipcMain.handle(IPC_CHANNELS.listPermissionRules, async (event) => {
    assertTrustedIpcSender(event);
    return callControlPlane(manager.getControlPlaneCredentials(), 'GET', '/api/settings/permissions');
  });

  ipcMain.handle(IPC_CHANNELS.deletePermissionRule, async (event, ruleId: string) => {
    assertTrustedIpcSender(event);
    return callControlPlane(
      manager.getControlPlaneCredentials(),
      'DELETE',
      `/api/settings/permissions/${encodeURIComponent(ruleId)}`,
    );
  });

  ipcMain.handle(IPC_CHANNELS.openLocalWorkspace, async (event, payload: OpenLocalWorkspacePayload) => {
    assertTrustedIpcSender(event);
    return callControlPlane(manager.getControlPlaneCredentials(), 'POST', '/api/gateway/workspaces', payload);
  });

  ipcMain.handle(IPC_CHANNELS.createBusinessPlugin, async (event, payload: CreateBusinessPluginRequest) => {
    assertTrustedIpcSender(event);
    return callControlPlane(manager.getControlPlaneCredentials(), 'POST', '/api/plugins', payload);
  });

  ipcMain.handle(
    IPC_CHANNELS.updateBusinessPluginConnection,
    async (event, payload: { pluginId: string } & UpdatePluginConnectionRequest) => {
      assertTrustedIpcSender(event);
      const { pluginId, ...body } = payload;
      return callControlPlane(
        manager.getControlPlaneCredentials(),
        'PUT',
        `/api/plugins/${encodeURIComponent(pluginId)}/connection`,
        body,
      );
    },
  );

  ipcMain.handle(
    IPC_CHANNELS.setBusinessPluginCredential,
    async (event, payload: { pluginId: string; slot: CredentialSlotKind } & SetPluginCredentialRequest) => {
      assertTrustedIpcSender(event);
      if (payload.slot !== 'document' && payload.slot !== 'execution') {
        throw new Error('Invalid credential slot');
      }
      const { pluginId, slot, ...body } = payload;
      return callControlPlane(
        manager.getControlPlaneCredentials(),
        'PUT',
        `/api/plugins/${encodeURIComponent(pluginId)}/credentials/${slot}`,
        body,
      );
    },
  );

  ipcMain.handle(
    IPC_CHANNELS.deleteBusinessPluginCredential,
    async (event, payload: { pluginId: string; slot: CredentialSlotKind; unbindIfReferenced?: boolean }) => {
      assertTrustedIpcSender(event);
      if (payload.slot !== 'document' && payload.slot !== 'execution') {
        throw new Error('Invalid credential slot');
      }
      const query = payload.unbindIfReferenced ? '?unbindIfReferenced=true' : '';
      return callControlPlane(
        manager.getControlPlaneCredentials(),
        'DELETE',
        `/api/plugins/${encodeURIComponent(payload.pluginId)}/credentials/${payload.slot}${query}`,
      );
    },
  );

  ipcMain.handle(IPC_CHANNELS.disableBusinessPlugin, async (event, pluginId: string) => {
    assertTrustedIpcSender(event);
    return callControlPlane(
      manager.getControlPlaneCredentials(),
      'POST',
      `/api/plugins/${encodeURIComponent(pluginId)}/disable`,
      {},
    );
  });

  ipcMain.handle(
    IPC_CHANNELS.discoverBusinessPlugin,
    async (event, payload: { pluginId: string; clientRequestId: string }) => {
      assertTrustedIpcSender(event);
      return callControlPlane(
        manager.getControlPlaneCredentials(),
        'POST',
        `/api/plugins/${encodeURIComponent(payload.pluginId)}/discover`,
        { clientRequestId: payload.clientRequestId },
        60_000,
      );
    },
  );

  ipcMain.handle(
    IPC_CHANNELS.updateBusinessCapabilityRisk,
    async (event, payload: { pluginId: string } & UpdateCapabilityRiskRequest) => {
      assertTrustedIpcSender(event);
      const { pluginId, ...body } = payload;
      return callControlPlane(
        manager.getControlPlaneCredentials(),
        'PUT',
        `/api/plugins/${encodeURIComponent(pluginId)}/capabilities/risk`,
        body,
      );
    },
  );

  ipcMain.handle(
    IPC_CHANNELS.updateSkillDraftSelection,
    async (event, payload: { draftId: string } & PutDraftSelectionRequest) => {
      assertTrustedIpcSender(event);
      const { draftId, ...body } = payload;
      return callControlPlane(
        manager.getControlPlaneCredentials(),
        'PUT',
        `/api/skill-drafts/${encodeURIComponent(draftId)}/selection`,
        body,
      );
    },
  );

  ipcMain.handle(
    IPC_CHANNELS.generateSkillDraftCandidate,
    async (event, payload: { draftId: string } & GenerateSkillCandidateRequest) => {
      assertTrustedIpcSender(event);
      const { draftId, ...body } = payload;
      return callControlPlane(
        manager.getControlPlaneCredentials(),
        'POST',
        `/api/skill-drafts/${encodeURIComponent(draftId)}/generate`,
        body,
        60_000,
      );
    },
  );

  ipcMain.handle(
    IPC_CHANNELS.startSkillDraftTestRun,
    async (event, payload: { draftId: string } & StartSkillRunRequest) => {
      assertTrustedIpcSender(event);
      const { draftId, ...body } = payload;
      return callControlPlane(
        manager.getControlPlaneCredentials(),
        'POST',
        `/api/skill-drafts/${encodeURIComponent(draftId)}/test-runs`,
        body,
        30_000,
      );
    },
  );

  ipcMain.handle(
    IPC_CHANNELS.publishSkillDraft,
    async (event, payload: { draftId: string } & PublishSkillDraftRequest) => {
      assertTrustedIpcSender(event);
      const { draftId, ...body } = payload;
      return callControlPlane(
        manager.getControlPlaneCredentials(),
        'POST',
        `/api/skill-drafts/${encodeURIComponent(draftId)}/publish`,
        body,
      );
    },
  );

  ipcMain.handle(
    IPC_CHANNELS.activateSkillVersion,
    async (event, payload: { skillId: string; version: number }) => {
      assertTrustedIpcSender(event);
      return callControlPlane(
        manager.getControlPlaneCredentials(),
        'POST',
        `/api/skills/${encodeURIComponent(payload.skillId)}/activate-version`,
        { version: payload.version },
      );
    },
  );

  ipcMain.handle(
    IPC_CHANNELS.startPublishedSkillRun,
    async (event, payload: { skillId: string } & StartSkillRunRequest) => {
      assertTrustedIpcSender(event);
      const { skillId, ...body } = payload;
      return callControlPlane(
        manager.getControlPlaneCredentials(),
        'POST',
        `/api/skills/${encodeURIComponent(skillId)}/runs`,
        body,
        30_000,
      );
    },
  );

  ipcMain.handle(IPC_CHANNELS.cancelBusinessSkillRun, async (event, runId: string) => {
    assertTrustedIpcSender(event);
    return callControlPlane(
      manager.getControlPlaneCredentials(),
      'POST',
      `/api/skill-runs/${encodeURIComponent(runId)}/cancel`,
      {},
    );
  });

  ipcMain.handle(
    IPC_CHANNELS.pruneBusinessSkillRetention,
    async (event, payload: Record<string, unknown> = {}) => {
      assertTrustedIpcSender(event);
      return callControlPlane(
        manager.getControlPlaneCredentials(),
        'POST',
        '/api/plugins/maintenance/prune',
        payload ?? {},
      );
    },
  );

  ipcMain.handle(IPC_CHANNELS.pickFolder, async (event) => {
    assertTrustedIpcSender(event);
    const targetWindow = mainWindow ?? undefined;
    const result = targetWindow
      ? await dialog.showOpenDialog(targetWindow, { properties: ['openDirectory'] })
      : await dialog.showOpenDialog({ properties: ['openDirectory'] });
    return result.canceled ? null : (result.filePaths[0] ?? null);
  });

  ipcMain.handle(IPC_CHANNELS.classifyDroppedFiles, (event, payload: DroppedFileClassifyInput): ClassifiedDroppedFile[] => {
    assertTrustedIpcSender(event);
    let realRoot: string | null = null;
    if (payload.workspacePath) {
      try {
        realRoot = realpathSync(resolve(payload.workspacePath));
      } catch {
        realRoot = null;
      }
    }

    const results: ClassifiedDroppedFile[] = [];
    for (const rawPath of payload.filePaths ?? []) {
      if (typeof rawPath !== 'string' || !rawPath.trim()) continue;
      try {
        const stat = lstatSync(rawPath);
        const isDir = stat.isDirectory();
        let workspaceRelativePath: string | null = null;
        if (!isDir && realRoot) {
          try {
            const realTarget = realpathSync(rawPath);
            if (isPathStrictlyInsideRoot(realRoot, realTarget)) {
              const rel = relative(realRoot, realTarget).split(sep).join('/');
              const segs = rel.split('/');
              const isProtected = segs.some((s) =>
                ['.git', '.gitmodules', '.codexflow', 'host.token', 'control.token'].includes(s.toLowerCase()),
              );
              if (rel && !isProtected) {
                workspaceRelativePath = rel;
              }
            }
          } catch {
            workspaceRelativePath = null;
          }
        }
        results.push({
          filePath: rawPath,
          exists: true,
          isDirectory: isDir,
          sizeBytes: isDir ? 0 : stat.size,
          workspaceRelativePath,
        });
      } catch {
        results.push({
          filePath: rawPath,
          exists: false,
          isDirectory: false,
          sizeBytes: 0,
          workspaceRelativePath: null,
        });
      }
    }
    return results;
  });

  ipcMain.handle(IPC_CHANNELS.pickStandaloneTokenFile, async (event, kind: 'access' | 'control') => {
    assertTrustedIpcSender(event);
    if (kind !== 'access' && kind !== 'control') {
      throw new Error('Invalid token file role');
    }
    const targetWindow = mainWindow ?? undefined;
    const options: Electron.OpenDialogOptions = {
      title: kind === 'control' ? '选择 control.token 文件' : '选择 host.token 文件',
      properties: ['openFile'],
    };
    const result = targetWindow
      ? await dialog.showOpenDialog(targetWindow, options)
      : await dialog.showOpenDialog(options);
    if (result.canceled || !result.filePaths[0]) {
      return null;
    }
    return manager.registerPickedTokenFile(kind, result.filePaths[0]);
  });

  ipcMain.handle(IPC_CHANNELS.discoverStandaloneInstance, (event, dataDir?: string) => {
    assertTrustedIpcSender(event);
    return manager.discoverStandaloneInstance(dataDir);
  });

  ipcMain.handle(IPC_CHANNELS.connectStandalone, (event, payload: ConnectStandalonePayload) => {
    assertTrustedIpcSender(event);
    return manager.connectStandalone(payload);
  });

  ipcMain.handle(IPC_CHANNELS.switchToManaged, (event) => {
    assertTrustedIpcSender(event);
    return manager.startManaged();
  });

  ipcMain.handle(IPC_CHANNELS.restartBackend, (event) => {
    assertTrustedIpcSender(event);
    return manager.restartManaged();
  });

  ipcMain.handle(IPC_CHANNELS.openExternal, async (event, rawUrl: string) => {
    assertTrustedIpcSender(event);
    return openSafeExternalUrl(rawUrl);
  });

  ipcMain.handle(IPC_CHANNELS.showNotification, (event, payload: DesktopNotificationPayload) => {
    assertTrustedIpcSender(event);
    if (
      payload.onlyWhenUnfocused &&
      mainWindow &&
      !mainWindow.isDestroyed() &&
      mainWindow.isFocused() &&
      mainWindow.isVisible()
    ) {
      return;
    }
    if (Notification.isSupported()) {
      const iconPath = resolveAppIconPath();
      const notification = new Notification({
        title: payload.title,
        body: payload.body,
        ...(iconPath ? { icon: iconPath } : {}),
      });
      notification.on('click', () => {
        restoreMainWindow();
      });
      notification.show();
    }
  });

  ipcMain.handle(IPC_CHANNELS.setDesktopPreferences, (event, payload: DesktopPreferencesPayload) => {
    assertTrustedIpcSender(event);
    if (payload.theme === 'dark' || payload.theme === 'light') {
      desktopPreferences.theme = payload.theme;
      if (mainWindow && !mainWindow.isDestroyed()) {
        mainWindow.setBackgroundColor(payload.theme === 'light' ? '#f8fafc' : '#0b0f17');
      }
    }
    if (payload.language === 'zh-CN' || payload.language === 'en-US') {
      desktopPreferences.language = payload.language;
    }
    if (typeof payload.minimizeToTrayOnClose === 'boolean') {
      desktopPreferences.minimizeToTrayOnClose = payload.minimizeToTrayOnClose;
    }
    trayManager?.refreshMenu();
    refreshApplicationMenu();
  });

  ipcMain.handle(IPC_CHANNELS.getUpdateState, (event) => {
    assertTrustedIpcSender(event);
    return (
      updaterManager?.getState() ?? {
        status: 'unavailable',
        currentVersion: app.getVersion(),
        availableVersion: null,
        downloadProgress: null,
        message: null,
      }
    );
  });

  ipcMain.handle(IPC_CHANNELS.checkForUpdates, (event) => {
    assertTrustedIpcSender(event);
    return updaterManager
      ? updaterManager.checkForUpdates()
      : Promise.resolve({
          status: 'unavailable' as const,
          currentVersion: app.getVersion(),
          availableVersion: null,
          downloadProgress: null,
          message: null,
        });
  });

  ipcMain.handle(IPC_CHANNELS.downloadUpdate, (event) => {
    assertTrustedIpcSender(event);
    return updaterManager
      ? updaterManager.downloadUpdate()
      : Promise.resolve({
          status: 'unavailable' as const,
          currentVersion: app.getVersion(),
          availableVersion: null,
          downloadProgress: null,
          message: null,
        });
  });

  ipcMain.handle(IPC_CHANNELS.quitAndInstall, (event) => {
    assertTrustedIpcSender(event);
    return updaterManager?.quitAndInstall() ?? false;
  });
}

const singleInstanceLock = app.requestSingleInstanceLock();
if (!singleInstanceLock) {
  app.quit();
} else {
  app.on('second-instance', () => {
    restoreMainWindow();
  });

  void app.whenReady().then(async () => {
    const rendererDistDir = resolve(__dirname, '../renderer');
    registerAppProtocolHandler(rendererDistDir);

    let devRendererOrigin: string | null = null;
    if (isDev && process.env.ELECTRON_RENDERER_URL) {
      try {
        devRendererOrigin = new URL(process.env.ELECTRON_RENDERER_URL).origin;
      } catch {
        devRendererOrigin = null;
      }
    }

    backendManager = new BackendManager({
      userDataDir: app.getPath('userData'),
      resourcesPath: process.resourcesPath,
      clientVersion: app.getVersion(),
      isPackaged: app.isPackaged,
      isDev,
      devRendererOrigin,
      getProxyOrigin: () => gatewayProxy?.getOrigin() ?? null,
      onChange: () => broadcastConnectionChange(),
    });

    updaterManager = new UpdaterManager({
      currentVersion: app.getVersion(),
      isPackaged: app.isPackaged,
      onStateChange: (state) => broadcastUpdateState(state),
      onPrepareInstall: () => {
        isQuitting = true;
        trayManager?.dispose();
      },
    });

    gatewayProxy = new LocalBackendProxy(
      () => backendManager?.getUpstreamState() ?? { targetBaseUrl: null, expectedAccessToken: null, controlToken: null },
      getAllowedRendererOrigins(),
    );
    const proxyOrigin = await gatewayProxy.start();

    attachCspHeaderHook(() => proxyOrigin, isDev);
    registerIpcHandlers(backendManager);
    refreshApplicationMenu();

    mainWindow = createMainWindow();
    mainWindow.on('closed', () => {
      mainWindow = null;
      trayManager?.refreshMenu();
    });

    try {
      trayManager = new TrayManager({
        iconPaths: [
          resolve(__dirname, '../renderer/icon.png'),
          resolve(process.resourcesPath, 'icon.png'),
          resolve(__dirname, '../../build/icon.png'),
          resolve(__dirname, '../../build/icon.ico'),
        ],
        getLanguage: () => desktopPreferences.language,
        getConnectionStatus: () => backendManager?.getPublicInfo().status ?? 'starting',
        isWindowVisible: () => Boolean(mainWindow && !mainWindow.isDestroyed() && mainWindow.isVisible()),
        onShowWindow: () => restoreMainWindow(),
        onHideWindow: () => {
          if (mainWindow && !mainWindow.isDestroyed()) {
            mainWindow.hide();
            trayManager?.refreshMenu();
          }
        },
        onDispatchCommand: (action) => dispatchCommandAction(action),
        onQuit: () => {
          isQuitting = true;
          app.quit();
        },
      });
      trayManager.initialize();
    } catch {
      trayManager = null;
    }

    // Launch managed backend without blocking initial window paint.
    void backendManager.startManaged();

    app.on('activate', () => {
      restoreMainWindow();
    });
  });

  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') {
      app.quit();
    }
  });

  let isDisposing = false;
  app.on('before-quit', (event) => {
    isQuitting = true;
    trayManager?.dispose();
    if ((backendManager || gatewayProxy) && !isDisposing) {
      isDisposing = true;
      event.preventDefault();
      void Promise.allSettled([
        backendManager?.dispose() ?? Promise.resolve(),
        gatewayProxy?.close() ?? Promise.resolve(),
      ]).finally(() => {
        app.exit(0);
      });
    }
  });
}
