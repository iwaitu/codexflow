import { memo, useEffect, useRef, useState, type KeyboardEvent } from 'react';
import * as Tooltip from '@radix-ui/react-tooltip';
import { parseMessageWithAttachments } from '../lib/attachments';
import { useI18n } from '../lib/i18n';
import type { UiMessage } from '../lib/sessionRecovery';

interface ChatTurn {
  id: string;
  messageIndex: number;
  prompt: string;
  reply: string;
  hasAttachments: boolean;
}

function previewText(content: string): string {
  return content.trim().slice(0, 240).replace(/\s+/g, ' ');
}

export function collectChatTurns(messages: UiMessage[]): ChatTurn[] {
  const turns: ChatTurn[] = [];
  let current: ChatTurn | undefined;
  messages.forEach((message, messageIndex) => {
    if (message.role === 'user') {
      const { text, attachments } = parseMessageWithAttachments(message.content);
      current = { id: message.id, messageIndex, prompt: previewText(text), reply: '', hasAttachments: attachments.length > 0 };
      turns.push(current);
    } else if (message.role === 'assistant' && current && message.content.trim()) {
      current.reply = previewText(message.content);
    }
  });
  return turns;
}

interface ChatTurnNavigationProps {
  turns: ChatTurn[];
  activeIndex: number;
  onNavigate: (messageIndex: number) => void;
}

export const ChatTurnNavigation = memo(function ChatTurnNavigation({ turns, activeIndex, onNavigate }: ChatTurnNavigationProps) {
  const { t } = useI18n();
  const listRef = useRef<HTMLOListElement>(null);
  const [focusedIndex, setFocusedIndex] = useState<number | null>(null);
  const tabStop = focusedIndex ?? activeIndex;

  useEffect(() => {
    if (focusedIndex !== null) return;
    const list = listRef.current;
    const item = list?.children[activeIndex];
    if (!list || !(item instanceof HTMLElement)) return;
    if (item.offsetTop < list.scrollTop) {
      list.scrollTop = item.offsetTop;
    } else if (item.offsetTop + item.offsetHeight > list.scrollTop + list.clientHeight) {
      list.scrollTop = item.offsetTop + item.offsetHeight - list.clientHeight;
    }
  }, [activeIndex, focusedIndex]);

  const handleKeyDown = (event: KeyboardEvent<HTMLButtonElement>, index: number) => {
    const targets: Partial<Record<string, number>> = {
      ArrowUp: Math.max(0, index - 1),
      ArrowDown: Math.min(turns.length - 1, index + 1),
      Home: 0,
      End: turns.length - 1,
    };
    const next = targets[event.key];
    if (next === undefined) return;
    event.preventDefault();
    listRef.current?.children[next]?.querySelector('button')?.focus();
  };

  return (
    <nav aria-label={t('chatNav.label')} className="absolute top-1/2 right-3 z-10 flex max-h-[65%] w-6 -translate-y-1/2 flex-col">
      <Tooltip.Provider delayDuration={250} disableHoverableContent>
        <ol
          ref={listRef}
          className="relative min-h-0 list-none space-y-px overflow-y-auto overscroll-contain [scrollbar-width:none]"
          onBlur={(event) => {
            if (!event.currentTarget.contains(event.relatedTarget)) setFocusedIndex(null);
          }}
        >
          {turns.map((turn, index) => {
            const label = t('chatNav.turn', { number: index + 1 });
            const active = index === activeIndex;
            return (
              <li key={turn.id}>
                <Tooltip.Root>
                  <Tooltip.Trigger asChild>
                    <button
                      type="button"
                      aria-label={label}
                      aria-current={active ? 'location' : undefined}
                      tabIndex={index === tabStop ? 0 : -1}
                      onFocus={(event) => setFocusedIndex(event.currentTarget.matches(':focus-visible') ? index : null)}
                      onKeyDown={(event) => handleKeyDown(event, index)}
                      onClick={() => onNavigate(turn.messageIndex)}
                      className="group flex h-4 w-full items-center justify-end rounded-sm pr-1 focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-indigo-400"
                    >
                      <span
                        aria-hidden="true"
                        className={`h-0.5 rounded-full group-hover:w-4 group-hover:bg-indigo-400 ${active ? 'w-4 bg-indigo-400' : 'w-2 bg-slate-500/80'}`}
                      />
                    </button>
                  </Tooltip.Trigger>
                  <Tooltip.Portal>
                    <Tooltip.Content
                      side="left"
                      sideOffset={8}
                      collisionPadding={12}
                      className="z-50 w-64 max-w-[calc(100vw-2rem)] rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-xs shadow-xl"
                    >
                      <p className="text-[11px] text-indigo-400">{label}</p>
                      {(turn.prompt || turn.hasAttachments) && (
                        <p className="mt-1 line-clamp-2 font-medium break-words text-slate-100">
                          {turn.prompt || t('chatNav.attachments')}
                        </p>
                      )}
                      {turn.reply && <p className="mt-1 line-clamp-3 break-words text-slate-400">{turn.reply}</p>}
                    </Tooltip.Content>
                  </Tooltip.Portal>
                </Tooltip.Root>
              </li>
            );
          })}
        </ol>
      </Tooltip.Provider>
    </nav>
  );
});
