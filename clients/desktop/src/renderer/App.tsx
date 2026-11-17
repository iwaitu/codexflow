import React, { useEffect, useMemo, useRef, useState } from 'react';
import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { Cpu, FileDiff, FolderTree } from 'lucide-react';
import { BusinessSkillsHub } from './components/business-skills/BusinessSkillsHub';
import { ChatView } from './components/ChatView';
import { CommandPaletteModal, type CommandPaletteItem } from './components/CommandPaletteModal';
import { Composer, type WorkspaceAttachmentRequest } from './components/Composer';
import { SettingsModal, type SettingsTabKind } from './components/SettingsModal';
import { Sidebar } from './components/Sidebar';
import { StatusBar } from './components/StatusBar';
import { WelcomeWizardModal } from './components/WelcomeWizardModal';
import { WorkspaceInspector } from './components/WorkspaceInspector';
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
  const [creatingConversation, setCreatingConversation] = useState(false);
  const [primaryView, setPrimaryView] = useState<'chat' | 'business-skills'>('chat');
  const [expandedSessionIds, setExpandedSessionIds] = useState<string[]>([]);
  const [externalAttachment, setExternalAttachment] = useState<WorkspaceAttachmentRequest | null>(null);
  const [isCommandPaletteOpen, setCommandPaletteOpen] = useState(false);
  const [settingsInitialTab, setSettingsInitialTab] = useState<SettingsTabKind>('execution');
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
    conversationMode,
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
    setConversationMode,
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
    setVerificationReceipt,
    setContextWindow,
    setJobs,
  } = useAppStore();

  useEffect(() => {
    syncHostPreferences();
  }, [syncHostPreferences]);

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
    enabled: Boolean(auth && activeSessionId),
  });

  useEffect(() => {
    if (systemStatusQuery.data && !systemStatusQuery.data.modelConfigured && !wizardDismissed) {
      setWizardOpen(true);
    }
  }, [systemStatusQuery.data, wizardDismissed, setWizardOpen]);

  // Bootstrap default workspace session once backend is ready
  useEffect(() => {
    if (!auth || activeSessionId) return;
    let cancelled = false;
    void apiClient.bootstrapSession(auth).then((res) => {
      if (!cancelled) {
        setActiveSession(res.sessionId);
        void queryClient.invalidateQueries({ queryKey: ['sessions'] });
      }
    });
    return () => {
      cancelled = true;
    };
  }, [auth, activeSessionId, setActiveSession, queryClient]);

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
        conversationMode,
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
    if (!auth || !activeSessionId) return;
    await apiClient.cancelTurn(auth, activeSessionId);
  };

  const handleCreateSession = async () => {
    if (!auth) return;
    setWorkspaceError(null);
    const created = await apiClient.createSession(auth, `工作区 ${new Date().toLocaleTimeString()}`);
    setActiveSession(created.sessionId);
    void queryClient.invalidateQueries({ queryKey: ['sessions'] });
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
  };

  crossSessionHandlersRef.current = {
    selectConversation: handleSelectConversation,
    createConversation: handleCreateConversation,
  };

  const handleVerifyJob = async (jobId: string) => {
    if (!auth || !activeSessionId) return;
    const sid = activeSessionId;
    const gen = useAppStore.getState().sessionGeneration;
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
    toggleInspector: (tab: InspectorTab) => void;
    switchView: (view: 'chat' | 'business-skills') => void;
  } | null>(null);

  commandActionsRef.current = {
    newConversation: () => void handleCreateConversation(),
    newWorkspace: () => void handleCreateSession(),
    openFolder: () => void handleOpenFolder(),
    openSettings: (tab = 'execution') => openSettingsTab(tab),
    toggleInspector: (tab) => {
      setPrimaryView('chat');
      toggleInspectorTab(tab);
    },
    switchView: (view) => setPrimaryView(view),
  };

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
  }, [activeSessionId, controlPlaneAvailable]);

  const commandPaletteItems = useMemo<CommandPaletteItem[]>(
    () => [
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
        id: 'check-updates',
        group: t('palette.group.system'),
        label: t('palette.cmd.checkUpdates'),
        keywords: 'update version release 检查更新 版本',
        onRun: () => openSettingsTab('preferences'),
      },
      {
        id: 'restart-backend',
        group: t('palette.group.system'),
        label: t('palette.cmd.restartBackend'),
        keywords: 'restart host backend 重启后端',
        disabled: connection?.mode !== 'managed',
        onRun: () => void window.codexflow.restartBackend(),
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
          onSelectPrimaryView={setPrimaryView}
          controlPlaneAvailable={controlPlaneAvailable}
          isTurnRunning={isTurnRunning}
          creatingConversation={creatingConversation}
          onSelectSession={(sid) => {
            setPrimaryView('chat');
            setActiveSession(sid);
          }}
          onSelectConversation={(cid, sid) => void handleSelectConversation(cid, sid)}
          onCreateConversation={handleCreateConversation}
          onCreateSession={handleCreateSession}
          onOpenFolder={handleOpenFolder}
          onRenameSession={async (sid, name) => {
            if (!auth) return;
            await apiClient.updateSession(auth, sid, { name });
            void queryClient.invalidateQueries({ queryKey: ['sessions'] });
          }}
          onArchiveSession={async (sid) => {
            if (!auth) return;
            await apiClient.updateSession(auth, sid, { isArchived: true });
            void queryClient.invalidateQueries({ queryKey: ['sessions'] });
          }}
          onDeleteSession={async (sid) => {
            if (!auth) return;
            await apiClient.deleteSession(auth, sid);
            if (activeSessionId === sid) setActiveSession(null);
            void queryClient.invalidateQueries({ queryKey: ['sessions'] });
          }}
          onRenameConversation={async (cid, name) => {
            if (!auth) return;
            await apiClient.updateConversation(auth, cid, { name });
            void queryClient.invalidateQueries({ queryKey: ['conversations'] });
          }}
          onDeleteConversation={async (cid) => {
            if (!auth) return;
            await apiClient.deleteConversation(auth, cid);
            void queryClient.invalidateQueries({ queryKey: ['conversations'] });
          }}
          onOpenSettings={() => openSettingsTab('execution')}
          workspaceError={workspaceError}
        />

        <main className="flex min-w-0 flex-1 flex-col bg-slate-900/40">
          {primaryView === 'business-skills' ? (
            <BusinessSkillsHub
              auth={auth}
              instanceKey={instanceKey}
              activeSessionId={activeSessionId}
              controlPlaneAvailable={controlPlaneAvailable}
              modelConfigured={Boolean(
                systemStatusQuery.data?.modelConfigured ?? modelSettingsQuery.data?.model,
              )}
            />
          ) : (
            <>
              <div className="flex h-11 shrink-0 items-center justify-between border-b border-slate-800/80 bg-slate-950/60 px-4 text-xs">
                <div className="flex min-w-0 items-center gap-2">
                  <span className="truncate font-medium text-slate-200">
                    {activeWorkspaceSession?.name || t('workspace.unselected')}
                  </span>
                  {activeWorkspacePath && (
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
                currentPlan={currentPlan}
                jobs={jobs}
                verificationReceipts={verificationReceipts}
                workerBar={workerBar}
                executionBackend={systemStatusQuery.data?.executionBackend ?? executionSettingsQuery.data?.backend ?? null}
                controlPlaneAvailable={controlPlaneAvailable}
                onApprovalResolved={() => void reconcileApprovals()}
                onPlanActionSubmitted={() =>
                  void reconcileSession('plan_action', {
                    onCursorSynced: (eid, epoch) => sseRef.current?.updateCursor(eid, epoch),
                  })
                }
                onVerifyJob={handleVerifyJob}
                onSwitchManaged={() => void window.codexflow.switchToManaged()}
              />

              <Composer
                disabled={!auth || !activeSessionId}
                isTurnRunning={isTurnRunning}
                conversationMode={conversationMode}
                turnNotice={turnNotice}
                workspacePath={activeWorkspacePath}
                externalAttachment={externalAttachment}
                onChangeMode={setConversationMode}
                onSend={handleSend}
                onCancel={handleCancel}
              />
            </>
          )}
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
        onOpenSettings={() => openSettingsTab('execution')}
        onOpenCommandPalette={() => setCommandPaletteOpen(true)}
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
    </div>
  );
};
