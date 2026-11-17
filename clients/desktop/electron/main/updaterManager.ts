import electronUpdater, { type AppUpdater, type ProgressInfo, type UpdateInfo } from 'electron-updater';
import type { AppUpdateState } from '../../src/shared/ipc';

export interface UpdaterManagerOptions {
  currentVersion: string;
  isPackaged: boolean;
  onStateChange: (state: AppUpdateState) => void;
  onPrepareInstall: () => void;
  updater?: Pick<
    AppUpdater,
    'autoDownload' | 'autoInstallOnAppQuit' | 'on' | 'checkForUpdates' | 'downloadUpdate' | 'quitAndInstall'
  >;
}

/**
 * Coordinates desktop application update checks via electron-updater while degrading safely
 * in unpackaged development or offline test environments.
 */
export class UpdaterManager {
  private readonly options: UpdaterManagerOptions;
  private initialized = false;
  private state: AppUpdateState;

  constructor(options: UpdaterManagerOptions) {
    this.options = options;
    this.state = {
      status: options.isPackaged ? 'idle' : 'unavailable',
      currentVersion: options.currentVersion,
      availableVersion: null,
      downloadProgress: null,
      message: options.isPackaged ? null : '开发或未打包模式下不启用自动更新。',
    };
  }

  getState(): AppUpdateState {
    return { ...this.state };
  }

  private resolveUpdater() {
    // Access the lazy `autoUpdater` getter only inside Electron packaged flows (or when injected for tests).
    return this.options.updater ?? electronUpdater.autoUpdater;
  }

  initialize(): void {
    if (this.initialized || !this.options.isPackaged) {
      return;
    }
    this.initialized = true;

    const updater = this.resolveUpdater();
    updater.autoDownload = false;
    updater.autoInstallOnAppQuit = true;

    updater.on('checking-for-update', () => {
      this.setState({
        status: 'checking',
        downloadProgress: null,
        message: null,
      });
    });

    updater.on('update-available', (info: UpdateInfo) => {
      this.setState({
        status: 'available',
        availableVersion: info.version ?? null,
        downloadProgress: null,
        message: null,
      });
    });

    updater.on('update-not-available', () => {
      this.setState({
        status: 'not-available',
        availableVersion: null,
        downloadProgress: null,
        message: null,
      });
    });

    updater.on('download-progress', (progress: ProgressInfo) => {
      this.setState({
        status: 'downloading',
        downloadProgress: Math.max(0, Math.min(100, Math.round(progress.percent))),
        message: null,
      });
    });

    updater.on('update-downloaded', (info: UpdateInfo) => {
      this.setState({
        status: 'downloaded',
        availableVersion: info.version ?? this.state.availableVersion,
        downloadProgress: 100,
        message: null,
      });
    });

    updater.on('error', (error: Error) => {
      this.setState({
        status: 'error',
        downloadProgress: null,
        message: error?.message || '检查更新失败',
      });
    });
  }

  async checkForUpdates(): Promise<AppUpdateState> {
    if (!this.options.isPackaged) {
      return this.getState();
    }

    this.initialize();
    this.setState({
      status: 'checking',
      downloadProgress: null,
      message: null,
    });

    try {
      await this.resolveUpdater().checkForUpdates();
    } catch (error) {
      this.setState({
        status: 'error',
        downloadProgress: null,
        message: error instanceof Error ? error.message : String(error),
      });
    }

    return this.getState();
  }

  async downloadUpdate(): Promise<AppUpdateState> {
    if (!this.options.isPackaged) {
      return this.getState();
    }

    this.initialize();
    try {
      await this.resolveUpdater().downloadUpdate();
    } catch (error) {
      this.setState({
        status: 'error',
        downloadProgress: null,
        message: error instanceof Error ? error.message : String(error),
      });
    }

    return this.getState();
  }

  quitAndInstall(): boolean {
    if (!this.options.isPackaged || this.state.status !== 'downloaded') {
      return false;
    }

    this.options.onPrepareInstall();
    this.resolveUpdater().quitAndInstall();
    return true;
  }

  private setState(partial: Partial<AppUpdateState>): void {
    this.state = {
      ...this.state,
      ...partial,
    };
    this.options.onStateChange(this.getState());
  }
}
