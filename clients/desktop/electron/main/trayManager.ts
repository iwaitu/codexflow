import { Menu, Tray, nativeImage, type MenuItemConstructorOptions } from 'electron';
import { existsSync } from 'node:fs';
import type { BackendConnectionStatus, DesktopCommandAction, DesktopLanguage } from '../../src/shared/ipc';

export interface TrayManagerOptions {
  iconPaths: string[];
  getLanguage: () => DesktopLanguage;
  getConnectionStatus: () => BackendConnectionStatus;
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

    const template: MenuItemConstructorOptions[] = [
      {
        label: `${labels.statusPrefix}: ${labels.statusNames[status] ?? status}`,
        enabled: false,
      },
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
