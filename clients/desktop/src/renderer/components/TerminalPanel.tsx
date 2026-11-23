import { useEffect, useRef, useState } from 'react';
import { FitAddon } from '@xterm/addon-fit';
import { Terminal, type ITheme } from '@xterm/xterm';
import { Play, Square } from 'lucide-react';
import '@xterm/xterm/css/xterm.css';
import { useI18n } from '../lib/i18n';
import { usePreferencesStore } from '../stores/usePreferencesStore';

type TerminalStatus =
  | { kind: 'starting' }
  | { kind: 'running'; shellName: string }
  | { kind: 'stopped' }
  | { kind: 'exited'; exitCode: number }
  | { kind: 'error'; message?: string };

const themes: Record<'dark' | 'light', ITheme> = {
  dark: { background: '#020617', foreground: '#e2e8f0', cursor: '#a5b4fc', selectionBackground: '#373f70' },
  light: { background: '#f8fafc', foreground: '#0f172a', cursor: '#4338ca', selectionBackground: '#d5ddfa' },
};

export default function TerminalPanel({ workspacePath, visible }: { workspacePath: string; visible: boolean }) {
  const { t } = useI18n();
  const theme = usePreferencesStore((state) => state.theme);
  const host = useRef<HTMLDivElement>(null);
  const activeId = useRef<string | null>(null);
  const terminalRef = useRef<{ terminal: Terminal; fit: () => void } | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [status, setStatus] = useState<TerminalStatus>({ kind: 'starting' });

  useEffect(() => {
    const element = host.current;
    if (!element) return;

    const bridge = window.codexflow;
    const id = crypto.randomUUID();
    const terminal = new Terminal({
      theme: themes[usePreferencesStore.getState().theme],
      fontFamily: 'Cascadia Mono, Menlo, Consolas, monospace',
      fontSize: 13,
      scrollback: 2000,
      cursorBlink: true,
      disableStdin: true,
    });
    const fitAddon = new FitAddon();
    terminal.loadAddon(fitAddon);
    terminal.open(element);

    const size = () => {
      if (!element.clientWidth || !element.clientHeight) return;
      const dimensions = fitAddon.proposeDimensions();
      if (!dimensions) return;
      const cols = Math.min(500, dimensions.cols);
      const rows = Math.min(300, dimensions.rows);
      if (Number.isFinite(cols) && Number.isFinite(rows) && cols > 0 && rows > 0) terminal.resize(cols, rows);
    };

    terminalRef.current = { terminal, fit: size };
    activeId.current = id;
    size();

    const unsubscribe = bridge.onWorkspaceTerminalEvent((event) => {
      if (activeId.current !== id || event.id !== id) return;
      if (event.type === 'data') {
        terminal.write(event.data, () => {
          if (activeId.current === id) bridge.actOnWorkspaceTerminal({ id, type: 'ack', length: event.data.length });
        });
      } else {
        activeId.current = null;
        terminal.options.disableStdin = true;
        setStatus({ kind: 'exited', exitCode: event.exitCode });
      }
    });

    terminal.onData((data) => {
      if (activeId.current !== id) return;
      for (let start = 0; start < data.length;) {
        let end = Math.min(start + 32 * 1024, data.length);
        const last = data.charCodeAt(end - 1);
        if (end < data.length && last >= 0xd800 && last <= 0xdbff) end--;
        bridge.actOnWorkspaceTerminal({ id, type: 'write', data: data.slice(start, end) });
        start = end;
      }
    });
    terminal.onResize((dimensions) => {
      if (activeId.current === id) bridge.actOnWorkspaceTerminal({ id, type: 'resize', ...dimensions });
    });

    const observer = new ResizeObserver(size);
    observer.observe(element);
    void bridge.startWorkspaceTerminal({ id, workspacePath, cols: terminal.cols, rows: terminal.rows }).then((result) => {
      if (activeId.current !== id) return;
      if (result.ok) {
        terminal.options.disableStdin = false;
        setStatus({ kind: 'running', shellName: result.shellName });
      } else {
        activeId.current = null;
        setStatus({ kind: 'error', message: result.error });
      }
    }).catch(() => {
      if (activeId.current !== id) return;
      activeId.current = null;
      setStatus({ kind: 'error' });
    });

    return () => {
      if (activeId.current === id) activeId.current = null;
      bridge.actOnWorkspaceTerminal({ id, type: 'close' });
      unsubscribe();
      observer.disconnect();
      terminal.dispose();
      terminalRef.current = null;
    };
  }, [attempt, workspacePath]);

  useEffect(() => {
    if (terminalRef.current) terminalRef.current.terminal.options.theme = themes[theme];
  }, [theme]);

  useEffect(() => {
    const id = activeId.current;
    if (!visible) {
      if (id) window.codexflow.actOnWorkspaceTerminal({ id, type: 'focus', focused: false });
      return;
    }
    const frame = requestAnimationFrame(() => {
      terminalRef.current?.fit();
      if (status.kind === 'running') terminalRef.current?.terminal.focus();
    });
    return () => cancelAnimationFrame(frame);
  }, [visible, status.kind]);

  const setTerminalFocus = (focused: boolean) => {
    const id = activeId.current;
    if (id) window.codexflow.actOnWorkspaceTerminal({ id, type: 'focus', focused });
  };

  const stop = () => {
    const id = activeId.current;
    if (!id) return;
    activeId.current = null;
    window.codexflow.actOnWorkspaceTerminal({ id, type: 'close' });
    if (terminalRef.current) terminalRef.current.terminal.options.disableStdin = true;
    setStatus({ kind: 'stopped' });
  };

  const openSystemTerminal = async () => {
    try {
      const result = await window.codexflow.openWorkspaceTerminal(workspacePath);
      if (!result.ok) setStatus({ kind: 'error', message: result.error });
    } catch {
      setStatus({ kind: 'error', message: t('overview.terminalOpenFailed') });
    }
  };

  const startingOrRunning = status.kind === 'starting' || status.kind === 'running';
  const actionClass = 'inline-flex items-center gap-1.5 rounded-md border border-slate-800 bg-slate-900 px-2 py-1 text-xs text-slate-200 hover:bg-slate-800';

  return (
    <div className="flex h-full min-h-0 flex-col bg-slate-950">
      <div className="flex shrink-0 items-center justify-between gap-2 border-b border-slate-800 px-3 py-2">
        <div className="min-w-0">
          <p className="text-xs font-semibold text-slate-100">{status.kind === 'running' ? status.shellName : t('overview.terminal')}</p>
          <p className="truncate font-mono text-[11px] text-slate-400" title={workspacePath}>{workspacePath}</p>
        </div>
        {startingOrRunning ? (
          <button type="button" className={`${actionClass} shrink-0`} onClick={stop}>
            <Square className="h-3 w-3" aria-hidden="true" />{t('terminal.stop')}
          </button>
        ) : (
          <button type="button" className={`${actionClass} shrink-0`} onClick={() => {
            setStatus({ kind: 'starting' });
            setAttempt((value) => value + 1);
          }}>
            <Play className="h-3 w-3" aria-hidden="true" />{t('terminal.start')}
          </button>
        )}
      </div>
      {status.kind === 'starting' && <p role="status" className="px-3 pt-2 text-xs text-slate-400">{t('terminal.starting')}</p>}
      {status.kind === 'stopped' && <p role="status" className="px-3 pt-2 text-xs text-slate-400">{t('terminal.stopped')}</p>}
      {status.kind === 'exited' && <p role="status" className="px-3 pt-2 text-xs text-slate-400">{t('terminal.exited', { code: status.exitCode })}</p>}
      {status.kind === 'error' && (
        <div className="space-y-2 px-3 pt-2 text-xs">
          <p role="alert" className="text-rose-300">{status.message || t('terminal.startFailed')}</p>
          <button type="button" className={actionClass} onClick={() => void openSystemTerminal()}>{t('terminal.openSystem')}</button>
        </div>
      )}
      <div className="min-h-0 flex-1 overflow-hidden px-2 py-1">
        <div
          ref={host}
          data-workspace-terminal
          role="region"
          aria-label={t('overview.terminal')}
          className="h-full min-w-0"
          onFocus={() => setTerminalFocus(true)}
          onBlur={() => setTerminalFocus(false)}
        />
      </div>
    </div>
  );
}
