import React, { useEffect, useState } from 'react';
import {
  Archive,
  Blocks,
  Bot,
  ChevronDown,
  ChevronRight,
  FolderOpen,
  FolderPlus,
  MessageSquare,
  PanelLeftClose,
  PanelLeftOpen,
  Pencil,
  Plus,
  Settings,
  Trash2,
} from 'lucide-react';
import type { ConversationSummary, SessionSummary } from '../lib/apiClient';
import { useI18n } from '../lib/i18n';
import { CodexFlowBrand } from './CodexFlowBrand';

interface SidebarProps {
  sessions: SessionSummary[];
  conversations: ConversationSummary[];
  conversationsBySession?: Record<string, ConversationSummary[]>;
  expandedSessionIds?: string[];
  onToggleExpandSession?: (sessionId: string) => void;
  activeSessionId: string | null;
  activeConversationId: string | null;
  activePrimaryView?: 'chat' | 'business-skills';
  onSelectPrimaryView?: (view: 'chat' | 'business-skills') => void;
  controlPlaneAvailable: boolean;
  isTurnRunning?: boolean;
  creatingConversation?: boolean;
  onSelectSession: (sessionId: string) => void;
  onSelectConversation: (conversationId: string, sessionId?: string) => void;
  onCreateConversation?: (sessionId?: string) => Promise<void>;
  onCreateSession: () => Promise<void>;
  onOpenDailyAssistant?: () => Promise<void>;
  onOpenFolder: () => Promise<void>;
  onRenameSession: (sessionId: string, name: string) => Promise<void>;
  onArchiveSession: (sessionId: string) => Promise<void>;
  onDeleteSession: (sessionId: string) => Promise<void>;
  onRenameConversation: (conversationId: string, name: string) => Promise<void>;
  onDeleteConversation: (conversationId: string) => Promise<void>;
  onOpenSettings: () => void;
  workspaceError: string | null;
}

export const Sidebar: React.FC<SidebarProps> = ({
  sessions,
  conversations,
  conversationsBySession,
  expandedSessionIds,
  onToggleExpandSession,
  activeSessionId,
  activeConversationId,
  activePrimaryView = 'chat',
  onSelectPrimaryView,
  controlPlaneAvailable,
  isTurnRunning = false,
  creatingConversation = false,
  onSelectSession,
  onSelectConversation,
  onCreateConversation,
  onCreateSession,
  onOpenDailyAssistant,
  onOpenFolder,
  onRenameSession,
  onArchiveSession,
  onDeleteSession,
  onRenameConversation,
  onDeleteConversation,
  onOpenSettings,
  workspaceError,
}) => {
  const { t } = useI18n();
  const [collapsed, setCollapsed] = useState(false);
  const [editingSessionId, setEditingSessionId] = useState<string | null>(null);
  const [editingName, setEditingName] = useState('');
  const [editingConvId, setEditingConvId] = useState<string | null>(null);
  const [editingConvName, setEditingConvName] = useState('');
  const [confirmAction, setConfirmAction] = useState<{
    kind: 'archive-session' | 'delete-session' | 'delete-conv';
    id: string;
    label: string;
  } | null>(null);
  const [busyActionId, setBusyActionId] = useState<string | null>(null);
  const [localExpandedIds, setLocalExpandedIds] = useState<string[]>(() =>
    activeSessionId ? [activeSessionId] : [],
  );

  useEffect(() => {
    if (!activeSessionId || expandedSessionIds !== undefined) {
      return;
    }
    setLocalExpandedIds((prev) =>
      prev.includes(activeSessionId) ? prev : [...prev, activeSessionId],
    );
  }, [activeSessionId, expandedSessionIds]);

  const effectiveExpandedSet = new Set(expandedSessionIds ?? localExpandedIds);

  const handleToggleExpand = (sessionId: string) => {
    if (onToggleExpandSession) {
      onToggleExpandSession(sessionId);
      return;
    }
    setLocalExpandedIds((prev) =>
      prev.includes(sessionId) ? prev.filter((id) => id !== sessionId) : [...prev, sessionId],
    );
  };

  const handleSelectWorkspaceNode = (sessionId: string) => {
    if (!effectiveExpandedSet.has(sessionId)) {
      handleToggleExpand(sessionId);
    }
    onSelectSession(sessionId);
  };

  const startRename = (id: string, currentName: string) => {
    setConfirmAction(null);
    setEditingSessionId(id);
    setEditingName(currentName);
  };

  const commitRename = async (sessionId: string) => {
    const trimmed = editingName.trim();
    setEditingSessionId(null);
    if (!trimmed) return;
    setBusyActionId(sessionId);
    try {
      await onRenameSession(sessionId, trimmed);
    } catch {
      // 错误由上层 workspaceError 横幅统一呈现
    } finally {
      setBusyActionId(null);
    }
  };

  const startRenameConversation = (id: string, currentName: string) => {
    setConfirmAction(null);
    setEditingConvId(id);
    setEditingConvName(currentName);
  };

  const commitRenameConversation = async (conversationId: string) => {
    const trimmed = editingConvName.trim();
    setEditingConvId(null);
    if (!trimmed) return;
    setBusyActionId(conversationId);
    try {
      await onRenameConversation(conversationId, trimmed);
    } catch {
      // 错误由上层 workspaceError 横幅统一呈现
    } finally {
      setBusyActionId(null);
    }
  };

  const executeConfirmedAction = async () => {
    if (!confirmAction) return;
    const action = confirmAction;
    setConfirmAction(null);
    setBusyActionId(action.id);
    try {
      if (action.kind === 'archive-session') {
        await onArchiveSession(action.id);
      } else if (action.kind === 'delete-session') {
        await onDeleteSession(action.id);
      } else {
        await onDeleteConversation(action.id);
      }
    } catch {
      // 错误由上层 workspaceError 横幅统一呈现
    } finally {
      setBusyActionId(null);
    }
  };

  const toggleLabel = t(collapsed ? 'sidebar.expand' : 'sidebar.collapse');
  const ToggleIcon = collapsed ? PanelLeftOpen : PanelLeftClose;

  return (
    <aside className={`flex h-full shrink-0 flex-col border-r border-slate-800 bg-slate-950 ${collapsed ? 'w-11' : 'w-60 xl:w-72'}`}>
      <div className={`flex shrink-0 items-center gap-2 border-b border-slate-800 py-3 ${collapsed ? 'flex-col' : 'px-3'}`}>
        <div className="mr-auto min-w-0" hidden={collapsed}>
          <CodexFlowBrand subtitle="Desktop" iconClassName="h-8 w-8" />
        </div>
        <button
          type="button"
          onClick={() => setCollapsed((wasCollapsed) => !wasCollapsed)}
          aria-expanded={!collapsed}
          aria-label={toggleLabel}
          title={toggleLabel}
          className="rounded-lg p-1.5 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
        >
          <ToggleIcon className="h-4 w-4" />
        </button>
        <button
          type="button"
          onClick={onOpenSettings}
          title={t('nav.settings')}
          aria-label={t('nav.settings')}
          className="rounded-lg p-1.5 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
        >
          <Settings className="h-4 w-4" />
        </button>
      </div>

      {onSelectPrimaryView && (
        <div hidden={collapsed} className="grid grid-cols-2 gap-1.5 border-b border-slate-800 px-3 py-2">
          <button
            type="button"
            onClick={() => onSelectPrimaryView('chat')}
            className={`inline-flex items-center justify-center gap-1.5 rounded-lg px-2 py-1.5 text-xs font-medium transition ${
              activePrimaryView === 'chat'
                ? 'bg-indigo-600 text-white'
                : 'bg-slate-900 text-slate-300 hover:bg-slate-800'
            }`}
          >
            <MessageSquare className="h-3.5 w-3.5" />
            {t('nav.workspaceChat')}
          </button>
          <button
            type="button"
            onClick={() => onSelectPrimaryView('business-skills')}
            className={`inline-flex items-center justify-center gap-1.5 rounded-lg px-2 py-1.5 text-xs font-medium transition ${
              activePrimaryView === 'business-skills'
                ? 'bg-indigo-600 text-white'
                : 'bg-slate-900 text-slate-300 hover:bg-slate-800'
            }`}
          >
            <Blocks className="h-3.5 w-3.5" />
            {t('nav.businessSkills')}
          </button>
        </div>
      )}

      <div hidden={collapsed} className="space-y-2 border-b border-slate-800 p-3">
        {onOpenDailyAssistant && (
          <button
            type="button"
            data-testid="daily-assistant-shortcut"
            onClick={() => void onOpenDailyAssistant()}
            className="flex w-full items-center justify-center gap-1.5 rounded-lg border border-emerald-500/40 bg-emerald-500/10 px-2.5 py-1.5 text-xs font-medium text-emerald-200 hover:bg-emerald-500/20"
          >
            <Bot className="h-3.5 w-3.5 text-emerald-400" />
            <span>日常助手</span>
          </button>
        )}
        <div className="grid grid-cols-2 gap-2">
          <button
            type="button"
            onClick={() => void onCreateSession()}
            className="inline-flex items-center justify-center gap-1.5 rounded-lg border border-slate-800 bg-slate-900 px-2.5 py-1.5 text-xs font-medium text-slate-200 hover:bg-slate-800"
          >
            <Plus className="h-3.5 w-3.5" />
            {t('sidebar.newWorkspace')}
          </button>
          <button
            type="button"
            disabled={!controlPlaneAvailable}
            onClick={() => void onOpenFolder()}
            title={controlPlaneAvailable ? t('sidebar.openFolder') : t('status.readonlyNoControl')}
            className="inline-flex items-center justify-center gap-1.5 rounded-lg bg-indigo-600/90 px-2.5 py-1.5 text-xs font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
          >
            <FolderPlus className="h-3.5 w-3.5" />
            {t('sidebar.openFolder')}
          </button>
        </div>
      </div>

      {workspaceError && (
        <div hidden={collapsed} className="mx-3 mt-2 rounded-lg border border-rose-500/40 bg-rose-950/30 p-2 text-[11px] text-rose-200">
          {workspaceError}
        </div>
      )}

      <div hidden={collapsed} className="min-h-0 flex-1 overflow-y-auto p-3">
        <div className="mb-2 flex items-center justify-between text-[11px] font-medium uppercase tracking-wider text-slate-500">
          <span>工作区与会话 ({sessions.length})</span>
        </div>

        <div className="space-y-1.5">
          {sessions.map((s) => {
            const isActiveSession = s.sessionId === activeSessionId;
            const isDefault = s.name === '默认工作区' || s.name === '日常助手';
            const isDailyMode = s.workMode === 'Daily';
            const isExpanded = effectiveExpandedSet.has(s.sessionId);
            const isSessionConfirming =
              confirmAction?.id === s.sessionId &&
              (confirmAction.kind === 'archive-session' || confirmAction.kind === 'delete-session');
            const sessionConversations =
              conversationsBySession?.[s.sessionId] ?? (isActiveSession ? conversations : []);

            return (
              <div key={s.sessionId} className="select-none">
                <div
                  className={`group rounded-lg border px-2 py-1.5 text-xs transition ${
                    isActiveSession
                      ? 'border-indigo-500/50 bg-indigo-950/30 text-slate-100'
                      : 'border-transparent text-slate-300 hover:bg-slate-900'
                  }`}
                >
                  {editingSessionId === s.sessionId ? (
                    <input
                      autoFocus
                      aria-label="工作区新名称"
                      value={editingName}
                      onChange={(e) => setEditingName(e.target.value)}
                      onBlur={() => void commitRename(s.sessionId)}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter') void commitRename(s.sessionId);
                        if (e.key === 'Escape') setEditingSessionId(null);
                      }}
                      className="w-full rounded border border-indigo-500 bg-slate-950 px-1.5 py-0.5 text-xs text-slate-100 focus:outline-none"
                    />
                  ) : (
                    <div className="flex items-center justify-between gap-1">
                      <button
                        type="button"
                        onClick={() => handleToggleExpand(s.sessionId)}
                        aria-label={
                          isExpanded
                            ? t('sidebar.collapseWorkspace', { name: s.name })
                            : t('sidebar.expandWorkspace', { name: s.name })
                        }
                        aria-expanded={isExpanded}
                        className="rounded p-0.5 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
                      >
                        {isExpanded ? (
                          <ChevronDown className="h-3.5 w-3.5" />
                        ) : (
                          <ChevronRight className="h-3.5 w-3.5" />
                        )}
                      </button>

                      <button
                        type="button"
                        onClick={() => handleSelectWorkspaceNode(s.sessionId)}
                        className="flex min-w-0 flex-1 items-center gap-1.5 text-left"
                      >
                        <FolderOpen className={`h-3.5 w-3.5 shrink-0 ${isDailyMode ? 'text-emerald-400' : 'text-indigo-400'}`} />
                        <span className="truncate font-medium">{s.name}</span>
                        <span
                          className={`shrink-0 rounded px-1 py-0.2 text-[9px] font-medium ${
                            isDailyMode
                              ? 'bg-emerald-500/15 text-emerald-300'
                              : 'bg-indigo-500/15 text-indigo-300'
                          }`}
                        >
                          {isDailyMode ? '日常' : '开发'}
                        </span>
                      </button>

                      <div className="flex shrink-0 items-center gap-0.5">
                        {onCreateConversation && (isActiveSession || isExpanded) && (
                          <button
                            type="button"
                            disabled={isTurnRunning || creatingConversation}
                            onClick={() => void onCreateConversation(s.sessionId)}
                            title={t('sidebar.newConversation')}
                            aria-label={isActiveSession ? t('sidebar.newConversation') : `${t('sidebar.newConversation')} (${s.name})`}
                            className="rounded p-1 text-slate-300 hover:bg-slate-800 hover:text-white disabled:cursor-not-allowed disabled:opacity-40"
                          >
                            <Plus className="h-3.5 w-3.5" />
                          </button>
                        )}

                        {!isDefault && !isSessionConfirming && (
                          <div className="hidden items-center gap-0.5 group-hover:flex group-focus-within:flex">
                            <button
                              type="button"
                              disabled={busyActionId === s.sessionId}
                              title="重命名"
                              aria-label={`重命名 ${s.name}`}
                              onClick={() => startRename(s.sessionId, s.name)}
                              className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100 disabled:opacity-40"
                            >
                              <Pencil className="h-3 w-3" />
                            </button>
                            <button
                              type="button"
                              disabled={busyActionId === s.sessionId}
                              title="归档"
                              aria-label={`归档 ${s.name}`}
                              onClick={() =>
                                setConfirmAction({
                                  kind: 'archive-session',
                                  id: s.sessionId,
                                  label: `归档「${s.name}」`,
                                })
                              }
                              className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100 disabled:opacity-40"
                            >
                              <Archive className="h-3 w-3" />
                            </button>
                            <button
                              type="button"
                              disabled={busyActionId === s.sessionId}
                              title="删除"
                              aria-label={`删除 ${s.name}`}
                              onClick={() =>
                                setConfirmAction({
                                  kind: 'delete-session',
                                  id: s.sessionId,
                                  label: `删除「${s.name}」`,
                                })
                              }
                              className="rounded p-1 text-slate-400 hover:bg-rose-950 hover:text-rose-300 disabled:opacity-40"
                            >
                              <Trash2 className="h-3 w-3" />
                            </button>
                          </div>
                        )}
                      </div>
                    </div>
                  )}

                  {isSessionConfirming && confirmAction && (
                    <div
                      data-testid={`confirm-${confirmAction.kind}-${s.sessionId}`}
                      className="mt-1.5 flex items-center justify-between gap-2 rounded border border-rose-500/40 bg-rose-950/30 px-2 py-1 text-[11px] text-rose-200"
                    >
                      <span className="truncate">确认{confirmAction.label}？</span>
                      <div className="flex shrink-0 items-center gap-1">
                        <button
                          type="button"
                          onClick={() => void executeConfirmedAction()}
                          className="rounded bg-rose-600 px-1.5 py-0.5 text-[10px] font-medium text-white hover:bg-rose-500"
                        >
                          确认
                        </button>
                        <button
                          type="button"
                          onClick={() => setConfirmAction(null)}
                          className="rounded border border-slate-700 px-1.5 py-0.5 text-[10px] text-slate-300 hover:bg-slate-800"
                        >
                          取消
                        </button>
                      </div>
                    </div>
                  )}

                  {s.workspacePath && !(isDailyMode && !s.isExternalWorkspace) && (
                    <div
                      className="mt-0.5 truncate pl-6 font-mono text-[10px] text-slate-500"
                      title={s.workspacePath}
                    >
                      {s.workspacePath}
                    </div>
                  )}
                </div>

                {isExpanded && (
                  <div className="ml-3 mt-1 space-y-0.5 border-l border-slate-800/90 pl-2.5">
                    {sessionConversations.length === 0 ? (
                      <div className="rounded-md border border-dashed border-slate-800/80 px-2 py-1.5 text-[11px] leading-relaxed text-slate-500">
                        暂无会话，点击工作区右侧「+」新建或直接发送消息。
                      </div>
                    ) : (
                      sessionConversations.map((c) => {
                        const isActiveConv =
                          isActiveSession && c.conversationId === activeConversationId;
                        const isConvConfirming =
                          confirmAction?.kind === 'delete-conv' && confirmAction.id === c.conversationId;
                        return (
                          <div key={c.conversationId} className="space-y-1">
                            {editingConvId === c.conversationId ? (
                              <input
                                autoFocus
                                aria-label="对话新名称"
                                value={editingConvName}
                                onChange={(e) => setEditingConvName(e.target.value)}
                                onBlur={() => void commitRenameConversation(c.conversationId)}
                                onKeyDown={(e) => {
                                  if (e.key === 'Enter') void commitRenameConversation(c.conversationId);
                                  if (e.key === 'Escape') setEditingConvId(null);
                                }}
                                className="w-full rounded border border-indigo-500 bg-slate-950 px-2 py-1 text-xs text-slate-100 focus:outline-none"
                              />
                            ) : (
                              <div
                                className={`group flex items-center justify-between gap-1 rounded-md px-2 py-1.5 text-xs transition ${
                                  isActiveConv
                                    ? 'bg-slate-800 text-slate-100'
                                    : 'text-slate-400 hover:bg-slate-900 hover:text-slate-200'
                                }`}
                              >
                                <button
                                  type="button"
                                  onClick={() => onSelectConversation(c.conversationId, s.sessionId)}
                                  className="flex min-w-0 flex-1 items-center gap-2 text-left"
                                >
                                  <MessageSquare className="h-3 w-3 shrink-0" />
                                  <span className="truncate">{c.name || c.conversationId}</span>
                                </button>
                                {!isConvConfirming && (
                                  <div className="hidden items-center gap-0.5 group-hover:flex group-focus-within:flex">
                                    <button
                                      type="button"
                                      disabled={busyActionId === c.conversationId}
                                      title="重命名对话"
                                      aria-label={`重命名对话 ${c.name || c.conversationId}`}
                                      onClick={() => startRenameConversation(c.conversationId, c.name)}
                                      className="rounded p-1 hover:bg-slate-700 disabled:opacity-40"
                                    >
                                      <Pencil className="h-3 w-3" />
                                    </button>
                                    <button
                                      type="button"
                                      disabled={busyActionId === c.conversationId}
                                      title="删除对话"
                                      aria-label={`删除对话 ${c.name || c.conversationId}`}
                                      onClick={() =>
                                        setConfirmAction({
                                          kind: 'delete-conv',
                                          id: c.conversationId,
                                          label: `删除对话「${c.name || '当前对话'}」`,
                                        })
                                      }
                                      className="rounded p-1 hover:bg-rose-950 hover:text-rose-300 disabled:opacity-40"
                                    >
                                      <Trash2 className="h-3 w-3" />
                                    </button>
                                  </div>
                                )}
                              </div>
                            )}
                            {isConvConfirming && confirmAction && (
                              <div
                                data-testid={`confirm-delete-conv-${c.conversationId}`}
                                className="flex items-center justify-between gap-1 rounded border border-rose-500/40 bg-rose-950/30 px-2 py-1 text-[11px] text-rose-200"
                              >
                                <span className="truncate">确认删除此对话？</span>
                                <div className="flex shrink-0 items-center gap-1">
                                  <button
                                    type="button"
                                    onClick={() => void executeConfirmedAction()}
                                    className="rounded bg-rose-600 px-1.5 py-0.5 text-[10px] font-medium text-white hover:bg-rose-500"
                                  >
                                    确认
                                  </button>
                                  <button
                                    type="button"
                                    onClick={() => setConfirmAction(null)}
                                    className="rounded border border-slate-700 px-1.5 py-0.5 text-[10px] text-slate-300 hover:bg-slate-800"
                                  >
                                    取消
                                  </button>
                                </div>
                              </div>
                            )}
                          </div>
                        );
                      })
                    )}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </div>
    </aside>
  );
};
