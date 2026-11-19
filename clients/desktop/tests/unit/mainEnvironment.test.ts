import { EventEmitter } from 'node:events';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { PassThrough } from 'node:stream';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  EnvironmentManager,
  isValidEnvironmentDependencyId,
  resolveInstallRecipe,
  type CommandProbeFn,
} from '../../electron/main/environmentManager';
import {
  clearCachedShellEnv,
  mergeSearchPaths,
  resolveEnhancedProcessEnv,
} from '../../electron/main/shellEnv';

const require = createRequire(import.meta.url);
const macosSign = require('../../scripts/macos-sign.cjs') as {
  isMachOFile: (filePath: string) => boolean;
  collectInsideOutSignTargets: (appPath: string) => {
    machOFiles: string[];
    nestedBundles: string[];
  };
  adHocSignAppBundle: (
    appPath: string,
    options?: {
      bundleIdentifier?: string;
      exec?: (cmd: string, args: string[]) => void;
    },
  ) => void;
};

const tempDirs: string[] = [];

afterEach(() => {
  clearCachedShellEnv();
  while (tempDirs.length > 0) {
    const dir = tempDirs.pop();
    if (dir) {
      rmSync(dir, { recursive: true, force: true });
    }
  }
});

describe('shellEnv', () => {
  it('merges and deduplicates PATH entries while preserving precedence', () => {
    const { mergedPath, addedPaths } = mergeSearchPaths(
      '/usr/bin:/bin',
      [
        ['/usr/bin', '/bin'],
        ['/opt/homebrew/bin', '/usr/bin', '/Users/test/.dotnet'],
        ['/usr/local/bin'],
      ],
      'darwin',
    );
    expect(mergedPath).toBe('/usr/bin:/bin:/opt/homebrew/bin:/Users/test/.dotnet:/usr/local/bin');
    expect(addedPaths).toEqual(['/opt/homebrew/bin', '/Users/test/.dotnet', '/usr/local/bin']);
  });

  it('enhances macOS GUI env with login shell PATH and auto-detected DOTNET_ROOT', () => {
    const existing = new Set([
      '/bin/zsh',
      '/opt/homebrew/bin',
      '/Users/tester/.dotnet',
      '/Users/tester/.dotnet/dotnet',
    ]);
    const result = resolveEnhancedProcessEnv(
      { PATH: '/usr/bin:/bin', SHELL: '/bin/zsh' },
      {
        platform: 'darwin',
        homeDir: '/Users/tester',
        pathExists: (candidate) => existing.has(candidate),
        probeLoginShellPath: () => '/opt/homebrew/bin:/usr/bin:/bin',
        forceRefresh: true,
      },
    );

    expect(result.enhanced).toBe(true);
    expect(result.env.PATH).toContain('/opt/homebrew/bin');
    expect(result.env.PATH).toContain('/Users/tester/.dotnet');
    expect(result.env.DOTNET_ROOT).toBe('/Users/tester/.dotnet');
  });
});

describe('EnvironmentManager', () => {
  it('validates allowlisted dependency IDs strictly', () => {
    expect(isValidEnvironmentDependencyId('git')).toBe(true);
    expect(isValidEnvironmentDependencyId('dotnet')).toBe(true);
    expect(isValidEnvironmentDependencyId('git; rm -rf /')).toBe(false);
    expect(isValidEnvironmentDependencyId('unknown')).toBe(false);
    expect(isValidEnvironmentDependencyId(null)).toBe(false);
  });

  it('selects platform-specific safe install recipes', () => {
    const macDotnet = resolveInstallRecipe('dotnet', 'darwin', null);
    expect(macDotnet.strategy).toBe('auto');
    expect(macDotnet.useDotnetInstallScript).toBe(true);

    const macGitBrew = resolveInstallRecipe('git', 'darwin', 'brew');
    expect(macGitBrew.strategy).toBe('auto');
    expect(macGitBrew.command).toBe('brew');
    expect(macGitBrew.args).toEqual(['install', 'git']);

    const winNodeWinget = resolveInstallRecipe('node', 'win32', 'winget');
    expect(winNodeWinget.strategy).toBe('auto');
    expect(winNodeWinget.command).toBe('winget');
    expect(winNodeWinget.args).toContain('OpenJS.NodeJS.LTS');
  });

  it('inspects all dependencies and differentiates stopped Docker vs missing tools', async () => {
    const probeCommand: CommandProbeFn = async (cmd, args) => {
      if (cmd === 'brew') return { ok: true, stdout: 'Homebrew 4.4.0', stderr: '', exitCode: 0 };
      if (cmd === 'git') return { ok: true, stdout: 'git version 2.45.0', stderr: '', exitCode: 0 };
      if (cmd === 'dotnet') {
        return { ok: true, stdout: '8.0.301 [/usr/local/share/dotnet/sdk]', stderr: '', exitCode: 0 };
      }
      if (cmd === 'node') return { ok: true, stdout: 'v22.14.0', stderr: '', exitCode: 0 };
      if (cmd === 'python3') return { ok: false, stdout: '', stderr: 'not found', exitCode: 127 };
      if (cmd === 'python') return { ok: false, stdout: '', stderr: 'not found', exitCode: 127 };
      if (cmd === 'docker' && args[0] === '--version') {
        return { ok: true, stdout: 'Docker version 27.3.1, build ce12230', stderr: '', exitCode: 0 };
      }
      if (cmd === 'docker' && args[0] === 'info') {
        return { ok: false, stdout: '', stderr: 'Cannot connect to the Docker daemon', exitCode: 1 };
      }
      if (cmd === 'which') {
        return { ok: true, stdout: `/opt/homebrew/bin/${args[0]}`, stderr: '', exitCode: 0 };
      }
      return { ok: false, stdout: '', stderr: 'not found', exitCode: 1 };
    };

    const existingPaths = new Set([
      '/app/resources/backend/CodexFlow.Local',
      '/app/resources/libreoffice/LibreOffice.app/Contents/MacOS/soffice',
    ]);

    const manager = new EnvironmentManager({
      isPackaged: true,
      resourcesPath: '/app/resources',
      platform: 'darwin',
      arch: 'arm64',
      homeDir: '/Users/tester',
      pathExists: (p) => existingPaths.has(p),
      probeCommand,
      getBackendInfo: () => ({
        mode: 'managed',
        status: 'ready',
        clientVersion: '0.1.14',
        baseUrl: 'http://127.0.0.1:41000',
        targetBaseUrl: 'http://127.0.0.1:52060',
        accessToken: 'tok',
        instanceId: 'inst-1',
        controlPlaneAvailable: true,
        selfApprovalProtection: true,
        dataDirectory: '/tmp/data',
        lastError: null,
        restartCount: 0,
      }),
    });

    const report = await manager.inspect(true);
    expect(report.packageManager).toBe('brew');
    const byId = new Map(report.items.map((item) => [item.id, item]));
    expect(byId.get('backend')?.status).toBe('ready');
    expect(byId.get('backend')?.bundled).toBe(true);
    expect(byId.get('git')?.status).toBe('ready');
    expect(byId.get('git')?.version).toBe('2.45.0');
    expect(byId.get('dotnet')?.status).toBe('outdated');
    expect(byId.get('dotnet')?.installStrategy).toBe('auto');
    expect(byId.get('node')?.status).toBe('ready');
    expect(byId.get('python')?.status).toBe('missing');
    expect(byId.get('python')?.installStrategy).toBe('auto');
    expect(byId.get('docker')?.status).toBe('stopped');
    expect(byId.get('libreoffice')?.status).toBe('ready');
    expect(byId.get('libreoffice')?.bundled).toBe(true);
  });

  it('prevents concurrent installs and streams progress events', async () => {
    const events: Array<{ phase: string; dependencyId: string }> = [];
    const fakeChild = Object.assign(new EventEmitter(), {
      pid: 99999,
      stdin: new PassThrough(),
      stdout: new PassThrough(),
      stderr: new PassThrough(),
      kill: vi.fn(),
    });

    const spawnMock = vi.fn(() => fakeChild) as unknown as typeof import('node:child_process').spawn;
    const probeCommand: CommandProbeFn = async (cmd) => {
      if (cmd === 'brew') return { ok: true, stdout: 'Homebrew 4.4.0', stderr: '', exitCode: 0 };
      return { ok: false, stdout: '', stderr: '', exitCode: 1 };
    };

    const manager = new EnvironmentManager({
      isPackaged: true,
      resourcesPath: '/app/resources',
      platform: 'darwin',
      arch: 'arm64',
      homeDir: '/Users/tester',
      pathExists: () => false,
      probeCommand,
      spawnProcess: spawnMock,
      onInstallEvent: (evt) => events.push({ phase: evt.phase, dependencyId: evt.dependencyId }),
    });

    const first = await manager.startInstall('git');
    expect(first.accepted).toBe(true);

    const second = await manager.startInstall('node');
    expect(second.accepted).toBe(false);
    expect(second.error).toContain('已有安装任务正在运行');

    fakeChild.stdout.write('Installing git...\n');
    fakeChild.emit('exit', 0);

    await vi.waitFor(() => {
      expect(events.some((e) => e.phase === 'succeeded' && e.dependencyId === 'git')).toBe(true);
    });
  });
});

describe('macos-sign script', () => {
  it('identifies Mach-O files and signs inside-out before verifying', () => {
    const root = mkdtempSync(join(tmpdir(), 'codexflow-sign-test-'));
    tempDirs.push(root);

    const appDir = join(root, 'CodexFlow Desktop.app');
    const mainMacOs = join(appDir, 'Contents', 'MacOS');
    const backendDir = join(appDir, 'Contents', 'Resources', 'backend');
    const frameworkDir = join(appDir, 'Contents', 'Frameworks', 'Sample.framework');
    mkdirSync(mainMacOs, { recursive: true });
    mkdirSync(backendDir, { recursive: true });
    mkdirSync(frameworkDir, { recursive: true });

    const machOBuf = Buffer.from([0xcf, 0xfa, 0xed, 0xfe, 0x00, 0x00, 0x00, 0x00]);
    const dllBuf = Buffer.from([0x4d, 0x5a, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00]);

    const mainExe = join(mainMacOs, 'CodexFlow Desktop');
    const backendBin = join(backendDir, 'CodexFlow.Local');
    const backendDll = join(backendDir, 'CodexFlow.Local.dll');
    const fwLib = join(frameworkDir, 'Sample');

    writeFileSync(mainExe, machOBuf);
    writeFileSync(backendBin, machOBuf);
    writeFileSync(backendDll, dllBuf);
    writeFileSync(fwLib, machOBuf);

    expect(macosSign.isMachOFile(backendBin)).toBe(true);
    expect(macosSign.isMachOFile(backendDll)).toBe(false);

    const calls: Array<{ cmd: string; args: string[] }> = [];
    macosSign.adHocSignAppBundle(appDir, {
      bundleIdentifier: 'com.codexflow.desktop',
      exec: (cmd, args) => calls.push({ cmd, args }),
    });

    const codesignCalls = calls.filter((c) => c.cmd === 'codesign');
    const signedTargets = codesignCalls
      .filter((c) => c.args.includes('--sign'))
      .map((c) => c.args[c.args.length - 1]);

    // Inner Mach-O binaries must be signed before nested bundles, and outer .app must be signed last.
    expect(signedTargets).toContain(backendBin);
    expect(signedTargets).not.toContain(backendDll);
    expect(signedTargets.indexOf(fwLib)).toBeLessThan(signedTargets.indexOf(frameworkDir));
    expect(signedTargets[signedTargets.length - 1]).toBe(appDir);

    const verifyCall = codesignCalls[codesignCalls.length - 1];
    expect(verifyCall?.args).toEqual(['--verify', '--deep', '--strict', '--verbose=2', appDir]);
  });
});
