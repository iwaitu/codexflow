import React, { useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ChatView } from './components/ChatView';
import { Composer } from './components/Composer';
import { SettingsModal } from './components/SettingsModal';
import { Sidebar } from './components/Sidebar';
import { StatusBar } from './components/StatusBar';
import { WelcomeWizardModal } from './components/WelcomeWizardModal';
import { ApiError, apiClient } from './lib/apiClient';
import { SessionSseClient } from './lib/sseClient';
import { useAppStore } from './stores/useAppStore';

export const App: React.FC = () => {
  const queryClient = useQueryClient();
  const [workspaceError, setWorkspaceError] = useState<string | null>(null);
  const sseRef = useRef<SessionSseClient | null>(null);
  const prevRestartCount = useRef<number>(0);

  const {
    connection,
    sseState,
    activeSessionId,
    activeConversationId,
    instanceKey,
    conversationMode,
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
    setConnection,
    setSseState,
    setActiveSession,
    setActiveConversation,
    setConversationMode,
    setTurnNotice,
    setSettingsOpen,
    setWizardOpen,
    appendOptimisticUserMessage,
    applySseEvent,
    reconcileSession,
    reconcileApprovals,
    setVerificationReceipt,
    setContextWindow,
    setJobs,
  } = useAppStore();

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

  const refreshMetadata = () => {
    void queryClient.invalidateQueries({ queryKey: ['system-status'] });
    void queryClient.invalidateQueries({ queryKey: ['execution-settings'] });
    void queryClient.invalidateQueries({ queryKey: ['model-settings'] });
    void queryClient.invalidateQueries({ queryKey: ['sessions'] });
  };

  const handleSend = async (text: string) => {
    if (!auth || !activeSessionId) return;
    setTurnNotice(null);
    appendOptimisticUserMessage(text);
    try {
      await apiClient.sendChat(auth, {
        sessionId: activeSessionId,
        message: text,
        conversationId: activeConversationId ?? undefined,
        conversationMode,
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

  const handleSelectConversation = async (conversationId: string) => {
    if (!auth || !activeSessionId) return;
    const sid = activeSessionId;
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

  return (
    <div className="flex h-screen w-screen flex-col bg-slate-950 text-slate-100">
      <div className="flex min-h-0 flex-1">
        <Sidebar
          sessions={sessionsQuery.data?.items ?? []}
          conversations={conversationsQuery.data?.items ?? []}
          activeSessionId={activeSessionId}
          activeConversationId={activeConversationId}
          controlPlaneAvailable={controlPlaneAvailable}
          onSelectSession={(sid) => setActiveSession(sid)}
          onSelectConversation={(cid) => void handleSelectConversation(cid)}
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
          onOpenSettings={() => setSettingsOpen(true)}
          workspaceError={workspaceError}
        />

        <main className="flex min-w-0 flex-1 flex-col bg-slate-900/40">
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
            onChangeMode={setConversationMode}
            onSend={handleSend}
            onCancel={handleCancel}
          />
        </main>
      </div>

      <StatusBar
        connection={connection}
        sseState={sseState}
        systemStatus={systemStatusQuery.data ?? null}
        contextWindow={contextWindow}
        onOpenSettings={() => setSettingsOpen(true)}
        onRestartBackend={() => void window.codexflow.restartBackend()}
      />

      <WelcomeWizardModal
        open={isWizardOpen}
        systemStatus={systemStatusQuery.data ?? null}
        controlPlaneAvailable={controlPlaneAvailable}
        onClose={() => setWizardOpen(false, true)}
        onSaved={refreshMetadata}
      />

      <SettingsModal
        open={isSettingsOpen}
        systemStatus={systemStatusQuery.data ?? null}
        executionSettings={executionSettingsQuery.data ?? null}
        modelSettings={modelSettingsQuery.data ?? null}
        controlPlaneAvailable={controlPlaneAvailable}
        onClose={() => setSettingsOpen(false)}
        onUpdated={refreshMetadata}
      />
    </div>
  );
};
