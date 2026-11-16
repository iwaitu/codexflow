import React, { useState } from 'react';
import {
  Archive,
  FolderOpen,
  FolderPlus,
  MessageSquare,
  Pencil,
  Plus,
  Settings,
  Trash2,
} from 'lucide-react';
import type { ConversationSummary, SessionSummary } from '../lib/apiClient';

interface SidebarProps {
  sessions: SessionSummary[];
  conversations: ConversationSummary[];
  activeSessionId: string | null;
  activeConversationId: string | null;
  controlPlaneAvailable: boolean;
  onSelectSession: (sessionId: string) => void;
  onSelectConversation: (conversationId: string) => void;
  onCreateSession: () => Promise<void>;
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
  activeSessionId,
  activeConversationId,
  controlPlaneAvailable,
  onSelectSession,
  onSelectConversation,
  onCreateSession,
  onOpenFolder,
  onRenameSession,
  onArchiveSession,
  onDeleteSession,
  onRenameConversation,
  onDeleteConversation,
  onOpenSettings,
  workspaceError,
}) => {
  const [editingSessionId, setEditingSessionId] = useState<string | null>(null);
  const [editingName, setEditingName] = useState('');

  const startRename = (id: string, currentName: string) => {
    setEditingSessionId(id);
    setEditingName(currentName);
  };

  const commitRename = async (sessionId: string) => {
    const trimmed = editingName.trim();
    setEditingSessionId(null);
    if (trimmed) {
      await onRenameSession(sessionId, trimmed);
    }
  };

  return (
    <aside className="flex h-full w-72 shrink-0 flex-col border-r border-slate-800 bg-slate-950">
      <div className="flex items-center justify-between border-b border-slate-800 px-4 py-3">
        <div>
          <div className="text-sm font-semibold tracking-wide text-slate-100">CodexFlow</div>
          <div className="text-[11px] text-slate-400">Desktop MVP</div>
        </div>
        <button
          type="button"
          onClick={onOpenSettings}
          title="设置"
          aria-label="打开设置"
          className="rounded-lg p-1.5 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
        >
          <Settings className="h-4 w-4" />
        </button>
      </div>

      <div className="grid grid-cols-2 gap-2 border-b border-slate-800 p-3">
        <button
          type="button"
          onClick={() => void onCreateSession()}
          className="inline-flex items-center justify-center gap-1.5 rounded-lg border border-slate-800 bg-slate-900 px-2.5 py-1.5 text-xs font-medium text-slate-200 hover:bg-slate-800"
        >
          <Plus className="h-3.5 w-3.5" />
          新建工作区
        </button>
        <button
          type="button"
          disabled={!controlPlaneAvailable}
          onClick={() => void onOpenFolder()}
          title={controlPlaneAvailable ? '打开本地文件夹作为工作区' : '需要控制面凭据（请使用托管模式）'}
          className="inline-flex items-center justify-center gap-1.5 rounded-lg bg-indigo-600/90 px-2.5 py-1.5 text-xs font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
        >
          <FolderPlus className="h-3.5 w-3.5" />
          打开文件夹
        </button>
      </div>

      {workspaceError && (
        <div className="mx-3 mt-2 rounded-lg border border-rose-500/40 bg-rose-950/30 p-2 text-[11px] text-rose-200">
          {workspaceError}
        </div>
      )}

      <div className="flex-1 overflow-y-auto p-3">
        <div className="mb-2 text-[11px] font-medium uppercase tracking-wider text-slate-500">
          工作区与会话 ({sessions.length})
        </div>
        <div className="space-y-1">
          {sessions.map((s) => {
            const isActive = s.sessionId === activeSessionId;
            const isDefault = s.name === '默认工作区';
            return (
              <div
                key={s.sessionId}
                className={`group rounded-lg border px-2.5 py-2 text-xs transition ${
                  isActive
                    ? 'border-indigo-500/50 bg-indigo-950/30 text-slate-100'
                    : 'border-transparent text-slate-300 hover:bg-slate-900'
                }`}
              >
                {editingSessionId === s.sessionId ? (
                  <input
                    autoFocus
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
                      onClick={() => onSelectSession(s.sessionId)}
                      className="flex min-w-0 flex-1 items-center gap-2 text-left"
                    >
                      <FolderOpen className="h-3.5 w-3.5 shrink-0 text-indigo-400" />
                      <span className="truncate font-medium">{s.name}</span>
                    </button>

                    {!isDefault && (
                      <div className="hidden items-center gap-0.5 group-hover:flex">
                        <button
                          type="button"
                          title="重命名"
                          onClick={() => startRename(s.sessionId, s.name)}
                          className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
                        >
                          <Pencil className="h-3 w-3" />
                        </button>
                        <button
                          type="button"
                          title="归档"
                          onClick={() => void onArchiveSession(s.sessionId)}
                          className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
                        >
                          <Archive className="h-3 w-3" />
                        </button>
                        <button
                          type="button"
                          title="删除"
                          onClick={() => void onDeleteSession(s.sessionId)}
                          className="rounded p-1 text-slate-400 hover:bg-rose-950 hover:text-rose-300"
                        >
                          <Trash2 className="h-3 w-3" />
                        </button>
                      </div>
                    )}
                  </div>
                )}

                {s.workspacePath && (
                  <div className="mt-1 truncate pl-5 font-mono text-[10px] text-slate-500" title={s.workspacePath}>
                    {s.workspacePath}
                  </div>
                )}
              </div>
            );
          })}
        </div>

        {conversations.length > 0 && (
          <div className="mt-5">
            <div className="mb-2 text-[11px] font-medium uppercase tracking-wider text-slate-500">
              对话线程 ({conversations.length})
            </div>
            <div className="space-y-1">
              {conversations.map((c) => {
                const isActive = c.conversationId === activeConversationId;
                return (
                  <div
                    key={c.conversationId}
                    className={`group flex items-center justify-between gap-1 rounded-lg px-2.5 py-1.5 text-xs ${
                      isActive
                        ? 'bg-slate-800 text-slate-100'
                        : 'text-slate-400 hover:bg-slate-900 hover:text-slate-200'
                    }`}
                  >
                    <button
                      type="button"
                      onClick={() => onSelectConversation(c.conversationId)}
                      className="flex min-w-0 flex-1 items-center gap-2 text-left"
                    >
                      <MessageSquare className="h-3 w-3 shrink-0" />
                      <span className="truncate">{c.name || c.conversationId}</span>
                    </button>
                    <div className="hidden items-center gap-0.5 group-hover:flex">
                      <button
                        type="button"
                        title="重命名对话"
                        onClick={() => {
                          const next = window.prompt('输入新的对话名称', c.name);
                          if (next && next.trim()) {
                            void onRenameConversation(c.conversationId, next.trim());
                          }
                        }}
                        className="rounded p-1 hover:bg-slate-700"
                      >
                        <Pencil className="h-3 w-3" />
                      </button>
                      <button
                        type="button"
                        title="删除对话"
                        onClick={() => void onDeleteConversation(c.conversationId)}
                        className="rounded p-1 hover:bg-rose-950 hover:text-rose-300"
                      >
                        <Trash2 className="h-3 w-3" />
                      </button>
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        )}
      </div>
    </aside>
  );
};
