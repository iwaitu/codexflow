import { create } from 'zustand';
import type {
  BackendConnectionPublicInfo,
  BrowserApprovalRequestDto,
  BrowserSessionStateDto,
  BusinessApprovalRequestDto,
  ComputerApprovalRequestDto,
  ComputerSessionStateDto,
} from '@shared/ipc';
import type { ParsedSseEvent } from '@shared/contracts/sse.generated';
import {
  apiClient,
  type BackgroundJob,
  type PlanModeExitRequestState,
  type SessionContextWindow,
  type SessionPlan,
  type SessionSnapshot,
  type SessionWorkMode,
  type VerificationReceipt,
} from '../lib/apiClient';
import {
  buildRecoveredSessionState,
  normalizeApprovalFromRest,
  normalizeApprovalFromSse,
  type UiMessage,
  type UiPendingApproval,
  type UiToolCall,
  type WorkerBarState,
} from '../lib/sessionRecovery';
import { translate, type I18nKey } from '../lib/i18n';
import { usePreferencesStore } from './usePreferencesStore';

function dispatchDesktopNotification(titleKey: I18nKey, body: string): void {
  const prefs = usePreferencesStore.getState();
  if (!prefs.notificationsEnabled || typeof window === 'undefined') {
    return;
  }
  void window.codexflow?.showNotification?.({
    title: translate(prefs.language, titleKey),
    body,
    onlyWhenUnfocused: prefs.notifyOnlyWhenUnfocused,
  });
}

export type ConversationMode = 'continue-current' | 'reuse-master' | 'new-thread';
export type InspectorTab = 'files' | 'changes' | 'jobs' | 'browser' | 'computer' | 'terminal';

type JobActivity = (
  | { kind: 'calling' | 'returned' | 'failed'; toolName: string }
  | { kind: 'error' }
) & { observedAt: number };

export interface ConversationTitleUpdate {
  conversationId: string;
  title: string;
  sessionId: string | null;
  seq: number;
}

export interface BufferedSseItem {
  event: ParsedSseEvent;
  connectionGeneration: number;
  streamEpoch: string | null;
}

export interface ReconcileOptions {
  expectedEpoch?: string | null;
  onCursorSynced?: (lastEventId: number, epoch: string | null) => void;
}

let activeSnapshotAbort: AbortController | null = null;
let activeApprovalsAbort: AbortController | null = null;
let activeContextWindowAbort: AbortController | null = null;
let jobRefreshGeneration = 0;

function computeInstanceKey(connection: BackendConnectionPublicInfo): string | null {
  if (!connection.instanceId && !connection.targetBaseUrl && !connection.baseUrl) {
    return null;
  }
  return `${connection.mode}:${connection.instanceId ?? ''}:${connection.targetBaseUrl ?? connection.baseUrl ?? ''}`;
}

export interface AppState {
  connection: BackendConnectionPublicInfo | null;
  instanceKey: string | null;
  connectionGeneration: number;
  sessionGeneration: number;
  reconcileGeneration: number;
  approvalsGeneration: number;
  isReconcilingSnapshot: boolean;
  bufferedSseEvents: BufferedSseItem[];

  sseState: 'connecting' | 'connected' | 'reconnecting' | 'closed';
  activeSessionId: string | null;
  activeConversationId: string | null;
  conversationMode: ConversationMode;
  activeWorkMode: SessionWorkMode;
  activeModeVersion: number;
  planModeActive: boolean;
  planModeVersion: number;
  planModeExitRequest: PlanModeExitRequestState | null;
  latestTitleUpdate: ConversationTitleUpdate | null;

  messages: UiMessage[];
  messagesTruncated: boolean;
  streamingContent: string;
  streamingThinking: string;
  isThinkingStreaming: boolean;
  streamingTruncated: boolean;
  isTurnRunning: boolean;
  turnNotice: string | null;

  toolCalls: UiToolCall[];
  pendingApprovals: UiPendingApproval[];
  pendingBusinessApprovals: BusinessApprovalRequestDto[];
  pendingBrowserApprovals: BrowserApprovalRequestDto[];
  pendingComputerApprovals: ComputerApprovalRequestDto[];
  browserState: BrowserSessionStateDto | null;
  computerState: ComputerSessionStateDto | null;
  currentPlan: SessionPlan | null;
  jobs: BackgroundJob[];
  jobActivity: Record<string, JobActivity>;
  verificationReceipts: Record<string, VerificationReceipt>;
  workerBar: WorkerBarState;
  contextWindow: SessionContextWindow | null;

  asOfEventId: number;
  channelEpoch: string | null;
  isSettingsOpen: boolean;
  isWizardOpen: boolean;
  wizardDismissed: boolean;
  isInspectorOpen: boolean;
  inspectorTab: InspectorTab;
  workspaceRevision: number;

  setConnection: (connection: BackendConnectionPublicInfo) => void;
  setSseState: (state: 'connecting' | 'connected' | 'reconnecting' | 'closed') => void;
  setActiveSession: (sessionId: string | null) => void;
  setActiveConversation: (conversationId: string | null) => void;
  startNewConversation: (conversationId: string) => void;
  setConversationMode: (mode: ConversationMode) => void;
  setTurnNotice: (notice: string | null) => void;
  setSettingsOpen: (open: boolean) => void;
  setWizardOpen: (open: boolean, dismissed?: boolean) => void;
  setInspectorOpen: (open: boolean) => void;
  setInspectorTab: (tab: InspectorTab) => void;
  openInspectorTab: (tab: InspectorTab) => void;
  bumpWorkspaceRevision: () => void;

  appendOptimisticUserMessage: (content: string) => void;
  applySseEvent: (event: ParsedSseEvent, meta?: { streamEpoch?: string | null }) => void;
  applySnapshot: (snapshot: SessionSnapshot) => void;
  reconcileSession: (reason: string, options?: ReconcileOptions) => Promise<void>;
  reconcileApprovals: () => Promise<void>;
  removeBusinessApproval: (approvalId: string) => void;
  removeBrowserApproval: (approvalId: string) => void;
  removeComputerApproval: (approvalId: string) => void;
  setBrowserState: (state: BrowserSessionStateDto | null) => void;
  setComputerState: (state: ComputerSessionStateDto | null) => void;
  setVerificationReceipt: (jobId: string, receipt: VerificationReceipt) => void;
  refreshContextWindow: () => Promise<void>;
  setJobs: (jobs: BackgroundJob[]) => void;
  setPlan: (plan: SessionPlan | null) => void;
  setSessionPolicyState: (policy: {
    workMode?: SessionWorkMode;
    modeVersion?: number;
    planModeActive?: boolean;
    planModeVersion?: number;
  }) => void;
  dismissPlanModeExitRequest: () => void;
  clearTurnVisuals: () => void;
}

function selectConversation(state: AppState, activeConversationId: string | null): Partial<AppState> {
  if (state.activeConversationId === activeConversationId) {
    return {};
  }
  activeContextWindowAbort?.abort();
  return { activeConversationId, contextWindow: null };
}

function refreshSessionJobs(get: () => AppState): void {
  const conn = get().connection;
  const sid = get().activeSessionId;
  const targetGen = get().sessionGeneration;
  const connectionGen = get().connectionGeneration;
  if (!conn?.baseUrl || !conn.accessToken || !sid) {
    return;
  }
  const refreshGen = ++jobRefreshGeneration;
  const auth = { baseUrl: conn.baseUrl, accessToken: conn.accessToken };
  void apiClient
    .listJobs(auth, sid)
    .then((jobs) => {
      const current = get();
      if (refreshGen === jobRefreshGeneration &&
          current.activeSessionId === sid && current.sessionGeneration === targetGen &&
          current.connectionGeneration === connectionGen) {
        current.setJobs(jobs);
      }
    })
    .catch(() => undefined);
}

function keepRunningJobActivity(jobs: BackgroundJob[], activity: AppState['jobActivity']): AppState['jobActivity'] {
  const retained: [string, JobActivity][] = [];
  for (const { jobId, status } of jobs) {
    if (jobId && status === 'Running' && Object.hasOwn(activity, jobId)) {
      const latest = activity[jobId];
      if (latest) retained.push([jobId, latest]);
    }
  }
  return Object.fromEntries(retained);
}

function refreshTurnMetadataAfterFinish(get: () => AppState, includeJobs: boolean): void {
  void get().refreshContextWindow();
  if (includeJobs) {
    refreshSessionJobs(get);
  }
}

function shouldAcceptBrowserState(
  current: BrowserSessionStateDto | null,
  incoming: BrowserSessionStateDto,
): boolean {
  if (!current) {
    return true;
  }
  if (current.sessionId && incoming.sessionId && current.sessionId !== incoming.sessionId) {
    return true;
  }
  const curTs = current.updatedAtUtc ? Date.parse(current.updatedAtUtc) : Number.NaN;
  const incTs = incoming.updatedAtUtc ? Date.parse(incoming.updatedAtUtc) : Number.NaN;
  if (!Number.isNaN(curTs) && !Number.isNaN(incTs)) {
    if (incTs < curTs) {
      return false;
    }
    if (incTs > curTs) {
      return true;
    }
  }
  if (current.status === 'running' && incoming.status === 'running') {
    const curSeq = Number(current.screenshotSeq ?? 0);
    const incSeq = Number(incoming.screenshotSeq ?? 0);
    if (incSeq < curSeq) {
      return false;
    }
  }
  return true;
}

function shouldAcceptComputerState(
  current: ComputerSessionStateDto | null,
  incoming: ComputerSessionStateDto,
): boolean {
  if (!current) {
    return true;
  }
  if (current.sessionId && incoming.sessionId && current.sessionId !== incoming.sessionId) {
    return true;
  }
  const curTs = current.updatedAtUtc ? Date.parse(current.updatedAtUtc) : Number.NaN;
  const incTs = incoming.updatedAtUtc ? Date.parse(incoming.updatedAtUtc) : Number.NaN;
  if (!Number.isNaN(curTs) && !Number.isNaN(incTs)) {
    if (incTs < curTs) {
      return false;
    }
    if (incTs > curTs) {
      return true;
    }
  }
  if (current.active && incoming.active) {
    const curSeq = Number(current.screenshotSeq ?? 0);
    const incSeq = Number(incoming.screenshotSeq ?? 0);
    if (incSeq < curSeq) {
      return false;
    }
  }
  return true;
}

function reduceSseEvent(
  evt: ParsedSseEvent,
  get: () => AppState,
  set: (partial: Partial<AppState> | ((state: AppState) => Partial<AppState> | AppState)) => void,
  opts?: { allowTriggerApprovalsFetch?: boolean; streamEpoch?: string | null },
): void {
  const state = get();
  const evtData = (evt.data ?? {}) as Record<string, unknown>;
  const payloadEpoch = typeof evtData.epoch === 'string' ? evtData.epoch : null;
  const effectiveEpoch = payloadEpoch ?? opts?.streamEpoch ?? null;

  // Reject stale events coming from an older epoch stream after we already transitioned to a newer epoch.
  if (effectiveEpoch && state.channelEpoch && effectiveEpoch !== state.channelEpoch && !payloadEpoch) {
    return;
  }

  // Reject stale replay events within the same epoch whose id is <= already applied watermark.
  if (
    evt.id !== undefined &&
    evt.id > 0 &&
    state.asOfEventId > 0 &&
    (!effectiveEpoch || !state.channelEpoch || effectiveEpoch === state.channelEpoch) &&
    evt.id <= state.asOfEventId
  ) {
    return;
  }

  if (payloadEpoch && payloadEpoch !== state.channelEpoch) {
    set({
      channelEpoch: payloadEpoch,
      asOfEventId: evt.id ?? 0,
      jobActivity: {},
    });
  } else if (evt.id !== undefined && evt.id > state.asOfEventId) {
    set({ asOfEventId: evt.id });
  }

  const jobId = typeof evtData.jobId === 'string' ? evtData.jobId : '';
  if (jobId && (evt.event === 'tool_call' || evt.event === 'error' ||
      (evt.event === 'task_status' && evtData.type === 'tool_result'))) {
    const toolName = typeof evtData.name === 'string' ? evtData.name.trim().slice(0, 80) : '';
    if (evt.event === 'error' || toolName) {
      const current = get();
      const observedAt = Date.now();
      const activity: JobActivity = evt.event === 'error'
        ? { kind: 'error', observedAt }
        : { kind: evt.event === 'tool_call' ? 'calling' : evtData.success === false ? 'failed' : 'returned', toolName, observedAt };
      set({ jobActivity: { ...current.jobActivity, [jobId]: activity } });
      if (!Object.hasOwn(current.jobActivity, jobId) &&
          !current.jobs.some((job) => job.jobId === jobId && job.status === 'Running')) {
        refreshSessionJobs(get);
      }
    }
    return;
  }

  switch (evt.event) {
    case 'heartbeat': {
      if (opts?.allowTriggerApprovalsFetch !== false) {
        const pendingCount = Number(evtData.pendingApprovals ?? 0);
        const cur = get();
        const localCount =
          cur.pendingApprovals.length +
          cur.pendingBrowserApprovals.length +
          cur.pendingComputerApprovals.length;
        if (pendingCount !== localCount) {
          void cur.reconcileApprovals();
        }
      }
      return;
    }

    case 'setconversationid': {
      const convId = evtData.conversationId as string | undefined;
      if (convId) {
        set((s) => ({
          ...selectConversation(s, convId),
          conversationMode: s.conversationMode === 'new-thread' ? 'continue-current' : s.conversationMode,
        }));
      }
      return;
    }

    case 'updateconversationtitle': {
      const convId = typeof evtData.conversationId === 'string' ? evtData.conversationId : '';
      const title = typeof evtData.title === 'string' ? evtData.title.trim() : '';
      const sessionId = typeof evtData.sessionId === 'string' ? evtData.sessionId : null;
      if (convId && title) {
        set((s) => ({
          latestTitleUpdate: {
            conversationId: convId,
            title,
            sessionId,
            seq: (s.latestTitleUpdate?.seq ?? 0) + 1,
          },
        }));
      }
      return;
    }

    case 'content': {
      const delta = String(evtData.content ?? evtData.text ?? '');
      set((s) => ({
        isTurnRunning: true,
        streamingContent: s.streamingContent + delta,
      }));
      return;
    }

    case 'thinking_start':
      set({ isTurnRunning: true, isThinkingStreaming: true });
      return;

    case 'thinking_content': {
      const delta = String(evtData.content ?? evtData.text ?? '');
      set((s) => ({
        isTurnRunning: true,
        isThinkingStreaming: true,
        streamingThinking: s.streamingThinking + delta,
      }));
      return;
    }

    case 'thinking_end': {
      const full = evtData.thinking_content as string | undefined;
      set((s) => ({
        isThinkingStreaming: false,
        streamingThinking: full && full.length > s.streamingThinking.length ? full : s.streamingThinking,
      }));
      return;
    }

    case 'tool_call': {
      const d = evtData as {
        name?: string;
        callId?: string | null;
        arguments?: Record<string, unknown> | null;
        argumentsTruncated?: boolean;
      };
      const name = d.name ?? 'tool';
      const key = d.callId || `${name}-${Date.now()}-${get().toolCalls.length}`;
      set((s) => ({
        isTurnRunning: true,
        toolCalls: [
          ...s.toolCalls,
          {
            key,
            callId: d.callId ?? null,
            name,
            arguments: d.arguments ?? null,
            argumentsTruncated: Boolean(d.argumentsTruncated),
            status: 'called',
            outputStdout: '',
            outputStderr: '',
            outputTruncated: false,
            suppressedLines: 0,
          },
        ],
      }));
      return;
    }

    case 'tool_started': {
      const d = evtData as { name?: string; callId?: string | null };
      set((s) => {
        const idx = d.callId
          ? s.toolCalls.findIndex((t) => t.callId === d.callId)
          : s.toolCalls.findIndex((t) => t.name === d.name && t.status === 'called');
        if (idx === -1) {
          return {
            isTurnRunning: true,
            toolCalls: [
              ...s.toolCalls,
              {
                key: d.callId || `${d.name ?? 'tool'}-${Date.now()}-${s.toolCalls.length}`,
                callId: d.callId ?? null,
                name: d.name ?? 'tool',
                status: 'running',
                outputStdout: '',
                outputStderr: '',
                outputTruncated: false,
                suppressedLines: 0,
              },
            ],
          };
        }
        const copy = [...s.toolCalls];
        const existing = copy[idx]!;
        copy[idx] = { ...existing, status: 'running' };
        return { isTurnRunning: true, toolCalls: copy };
      });
      return;
    }

    case 'tool_output': {
      const d = evtData as {
        tool?: string;
        commandTaskId?: string;
        stdout?: string | null;
        stderr?: string | null;
        throttled?: boolean;
        suppressedLines?: number | null;
      };
      set((s) => {
        let realIdx = -1;
        if (d.commandTaskId) {
          realIdx = s.toolCalls.findIndex((t) => t.callId === d.commandTaskId);
        }
        if (realIdx === -1 && d.tool) {
          const sameNameRunning = s.toolCalls
            .map((t, i) => ({ t, i }))
            .filter((x) => x.t.name === d.tool && (x.t.status === 'running' || x.t.status === 'called'));
          if (sameNameRunning.length === 1) {
            realIdx = sameNameRunning[0]!.i;
          } else if (sameNameRunning.length > 1) {
            realIdx = sameNameRunning[sameNameRunning.length - 1]!.i;
          }
        }
        if (realIdx === -1) {
          const anyRunning = s.toolCalls
            .map((t, i) => ({ t, i }))
            .filter((x) => x.t.status === 'running');
          if (anyRunning.length === 1) {
            realIdx = anyRunning[0]!.i;
          }
        }

        if (realIdx === -1) {
          return {
            toolCalls: [
              ...s.toolCalls,
              {
                key: d.commandTaskId || `out-${Date.now()}-${s.toolCalls.length}`,
                callId: d.commandTaskId ?? null,
                name: d.tool ?? 'exec_cmd',
                status: 'running',
                outputStdout: d.stdout ?? '',
                outputStderr: d.stderr ?? '',
                outputTruncated: Boolean(d.throttled),
                suppressedLines: Number(d.suppressedLines ?? 0),
              },
            ],
          };
        }
        const copy = [...s.toolCalls];
        const target = copy[realIdx]!;
        copy[realIdx] = {
          ...target,
          status: target.status === 'completed' || target.status === 'failed' ? target.status : 'running',
          outputStdout: target.outputStdout + (d.stdout ?? ''),
          outputStderr: target.outputStderr + (d.stderr ?? ''),
          outputTruncated: target.outputTruncated || Boolean(d.throttled),
          suppressedLines: target.suppressedLines + Number(d.suppressedLines ?? 0),
        };
        return { toolCalls: copy };
      });
      return;
    }

    case 'task_status': {
      if (typeof evtData.taskId === 'string' && (evtData.status === 'completed' || evtData.status === 'failed')) {
        refreshSessionJobs(get);
        return;
      }

      const d = evtData as { name?: string | null; callId?: string | null; success?: boolean | null };
      if (d.name || d.callId) {
        set((s) => {
          const revIdx = [...s.toolCalls]
            .reverse()
            .findIndex(
              (t) =>
                (d.callId && t.callId === d.callId) ||
                (!d.callId && t.name === d.name && t.status !== 'completed' && t.status !== 'failed'),
            );
          if (revIdx === -1) {
            if (!s.isTurnRunning || !d.name || (evtData.type !== 'tool_result')) return s;
            return {
              toolCalls: [...s.toolCalls, {
                key: d.callId || `result-${evt.id ?? s.toolCalls.length}`,
                callId: d.callId ?? null,
                name: d.name,
                status: d.success === false ? 'failed' : 'completed',
                outputStdout: '',
                outputStderr: '',
                outputTruncated: false,
                suppressedLines: 0,
              }],
            };
          }
          const realIdx = s.toolCalls.length - 1 - revIdx;
          const copy = [...s.toolCalls];
          const target = copy[realIdx]!;
          copy[realIdx] = {
            ...target,
            status: d.success === false ? 'failed' : 'completed',
          };
          return { toolCalls: copy };
        });
      }
      return;
    }

    case 'tool_approval_request': {
      const d = evtData as Parameters<typeof normalizeApprovalFromSse>[0];
      const normalized = normalizeApprovalFromSse(d);
      set((s) => {
        const exists = s.pendingApprovals.some((a) => a.approvalId === normalized.approvalId);
        if (exists) {
          return s;
        }
        return { pendingApprovals: [...s.pendingApprovals, normalized] };
      });
      dispatchDesktopNotification(
        'notify.toolApprovalTitle',
        `${normalized.tool}: ${normalized.executable}`,
      );
      return;
    }

    case 'tool_approval_resolved': {
      const d = evtData as { approvalId?: string };
      if (d.approvalId) {
        set((s) => ({
          pendingApprovals: s.pendingApprovals.filter((a) => a.approvalId !== d.approvalId),
        }));
      }
      return;
    }

    case 'business_skill_approval_requested': {
      const d = evtData as {
        approvalId?: string;
        requestDigest?: string;
        sessionId?: string;
        runId?: string;
        stepId?: string;
        kind?: string;
        allowedDecisions?: string[];
        expiresAtUtc?: string;
      };
      const approvalId = d.approvalId;
      if (!approvalId) {
        return;
      }
      const currentSid = get().activeSessionId;
      if (d.sessionId && currentSid && d.sessionId !== currentSid) {
        return;
      }
      const placeholder: BusinessApprovalRequestDto = {
        approvalId,
        requestDigest: d.requestDigest,
        sessionId: d.sessionId ?? currentSid ?? undefined,
        runId: d.runId,
        stepId: d.stepId,
        kind: d.kind,
        allowedDecisions: d.allowedDecisions,
        expiresAtUtc: d.expiresAtUtc,
      };
      set((s) => {
        const exists = s.pendingBusinessApprovals.some((a) => a.approvalId === approvalId);
        if (exists) {
          return s;
        }
        return { pendingBusinessApprovals: [...s.pendingBusinessApprovals, placeholder] };
      });
      dispatchDesktopNotification(
        'notify.toolApprovalTitle',
        `业务 Skill 写入确认: ${d.runId ?? approvalId}`,
      );
      const conn = get().connection;
      const targetGen = get().sessionGeneration;
      const targetConnGen = get().connectionGeneration;
      if (conn?.baseUrl && conn.accessToken && d.runId) {
        void apiClient
          .getSkillRun({ baseUrl: conn.baseUrl, accessToken: conn.accessToken }, d.runId)
          .then((run) => {
            const latest = get();
            if (
              latest.activeSessionId !== currentSid ||
              latest.sessionGeneration !== targetGen ||
              latest.connectionGeneration !== targetConnGen
            ) {
              return;
            }
            const hydrated = run.pendingApproval;
            if (!hydrated || hydrated.approvalId !== approvalId) {
              return;
            }
            set((s) => {
              if (!s.pendingBusinessApprovals.some((a) => a.approvalId === approvalId)) {
                return s;
              }
              return {
                pendingBusinessApprovals: s.pendingBusinessApprovals.map((a) =>
                  a.approvalId === approvalId ? hydrated : a,
                ),
              };
            });
          })
          .catch(() => undefined);
      }
      return;
    }

    case 'business_skill_approval_resolved': {
      const d = evtData as { approvalId?: string };
      if (d.approvalId) {
        set((s) => ({
          pendingBusinessApprovals: s.pendingBusinessApprovals.filter(
            (a) => a.approvalId !== d.approvalId,
          ),
        }));
      }
      return;
    }

    case 'browser_state_updated': {
      const incoming = evtData as BrowserSessionStateDto;
      const currentSid = get().activeSessionId;
      if (!currentSid || (incoming.sessionId && incoming.sessionId !== currentSid)) {
        return;
      }
      set((s) => ({
        browserState: shouldAcceptBrowserState(s.browserState, incoming)
          ? incoming
          : s.browserState,
      }));
      return;
    }

    case 'browser_approval_requested': {
      const incoming = evtData as BrowserApprovalRequestDto;
      const currentSid = get().activeSessionId;
      if (!currentSid || (incoming.sessionId && incoming.sessionId !== currentSid)) {
        return;
      }
      if (!incoming.approvalId) {
        return;
      }
      let inserted = false;
      set((s) => {
        if (s.pendingBrowserApprovals.some((a) => a.approvalId === incoming.approvalId)) {
          return s;
        }
        inserted = true;
        return { pendingBrowserApprovals: [...s.pendingBrowserApprovals, incoming] };
      });
      if (inserted) {
        dispatchDesktopNotification(
          'notify.toolApprovalTitle',
          `浏览器安全确认: ${incoming.targetDescription ?? incoming.action ?? incoming.approvalId}`,
        );
      }
      return;
    }

    case 'browser_approval_resolved': {
      const d = evtData as { approvalId?: string };
      if (d.approvalId) {
        set((s) => ({
          pendingBrowserApprovals: s.pendingBrowserApprovals.filter(
            (a) => a.approvalId !== d.approvalId,
          ),
        }));
      }
      return;
    }

    case 'computer_state_updated': {
      const incoming = evtData as ComputerSessionStateDto;
      const currentSid = get().activeSessionId;
      if (!currentSid || (incoming.sessionId && incoming.sessionId !== currentSid)) {
        return;
      }
      set((s) => ({
        computerState: shouldAcceptComputerState(s.computerState, incoming)
          ? incoming
          : s.computerState,
      }));
      return;
    }

    case 'computer_approval_requested': {
      const incoming = evtData as ComputerApprovalRequestDto;
      const currentSid = get().activeSessionId;
      if (!currentSid || (incoming.sessionId && incoming.sessionId !== currentSid)) {
        return;
      }
      if (!incoming.approvalId) {
        return;
      }
      let inserted = false;
      set((s) => {
        if (s.pendingComputerApprovals.some((a) => a.approvalId === incoming.approvalId)) {
          return s;
        }
        inserted = true;
        return { pendingComputerApprovals: [...s.pendingComputerApprovals, incoming] };
      });
      if (inserted) {
        dispatchDesktopNotification(
          'notify.toolApprovalTitle',
          `桌面操控确认: ${incoming.actionSummary ?? incoming.action ?? incoming.approvalId}`,
        );
      }
      return;
    }

    case 'computer_approval_resolved': {
      const d = evtData as { approvalId?: string };
      if (d.approvalId) {
        set((s) => ({
          pendingComputerApprovals: s.pendingComputerApprovals.filter(
            (a) => a.approvalId !== d.approvalId,
          ),
        }));
      }
      return;
    }

    case 'business_skill_run_updated': {
      const d = evtData as { runId?: string; status?: string };
      const terminalStatuses = new Set([
        'Succeeded',
        'PartialSucceeded',
        'Failed',
        'Cancelled',
        'OutcomeUnknown',
      ]);
      if (d.runId && d.status && terminalStatuses.has(d.status)) {
        set((s) => ({
          pendingBusinessApprovals: s.pendingBusinessApprovals.filter((a) => a.runId !== d.runId),
        }));
      }
      return;
    }

    case 'plan_approval_requested': {
      const prefs = usePreferencesStore.getState();
      dispatchDesktopNotification(
        'notify.planApprovalTitle',
        translate(prefs.language, 'notify.planApprovalBody'),
      );
      const conn = get().connection;
      const sid = get().activeSessionId;
      const targetGen = get().sessionGeneration;
      if (conn?.baseUrl && conn.accessToken && sid) {
        void apiClient
          .getPlan({ baseUrl: conn.baseUrl, accessToken: conn.accessToken }, sid)
          .then((plan) => {
            if (get().activeSessionId === sid && get().sessionGeneration === targetGen) {
              set({ currentPlan: plan });
            }
          })
          .catch(() => undefined);
      }
      return;
    }

    case 'plan_mode_changed': {
      const d = evtData as {
        sessionId?: string;
        workMode?: string;
        modeVersion?: number;
        planModeActive?: boolean;
        planModeVersion?: number;
      };
      const curSid = get().activeSessionId;
      if (d.sessionId && curSid && d.sessionId !== curSid) {
        return;
      }
      set((s) => {
        const nextActive = typeof d.planModeActive === 'boolean' ? d.planModeActive : s.planModeActive;
        const nextVersion = typeof d.planModeVersion === 'number' ? d.planModeVersion : s.planModeVersion;
        return {
          activeWorkMode: d.workMode === 'Daily' ? 'Daily' : d.workMode === 'Development' ? 'Development' : s.activeWorkMode,
          activeModeVersion: typeof d.modeVersion === 'number' ? d.modeVersion : s.activeModeVersion,
          planModeActive: nextActive,
          planModeVersion: nextVersion,
          planModeExitRequest: nextActive ? s.planModeExitRequest : null,
        };
      });
      return;
    }

    case 'plan_mode_exit_requested': {
      const d = evtData as {
        sessionId?: string;
        planModeVersion?: number;
        planSummary?: string;
        reason?: string | null;
      };
      const curSid = get().activeSessionId;
      if (d.sessionId && curSid && d.sessionId !== curSid) {
        return;
      }
      set({
        planModeExitRequest: {
          sessionId: d.sessionId || curSid || '',
          planModeVersion: Number(d.planModeVersion ?? get().planModeVersion),
          planSummary: String(d.planSummary ?? ''),
          reason: d.reason ?? null,
        },
      });
      return;
    }

    case 'worker_started':
      set({ workerBar: { status: 'started', message: String(evtData.content ?? evtData.text ?? '后台 Worker 正在执行...') } });
      refreshTurnMetadataAfterFinish(get, true);
      return;
    case 'worker_waiting_user': {
      const workerMsg = String(evtData.content ?? evtData.text ?? '后台 Worker 等待用户审批');
      set({ workerBar: { status: 'waiting_user', message: workerMsg } });
      dispatchDesktopNotification('notify.workerWaitingTitle', workerMsg);
      refreshTurnMetadataAfterFinish(get, true);
      return;
    }
    case 'worker_completed':
      set((s) => ({
        workerBar: { status: 'completed', message: String(evtData.content ?? evtData.text ?? '后台 Worker 已完成') },
        workspaceRevision: s.workspaceRevision + 1,
      }));
      refreshTurnMetadataAfterFinish(get, true);
      return;
    case 'worker_failed':
      set({ workerBar: { status: 'failed', message: String(evtData.content ?? evtData.text ?? '后台 Worker 失败') } });
      refreshTurnMetadataAfterFinish(get, true);
      return;

    case 'file_tree_updated':
      set((s) => ({ workspaceRevision: s.workspaceRevision + 1 }));
      return;

    case 'system_message': {
      const content = String(evtData.content ?? evtData.text ?? '');
      if (content) {
        set((s) => ({
          messages: [
            ...s.messages,
            {
              id: `sys-${evt.id ?? Date.now()}`,
              role: 'system',
              content,
              createdUtc: new Date().toISOString(),
            },
          ],
        }));
      }
      return;
    }

    case 'error': {
      const d = evtData as { error?: string; message?: string | null };
      const msg = d.message || d.error || '发生错误';
      const hadActiveTurn = get().isTurnRunning;
      set((s) => ({
        isTurnRunning: false,
        isThinkingStreaming: false,
        turnNotice: msg,
        messages: s.streamingContent || s.streamingThinking
          ? [
              ...s.messages,
              {
                id: `assistant-err-${Date.now()}`,
                role: 'assistant',
                content: s.streamingContent,
                thinkingContent: s.streamingThinking || null,
              },
            ]
          : s.messages,
        streamingContent: '',
        streamingThinking: '',
        toolCalls: [],
      }));
      if (hadActiveTurn) {
        dispatchDesktopNotification('notify.turnErrorTitle', msg);
      }
      refreshTurnMetadataAfterFinish(get, false);
      return;
    }

    case 'done': {
      const d = evtData as { source?: string | null };
      const prevState = get();
      const shouldNotifyDone =
        d.source !== 'cancelled' &&
        (prevState.isTurnRunning || Boolean(prevState.streamingContent || prevState.streamingThinking));
      set((s) => {
        const nextMessages = [...s.messages];
        if (s.streamingContent || s.streamingThinking) {
          nextMessages.push({
            id: `assistant-${evt.id ?? Date.now()}`,
            role: 'assistant',
            content: s.streamingContent || (d.source === 'cancelled' ? '（已取消本回合）' : ''),
            thinkingContent: s.streamingThinking || null,
            truncated: s.streamingTruncated,
            createdUtc: new Date().toISOString(),
          });
        }
        return {
          isTurnRunning: false,
          isThinkingStreaming: false,
          messages: nextMessages,
          streamingContent: '',
          streamingThinking: '',
          streamingTruncated: false,
          toolCalls: [],
          turnNotice: d.source === 'cancelled' ? '本回合已取消。' : null,
          workspaceRevision: s.workspaceRevision + 1,
        };
      });
      if (shouldNotifyDone) {
        const prefs = usePreferencesStore.getState();
        dispatchDesktopNotification(
          'notify.turnDoneTitle',
          translate(prefs.language, 'notify.turnDoneBody'),
        );
      }
      refreshTurnMetadataAfterFinish(get, true);
      return;
    }

    case 'replay_gap':
    default:
      return;
  }
}

export const useAppStore = create<AppState>((set, get) => ({
  connection: null,
  instanceKey: null,
  connectionGeneration: 0,
  sessionGeneration: 0,
  reconcileGeneration: 0,
  approvalsGeneration: 0,
  isReconcilingSnapshot: false,
  bufferedSseEvents: [],

  sseState: 'closed',
  activeSessionId: null,
  activeConversationId: null,
  conversationMode: 'reuse-master',
  activeWorkMode: 'Development',
  activeModeVersion: 1,
  planModeActive: false,
  planModeVersion: 1,
  planModeExitRequest: null,
  latestTitleUpdate: null,

  messages: [],
  messagesTruncated: false,
  streamingContent: '',
  streamingThinking: '',
  isThinkingStreaming: false,
  streamingTruncated: false,
  isTurnRunning: false,
  turnNotice: null,

  toolCalls: [],
  pendingApprovals: [],
  pendingBusinessApprovals: [],
  pendingBrowserApprovals: [],
  pendingComputerApprovals: [],
  browserState: null,
  computerState: null,
  currentPlan: null,
  jobs: [],
  jobActivity: {},
  verificationReceipts: {},
  workerBar: { status: 'idle', message: null },
  contextWindow: null,

  asOfEventId: 0,
  channelEpoch: null,
  isSettingsOpen: false,
  isWizardOpen: false,
  wizardDismissed: false,
  isInspectorOpen: false,
  inspectorTab: 'files',
  workspaceRevision: 0,

  setConnection: (connection) => {
    const prevConn = get().connection;
    const prevKey = get().instanceKey;
    const nextKey = computeInstanceKey(connection);

    const modeSwitched = Boolean(prevConn && prevConn.mode !== connection.mode);
    const standaloneTargetSwitched =
      connection.mode === 'standalone' &&
      Boolean(
        prevConn?.mode === 'standalone' &&
          ((prevConn.targetBaseUrl &&
            connection.targetBaseUrl &&
            prevConn.targetBaseUrl !== connection.targetBaseUrl) ||
            (prevConn.instanceId && connection.instanceId && prevConn.instanceId !== connection.instanceId)),
      );
    const switchedInstance = modeSwitched || standaloneTargetSwitched;

    const backendEpochSwitched = Boolean(
      prevConn &&
        ((prevConn.instanceId && connection.instanceId && prevConn.instanceId !== connection.instanceId) ||
          (prevConn.targetBaseUrl && connection.targetBaseUrl && prevConn.targetBaseUrl !== connection.targetBaseUrl) ||
          prevConn.restartCount !== connection.restartCount),
    );

    if (switchedInstance) {
      activeSnapshotAbort?.abort();
      activeApprovalsAbort?.abort();
      activeContextWindowAbort?.abort();
      set((s) => ({
        connection,
        instanceKey: nextKey,
        connectionGeneration: s.connectionGeneration + 1,
        sessionGeneration: s.sessionGeneration + 1,
        reconcileGeneration: s.reconcileGeneration + 1,
        approvalsGeneration: s.approvalsGeneration + 1,
        isReconcilingSnapshot: false,
        bufferedSseEvents: [],
        activeSessionId: null,
        activeConversationId: null,
        messages: [],
        messagesTruncated: false,
        streamingContent: '',
        streamingThinking: '',
        isThinkingStreaming: false,
        streamingTruncated: false,
        isTurnRunning: false,
        toolCalls: [],
        pendingApprovals: [],
        pendingBusinessApprovals: [],
        pendingBrowserApprovals: [],
        pendingComputerApprovals: [],
        browserState: null,
        computerState: null,
        currentPlan: null,
        jobs: [],
        jobActivity: {},
        verificationReceipts: {},
        workerBar: { status: 'idle', message: null },
        contextWindow: null,
        asOfEventId: 0,
        channelEpoch: null,
        turnNotice: null,
        activeWorkMode: 'Development',
        activeModeVersion: 1,
        planModeActive: false,
        planModeVersion: 1,
        planModeExitRequest: null,
      }));
      return;
    }

    if (backendEpochSwitched) {
      activeSnapshotAbort?.abort();
      activeApprovalsAbort?.abort();
      activeContextWindowAbort?.abort();
      set((s) => ({
        connection,
        instanceKey: nextKey ?? prevKey,
        connectionGeneration: s.connectionGeneration + 1,
        bufferedSseEvents: [],
        jobActivity: {},
        contextWindow: null,
      }));
      return;
    }

    set({ connection, instanceKey: nextKey ?? prevKey });
  },

  setSseState: (sseState) => set({ sseState }),

  setActiveSession: (sessionId) => {
    activeSnapshotAbort?.abort();
    activeApprovalsAbort?.abort();
    activeContextWindowAbort?.abort();
    set((s) => ({
      sessionGeneration: s.sessionGeneration + 1,
      reconcileGeneration: s.reconcileGeneration + 1,
      approvalsGeneration: s.approvalsGeneration + 1,
      isReconcilingSnapshot: false,
      bufferedSseEvents: [],
      activeSessionId: sessionId,
      activeConversationId: null,
      messages: [],
      messagesTruncated: false,
      streamingContent: '',
      streamingThinking: '',
      isThinkingStreaming: false,
      streamingTruncated: false,
      isTurnRunning: false,
      toolCalls: [],
      pendingApprovals: [],
      pendingBusinessApprovals: [],
      pendingBrowserApprovals: [],
      pendingComputerApprovals: [],
      browserState: null,
      computerState: null,
      currentPlan: null,
      jobs: [],
      jobActivity: {},
      verificationReceipts: {},
      contextWindow: null,
      workerBar: { status: 'idle', message: null },
      asOfEventId: 0,
      channelEpoch: null,
      turnNotice: null,
      latestTitleUpdate: null,
      activeWorkMode: 'Development',
      activeModeVersion: 1,
      planModeActive: false,
      planModeVersion: 1,
      planModeExitRequest: null,
    }));
  },

  setActiveConversation: (conversationId) => set((s) => selectConversation(s, conversationId)),
  startNewConversation: (conversationId) =>
    set((s) => ({
      reconcileGeneration: s.reconcileGeneration + 1,
      ...selectConversation(s, conversationId),
      conversationMode: 'continue-current',
      messages: [],
      messagesTruncated: false,
      streamingContent: '',
      streamingThinking: '',
      isThinkingStreaming: false,
      streamingTruncated: false,
      isTurnRunning: false,
      toolCalls: [],
      turnNotice: null,
    })),
  setConversationMode: (conversationMode) => set({ conversationMode }),
  setTurnNotice: (turnNotice) => set({ turnNotice }),
  setSettingsOpen: (isSettingsOpen) => set({ isSettingsOpen }),
  setWizardOpen: (isWizardOpen, dismissed) =>
    set((state) => ({
      isWizardOpen,
      wizardDismissed: dismissed !== undefined ? dismissed : state.wizardDismissed,
    })),
  setInspectorOpen: (isInspectorOpen) => set({ isInspectorOpen }),
  setInspectorTab: (inspectorTab) => set({ inspectorTab }),
  openInspectorTab: (inspectorTab) => set({ isInspectorOpen: true, inspectorTab }),
  bumpWorkspaceRevision: () => set((s) => ({ workspaceRevision: s.workspaceRevision + 1 })),

  appendOptimisticUserMessage: (content) =>
    set((state) => ({
      isTurnRunning: true,
      turnNotice: null,
      streamingContent: '',
      streamingThinking: '',
      streamingTruncated: false,
      toolCalls: [],
      messages: [
        ...state.messages,
        {
          id: `local-user-${Date.now()}`,
          role: 'user',
          content,
          createdUtc: new Date().toISOString(),
        },
      ],
    })),

  applySseEvent: (evt, meta) => {
    const state = get();
    const evtData = (evt.data ?? {}) as Record<string, unknown>;
    const payloadEpoch = typeof evtData.epoch === 'string' ? evtData.epoch : null;
    const streamEpoch = payloadEpoch ?? meta?.streamEpoch ?? null;

    if (state.isReconcilingSnapshot) {
      // If a new epoch is announced via heartbeat/replay_gap during snapshot recovery, update
      // channelEpoch immediately so subsequent current-epoch buffered events aren't rejected
      // as mismatched if snapshot fetch fails (F1 failure-recovery path).
      if (payloadEpoch && payloadEpoch !== state.channelEpoch) {
        set({ channelEpoch: payloadEpoch, asOfEventId: 0, jobActivity: {} });
      }
      if (evt.event !== 'heartbeat' && evt.event !== 'replay_gap') {
        set((s) => ({
          bufferedSseEvents: [
            ...s.bufferedSseEvents,
            { event: evt, connectionGeneration: s.connectionGeneration, streamEpoch },
          ],
        }));
      }
      return;
    }
    reduceSseEvent(evt, get, set, { streamEpoch });
  },

  applySnapshot: (snapshot) => {
    const recovered = buildRecoveredSessionState(snapshot);
    const snapWorkMode: SessionWorkMode = snapshot.session?.workMode === 'Daily' ? 'Daily' : 'Development';
    const snapModeVersion = Number(snapshot.session?.modeVersion ?? 1);
    const snapPlanActive = Boolean(snapshot.session?.planModeActive);
    const snapPlanVersion = Number(snapshot.session?.planModeVersion ?? 1);
    set((s) => ({
      asOfEventId: recovered.asOfEventId,
      channelEpoch: recovered.channelEpoch,
      ...selectConversation(s, recovered.activeConversationId),
      isTurnRunning: recovered.isTurnRunning,
      streamingContent: recovered.streamingContent,
      streamingThinking: recovered.streamingThinking,
      streamingTruncated: recovered.streamingTruncated,
      messagesTruncated: recovered.messagesTruncated,
      messages: recovered.messages,
      pendingApprovals: recovered.pendingApprovals,
      currentPlan: recovered.currentPlan,
      toolCalls: recovered.toolCalls,
      jobs: snapshot.jobs ?? [],
      jobActivity: recovered.channelEpoch === s.channelEpoch
        ? keepRunningJobActivity(snapshot.jobs ?? [], s.jobActivity)
        : {},
      activeWorkMode: snapWorkMode,
      activeModeVersion: snapModeVersion,
      planModeActive: snapPlanActive,
      planModeVersion: snapPlanVersion,
      planModeExitRequest: snapPlanActive ? s.planModeExitRequest : null,
    }));
  },

  reconcileSession: async (_reason, options) => {
    const {
      connection,
      activeSessionId,
      sessionGeneration,
      connectionGeneration: targetConnGen,
      asOfEventId: preAsOf,
      channelEpoch: preEpoch,
    } = get();
    if (!connection?.baseUrl || !connection.accessToken || !activeSessionId) {
      return;
    }

    activeSnapshotAbort?.abort();
    const controller = new AbortController();
    activeSnapshotAbort = controller;

    const targetReconcileGen = get().reconcileGeneration + 1;
    // Preserve buffered events belonging to the CURRENT connectionGeneration so overlapping
    // reconciles never drop in-flight events, while discarding any older connection leftovers (R2 + F1).
    set((s) => ({
      reconcileGeneration: targetReconcileGen,
      isReconcilingSnapshot: true,
      ...(options?.expectedEpoch && options.expectedEpoch !== s.channelEpoch
        ? { channelEpoch: options.expectedEpoch, asOfEventId: 0, jobActivity: {} }
        : {}),
      bufferedSseEvents: s.bufferedSseEvents.filter((b) => b.connectionGeneration === targetConnGen),
    }));

    const auth = { baseUrl: connection.baseUrl, accessToken: connection.accessToken };
    try {
      const snapshot = await apiClient.getSnapshot(auth, activeSessionId, controller.signal);

      const cur = get();
      if (
        controller.signal.aborted ||
        cur.activeSessionId !== activeSessionId ||
        cur.sessionGeneration !== sessionGeneration ||
        cur.connectionGeneration !== targetConnGen ||
        cur.reconcileGeneration !== targetReconcileGen
      ) {
        return;
      }

      const buffered = cur.bufferedSseEvents.filter((b) => b.connectionGeneration === targetConnGen);
      const snapEventId = Number(snapshot.asOfEventId ?? 0);
      const snapEpoch = snapshot.channelEpoch ?? null;

      // Reject older snapshot within the exact same epoch if client already moved strictly ahead before reconcile began.
      const isOlderSameEpochSnapshot =
        Boolean(preEpoch && snapEpoch && preEpoch === snapEpoch) && snapEventId < preAsOf;

      if (!isOlderSameEpochSnapshot) {
        cur.applySnapshot(snapshot);
      }

      const baselineEpoch = get().channelEpoch;
      const baselineEventId = get().asOfEventId;

      set({ isReconcilingSnapshot: false, bufferedSseEvents: [] });

      for (const item of buffered) {
        if (item.streamEpoch && baselineEpoch && item.streamEpoch !== baselineEpoch) {
          // Discard leftover events from an earlier stream epoch (F1)
          continue;
        }
        if (item.event.id !== undefined && item.event.id > 0 && item.event.id <= baselineEventId) {
          continue;
        }
        reduceSseEvent(item.event, get, set, {
          allowTriggerApprovalsFetch: false,
          streamEpoch: item.streamEpoch,
        });
      }

      const finalState = get();
      options?.onCursorSynced?.(finalState.asOfEventId, finalState.channelEpoch);

      void get().refreshContextWindow();

      try {
        const approvalsRes = await apiClient.getPendingApprovals(
          auth,
          activeSessionId,
          controller.signal,
        );
        const latest = get();
        if (
          !controller.signal.aborted &&
          latest.activeSessionId === activeSessionId &&
          latest.sessionGeneration === sessionGeneration &&
          latest.connectionGeneration === targetConnGen &&
          latest.reconcileGeneration === targetReconcileGen
        ) {
          set((s) => ({
            pendingBusinessApprovals: Array.isArray(approvalsRes.businessApprovals)
              ? approvalsRes.businessApprovals
              : s.pendingBusinessApprovals,
            pendingBrowserApprovals: Array.isArray(approvalsRes.browserApprovals)
              ? approvalsRes.browserApprovals
              : s.pendingBrowserApprovals,
            pendingComputerApprovals: Array.isArray(approvalsRes.computerApprovals)
              ? approvalsRes.computerApprovals
              : s.pendingComputerApprovals,
          }));
        }
      } catch {
        // optional
      }

      try {
        const bState = await apiClient.getBrowserState(auth, activeSessionId, controller.signal);
        const latest = get();
        if (
          !controller.signal.aborted &&
          latest.activeSessionId === activeSessionId &&
          latest.sessionGeneration === sessionGeneration &&
          latest.connectionGeneration === targetConnGen &&
          latest.reconcileGeneration === targetReconcileGen
        ) {
          set((s) => ({
            browserState: shouldAcceptBrowserState(s.browserState, bState)
              ? bState
              : s.browserState,
          }));
        }
      } catch {
        // optional
      }

      try {
        const cState = await apiClient.getComputerState(auth, activeSessionId, controller.signal);
        const latest = get();
        if (
          !controller.signal.aborted &&
          latest.activeSessionId === activeSessionId &&
          latest.sessionGeneration === sessionGeneration &&
          latest.connectionGeneration === targetConnGen &&
          latest.reconcileGeneration === targetReconcileGen
        ) {
          set((s) => ({
            computerState: shouldAcceptComputerState(s.computerState, cState)
              ? cState
              : s.computerState,
          }));
        }
      } catch {
        // optional
      }
    } catch {
      const cur = get();
      if (cur.reconcileGeneration === targetReconcileGen && cur.connectionGeneration === targetConnGen) {
        const buffered = cur.bufferedSseEvents.filter((b) => b.connectionGeneration === targetConnGen);
        set({ isReconcilingSnapshot: false, bufferedSseEvents: [] });
        for (const item of buffered) {
          reduceSseEvent(item.event, get, set, {
            allowTriggerApprovalsFetch: false,
            streamEpoch: item.streamEpoch,
          });
        }
      }
    } finally {
      if (activeSnapshotAbort === controller) {
        activeSnapshotAbort = null;
      }
    }
  },

  reconcileApprovals: async () => {
    const { connection, activeSessionId, sessionGeneration } = get();
    if (!connection?.baseUrl || !connection.accessToken || !activeSessionId) {
      return;
    }

    activeApprovalsAbort?.abort();
    const controller = new AbortController();
    activeApprovalsAbort = controller;

    const targetGen = get().approvalsGeneration + 1;
    set({ approvalsGeneration: targetGen });

    const auth = { baseUrl: connection.baseUrl, accessToken: connection.accessToken };
    try {
      const res = await apiClient.getPendingApprovals(auth, activeSessionId, controller.signal);
      const cur = get();
      if (
        controller.signal.aborted ||
        cur.activeSessionId !== activeSessionId ||
        cur.sessionGeneration !== sessionGeneration ||
        cur.approvalsGeneration !== targetGen ||
        cur.isReconcilingSnapshot
      ) {
        return;
      }
      set((s) => ({
        pendingApprovals: (res.items ?? []).map(normalizeApprovalFromRest),
        ...(Array.isArray(res.businessApprovals)
          ? { pendingBusinessApprovals: res.businessApprovals }
          : { pendingBusinessApprovals: s.pendingBusinessApprovals }),
        ...(Array.isArray(res.browserApprovals)
          ? { pendingBrowserApprovals: res.browserApprovals }
          : { pendingBrowserApprovals: s.pendingBrowserApprovals }),
        ...(Array.isArray(res.computerApprovals)
          ? { pendingComputerApprovals: res.computerApprovals }
          : { pendingComputerApprovals: s.pendingComputerApprovals }),
      }));
    } catch {
      // ignore aborted or stale approval fetch
    } finally {
      if (activeApprovalsAbort === controller) {
        activeApprovalsAbort = null;
      }
    }
  },

  removeBusinessApproval: (approvalId) =>
    set((s) => ({
      pendingBusinessApprovals: s.pendingBusinessApprovals.filter(
        (a) => a.approvalId !== approvalId,
      ),
    })),

  removeBrowserApproval: (approvalId) =>
    set((s) => ({
      pendingBrowserApprovals: s.pendingBrowserApprovals.filter(
        (a) => a.approvalId !== approvalId,
      ),
    })),

  removeComputerApproval: (approvalId) =>
    set((s) => ({
      pendingComputerApprovals: s.pendingComputerApprovals.filter(
        (a) => a.approvalId !== approvalId,
      ),
    })),

  setBrowserState: (browserState) => set({ browserState }),
  setComputerState: (computerState) => set({ computerState }),

  setVerificationReceipt: (jobId, receipt) =>
    set((s) => ({
      verificationReceipts: { ...s.verificationReceipts, [jobId]: receipt },
    })),
  refreshContextWindow: async () => {
    const { connection, activeSessionId, activeConversationId, sessionGeneration, connectionGeneration } = get();
    if (connection?.status !== 'ready' || !connection.baseUrl || !connection.accessToken || !activeSessionId || !activeConversationId) {
      return;
    }

    activeContextWindowAbort?.abort();
    const controller = new AbortController();
    activeContextWindowAbort = controller;
    try {
      const contextWindow = await apiClient.getContextWindow(
        { baseUrl: connection.baseUrl, accessToken: connection.accessToken },
        activeSessionId,
        controller.signal,
        activeConversationId,
      );
      const latest = get();
      if (
        !controller.signal.aborted &&
        latest.sessionGeneration === sessionGeneration &&
        latest.connectionGeneration === connectionGeneration &&
        latest.activeSessionId === activeSessionId &&
        latest.activeConversationId === activeConversationId
      ) {
        set({ contextWindow });
      }
    } catch {
      // Context usage is optional for the chat UI.
    } finally {
      if (activeContextWindowAbort === controller) {
        activeContextWindowAbort = null;
      }
    }
  },
  setJobs: (jobs) => set((s) => ({ jobs, jobActivity: keepRunningJobActivity(jobs, s.jobActivity) })),
  setPlan: (currentPlan) => set({ currentPlan }),
  setSessionPolicyState: (policy) =>
    set((s) => ({
      activeWorkMode: policy.workMode ?? s.activeWorkMode,
      activeModeVersion: policy.modeVersion ?? s.activeModeVersion,
      planModeActive: policy.planModeActive ?? s.planModeActive,
      planModeVersion: policy.planModeVersion ?? s.planModeVersion,
      planModeExitRequest: policy.planModeActive === false ? null : s.planModeExitRequest,
    })),
  dismissPlanModeExitRequest: () => set({ planModeExitRequest: null }),
  clearTurnVisuals: () =>
    set({
      streamingContent: '',
      streamingThinking: '',
      isThinkingStreaming: false,
      toolCalls: [],
      turnNotice: null,
    }),
}));
