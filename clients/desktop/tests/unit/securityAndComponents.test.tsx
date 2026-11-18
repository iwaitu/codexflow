import React from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { BackendConnectionPublicInfo, DesktopBridge } from '../../src/shared/ipc';
import { apiClient } from '../../src/renderer/lib/apiClient';
import { parseUnifiedDiff } from '../../src/renderer/lib/diffParser';
import { PlanApprovalCard } from '../../src/renderer/components/PlanApprovalCard';
import { CodexFlowBrand } from '../../src/renderer/components/CodexFlowBrand';
import { Composer } from '../../src/renderer/components/Composer';
import { ContextUsageRing } from '../../src/renderer/components/ContextUsageRing';
import { JobsPanel } from '../../src/renderer/components/JobsPanel';
import { MarkdownRenderer } from '../../src/renderer/components/MarkdownRenderer';
import { MessageItem } from '../../src/renderer/components/MessageItem';
import { SettingsModal } from '../../src/renderer/components/SettingsModal';
import { Sidebar } from '../../src/renderer/components/Sidebar';
import { ToolApprovalCard } from '../../src/renderer/components/ToolApprovalCard';
import { WelcomeWizardModal } from '../../src/renderer/components/WelcomeWizardModal';
import { WorkspaceChangesPanel } from '../../src/renderer/components/WorkspaceChangesPanel';
import { WorkspaceFilesPanel } from '../../src/renderer/components/WorkspaceFilesPanel';
import {
  formatOptimisticMessageWithAttachments,
  parseMessageWithAttachments,
} from '../../src/renderer/lib/attachments';
import { parseModelSettingsInput } from '../../src/renderer/lib/modelSettingsForm';
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
    getApprovalScript: vi.fn().mockResolvedValue({ ok: false, status: 404 }),
    setSessionPlanMode: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    submitPlanChatAction: vi.fn().mockResolvedValue({ ok: true, status: 202 }),
    updateExecutionSettings: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    updateModelSettings: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    listPermissionRules: vi.fn().mockResolvedValue({ ok: true, status: 200, data: { count: 0, rules: [] } }),
    deletePermissionRule: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    openLocalWorkspace: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
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

  it('validates maxTokensLength, temperature and topP in parseModelSettingsInput', () => {
    const valid = parseModelSettingsInput({
      apiUrl: ' http://10.0.30.247:8001/v1 ',
      apiMode: 'ChatCompletions',
      model: ' qwen3.8-27b-fp8 ',
      maxTokensLength: '131072',
      temperature: '0.7',
      topP: '0.9',
      apiKey: '',
    });
    expect(valid).toEqual({
      ok: true,
      hasAnyChange: true,
      payload: {
        apiUrl: 'http://10.0.30.247:8001/v1',
        apiMode: 'ChatCompletions',
        model: 'qwen3.8-27b-fp8',
        maxTokensLength: 131072,
        temperature: 0.7,
        topP: 0.9,
        apiKey: undefined,
      },
    });

    expect(
      parseModelSettingsInput({
        apiUrl: '',
        apiMode: 'ChatCompletions',
        model: '',
        maxTokensLength: '0',
        apiKey: '',
      }),
    ).toEqual({
      ok: false,
      error: '最大上下文长度必须为大于 0 的整数（例如 128000）。',
    });

    expect(
      parseModelSettingsInput({
        apiUrl: '',
        apiMode: 'ChatCompletions',
        model: '',
        maxTokensLength: '128.5',
        apiKey: '',
      }).ok,
    ).toBe(false);

    expect(
      parseModelSettingsInput({
        apiUrl: '',
        apiMode: 'ChatCompletions',
        model: '',
        maxTokensLength: '',
        temperature: '2.5',
        apiKey: '',
      }),
    ).toEqual({
      ok: false,
      error: 'Temperature 必须在 0 到 2 之间。',
    });

    expect(
      parseModelSettingsInput({
        apiUrl: '',
        apiMode: 'ChatCompletions',
        model: '',
        maxTokensLength: '',
        topP: '1.2',
        apiKey: '',
      }),
    ).toEqual({
      ok: false,
      error: 'TopP 必须在 0 到 1 之间。',
    });
  });

  it('allows setting maxTokensLength via preset or custom value in WelcomeWizardModal and blocks invalid input', async () => {
    const updateModel = vi.fn().mockResolvedValue({ ok: true, status: 200 });
    const updateExec = vi.fn().mockResolvedValue({ ok: true, status: 200 });
    const onSaved = vi.fn();
    const onClose = vi.fn();
    installMockBridge({ updateModelSettings: updateModel, updateExecutionSettings: updateExec });

    render(
      <WelcomeWizardModal
        open
        systemStatus={{
          executionBackend: 'Local',
          permissionMode: 'Ask',
          backendAvailable: true,
          dockerAvailable: false,
          controlPlaneAvailable: true,
          selfApprovalProtection: true,
          tokenDeliveryMode: 'Pipe',
          version: '0.1.3',
          modelConfigured: false,
          modelReachable: false,
          modelProbeStatus: 'Unconfigured',
          preflightMode: 'Report',
          preflightIssues: [],
        }}
        modelSettings={null}
        controlPlaneAvailable
        onClose={onClose}
        onSaved={onSaved}
      />,
    );

    const maxContextInput = screen.getByLabelText('最大上下文长度') as HTMLInputElement;
    expect(maxContextInput.value).toBe('128000');

    // Invalid input blocks submission before calling IPC
    fireEvent.change(maxContextInput, { target: { value: '0' } });
    fireEvent.click(screen.getByText('保存并开始'));
    expect(screen.getByText('最大上下文长度必须为大于 0 的整数（例如 128000）。')).toBeInTheDocument();
    expect(updateModel).not.toHaveBeenCalled();

    // Quick preset 64K updates input and submits correctly
    fireEvent.click(screen.getByText('64K'));
    expect(maxContextInput.value).toBe('65536');

    await act(async () => {
      fireEvent.click(screen.getByText('保存并开始'));
    });

    expect(updateModel).toHaveBeenCalledWith(
      expect.objectContaining({
        apiMode: 'ChatCompletions',
        maxTokensLength: 65536,
      }),
    );
    expect(updateExec).toHaveBeenCalledWith({ backend: 'Local', permissionMode: 'Ask' });
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('displays and updates maxTokensLength, temperature and topP in SettingsModal model tab', async () => {
    const updateModel = vi.fn().mockResolvedValue({ ok: true, status: 200 });
    const onUpdated = vi.fn();
    installMockBridge({ updateModelSettings: updateModel });

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
        modelSettings={{
          apiUrl: 'http://10.0.30.247:8001/v1',
          apiMode: 'ChatCompletions',
          model: 'qwen3.8-27b-fp8',
          maxTokensLength: 128000,
          temperature: 0.6,
          topP: 0.95,
          apiKeySet: true,
          apiKeyLast4: '1234',
          supportsMultimodal: false,
        }}
        controlPlaneAvailable
        onClose={vi.fn()}
        onUpdated={onUpdated}
      />,
    );

    fireEvent.click(screen.getByText('模型设置'));

    const maxContextInput = screen.getByLabelText('最大上下文长度') as HTMLInputElement;
    const tempInput = screen.getByLabelText('Temperature') as HTMLInputElement;
    const topPInput = screen.getByLabelText('TopP') as HTMLInputElement;
    expect(maxContextInput.value).toBe('128000');
    expect(tempInput.value).toBe('0.6');
    expect(topPInput.value).toBe('0.95');

    fireEvent.change(maxContextInput, { target: { value: '200000' } });
    fireEvent.change(tempInput, { target: { value: '0.2' } });
    fireEvent.change(topPInput, { target: { value: '0.85' } });

    await act(async () => {
      fireEvent.click(screen.getByText('保存模型设置'));
    });

    expect(updateModel).toHaveBeenCalledWith({
      apiUrl: 'http://10.0.30.247:8001/v1',
      apiMode: 'ChatCompletions',
      model: 'qwen3.8-27b-fp8',
      maxTokensLength: 200000,
      temperature: 0.2,
      topP: 0.85,
      apiKey: undefined,
    });
    expect(onUpdated).toHaveBeenCalledTimes(1);
  });

  it('renders ContextUsageRing progressbar, sub-1% precision, cumulative tokens, and high-usage warning color', () => {
    const { rerender } = render(
      <ContextUsageRing
        contextWindow={{
          sessionId: 'sess-1',
          estimatedTokens: 7048,
          estimatedChars: 1688,
          recentTurns: 8,
          summaryChars: 0,
          totalPromptTokens: 25003,
          totalCachedPromptTokens: 9696,
          totalCompletionTokens: 1430,
          totalSessionTokens: 26433,
          modelMaxTokens: 262144,
          compressionTriggerTokens: 222822,
          usageRatio: 7048 / 262144,
          triggerRatio: 7048 / 222822,
          updatedAtUtc: '2026-11-17T01:29:11Z',
        }}
      />,
    );

    const bar = screen.getByRole('progressbar', { name: '上下文窗口使用率' });
    expect(bar).toHaveAttribute('aria-valuenow', '2.7');
    expect(screen.getByTestId('context-usage-ring')).toHaveTextContent('上下文 3%');
    expect(screen.getByTestId('context-usage-ring')).toHaveTextContent('7,048 / 262,144');
    expect(screen.getByTestId('context-usage-ring')).toHaveTextContent('累计 26,433 tok');

    // Sub-1% shows one decimal place instead of misleading 0%
    rerender(
      <ContextUsageRing
        contextWindow={{
          sessionId: 'sess-1',
          estimatedTokens: 800,
          estimatedChars: 1600,
          recentTurns: 2,
          summaryChars: 0,
          totalPromptTokens: 800,
          totalCachedPromptTokens: 0,
          totalCompletionTokens: 100,
          totalSessionTokens: 900,
          modelMaxTokens: 262144,
          compressionTriggerTokens: 222822,
          usageRatio: 800 / 262144,
          triggerRatio: 800 / 222822,
          updatedAtUtc: null,
        }}
      />,
    );
    expect(screen.getByTestId('context-usage-ring')).toHaveTextContent('上下文 0.3%');

    // >= 85% switches ring stroke to rose-400
    rerender(
      <ContextUsageRing
        contextWindow={{
          sessionId: 'sess-1',
          estimatedTokens: 112000,
          estimatedChars: 448000,
          recentTurns: 20,
          summaryChars: 0,
          totalPromptTokens: 200000,
          totalCachedPromptTokens: 0,
          totalCompletionTokens: 10000,
          totalSessionTokens: 210000,
          modelMaxTokens: 128000,
          compressionTriggerTokens: 108800,
          usageRatio: 112000 / 128000,
          triggerRatio: 112000 / 108800,
          updatedAtUtc: null,
        }}
      />,
    );
    const circles = screen.getByRole('progressbar', { name: '上下文窗口使用率' }).querySelectorAll('circle');
    expect(circles[1]?.getAttribute('class')).toContain('stroke-rose-400');
  });

  it('renders GFM Markdown tables with headers, rows, alignment styles, and scroll wrapper', () => {
    render(
      <MarkdownRenderer
        content={`
| 指标 | 2024年实际 | 单位 |
|:---|:---:|---:|
| 耕地总面积 | 115.92 | 万亩 |
| 永久基本农田 | 106.19 | 万亩 |
`}
      />,
    );

    const table = screen.getByRole('table');
    expect(table).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: '指标' })).toHaveStyle({ textAlign: 'left' });
    expect(screen.getByRole('columnheader', { name: '2024年实际' })).toHaveStyle({ textAlign: 'center' });
    expect(screen.getByRole('columnheader', { name: '单位' })).toHaveStyle({ textAlign: 'right' });
    expect(screen.getByRole('cell', { name: '115.92' })).toBeInTheDocument();
    expect(screen.getByRole('cell', { name: '永久基本农田' })).toBeInTheDocument();
  });

  it('renders CodexFlowBrand animated SVG logo with isolated gradient IDs across multiple instances', () => {
    render(
      <div>
        <CodexFlowBrand subtitle="Desktop" />
        <CodexFlowBrand showText={false} />
      </div>,
    );

    const svgs = screen.getAllByTestId('codexflow-brand-svg');
    expect(svgs).toHaveLength(2);
    expect(screen.getByText('codexflow')).toBeInTheDocument();
    expect(screen.getByText('Desktop')).toBeInTheDocument();

    const gradIds = svgs.flatMap((svg) =>
      Array.from(svg.querySelectorAll('linearGradient')).map((g) => g.getAttribute('id')),
    );
    expect(gradIds).toHaveLength(4);
    expect(new Set(gradIds).size).toBe(4);
  });

  it('supports dragging local files onto Composer, classifying workspace files, removing items, and sending attachments without text', async () => {
    const getPathForFile = vi.fn((file: File) => `D:/downloads/${file.name}`);
    const classifyDroppedFiles = vi.fn().mockResolvedValue([
      {
        filePath: 'D:/downloads/inside.ts',
        exists: true,
        isDirectory: false,
        sizeBytes: 12,
        workspaceRelativePath: 'src/inside.ts',
      },
      {
        filePath: 'D:/downloads/report.csv',
        exists: true,
        isDirectory: false,
        sizeBytes: 24,
        workspaceRelativePath: null,
      },
    ]);
    installMockBridge({ getPathForFile, classifyDroppedFiles });

    const onSend = vi.fn().mockResolvedValue(undefined);
    render(
      <Composer
        disabled={false}
        isTurnRunning={false}
        turnNotice={null}
        workspacePath="D:/repo"
        onSend={onSend}
        onCancel={vi.fn()}
      />,
    );

    const dropzone = screen.getByTestId('composer-dropzone');
    const insideFile = new File(['console.log(1)'], 'inside.ts', { type: 'text/typescript' });
    const outsideFile = new File(['col1,col2\n1,2'], 'report.csv', { type: 'text/csv' });

    await act(async () => {
      fireEvent.drop(dropzone, {
        dataTransfer: {
          types: ['Files'],
          files: [insideFile, outsideFile],
        },
      });
    });

    await waitFor(() => expect(screen.getByTestId('composer-attachments')).toBeInTheDocument());
    expect(screen.getByText('inside.ts')).toBeInTheDocument();
    expect(screen.getByText('report.csv')).toBeInTheDocument();
    expect(screen.getByText('工作区')).toBeInTheDocument();

    // Remove the workspace reference, keep report.csv, and send with empty text
    fireEvent.click(screen.getByLabelText('移除附件 inside.ts'));
    expect(screen.queryByText('inside.ts')).not.toBeInTheDocument();

    await act(async () => {
      fireEvent.click(screen.getByText('发送'));
    });

    expect(onSend).toHaveBeenCalledWith(
      '',
      [
        expect.objectContaining({
          name: 'report.csv',
          sizeBytes: outsideFile.size,
          workspaceRelativePath: null,
        }),
      ],
    );
    expect(screen.queryByTestId('composer-attachments')).not.toBeInTheDocument();
  });

  it('rejects empty files and directories in Composer dropzone', async () => {
    const getPathForFile = vi.fn((file: File) => `D:/downloads/${file.name}`);
    const classifyDroppedFiles = vi.fn().mockResolvedValue([
      {
        filePath: 'D:/downloads/folder',
        exists: true,
        isDirectory: true,
        sizeBytes: 0,
        workspaceRelativePath: null,
      },
    ]);
    installMockBridge({ getPathForFile, classifyDroppedFiles });

    render(
      <Composer
        disabled={false}
        isTurnRunning={false}
        turnNotice={null}
        onSend={vi.fn()}
        onCancel={vi.fn()}
      />,
    );

    const dropzone = screen.getByTestId('composer-dropzone');
    const emptyFile = new File([], 'empty.txt', { type: 'text/plain' });
    const dirDummy = new File(['x'], 'folder', { type: '' });

    await act(async () => {
      fireEvent.drop(dropzone, {
        dataTransfer: {
          types: ['Files'],
          files: [dirDummy, emptyFile],
        },
      });
    });

    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument());
    expect(screen.getByRole('alert')).toHaveTextContent('文件夹');
    expect(screen.getByRole('alert')).toHaveTextContent('空文件');
    expect(screen.queryByTestId('composer-attachments')).not.toBeInTheDocument();
  });

  it('parses and renders user attachment chips in MessageItem without showing raw attachment metadata tags', () => {
    const raw = `${formatOptimisticMessageWithAttachments('请核对这份表', [
      {
        name: 'data.csv',
        relativePath: '.codexflow-attachments/20261117-abc123-data.csv',
        sizeBytes: 2048,
        contentType: 'text/csv',
        isWorkspaceReference: false,
      },
    ])}\n[用户附带文件]\n- data.csv (.codexflow-attachments/20261117-abc123-data.csv, 2.0 KB)`;

    const parsed = parseMessageWithAttachments(raw);
    expect(parsed.text).toBe('请核对这份表');
    expect(parsed.attachments).toHaveLength(1);
    expect(parsed.attachments[0]?.name).toBe('data.csv');

    render(
      <MessageItem
        message={{
          id: 'm-1',
          role: 'user',
          content: raw,
        }}
      />,
    );

    expect(screen.getByText('请核对这份表')).toBeInTheDocument();
    expect(screen.getByTestId('message-attachments')).toHaveTextContent('data.csv');
    expect(screen.getByTestId('message-attachments')).toHaveTextContent('2.0 KB');
    expect(screen.queryByText(/codexflow_attachments/)).not.toBeInTheDocument();
  });

  it('renders 新建会话 button in Sidebar even when no conversations exist and disables it while turn is running', async () => {
    const onCreateConversation = vi.fn().mockResolvedValue(undefined);
    const sessions = [
      {
        sessionId: 'sess-1',
        name: '默认工作区',
        workspacePath: 'D:/repo',
        currentStage: 1,
        isArchived: false,
        isExternalWorkspace: false,
        createdUtc: '2026-11-17T00:00:00Z',
        currentPlanArtifactId: null,
        activeTaskId: null,
        workMode: 'Development',
        modeVersion: 1,
        planModeActive: false,
        planModeVersion: 1,
      },
    ];

    const { rerender } = render(
      <Sidebar
        sessions={sessions}
        conversations={[]}
        activeSessionId="sess-1"
        activeConversationId={null}
        controlPlaneAvailable
        isTurnRunning={false}
        onSelectSession={vi.fn()}
        onSelectConversation={vi.fn()}
        onCreateConversation={onCreateConversation}
        onCreateSession={vi.fn().mockResolvedValue(undefined)}
        onOpenFolder={vi.fn().mockResolvedValue(undefined)}
        onRenameSession={vi.fn().mockResolvedValue(undefined)}
        onArchiveSession={vi.fn().mockResolvedValue(undefined)}
        onDeleteSession={vi.fn().mockResolvedValue(undefined)}
        onRenameConversation={vi.fn().mockResolvedValue(undefined)}
        onDeleteConversation={vi.fn().mockResolvedValue(undefined)}
        onOpenSettings={vi.fn()}
        workspaceError={null}
      />,
    );

    const newConvBtn = screen.getByRole('button', { name: '新建会话' });
    expect(newConvBtn).toBeEnabled();
    expect(screen.getByText(/暂无会话/)).toBeInTheDocument();

    await act(async () => {
      fireEvent.click(newConvBtn);
    });
    expect(onCreateConversation).toHaveBeenCalledTimes(1);

    rerender(
      <Sidebar
        sessions={sessions}
        conversations={[
          {
            conversationId: 'conv-1',
            sessionId: 'sess-1',
            name: '新会话',
            threadType: 'Chat',
            isArchived: false,
            taskId: null,
            createdUtc: '2026-11-17T00:00:00Z',
            updatedAtUtc: '2026-11-17T00:00:00Z',
          },
        ]}
        activeSessionId="sess-1"
        activeConversationId="conv-1"
        controlPlaneAvailable
        isTurnRunning
        onSelectSession={vi.fn()}
        onSelectConversation={vi.fn()}
        onCreateConversation={onCreateConversation}
        onCreateSession={vi.fn().mockResolvedValue(undefined)}
        onOpenFolder={vi.fn().mockResolvedValue(undefined)}
        onRenameSession={vi.fn().mockResolvedValue(undefined)}
        onArchiveSession={vi.fn().mockResolvedValue(undefined)}
        onDeleteSession={vi.fn().mockResolvedValue(undefined)}
        onRenameConversation={vi.fn().mockResolvedValue(undefined)}
        onDeleteConversation={vi.fn().mockResolvedValue(undefined)}
        onOpenSettings={vi.fn()}
        workspaceError={null}
      />,
    );

    expect(screen.getByRole('button', { name: '新建会话' })).toBeDisabled();
    expect(screen.getByText('新会话')).toBeInTheDocument();
  });

  it('renders workspace-conversation tree with collapse toggle and cross-workspace selection', async () => {
    const onSelectConv = vi.fn();
    const sessions = [
      {
        sessionId: 'sess-a',
        name: '默认工作区',
        workspacePath: 'D:/repo-a',
        currentStage: 1,
        isArchived: false,
        isExternalWorkspace: false,
        createdUtc: '2026-11-17T00:00:00Z',
        currentPlanArtifactId: null,
        activeTaskId: null,
        workMode: 'Development',
        modeVersion: 1,
        planModeActive: false,
        planModeVersion: 1,
      },
      {
        sessionId: 'sess-b',
        name: '项目 B',
        workspacePath: 'D:/repo-b',
        currentStage: 1,
        isArchived: false,
        isExternalWorkspace: false,
        createdUtc: '2026-11-17T00:00:00Z',
        currentPlanArtifactId: null,
        activeTaskId: null,
        workMode: 'Development',
        modeVersion: 1,
        planModeActive: false,
        planModeVersion: 1,
      },
    ];

    render(
      <Sidebar
        sessions={sessions}
        conversations={[]}
        conversationsBySession={{
          'sess-a': [
            {
              conversationId: 'conv-a1',
              sessionId: 'sess-a',
              name: '线程 A1',
              threadType: 'Chat',
              isArchived: false,
              taskId: null,
              createdUtc: '2026-11-17T00:00:00Z',
              updatedAtUtc: '2026-11-17T00:00:00Z',
            },
          ],
          'sess-b': [
            {
              conversationId: 'conv-b1',
              sessionId: 'sess-b',
              name: '线程 B1',
              threadType: 'Chat',
              isArchived: false,
              taskId: null,
              createdUtc: '2026-11-17T00:00:00Z',
              updatedAtUtc: '2026-11-17T00:00:00Z',
            },
          ],
        }}
        activeSessionId="sess-a"
        activeConversationId="conv-a1"
        controlPlaneAvailable
        onSelectSession={vi.fn()}
        onSelectConversation={onSelectConv}
        onCreateConversation={vi.fn().mockResolvedValue(undefined)}
        onCreateSession={vi.fn().mockResolvedValue(undefined)}
        onOpenFolder={vi.fn().mockResolvedValue(undefined)}
        onRenameSession={vi.fn().mockResolvedValue(undefined)}
        onArchiveSession={vi.fn().mockResolvedValue(undefined)}
        onDeleteSession={vi.fn().mockResolvedValue(undefined)}
        onRenameConversation={vi.fn().mockResolvedValue(undefined)}
        onDeleteConversation={vi.fn().mockResolvedValue(undefined)}
        onOpenSettings={vi.fn()}
        workspaceError={null}
      />,
    );

    expect(screen.getByText('线程 A1')).toBeInTheDocument();
    expect(screen.queryByText('线程 B1')).not.toBeInTheDocument();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '展开 项目 B' }));
    });
    expect(screen.getByText('线程 B1')).toBeInTheDocument();

    await act(async () => {
      fireEvent.click(screen.getByText('线程 B1'));
    });
    expect(onSelectConv).toHaveBeenCalledWith('conv-b1', 'sess-b');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '折叠 默认工作区' }));
    });
    expect(screen.queryByText('线程 A1')).not.toBeInTheDocument();
    expect(screen.getByText('线程 B1')).toBeInTheDocument();
  });

  it('parses unified diff and filters WorkspaceChangesPanel by selected file', () => {
    const sampleDiff = [
      'diff --git a/src/a.ts b/src/a.ts',
      'index 1111111..2222222 100644',
      '--- a/src/a.ts',
      '+++ b/src/a.ts',
      '@@ -1,2 +1,2 @@',
      '-const oldVal = 1;',
      '+const newVal = 2;',
      ' export { newVal };',
      'diff --git a/README.md b/README.md',
      '--- a/README.md',
      '+++ b/README.md',
      '@@ -1,1 +1,2 @@',
      ' # Docs',
      '+Added line',
    ].join('\n');

    const parsed = parseUnifiedDiff(sampleDiff);
    expect(parsed).toHaveLength(2);
    expect(parsed[0]?.filePath).toBe('src/a.ts');
    expect(parsed[0]?.lines.find((l) => l.kind === 'add')?.newLineNumber).toBe(1);

    render(
      <WorkspaceChangesPanel
        sessionId="sess-1"
        changes={{
          sessionId: 'sess-1',
          hasGitRepository: true,
          baseRef: 'HEAD',
          files: [
            { filePath: 'src/a.ts', status: 'Modified', additions: 1, deletions: 1 },
            { filePath: 'README.md', status: 'Modified', additions: 1, deletions: 0 },
          ],
          unifiedDiff: sampleDiff,
          truncated: false,
        }}
        isLoading={false}
        isFetching={false}
        error={null}
        onRefresh={vi.fn()}
      />,
    );

    expect(screen.getByText('基线: HEAD')).toBeInTheDocument();
    expect(screen.getByText('+const newVal = 2;')).toBeInTheDocument();
    expect(screen.getByText('+Added line')).toBeInTheDocument();

    fireEvent.click(screen.getAllByText('README.md')[0]!);
    expect(screen.queryByText('+const newVal = 2;')).not.toBeInTheDocument();
    expect(screen.getByText('+Added line')).toBeInTheDocument();
  });

  it('browses directories, previews file content, and attaches workspace file in WorkspaceFilesPanel', async () => {
    const listSpy = vi
      .spyOn(apiClient, 'listFiles')
      .mockResolvedValueOnce({
        sessionId: 'sess-1',
        path: '',
        entries: [
          { name: 'src', path: 'src', isDirectory: true, sizeBytes: 0, lastModifiedUtc: '2026-11-17T00:00:00Z' },
        ],
        truncated: false,
      })
      .mockResolvedValueOnce({
        sessionId: 'sess-1',
        path: 'src',
        entries: [
          { name: 'app.ts', path: 'src/app.ts', isDirectory: false, sizeBytes: 42, lastModifiedUtc: '2026-11-17T00:00:00Z' },
        ],
        truncated: false,
      });
    const contentSpy = vi.spyOn(apiClient, 'readWorkspaceFileContent').mockResolvedValueOnce({
      sessionId: 'sess-1',
      path: 'src/app.ts',
      content: 'export const ok = true;',
      sizeBytes: 42,
      truncated: false,
      isBinary: false,
      encoding: 'utf-8',
    });

    const onAttach = vi.fn();
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    try {
      render(
        <QueryClientProvider client={qc}>
          <WorkspaceFilesPanel
            auth={{ baseUrl: 'http://127.0.0.1:59999', accessToken: 'tok' }}
            sessionId="sess-1"
            workspaceRevision={0}
            onAttachWorkspaceFile={onAttach}
          />
        </QueryClientProvider>,
      );

      await waitFor(() => expect(screen.getByText('src')).toBeInTheDocument());
      fireEvent.click(screen.getByText('src'));

      await waitFor(() => expect(screen.getByText('app.ts')).toBeInTheDocument());
      fireEvent.click(screen.getByText('app.ts'));

      await waitFor(() => expect(screen.getByTestId('workspace-file-preview')).toBeInTheDocument());
      expect(screen.getByText('export const ok = true;')).toBeInTheDocument();

      fireEvent.click(screen.getByText('引用'));
      expect(onAttach).toHaveBeenCalledWith({
        name: 'app.ts',
        path: 'src/app.ts',
        sizeBytes: 42,
      });
    } finally {
      listSpy.mockRestore();
      contentSpy.mockRestore();
    }
  });

  it('dispatches cancel, resume, retry, and continue actions in JobsPanel', async () => {
    const cancelSpy = vi.spyOn(apiClient, 'cancelJob').mockResolvedValue({ success: true });
    const resumeSpy = vi.spyOn(apiClient, 'resumeJob').mockResolvedValue({ success: true });
    const retrySpy = vi.spyOn(apiClient, 'retryJob').mockResolvedValue({ success: true });
    const continueSpy = vi.spyOn(apiClient, 'continueJob').mockResolvedValue({ success: true, newJobId: 'job-new' });
    const onMutated = vi.fn().mockResolvedValue(undefined);
    const auth = { baseUrl: 'http://127.0.0.1:59999', accessToken: 'tok' };

    try {
      render(
        <JobsPanel
          auth={auth}
          sessionId="sess-1"
          executionBackend="Local"
          verificationReceipts={{}}
          onVerifyJob={vi.fn().mockResolvedValue(undefined)}
          onJobsMutated={onMutated}
          jobs={[
            {
              jobId: 'job-wait',
              taskId: 'T-wait',
              jobType: 'ForgeWorker',
              status: 'WaitingUser',
              waitingUser: true,
              resumeToken: 'rt-1',
              waitingReason: '等待人工恢复',
            },
            {
              jobId: 'job-fail',
              taskId: 'T-fail',
              jobType: 'ForgeWorker',
              status: 'Failed',
              resumeToken: 'rt-2',
              failure: { message: '单测未通过', retryable: true },
            },
          ]}
        />,
      );

      await act(async () => {
        fireEvent.click(screen.getByText('取消作业'));
      });
      expect(cancelSpy).toHaveBeenCalledWith(auth, 'job-wait');

      await act(async () => {
        fireEvent.click(screen.getByText('恢复执行'));
      });
      expect(resumeSpy).toHaveBeenCalledWith(auth, 'job-wait', 'rt-1');

      await act(async () => {
        fireEvent.click(screen.getByText('重试'));
      });
      expect(retrySpy).toHaveBeenCalledWith(auth, 'job-fail');

      fireEvent.click(screen.getByText('继续执行'));
      const promptBox = screen.getByPlaceholderText(/可选：输入补充要求/);
      fireEvent.change(promptBox, { target: { value: '请修复失败用例' } });
      await act(async () => {
        fireEvent.click(screen.getByText('确认继续'));
      });
      expect(continueSpy).toHaveBeenCalledWith(auth, 'job-fail', '请修复失败用例', 'rt-2');
      expect(onMutated).toHaveBeenCalledTimes(4);
    } finally {
      cancelSpy.mockRestore();
      resumeSpy.mockRestore();
      retrySpy.mockRestore();
      continueSpy.mockRestore();
    }
  });

  it('auto-updates multimodal checkbox when SettingsModal save probe returns supportsMultimodal=true', async () => {
    const updateModel = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      data: {
        apiUrl: 'http://10.0.30.247:8001/v1',
        apiMode: 'ChatCompletions',
        model: 'qwen-vl',
        maxTokensLength: 128000,
        temperature: 0.2,
        topP: 0.9,
        apiKeySet: true,
        apiKeyLast4: '1234',
        supportsMultimodal: true,
        multimodalProbeStatus: 'Supported',
        multimodalProbeMessage: '内置测试图片识别成功，已自动启用多模态能力。',
      },
    });
    installMockBridge({ updateModelSettings: updateModel });

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
        modelSettings={{
          apiUrl: 'http://10.0.30.247:8001/v1',
          apiMode: 'ChatCompletions',
          model: 'qwen-vl',
          maxTokensLength: 128000,
          temperature: 0.2,
          topP: 0.9,
          apiKeySet: true,
          apiKeyLast4: '1234',
          supportsMultimodal: false,
        }}
        controlPlaneAvailable
        onClose={vi.fn()}
        onUpdated={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByText('模型设置'));
    const checkbox = screen.getByRole('checkbox') as HTMLInputElement;
    expect(checkbox.checked).toBe(false);

    await act(async () => {
      fireEvent.click(screen.getByText('保存模型设置'));
    });

    expect(checkbox.checked).toBe(true);
    expect(screen.getByText(/内置测试图片识别成功，已自动启用多模态能力/)).toBeInTheDocument();
  });

  it('blocks image drops with clear notice when supportsMultimodal is false, and allows office documents', async () => {
    installMockBridge();
    const onSend = vi.fn().mockResolvedValue(undefined);

    render(
      <Composer
        disabled={false}
        isTurnRunning={false}
        turnNotice={null}
        workspacePath="D:/repo"
        supportsMultimodal={false}
        onSend={onSend}
        onCancel={vi.fn()}
      />,
    );

    const dropzone = screen.getByTestId('composer-dropzone');
    const imgFile = new File(['pngbytes'], 'photo.png', { type: 'image/png' });
    const docxFile = new File(['docxbytes'], 'report.docx', {
      type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    });

    await act(async () => {
      fireEvent.drop(dropzone, {
        dataTransfer: {
          types: ['Files'],
          files: [imgFile, docxFile],
        },
      });
    });

    expect(screen.getByText(/当前模型未启用多模态能力，无法读取图片“photo\.png”/)).toBeInTheDocument();
    expect(screen.queryByText('photo.png')).not.toBeInTheDocument();
    expect(screen.getByText('report.docx')).toBeInTheDocument();
  });

  it('displays office document preview label in WorkspaceFilesPanel when encoding is document-markdown', async () => {
    const listSpy = vi.spyOn(apiClient, 'listFiles').mockResolvedValue({
      sessionId: 'sess-1',
      path: '',
      entries: [
        { name: 'spec.docx', path: 'spec.docx', isDirectory: false, sizeBytes: 4096, lastModifiedUtc: '2026-01-01T00:00:00Z' },
      ],
      truncated: false,
    });
    const readSpy = vi.spyOn(apiClient, 'readWorkspaceFileContent').mockResolvedValue({
      sessionId: 'sess-1',
      path: 'spec.docx',
      content: '# Spec Heading',
      sizeBytes: 4096,
      truncated: false,
      isBinary: false,
      encoding: 'document-markdown',
    });
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    try {
      render(
        <QueryClientProvider client={qc}>
          <WorkspaceFilesPanel
            auth={{ baseUrl: 'http://127.0.0.1:5000', accessToken: 'tok' }}
            sessionId="sess-1"
            workspaceRevision={0}
          />
        </QueryClientProvider>,
      );

      await waitFor(() => expect(screen.getByText('spec.docx')).toBeInTheDocument());
      await act(async () => {
        fireEvent.click(screen.getByText('spec.docx'));
      });
      await waitFor(() => expect(screen.getByTestId('workspace-file-preview')).toBeInTheDocument());
      expect(screen.getByText(/办公文档文本预览/)).toBeInTheDocument();
      expect(screen.getByText('# Spec Heading')).toBeInTheDocument();
    } finally {
      listSpy.mockRestore();
      readSpy.mockRestore();
    }
  });
});
