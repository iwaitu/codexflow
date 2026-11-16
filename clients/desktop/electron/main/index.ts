import { app, BrowserWindow, dialog, ipcMain, Notification, shell, type IpcMainInvokeEvent } from 'electron';
import { join, resolve } from 'node:path';
import {
  IPC_CHANNELS,
  type ConnectStandalonePayload,
  type OpenLocalWorkspacePayload,
  type PlanChatControlPayload,
  type SubmitToolApprovalPayload,
  type UpdateExecutionSettingsPayload,
  type UpdateModelSettingsPayload,
} from '../../src/shared/ipc';
import { BackendManager } from './backendManager';
import { callControlPlane } from './controlPlaneProxy';
import { LocalBackendProxy } from './localBackendProxy';
import {
  APP_ORIGIN,
  attachCspHeaderHook,
  registerAppProtocolHandler,
  registerAppSchemePrivileges,
} from './protocol';
import { isTrustedRendererOrigin, validateIpcCaller } from './securityUtils';

registerAppSchemePrivileges();

const isDev = Boolean(process.env.ELECTRON_RENDERER_URL);
let mainWindow: BrowserWindow | null = null;
let backendManager: BackendManager | null = null;
let gatewayProxy: LocalBackendProxy | null = null;

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

function broadcastConnectionChange(): void {
  if (!backendManager || !mainWindow || mainWindow.isDestroyed()) {
    return;
  }
  mainWindow.webContents.send(IPC_CHANNELS.backendConnectionChanged, backendManager.getPublicInfo());
}

function createMainWindow(): BrowserWindow {
  const win = new BrowserWindow({
    width: 1380,
    height: 900,
    minWidth: 1024,
    minHeight: 680,
    show: false,
    title: 'CodexFlow Desktop',
    backgroundColor: '#0b0f17',
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

  win.once('ready-to-show', () => {
    win.show();
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

  ipcMain.handle(IPC_CHANNELS.pickFolder, async (event) => {
    assertTrustedIpcSender(event);
    const targetWindow = mainWindow ?? undefined;
    const result = targetWindow
      ? await dialog.showOpenDialog(targetWindow, { properties: ['openDirectory'] })
      : await dialog.showOpenDialog({ properties: ['openDirectory'] });
    return result.canceled ? null : (result.filePaths[0] ?? null);
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

  ipcMain.handle(IPC_CHANNELS.showNotification, (event, payload: { title: string; body: string }) => {
    assertTrustedIpcSender(event);
    if (Notification.isSupported()) {
      new Notification({ title: payload.title, body: payload.body }).show();
    }
  });
}

const singleInstanceLock = app.requestSingleInstanceLock();
if (!singleInstanceLock) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.focus();
    }
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
      isPackaged: app.isPackaged,
      isDev,
      devRendererOrigin,
      getProxyOrigin: () => gatewayProxy?.getOrigin() ?? null,
      onChange: () => broadcastConnectionChange(),
    });

    gatewayProxy = new LocalBackendProxy(
      () => backendManager?.getUpstreamState() ?? { targetBaseUrl: null, expectedAccessToken: null, controlToken: null },
      getAllowedRendererOrigins(),
    );
    const proxyOrigin = await gatewayProxy.start();

    attachCspHeaderHook(() => proxyOrigin, isDev);
    registerIpcHandlers(backendManager);

    mainWindow = createMainWindow();
    mainWindow.on('closed', () => {
      mainWindow = null;
    });

    // Launch managed backend without blocking initial window paint.
    void backendManager.startManaged();

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) {
        mainWindow = createMainWindow();
      }
    });
  });

  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') {
      app.quit();
    }
  });

  let isDisposing = false;
  app.on('before-quit', (event) => {
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
