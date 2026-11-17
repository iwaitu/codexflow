import React, { useState } from 'react';
import { Bot, ChevronDown, ChevronRight, Info, Paperclip, User } from 'lucide-react';
import { formatFileSize, parseMessageWithAttachments } from '../lib/attachments';
import type { UiMessage } from '../lib/sessionRecovery';
import { MarkdownRenderer } from './MarkdownRenderer';

interface MessageItemProps {
  message: UiMessage;
}

export const MessageItem: React.FC<MessageItemProps> = ({ message }) => {
  const [thinkingOpen, setThinkingOpen] = useState(false);

  if (message.role === 'system') {
    return (
      <div className="my-2 flex items-center gap-2 rounded-lg border border-slate-800 bg-slate-900/60 px-3 py-2 text-xs text-slate-300">
        <Info className="h-3.5 w-3.5 shrink-0 text-sky-400" />
        <span className="whitespace-pre-wrap">{message.content}</span>
      </div>
    );
  }

  const isUser = message.role === 'user';
  const parsedUserContent = isUser ? parseMessageWithAttachments(message.content) : null;

  return (
    <div className={`flex gap-3 py-3 ${isUser ? 'justify-end' : 'justify-start'}`}>
      {!isUser && (
        <div className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-indigo-600/20 text-indigo-300">
          <Bot className="h-4 w-4" />
        </div>
      )}

      <div
        className={`max-w-[85%] rounded-xl px-4 py-3 ${
          isUser
            ? 'bg-indigo-600/90 text-white'
            : 'border border-slate-800/80 bg-slate-900/80 text-slate-100'
        }`}
      >
        {!isUser && message.thinkingContent && (
          <div className="mb-2 rounded-lg border border-slate-800 bg-slate-950/60">
            <button
              type="button"
              onClick={() => setThinkingOpen((v) => !v)}
              className="flex w-full items-center gap-1.5 px-2.5 py-1.5 text-left text-xs text-slate-400 hover:text-slate-200"
            >
              {thinkingOpen ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
              <span>思考过程</span>
            </button>
            {thinkingOpen && (
              <div className="border-t border-slate-800 px-3 py-2 text-xs text-slate-300 whitespace-pre-wrap">
                {message.thinkingContent}
              </div>
            )}
          </div>
        )}

        {isUser ? (
          <div>
            {parsedUserContent?.text && (
              <div className="whitespace-pre-wrap text-sm">{parsedUserContent.text}</div>
            )}
            {parsedUserContent && parsedUserContent.attachments.length > 0 && (
              <div
                data-testid="message-attachments"
                className={`${parsedUserContent.text ? 'mt-2 border-t border-indigo-400/30 pt-2' : ''} flex flex-wrap gap-1.5`}
              >
                {parsedUserContent.attachments.map((att, idx) => (
                  <span
                    key={`${att.relativePath}-${idx}`}
                    title={att.relativePath}
                    className="inline-flex items-center gap-1 rounded-md bg-indigo-950/60 px-2 py-0.5 text-xs text-indigo-100 ring-1 ring-indigo-300/30"
                  >
                    <Paperclip className="h-3 w-3 shrink-0 text-indigo-200" />
                    <span className="max-w-[200px] truncate font-medium">{att.name}</span>
                    <span className="text-[10px] text-indigo-200/80">{formatFileSize(att.sizeBytes)}</span>
                  </span>
                ))}
              </div>
            )}
          </div>
        ) : (
          <MarkdownRenderer content={message.content} />
        )}

        {message.truncated && (
          <div className="mt-2 inline-block rounded bg-amber-500/15 px-1.5 py-0.5 text-[11px] text-amber-300">
            内容因快照长度限制已截断
          </div>
        )}
      </div>

      {isUser && (
        <div className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-slate-800 text-slate-300">
          <User className="h-4 w-4" />
        </div>
      )}
    </div>
  );
};
