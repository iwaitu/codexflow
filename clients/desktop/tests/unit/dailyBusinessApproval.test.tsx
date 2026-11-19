import React from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type {
  BackendConnectionPublicInfo,
  BusinessApprovalRequestDto,
  DesktopBridge,
  SkillRunDto,
} from '../../src/shared/ipc';
import { apiClient, type SessionSnapshot } from '../../src/renderer/lib/apiClient';
import { useAppStore } from '../../src/renderer/stores/useAppStore';
import { BusinessSkillApprovalCard } from '../../src/renderer/components/BusinessSkillApprovalCard';

function installMockBridge(overrides: Partial<DesktopBridge> = {}): DesktopBridge {
  const bridge = {
    approveToolExecution: vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      data: { success: true, approvalId: 'bappr-1', decision: 'allow_once', message: 'ok' },
    }),
    showNotification: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  } as unknown as DesktopBridge;
  window.codexflow = bridge;
  return bridge;
}

const CONNECTION: BackendConnectionPublicInfo = {
  mode: 'managed',
  status: 'ready',
  baseUrl: 'http://127.0.0.1:59999',
  targetBaseUrl: 'http://127.0.0.1:5206',
  accessToken: 'access-token-daily',
  instanceId: 'inst-daily-1',
  controlPlaneAvailable: true,
  selfApprovalProtection: true,
  dataDirectory: 'D:/data',
  lastError: null,
  restartCount: 0,
};

const FULL_APPROVAL: BusinessApprovalRequestDto = {
  approvalId: 'bappr-1',
  requestDigest: 'digest-biz-1',
  sessionId: 'sess-daily-1',
  runId: 'srun-1',
  stepId: 'step_write',
  kind: 'BusinessWrite',
  allowedDecisions: ['allow_once', 'deny'],
  createdAtUtc: '2026-11-20T00:00:00Z',
  expiresAtUtc: '2026-11-20T00:15:00Z',
  summary: {
    systemName: 'ERP 系统',
    environment: 'prod',
    plannedMutation: 'POST /api/collection-tasks',
    targetRecordSummary: '客户 C-1 发票 INV-01',
    batchCount: 1,
    keyAmount: 70000,
    currency: 'CNY',
    writeStepUnverified: true,
    requestPreviews: [
      {
        capabilityId: 'plg-1:POST:/api/collection-tasks',
        methodOrTool: 'POST',
        targetPath: '/api/collection-tasks',
        redactedBodyPreview: '{"customerId":"C-1","amount":70000}',
      },
    ],
  },
};

function makeDailySnapshot(): SessionSnapshot {
  return {
    sessionId: 'sess-daily-1',
    asOfEventId: 10,
    channelEpoch: 'epoch-daily-1',
    activeConversationId: 'conv-daily-1',
    activeTurn: {
      isRunning: true,
      messageId: 'msg-1',
      conversationId: 'conv-daily-1',
      partialContent: null,
      partialThinking: null,
      contentTruncated: false,
    },
    session: {
      sessionId: 'sess-daily-1',
      name: '日常助手',
      workspacePath: 'D:/daily',
      currentStage: 1,
      isArchived: false,
      isExternalWorkspace: false,
      createdUtc: '2026-11-20T00:00:00Z',
      currentPlanArtifactId: null,
      activeTaskId: null,
      workMode: 'Daily',
      modeVersion: 1,
      planModeActive: false,
      planModeVersion: 1,
    },
    messages: [],
    pendingApprovals: [],
    currentPlan: null,
    taskListJson: null,
    activeToolResults: [],
    activeToolOutputs: [],
    toolOutputsTruncated: false,
    toolResultsTruncated: false,
    controlPlaneAvailable: true,
    selfApprovalProtection: true,
    jobs: [],
  };
}

describe('Daily mode business skill approvals', () => {
  beforeEach(() => {
    installMockBridge();
    useAppStore.getState().setConnection(CONNECTION);
    useAppStore.getState().setActiveSession('sess-daily-1');
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('hydrates business approval via getSkillRun on SSE and clears on resolved or terminal run status', async () => {
    const getRunSpy = vi.spyOn(apiClient, 'getSkillRun').mockResolvedValue({
      id: 'srun-1',
      skillId: 'skill-1',
      skillVersion: 1,
      status: 'WaitingForApproval',
      isTestRun: false,
      isDryRun: false,
      externalRequestCount: 1,
      readRetryCount: 0,
      stopRequested: false,
      createdAtUtc: '2026-11-20T00:00:00Z',
      pendingApproval: FULL_APPROVAL,
    } as SkillRunDto);

    act(() => {
      useAppStore.getState().applySseEvent({
        id: 1,
        event: 'business_skill_approval_requested',
        data: {
          approvalId: 'bappr-1',
          requestDigest: 'digest-biz-1',
          sessionId: 'sess-daily-1',
          runId: 'srun-1',
          stepId: 'step_write',
          kind: 'BusinessWrite',
          allowedDecisions: ['allow_once', 'deny'],
          expiresAtUtc: '2026-11-20T00:15:00Z',
        },
      });
    });

    // Placeholder immediately present before hydration finishes
    expect(useAppStore.getState().pendingBusinessApprovals).toHaveLength(1);
    expect(useAppStore.getState().pendingBusinessApprovals[0]?.approvalId).toBe('bappr-1');

    await waitFor(() => {
      expect(getRunSpy).toHaveBeenCalledWith(
        { baseUrl: CONNECTION.baseUrl!, accessToken: CONNECTION.accessToken! },
        'srun-1',
      );
      expect(useAppStore.getState().pendingBusinessApprovals[0]?.summary?.systemName).toBe('ERP 系统');
    });

    // Resolved event removes card
    act(() => {
      useAppStore.getState().applySseEvent({
        id: 2,
        event: 'business_skill_approval_resolved',
        data: {
          approvalId: 'bappr-1',
          requestDigest: 'digest-biz-1',
          runId: 'srun-1',
          decision: 'allow_once',
          resolvedAt: '2026-11-20T00:01:00Z',
        },
      });
    });
    expect(useAppStore.getState().pendingBusinessApprovals).toHaveLength(0);

    // Re-add and verify terminal run update clears matching runId
    act(() => {
      useAppStore.setState({ pendingBusinessApprovals: [FULL_APPROVAL] });
      useAppStore.getState().applySseEvent({
        id: 3,
        event: 'business_skill_run_updated',
        data: {
          runId: 'srun-1',
          skillId: 'skill-1',
          skillVersion: 1,
          status: 'Cancelled',
          stopRequested: true,
          updatedAtUtc: '2026-11-20T00:02:00Z',
        },
      });
    });
    expect(useAppStore.getState().pendingBusinessApprovals).toHaveLength(0);
  });

  it('keeps pendingBusinessApprovals isolated from heartbeat reconcileApprovals when businessApprovals is omitted', async () => {
    act(() => {
      useAppStore.setState({ pendingBusinessApprovals: [FULL_APPROVAL] });
    });

    // Simulate heartbeat mismatch reconcile where response omits businessApprovals
    vi.spyOn(apiClient, 'getPendingApprovals').mockResolvedValueOnce({
      sessionId: 'sess-daily-1',
      count: 0,
      items: [],
      controlPlaneAvailable: true,
      selfApprovalProtection: true,
    });

    await act(async () => {
      await useAppStore.getState().reconcileApprovals();
    });

    // Must NOT wipe active business approval card
    expect(useAppStore.getState().pendingBusinessApprovals).toHaveLength(1);
    expect(useAppStore.getState().pendingBusinessApprovals[0]?.approvalId).toBe('bappr-1');

    // When backend explicitly returns authoritative businessApprovals array, it syncs cleanly
    vi.spyOn(apiClient, 'getPendingApprovals').mockResolvedValueOnce({
      sessionId: 'sess-daily-1',
      count: 0,
      items: [],
      controlPlaneAvailable: true,
      selfApprovalProtection: true,
      businessApprovals: [],
    });

    await act(async () => {
      await useAppStore.getState().reconcileApprovals();
    });

    expect(useAppStore.getState().pendingBusinessApprovals).toHaveLength(0);
  });

  it('restores pendingBusinessApprovals during reconcileSession after reconnect', async () => {
    vi.spyOn(apiClient, 'getSnapshot').mockResolvedValueOnce(makeDailySnapshot());
    vi.spyOn(apiClient, 'getContextWindow').mockRejectedValueOnce(new Error('skip'));
    vi.spyOn(apiClient, 'getPendingApprovals').mockResolvedValueOnce({
      sessionId: 'sess-daily-1',
      count: 0,
      items: [],
      controlPlaneAvailable: true,
      selfApprovalProtection: true,
      businessApprovals: [FULL_APPROVAL],
    });

    await act(async () => {
      await useAppStore.getState().reconcileSession('reconnect');
    });

    expect(useAppStore.getState().pendingBusinessApprovals).toHaveLength(1);
    expect(useAppStore.getState().pendingBusinessApprovals[0]?.approvalId).toBe('bappr-1');
  });

  it('BusinessSkillApprovalCard allows only allow_once/deny and enforces readonly control-plane mode', async () => {
    const bridge = installMockBridge();
    const onResolved = vi.fn();

    const { rerender } = render(
      <BusinessSkillApprovalCard
        approval={FULL_APPROVAL}
        controlPlaneAvailable={true}
        onResolved={onResolved}
      />,
    );

    expect(screen.getByText('ERP 系统')).toBeTruthy();
    expect(screen.getByText(/尚未经过真实试运行验证/)).toBeTruthy();
    expect(screen.queryByText(/本会话允许/)).toBeNull();
    expect(screen.queryByText(/始终允许/)).toBeNull();

    const allowBtn = screen.getByRole('button', { name: /仅本次批次允许/ });
    const denyBtn = screen.getByRole('button', { name: /拒绝执行/ });
    expect((allowBtn as HTMLButtonElement).disabled).toBe(false);
    expect((denyBtn as HTMLButtonElement).disabled).toBe(false);

    fireEvent.click(allowBtn);

    await waitFor(() => {
      expect(bridge.approveToolExecution).toHaveBeenCalledWith({
        approvalId: 'bappr-1',
        requestDigest: 'digest-biz-1',
        decision: 'allow_once',
        note: undefined,
      });
      expect(onResolved).toHaveBeenCalledWith('bappr-1');
    });

    // Readonly mode disables both actions and shows warning
    rerender(
      <BusinessSkillApprovalCard
        approval={FULL_APPROVAL}
        controlPlaneAvailable={false}
        onResolved={onResolved}
      />,
    );

    expect(screen.getByText(/只读模式/)).toBeTruthy();
    expect((screen.getByRole('button', { name: /仅本次批次允许/ }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: /拒绝执行/ }) as HTMLButtonElement).disabled).toBe(true);
  });
});
