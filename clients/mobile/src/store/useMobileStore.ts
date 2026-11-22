import { App } from '@capacitor/app';
import { Capacitor, type PluginListenerHandle } from '@capacitor/core';
import { Haptics, ImpactStyle } from '@capacitor/haptics';
import { Network } from '@capacitor/network';
import { create } from 'zustand';
import type {
  BrowserSessionStateDto,
  ComputerSessionStateDto,
  ConversationSummary,
  ExecutionSettingsDto,
  HistoryMessage,
  PagedConversationsResponse,
  PagedSessionsResponse,
  RemotePendingTodoItem,
  SessionBootstrapResponse,
  SessionPlanDto,
  SessionSummary,
  WorkspaceSummary,
} from '@/api/hostTypes';
import { getNativeCryptoPlugin } from '@/bridge/nativeCryptoBridge';
import type {
  NativeCryptoCapabilities,
  PersistedSecurePairingState,
} from '@/bridge/nativeCryptoTypes';
import { sendChatWithIdempotency, type MobileChatAttachmentRef, type MobileStructuredPlanAction } from '@/services/chatService';
import { classifyRemoteError, RemoteApiException, type MobileAppError } from '@/services/errorClassifier';
import { runQrPairingFlow, type ActivePairingPreview, type PairingProgressStage } from '@/services/pairingService';
import {
  fetchVerifiedApprovalScript,
  submitVerifiedTodoDecision,
  syncPendingSummary,
  type PendingSyncCursor,
} from '@/services/pendingSyncService';
import { DEFAULT_PORTAL_URL, normalizePortalOrigin } from '@/services/portalConfig';
import { IosPushTokenManager } from '@/services/pushTokenService';
import { RelayClient } from '@/services/relayClient';
import { ScreenshotCache } from '@/services/screenshotCache';
import { subscribeSessionSse, type SseSubscriptionHandle } from '@/services/sseClient';
import { uploadSessionAttachment } from '@/services/uploadService';

export type MobileTab = 'sessions' | 'approvals' | 'settings';

export interface ActiveSessionRow {
  readonly sessionId: string;
  readonly name: string;
  readonly workMode: string;
  readonly updatedLabel: string;
  readonly isExternalWorkspace: boolean;
  readonly planModeActive: boolean;
}

interface MobileStoreState {
  bootstrapped: boolean;
  capabilities: NativeCryptoCapabilities | null;
  pairingState: PersistedSecurePairingState | null;
  portalUrlDraft: string;
  pairingStage: PairingProgressStage | null;
  pairingPreview: ActivePairingPreview | null;
  activeTab: MobileTab;
  desktopOnline: boolean | null;
  lastError: MobileAppError | null;

  sessions: ActiveSessionRow[];
  loadingSessions: boolean;
  selectedSessionId: string | null;
  conversations: ConversationSummary[];
  selectedConversationId: string | null;
  messages: HistoryMessage[];
  streamingText: string;
  sseStatus: 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'stopped';
  currentPlan: SessionPlanDto | null;
  browserState: BrowserSessionStateDto | null;
  browserScreenshotUrl: string | null;
  computerState: ComputerSessionStateDto | null;
  executionSettings: ExecutionSettingsDto | null;

  pendingTodos: RemotePendingTodoItem[];
  pendingCursor: PendingSyncCursor | null;
  loadingTodos: boolean;
  sendingChat: boolean;

  bootstrap(): Promise<void>;
  disposeLifecycleListeners(): Promise<void>;
  setActiveTab(tab: MobileTab): void;
  setPortalUrlDraft(url: string): void;
  clearError(): void;
  setError(err: unknown): void;

  startQrPairing(params: { rawQrUri: string; mobileDeviceName?: string }): Promise<void>;
  scanWithNativeCamera(): Promise<string>;

  refreshSessions(): Promise<void>;
  createDefaultSession(name?: string): Promise<string | null>;
  createDailyAssistantSession(): Promise<string | null>;
  openSession(sessionId: string): Promise<void>;
  closeOpenSession(): void;
  selectConversation(conversationId: string): Promise<void>;
  createNewConversation(): Promise<void>;

  sendMessage(params: {
    text: string;
    attachments?: readonly MobileChatAttachmentRef[];
  }): Promise<void>;
  uploadAttachment(file: File, onProgressLabel?: (l: string) => void): Promise<MobileChatAttachmentRef>;
  cancelActiveTurn(): Promise<void>;

  loadSessionPlan(sessionId: string): Promise<void>;
  submitPlanDecision(action: 'approve' | 'execute', startExecution: boolean): Promise<void>;
  refreshBrowserAndComputer(sessionId: string): Promise<void>;
  closeBrowserSession(sessionId: string): Promise<void>;
  emergencyStopComputer(sessionId: string): Promise<void>;

  syncPendingTodos(forceSnapshot?: boolean): Promise<void>;
  inspectCommandScript(approvalId: string): Promise<string>;
  decideTodoApproval(item: RemotePendingTodoItem, decision: 'allow_once' | 'deny', note?: string): Promise<void>;

  loadExecutionSettings(): Promise<void>;
  updatePermissionMode(mode: string): Promise<void>;
  toggleApnsPush(enabled: boolean): Promise<void>;
  logoutAndUnpair(): Promise<void>;
}

const screenshotCache = new ScreenshotCache();
const pushTokenManager = new IosPushTokenManager();
let relayClientSingleton: RelayClient | null = null;
let activeSseHandle: SseSubscriptionHandle | null = null;
let appStateListener: PluginListenerHandle | null = null;
let networkListener: PluginListenerHandle | null = null;

function ensureRelayClient(
  onRevoked: (err: MobileAppError) => void,
  onUpdated: (next: PersistedSecurePairingState | null) => void,
): RelayClient {
  if (!relayClientSingleton) {
    relayClientSingleton = new RelayClient({
      onSessionRevoked: onRevoked,
      onStateUpdated: onUpdated,
    });
  }
  return relayClientSingleton;
}

function toAppError(err: unknown): MobileAppError {
  if (err instanceof RemoteApiException) {
    return err.appError;
  }
  if (err instanceof Error) {
    return classifyRemoteError('client_error', 400, err.message);
  }
  return classifyRemoteError('unknown_error', 500, '发生未知错误');
}

async function triggerHapticLight(): Promise<void> {
  if (!Capacitor.isNativePlatform()) {
    return;
  }
  try {
    await Haptics.impact({ style: ImpactStyle.Light });
  } catch {
    // ignore haptics error
  }
}

export const useMobileStore = create<MobileStoreState>((set, get) => {
  const client = ensureRelayClient(
    (revErr) => {
      activeSseHandle?.close();
      activeSseHandle = null;
      screenshotCache.clearAll();
      set({
        pairingState: null,
        selectedSessionId: null,
        selectedConversationId: null,
        messages: [],
        pendingTodos: [],
        pendingCursor: null,
        desktopOnline: null,
        lastError: revErr,
      });
    },
    (next) => {
      set({ pairingState: next });
    },
  );

  return {
    bootstrapped: false,
    capabilities: null,
    pairingState: null,
    portalUrlDraft: DEFAULT_PORTAL_URL,
    pairingStage: null,
    pairingPreview: null,
    activeTab: 'sessions',
    desktopOnline: null,
    lastError: null,

    sessions: [],
    loadingSessions: false,
    selectedSessionId: null,
    conversations: [],
    selectedConversationId: null,
    messages: [],
    streamingText: '',
    sseStatus: 'idle',
    currentPlan: null,
    browserState: null,
    browserScreenshotUrl: null,
    computerState: null,
    executionSettings: null,

    pendingTodos: [],
    pendingCursor: null,
    loadingTodos: false,
    sendingChat: false,

    async bootstrap() {
      if (get().bootstrapped) {
        return;
      }
      try {
        const plugin = await getNativeCryptoPlugin();
        const [caps, persisted] = await Promise.all([
          plugin.getCapabilities(),
          client.loadPersistedState(),
        ]);
        set({
          bootstrapped: true,
          capabilities: caps,
          pairingState: persisted,
          portalUrlDraft: persisted?.portalUrl ?? DEFAULT_PORTAL_URL,
        });

        if (Capacitor.isNativePlatform() && !appStateListener) {
          appStateListener = await App.addListener('appStateChange', ({ isActive }) => {
            if (isActive && get().pairingState) {
              void get().syncPendingTodos(false);
            }
          });
          networkListener = await Network.addListener('networkStatusChange', ({ connected }) => {
            if (connected && get().pairingState) {
              void get().syncPendingTodos(false);
            }
          });
        }

        if (persisted) {
          void get().refreshSessions();
          void get().syncPendingTodos(true);
        }
      } catch (err) {
        set({
          bootstrapped: true,
          lastError: toAppError(err),
        });
      }
    },

    async disposeLifecycleListeners() {
      activeSseHandle?.close();
      activeSseHandle = null;
      await appStateListener?.remove();
      appStateListener = null;
      await networkListener?.remove();
      networkListener = null;
      await pushTokenManager.disposeListeners();
      screenshotCache.clearAll();
    },

    setActiveTab(tab) {
      set({ activeTab: tab });
      if (tab === 'approvals' && get().pairingState) {
        void get().syncPendingTodos(false);
      } else if (tab === 'settings' && get().pairingState) {
        void get().loadExecutionSettings();
      }
    },

    setPortalUrlDraft(url) {
      set({ portalUrlDraft: url });
    },

    clearError() {
      set({ lastError: null });
    },

    setError(err) {
      set({ lastError: toAppError(err) });
    },

    async scanWithNativeCamera() {
      const plugin = await getNativeCryptoPlugin();
      const { rawUri } = await plugin.scanQrCode();
      return rawUri;
    },

    async startQrPairing(params) {
      set({ lastError: null, pairingStage: 'scanning_qr', pairingPreview: null });
      try {
        const normalizedPortal = normalizePortalOrigin(get().portalUrlDraft);
        const result = await runQrPairingFlow({
          rawQrUri: params.rawQrUri,
          portalUrl: normalizedPortal,
          mobileDeviceName: params.mobileDeviceName ?? 'CodexFlow Phone',
          onStageChange: (stage, preview) => {
            set({ pairingStage: stage, pairingPreview: preview ?? get().pairingPreview });
          },
        });
        client.setState(result);
        set({
          pairingState: result,
          pairingStage: null,
          pairingPreview: null,
          activeTab: 'sessions',
        });
        void triggerHapticLight();
        void get().refreshSessions();
        void get().syncPendingTodos(true);
      } catch (err) {
        set({
          pairingStage: null,
          lastError: toAppError(err),
        });
      }
    },

    async refreshSessions() {
      if (!client.getState()) {
        return;
      }
      set({ loadingSessions: true });
      try {
        await client.requestJson<{ status?: string }>({
          method: 'GET',
          path: '/api/system/status',
          queryPairs: [['refresh', 'true']],
        });

        const [workspaces, paged] = await Promise.all([
          client.requestJson<WorkspaceSummary[]>({
            method: 'GET',
            path: '/api/gateway/workspaces',
          }),
          client.requestJson<PagedSessionsResponse>({
            method: 'GET',
            path: '/api/gateway/sessions',
            queryPairs: [
              ['page', '1'],
              ['pageSize', '50'],
              ['includeArchived', 'false'],
            ],
          }),
        ]);

        const wsMap = new Map<string, WorkspaceSummary>();
        for (const w of workspaces) {
          wsMap.set(w.sessionId, w);
        }

        const rows: ActiveSessionRow[] = paged.items.map((s: SessionSummary) => {
          const ws = wsMap.get(s.sessionId);
          return {
            sessionId: s.sessionId,
            name: s.name || ws?.projectName || s.sessionId,
            workMode: s.workMode ?? 'Development',
            updatedLabel: s.updatedAtUtc || ws?.lastActive || '',
            isExternalWorkspace: Boolean(s.isExternalWorkspace),
            planModeActive: Boolean(s.planModeActive),
          };
        });

        // 补充在 workspaces 返回中出现但尚未在第一页的项
        for (const w of workspaces) {
          if (!rows.some((r) => r.sessionId === w.sessionId)) {
            rows.push({
              sessionId: w.sessionId,
              name: w.projectName || w.sessionId,
              workMode: 'Development',
              updatedLabel: w.lastActive,
              isExternalWorkspace: false,
              planModeActive: false,
            });
          }
        }

        set({
          sessions: rows,
          desktopOnline: true,
          loadingSessions: false,
        });
      } catch (err) {
        const appErr = toAppError(err);
        set({
          loadingSessions: false,
          desktopOnline: appErr.code.includes('desktop_offline') ? false : get().desktopOnline,
          lastError: appErr,
        });
      }
    },

    async createDefaultSession(name = '移动新建会话') {
      try {
        const created = await client.requestJson<{ sessionId: string }>({
          method: 'POST',
          path: '/api/gateway/session',
          queryPairs: [['name', name.trim() || '移动新建会话']],
          jsonBody: {},
        });
        await get().refreshSessions();
        if (created?.sessionId) {
          await get().openSession(created.sessionId);
          return created.sessionId;
        }
        return null;
      } catch (err) {
        set({ lastError: toAppError(err) });
        return null;
      }
    },

    async createDailyAssistantSession() {
      try {
        const boot = await client.requestJson<SessionBootstrapResponse>({
          method: 'POST',
          path: '/api/gateway/session/daily-assistant',
          jsonBody: {},
        });
        await get().refreshSessions();
        if (boot?.sessionId) {
          await get().openSession(boot.sessionId);
          return boot.sessionId;
        }
        return null;
      } catch (err) {
        set({ lastError: toAppError(err) });
        return null;
      }
    },

    async openSession(sessionId) {
      activeSseHandle?.close();
      activeSseHandle = null;
      set({
        selectedSessionId: sessionId,
        conversations: [],
        selectedConversationId: null,
        messages: [],
        streamingText: '',
        currentPlan: null,
        browserState: null,
        browserScreenshotUrl: null,
        computerState: null,
      });

      try {
        const convPage = await client.requestJson<PagedConversationsResponse>({
          method: 'GET',
          path: `/api/gateway/session/${encodeURIComponent(sessionId)}/conversations`,
          queryPairs: [
            ['page', '1'],
            ['pageSize', '50'],
          ],
        });
        const firstConvId = convPage.items[0]?.conversationId ?? null;
        set({
          conversations: [...convPage.items],
          selectedConversationId: firstConvId,
        });

        if (firstConvId) {
          await get().selectConversation(firstConvId);
        }

        void get().loadSessionPlan(sessionId);
        void get().refreshBrowserAndComputer(sessionId);

        activeSseHandle = subscribeSessionSse({
          relayClient: client,
          sessionId,
          onStatusChange: (st) => {
            set({ sseStatus: st });
          },
          onEvent: (frame) => {
            if (!frame.data) {
              return;
            }
            try {
              const parsed = JSON.parse(frame.data) as {
                type?: string;
                delta?: string;
                content?: string;
                conversationId?: string;
              };
              const evType = (frame.event || parsed.type || '').toLowerCase();
              if (evType === 'token' || evType === 'assistant_delta') {
                const delta = parsed.delta ?? parsed.content ?? '';
                if (delta) {
                  set({ streamingText: get().streamingText + delta });
                }
              } else if (evType === 'done' || evType === 'turn_completed') {
                set({ streamingText: '' });
                const cid = get().selectedConversationId;
                if (cid) {
                  void get().selectConversation(cid);
                }
                void get().loadSessionPlan(sessionId);
              } else if (evType.includes('approval')) {
                void get().syncPendingTodos(false);
              }
            } catch {
              // ignore unparsed non-JSON SSE heartbeat frames
            }
          },
          onError: (err) => {
            set({ lastError: toAppError(err) });
          },
        });
      } catch (err) {
        set({ lastError: toAppError(err) });
      }
    },

    closeOpenSession() {
      activeSseHandle?.close();
      activeSseHandle = null;
      set({
        selectedSessionId: null,
        selectedConversationId: null,
        messages: [],
        streamingText: '',
        sseStatus: 'idle',
      });
    },

    async selectConversation(conversationId) {
      set({ selectedConversationId: conversationId });
      try {
        const msgs = await client.requestJson<HistoryMessage[]>({
          method: 'GET',
          path: `/api/gateway/history/${encodeURIComponent(conversationId)}`,
        });
        set({ messages: msgs });
      } catch (err) {
        set({ lastError: toAppError(err) });
      }
    },

    async createNewConversation() {
      const sid = get().selectedSessionId;
      if (!sid) {
        return;
      }
      try {
        const created = await client.requestJson<ConversationSummary>({
          method: 'POST',
          path: `/api/gateway/session/${encodeURIComponent(sid)}/conversations`,
          jsonBody: {},
        });
        set({
          conversations: [created, ...get().conversations],
          selectedConversationId: created.conversationId,
          messages: [],
          streamingText: '',
        });
      } catch (err) {
        set({ lastError: toAppError(err) });
      }
    },

    async uploadAttachment(file, onProgressLabel) {
      const sid = get().selectedSessionId;
      if (!sid) {
        throw new Error('请先选择会话后再上传附件。');
      }
      const res = await uploadSessionAttachment({
        relayClient: client,
        sessionId: sid,
        file,
        onProgressLabel,
      });
      return { uploadId: res.uploadId, dto: res.attachment };
    },

    async sendMessage(params) {
      const sid = get().selectedSessionId;
      if (!sid) {
        return;
      }
      set({ sendingChat: true, lastError: null });
      try {
        const sessionRow = get().sessions.find((s) => s.sessionId === sid);
        const mode = sessionRow?.planModeActive ? 'plan' : 'execution';
        await sendChatWithIdempotency({
          relayClient: client,
          sessionId: sid,
          conversationId: get().selectedConversationId,
          conversationMode: mode,
          message: params.text,
          attachments: params.attachments,
        });
        void triggerHapticLight();
        const cid = get().selectedConversationId;
        if (cid) {
          await get().selectConversation(cid);
        }
      } catch (err) {
        set({ lastError: toAppError(err) });
      } finally {
        set({ sendingChat: false });
      }
    },

    async cancelActiveTurn() {
      const sid = get().selectedSessionId;
      if (!sid) {
        return;
      }
      try {
        await client.requestJson({
          method: 'POST',
          path: `/api/gateway/session/${encodeURIComponent(sid)}/cancel`,
          jsonBody: {},
        });
        set({ streamingText: '' });
      } catch (err) {
        set({ lastError: toAppError(err) });
      }
    },

    async loadSessionPlan(sessionId) {
      try {
        const plan = await client.requestJson<SessionPlanDto>({
          method: 'GET',
          path: `/api/gateway/session/${encodeURIComponent(sessionId)}/plan`,
        });
        set({ currentPlan: plan });
      } catch {
        // 未生成计划时静默忽略 404
      }
    },

    async submitPlanDecision(action, startExecution) {
      const sid = get().selectedSessionId;
      const plan = get().currentPlan;
      if (!sid || !plan) {
        return;
      }
      set({ sendingChat: true, lastError: null });
      try {
        const envelope = startExecution ? plan.signedEnvelope : plan.signedEnvelopeApproveOnly;
        const sa: MobileStructuredPlanAction = {
          action,
          planArtifactId: plan.planArtifactId,
          planVersion: plan.version,
          planHash: plan.markdownHash,
          startExecution,
          markdown: plan.markdown,
          signedEnvelope: envelope ?? null,
        };
        await sendChatWithIdempotency({
          relayClient: client,
          sessionId: sid,
          conversationId: get().selectedConversationId,
          conversationMode: 'plan',
          message: '',
          structuredAction: sa,
        });
        void triggerHapticLight();
        await get().loadSessionPlan(sid);
      } catch (err) {
        set({ lastError: toAppError(err) });
      } finally {
        set({ sendingChat: false });
      }
    },

    async refreshBrowserAndComputer(sessionId) {
      try {
        const [brState, compState, shot] = await Promise.all([
          client
            .requestJson<BrowserSessionStateDto>({
              method: 'GET',
              path: `/api/gateway/session/${encodeURIComponent(sessionId)}/browser`,
            })
            .catch(() => null),
          client
            .requestJson<ComputerSessionStateDto>({
              method: 'GET',
              path: `/api/gateway/session/${encodeURIComponent(sessionId)}/computer`,
            })
            .catch(() => null),
          screenshotCache
            .fetchLatestScreenshot({ relayClient: client, sessionId })
            .catch(() => null),
        ]);
        set({
          browserState: brState,
          computerState: compState,
          browserScreenshotUrl: shot?.objectUrl ?? null,
        });
      } catch {
        // ignore transient poll error
      }
    },

    async closeBrowserSession(sessionId) {
      try {
        await client.requestJson({
          method: 'POST',
          path: `/api/gateway/session/${encodeURIComponent(sessionId)}/browser/close`,
          keyPurpose: 'biometric_approval',
          promptReason: '关闭当前会话托管浏览器',
          jsonBody: {},
        });
        void triggerHapticLight();
        await get().refreshBrowserAndComputer(sessionId);
      } catch (err) {
        set({ lastError: toAppError(err) });
      }
    },

    async emergencyStopComputer(sessionId) {
      try {
        await client.requestJson({
          method: 'POST',
          path: '/api/gateway/computer/emergency-stop',
          keyPurpose: 'biometric_approval',
          promptReason: '紧急停止当前会话桌面操控',
          jsonBody: { sessionId },
        });
        void triggerHapticLight();
        await get().refreshBrowserAndComputer(sessionId);
      } catch (err) {
        set({ lastError: toAppError(err) });
      }
    },

    async syncPendingTodos(forceSnapshot = false) {
      if (!client.getState()) {
        return;
      }
      set({ loadingTodos: true });
      try {
        const res = await syncPendingSummary({
          relayClient: client,
          currentItems: get().pendingTodos,
          cursor: forceSnapshot ? null : get().pendingCursor,
        });
        set({
          pendingTodos: res.items,
          pendingCursor: res.cursor,
          desktopOnline: true,
          loadingTodos: false,
        });
      } catch (err) {
        const appErr = toAppError(err);
        set({
          loadingTodos: false,
          desktopOnline: appErr.code.includes('desktop_offline') ? false : get().desktopOnline,
          lastError: appErr,
        });
      }
    },

    async inspectCommandScript(approvalId) {
      const snap = await fetchVerifiedApprovalScript({
        relayClient: client,
        approvalId,
      });
      return snap.scriptText;
    },

    async decideTodoApproval(item, decision, note) {
      try {
        await submitVerifiedTodoDecision({
          relayClient: client,
          item,
          decision,
          note,
        });
        void triggerHapticLight();
        await get().syncPendingTodos(false);
      } catch (err) {
        set({ lastError: toAppError(err) });
      }
    },

    async loadExecutionSettings() {
      try {
        const res = await client.requestJson<ExecutionSettingsDto>({
          method: 'GET',
          path: '/api/settings/execution',
        });
        set({ executionSettings: res });
      } catch (err) {
        set({ lastError: toAppError(err) });
      }
    },

    async updatePermissionMode(mode) {
      try {
        const res = await client.requestJson<ExecutionSettingsDto>({
          method: 'PUT',
          path: '/api/settings/execution',
          jsonBody: { permissionMode: mode },
        });
        set({ executionSettings: res });
      } catch (err) {
        set({ lastError: toAppError(err) });
      }
    },

    async toggleApnsPush(enabled) {
      const current = get().pairingState;
      if (!current) {
        return;
      }
      try {
        const finalState = await pushTokenManager.setApnsEnabled({
          relayClient: client,
          enabled,
        });
        const nextState: PersistedSecurePairingState = {
          ...current,
          apnsPushEnabled: finalState,
        };
        const plugin = await getNativeCryptoPlugin();
        await plugin.saveSecureState({ stateJson: JSON.stringify(nextState) });
        client.setState(nextState);
        set({ pairingState: nextState });
      } catch (err) {
        set({ lastError: toAppError(err) });
      }
    },

    async logoutAndUnpair() {
      activeSseHandle?.close();
      activeSseHandle = null;
      await pushTokenManager.disposeListeners();
      await client.logoutAndClear();
      screenshotCache.clearAll();
      set({
        pairingState: null,
        selectedSessionId: null,
        selectedConversationId: null,
        messages: [],
        pendingTodos: [],
        pendingCursor: null,
        desktopOnline: null,
        lastError: null,
      });
    },
  };
});
