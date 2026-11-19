import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AppUpdater, UpdateInfo } from 'electron-updater';
import { UpdaterManager } from '../../electron/main/updaterManager';

function createFakeUpdater() {
  const listeners = new Map<string, Array<(...args: unknown[]) => void>>();

  const checkForUpdates = vi.fn().mockImplementation(async () => {
    for (const cb of listeners.get('update-not-available') ?? []) {
      cb();
    }
    return null;
  });

  const fake = {
    autoDownload: true,
    autoInstallOnAppQuit: false,
    on: vi.fn((event: string, listener: (...args: unknown[]) => void) => {
      const list = listeners.get(event) ?? [];
      list.push(listener);
      listeners.set(event, list);
      return fake;
    }),
    checkForUpdates,
    downloadUpdate: vi.fn().mockResolvedValue([]),
    quitAndInstall: vi.fn(),
  } as unknown as Pick<
    AppUpdater,
    'autoDownload' | 'autoInstallOnAppQuit' | 'on' | 'checkForUpdates' | 'downloadUpdate' | 'quitAndInstall'
  >;

  return {
    updater: fake,
    checkForUpdates,
    emit: (event: string, ...args: unknown[]) => {
      for (const cb of listeners.get(event) ?? []) {
        cb(...args);
      }
    },
  };
}

describe('UpdaterManager startup & concurrency', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('schedules a single background update check on startup in packaged builds', async () => {
    const fake = createFakeUpdater();
    fake.checkForUpdates.mockImplementationOnce(async () => {
      fake.emit('update-available', { version: '0.2.0' } as UpdateInfo);
      return null;
    });

    const states: string[] = [];
    const manager = new UpdaterManager({
      currentVersion: '0.1.13',
      isPackaged: true,
      updater: fake.updater,
      onStateChange: (s) => states.push(s.status),
      onPrepareInstall: vi.fn(),
    });

    manager.scheduleStartupCheck(2500);
    manager.scheduleStartupCheck(100);
    expect(fake.checkForUpdates).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(2500);
    expect(fake.checkForUpdates).toHaveBeenCalledTimes(1);
    expect(manager.getState().status).toBe('available');
    expect(manager.getState().availableVersion).toBe('0.2.0');
    expect(states).toContain('available');
  });

  it('skips startup check in unpackaged builds and cancels timer on dispose', async () => {
    const devFake = createFakeUpdater();
    const devManager = new UpdaterManager({
      currentVersion: '0.1.13',
      isPackaged: false,
      updater: devFake.updater,
      onStateChange: vi.fn(),
      onPrepareInstall: vi.fn(),
    });
    devManager.scheduleStartupCheck(500);
    await vi.advanceTimersByTimeAsync(1000);
    expect(devFake.checkForUpdates).not.toHaveBeenCalled();
    expect(devManager.getState().status).toBe('unavailable');

    const prodFake = createFakeUpdater();
    const prodManager = new UpdaterManager({
      currentVersion: '0.1.13',
      isPackaged: true,
      updater: prodFake.updater,
      onStateChange: vi.fn(),
      onPrepareInstall: vi.fn(),
    });
    prodManager.scheduleStartupCheck(1000);
    prodManager.dispose();
    await vi.advanceTimersByTimeAsync(2000);
    expect(prodFake.checkForUpdates).not.toHaveBeenCalled();
  });

  it('deduplicates concurrent checkForUpdates calls', async () => {
    const fake = createFakeUpdater();
    const deferred: { resolve?: () => void } = {};
    fake.checkForUpdates.mockImplementationOnce(
      () =>
        new Promise<null>((resolve) => {
          deferred.resolve = () => {
            fake.emit('update-not-available');
            resolve(null);
          };
        }),
    );

    const manager = new UpdaterManager({
      currentVersion: '0.1.13',
      isPackaged: true,
      updater: fake.updater,
      onStateChange: vi.fn(),
      onPrepareInstall: vi.fn(),
    });

    const p1 = manager.checkForUpdates();
    const p2 = manager.checkForUpdates();
    expect(fake.checkForUpdates).toHaveBeenCalledTimes(1);

    deferred.resolve?.();
    const [s1, s2] = await Promise.all([p1, p2]);
    expect(s1.status).toBe('not-available');
    expect(s2.status).toBe('not-available');
  });
});
