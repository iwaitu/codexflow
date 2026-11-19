import React, { useEffect, useMemo, useRef, useState } from 'react';
import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { Cpu, FileDiff, FolderTree } from 'lucide-react';
import { BusinessSkillsHub } from './components/business-skills/BusinessSkillsHub';
import { AboutModal } from './components/AboutModal';
import { ChatView } from './components/ChatView';
import { CommandPaletteModal, type CommandPaletteItem } from './components/CommandPaletteModal';
import { Composer, type WorkspaceAttachmentRequest } from './components/Composer';
import { SettingsModal, type SettingsTabKind } from './components/SettingsModal';
import { Sidebar } from './components/Sidebar';
import { StatusBar } from './components/StatusBar';
import { UpdateModal } from './components/UpdateModal';
import { WelcomeWizardModal } from './components/WelcomeWizardModal';
import { WorkspaceInspector } from './components/WorkspaceInspector';
import type { AppUpdateState } from '@shared/ipc';
import { ApiError, apiClient, type ConversationSummary } from './lib/apiClient';
import {
  formatOptimisticMessageWithAttachments,
  type ChatAttachment,
  type PendingAttachment,
} from './lib/attachments';
import { useI18n } from './lib/i18n';
import { SessionSseClient } from './lib/sseClient';
import { useAppStore, type InspectorTab } from './stores/useAppStore';
import { usePreferencesStore } from './stores/usePreferencesStore';

export const App: React.FC = () => {
  const queryClient = useQueryClient();
  const { t } = useI18n();
  const { toggleTheme, toggleLanguage, syncHostPreferences } = usePreferencesStore();
  const [workspaceError, setWorkspaceError] = useState<string | null>(null);
  const [bootstrapState, setBootstrapState] = useState<'idle' | 'loading' | 'error'>('idle');
  const [bootstrapError, setBootstrapError] = useState<string | null>(null);
  const [retryBootstrapTick, setRetryBootstrapTick] = useState(0);
  const [cancellingTurn, setCancellingTurn] = useState(false);
  const [creatingConversation, setCreatingConversation] = useState(false);
  const [primaryView, setPrimaryView] = useState<'chat' | 'business-skills'>('chat');
  const [businessHubMounted, setBusinessHubMounted] = useState(false);
  const [expandedSessionIds, setExpandedSessionIds] = useState<string[]>([]);
  const [externalAttachment, setExternalAttachment] = useState<WorkspaceAttachmentRequest | null>(null);
  const [isCommandPaletteOpen, setCommandPaletteOpen] = useState(false);
  const [isUpdateModalOpen, setUpdateModalOpen] = useState(false);
  const [appUpdateState, setAppUpdateState] = useState<AppUpdateState | null>(null);
  const [environmentIssueCount, setEnvironmentIssueCount] = useState(0);
  const [isAboutOpen, setAboutOpen] = useState(false);
  const [settingsInitialTab, setSettingsInitialTab] = useState<SettingsTabKind>('execution');
  const [chatSearchOpen, setChatSearchOpen] = useState(false);
  const [chatSearchFocusNonce, setChatSearchFocusNonce] = useState(0);
  const sseRef = useRef<SessionSseClient | null>(null);
  const prevRestartCount = useRef<number>(0);
  const wasTurnRunningRef = useRef(false);
  const pendingCrossSessionIntentRef = useRef<
    | { sessionId: string; type: 'select'; conversationId: string }
    | { sessionId: string; type: 'create' }
    | null
  >(null);
  const crossSessionHandlersRef = useRef<{
    selectConversation: (conversationId: string, targetSessionId?: string) => Promise<void>;
    createConversation: (targetSessionId?: string) => Promise<void>;
  } | null>(null);

  const {
    connection,
    sseState,
    activeSessionId,
    activeConversationId,
    instanceKey,
    activeWorkMode,
    planModeActive,
    planModeVersion,
    planModeExitRequest,
    latestTitleUpdate,
    messages,
    messagesTruncated,
    streamingContent,
    streamingThinking,
    isThinkingStreaming,
    isTurnRunning,
    turnNotice,
    toolCalls,
    pendingApprovals,
    pendingBusinessApprovals,
    currentPlan,
    jobs,
    verificationReceipts,
    workerBar,
    contextWindow,
    isSettingsOpen,
    isWizardOpen,
    wizardDismissed,
    isInspectorOpen,
    inspectorTab,
    workspaceRevision,
    setConnection,
    setSseState,
    setActiveSession,
    setActiveConversation,
    startNewConversation,
    setTurnNotice,
    setSettingsOpen,
    setWizardOpen,
    setInspectorOpen,
    setInspectorTab,
    openInspectorTab,
    appendOptimisticUserMessage,
    applySseEvent,
    reconcileSession,
    reconcileApprovals,
    removeBusinessApproval,
    setVerificationReceipt,
    setContextWindow,
    setJobs,
    setSessionPolicyState,
    dismissPlanModeExitRequest,
  } = useAppStore();

  useEffect(() => {
    syncHostPreferences();
  }, [syncHostPreferences]);

  useEffect(() => {
    let cancelled = false;
    const bridge = window.codexflow;
    const syncIssueCount = (items: Array<{ severity: string; status: string }>) => {
      if (cancelled) return;
      const missingRequired = items.filter(
        (item) => item.severity === 'required' && item.status !== 'ready',
      ).length;
      setEnvironmentIssueCount(missingRequired);
    };
    void bridge
      ?.inspectEnvironment?.(false)
      .then((report) => syncIssueCount(report.items))
      .catch(() => undefined);
    const unsubscribe = bridge?.onEnvironmentInstallEvent?.((event) => {
      if (event.report) {
        syncIssueCount(event.report.items);
      }
    });
    return () => {
      cancelled = true;
      unsubscribe?.();
    };
  }, []);

  useEffect(() => {
    let active = true;
    void window.codexflow.getBackendConnection().then((info) => {
      if (active) setConnection(info);
    });
    const unsubscribe = window.codexflow.onBackendConnectionChanged((info) => {
      setConnection(info);
    });
    return () => {
      active = false;
      unsubscribe();
    };
  }, [setConnection]);

  const auth = useMemo(() => {
    if (connection?.status === 'ready' && connection.baseUrl && connection.accessToken) {
      return { baseUrl: connection.baseUrl, accessToken: connection.accessToken };
    }
    return null;
  }, [connection]);

  const prevInstanceKey = useRef<string | null>(null);
  useEffect(() => {
    if (instanceKey && prevInstanceKey.current && instanceKey !== prevInstanceKey.current) {
      queryClient.clear();
    }
    if (instanceKey) {
      prevInstanceKey.current = instanceKey;
    }
  }, [instanceKey, queryClient]);

  const systemStatusQuery = useQuery({
    queryKey: ['system-status', instanceKey, auth?.baseUrl],
    queryFn: () => apiClient.getSystemStatus(auth!),
    enabled: Boolean(auth),
    refetchInterval: 30_000,
  });

  const executionSettingsQuery = useQuery({
    queryKey: ['execution-settings', instanceKey, auth?.baseUrl],
    queryFn: () => apiClient.getExecutionSettings(auth!),
    enabled: Boolean(auth),
  });

  const modelSettingsQuery = useQuery({
    queryKey: ['model-settings', instanceKey, auth?.baseUrl],
    queryFn: () => apiClient.getModelSettings(auth!),
    enabled: Boolean(auth),
  });

  const sessionsQuery = useQuery({
    queryKey: ['sessions', instanceKey, auth?.baseUrl],
    queryFn: () => apiClient.listSessions(auth!),
    enabled: Boolean(auth),
  });

  const conversationsQuery = useQuery({
    queryKey: ['conversations', instanceKey, auth?.baseUrl, activeSessionId],
    queryFn: () => apiClient.listConversations(auth!, activeSessionId!),
    enabled: Boolean(auth && activeSessionId),
  });

  useEffect(() => {
    if (!activeSessionId) return;
    setExpandedSessionIds((prev) =>
      prev.includes(activeSessionId) ? prev : [...prev, activeSessionId],
    );
  }, [activeSessionId]);

  const expandedOtherSessionIds = useMemo(
    () => expandedSessionIds.filter((id) => id && id !== activeSessionId),
    [expandedSessionIds, activeSessionId],
  );

  const expandedOtherConversationQueries = useQueries({
    queries: expandedOtherSessionIds.map((sid) => ({
      queryKey: ['conversations', instanceKey, auth?.baseUrl, sid],
      queryFn: () => apiClient.listConversations(auth!, sid),
      enabled: Boolean(auth && sid),
    })),
  });

  const conversationsBySession = useMemo(() => {
    const map: Record<string, ConversationSummary[]> = {};
    if (activeSessionId && conversationsQuery.data?.items) {
      map[activeSessionId] = conversationsQuery.data.items;
    }
    expandedOtherSessionIds.forEach((sid, idx) => {
      const items = expandedOtherConversationQueries[idx]?.data?.items;
      if (items) {
        map[sid] = items;
      }
    });
    return map;
  }, [
    activeSessionId,
    conversationsQuery.data?.items,
    expandedOtherSessionIds,
    expandedOtherConversationQueries,
  ]);

  const changesQuery = useQuery({
    queryKey: ['workspace-changes', instanceKey, auth?.baseUrl, activeSessionId, workspaceRevision],
    queryFn: () => apiClient.getChanges(auth!, activeSessionId!),
    enabled: Boolean(auth && activeSessionId && activeWorkMode !== 'Daily'),
  });

  useEffect(() => {
    if (activeWorkMode === 'Daily' && inspectorTab !== 'files') {
      setInspectorTab('files');
    }
  }, [activeWorkMode, inspectorTab, setInspectorTab]);

  useEffect(() => {
    if (systemStatusQuery.data && !systemStatusQuery.data.modelConfigured && !wizardDismissed) {
      setWizardOpen(true);
    }
  }, [systemStatusQuery.data, wizardDismissed, setWizardOpen]);

  // Bootstrap default workspace session once backend is ready
  useEffect(() => {
    if (!auth || activeSessionId) {
      setBootstrapState('idle');
      setBootstrapError(null);
      return;
    }
    let cancelled = false;
    setBootstrapState('loading');
    setBootstrapError(null);
    void apiClient
      .bootstrapSession(auth)
      .then((res) => {
        if (!cancelled) {
          setBootstrapState('idle');
          setActiveSession(res.sessionId);
          setSessionPolicyState({
            workMode: res.workMode === 'Daily' ? 'Daily' : 'Development',
            modeVersion: Number(res.modeVersion ?? 1),
            planModeActive: Boolean(res.planModeActive),
            planModeVersion: Number(res.planModeVersion ?? 1),
          });
          void queryClient.invalidateQueries({ queryKey: ['sessions'] });
        }
      })
      .catch((error) => {
        if (!cancelled) {
          setBootstrapState('error');
          setBootstrapError(error instanceof Error ? error.message : String(error));
        }
      });
    return () => {
      cancelled = true;
    };
  }, [auth, activeSessionId, retryBootstrapTick, setActiveSession, setSessionPolicyState, queryClient]);

  // Reconcile snapshot on initial session load or backend restart
  useEffect(() => {
    if (!auth || !activeSessionId) return;
    const currentRestart = connection?.restartCount ?? 0;
    const restarted = currentRestart !== prevRestartCount.current;
    prevRestartCount.current = currentRestart;

    void reconcileSession(restarted ? 'backend_restarted' : 'session_selected', {
      onCursorSynced: (eid, epoch) => sseRef.current?.updateCursor(eid, epoch),
    }).then(() => {
      const intent = pendingCrossSessionIntentRef.current;
      if (!intent || intent.sessionId !== activeSessionId) {
        return;
      }
      if (useAppStore.getState().activeSessionId !== activeSessionId) {
        return;
      }
      pendingCrossSessionIntentRef.current = null;
      if (intent.type === 'select') {
        void crossSessionHandlersRef.current?.selectConversation(
          intent.conversationId,
          intent.sessionId,
        );
      } else {
        void crossSessionHandlersRef.current?.createConversation(intent.sessionId);
      }
    });
  }, [auth, activeSessionId, connection?.restartCount, reconcileSession]);

  // Manage active SSE subscription per session
  useEffect(() => {
    sseRef.current?.stop();
    sseRef.current = null;

    if (!auth || !activeSessionId) {
      setSseState('closed');
      return;
    }

    const state = useAppStore.getState();
    const client = new SessionSseClient({
      baseUrl: auth.baseUrl,
      accessToken: auth.accessToken,
      sessionId: activeSessionId,
      initialLastEventId: state.asOfEventId,
      initialEpoch: state.channelEpoch,
      onEvent: (evt, meta) => applySseEvent(evt, meta),
      onReconcileRequired: (reason, info) => {
        void reconcileSession(reason, {
          expectedEpoch: info?.epoch,
          onCursorSynced: (eid, epoch) => client.updateCursor(eid, epoch),
        });
      },
      onConnectionStateChange: (s) => setSseState(s),
    });
    sseRef.current = client;
    client.start();

    return () => {
      client.stop();
      if (sseRef.current === client) {
        sseRef.current = null;
      }
    };
  }, [auth, activeSessionId, applySseEvent, reconcileSession, setSseState]);

  // 实时响应后端 updateconversationtitle SSE 事件，秒级刷新侧栏标题
  useEffect(() => {
    if (!auth || !latestTitleUpdate) return;
    const targetSessionId = latestTitleUpdate.sessionId ?? activeSessionId;
    if (!targetSessionId) return;

    const queryKey = ['conversations', instanceKey, auth.baseUrl, targetSessionId] as const;
    queryClient.setQueryData<{ items: ConversationSummary[]; total: number; page: number; pageSize: number } | undefined>(
      queryKey,
      (prev) => {
        if (!prev) return prev;
        let touched = false;
        const items = prev.items.map((item) => {
          if (item.conversationId === latestTitleUpdate.conversationId) {
            touched = true;
            return { ...item, name: latestTitleUpdate.title, updatedAtUtc: new Date().toISOString() };
          }
          return item;
        });
        return touched ? { ...prev, items } : prev;
      },
    );
    void queryClient.invalidateQueries({ queryKey });
  }, [auth, instanceKey, activeSessionId, latestTitleUpdate, queryClient]);

  // 绑定新会话 ID 或回合结束后刷新一次会话列表，确保新创建的线程与排序同步
  useEffect(() => {
    const turnFinished = wasTurnRunningRef.current && !isTurnRunning;
    wasTurnRunningRef.current = isTurnRunning;

    if (!auth || !activeSessionId) return;
    if (turnFinished || activeConversationId) {
      void queryClient.invalidateQueries({ queryKey: ['conversations', instanceKey, auth.baseUrl, activeSessionId] });
    }
  }, [auth, instanceKey, activeSessionId, activeConversationId, isTurnRunning, queryClient]);

  const refreshMetadata = () => {
    void queryClient.invalidateQueries({ queryKey: ['system-status'] });
    void queryClient.invalidateQueries({ queryKey: ['execution-settings'] });
    void queryClient.invalidateQueries({ queryKey: ['model-settings'] });
    void queryClient.invalidateQueries({ queryKey: ['sessions'] });
    if (auth && activeSessionId) {
      const targetSessionId = activeSessionId;
      const targetGeneration = useAppStore.getState().sessionGeneration;
      void apiClient
        .getContextWindow(auth, targetSessionId)
        .then((cw) => {
          const latest = useAppStore.getState();
          if (latest.activeSessionId === targetSessionId && latest.sessionGeneration === targetGeneration) {
            latest.setContextWindow(cw);
          }
        })
        .catch(() => undefined);
    }
  };

  const activeWorkspaceSession = useMemo(() => {
    if (!activeSessionId || !sessionsQuery.data?.items) return null;
    return sessionsQuery.data.items.find((s) => s.sessionId === activeSessionId) ?? null;
  }, [activeSessionId, sessionsQuery.data]);

  const activeConversationName = useMemo(() => {
    if (!activeConversationId || !activeSessionId) return null;
    const list = conversationsBySession[activeSessionId] ?? conversationsQuery.data?.items ?? [];
    return list.find((c) => c.conversationId === activeConversationId)?.name ?? null;
  }, [activeConversationId, activeSessionId, conversationsBySession, conversationsQuery.data]);

  const activeWorkspacePath = activeWorkspaceSession?.workspacePath ?? null;

  const toggleInspectorTab = (tab: InspectorTab) => {
    if (isInspectorOpen && inspectorTab === tab) {
      setInspectorOpen(false);
    } else {
      openInspectorTab(tab);
    }
  };

  const refreshSessionJobs = async () => {
    if (!auth || !activeSessionId) return;
    const sid = activeSessionId;
    const gen = useAppStore.getState().sessionGeneration;
    const updatedJobs = await apiClient.listJobs(auth, sid);
    const cur = useAppStore.getState();
    if (cur.activeSessionId === sid && cur.sessionGeneration === gen) {
      setJobs(updatedJobs);
    }
  };

  const handleSend = async (text: string, pendingAttachments: PendingAttachment[] = []) => {
    if (!auth || !activeSessionId) return;
    setTurnNotice(null);

    const resolvedAttachments: ChatAttachment[] = [];
    for (const item of pendingAttachments) {
      if (item.workspaceRelativePath) {
        resolvedAttachments.push({
          name: item.name,
          relativePath: item.workspaceRelativePath,
          sizeBytes: item.sizeBytes,
          contentType: item.contentType,
          isWorkspaceReference: true,
        });
      } else {
        const uploaded = await apiClient.uploadAttachment(auth, activeSessionId, item.file);
        resolvedAttachments.push({
          name: uploaded.name,
          relativePath: uploaded.relativePath,
          sizeBytes: uploaded.sizeBytes,
          contentType: uploaded.contentType,
          isWorkspaceReference: false,
        });
      }
    }

    appendOptimisticUserMessage(formatOptimisticMessageWithAttachments(text, resolvedAttachments));
    try {
      await apiClient.sendChat(auth, {
        sessionId: activeSessionId,
        message: text,
        conversationId: activeConversationId ?? undefined,
        conversationMode: activeConversationId ? 'continue-current' : 'reuse-master',
        attachments: resolvedAttachments.length > 0 ? resolvedAttachments : undefined,
      });
      void queryClient.invalidateQueries({ queryKey: ['conversations', instanceKey, auth.baseUrl, activeSessionId] });
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        setTurnNotice(`当前会话正在执行中，请等待或点击“停止”后重试 (${error.message})`);
      } else {
        setTurnNotice(error instanceof Error ? error.message : String(error));
      }
      await reconcileSession('send_failed', {
        onCursorSynced: (eid, epoch) => sseRef.current?.updateCursor(eid, epoch),
      });
      throw error;
    }
  };

  const handleCancel = async () => {
    if (!auth || !activeSessionId || cancellingTurn) return;
    setCancellingTurn(true);
    try {
      await apiClient.cancelTurn(auth, activeSessionId);
    } catch (error) {
      setTurnNotice(`停止请求失败：${error instanceof Error ? error.message : String(error)}`);
    } finally {
      setCancellingTurn(false);
    }
  };

  const handleCreateSession = async (
    workMode: 'daily' | 'development' = 'daily',
    summary?: string,
  ) => {
    if (!auth) {
      const msg = '后端服务尚未就绪，无法创建新会话。';
      setWorkspaceError(msg);
      setTurnNotice(msg);
      return;
    }
    setWorkspaceError(null);
    try {
      const labelPrefix = workMode === 'daily' ? '日常对话' : '开发工作区';
      const created = await apiClient.createSession(
        auth,
        `${labelPrefix} ${new Date().toLocaleTimeString()}`,
        workMode,
      );
      const nextWorkMode = created.workMode === 'Daily' ? 'Daily' : workMode === 'daily' ? 'Daily' : 'Development';
      setPrimaryView('chat');
      setActiveSession(created.sessionId);
      setSessionPolicyState({
        workMode: nextWorkMode,
        modeVersion: Number(created.modeVersion ?? 1),
        planModeActive: Boolean(created.planModeActive),
        planModeVersion: Number(created.planModeVersion ?? 1),
      });
      setTurnNotice(
        summary
          ? `已创建新 ${nextWorkMode === 'Daily' ? '日常模式' : '开发模式'} 会话，摘要参考：${summary}`
          : `已新建并切换到 ${nextWorkMode === 'Daily' ? '日常模式' : '开发模式'} 会话。`,
      );
      void queryClient.invalidateQueries({ queryKey: ['sessions'] });
    } catch (error) {
      const msg = error instanceof Error ? error.message : String(error);
      setWorkspaceError(`新建会话失败：${msg}`);
      setTurnNotice(`新建会话失败：${msg}`);
    }
  };

  const handleOpenDailyAssistant = async () => {
    if (!auth) {
      const msg = '后端服务尚未就绪，无法打开日常助手。';
      setWorkspaceError(msg);
      setTurnNotice(msg);
      return;
    }
    setWorkspaceError(null);
    try {
      const res = await apiClient.getOrCreateDailyAssistant(auth);
      const nextWorkMode = res.workMode === 'Daily' ? 'Daily' : 'Development';
      const isSameSession = res.sessionId === useAppStore.getState().activeSessionId;
      setPrimaryView('chat');
      if (!isSameSession) {
        setActiveSession(res.sessionId);
      }
      setSessionPolicyState({
        workMode: nextWorkMode,
        modeVersion: Number(res.modeVersion ?? 1),
        planModeActive: Boolean(res.planModeActive),
        planModeVersion: Number(res.planModeVersion ?? 1),
      });
      setTurnNotice(
        isSameSession
          ? '当前已在「日常助手」（日常模式）工作空间。'
          : '已切换到「日常助手」（日常模式）：支持文档整理与受控 Shell 审批。',
      );
      void queryClient.invalidateQueries({ queryKey: ['sessions'] });
    } catch (error) {
      const msg = error instanceof Error ? error.message : String(error);
      setWorkspaceError(`打开日常助手失败：${msg}`);
      setTurnNotice(`打开日常助手失败：${msg}`);
    }
  };

  const handleEnterPlanMode = async () => {
    if (!auth || !activeSessionId) return;
    try {
      const res = await apiClient.enterPlanMode(auth, activeSessionId, 'user_slash_command');
      setSessionPolicyState({
        workMode: res.workMode === 'Daily' ? 'Daily' : 'Development',
        modeVersion: Number(res.modeVersion ?? 1),
        planModeActive: res.planModeActive,
        planModeVersion: Number(res.planModeVersion ?? 1),
      });
      setTurnNotice('已进入只读 Plan Mode（输入 /plan off 可退出）。');
    } catch (error) {
      const msg = error instanceof Error ? error.message : String(error);
      setTurnNotice(`进入 Plan Mode 失败：${msg}`);
    }
  };

  const handleExitPlanMode = async () => {
    if (!activeSessionId) return;
    const res = await window.codexflow.setSessionPlanMode({
      sessionId: activeSessionId,
      active: false,
      expectedPlanModeVersion: planModeVersion,
      reason: 'user_confirmed_exit',
    });
    if (res.ok && res.data) {
      setSessionPolicyState({
        workMode: res.data.workMode === 'Daily' ? 'Daily' : 'Development',
        modeVersion: Number(res.data.modeVersion ?? 1),
        planModeActive: res.data.planModeActive,
        planModeVersion: Number(res.data.planModeVersion ?? 1),
      });
      setTurnNotice('已退出 Plan Mode，工具权限已恢复。');
      return;
    }
    if (res.status === 409) {
      await reconcileSession('plan_mode_version_conflict', {
        onCursorSynced: (eid, epoch) => sseRef.current?.updateCursor(eid, epoch),
      });
      setTurnNotice('Plan Mode 版本状态已变更，已自动同步最新状态，请再次确认。');
      return;
    }
    setTurnNotice(res.errorMessage || `无法退出 Plan Mode (${res.status})`);
  };

  const handleCompactContext = async (focus?: string) => {
    if (!auth || !activeSessionId) {
      setTurnNotice('当前没有活动会话，无法压缩上下文。');
      return;
    }
    if (isTurnRunning) {
      setTurnNotice('当前回合正在执行中，请等待完成或取消后再压缩上下文。');
      return;
    }
    setTurnNotice('正在压缩当前会话上下文，请稍候…');
    try {
      const res = await apiClient.compactContext(auth, activeSessionId, {
        conversationId: activeConversationId ?? undefined,
        focus: focus?.trim() || undefined,
      });
      if (res.contextWindow) {
        setContextWindow(res.contextWindow);
      }
      if (res.compressed) {
        setTurnNotice(
          `上下文已压缩：归档 ${res.compressedTurnCount} 条对话，估算占用 ${res.estimatedTokensBefore.toLocaleString()} → ${res.estimatedTokensAfter.toLocaleString()} tokens。`,
        );
      } else {
        setTurnNotice('当前没有可压缩的新增对话上下文。');
      }
    } catch (error) {
      const msg = error instanceof Error ? error.message : String(error);
      setTurnNotice(`压缩上下文失败：${msg}`);
    }
  };

  const handleSlashCommand = async (action: {
    kind: 'plan-on' | 'plan-off' | 'compact' | 'daily' | 'dev';
    argument?: string;
  }) => {
    if (action.kind === 'plan-on') {
      await handleEnterPlanMode();
      return;
    }
    if (action.kind === 'plan-off') {
      await handleExitPlanMode();
      return;
    }
    if (action.kind === 'compact') {
      await handleCompactContext(action.argument);
      return;
    }
    if (action.kind === 'daily') {
      await handleCreateSession('daily', action.argument);
      return;
    }
    await handleCreateSession('development', action.argument);
  };

  const handleCreateConversation = async (targetSessionId?: string) => {
    if (!auth || isTurnRunning || creatingConversation) return;
    const sid = targetSessionId ?? activeSessionId;
    if (!sid) return;
    setPrimaryView('chat');
    if (sid !== activeSessionId) {
      pendingCrossSessionIntentRef.current = { sessionId: sid, type: 'create' };
      setActiveSession(sid);
      return;
    }
    setWorkspaceError(null);
    setCreatingConversation(true);
    try {
      const created = await apiClient.createConversation(auth, sid);
      startNewConversation(created.conversationId);
      const queryKey = ['conversations', instanceKey, auth.baseUrl, sid] as const;
      queryClient.setQueryData<{ items: ConversationSummary[]; total: number; page: number; pageSize: number } | undefined>(
        queryKey,
        (prev) => {
          if (!prev) {
            return { items: [created], total: 1, page: 1, pageSize: 50 };
          }
          const exists = prev.items.some((item) => item.conversationId === created.conversationId);
          const remaining = prev.items.filter((item) => item.conversationId !== created.conversationId);
          return {
            ...prev,
            items: [created, ...remaining],
            total: exists ? prev.total : prev.total + 1,
          };
        },
      );
      void queryClient.invalidateQueries({ queryKey });
    } catch (error) {
      setWorkspaceError(error instanceof Error ? error.message : String(error));
    } finally {
      setCreatingConversation(false);
    }
  };

  const handleOpenFolder = async () => {
    setWorkspaceError(null);
    const folder = await window.codexflow.pickFolder();
    if (!folder) return;
    const res = await window.codexflow.openLocalWorkspace({ path: folder });
    if (!res.ok || !res.data) {
      setWorkspaceError(res.errorMessage || `无法打开该目录 (${res.status})`);
      return;
    }
    setActiveSession(res.data.sessionId);
    void queryClient.invalidateQueries({ queryKey: ['sessions'] });
  };

  const handleSelectConversation = async (conversationId: string, targetSessionId?: string) => {
    if (!auth) return;
    const sid = targetSessionId ?? activeSessionId;
    if (!sid) return;
    setPrimaryView('chat');
    if (sid !== activeSessionId) {
      pendingCrossSessionIntentRef.current = {
        sessionId: sid,
        type: 'select',
        conversationId,
      };
      setActiveSession(sid);
      return;
    }
    const gen = useAppStore.getState().sessionGeneration;
    setActiveConversation(conversationId);
    try {
      const history = await apiClient.getHistory(auth, conversationId);
      const cur = useAppStore.getState();
      if (cur.activeSessionId !== sid || cur.sessionGeneration !== gen || cur.activeConversationId !== conversationId) {
        return;
      }
      useAppStore.setState({ messages: history, messagesTruncated: false });
      const cw = await apiClient.getContextWindow(auth, sid).catch(() => null);
      const afterCw = useAppStore.getState();
      if (afterCw.activeSessionId === sid && afterCw.sessionGeneration === gen) {
        setContextWindow(cw);
      }
    } catch (error) {
      setTurnNotice(`加载历史对话失败：${error instanceof Error ? error.message : String(error)}`);
    }
  };

  crossSessionHandlersRef.current = {
    selectConversation: handleSelectConversation,
    createConversation: handleCreateConversation,
  };

  const handleVerifyJob = async (jobId: string) => {
    if (!auth || !activeSessionId) return;
    const sid = activeSessionId;
    const gen = useAppStore.getState().sessionGeneration;
    try {
      const receipt = await apiClient.verifyArtifact(auth, sid, jobId);
      const cur = useAppStore.getState();
      if (cur.activeSessionId !== sid || cur.sessionGeneration !== gen) {
        return;
      }
      setVerificationReceipt(jobId, receipt);
      const updatedJobs = await apiClient.listJobs(auth, sid);
      const afterJobs = useAppStore.getState();
      if (afterJobs.activeSessionId === sid && afterJobs.sessionGeneration === gen) {
        setJobs(updatedJobs);
      }
    } catch (error) {
      setTurnNotice(`触发验收失败：${error instanceof Error ? error.message : String(error)}`);
    }
  };

  const controlPlaneAvailable =
    connection?.controlPlaneAvailable ?? systemStatusQuery.data?.controlPlaneAvailable ?? true;

  const openSettingsTab = (tab: SettingsTabKind = 'execution') => {
    setSettingsInitialTab(tab);
    setSettingsOpen(true);
  };

  const commandActionsRef = useRef<{
    newConversation: () => void;
    newWorkspace: () => void;
    openFolder: () => void;
    openSettings: (tab?: SettingsTabKind) => void;
    openUpdates: () => void;
    openAbout: () => void;
    openCommandPalette: () => void;
    toggleTheme: () => void;
    toggleInspector: (tab: InspectorTab) => void;
    switchView: (view: 'chat' | 'business-skills') => void;
  } | null>(null);

  commandActionsRef.current = {
    newConversation: () => void handleCreateConversation(),
    newWorkspace: () => void handleCreateSession(),
    openFolder: () => void handleOpenFolder(),
    openSettings: (tab = 'execution') => openSettingsTab(tab),
    openUpdates: () => setUpdateModalOpen(true),
    openAbout: () => setAboutOpen(true),
    openCommandPalette: () => setCommandPaletteOpen(true),
    toggleTheme: () => toggleTheme(),
    toggleInspector: (tab) => {
      setPrimaryView('chat');
      toggleInspectorTab(tab);
    },
    switchView: (view) => setPrimaryView(view),
  };

  useEffect(() => {
    let active = true;
    const bridge = window.codexflow;
    void bridge?.getUpdateState?.().then((state) => {
      if (active && state) {
        setAppUpdateState(state);
      }
    });
    const unsubscribe = bridge?.onUpdateStateChanged?.((state) => {
      if (active) {
        setAppUpdateState(state);
      }
    });
    return () => {
      active = false;
      unsubscribe?.();
    };
  }, []);

  useEffect(() => {
    const unsubscribe = window.codexflow?.onCommandAction?.((action) => {
      const actions = commandActionsRef.current;
      if (!actions) return;
      switch (action) {
        case 'new-conversation':
          actions.newConversation();
          return;
        case 'new-workspace':
          actions.newWorkspace();
          return;
        case 'open-folder':
          actions.openFolder();
          return;
        case 'open-settings':
          actions.openSettings('execution');
          return;
        case 'open-environment':
          actions.openSettings('environment');
          return;
        case 'open-updates':
          actions.openUpdates();
          return;
        case 'open-about':
          actions.openAbout();
          return;
        case 'open-command-palette':
          actions.openCommandPalette();
          return;
        case 'toggle-theme':
          actions.toggleTheme();
          return;
        case 'toggle-inspector-files':
          actions.toggleInspector('files');
          return;
        case 'toggle-inspector-changes':
          actions.toggleInspector('changes');
          return;
        case 'toggle-inspector-jobs':
          actions.toggleInspector('jobs');
          return;
        case 'switch-view-chat':
          actions.switchView('chat');
          return;
        case 'switch-view-skills':
          actions.switchView('business-skills');
          return;
      }
    });
    return () => {
      unsubscribe?.();
    };
  }, []);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.isComposing) return;
      const mod = event.ctrlKey || event.metaKey;
      if (!mod) return;
      const key = event.key.toLowerCase();
      const actions = commandActionsRef.current;
      if (!actions) return;

      if (key === 'k' && !event.shiftKey && !event.altKey) {
        event.preventDefault();
        setCommandPaletteOpen((prev) => !prev);
        return;
      }
      if (key === 'f' && !event.shiftKey && !event.altKey) {
        if (primaryView === 'chat' && !isSettingsOpen && !isCommandPaletteOpen) {
          event.preventDefault();
          setChatSearchOpen(true);
          setChatSearchFocusNonce((n) => n + 1);
        }
        return;
      }
      if (key === ',' && !event.shiftKey && !event.altKey) {
        event.preventDefault();
        actions.openSettings('execution');
        return;
      }
      if (key === 'n' && !event.shiftKey && !event.altKey) {
        event.preventDefault();
        actions.newConversation();
        return;
      }
      if (key === 'o' && !event.shiftKey && !event.altKey && controlPlaneAvailable) {
        event.preventDefault();
        actions.openFolder();
        return;
      }
      if (event.shiftKey && !event.altKey && activeSessionId) {
        if (key === 'f') {
          event.preventDefault();
          actions.toggleInspector('files');
        } else if (key === 'g') {
          event.preventDefault();
          actions.toggleInspector('changes');
        } else if (key === 'j') {
          event.preventDefault();
          actions.toggleInspector('jobs');
        }
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [activeSessionId, controlPlaneAvailable, primaryView, isSettingsOpen, isCommandPaletteOpen]);

  const commandPaletteItems = useMemo<CommandPaletteItem[]>(
    () => [
      {
        id: 'search-in-chat',
        group: t('palette.group.workspace'),
        label: '在当前会话中搜索',
        keywords: 'find search ctrl+f 搜索 查找 会话',
        shortcut: 'Ctrl/Cmd+F',
        disabled: !activeSessionId,
        onRun: () => {
          setPrimaryView('chat');
          setChatSearchOpen(true);
          setChatSearchFocusNonce((n) => n + 1);
        },
      },
      {
        id: 'compact-context',
        group: t('palette.group.workspace'),
        label: '压缩当前会话上下文 (/compact)',
        keywords: 'compact compress context 压缩 上下文 摘要',
        disabled: !auth || !activeSessionId || isTurnRunning,
        onRun: () => void handleCompactContext(),
      },
      {
        id: 'new-conversation',
        group: t('palette.group.workspace'),
        label: t('palette.cmd.newConversation'),
        keywords: 'new chat thread 新建对话 会话',
        shortcut: 'Ctrl/Cmd+N',
        disabled: !auth || !activeSessionId || isTurnRunning || creatingConversation,
        onRun: () => void handleCreateConversation(),
      },
      {
        id: 'new-workspace',
        group: t('palette.group.workspace'),
        label: t('palette.cmd.newWorkspace'),
        keywords: 'new session workspace 新建工作区',
        disabled: !auth,
        onRun: () => void handleCreateSession(),
      },
      {
        id: 'open-folder',
        group: t('palette.group.workspace'),
        label: t('palette.cmd.openFolder'),
        keywords: 'open folder directory workspace 打开文件夹 目录',
        shortcut: 'Ctrl/Cmd+O',
        disabled: !controlPlaneAvailable,
        onRun: () => void handleOpenFolder(),
      },
      {
        id: 'view-chat',
        group: t('palette.group.view'),
        label: t('palette.cmd.viewChat'),
        keywords: 'chat view workspace 对话 工作区',
        onRun: () => setPrimaryView('chat'),
      },
      {
        id: 'view-skills',
        group: t('palette.group.view'),
        label: t('palette.cmd.viewSkills'),
        keywords: 'plugins skills hub 插件 技能',
        onRun: () => setPrimaryView('business-skills'),
      },
      {
        id: 'toggle-files',
        group: t('palette.group.view'),
        label: t('palette.cmd.toggleFiles'),
        keywords: 'files tree inspector 文件树',
        shortcut: 'Ctrl/Cmd+Shift+F',
        disabled: !activeSessionId,
        onRun: () => {
          setPrimaryView('chat');
          toggleInspectorTab('files');
        },
      },
      {
        id: 'toggle-changes',
        group: t('palette.group.view'),
        label: t('palette.cmd.toggleChanges'),
        keywords: 'git diff changes 变更 差异',
        shortcut: 'Ctrl/Cmd+Shift+G',
        disabled: !activeSessionId,
        onRun: () => {
          setPrimaryView('chat');
          toggleInspectorTab('changes');
        },
      },
      {
        id: 'toggle-jobs',
        group: t('palette.group.view'),
        label: t('palette.cmd.toggleJobs'),
        keywords: 'background jobs 作业 任务',
        shortcut: 'Ctrl/Cmd+Shift+J',
        disabled: !activeSessionId,
        onRun: () => {
          setPrimaryView('chat');
          toggleInspectorTab('jobs');
        },
      },
      {
        id: 'toggle-theme',
        group: t('palette.group.preferences'),
        label: t('palette.cmd.toggleTheme'),
        keywords: 'theme dark light 主题 深色 浅色',
        onRun: () => toggleTheme(),
      },
      {
        id: 'toggle-language',
        group: t('palette.group.preferences'),
        label: t('palette.cmd.toggleLanguage'),
        keywords: 'language i18n chinese english 语言 中文 英文',
        onRun: () => toggleLanguage(),
      },
      {
        id: 'open-settings',
        group: t('palette.group.system'),
        label: t('palette.cmd.openSettings'),
        keywords: 'settings preferences 设置 配置',
        shortcut: 'Ctrl/Cmd+,',
        onRun: () => openSettingsTab('execution'),
      },
      {
        id: 'open-environment',
        group: t('palette.group.system'),
        label: t('env.title'),
        keywords: 'environment dependencies doctor install dotnet git node python docker 环境 依赖 检测 安装',
        onRun: () => openSettingsTab('environment'),
      },
      {
        id: 'check-updates',
        group: t('palette.group.system'),
        label: t('palette.cmd.checkUpdates'),
        keywords: 'update version release 检查更新 版本',
        onRun: () => setUpdateModalOpen(true),
      },
      {
        id: 'restart-backend',
        group: t('palette.group.system'),
        label: t('palette.cmd.restartBackend'),
        keywords: 'restart host backend 重启后端',
        disabled: connection?.mode !== 'managed',
        onRun: () => void window.codexflow.restartBackend(),
      },
      {
        id: 'about',
        group: t('palette.group.system'),
        label: t('palette.cmd.about'),
        keywords: 'about version author github 关于 版本 作者 项目地址',
        onRun: () => setAboutOpen(true),
      },
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [t, auth, activeSessionId, isTurnRunning, creatingConversation, controlPlaneAvailable, connection?.mode],
  );

  return (
    <div className="flex h-screen w-screen flex-col bg-slate-950 text-slate-100">
      <div className="flex min-h-0 flex-1">
        <Sidebar
          sessions={sessionsQuery.data?.items ?? []}
          conversations={conversationsQuery.data?.items ?? []}
          conversationsBySession={conversationsBySession}
          expandedSessionIds={expandedSessionIds}
          onToggleExpandSession={(sid) =>
            setExpandedSessionIds((prev) =>
              prev.includes(sid) ? prev.filter((id) => id !== sid) : [...prev, sid],
            )
          }
          activeSessionId={activeSessionId}
          activeConversationId={activeConversationId}
          activePrimaryView={primaryView}
          onSelectPrimaryView={(view) => {
            if (view === 'business-skills') setBusinessHubMounted(true);
            setPrimaryView(view);
          }}
          controlPlaneAvailable={controlPlaneAvailable}
          isTurnRunning={isTurnRunning}
          creatingConversation={creatingConversation}
          onSelectSession={(sid) => {
            setPrimaryView('chat');
            setActiveSession(sid);
          }}
          onSelectConversation={(cid, sid) => void handleSelectConversation(cid, sid)}
          onCreateConversation={handleCreateConversation}
          onCreateSession={() => handleCreateSession('daily')}
          onOpenDailyAssistant={handleOpenDailyAssistant}
          onOpenFolder={handleOpenFolder}
          onRenameSession={async (sid, name) => {
            if (!auth) return;
            setWorkspaceError(null);
            try {
              await apiClient.updateSession(auth, sid, { name });
              void queryClient.invalidateQueries({ queryKey: ['sessions'] });
            } catch (error) {
              setWorkspaceError(`重命名失败：${error instanceof Error ? error.message : String(error)}`);
              throw error;
            }
          }}
          onArchiveSession={async (sid) => {
            if (!auth) return;
            setWorkspaceError(null);
            try {
              await apiClient.updateSession(auth, sid, { isArchived: true });
              if (activeSessionId === sid) setActiveSession(null);
              void queryClient.invalidateQueries({ queryKey: ['sessions'] });
            } catch (error) {
              setWorkspaceError(`归档失败：${error instanceof Error ? error.message : String(error)}`);
              throw error;
            }
          }}
          onDeleteSession={async (sid) => {
            if (!auth) return;
            setWorkspaceError(null);
            try {
              await apiClient.deleteSession(auth, sid);
              if (activeSessionId === sid) setActiveSession(null);
              void queryClient.invalidateQueries({ queryKey: ['sessions'] });
            } catch (error) {
              setWorkspaceError(`删除失败：${error instanceof Error ? error.message : String(error)}`);
              throw error;
            }
          }}
          onRenameConversation={async (cid, name) => {
            if (!auth) return;
            setWorkspaceError(null);
            try {
              await apiClient.updateConversation(auth, cid, { name });
              void queryClient.invalidateQueries({ queryKey: ['conversations'] });
            } catch (error) {
              setWorkspaceError(`重命名对话失败：${error instanceof Error ? error.message : String(error)}`);
              throw error;
            }
          }}
          onDeleteConversation={async (cid) => {
            if (!auth) return;
            setWorkspaceError(null);
            try {
              await apiClient.deleteConversation(auth, cid);
              if (activeConversationId === cid) setActiveConversation(null);
              void queryClient.invalidateQueries({ queryKey: ['conversations'] });
            } catch (error) {
              setWorkspaceError(`删除对话失败：${error instanceof Error ? error.message : String(error)}`);
              throw error;
            }
          }}
          onOpenSettings={() => openSettingsTab('execution')}
          workspaceError={workspaceError}
        />

        <main className="flex min-w-0 flex-1 flex-col bg-slate-900/40">
          {(primaryView === 'business-skills' || businessHubMounted) && (
            <div className={primaryView === 'business-skills' ? 'flex min-h-0 flex-1 flex-col' : 'hidden'}>
              <BusinessSkillsHub
                auth={auth}
                instanceKey={instanceKey}
                activeSessionId={activeSessionId}
                controlPlaneAvailable={controlPlaneAvailable}
                modelConfigured={Boolean(
                  systemStatusQuery.data?.modelConfigured ?? modelSettingsQuery.data?.model,
                )}
              />
            </div>
          )}
          <div className={primaryView === 'business-skills' ? 'hidden' : 'flex min-h-0 flex-1 flex-col'}>
              <div className="flex h-11 shrink-0 items-center justify-between border-b border-slate-800/80 bg-slate-950/60 px-4 text-xs">
                <div className="flex min-w-0 items-center gap-2">
                  <span className="truncate font-medium text-slate-200">
                    {activeWorkspaceSession?.name || t('workspace.unselected')}
                  </span>
                  {activeConversationName && (
                    <span className="truncate text-slate-400" title={activeConversationName}>
                      / {activeConversationName}
                    </span>
                  )}
                  <span
                    className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium ${
                      activeWorkMode === 'Daily'
                        ? 'bg-emerald-500/15 text-emerald-300'
                        : 'bg-indigo-500/15 text-indigo-300'
                    }`}
                  >
                    {activeWorkMode === 'Daily' ? '日常模式' : '开发模式'}
                  </span>
                  {planModeActive && (
                    <span className="shrink-0 rounded border border-amber-500/40 bg-amber-500/15 px-1.5 py-0.5 text-[10px] font-medium text-amber-200">
                      Plan Mode
                    </span>
                  )}
                  {activeWorkspacePath && !(activeWorkMode === 'Daily' && !activeWorkspaceSession?.isExternalWorkspace) && (
                    <span className="hidden truncate font-mono text-[11px] text-slate-500 md:inline" title={activeWorkspacePath}>
                      {activeWorkspacePath}
                    </span>
                  )}
                </div>
                <div className="flex shrink-0 items-center gap-1.5">
                  <button
                    type="button"
                    disabled={!activeSessionId}
                    onClick={() => toggleInspectorTab('files')}
                    aria-label={t('workspace.filesAria')}
                    className={`inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-[11px] font-medium disabled:opacity-40 ${
                      isInspectorOpen && inspectorTab === 'files'
                        ? 'border-indigo-500/60 bg-indigo-500/20 text-indigo-200'
                        : 'border-slate-800 bg-slate-900 text-slate-300 hover:bg-slate-800'
                    }`}
                  >
                    <FolderTree className="h-3.5 w-3.5" />
                    <span>{t('workspace.files')}</span>
                  </button>
                  {activeWorkMode !== 'Daily' && (
                    <>
                      <button
                        type="button"
                        disabled={!activeSessionId}
                        onClick={() => toggleInspectorTab('changes')}
                        aria-label={t('workspace.changesAria')}
                        className={`inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-[11px] font-medium disabled:opacity-40 ${
                          isInspectorOpen && inspectorTab === 'changes'
                            ? 'border-indigo-500/60 bg-indigo-500/20 text-indigo-200'
                            : 'border-slate-800 bg-slate-900 text-slate-300 hover:bg-slate-800'
                        }`}
                      >
                        <FileDiff className="h-3.5 w-3.5" />
                        <span>
                          {t('workspace.changes')}
                          {changesQuery.data?.files?.length ? ` (${changesQuery.data.files.length})` : ''}
                        </span>
                      </button>
                      <button
                        type="button"
                        disabled={!activeSessionId}
                        onClick={() => toggleInspectorTab('jobs')}
                        aria-label={t('workspace.jobsAria')}
                        className={`inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-[11px] font-medium disabled:opacity-40 ${
                          isInspectorOpen && inspectorTab === 'jobs'
                            ? 'border-indigo-500/60 bg-indigo-500/20 text-indigo-200'
                            : 'border-slate-800 bg-slate-900 text-slate-300 hover:bg-slate-800'
                        }`}
                      >
                        <Cpu className="h-3.5 w-3.5" />
                        <span>
                          {t('workspace.jobs')}
                          {jobs.length > 0 ? ` (${jobs.length})` : ''}
                        </span>
                      </button>
                    </>
                  )}
                </div>
              </div>

              <ChatView
                sessionId={activeSessionId}
                conversationId={activeConversationId}
                messages={messages}
                messagesTruncated={messagesTruncated}
                streamingContent={streamingContent}
                streamingThinking={streamingThinking}
                isThinkingStreaming={isThinkingStreaming}
                isTurnRunning={isTurnRunning}
                toolCalls={toolCalls}
                pendingApprovals={pendingApprovals}
                pendingBusinessApprovals={pendingBusinessApprovals}
                currentPlan={currentPlan}
                jobs={jobs}
                verificationReceipts={verificationReceipts}
                workerBar={workerBar}
                executionBackend={systemStatusQuery.data?.executionBackend ?? executionSettingsQuery.data?.backend ?? null}
                controlPlaneAvailable={controlPlaneAvailable}
                workMode={activeWorkMode}
                planModeActive={planModeActive}
                planModeExitRequest={planModeExitRequest}
                bootstrapState={bootstrapState}
                bootstrapError={bootstrapError}
                sseState={sseState}
                onRetryBootstrap={() => setRetryBootstrapTick((n) => n + 1)}
                onOpenDailyAssistant={handleOpenDailyAssistant}
                onCreateDailySession={() => handleCreateSession('daily')}
                onOpenFolder={handleOpenFolder}
                onApprovalResolved={() => void reconcileApprovals()}
                onBusinessApprovalResolved={(approvalId) => removeBusinessApproval(approvalId)}
                onPlanActionSubmitted={() =>
                  void reconcileSession('plan_action', {
                    onCursorSynced: (eid, epoch) => sseRef.current?.updateCursor(eid, epoch),
                  })
                }
                onConfirmExitPlanMode={handleExitPlanMode}
                onDismissPlanModeExitRequest={dismissPlanModeExitRequest}
                onVerifyJob={handleVerifyJob}
                onSwitchManaged={() => void window.codexflow.switchToManaged()}
                searchOpen={chatSearchOpen}
                searchFocusNonce={chatSearchFocusNonce}
                onCloseSearch={() => setChatSearchOpen(false)}
              />

              <Composer
                disabled={!auth || !activeSessionId}
                disabledReason={
                  !auth
                    ? '后端服务连接中或未就绪，请等待连接恢复...'
                    : !activeSessionId
                      ? '请先在左侧选择工作区，或点击「日常助手」「打开本地目录」开始对话...'
                      : undefined
                }
                isTurnRunning={isTurnRunning}
                cancelling={cancellingTurn}
                turnNotice={turnNotice}
                workspacePath={activeWorkspacePath}
                supportsMultimodal={Boolean(modelSettingsQuery.data?.supportsMultimodal)}
                externalAttachment={externalAttachment}
                workMode={activeWorkMode}
                planModeActive={planModeActive}
                controlPlaneAvailable={controlPlaneAvailable}
                onSend={handleSend}
                onCancel={handleCancel}
                onSlashCommand={handleSlashCommand}
                onOpenDailyAssistant={handleOpenDailyAssistant}
              />
          </div>
        </main>

        <WorkspaceInspector
          open={isInspectorOpen}
          activeTab={inspectorTab}
          auth={auth}
          sessionId={activeSessionId}
          workspaceRevision={workspaceRevision}
          changes={changesQuery.data}
          changesLoading={changesQuery.isLoading}
          changesFetching={changesQuery.isFetching}
          changesError={changesQuery.error instanceof Error ? changesQuery.error.message : changesQuery.error ? String(changesQuery.error) : null}
          jobs={jobs}
          verificationReceipts={verificationReceipts}
          executionBackend={systemStatusQuery.data?.executionBackend ?? executionSettingsQuery.data?.backend ?? null}
          onSelectTab={setInspectorTab}
          onClose={() => setInspectorOpen(false)}
          onRefreshChanges={() => void changesQuery.refetch()}
          onAttachWorkspaceFile={(file) =>
            setExternalAttachment({
              nonce: Date.now() + Math.random(),
              name: file.name,
              workspaceRelativePath: file.path,
              sizeBytes: file.sizeBytes,
            })
          }
          onVerifyJob={handleVerifyJob}
          onJobsMutated={refreshSessionJobs}
        />
      </div>

      <StatusBar
        connection={connection}
        sseState={sseState}
        systemStatus={systemStatusQuery.data ?? null}
        contextWindow={contextWindow}
        updateState={appUpdateState}
        environmentIssueCount={environmentIssueCount}
        onOpenSettings={() => openSettingsTab('execution')}
        onOpenEnvironment={() => openSettingsTab('environment')}
        onOpenCommandPalette={() => setCommandPaletteOpen(true)}
        onOpenUpdates={() => setUpdateModalOpen(true)}
        onRestartBackend={() => void window.codexflow.restartBackend()}
      />

      <WelcomeWizardModal
        open={isWizardOpen}
        systemStatus={systemStatusQuery.data ?? null}
        modelSettings={modelSettingsQuery.data ?? null}
        controlPlaneAvailable={controlPlaneAvailable}
        onClose={() => setWizardOpen(false, true)}
        onSaved={refreshMetadata}
      />

      <SettingsModal
        open={isSettingsOpen}
        initialTab={settingsInitialTab}
        clientVersion={connection?.clientVersion ?? null}
        systemStatus={systemStatusQuery.data ?? null}
        executionSettings={executionSettingsQuery.data ?? null}
        modelSettings={modelSettingsQuery.data ?? null}
        controlPlaneAvailable={controlPlaneAvailable}
        onClose={() => setSettingsOpen(false)}
        onUpdated={refreshMetadata}
      />

      <CommandPaletteModal
        open={isCommandPaletteOpen}
        commands={commandPaletteItems}
        onOpenChange={setCommandPaletteOpen}
      />

      <UpdateModal
        open={isUpdateModalOpen}
        clientVersion={connection?.clientVersion ?? null}
        hostVersion={systemStatusQuery.data?.version ?? null}
        onOpenChange={setUpdateModalOpen}
      />

      <AboutModal
        open={isAboutOpen}
        clientVersion={connection?.clientVersion ?? null}
        hostVersion={systemStatusQuery.data?.version ?? null}
        onOpenChange={setAboutOpen}
      />
    </div>
  );
};
