import { create } from 'zustand';
import type { BackendConnectionPublicInfo } from '@shared/ipc';
import type { ParsedSseEvent } from '@shared/contracts/sse.generated';
import {
  apiClient,
  type BackgroundJob,
  type SessionContextWindow,
  type SessionPlan,
  type SessionSnapshot,
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
export type InspectorTab = 'files' | 'changes' | 'jobs';

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
  currentPlan: SessionPlan | null;
  jobs: BackgroundJob[];
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
  setVerificationReceipt: (jobId: string, receipt: VerificationReceipt) => void;
  setContextWindow: (cw: SessionContextWindow | null) => void;
  setJobs: (jobs: BackgroundJob[]) => void;
  setPlan: (plan: SessionPlan | null) => void;
  clearTurnVisuals: () => void;
}

function refreshTurnMetadataAfterFinish(
  get: () => AppState,
  set: (partial: Partial<AppState> | ((state: AppState) => Partial<AppState> | AppState)) => void,
  includeJobs: boolean,
): void {
  const conn = get().connection;
  const sid = get().activeSessionId;
  const targetGen = get().sessionGeneration;
  if (!conn?.baseUrl || !conn.accessToken || !sid) {
    return;
  }
  const auth = { baseUrl: conn.baseUrl, accessToken: conn.accessToken };
  void apiClient
    .getContextWindow(auth, sid)
    .then((cw) => {
      if (get().activeSessionId === sid && get().sessionGeneration === targetGen) {
        set({ contextWindow: cw });
      }
    })
    .catch(() => undefined);

  if (includeJobs) {
    void apiClient
      .listJobs(auth, sid)
      .then((jobs) => {
        if (get().activeSessionId === sid && get().sessionGeneration === targetGen) {
          set({ jobs });
        }
      })
      .catch(() => undefined);
  }
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
    });
  } else if (evt.id !== undefined && evt.id > state.asOfEventId) {
    set({ asOfEventId: evt.id });
  }

  switch (evt.event) {
    case 'heartbeat': {
      if (opts?.allowTriggerApprovalsFetch !== false) {
        const pendingCount = Number(evtData.pendingApprovals ?? 0);
        if (pendingCount !== get().pendingApprovals.length) {
          void get().reconcileApprovals();
        }
      }
      return;
    }

    case 'setconversationid': {
      const convId = evtData.conversationId as string | undefined;
      if (convId) {
        set((s) => ({
          activeConversationId: convId,
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
        return { toolCalls: copy };
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
      const d = evtData as { name?: string | null; callId?: string | null; success?: boolean | null; result?: string | null };
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
            return s;
          }
          const realIdx = s.toolCalls.length - 1 - revIdx;
          const copy = [...s.toolCalls];
          const target = copy[realIdx]!;
          copy[realIdx] = {
            ...target,
            status: d.success === false ? 'failed' : 'completed',
            result: d.result ?? target.result,
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

    case 'worker_started':
      set({ workerBar: { status: 'started', message: String(evtData.content ?? evtData.text ?? '后台 Worker 正在执行...') } });
      refreshTurnMetadataAfterFinish(get, set, true);
      return;
    case 'worker_waiting_user': {
      const workerMsg = String(evtData.content ?? evtData.text ?? '后台 Worker 等待用户审批');
      set({ workerBar: { status: 'waiting_user', message: workerMsg } });
      dispatchDesktopNotification('notify.workerWaitingTitle', workerMsg);
      refreshTurnMetadataAfterFinish(get, set, true);
      return;
    }
    case 'worker_completed':
      set((s) => ({
        workerBar: { status: 'completed', message: String(evtData.content ?? evtData.text ?? '后台 Worker 已完成') },
        workspaceRevision: s.workspaceRevision + 1,
      }));
      refreshTurnMetadataAfterFinish(get, set, true);
      return;
    case 'worker_failed':
      set({ workerBar: { status: 'failed', message: String(evtData.content ?? evtData.text ?? '后台 Worker 失败') } });
      refreshTurnMetadataAfterFinish(get, set, true);
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
        messages: s.streamingContent
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
      }));
      if (hadActiveTurn) {
        dispatchDesktopNotification('notify.turnErrorTitle', msg);
      }
      refreshTurnMetadataAfterFinish(get, set, false);
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
      refreshTurnMetadataAfterFinish(get, set, true);
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
  currentPlan: null,
  jobs: [],
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
        currentPlan: null,
        jobs: [],
        verificationReceipts: {},
        workerBar: { status: 'idle', message: null },
        contextWindow: null,
        asOfEventId: 0,
        channelEpoch: null,
        turnNotice: null,
      }));
      return;
    }

    if (backendEpochSwitched) {
      activeSnapshotAbort?.abort();
      activeApprovalsAbort?.abort();
      set((s) => ({
        connection,
        instanceKey: nextKey ?? prevKey,
        connectionGeneration: s.connectionGeneration + 1,
        bufferedSseEvents: [],
      }));
      return;
    }

    set({ connection, instanceKey: nextKey ?? prevKey });
  },

  setSseState: (sseState) => set({ sseState }),

  setActiveSession: (sessionId) => {
    activeSnapshotAbort?.abort();
    activeApprovalsAbort?.abort();
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
      currentPlan: null,
      jobs: [],
      verificationReceipts: {},
      contextWindow: null,
      workerBar: { status: 'idle', message: null },
      asOfEventId: 0,
      channelEpoch: null,
      turnNotice: null,
      latestTitleUpdate: null,
    }));
  },

  setActiveConversation: (activeConversationId) => set({ activeConversationId }),
  startNewConversation: (conversationId) =>
    set((s) => ({
      reconcileGeneration: s.reconcileGeneration + 1,
      activeConversationId: conversationId,
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
        set({ channelEpoch: payloadEpoch, asOfEventId: 0 });
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
    set({
      asOfEventId: recovered.asOfEventId,
      channelEpoch: recovered.channelEpoch,
      activeConversationId: recovered.activeConversationId,
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
    });
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
        ? { channelEpoch: options.expectedEpoch, asOfEventId: 0 }
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

      try {
        const cw = await apiClient.getContextWindow(auth, activeSessionId, controller.signal);
        const latest = get();
        if (
          !controller.signal.aborted &&
          latest.activeSessionId === activeSessionId &&
          latest.sessionGeneration === sessionGeneration &&
          latest.connectionGeneration === targetConnGen
        ) {
          set({ contextWindow: cw });
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
      set({ pendingApprovals: (res.items ?? []).map(normalizeApprovalFromRest) });
    } catch {
      // ignore aborted or stale approval fetch
    } finally {
      if (activeApprovalsAbort === controller) {
        activeApprovalsAbort = null;
      }
    }
  },

  setVerificationReceipt: (jobId, receipt) =>
    set((s) => ({
      verificationReceipts: { ...s.verificationReceipts, [jobId]: receipt },
    })),
  setContextWindow: (contextWindow) => set({ contextWindow }),
  setJobs: (jobs) => set({ jobs }),
  setPlan: (currentPlan) => set({ currentPlan }),
  clearTurnVisuals: () =>
    set({
      streamingContent: '',
      streamingThinking: '',
      isThinkingStreaming: false,
      toolCalls: [],
      turnNotice: null,
    }),
}));
