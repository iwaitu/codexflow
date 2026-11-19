import React from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { DesktopBridge, EnvironmentInstallEvent, EnvironmentReport } from '@shared/ipc';
import { EnvironmentCheckPanel } from '../../src/renderer/components/EnvironmentCheckPanel';
import { SettingsModal } from '../../src/renderer/components/SettingsModal';

const sampleReport: EnvironmentReport = {
  platform: 'darwin',
  arch: 'arm64',
  isPackaged: true,
  packageManager: 'brew',
  shellPathEnhanced: true,
  checkedAtIso: '2026-11-19T08:00:00.000Z',
  items: [
    {
      id: 'backend',
      name: 'CodexFlow 本地后端',
      severity: 'required',
      status: 'ready',
      version: '0.1.14',
      resolvedPath: '/Applications/CodexFlow Desktop.app/Contents/Resources/backend/CodexFlow.Local',
      bundled: true,
      summary: '自包含 .NET 10 后端运行正常',
      detail: null,
      installStrategy: 'none',
      installCommandPreview: null,
      downloadUrl: null,
    },
    {
      id: 'git',
      name: 'Git',
      severity: 'required',
      status: 'missing',
      version: null,
      resolvedPath: null,
      bundled: false,
      summary: '未检测到 Git',
      detail: null,
      installStrategy: 'auto',
      installCommandPreview: 'brew install git',
      downloadUrl: 'https://git-scm.com/download/mac',
    },
  ],
};

describe('EnvironmentCheckPanel & SettingsModal integration', () => {
  let installListener: ((evt: EnvironmentInstallEvent) => void) | null = null;
  const inspectEnvironment = vi.fn<DesktopBridge['inspectEnvironment']>();
  const installEnvironmentDependency = vi.fn<DesktopBridge['installEnvironmentDependency']>();
  const cancelEnvironmentInstall = vi.fn<DesktopBridge['cancelEnvironmentInstall']>();
  const openExternal = vi.fn<DesktopBridge['openExternal']>();

  beforeEach(() => {
    installListener = null;
    inspectEnvironment.mockResolvedValue(sampleReport);
    installEnvironmentDependency.mockResolvedValue({ accepted: true });
    cancelEnvironmentInstall.mockResolvedValue(true);
    openExternal.mockResolvedValue(true);

    (window as unknown as { codexflow: Partial<DesktopBridge> }).codexflow = {
      inspectEnvironment,
      installEnvironmentDependency,
      cancelEnvironmentInstall,
      openExternal,
      onEnvironmentInstallEvent: (cb) => {
        installListener = cb;
        return () => {
          installListener = null;
        };
      },
      listPermissionRules: vi.fn().mockResolvedValue({ ok: true, status: 200, data: { count: 0, rules: [] } }),
      setDesktopPreferences: vi.fn().mockResolvedValue(undefined),
    };
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('displays dependency report, triggers one-click install, and applies live progress', async () => {
    render(<EnvironmentCheckPanel active />);

    await waitFor(() => {
      expect(screen.getByTestId('environment-ready-summary')).toHaveTextContent('1/2');
    });

    expect(screen.getByTestId('env-item-backend')).toHaveTextContent('应用内置');
    expect(screen.getByTestId('env-install-git')).toBeInTheDocument();

    fireEvent.click(screen.getByTestId('env-install-git'));
    await waitFor(() => {
      expect(installEnvironmentDependency).toHaveBeenCalledWith('git');
    });

    const updatedReport: EnvironmentReport = {
      ...sampleReport,
      items: sampleReport.items.map((item) =>
        item.id === 'git' ? { ...item, status: 'ready', version: '2.46.0' } : item,
      ),
    };

    act(() => {
      installListener?.({
        dependencyId: 'git',
        phase: 'running',
        message: '正在安装 git...',
        logChunk: '==> Pouring git--2.46.0\n',
      });
    });

    await waitFor(() => {
      expect(screen.getByTestId('environment-install-console')).toHaveTextContent('Pouring git--2.46.0');
    });

    act(() => {
      installListener?.({
        dependencyId: 'git',
        phase: 'succeeded',
        message: 'git 安装完成并已刷新环境状态。',
        exitCode: 0,
        report: updatedReport,
      });
    });

    await waitFor(() => {
      expect(screen.getByTestId('environment-ready-summary')).toHaveTextContent('2/2');
    });
  });

  it('renders Environment tab inside SettingsModal', async () => {
    render(
      <SettingsModal
        open
        initialTab="environment"
        systemStatus={null}
        executionSettings={null}
        modelSettings={null}
        controlPlaneAvailable
        onClose={vi.fn()}
        onUpdated={vi.fn()}
      />,
    );

    expect(screen.getByTestId('settings-tab-environment')).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByTestId('environment-check-panel')).toBeInTheDocument();
    });
  });
});
