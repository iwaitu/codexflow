import { BrowserWindow, globalShortcut, ipcMain, screen, type IpcMainInvokeEvent } from 'electron';
import { join } from 'node:path';
import { COMPUTER_OVERLAY_IPC_CHANNELS } from '../../src/shared/computerOverlayIpc';
import type { ComputerControlOverlayState } from '../../src/shared/ipc';

const OVERLAY_WIDTH = 380;
const OVERLAY_HEIGHT = 44;
const DEFAULT_SHORTCUT = 'CommandOrControl+.';

const OVERLAY_HTML = `<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8" />
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline';" />
  <title>CodexFlow Control</title>
  <style>
    * { box-sizing: border-box; user-select: none; -webkit-user-select: none; }
    html, body {
      margin: 0;
      padding: 0;
      width: 100%;
      height: 100%;
      background: transparent;
      overflow: hidden;
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif;
    }
    .pill {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      width: 100%;
      height: 100%;
      padding: 0 10px 0 12px;
      border-radius: 9999px;
      background: rgba(15, 23, 42, 0.92);
      border: 1px solid rgba(244, 63, 94, 0.55);
      box-shadow: 0 6px 20px rgba(0, 0, 0, 0.45);
      color: #f8fafc;
    }
    .left {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
      flex: 1;
    }
    .dot {
      width: 8px;
      height: 8px;
      border-radius: 9999px;
      background: #f43f5e;
      box-shadow: 0 0 8px #f43f5e;
      flex-shrink: 0;
      animation: pulse 1.2s ease-in-out infinite;
    }
    @keyframes pulse {
      0%, 100% { opacity: 1; transform: scale(1); }
      50% { opacity: 0.45; transform: scale(0.85); }
    }
    .texts {
      display: flex;
      flex-direction: column;
      min-width: 0;
      line-height: 1.2;
    }
    .status {
      font-size: 11px;
      font-weight: 600;
      color: #f8fafc;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .shortcut {
      font-size: 10px;
      color: #cbd5e1;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .stop-btn {
      flex-shrink: 0;
      border: none;
      border-radius: 9999px;
      padding: 5px 11px;
      font-size: 11px;
      font-weight: 600;
      color: #ffffff;
      background: #e11d48;
      cursor: pointer;
      transition: background 120ms ease;
    }
    .stop-btn:hover { background: #be123c; }
    .stop-btn:disabled { opacity: 0.6; cursor: default; }
  </style>
</head>
<body>
  <div class="pill">
    <div class="left">
      <span class="dot"></span>
      <div class="texts">
        <div id="cf-overlay-status" class="status">AI 正在操控本机桌面</div>
        <div id="cf-overlay-shortcut" class="shortcut">Ctrl+. 或连按物理 Esc×2 急停</div>
      </div>
    </div>
    <button id="cf-overlay-stop-btn" type="button" class="stop-btn">■ 停止</button>
  </div>
</body>
</html>`;

export interface ComputerOverlayManagerOptions {
  onEmergencyStopTriggered: (sessionId: string | null, reason: string) => Promise<void>;
  onProtectedWindowsChanged: () => void;
}

export class ComputerOverlayManager {
  private readonly options: ComputerOverlayManagerOptions;
  private overlayWindow: BrowserWindow | null = null;
  private registeredShortcut: string | null = null;
  private currentState: ComputerControlOverlayState = { active: false, sessionId: null };
  private ipcRegistered = false;
  private stopping = false;

  constructor(options: ComputerOverlayManagerOptions) {
    this.options = options;
  }

  public initialize(): void {
    if (this.ipcRegistered) {
      return;
    }
    this.ipcRegistered = true;
    ipcMain.handle(COMPUTER_OVERLAY_IPC_CHANNELS.triggerStop, async (event: IpcMainInvokeEvent) => {
      if (!this.overlayWindow || this.overlayWindow.isDestroyed() || event.sender !== this.overlayWindow.webContents) {
        throw new Error('Unauthorized IPC caller: invalid overlay stop sender');
      }
      await this.triggerEmergencyStop('overlay_button');
    });
  }

  public getOverlayWindow(): BrowserWindow | null {
    return this.overlayWindow && !this.overlayWindow.isDestroyed() ? this.overlayWindow : null;
  }

  public updateState(nextState: ComputerControlOverlayState): void {
    this.currentState = nextState;

    if (!nextState.active) {
      this.unregisterShortcut();
      this.hideOverlay();
      return;
    }

    const desiredShortcut = this.normalizeShortcut(nextState.shortcut);
    this.ensureShortcutRegistered(desiredShortcut);

    const shouldShowOverlay = nextState.status !== 'takeover' && nextState.status !== 'emergency_stopped';
    if (shouldShowOverlay) {
      this.showOrUpdateOverlay(nextState);
    } else {
      this.hideOverlay();
    }
  }

  public async triggerEmergencyStop(reason: string): Promise<void> {
    if (this.stopping) {
      return;
    }
    this.stopping = true;
    const sessionId = this.currentState.sessionId ?? null;
    try {
      this.currentState = { active: false, sessionId };
      this.unregisterShortcut();
      this.hideOverlay();
      await this.options.onEmergencyStopTriggered(sessionId, reason);
    } finally {
      this.stopping = false;
    }
  }

  public dispose(): void {
    this.unregisterShortcut();
    if (this.overlayWindow && !this.overlayWindow.isDestroyed()) {
      this.overlayWindow.destroy();
    }
    this.overlayWindow = null;
    if (this.ipcRegistered) {
      ipcMain.removeHandler(COMPUTER_OVERLAY_IPC_CHANNELS.triggerStop);
      this.ipcRegistered = false;
    }
  }

  private normalizeShortcut(raw?: string | null): string {
    const trimmed = raw?.trim();
    if (!trimmed) {
      return DEFAULT_SHORTCUT;
    }
    return trimmed;
  }

  private ensureShortcutRegistered(shortcut: string): void {
    if (this.registeredShortcut === shortcut) {
      return;
    }
    this.unregisterShortcut();

    const tryRegister = (accelerator: string): boolean => {
      try {
        return globalShortcut.register(accelerator, () => {
          void this.triggerEmergencyStop('global_shortcut');
        });
      } catch {
        return false;
      }
    };

    if (tryRegister(shortcut)) {
      this.registeredShortcut = shortcut;
      return;
    }

    if (shortcut !== DEFAULT_SHORTCUT && tryRegister(DEFAULT_SHORTCUT)) {
      this.registeredShortcut = DEFAULT_SHORTCUT;
    }
  }

  private unregisterShortcut(): void {
    if (!this.registeredShortcut) {
      return;
    }
    try {
      globalShortcut.unregister(this.registeredShortcut);
    } catch {
      // ignore teardown unregister errors
    }
    this.registeredShortcut = null;
  }

  private ensureOverlayWindow(): BrowserWindow {
    if (this.overlayWindow && !this.overlayWindow.isDestroyed()) {
      return this.overlayWindow;
    }

    const win = new BrowserWindow({
      width: OVERLAY_WIDTH,
      height: OVERLAY_HEIGHT,
      show: false,
      frame: false,
      transparent: true,
      alwaysOnTop: true,
      focusable: false,
      skipTaskbar: true,
      resizable: false,
      minimizable: false,
      maximizable: false,
      hasShadow: true,
      webPreferences: {
        preload: join(__dirname, '../preload/computerOverlay.cjs'),
        contextIsolation: true,
        sandbox: true,
        nodeIntegration: false,
        webSecurity: true,
        allowRunningInsecureContent: false,
        webviewTag: false,
      },
    });

    try {
      win.setAlwaysOnTop(true, 'screen-saver');
      win.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true });
    } catch {
      // ignore WM compatibility issues
    }

    win.on('closed', () => {
      if (this.overlayWindow === win) {
        this.overlayWindow = null;
        this.options.onProtectedWindowsChanged();
      }
    });

    win.webContents.once('did-finish-load', () => {
      if (!win.isDestroyed()) {
        win.webContents.send(COMPUTER_OVERLAY_IPC_CHANNELS.stateUpdated, this.currentState);
      }
    });

    void win.loadURL(`data:text/html;charset=utf-8,${encodeURIComponent(OVERLAY_HTML)}`);
    this.overlayWindow = win;
    this.options.onProtectedWindowsChanged();
    return win;
  }

  private showOrUpdateOverlay(state: ComputerControlOverlayState): void {
    const win = this.ensureOverlayWindow();
    const primaryWorkArea = screen.getPrimaryDisplay().workArea;
    const x = Math.round(primaryWorkArea.x + (primaryWorkArea.width - OVERLAY_WIDTH) / 2);
    const y = Math.round(primaryWorkArea.y + 10);
    win.setBounds({ x, y, width: OVERLAY_WIDTH, height: OVERLAY_HEIGHT });
    if (!win.webContents.isLoading()) {
      win.webContents.send(COMPUTER_OVERLAY_IPC_CHANNELS.stateUpdated, state);
    }
    if (!win.isVisible()) {
      win.showInactive();
    }
  }

  private hideOverlay(): void {
    if (this.overlayWindow && !this.overlayWindow.isDestroyed() && this.overlayWindow.isVisible()) {
      this.overlayWindow.hide();
    }
  }
}
