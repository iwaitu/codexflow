import React from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { BackendConnectionPublicInfo, DesktopBridge } from '../../src/shared/ipc';
import { CommandPaletteModal } from '../../src/renderer/components/CommandPaletteModal';
import { SettingsModal } from '../../src/renderer/components/SettingsModal';
import { StatusBar } from '../../src/renderer/components/StatusBar';
import { I18N_DICTIONARIES, translate } from '../../src/renderer/lib/i18n';
import { useAppStore } from '../../src/renderer/stores/useAppStore';
import { usePreferencesStore } from '../../src/renderer/stores/usePreferencesStore';

function installMockBridge(overrides: Partial<DesktopBridge> = {}): DesktopBridge {
  const bridge: DesktopBridge = {
    getBackendConnection: vi.fn().mockResolvedValue({
      mode: 'managed',
      status: 'ready',
      baseUrl: 'http://127.0.0.1:59999',
      targetBaseUrl: 'http://127.0.0.1:5206',
      accessToken: 'normal-token-only',
      instanceId: 'inst-1',
      controlPlaneAvailable: true,
      selfApprovalProtection: true,
      dataDirectory: 'D:/data',
      lastError: null,
      restartCount: 0,
    } satisfies BackendConnectionPublicInfo),
    onBackendConnectionChanged: vi.fn().mockReturnValue(() => {}),
    approveToolExecution: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    getApprovalScript: vi.fn().mockResolvedValue({ ok: false, status: 404 }),
    setSessionPlanMode: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    submitPlanChatAction: vi.fn().mockResolvedValue({ ok: true, status: 202 }),
    updateExecutionSettings: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    updateModelSettings: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    listPermissionRules: vi.fn().mockResolvedValue({ ok: true, status: 200, data: { count: 0, rules: [] } }),
    deletePermissionRule: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    openLocalWorkspace: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    createBusinessPlugin: vi.fn().mockResolvedValue({ ok: true, status: 201 }),
    updateBusinessPluginConnection: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    setBusinessPluginCredential: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    deleteBusinessPluginCredential: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    disableBusinessPlugin: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    discoverBusinessPlugin: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    updateBusinessCapabilityRisk: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    updateSkillDraftSelection: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    generateSkillDraftCandidate: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    startSkillDraftTestRun: vi.fn().mockResolvedValue({ ok: true, status: 202 }),
    publishSkillDraft: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    activateSkillVersion: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    startPublishedSkillRun: vi.fn().mockResolvedValue({ ok: true, status: 202 }),
    cancelBusinessSkillRun: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    pruneBusinessSkillRetention: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    pickFolder: vi.fn().mockResolvedValue(null),
    getPathForFile: vi.fn().mockReturnValue(null),
    classifyDroppedFiles: vi.fn().mockResolvedValue([]),
    pickStandaloneTokenFile: vi.fn().mockResolvedValue(null),
    discoverStandaloneInstance: vi.fn().mockResolvedValue(null),
    connectStandalone: vi.fn(),
    switchToManaged: vi.fn(),
    restartBackend: vi.fn(),
    openExternal: vi.fn().mockResolvedValue(true),
    showNotification: vi.fn().mockResolvedValue(undefined),
    setDesktopPreferences: vi.fn().mockResolvedValue(undefined),
    onCommandAction: vi.fn().mockReturnValue(() => {}),
    getUpdateState: vi.fn().mockResolvedValue({
      status: 'unavailable',
      currentVersion: '0.1.5',
      availableVersion: null,
      downloadProgress: null,
      message: null,
    }),
    onUpdateStateChanged: vi.fn().mockReturnValue(() => {}),
    checkForUpdates: vi.fn().mockResolvedValue({
      status: 'unavailable',
      currentVersion: '0.1.5',
      availableVersion: null,
      downloadProgress: null,
      message: null,
    }),
    downloadUpdate: vi.fn().mockResolvedValue({
      status: 'unavailable',
      currentVersion: '0.1.5',
      availableVersion: null,
      downloadProgress: null,
      message: null,
    }),
    quitAndInstall: vi.fn().mockResolvedValue(false),
    ...overrides,
  };
  window.codexflow = bridge;
  return bridge;
}

describe('P5 Desktop Enhancements (Notifications, Theme, i18n, Command Palette, Updater)', () => {
  beforeEach(() => {
    installMockBridge();
    usePreferencesStore.getState().resetForTests();
    useAppStore.setState({
      isTurnRunning: false,
      streamingContent: '',
      streamingThinking: '',
      pendingApprovals: [],
    });
  });

  it('i18n dictionaries have matching keys for zh-CN and en-US and interpolate params', () => {
    const zhKeys = Object.keys(I18N_DICTIONARIES['zh-CN']).sort();
    const enKeys = Object.keys(I18N_DICTIONARIES['en-US']).sort();
    expect(enKeys).toEqual(zhKeys);
    expect(translate('en-US', 'status.backendAndPerm', { backend: 'Local', perm: 'Ask' })).toBe(
      'Backend: Local · Permission: Ask',
    );
  });

  it('triggers desktop notifications for plan approval, worker waiting, and completed turns, while respecting mute & cancel', () => {
    const notifySpy = vi.fn().mockResolvedValue(undefined);
    installMockBridge({ showNotification: notifySpy });

    // Plan approval requested
    useAppStore.getState().applySseEvent({ id: 1, event: 'plan_approval_requested', data: {} });
    expect(notifySpy).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'CodexFlow 计划已生成，等待审批',
        onlyWhenUnfocused: true,
      }),
    );

    // Worker waiting user
    useAppStore.getState().applySseEvent({
      id: 2,
      event: 'worker_waiting_user',
      data: { content: '需要人工确认' },
    });
    expect(notifySpy).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'CodexFlow 后台作业等待审批',
        body: '需要人工确认',
      }),
    );

    // Completed turn notifies; cancelled turn does NOT notify
    useAppStore.setState({ isTurnRunning: true, streamingContent: '完成啦' });
    useAppStore.getState().applySseEvent({ id: 3, event: 'done', data: {} });
    expect(notifySpy).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'CodexFlow 回合已完成',
      }),
    );

    notifySpy.mockClear();
    useAppStore.setState({ isTurnRunning: true, streamingContent: '部分输出' });
    useAppStore.getState().applySseEvent({
      id: 4,
      event: 'done',
      data: { source: 'cancelled' },
    });
    expect(notifySpy).not.toHaveBeenCalled();

    // Turning off notifications suppresses popups
    usePreferencesStore.getState().setNotificationsEnabled(false);
    useAppStore.getState().applySseEvent({ id: 5, event: 'plan_approval_requested', data: {} });
    expect(notifySpy).not.toHaveBeenCalled();
  });

  it('syncs theme, language, and minimizeToTrayOnClose to DOM and main process bridge', () => {
    const prefSpy = vi.fn().mockResolvedValue(undefined);
    installMockBridge({ setDesktopPreferences: prefSpy });

    usePreferencesStore.getState().setTheme('light');
    usePreferencesStore.getState().setLanguage('en-US');
    usePreferencesStore.getState().setMinimizeToTrayOnClose(true);

    expect(document.documentElement.classList.contains('light')).toBe(true);
    expect(document.documentElement.lang).toBe('en-US');
    expect(prefSpy).toHaveBeenLastCalledWith({
      theme: 'light',
      language: 'en-US',
      minimizeToTrayOnClose: true,
    });
  });

  it('filters commands and executes selected item via keyboard in CommandPaletteModal', () => {
    const onRunTheme = vi.fn();
    const onRunNew = vi.fn();
    const onOpenChange = vi.fn();

    render(
      <CommandPaletteModal
        open
        onOpenChange={onOpenChange}
        commands={[
          {
            id: 'new-conversation',
            group: '工作区与对话',
            label: '新建对话',
            keywords: 'new chat',
            onRun: onRunNew,
          },
          {
            id: 'toggle-theme',
            group: '外观与偏好',
            label: '切换深色 / 浅色主题',
            keywords: 'theme light dark',
            onRun: onRunTheme,
          },
        ]}
      />,
    );

    const input = screen.getByTestId('command-palette-input');
    fireEvent.change(input, { target: { value: 'theme' } });
    expect(screen.queryByTestId('command-item-new-conversation')).toBeNull();
    expect(screen.getByTestId('command-item-toggle-theme')).toBeInTheDocument();

    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onRunTheme).toHaveBeenCalledTimes(1);
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it('renders Preferences tab in SettingsModal and handles StatusBar theme/language toggles', async () => {
    const checkUpdatesSpy = vi.fn().mockResolvedValue({
      status: 'not-available',
      currentVersion: '0.1.5',
      availableVersion: null,
      downloadProgress: null,
      message: null,
    });
    installMockBridge({ checkForUpdates: checkUpdatesSpy });

    render(
      <>
        <SettingsModal
          open
          initialTab="preferences"
          systemStatus={null}
          executionSettings={null}
          modelSettings={null}
          controlPlaneAvailable
          onClose={vi.fn()}
          onUpdated={vi.fn()}
        />
        <StatusBar
          connection={{
            mode: 'managed',
            status: 'ready',
            baseUrl: 'http://127.0.0.1:59999',
            targetBaseUrl: 'http://127.0.0.1:5206',
            accessToken: 'acc',
            instanceId: 'inst-1',
            controlPlaneAvailable: true,
            selfApprovalProtection: true,
            dataDirectory: 'D:/data',
            lastError: null,
            restartCount: 0,
          }}
          sseState="connected"
          systemStatus={null}
          contextWindow={null}
          onOpenSettings={vi.fn()}
          onOpenCommandPalette={vi.fn()}
          onRestartBackend={vi.fn()}
        />
      </>,
    );

    fireEvent.click(screen.getByTestId('theme-select-light'));
    expect(usePreferencesStore.getState().theme).toBe('light');

    fireEvent.click(screen.getByTestId('lang-select-en-US'));
    expect(usePreferencesStore.getState().language).toBe('en-US');
    expect(screen.getByText('Appearance & Desktop')).toBeInTheDocument();

    await act(async () => {
      fireEvent.click(screen.getByTestId('check-updates-button'));
    });
    expect(checkUpdatesSpy).toHaveBeenCalledTimes(1);
  });
});
