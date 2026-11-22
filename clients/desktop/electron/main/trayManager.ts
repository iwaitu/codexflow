import { Menu, Tray, nativeImage, type MenuItemConstructorOptions } from 'electron';
import { existsSync } from 'node:fs';
import type { BackendConnectionStatus, DesktopCommandAction, DesktopLanguage } from '../../src/shared/ipc';

export interface TrayRemoteStatusInfo {
  bound: boolean;
  paused: boolean;
  dialState: string;
  pendingPairCount: number;
}

export interface TrayManagerOptions {
  iconPaths: string[];
  getLanguage: () => DesktopLanguage;
  getConnectionStatus: () => BackendConnectionStatus;
  getRemoteStatus?: () => TrayRemoteStatusInfo | null;
  onToggleRemotePaused?: () => void;
  isWindowVisible: () => boolean;
  onShowWindow: () => void;
  onHideWindow: () => void;
  onDispatchCommand: (action: DesktopCommandAction) => void;
  onQuit: () => void;
}

const TRAY_LABELS: Record<
  DesktopLanguage,
  {
    statusPrefix: string;
    statusNames: Record<BackendConnectionStatus, string>;
    remotePrefix: string;
    remoteUnbound: string;
    remotePaused: string;
    remoteConnected: string;
    remoteConnecting: string;
    remoteAttention: string;
    remotePauseAction: string;
    remoteResumeAction: string;
    remoteSettings: string;
    remotePendingPairSuffix: string;
    showWindow: string;
    hideWindow: string;
    newConversation: string;
    openSettings: string;
    quit: string;
  }
> = {
  'zh-CN': {
    statusPrefix: '后端状态',
    statusNames: {
      starting: '启动中',
      ready: '已连接',
      reconnecting: '重连中',
      error: '连接异常',
      disconnected: '已断开',
    },
    remotePrefix: '远程控制',
    remoteUnbound: '未绑定',
    remotePaused: '已暂停',
    remoteConnected: '已在线',
    remoteConnecting: '连接中',
    remoteAttention: '需处理',
    remotePauseAction: '暂停远程控制',
    remoteResumeAction: '恢复远程控制',
    remoteSettings: '远程控制设置',
    remotePendingPairSuffix: '条待确认配对',
    showWindow: '显示主窗口',
    hideWindow: '隐藏到托盘',
    newConversation: '新建对话',
    openSettings: '设置',
    quit: '退出 CodexFlow',
  },
  'en-US': {
    statusPrefix: 'Backend',
    statusNames: {
      starting: 'Starting',
      ready: 'Ready',
      reconnecting: 'Reconnecting',
      error: 'Error',
      disconnected: 'Disconnected',
    },
    remotePrefix: 'Remote',
    remoteUnbound: 'Unbound',
    remotePaused: 'Paused',
    remoteConnected: 'Connected',
    remoteConnecting: 'Connecting',
    remoteAttention: 'Action Needed',
    remotePauseAction: 'Pause Remote Control',
    remoteResumeAction: 'Resume Remote Control',
    remoteSettings: 'Remote Control Settings',
    remotePendingPairSuffix: 'pending pair(s)',
    showWindow: 'Show Window',
    hideWindow: 'Hide to Tray',
    newConversation: 'New Conversation',
    openSettings: 'Settings',
    quit: 'Quit CodexFlow',
  },
};

function loadTrayIcon(candidates: string[]): Electron.NativeImage {
  for (const path of candidates) {
    if (path && existsSync(path)) {
      const img = nativeImage.createFromPath(path);
      if (!img.isEmpty()) {
        const size = process.platform === 'darwin' ? 18 : 16;
        return img.resize({ width: size, height: size });
      }
    }
  }
  return nativeImage.createEmpty();
}

/**
 * Manages the desktop system tray icon, dynamic localized context menu, and window restore actions.
 */
export class TrayManager {
  private tray: Tray | null = null;
  private readonly options: TrayManagerOptions;

  constructor(options: TrayManagerOptions) {
    this.options = options;
  }

  initialize(): void {
    if (this.tray) {
      return;
    }

    const icon = loadTrayIcon(this.options.iconPaths);
    const tray = new Tray(icon);
    tray.setToolTip('CodexFlow Desktop');
    tray.on('click', () => {
      this.options.onShowWindow();
    });
    tray.on('double-click', () => {
      this.options.onShowWindow();
    });

    this.tray = tray;
    this.refreshMenu();
  }

  refreshMenu(): void {
    if (!this.tray) {
      return;
    }

    const lang = this.options.getLanguage();
    const labels = TRAY_LABELS[lang] ?? TRAY_LABELS['zh-CN'];
    const status = this.options.getConnectionStatus();
    const visible = this.options.isWindowVisible();

    const remoteStatus = this.options.getRemoteStatus?.() ?? null;
    const remoteItems: MenuItemConstructorOptions[] = [];
    if (remoteStatus) {
      let remoteSummary = labels.remoteUnbound;
      if (remoteStatus.bound) {
        if (remoteStatus.paused || remoteStatus.dialState === 'paused') {
          remoteSummary = labels.remotePaused;
        } else if (remoteStatus.dialState === 'connected') {
          remoteSummary = labels.remoteConnected;
        } else if (remoteStatus.dialState === 'dialing' || remoteStatus.dialState === 'backoff') {
          remoteSummary = labels.remoteConnecting;
        } else if (
          remoteStatus.dialState === 'rebind_required' ||
          remoteStatus.dialState === 'unauthorized_401' ||
          remoteStatus.dialState === 'forbidden_403' ||
          remoteStatus.dialState === 'auth_required_407'
        ) {
          remoteSummary = `${labels.remoteAttention} (${remoteStatus.dialState})`;
        } else {
          remoteSummary = remoteStatus.dialState;
        }
      }
      if (remoteStatus.pendingPairCount > 0) {
        remoteSummary = `${remoteSummary} · ${remoteStatus.pendingPairCount} ${labels.remotePendingPairSuffix}`;
      }
      remoteItems.push({
        label: `${labels.remotePrefix}: ${remoteSummary}`,
        enabled: false,
      });
      if (remoteStatus.bound && this.options.onToggleRemotePaused) {
        remoteItems.push({
          label: remoteStatus.paused ? labels.remoteResumeAction : labels.remotePauseAction,
          click: () => this.options.onToggleRemotePaused?.(),
        });
      }
      remoteItems.push({
        label: labels.remoteSettings,
        click: () => {
          this.options.onShowWindow();
          this.options.onDispatchCommand('open-remote-settings');
        },
      });
      remoteItems.push({ type: 'separator' });
    }

    const template: MenuItemConstructorOptions[] = [
      {
        label: `${labels.statusPrefix}: ${labels.statusNames[status] ?? status}`,
        enabled: false,
      },
      ...remoteItems,
      { type: 'separator' },
      visible
        ? {
            label: labels.hideWindow,
            click: () => this.options.onHideWindow(),
          }
        : {
            label: labels.showWindow,
            click: () => this.options.onShowWindow(),
          },
      {
        label: labels.newConversation,
        click: () => {
          this.options.onShowWindow();
          this.options.onDispatchCommand('new-conversation');
        },
      },
      {
        label: labels.openSettings,
        click: () => {
          this.options.onShowWindow();
          this.options.onDispatchCommand('open-settings');
        },
      },
      { type: 'separator' },
      {
        label: labels.quit,
        click: () => this.options.onQuit(),
      },
    ];

    this.tray.setContextMenu(Menu.buildFromTemplate(template));
  }

  dispose(): void {
    if (this.tray) {
      this.tray.destroy();
      this.tray = null;
    }
  }
}
