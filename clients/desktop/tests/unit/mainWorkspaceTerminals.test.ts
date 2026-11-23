import { mkdtempSync, realpathSync, rmdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { IPty } from 'node-pty';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { WorkspaceTerminals } from '../../electron/main/workspaceTerminals';
import { IPC_CHANNELS, isLocalBackendConnection } from '../../src/shared/ipc';

const firstId = 'c4613941-850f-4f9c-bb55-7d6588315a3a';
const secondId = 'f953f62a-8920-4d3d-a83d-2e34da7df70a';

function mockTerminal() {
  let receiveData: (data: string) => void = () => undefined;
  let receiveExit: (event: { exitCode: number }) => void = () => undefined;
  const disposeData = vi.fn();
  const disposeExit = vi.fn();
  const implementation = {
    onData: (listener: typeof receiveData) => {
      receiveData = listener;
      return { dispose: disposeData };
    },
    onExit: (listener: typeof receiveExit) => {
      receiveExit = listener;
      return { dispose: disposeExit };
    },
    pause: vi.fn(),
    resume: vi.fn(),
    resize: vi.fn(),
    write: vi.fn(),
    kill: vi.fn(),
  };
  return {
    ...implementation,
    pty: implementation as unknown as IPty,
    sendData: (data: string) => receiveData(data),
    sendExit: (exitCode: number) => receiveExit({ exitCode }),
    disposeData,
    disposeExit,
  };
}

function terminalOwner(id: number) {
  return { id, isDestroyed: () => false, send: vi.fn(), setIgnoreMenuShortcuts: vi.fn() };
}

describe('main-process workspace terminal session', () => {
  let workspacePath: string;

  beforeEach(() => {
    workspacePath = mkdtempSync(join(tmpdir(), 'codexflow terminal; 工作区-'));
  });

  afterEach(() => rmdirSync(workspacePath));

  it('uses the canonical workspace directory and scopes actions to the owner and active terminal', () => {
    const terminal = mockTerminal();
    const spawn = vi.fn().mockReturnValue(terminal.pty);
    const terminals = new WorkspaceTerminals(spawn);
    const owner = terminalOwner(10);
    expect(terminals.start(owner, { id: firstId, workspacePath, cols: 96, rows: 40 }, 'en-US').ok).toBe(true);
    expect(spawn).toHaveBeenCalledWith(expect.any(String), expect.any(Array), expect.objectContaining({
      cwd: realpathSync(workspacePath), cols: 96, rows: 40,
    }));

    terminals.act(11, { id: firstId, type: 'write', data: 'other window' });
    terminals.act(10, { id: secondId, type: 'write', data: 'stale terminal' });
    terminals.act(10, { id: firstId, type: 'write', data: 'x'.repeat(65 * 1024) });
    terminals.act(10, { id: firstId, type: 'resize', cols: 0, rows: 40 });
    expect(terminal.write).not.toHaveBeenCalled();
    expect(terminal.resize).not.toHaveBeenCalled();

    terminals.act(10, { id: firstId, type: 'write', data: 'Get-Location\r' });
    terminals.act(10, { id: firstId, type: 'resize', cols: 110, rows: 28 });
    terminals.act(10, { id: firstId, type: 'focus', focused: true });
    expect(terminal.write).toHaveBeenCalledWith('Get-Location\r');
    expect(terminal.resize).toHaveBeenCalledWith(110, 28);
    expect(owner.setIgnoreMenuShortcuts).toHaveBeenLastCalledWith(true);
    terminals.dispose();
    expect(terminal.kill).toHaveBeenCalledTimes(1);
    expect(owner.setIgnoreMenuShortcuts).toHaveBeenLastCalledWith(false);
  });

  it('pauses output under load and ignores events from a replaced terminal', () => {
    const first = mockTerminal();
    const second = mockTerminal();
    const spawn = vi.fn().mockReturnValueOnce(first.pty).mockReturnValueOnce(second.pty);
    const terminals = new WorkspaceTerminals(spawn);
    const owner = terminalOwner(10);
    const request = { workspacePath, cols: 100, rows: 35 };
    terminals.start(owner, { ...request, id: firstId }, 'en-US');

    const output = 'x'.repeat(256 * 1024);
    first.sendData(output);
    expect(first.pause).toHaveBeenCalledTimes(1);
    terminals.act(owner.id, { id: firstId, type: 'ack', length: output.length + 1 });
    terminals.act(owner.id, { id: firstId, type: 'ack', length: 192 * 1024 });
    expect(first.resume).not.toHaveBeenCalled();
    terminals.act(owner.id, { id: firstId, type: 'ack', length: 1 });
    expect(first.resume).toHaveBeenCalledTimes(1);

    terminals.start(owner, { ...request, id: secondId }, 'en-US');
    expect(first.kill).toHaveBeenCalledTimes(1);
    expect(first.disposeData).toHaveBeenCalledTimes(1);
    first.sendData('old output');
    first.sendExit(0);
    expect(owner.send).toHaveBeenCalledTimes(1);

    second.sendExit(7);
    expect(owner.send).toHaveBeenLastCalledWith(IPC_CHANNELS.workspaceTerminalEvent, { id: secondId, type: 'exit', exitCode: 7 });
    expect(second.disposeExit).toHaveBeenCalledTimes(1);
    terminals.closeOwner(owner.id);
    expect(second.kill).not.toHaveBeenCalled();
  });

  it('rejects relative directories and reports a native module launch failure', () => {
    const spawn = vi.fn(() => { throw new Error('Native test failure'); });
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    try {
      const terminals = new WorkspaceTerminals(spawn);
      const owner = terminalOwner(10);
      const options = { id: firstId, cols: 96, rows: 40 };
      expect(terminals.start(owner, { ...options, workspacePath: '.' }, 'en-US').ok).toBe(false);
      expect(spawn).not.toHaveBeenCalled();
      expect(terminals.start(owner, { ...options, workspacePath }, 'en-US')).toEqual({
        ok: false, error: 'Unable to start the embedded terminal.',
      });
    } finally {
      warn.mockRestore();
    }
  });

  it('only enables local terminals for managed or loopback backends', () => {
    expect(isLocalBackendConnection({ mode: 'managed', targetBaseUrl: null })).toBe(true);
    expect(isLocalBackendConnection({ mode: 'standalone', targetBaseUrl: 'http://[::1]:8080' })).toBe(true);
    expect(isLocalBackendConnection({ mode: 'standalone', targetBaseUrl: 'http://127.0.0.2:8080' })).toBe(true);
    expect(isLocalBackendConnection({ mode: 'standalone', targetBaseUrl: 'http://127.0.0.1.example.com:8080' })).toBe(false);
    expect(isLocalBackendConnection({ mode: 'standalone', targetBaseUrl: 'http://192.0.2.1:8080' })).toBe(false);
  });
});
