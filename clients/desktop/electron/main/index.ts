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
  type EnvironmentDependencyId,
  type EnvironmentInstallEvent,
  type GenerateSkillCandidateRequest,
  type OpenLocalWorkspacePayload,
  type PlanChatControlPayload,
  type PublishSkillDraftRequest,
  type PutDraftSelectionRequest,
  type SetPlanModePayload,
  type SetPluginCredentialRequest,
  type StartSkillRunRequest,
  type SubmitToolApprovalPayload,
  type UpdateBrowserSettingsRequest,
  type UpdateCapabilityRiskRequest,
  type UpdateExecutionSettingsPayload,
  type UpdateModelSettingsPayload,
  type UpdatePluginConnectionRequest,
  type WorkspaceFileActionResult,
  type WorkspaceFileContextMenuPayload,
} from '../../src/shared/ipc';
import {
  buildApplicationMenuTemplate,
  buildContextMenuTemplate,
  buildWorkspaceFileContextMenuTemplate,
} from './appMenu';
import { BackendManager } from './backendManager';
import { callControlPlane } from './controlPlaneProxy';
import { EnvironmentManager, isValidEnvironmentDependencyId } from './environmentManager';
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
import {
  openWorkspaceFileWithDefaultApp,
  openWorkspaceTargetInFileManager,
  resolveSafeWorkspaceTarget,
} from './workspaceFiles';

registerAppSchemePrivileges();

const isDev = Boolean(process.env.ELECTRON_RENDERER_URL);
let mainWindow: BrowserWindow | null = null;
let backendManager: BackendManager | null = null;
let gatewayProxy: LocalBackendProxy | null = null;
let trayManager: TrayManager | null = null;
let updaterManager: UpdaterManager | null = null;
let environmentManager: EnvironmentManager | null = null;
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

function broadcastEnvironmentInstallEvent(payload: EnvironmentInstallEvent): void {
  if (!mainWindow || mainWindow.isDestroyed()) {
    return;
  }
  mainWindow.webContents.send(IPC_CHANNELS.environmentInstallEvent, payload);
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

  ipcMain.handle(IPC_CHANNELS.updateBrowserSettings, async (event, payload: unknown) => {
    assertTrustedIpcSender(event);
    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
      return {
        ok: false,
        status: 400,
        errorCode: 'invalid_browser_settings_payload',
        errorMessage: '浏览器设置参数必须是有效对象。',
      };
    }

    const raw = payload as Record<string, unknown>;
    const allowedKeys = new Set([
      'enabled',
      'executablePath',
      'headless',
      'profileMode',
      'interactionApprovalPolicy',
      'allowLocalhostInDailyMode',
      'allowControlledDownloads',
      'idleTimeoutMinutes',
      'maxConcurrentSessions',
    ]);
    for (const key of Object.keys(raw)) {
      if (!allowedKeys.has(key)) {
        return {
          ok: false,
          status: 400,
          errorCode: 'unexpected_browser_settings_field',
          errorMessage: `不允许通过桌面端提交未授权的浏览器配置字段：${key}`,
        };
      }
    }

    const sanitized: UpdateBrowserSettingsRequest = {};
    if (raw.enabled !== undefined) {
      if (typeof raw.enabled !== 'boolean') {
        return { ok: false, status: 400, errorCode: 'invalid_browser_settings_payload', errorMessage: 'enabled 必须为布尔值。' };
      }
      sanitized.enabled = raw.enabled;
    }
    if (raw.executablePath !== undefined && raw.executablePath !== null) {
      if (typeof raw.executablePath !== 'string' || raw.executablePath.length > 1024) {
        return { ok: false, status: 400, errorCode: 'invalid_browser_settings_payload', errorMessage: 'executablePath 必须为有效路径字符串。' };
      }
      sanitized.executablePath = raw.executablePath;
    }
    if (raw.headless !== undefined) {
      if (typeof raw.headless !== 'boolean') {
        return { ok: false, status: 400, errorCode: 'invalid_browser_settings_payload', errorMessage: 'headless 必须为布尔值。' };
      }
      sanitized.headless = raw.headless;
    }
    if (raw.profileMode !== undefined && raw.profileMode !== null) {
      const validProfiles = new Set(['Ephemeral', 'Persistent']);
      if (typeof raw.profileMode !== 'string' || !validProfiles.has(raw.profileMode)) {
        return { ok: false, status: 400, errorCode: 'invalid_browser_settings_payload', errorMessage: 'profileMode 必须为 Ephemeral 或 Persistent。' };
      }
      sanitized.profileMode = raw.profileMode;
    }
    if (raw.interactionApprovalPolicy !== undefined && raw.interactionApprovalPolicy !== null) {
      const validPolicies = new Set(['HighRiskOnly', 'AlwaysAsk', 'AutoInSession']);
      if (typeof raw.interactionApprovalPolicy !== 'string' || !validPolicies.has(raw.interactionApprovalPolicy)) {
        return { ok: false, status: 400, errorCode: 'invalid_browser_settings_payload', errorMessage: 'interactionApprovalPolicy 取值无效。' };
      }
      sanitized.interactionApprovalPolicy = raw.interactionApprovalPolicy;
    }
    if (raw.allowLocalhostInDailyMode !== undefined) {
      if (typeof raw.allowLocalhostInDailyMode !== 'boolean') {
        return { ok: false, status: 400, errorCode: 'invalid_browser_settings_payload', errorMessage: 'allowLocalhostInDailyMode 必须为布尔值。' };
      }
      sanitized.allowLocalhostInDailyMode = raw.allowLocalhostInDailyMode;
    }
    if (raw.allowControlledDownloads !== undefined) {
      if (typeof raw.allowControlledDownloads !== 'boolean') {
        return { ok: false, status: 400, errorCode: 'invalid_browser_settings_payload', errorMessage: 'allowControlledDownloads 必须为布尔值。' };
      }
      sanitized.allowControlledDownloads = raw.allowControlledDownloads;
    }
    if (raw.idleTimeoutMinutes !== undefined && raw.idleTimeoutMinutes !== null) {
      if (typeof raw.idleTimeoutMinutes !== 'number' || !Number.isInteger(raw.idleTimeoutMinutes) || raw.idleTimeoutMinutes < 1 || raw.idleTimeoutMinutes > 120) {
        return { ok: false, status: 400, errorCode: 'invalid_browser_settings_payload', errorMessage: 'idleTimeoutMinutes 必须为 1-120 的整数。' };
      }
      sanitized.idleTimeoutMinutes = raw.idleTimeoutMinutes;
    }
    if (raw.maxConcurrentSessions !== undefined && raw.maxConcurrentSessions !== null) {
      if (typeof raw.maxConcurrentSessions !== 'number' || !Number.isInteger(raw.maxConcurrentSessions) || raw.maxConcurrentSessions < 1 || raw.maxConcurrentSessions > 8) {
        return { ok: false, status: 400, errorCode: 'invalid_browser_settings_payload', errorMessage: 'maxConcurrentSessions 必须为 1-8 的整数。' };
      }
      sanitized.maxConcurrentSessions = raw.maxConcurrentSessions;
    }

    return callControlPlane(manager.getControlPlaneCredentials(), 'PUT', '/api/settings/browser', sanitized);
  });

  function isValidBrowserSessionId(value: unknown): value is string {
    if (typeof value !== 'string') {
      return false;
    }
    const trimmed = value.trim();
    if (trimmed.length === 0 || trimmed.length > 128 || trimmed === '.' || trimmed === '..') {
      return false;
    }
    return /^[A-Za-z0-9._:-]+$/.test(trimmed);
  }

  ipcMain.handle(IPC_CHANNELS.closeBrowserSession, async (event, sessionId: unknown) => {
    assertTrustedIpcSender(event);
    if (!isValidBrowserSessionId(sessionId)) {
      return {
        ok: false,
        status: 400,
        errorCode: 'invalid_session_id',
        errorMessage: '无效的浏览器会话 ID。',
      };
    }

    return callControlPlane(
      manager.getControlPlaneCredentials(),
      'POST',
      `/api/gateway/session/${encodeURIComponent(sessionId.trim())}/browser/close`,
      {},
    );
  });

  ipcMain.handle(IPC_CHANNELS.startBrowserTakeover, async (event, sessionId: unknown, reason: unknown) => {
    assertTrustedIpcSender(event);
    if (!isValidBrowserSessionId(sessionId)) {
      return {
        ok: false,
        status: 400,
        errorCode: 'invalid_session_id',
        errorMessage: '无效的浏览器会话 ID。',
      };
    }
    const cleanReason = typeof reason === 'string' && reason.trim().length > 0 ? reason.trim().slice(0, 300) : undefined;
    return callControlPlane(
      manager.getControlPlaneCredentials(),
      'POST',
      `/api/gateway/session/${encodeURIComponent(sessionId.trim())}/browser/takeover/start`,
      cleanReason ? { reason: cleanReason } : {},
    );
  });

  ipcMain.handle(IPC_CHANNELS.completeBrowserTakeover, async (event, sessionId: unknown, note: unknown) => {
    assertTrustedIpcSender(event);
    if (!isValidBrowserSessionId(sessionId)) {
      return {
        ok: false,
        status: 400,
        errorCode: 'invalid_session_id',
        errorMessage: '无效的浏览器会话 ID。',
      };
    }
    const cleanNote = typeof note === 'string' && note.trim().length > 0 ? note.trim().slice(0, 300) : undefined;
    return callControlPlane(
      manager.getControlPlaneCredentials(),
      'POST',
      `/api/gateway/session/${encodeURIComponent(sessionId.trim())}/browser/takeover/complete`,
      cleanNote ? { note: cleanNote } : {},
    );
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
        180_000,
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

  ipcMain.handle(
    IPC_CHANNELS.showWorkspaceFileContextMenu,
    async (event, payload: WorkspaceFileContextMenuPayload): Promise<WorkspaceFileActionResult> => {
      assertTrustedIpcSender(event);
      if (!mainWindow || mainWindow.isDestroyed()) {
        return {
          ok: false,
          error: desktopPreferences.language === 'en-US' ? 'Main window is unavailable.' : '主窗口不可用。',
        };
      }

      const language = desktopPreferences.language;
      const resolved = resolveSafeWorkspaceTarget(
        payload?.workspacePath ?? '',
        payload?.relativePath ?? '',
        language,
      );
      if (!resolved.ok) {
        return { ok: false, error: resolved.error };
      }

      const target = resolved.target;
      const win = mainWindow;

      return new Promise<WorkspaceFileActionResult>((resolveAction) => {
        let settled = false;
        let actionTriggered = false;
        const finish = (result: WorkspaceFileActionResult) => {
          if (settled) return;
          settled = true;
          resolveAction(result);
        };

        const runAsyncAction = (fn: () => Promise<WorkspaceFileActionResult>) => {
          actionTriggered = true;
          void fn().then(finish, (error) => {
            finish({ ok: false, error: error instanceof Error ? error.message : String(error) });
          });
        };

        const template = buildWorkspaceFileContextMenuTemplate({
          isDirectory: target.isDirectory,
          hasRelativePath: Boolean(target.relativePath),
          language,
          onOpenWithDefaultApp: () =>
            runAsyncAction(() => openWorkspaceFileWithDefaultApp(target, shell, language)),
          onOpenInFileManager: () =>
            runAsyncAction(() => openWorkspaceTargetInFileManager(target, shell, language)),
          onCopyRelativePath: () => {
            actionTriggered = true;
            clipboard.writeText(target.relativePath);
            finish({ ok: true });
          },
          onCopyFullPath: () => {
            actionTriggered = true;
            clipboard.writeText(target.realPath);
            finish({ ok: true });
          },
        });

        const menu = Menu.buildFromTemplate(template);
        menu.popup({
          window: win,
          callback: () => {
            setTimeout(() => {
              if (!actionTriggered) {
                finish({ ok: true, cancelled: true });
              }
            }, 80);
          },
        });
      });
    },
  );

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

  ipcMain.handle(IPC_CHANNELS.inspectEnvironment, async (event, forceRefresh?: boolean) => {
    assertTrustedIpcSender(event);
    if (!environmentManager) {
      throw new Error('Environment manager is not initialized');
    }
    return environmentManager.inspect(Boolean(forceRefresh));
  });

  ipcMain.handle(
    IPC_CHANNELS.installEnvironmentDependency,
    async (event, dependencyId: EnvironmentDependencyId) => {
      assertTrustedIpcSender(event);
      if (!isValidEnvironmentDependencyId(dependencyId)) {
        return { accepted: false, error: '无效的依赖项标识。' };
      }
      if (!environmentManager) {
        return { accepted: false, error: '环境管理器尚未初始化。' };
      }
      return environmentManager.startInstall(dependencyId);
    },
  );

  ipcMain.handle(IPC_CHANNELS.cancelEnvironmentInstall, (event) => {
    assertTrustedIpcSender(event);
    return environmentManager?.cancelInstall() ?? false;
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
    if (!isDev || existsSync(rendererDistDir)) {
      registerAppProtocolHandler(rendererDistDir);
    }

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

    environmentManager = new EnvironmentManager({
      isPackaged: app.isPackaged,
      resourcesPath: process.resourcesPath,
      getBackendInfo: () => backendManager?.getPublicInfo() ?? null,
      onInstallEvent: (payload) => broadcastEnvironmentInstallEvent(payload),
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
    mainWindow.webContents.once('did-finish-load', () => {
      updaterManager?.scheduleStartupCheck(3000);
    });
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
    environmentManager?.dispose();
    updaterManager?.dispose();
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
