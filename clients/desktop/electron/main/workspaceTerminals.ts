import { existsSync } from 'node:fs';
import { createRequire } from 'node:module';
import { basename, isAbsolute, join } from 'node:path';
import type { WebContents } from 'electron';
import type { IDisposable, IPty, spawn } from 'node-pty';
import {
  IPC_CHANNELS,
  type DesktopLanguage,
  type WorkspaceTerminalAction,
  type WorkspaceTerminalSize,
  type WorkspaceTerminalStartRequest,
  type WorkspaceTerminalStartResult,
} from '../../src/shared/ipc';
import { resolveSafeWorkspaceTarget } from './workspaceFiles';

type TerminalOwner = Pick<WebContents, 'id' | 'isDestroyed' | 'send' | 'setIgnoreMenuShortcuts'>;

interface TerminalSession {
  id: string;
  owner: TerminalOwner;
  pty: IPty;
  subscriptions: IDisposable[];
  pendingLength: number;
  paused: boolean;
}

const require = createRequire(import.meta.url);
const OUTPUT_HIGH_WATERMARK = 256 * 1024;
const OUTPUT_LOW_WATERMARK = 64 * 1024;
const MAX_INPUT_LENGTH = 64 * 1024;

const spawnPty: typeof spawn = (...args) => (require('node-pty') as typeof import('node-pty')).spawn(...args);

function positiveDimension(value: number, max: number): boolean {
  return Number.isInteger(value) && value > 0 && value <= max;
}

function validSize(size: WorkspaceTerminalSize): boolean {
  return positiveDimension(size.cols, 500) && positiveDimension(size.rows, 300);
}

function localShell(): { executable: string; args: string[] } | null {
  if (process.platform === 'win32') {
    const executable = [
      join(process.env.ProgramW6432 ?? process.env.ProgramFiles ?? '', 'PowerShell', '7', 'pwsh.exe'),
      join(process.env.SystemRoot ?? '', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe'),
    ].find((candidate) => isAbsolute(candidate) && existsSync(candidate));
    return executable ? { executable, args: ['-NoLogo', '-NoProfile'] } : null;
  }

  const executable = [process.env.SHELL ?? '', '/bin/bash', '/bin/sh']
    .find((candidate) => isAbsolute(candidate) && existsSync(candidate));
  return executable ? { executable, args: ['-i'] } : null;
}

export class WorkspaceTerminals {
  private readonly sessions = new Map<number, TerminalSession>();

  constructor(private readonly spawnTerminal: typeof spawn = spawnPty) {}

  start(owner: TerminalOwner, request: WorkspaceTerminalStartRequest, language: DesktopLanguage): WorkspaceTerminalStartResult {
    const localize = (zh: string, en: string) => language === 'en-US' ? en : zh;
    if (owner.isDestroyed() || typeof request?.id !== 'string' ||
        !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(request.id) || !validSize(request)) {
      return { ok: false, error: localize('终端启动参数无效。', 'Invalid terminal start request.') };
    }

    const resolved = resolveSafeWorkspaceTarget(
      typeof request.workspacePath === 'string' && isAbsolute(request.workspacePath) ? request.workspacePath : '',
      '',
      language,
    );
    if (!resolved.ok) return { ok: false, error: resolved.error };

    this.closeOwner(owner.id);
    const shell = localShell();
    if (!shell) return { ok: false, error: localize('找不到可用的本地 Shell。', 'No local shell was found.') };

    let pty: IPty;
    try {
      pty = this.spawnTerminal(shell.executable, shell.args, {
        cwd: resolved.target.realPath,
        name: 'xterm-256color',
        cols: request.cols,
        rows: request.rows,
        ...(process.platform === 'win32' ? { useConptyDll: true } : {}),
      });
    } catch (error) {
      console.warn('[workspace-terminal] spawn failed:', error);
      return { ok: false, error: localize('无法启动嵌入式终端。', 'Unable to start the embedded terminal.') };
    }

    const session: TerminalSession = { id: request.id, owner, pty, subscriptions: [], pendingLength: 0, paused: false };
    this.sessions.set(owner.id, session);
    session.subscriptions.push(pty.onData((data) => {
      if (this.sessions.get(owner.id) !== session) return;
      if (owner.isDestroyed()) {
        this.closeOwner(owner.id);
        return;
      }
      session.pendingLength += data.length;
      if (!session.paused && session.pendingLength >= OUTPUT_HIGH_WATERMARK) {
        session.paused = true;
        pty.pause();
      }
      owner.send(IPC_CHANNELS.workspaceTerminalEvent, { id: session.id, type: 'data', data });
    }));
    session.subscriptions.push(pty.onExit(({ exitCode }) => {
      if (this.sessions.get(owner.id) !== session) return;
      this.releaseSession(owner.id);
      if (!owner.isDestroyed()) {
        owner.send(IPC_CHANNELS.workspaceTerminalEvent, { id: session.id, type: 'exit', exitCode });
      }
    }));

    return { ok: true, shellName: basename(shell.executable) };
  }

  act(ownerId: number, action: WorkspaceTerminalAction): void {
    const session = this.sessions.get(ownerId);
    if (!session || session.id !== action?.id) return;

    switch (action.type) {
      case 'write':
        if (typeof action.data === 'string' && action.data.length > 0 && action.data.length <= MAX_INPUT_LENGTH) {
          session.pty.write(action.data);
        }
        break;
      case 'resize':
        if (validSize(action)) session.pty.resize(action.cols, action.rows);
        break;
      case 'ack':
        if (!positiveDimension(action.length, session.pendingLength)) break;
        session.pendingLength -= action.length;
        if (session.paused && session.pendingLength < OUTPUT_LOW_WATERMARK) {
          session.paused = false;
          session.pty.resume();
        }
        break;
      case 'focus':
        if (typeof action.focused === 'boolean') session.owner.setIgnoreMenuShortcuts(action.focused);
        break;
      case 'close':
        this.closeOwner(ownerId);
        break;
    }
  }

  closeOwner(ownerId: number): void {
    const session = this.releaseSession(ownerId);
    if (!session) return;

    try {
      if (session.paused) session.pty.resume();
      session.pty.kill();
    } catch (error) {
      console.warn('[workspace-terminal] cleanup failed:', error);
    }
  }

  dispose(): void {
    for (const ownerId of this.sessions.keys()) this.closeOwner(ownerId);
  }

  private releaseSession(ownerId: number): TerminalSession | undefined {
    const session = this.sessions.get(ownerId);
    if (!session) return undefined;

    this.sessions.delete(ownerId);
    for (const subscription of session.subscriptions) subscription.dispose();
    if (!session.owner.isDestroyed()) session.owner.setIgnoreMenuShortcuts(false);
    return session;
  }
}
