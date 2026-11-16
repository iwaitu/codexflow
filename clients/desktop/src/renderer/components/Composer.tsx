import React, { useState } from 'react';
import { Send, Square } from 'lucide-react';
import type { ConversationMode } from '../stores/useAppStore';

interface ComposerProps {
  disabled: boolean;
  isTurnRunning: boolean;
  conversationMode: ConversationMode;
  turnNotice: string | null;
  onChangeMode: (mode: ConversationMode) => void;
  onSend: (text: string) => Promise<void>;
  onCancel: () => Promise<void>;
}

export const Composer: React.FC<ComposerProps> = ({
  disabled,
  isTurnRunning,
  conversationMode,
  turnNotice,
  onChangeMode,
  onSend,
  onCancel,
}) => {
  const [text, setText] = useState('');
  const [sending, setSending] = useState(false);

  const handleSubmit = async () => {
    const trimmed = text.trim();
    if (!trimmed || disabled || isTurnRunning || sending) {
      return;
    }
    setSending(true);
    try {
      await onSend(trimmed);
      setText('');
    } catch {
      // Preserve draft text when send fails so user can retry or inspect turnNotice.
    } finally {
      setSending(false);
    }
  };

  return (
    <div className="border-t border-slate-800 bg-slate-950/90 px-4 py-3">
      {turnNotice && (
        <div className="mb-2 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-1.5 text-xs text-amber-200">
          {turnNotice}
        </div>
      )}

      <div className="rounded-xl border border-slate-800 bg-slate-900/90 p-2 focus-within:border-indigo-500/60">
        <textarea
          rows={3}
          value={text}
          disabled={disabled}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.nativeEvent.isComposing) {
              return;
            }
            if (e.key === 'Enter' && !e.shiftKey) {
              e.preventDefault();
              void handleSubmit();
            }
          }}
          placeholder={disabled ? '等待后端连接就绪...' : '输入指令或问题（Enter 发送，Shift+Enter 换行）...'}
          className="w-full resize-none bg-transparent px-2 py-1 text-sm text-slate-100 placeholder:text-slate-500 focus:outline-none disabled:opacity-50"
        />

        <div className="mt-1.5 flex items-center justify-between border-t border-slate-800/80 pt-2">
          <div className="flex items-center gap-2 text-xs text-slate-400">
            <span>会话模式：</span>
            <select
              aria-label="会话模式"
              value={conversationMode}
              onChange={(e) => onChangeMode(e.target.value as ConversationMode)}
              className="rounded border border-slate-700 bg-slate-950 px-2 py-1 text-xs text-slate-200 focus:outline-none"
            >
              <option value="reuse-master">继续主对话 (continue)</option>
              <option value="continue-current">继续当前线程</option>
              <option value="new-thread">新建对话线程 (new thread)</option>
            </select>
          </div>

          <div className="flex items-center gap-2">
            {isTurnRunning ? (
              <button
                type="button"
                onClick={() => void onCancel()}
                className="inline-flex items-center gap-1.5 rounded-lg bg-rose-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-rose-500"
              >
                <Square className="h-3.5 w-3.5 fill-current" />
                停止
              </button>
            ) : (
              <button
                type="button"
                disabled={disabled || sending || !text.trim()}
                onClick={() => void handleSubmit()}
                className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
              >
                <Send className="h-3.5 w-3.5" />
                发送
              </button>
            )}
          </div>
        </div>
      </div>
    </div>
  );
};
