import React from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { BackendConnectionPublicInfo, DesktopBridge } from '../../src/shared/ipc';
import { PlanApprovalCard } from '../../src/renderer/components/PlanApprovalCard';
import { SettingsModal } from '../../src/renderer/components/SettingsModal';
import { ToolApprovalCard } from '../../src/renderer/components/ToolApprovalCard';
import {
  isUnverifiedWithoutIsolation,
  resolveJobVerificationKind,
  VerificationBadge,
} from '../../src/renderer/components/VerificationBadge';

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
    submitPlanChatAction: vi.fn().mockResolvedValue({ ok: true, status: 202 }),
    updateExecutionSettings: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    updateModelSettings: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    listPermissionRules: vi.fn().mockResolvedValue({ ok: true, status: 200, data: { count: 0, rules: [] } }),
    deletePermissionRule: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    openLocalWorkspace: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    pickFolder: vi.fn().mockResolvedValue(null),
    pickStandaloneTokenFile: vi.fn().mockResolvedValue(null),
    discoverStandaloneInstance: vi.fn().mockResolvedValue(null),
    connectStandalone: vi.fn(),
    switchToManaged: vi.fn(),
    restartBackend: vi.fn(),
    openExternal: vi.fn().mockResolvedValue(true),
    showNotification: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  };
  window.codexflow = bridge;
  return bridge;
}

describe('Security boundary & MVP interactive cards', () => {

  it('requires secondary confirmation when relaxing permission mode or downgrading Docker->Local in SettingsModal', async () => {
    const updateExec = vi.fn().mockResolvedValue({ ok: true, status: 200 });
    installMockBridge({ updateExecutionSettings: updateExec });

    render(
      <SettingsModal
        open
        systemStatus={null}
        executionSettings={{
          backend: 'Local',
          permissionMode: 'Ask',
          controlPlaneAvailable: true,
          selfApprovalProtection: true,
          tokenDeliveryMode: 'Pipe',
        }}
        modelSettings={null}
        controlPlaneAvailable
        onClose={vi.fn()}
        onUpdated={vi.fn()}
      />,
    );

    const modeSelect = screen.getByLabelText('权限模式');
    fireEvent.change(modeSelect, { target: { value: 'FullAuto' } });
    fireEvent.click(screen.getByText('保存执行设置'));

    expect(screen.getByTestId('relax-permission-confirm')).toBeInTheDocument();
    expect(updateExec).not.toHaveBeenCalled();

    await act(async () => {
      fireEvent.click(screen.getByText('确认放宽'));
    });
    expect(updateExec).toHaveBeenCalledWith({ backend: 'Local', permissionMode: 'FullAuto' });
  });

  it('B5: lists PersistedPermissionRule using backend id/sessionAgnosticDigest fields and deletes by id', async () => {
    const deleteRule = vi.fn().mockResolvedValue({ ok: true, status: 200, data: { removed: true, ruleId: 'rule-real-9' } });
    const listRules = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      data: {
        count: 1,
        rules: [
          {
            id: 'rule-real-9',
            sessionAgnosticDigest: 'digest-abc',
            toolName: 'run_command',
            executable: 'dotnet.exe',
            argv: ['dotnet', 'test'],
            createdAtUtc: '2026-11-16T12:00:00Z',
            note: 'trusted test command',
          },
        ],
      },
    });
    installMockBridge({ listPermissionRules: listRules, deletePermissionRule: deleteRule });

    render(
      <SettingsModal
        open
        systemStatus={null}
        executionSettings={{
          backend: 'Local',
          permissionMode: 'Ask',
          controlPlaneAvailable: true,
          selfApprovalProtection: true,
          tokenDeliveryMode: 'Pipe',
        }}
        modelSettings={null}
        controlPlaneAvailable
        onClose={vi.fn()}
        onUpdated={vi.fn()}
      />,
    );

    await act(async () => {
      fireEvent.click(screen.getByText('始终允许规则'));
    });

    await waitFor(() => expect(screen.getByText('dotnet.exe dotnet test')).toBeInTheDocument());
    await act(async () => {
      fireEvent.click(screen.getByTitle('撤销规则'));
    });

    expect(deleteRule).toHaveBeenCalledWith('rule-real-9');
  });

  it('defensively hides allow_always/allow_session when executesWorkspaceCode=true even if allowedDecisions contains them', async () => {
    const approveFn = vi.fn().mockResolvedValue({ ok: true, status: 200 });
    installMockBridge({ approveToolExecution: approveFn });

    render(
      <ToolApprovalCard
        approval={{
          approvalId: 'appr-1',
          requestDigest: 'sha256-1234',
          sessionId: 'sess-1',
          tool: 'run_command',
          executable: 'python.exe',
          interpreterChain: [],
          argv: ['python', 'build.py'],
          cwd: 'D:/repo',
          backend: 'Local',
          network: 'Host',
          filesystem: 'unrestricted',
          reason: 'Ask mode',
          expiresAtUtc: '2026-11-16T00:10:00Z',
          // Conflicting/permissive list deliberately passed to test UI defense-in-depth
          allowedDecisions: ['allow_once', 'allow_session', 'allow_always', 'deny'],
          executesWorkspaceCode: true,
          trustedOriginVerified: false,
        }}
        controlPlaneAvailable
        onResolved={vi.fn()}
      />,
    );

    expect(screen.getByText(/会读取并执行工作区内的项目代码/)).toBeInTheDocument();
    expect(screen.queryByText('始终允许')).not.toBeInTheDocument();
    expect(screen.queryByText('本会话允许')).not.toBeInTheDocument();

    await act(async () => {
      fireEvent.click(screen.getByText('允许一次'));
    });
    expect(approveFn).toHaveBeenCalledWith({
      approvalId: 'appr-1',
      requestDigest: 'sha256-1234',
      decision: 'allow_once',
    });
  });

  it('B10 & R5: never shows green isolated badge without explicit verification proof, even when summary contains Docker', () => {
    // 1. Completed job without receipt -> pending verification (NOT green verified)
    expect(resolveJobVerificationKind({ jobId: 'j0', status: 'Completed' }, null)).toBe('unverified');
    // Even if summary mentions "Docker unavailable; local checks passed" and verification.passed=true, NO green badge!
    const dockerSummaryTrap = resolveJobVerificationKind(
      {
        jobId: 'j-trap',
        status: 'Completed',
        summary: 'Docker unavailable; local checks passed',
        verification: { passed: true, summary: 'Docker unavailable; local checks passed' },
      },
      null,
    );
    expect(dockerSummaryTrap).toBe('unverified');

    const { rerender } = render(<VerificationBadge kind={dockerSummaryTrap} />);
    expect(screen.queryByTestId('verified-isolation-badge')).not.toBeInTheDocument();
    expect(screen.getByTestId('pending-verification-badge')).toHaveTextContent('待隔离验证');

    // Omitting kind defaults safely to pending (never green verified)
    rerender(<VerificationBadge />);
    expect(screen.queryByTestId('verified-isolation-badge')).not.toBeInTheDocument();

    // Conflicting evidence: explicit job failure / verification.passed=false ALWAYS overrides any receipt
    expect(
      resolveJobVerificationKind(
        { jobId: 'j-fail-1', status: 'Failed' },
        { verifiedWithoutIsolation: true, verificationStatus: 'skipped(requires_docker_backend)' },
      ),
    ).toBe('failed');
    expect(
      resolveJobVerificationKind(
        { jobId: 'j-fail-2', status: 'Completed', verification: { passed: false } },
        {
          identityVerified: true,
          buildPassed: true,
          testsPassed: true,
          verifiedWithoutIsolation: false,
          verificationImageId: 'sha256:container123',
        },
      ),
    ).toBe('failed');

    // 2. Receipt with testsPassed=false -> failed (even if verifiedWithoutIsolation=true)
    const failKind = resolveJobVerificationKind(
      { jobId: 'j1', status: 'Completed' },
      {
        identityVerified: true,
        buildPassed: true,
        testsPassed: false,
        verifiedWithoutIsolation: true,
        verificationImageId: 'sha256:img',
      },
    );
    expect(failKind).toBe('failed');
    rerender(<VerificationBadge kind={failKind} />);
    expect(screen.getByTestId('failed-verification-badge')).toHaveTextContent('验证失败');

    // 3. Local skipped -> verified_unisolated
    expect(
      isUnverifiedWithoutIsolation(
        { jobId: 'j2', status: 'Completed' },
        { verificationStatus: 'skipped(requires_docker_backend)', verifiedWithoutIsolation: true },
        'Local',
      ),
    ).toBe(true);

    // 4. Full positive receipt with container image -> verified_isolated (green)
    const okKind = resolveJobVerificationKind(
      { jobId: 'j3', status: 'Completed' },
      {
        identityVerified: true,
        buildPassed: true,
        testsPassed: true,
        verifiedWithoutIsolation: false,
        verificationImageId: 'sha256:container123',
      },
    );
    expect(okKind).toBe('verified_isolated');
    rerender(<VerificationBadge kind={okKind} />);
    expect(screen.getByTestId('verified-isolation-badge')).toHaveTextContent('隔离已验证');
  });

  it('submits plan approval with version and markdownHash', async () => {
    const submitPlan = vi.fn().mockResolvedValue({ ok: true, status: 202 });
    installMockBridge({ submitPlanChatAction: submitPlan });

    render(
      <PlanApprovalCard
        sessionId="sess-1"
        conversationId="conv-1"
        plan={{
          planArtifactId: 'plan-9',
          version: '2',
          markdownHash: 'hash-abc',
          status: 'ReadyForApproval',
          goal: 'Fix',
          markdown: '# Plan',
          tasks: [],
          hostRefusal: null,
          updatedAtUtc: '2026-11-16T00:00:00Z',
        }}
        executionBackend="Local"
        controlPlaneAvailable
        onSubmitted={vi.fn()}
      />,
    );

    await act(async () => {
      fireEvent.click(screen.getByText('批准并执行'));
    });
    expect(submitPlan).toHaveBeenCalledWith(
      expect.objectContaining({
        sessionId: 'sess-1',
        structuredAction: expect.objectContaining({
          action: 'approve',
          planArtifactId: 'plan-9',
          planVersion: '2',
          planHash: 'hash-abc',
          startExecution: true,
        }),
      }),
    );
  });
});
