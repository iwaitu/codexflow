import React, { useEffect, useMemo, useState } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { Command, Search, X } from 'lucide-react';
import { useI18n } from '../lib/i18n';

export interface CommandPaletteItem {
  id: string;
  group: string;
  label: string;
  keywords?: string;
  shortcut?: string;
  disabled?: boolean;
  onRun: () => void;
}

interface CommandPaletteModalProps {
  open: boolean;
  commands: CommandPaletteItem[];
  onOpenChange: (open: boolean) => void;
}

export const CommandPaletteModal: React.FC<CommandPaletteModalProps> = ({
  open,
  commands,
  onOpenChange,
}) => {
  const { t } = useI18n();
  const [query, setQuery] = useState('');
  const [selectedIndex, setSelectedIndex] = useState(0);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) {
      return commands;
    }
    return commands.filter((cmd) => {
      const haystack = `${cmd.label} ${cmd.group} ${cmd.keywords ?? ''} ${cmd.shortcut ?? ''}`.toLowerCase();
      return haystack.includes(q);
    });
  }, [commands, query]);

  useEffect(() => {
    if (open) {
      setQuery('');
      setSelectedIndex(0);
    }
  }, [open]);

  useEffect(() => {
    if (selectedIndex >= filtered.length) {
      setSelectedIndex(0);
    }
  }, [filtered.length, selectedIndex]);

  const executeItem = (item: CommandPaletteItem | undefined) => {
    if (!item || item.disabled) {
      return;
    }
    onOpenChange(false);
    item.onRun();
  };

  const handleKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      if (filtered.length > 0) {
        setSelectedIndex((prev) => (prev + 1) % filtered.length);
      }
      return;
    }
    if (event.key === 'ArrowUp') {
      event.preventDefault();
      if (filtered.length > 0) {
        setSelectedIndex((prev) => (prev - 1 + filtered.length) % filtered.length);
      }
      return;
    }
    if (event.key === 'Enter') {
      event.preventDefault();
      executeItem(filtered[selectedIndex]);
    }
  };

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/60 backdrop-blur-xs" />
        <Dialog.Content
          data-testid="command-palette-modal"
          aria-describedby={undefined}
          className="fixed left-1/2 top-[18%] z-50 w-full max-w-xl -translate-x-1/2 overflow-hidden rounded-xl border border-slate-700 bg-slate-900 shadow-2xl"
        >
          <Dialog.Title className="sr-only">{t('palette.title')}</Dialog.Title>
          <div className="flex items-center gap-2 border-b border-slate-800 px-3.5 py-2.5">
            <Search className="h-4 w-4 shrink-0 text-slate-400" />
            <input
              data-testid="command-palette-input"
              autoFocus
              value={query}
              onChange={(e) => {
                setQuery(e.target.value);
                setSelectedIndex(0);
              }}
              onKeyDown={handleKeyDown}
              placeholder={t('palette.placeholder')}
              className="w-full bg-transparent text-sm text-slate-100 placeholder:text-slate-500 focus:outline-none"
            />
            <button
              type="button"
              onClick={() => onOpenChange(false)}
              aria-label={t('settings.cancel')}
              className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
            >
              <X className="h-4 w-4" />
            </button>
          </div>

          <div className="max-h-80 overflow-y-auto p-2">
            {filtered.length === 0 ? (
              <div className="px-3 py-8 text-center text-xs text-slate-500">{t('palette.empty')}</div>
            ) : (
              filtered.map((item, idx) => {
                const active = idx === selectedIndex;
                return (
                  <button
                    key={item.id}
                    type="button"
                    data-testid={`command-item-${item.id}`}
                    disabled={item.disabled}
                    onMouseEnter={() => setSelectedIndex(idx)}
                    onClick={() => executeItem(item)}
                    className={`flex w-full items-center justify-between gap-3 rounded-lg px-3 py-2 text-left text-xs transition ${
                      active ? 'bg-indigo-600/20 text-slate-100' : 'text-slate-300 hover:bg-slate-800/60'
                    } disabled:cursor-not-allowed disabled:opacity-40`}
                  >
                    <div className="flex min-w-0 items-center gap-2.5">
                      <Command className="h-3.5 w-3.5 shrink-0 text-indigo-400" />
                      <div className="min-w-0">
                        <div className="truncate font-medium">{item.label}</div>
                        <div className="truncate text-[10px] text-slate-500">{item.group}</div>
                      </div>
                    </div>
                    {item.shortcut && (
                      <kbd className="shrink-0 rounded border border-slate-700 bg-slate-950 px-1.5 py-0.5 font-mono text-[10px] text-slate-400">
                        {item.shortcut}
                      </kbd>
                    )}
                  </button>
                );
              })
            )}
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
};
