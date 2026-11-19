import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { APP_METADATA, type AppUpdateState, type DesktopBridge } from '@shared/ipc';
import { StatusBar } from '@renderer/components/StatusBar';
import { UpdateModal } from '@renderer/components/UpdateModal';
import { usePreferencesStore } from '@renderer/stores/usePreferencesStore';

function installUpdateMockBridge(initialState: AppUpdateState) {
  let stateListener: ((state: AppUpdateState) => void) | null = null;
  const checkForUpdates = vi.fn().mockResolvedValue({
    ...initialState,
    status: 'not-available' as const,
  });
  const downloadUpdate = vi.fn().mockResolvedValue({
    ...initialState,
    status: 'downloading' as const,
    availableVersion: '0.2.0',
    downloadProgress: 25,
  });
  const quitAndInstall = vi.fn().mockResolvedValue(true);
  const openExternal = vi.fn().mockResolvedValue(true);

  const bridge = {
    getUpdateState: vi.fn().mockResolvedValue(initialState),
    onUpdateStateChanged: vi.fn((listener: (state: AppUpdateState) => void) => {
      stateListener = listener;
      return () => {
        stateListener = null;
      };
    }),
    checkForUpdates,
    downloadUpdate,
    quitAndInstall,
    openExternal,
  };

  Object.defineProperty(window, 'codexflow', {
    value: bridge as unknown as DesktopBridge,
    configurable: true,
    writable: true,
  });

  return {
    bridge,
    emitState: (next: AppUpdateState) => {
      stateListener?.(next);
    },
  };
}

describe('UpdateModal', () => {
  beforeEach(() => {
    usePreferencesStore.getState().resetForTests();
    usePreferencesStore.setState({ language: 'zh-CN', theme: 'dark' });
  });

  it('opens dedicated update dialog and automatically checks for updates when idle', async () => {
    const { bridge } = installUpdateMockBridge({
      status: 'idle',
      currentVersion: '0.1.13',
      availableVersion: null,
      downloadProgress: null,
      message: null,
    });

    render(<UpdateModal open={true} clientVersion="0.1.13" onOpenChange={vi.fn()} />);

    expect(screen.getByTestId('update-modal')).toBeInTheDocument();
    expect(screen.getByText('应用更新')).toBeInTheDocument();
    expect(screen.getByTestId('update-current-version')).toHaveTextContent('0.1.13');

    await waitFor(() => {
      expect(bridge.checkForUpdates).toHaveBeenCalledTimes(1);
    });
    expect(screen.getByTestId('update-status-message')).toHaveTextContent('当前已是最新版本。');

    fireEvent.click(screen.getByTestId('update-releases-button'));
    expect(bridge.openExternal).toHaveBeenCalledWith(APP_METADATA.releasesUrl);
  });

  it('supports available -> downloading -> downloaded flow without opening settings', async () => {
    const { bridge, emitState } = installUpdateMockBridge({
      status: 'idle',
      currentVersion: '0.1.13',
      availableVersion: null,
      downloadProgress: null,
      message: null,
    });

    render(<UpdateModal open={true} clientVersion="0.1.13" onOpenChange={vi.fn()} />);

    await waitFor(() => {
      expect(bridge.checkForUpdates).toHaveBeenCalledTimes(1);
    });

    act(() => {
      emitState({
        status: 'available',
        currentVersion: '0.1.13',
        availableVersion: '0.2.0',
        downloadProgress: null,
        message: null,
      });
    });

    const downloadBtn = screen.getByTestId('download-update-button');
    expect(downloadBtn).toHaveTextContent('下载更新 (v0.2.0)');
    fireEvent.click(downloadBtn);
    expect(bridge.downloadUpdate).toHaveBeenCalledTimes(1);

    act(() => {
      emitState({
        status: 'downloading',
        currentVersion: '0.1.13',
        availableVersion: '0.2.0',
        downloadProgress: 42,
        message: null,
      });
    });

    expect(screen.getByTestId('update-download-progress')).toBeInTheDocument();
    expect(screen.getByTestId('update-status-message')).toHaveTextContent('下载中 42%');

    act(() => {
      emitState({
        status: 'downloaded',
        currentVersion: '0.1.13',
        availableVersion: '0.2.0',
        downloadProgress: 100,
        message: null,
      });
    });

    const installBtn = screen.getByTestId('install-update-button');
    fireEvent.click(installBtn);
    expect(bridge.quitAndInstall).toHaveBeenCalledTimes(1);
  });

  it('does not redundantly trigger checkForUpdates when auto-update is unavailable in unpackaged builds', async () => {
    const { bridge } = installUpdateMockBridge({
      status: 'unavailable',
      currentVersion: '0.1.13',
      availableVersion: null,
      downloadProgress: null,
      message: '开发或未打包模式下不启用自动更新。',
    });

    render(<UpdateModal open={true} clientVersion="0.1.13" onOpenChange={vi.fn()} />);

    await waitFor(() => {
      expect(screen.getByTestId('update-status-message')).toHaveTextContent('开发或未打包环境下不启用自动更新。');
    });
    expect(bridge.checkForUpdates).not.toHaveBeenCalled();
  });

  it('shows clickable StatusBar update badge only when a new version is available or downloaded', () => {
    const onOpenUpdates = vi.fn();
    const baseProps = {
      connection: {
        mode: 'managed' as const,
        status: 'ready' as const,
        baseUrl: 'http://127.0.0.1:59999',
        targetBaseUrl: 'http://127.0.0.1:5206',
        accessToken: 'acc',
        instanceId: 'inst-1',
        controlPlaneAvailable: true,
        selfApprovalProtection: true,
        dataDirectory: 'D:/data',
        lastError: null,
        restartCount: 0,
        clientVersion: '0.1.13',
      },
      sseState: 'connected' as const,
      systemStatus: null,
      contextWindow: null,
      onOpenSettings: vi.fn(),
      onOpenUpdates,
      onRestartBackend: vi.fn(),
    };

    const { rerender } = render(
      <StatusBar
        {...baseProps}
        updateState={{
          status: 'not-available',
          currentVersion: '0.1.13',
          availableVersion: null,
          downloadProgress: null,
          message: null,
        }}
      />,
    );
    expect(screen.queryByTestId('status-update-badge')).not.toBeInTheDocument();

    rerender(
      <StatusBar
        {...baseProps}
        updateState={{
          status: 'available',
          currentVersion: '0.1.13',
          availableVersion: '0.2.0',
          downloadProgress: null,
          message: null,
        }}
      />,
    );
    const badge = screen.getByTestId('status-update-badge');
    expect(badge).toHaveTextContent('发现新版 v0.2.0');
    fireEvent.click(badge);
    expect(onOpenUpdates).toHaveBeenCalledTimes(1);

    rerender(
      <StatusBar
        {...baseProps}
        updateState={{
          status: 'downloaded',
          currentVersion: '0.1.13',
          availableVersion: '0.2.0',
          downloadProgress: 100,
          message: null,
        }}
      />,
    );
    expect(screen.getByTestId('status-update-badge')).toHaveTextContent('待安装 v0.2.0');
  });
});
