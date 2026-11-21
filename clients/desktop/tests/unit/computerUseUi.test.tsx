import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { Composer } from '../../src/renderer/components/Composer';
import { ComputerApprovalCard } from '../../src/renderer/components/ComputerApprovalCard';
import { ComputerPanel } from '../../src/renderer/components/ComputerPanel';
import { EnvironmentCheckPanel } from '../../src/renderer/components/EnvironmentCheckPanel';
import { SettingsModal } from '../../src/renderer/components/SettingsModal';
import { WorkspaceInspector } from '../../src/renderer/components/WorkspaceInspector';
import { useAppStore } from '../../src/renderer/stores/useAppStore';
import type { DesktopBridge } from '../../src/shared/ipc';

describe('Computer Use desktop UI & store', () => {
  beforeEach(() => {
    useAppStore.getState().setActiveSession('s-comp-1');
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it('reduces computer SSE events in useAppStore and clears approvals on resolution', () => {
    useAppStore.getState().applySseEvent({
      id: 1,
      event: 'computer_state_updated',
      data: {
        sessionId: 's-comp-1',
        status: 'running',
        active: true,
        enabled: true,
        pinnedProcessName: 'notepad',
        pinnedWindowTitle: 'Untitled - Notepad',
        screenshotSeq: 2,
        imageWidth: 1280,
        imageHeight: 720,
        coordinateMode: 'ScreenshotPixels',
        lastActionSummary: 'click (640, 360)',
        hostWindowAvoidanceRequested: true,
        recentActions: [],
      },
    } as never);

    expect(useAppStore.getState().computerState?.pinnedProcessName).toBe('notepad');
    expect(useAppStore.getState().computerState?.active).toBe(true);

    useAppStore.getState().applySseEvent({
      id: 2,
      event: 'computer_approval_requested',
      data: {
        approvalId: 'capr-123',
        requestDigest: 'digest-123',
        sessionId: 's-comp-1',
        tool: 'computer_act',
        action: 'click',
        targetProcessName: 'notepad',
        targetWindowTitle: 'Untitled - Notepad',
        targetWindowHandle: '0x1234',
        actionSummary: '左键单击 (640, 360)',
        riskReason: '首次交互目标应用',
        targetX: 640,
        targetY: 360,
        expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
        allowedDecisions: ['allow_once', 'allow_session', 'deny'],
        controlPlaneAvailable: true,
        selfApprovalProtection: true,
        previewImageBase64: 'AQID',
      },
    } as never);

    expect(useAppStore.getState().pendingComputerApprovals).toHaveLength(1);

    useAppStore.getState().applySseEvent({
      id: 3,
      event: 'computer_approval_resolved',
      data: {
        approvalId: 'capr-123',
        sessionId: 's-comp-1',
        decision: 'allow_once',
        resolvedAtUtc: new Date().toISOString(),
      },
    } as never);

    expect(useAppStore.getState().pendingComputerApprovals).toHaveLength(0);
  });

  it('renders preview thumbnail in ComputerApprovalCard and handles allow_session + expiry + 409', async () => {
    const approveSpy = vi
      .fn()
      .mockResolvedValueOnce({ ok: true, status: 200 })
      .mockResolvedValueOnce({ ok: false, status: 409, errorMessage: 'Expired' });
    Object.defineProperty(window, 'codexflow', {
      configurable: true,
      writable: true,
      value: { approveToolExecution: approveSpy } as unknown as DesktopBridge,
    });
    const onResolved = vi.fn();

    const { rerender } = render(
      <ComputerApprovalCard
        controlPlaneAvailable
        onResolved={onResolved}
        approval={{
          approvalId: 'capr-999',
          requestDigest: 'digest-xyz',
          sessionId: 's-comp-1',
          tool: 'computer_act',
          action: 'click',
          targetProcessName: 'TextEdit',
          targetWindowTitle: 'Draft.txt',
          targetWindowHandle: '101',
          actionSummary: 'click at (320, 240)',
          riskReason: '首次操作应用',
          targetX: 320,
          targetY: 240,
          expiresAtUtc: new Date(Date.now() + 120_000).toISOString(),
          allowedDecisions: ['allow_once', 'allow_session', 'deny'],
          controlPlaneAvailable: true,
          selfApprovalProtection: true,
          previewImageBase64: 'iVBORw0KGgo=',
        }}
      />,
    );

    const img = screen.getByTestId('computer-approval-preview') as HTMLImageElement;
    expect(img.src).toBe('data:image/png;base64,iVBORw0KGgo=');
    expect(screen.getByTestId('computer-approval-coords')).toHaveTextContent('(320, 240)');

    fireEvent.click(screen.getByText('本会话允许此应用'));

    await waitFor(() => {
      expect(approveSpy).toHaveBeenCalledWith({
        approvalId: 'capr-999',
        requestDigest: 'digest-xyz',
        decision: 'allow_session',
        note: undefined,
      });
      expect(onResolved).toHaveBeenCalledWith('capr-999');
    });

    // Expired state disables action buttons
    rerender(
      <ComputerApprovalCard
        controlPlaneAvailable
        onResolved={onResolved}
        approval={{
          approvalId: 'capr-exp',
          requestDigest: 'digest-exp',
          sessionId: 's-comp-1',
          tool: 'computer_act',
          action: 'click',
          targetProcessName: 'TextEdit',
          targetWindowTitle: 'Draft.txt',
          targetWindowHandle: '101',
          actionSummary: 'click at (320, 240)',
          riskReason: '首次操作应用',
          targetX: 320,
          targetY: 240,
          expiresAtUtc: new Date(Date.now() - 5_000).toISOString(),
          allowedDecisions: ['allow_once', 'deny'],
          controlPlaneAvailable: true,
          selfApprovalProtection: true,
        }}
      />,
    );
    expect((screen.getByText('允许一次').closest('button') as HTMLButtonElement).disabled).toBe(true);

    // 409 conflict auto-clears card
    rerender(
      <ComputerApprovalCard
        controlPlaneAvailable
        onResolved={onResolved}
        approval={{
          approvalId: 'capr-409',
          requestDigest: 'digest-409',
          sessionId: 's-comp-1',
          tool: 'computer_act',
          action: 'click',
          targetProcessName: 'TextEdit',
          targetWindowTitle: 'Draft.txt',
          targetWindowHandle: '101',
          actionSummary: 'click at (320, 240)',
          riskReason: '首次操作应用',
          targetX: 320,
          targetY: 240,
          expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
          allowedDecisions: ['allow_once', 'deny'],
          controlPlaneAvailable: true,
          selfApprovalProtection: true,
        }}
      />,
    );

    fireEvent.click(screen.getByText('允许一次'));
    await waitFor(() => {
      expect(onResolved).toHaveBeenCalledWith('capr-409');
    });
  });

  it('fetches authenticated computer screenshot, revokes object URL, and supports emergency stop + takeover handoff', async () => {
    const createUrlSpy = vi.fn().mockReturnValue('blob:mock-cshot-1');
    const revokeUrlSpy = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, writable: true, value: createUrlSpy });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, writable: true, value: revokeUrlSpy });

    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockImplementation((input) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.endsWith('/computer/screenshot')) {
        return Promise.resolve(
          new Response(new Uint8Array([0x89, 0x50, 0x4e, 0x47]), {
            status: 200,
            headers: {
              'Content-Type': 'image/png',
              'X-Screenshot-Seq': '5',
              ETag: '"cf-cshot-g1-5"',
            },
          }),
        );
      }
      return Promise.resolve(
        new Response(
          JSON.stringify({
            sessionId: 's-comp-1',
            status: 'emergency_stopped',
            active: false,
            enabled: true,
            screenshotSeq: 5,
            coordinateMode: 'ScreenshotPixels',
            recentActions: [],
            emergencyStopped: true,
            takeoverPending: false,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      );
    });

    const stopSpy = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      data: { stopped: true, cancelledSessions: 1, message: 'stopped' },
    });
    const completeTakeoverSpy = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      data: { active: false, sessionId: 's-comp-1' },
    });
    Object.defineProperty(window, 'codexflow', {
      configurable: true,
      writable: true,
      value: {
        emergencyStopComputer: stopSpy,
        completeComputerTakeover: completeTakeoverSpy,
      } as unknown as DesktopBridge,
    });
    const onEmergencyStopped = vi.fn();

    const { unmount } = render(
      <ComputerPanel
        auth={{ baseUrl: 'http://127.0.0.1:32586', accessToken: 'tok-secret' }}
        sessionId="s-comp-1"
        controlPlaneAvailable
        onStateUpdated={vi.fn()}
        onEmergencyStopped={onEmergencyStopped}
        computerState={{
          sessionId: 's-comp-1',
          status: 'takeover',
          active: true,
          enabled: true,
          pinnedWindowHandle: '0x20',
          pinnedProcessName: 'Calculator',
          pinnedWindowTitle: 'Calculator',
          screenshotSeq: 5,
          imageWidth: 800,
          imageHeight: 600,
          coordinateMode: 'ScreenshotPixels',
          lastActionSummary: '等待用户完成验证码',
          recentActions: [
            {
              seq: 1,
              tool: 'computer_act',
              action: 'click',
              targetProcessName: 'Calculator',
              targetWindowTitle: 'Calculator',
              targetX: 200,
              targetY: 300,
              succeeded: true,
              summary: 'clicked button 7',
              durationMs: 42,
              timestampUtc: new Date().toISOString(),
            },
          ],
          takeoverPending: true,
          takeoverReason: '需要人工登录验证',
          emergencyStopped: false,
          hostWindowAvoidanceRequested: false,
        }}
      />,
    );

    await waitFor(() => {
      const img = screen.getByTestId('computer-preview-image') as HTMLImageElement;
      expect(img.src).toBe('blob:mock-cshot-1');
    });

    expect(fetchSpy).toHaveBeenCalledWith(
      'http://127.0.0.1:32586/api/gateway/session/s-comp-1/computer/screenshot',
      expect.objectContaining({
        headers: expect.objectContaining({ Authorization: 'Bearer tok-secret' }),
      }),
    );

    fireEvent.click(screen.getByTestId('computer-complete-takeover-btn'));
    await waitFor(() => {
      expect(completeTakeoverSpy).toHaveBeenCalledWith('s-comp-1');
    });

    fireEvent.click(screen.getByTestId('computer-emergency-stop-btn'));
    await waitFor(() => {
      expect(stopSpy).toHaveBeenCalledWith({ sessionId: 's-comp-1', reason: 'panel_button' });
      expect(onEmergencyStopped).toHaveBeenCalled();
    });

    unmount();
    expect(revokeUrlSpy).toHaveBeenCalledWith('blob:mock-cshot-1');
  });

  it('shows Computer tab in WorkspaceInspector during Daily mode and triggers IME-safe emergency stop from Composer', async () => {
    render(
      <WorkspaceInspector
        open
        activeTab="computer"
        workMode="Daily"
        auth={null}
        sessionId="s-comp-daily"
        workspaceRevision={0}
        changes={undefined}
        changesLoading={false}
        changesFetching={false}
        changesError={null}
        jobs={[]}
        verificationReceipts={{}}
        executionBackend="Local"
        computerState={null}
        onSelectTab={vi.fn()}
        onClose={vi.fn()}
        onRefreshChanges={vi.fn()}
        onVerifyJob={vi.fn().mockResolvedValue(undefined)}
        onJobsMutated={vi.fn().mockResolvedValue(undefined)}
      />,
    );

    expect(screen.getByTestId('inspector-tab-computer')).toBeInTheDocument();
    expect(screen.getByTestId('computer-panel')).toBeInTheDocument();

    const onCancel = vi.fn().mockResolvedValue(undefined);
    const onEmergencyStop = vi.fn().mockResolvedValue(undefined);
    render(
      <Composer
        disabled={false}
        isTurnRunning
        isComputerActive
        turnNotice={null}
        onSend={vi.fn().mockResolvedValue(undefined)}
        onCancel={onCancel}
        onEmergencyStop={onEmergencyStop}
      />,
    );

    const textbox = screen.getByRole('textbox');
    // IME composition should NOT trigger emergency stop
    fireEvent.compositionStart(textbox);
    fireEvent.keyDown(textbox, { key: 'Escape', isComposing: true });
    expect(onEmergencyStop).not.toHaveBeenCalled();
    fireEvent.compositionEnd(textbox);

    // Non-composing Ctrl+. should trigger emergency stop and cancel
    fireEvent.keyDown(textbox, { key: '.', ctrlKey: true });
    await waitFor(() => {
      expect(onEmergencyStop).toHaveBeenCalledTimes(1);
      expect(onCancel).toHaveBeenCalledTimes(1);
    });
  });

  it('renders Computer Use settings in SettingsModal + EnvironmentCheckPanel and submits updates', async () => {
    const updateSpy = vi.fn().mockResolvedValue({ ok: true, status: 200 });
    Object.defineProperty(window, 'codexflow', {
      configurable: true,
      writable: true,
      value: {
        updateComputerSettings: updateSpy,
        listPermissionRules: vi.fn().mockResolvedValue({ ok: true, status: 200, data: { count: 0, rules: [] } }),
        setDesktopPreferences: vi.fn().mockResolvedValue(undefined),
        inspectEnvironment: vi.fn().mockResolvedValue({
          platform: 'darwin',
          arch: 'arm64',
          isPackaged: false,
          packageManager: 'brew',
          shellPathEnhanced: true,
          checkedAtIso: '2026-11-21T08:00:00.000Z',
          items: [],
        }),
        onEnvironmentInstallEvent: vi.fn(() => () => undefined),
      } as unknown as DesktopBridge,
    });

    const driverStatus = {
      available: false,
      platform: 'macOS',
      driverName: 'MacOsNativeDriver',
      screenCaptureGranted: false,
      accessibilityGranted: true,
      displayCount: 1,
      diagnosticMessage: 'Screen Recording TCC permission is missing.',
      permissionGuide: '请在系统设置 -> 隐私与安全性 -> 屏幕录制 中勾选 CodexFlow。',
      emergencyStopShortcut: 'CommandOrControl+.',
    };

    render(
      <SettingsModal
        open
        initialTab="computer"
        systemStatus={null}
        executionSettings={null}
        modelSettings={null}
        computerSettings={{
          enabled: false,
          interactionApprovalPolicy: 'HighRiskOnly',
          coordinateMode: 'Auto',
          maxImageLongEdge: 1280,
          maxScreenshotsPerTurn: 10,
          enableGridOverlay: true,
          autoMinimizeHostWindow: true,
          blockedProcessNames: ['1password', 'keepass'],
          emergencyStopShortcut: 'CommandOrControl+.',
          idleTimeoutMinutes: 15,
          driverStatus,
        }}
        controlPlaneAvailable
        onClose={vi.fn()}
        onUpdated={vi.fn()}
      />,
    );

    expect(screen.getByText(/屏幕录制 中勾选 CodexFlow/)).toBeInTheDocument();
    fireEvent.click(screen.getByTestId('computer-enabled-toggle'));
    fireEvent.click(screen.getByTestId('computer-save-settings-btn'));

    await waitFor(() => {
      expect(updateSpy).toHaveBeenCalledWith(
        expect.objectContaining({
          enabled: true,
          interactionApprovalPolicy: 'HighRiskOnly',
          coordinateMode: 'Auto',
          blockedProcessNames: ['1password', 'keepass'],
        }),
      );
    });

    render(<EnvironmentCheckPanel active computerDriverStatus={driverStatus} />);
    await waitFor(() => {
      expect(screen.getByTestId('env-item-computer-driver')).toHaveTextContent('MacOsNativeDriver');
    });
  });
});
