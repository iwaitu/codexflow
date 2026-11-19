import { execFile, spawn, spawnSync, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { join, posix, resolve, win32 } from 'node:path';
import type {
  BackendConnectionPublicInfo,
  DependencyInstallStrategy,
  EnvironmentDependencyId,
  EnvironmentDependencyItem,
  EnvironmentInstallEvent,
  EnvironmentInstallStartResult,
  EnvironmentPackageManager,
  EnvironmentReport,
} from '../../src/shared/ipc';
import { resolveBundledLibreOfficePath } from './backendManager';
import { clearCachedShellEnv, resolveEnhancedProcessEnv } from './shellEnv';

const VALID_DEPENDENCY_IDS = new Set<EnvironmentDependencyId>([
  'backend',
  'git',
  'dotnet',
  'node',
  'python',
  'docker',
  'libreoffice',
]);

const REPORT_CACHE_TTL_MS = 10_000;
const PROBE_TIMEOUT_MS = 5_000;
const DOTNET_INSTALL_SCRIPT_URL = 'https://dot.net/v1/dotnet-install.sh';

export interface CommandProbeResult {
  ok: boolean;
  stdout: string;
  stderr: string;
  exitCode: number | null;
}

export type CommandProbeFn = (
  command: string,
  args: string[],
  env: NodeJS.ProcessEnv,
  timeoutMs?: number,
) => Promise<CommandProbeResult>;

export interface InstallRecipe {
  strategy: DependencyInstallStrategy;
  command?: string;
  args?: string[];
  extraEnv?: Record<string, string>;
  useDotnetInstallScript?: boolean;
  preview: string | null;
  downloadUrl: string | null;
}

export interface EnvironmentManagerOptions {
  isPackaged: boolean;
  resourcesPath: string;
  repoRoot?: string;
  platform?: NodeJS.Platform;
  arch?: string;
  homeDir?: string;
  getBackendInfo?: () => BackendConnectionPublicInfo | null;
  onInstallEvent?: (event: EnvironmentInstallEvent) => void;
  probeCommand?: CommandProbeFn;
  pathExists?: (candidate: string) => boolean;
  spawnProcess?: typeof spawn;
  fetchText?: (url: string, signal: AbortSignal) => Promise<string>;
}

export function isValidEnvironmentDependencyId(value: unknown): value is EnvironmentDependencyId {
  return typeof value === 'string' && VALID_DEPENDENCY_IDS.has(value as EnvironmentDependencyId);
}

function defaultProbeCommand(
  command: string,
  args: string[],
  env: NodeJS.ProcessEnv,
  timeoutMs = PROBE_TIMEOUT_MS,
): Promise<CommandProbeResult> {
  return new Promise((resolveProbe) => {
    execFile(
      command,
      args,
      {
        env,
        timeout: timeoutMs,
        windowsHide: true,
        maxBuffer: 512 * 1024,
      },
      (error, stdout, stderr) => {
        if (!error) {
          resolveProbe({
            ok: true,
            stdout: String(stdout ?? '').trim(),
            stderr: String(stderr ?? '').trim(),
            exitCode: 0,
          });
          return;
        }
        const exitCode =
          typeof (error as NodeJS.ErrnoException & { code?: number | string }).code === 'number'
            ? ((error as NodeJS.ErrnoException & { code?: number }).code ?? null)
            : null;
        resolveProbe({
          ok: false,
          stdout: String(stdout ?? '').trim(),
          stderr: String(stderr ?? error.message ?? '').trim(),
          exitCode,
        });
      },
    );
  });
}

async function defaultFetchText(url: string, signal: AbortSignal): Promise<string> {
  const res = await fetch(url, { signal });
  if (!res.ok) {
    throw new Error(`HTTP ${res.status} downloading ${url}`);
  }
  return res.text();
}

export function parseSemverTriplet(raw: string): [number, number, number] | null {
  const match = raw.match(/(\d+)\.(\d+)\.(\d+)/);
  if (!match) {
    return null;
  }
  return [Number(match[1]), Number(match[2]), Number(match[3])];
}

export function compareSemver(a: [number, number, number], b: [number, number, number]): number {
  if (a[0] !== b[0]) return a[0] - b[0];
  if (a[1] !== b[1]) return a[1] - b[1];
  return a[2] - b[2];
}

export function resolveInstallRecipe(
  id: EnvironmentDependencyId,
  platform: NodeJS.Platform,
  packageManager: EnvironmentPackageManager,
): InstallRecipe {
  switch (id) {
    case 'backend':
      return {
        strategy: 'none',
        preview: null,
        downloadUrl: 'https://github.com/iwaitu/codexflow/releases',
      };
    case 'git':
      if (platform === 'darwin') {
        if (packageManager === 'brew') {
          return {
            strategy: 'auto',
            command: 'brew',
            args: ['install', 'git'],
            extraEnv: { HOMEBREW_NO_AUTO_UPDATE: '1' },
            preview: 'brew install git',
            downloadUrl: 'https://git-scm.com/download/mac',
          };
        }
        return {
          strategy: 'manual_cmd',
          preview: 'xcode-select --install',
          downloadUrl: 'https://git-scm.com/download/mac',
        };
      }
      if (platform === 'win32') {
        if (packageManager === 'winget') {
          return {
            strategy: 'auto',
            command: 'winget',
            args: [
              'install',
              '--id',
              'Git.Git',
              '-e',
              '--accept-source-agreements',
              '--accept-package-agreements',
            ],
            preview: 'winget install --id Git.Git -e',
            downloadUrl: 'https://git-scm.com/download/win',
          };
        }
        return {
          strategy: 'download_url',
          preview: null,
          downloadUrl: 'https://git-scm.com/download/win',
        };
      }
      return {
        strategy: 'manual_cmd',
        preview:
          packageManager === 'dnf'
            ? 'sudo dnf install -y git'
            : packageManager === 'pacman'
              ? 'sudo pacman -S --noconfirm git'
              : 'sudo apt-get update && sudo apt-get install -y git',
        downloadUrl: 'https://git-scm.com/download/linux',
      };

    case 'dotnet':
      if (platform === 'darwin' || platform === 'linux') {
        return {
          strategy: 'auto',
          useDotnetInstallScript: true,
          preview: 'curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"',
          downloadUrl: 'https://dotnet.microsoft.com/download/dotnet/10.0',
        };
      }
      if (packageManager === 'winget') {
        return {
          strategy: 'auto',
          command: 'winget',
          args: [
            'install',
            '--id',
            'Microsoft.DotNet.SDK.10',
            '-e',
            '--accept-source-agreements',
            '--accept-package-agreements',
          ],
          preview: 'winget install --id Microsoft.DotNet.SDK.10 -e',
          downloadUrl: 'https://dotnet.microsoft.com/download/dotnet/10.0',
        };
      }
      return {
        strategy: 'download_url',
        preview: null,
        downloadUrl: 'https://dotnet.microsoft.com/download/dotnet/10.0',
      };

    case 'node':
      if (platform === 'darwin' && packageManager === 'brew') {
        return {
          strategy: 'auto',
          command: 'brew',
          args: ['install', 'node@22'],
          extraEnv: { HOMEBREW_NO_AUTO_UPDATE: '1' },
          preview: 'brew install node@22',
          downloadUrl: 'https://nodejs.org/en/download',
        };
      }
      if (platform === 'win32' && packageManager === 'winget') {
        return {
          strategy: 'auto',
          command: 'winget',
          args: [
            'install',
            '--id',
            'OpenJS.NodeJS.LTS',
            '-e',
            '--accept-source-agreements',
            '--accept-package-agreements',
          ],
          preview: 'winget install --id OpenJS.NodeJS.LTS -e',
          downloadUrl: 'https://nodejs.org/en/download',
        };
      }
      return {
        strategy: platform === 'linux' ? 'manual_cmd' : 'download_url',
        preview: platform === 'linux' ? 'curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash - && sudo apt-get install -y nodejs' : null,
        downloadUrl: 'https://nodejs.org/en/download',
      };

    case 'python':
      if (platform === 'darwin' && packageManager === 'brew') {
        return {
          strategy: 'auto',
          command: 'brew',
          args: ['install', 'python@3.12'],
          extraEnv: { HOMEBREW_NO_AUTO_UPDATE: '1' },
          preview: 'brew install python@3.12',
          downloadUrl: 'https://www.python.org/downloads/',
        };
      }
      if (platform === 'win32' && packageManager === 'winget') {
        return {
          strategy: 'auto',
          command: 'winget',
          args: [
            'install',
            '--id',
            'Python.Python.3.12',
            '-e',
            '--accept-source-agreements',
            '--accept-package-agreements',
          ],
          preview: 'winget install --id Python.Python.3.12 -e',
          downloadUrl: 'https://www.python.org/downloads/',
        };
      }
      return {
        strategy: platform === 'linux' ? 'manual_cmd' : 'download_url',
        preview:
          platform === 'linux'
            ? packageManager === 'dnf'
              ? 'sudo dnf install -y python3'
              : packageManager === 'pacman'
                ? 'sudo pacman -S --noconfirm python'
                : 'sudo apt-get update && sudo apt-get install -y python3'
            : null,
        downloadUrl: 'https://www.python.org/downloads/',
      };

    case 'docker':
      if (platform === 'darwin' && packageManager === 'brew') {
        return {
          strategy: 'auto',
          command: 'brew',
          args: ['install', '--cask', 'docker'],
          extraEnv: { HOMEBREW_NO_AUTO_UPDATE: '1' },
          preview: 'brew install --cask docker',
          downloadUrl: 'https://www.docker.com/products/docker-desktop/',
        };
      }
      if (platform === 'win32' && packageManager === 'winget') {
        return {
          strategy: 'auto',
          command: 'winget',
          args: [
            'install',
            '--id',
            'Docker.DockerDesktop',
            '-e',
            '--accept-source-agreements',
            '--accept-package-agreements',
          ],
          preview: 'winget install --id Docker.DockerDesktop -e',
          downloadUrl: 'https://www.docker.com/products/docker-desktop/',
        };
      }
      return {
        strategy: 'download_url',
        preview: null,
        downloadUrl: 'https://www.docker.com/products/docker-desktop/',
      };

    case 'libreoffice':
      if (platform === 'darwin' && packageManager === 'brew') {
        return {
          strategy: 'auto',
          command: 'brew',
          args: ['install', '--cask', 'libreoffice'],
          extraEnv: { HOMEBREW_NO_AUTO_UPDATE: '1' },
          preview: 'brew install --cask libreoffice',
          downloadUrl: 'https://www.libreoffice.org/download/download-libreoffice/',
        };
      }
      if (platform === 'win32' && packageManager === 'winget') {
        return {
          strategy: 'auto',
          command: 'winget',
          args: [
            'install',
            '--id',
            'TheDocumentFoundation.LibreOffice',
            '-e',
            '--accept-source-agreements',
            '--accept-package-agreements',
          ],
          preview: 'winget install --id TheDocumentFoundation.LibreOffice -e',
          downloadUrl: 'https://www.libreoffice.org/download/download-libreoffice/',
        };
      }
      return {
        strategy: platform === 'linux' ? 'manual_cmd' : 'download_url',
        preview:
          platform === 'linux'
            ? packageManager === 'dnf'
              ? 'sudo dnf install -y libreoffice'
              : packageManager === 'pacman'
                ? 'sudo pacman -S --noconfirm libreoffice-fresh'
                : 'sudo apt-get update && sudo apt-get install -y libreoffice'
            : null,
        downloadUrl: 'https://www.libreoffice.org/download/download-libreoffice/',
      };
  }
}

export class EnvironmentManager {
  private readonly isPackaged: boolean;
  private readonly resourcesPath: string;
  private readonly repoRoot: string;
  private readonly platform: NodeJS.Platform;
  private readonly arch: string;
  private readonly homeDir: string;
  private readonly getBackendInfo: () => BackendConnectionPublicInfo | null;
  private readonly onInstallEvent: (event: EnvironmentInstallEvent) => void;
  private readonly probeCommand: CommandProbeFn;
  private readonly pathExists: (candidate: string) => boolean;
  private readonly spawnProcess: typeof spawn;
  private readonly fetchText: (url: string, signal: AbortSignal) => Promise<string>;

  private cachedReport: { report: EnvironmentReport; cachedAtMs: number } | null = null;
  private activeInstall: {
    dependencyId: EnvironmentDependencyId;
    abortController: AbortController;
    child: ChildProcessWithoutNullStreams | null;
    cancelled: boolean;
  } | null = null;

  constructor(options: EnvironmentManagerOptions) {
    this.isPackaged = options.isPackaged;
    this.resourcesPath = options.resourcesPath;
    this.repoRoot = options.repoRoot ?? resolve(__dirname, '../../../..');
    this.platform = options.platform ?? process.platform;
    this.arch = options.arch ?? process.arch;
    this.homeDir = options.homeDir ?? homedir();
    this.getBackendInfo = options.getBackendInfo ?? (() => null);
    this.onInstallEvent = options.onInstallEvent ?? (() => undefined);
    this.probeCommand = options.probeCommand ?? defaultProbeCommand;
    this.pathExists = options.pathExists ?? existsSync;
    this.spawnProcess = options.spawnProcess ?? spawn;
    this.fetchText = options.fetchText ?? defaultFetchText;
  }

  public async inspect(forceRefresh = false): Promise<EnvironmentReport> {
    if (
      !forceRefresh &&
      this.cachedReport &&
      Date.now() - this.cachedReport.cachedAtMs < REPORT_CACHE_TTL_MS
    ) {
      return this.cachedReport.report;
    }

    if (forceRefresh) {
      clearCachedShellEnv();
    }

    const enhanced = resolveEnhancedProcessEnv(process.env, {
      platform: this.platform,
      homeDir: this.homeDir,
      pathExists: this.pathExists,
      forceRefresh,
    });
    const env = enhanced.env;

    const packageManager = await this.detectPackageManager(env);
    const items = await Promise.all([
      this.checkBackend(),
      this.checkGit(env, packageManager),
      this.checkDotnet(env, packageManager),
      this.checkNode(env, packageManager),
      this.checkPython(env, packageManager),
      this.checkDocker(env, packageManager),
      this.checkLibreOffice(env, packageManager),
    ]);

    const report: EnvironmentReport = {
      platform: this.platform,
      arch: this.arch,
      isPackaged: this.isPackaged,
      packageManager,
      shellPathEnhanced: enhanced.enhanced,
      checkedAtIso: new Date().toISOString(),
      items,
    };

    this.cachedReport = { report, cachedAtMs: Date.now() };
    return report;
  }

  public async startInstall(dependencyId: EnvironmentDependencyId): Promise<EnvironmentInstallStartResult> {
    if (!isValidEnvironmentDependencyId(dependencyId)) {
      return { accepted: false, error: '无效的依赖项标识。' };
    }

    if (this.activeInstall) {
      return {
        accepted: false,
        error: `已有安装任务正在运行 (${this.activeInstall.dependencyId})，请等待完成或取消后再试。`,
      };
    }

    const report = await this.inspect(false);
    const recipe = resolveInstallRecipe(dependencyId, this.platform, report.packageManager);
    if (recipe.strategy !== 'auto') {
      return {
        accepted: false,
        error: recipe.preview
          ? `当前平台暂不支持自动安装，请复制命令手动执行：${recipe.preview}`
          : '当前平台暂不支持自动安装，请通过官方下载页面安装。',
      };
    }

    const abortController = new AbortController();
    this.activeInstall = {
      dependencyId,
      abortController,
      child: null,
      cancelled: false,
    };

    this.emitInstall({
      dependencyId,
      phase: 'starting',
      message: `正在准备安装 ${dependencyId}...`,
    });

    void this.runInstallLifecycle(dependencyId, recipe, abortController);
    return { accepted: true };
  }

  public cancelInstall(): boolean {
    const active = this.activeInstall;
    if (!active) {
      return false;
    }
    active.cancelled = true;
    active.abortController.abort();
    if (active.child) {
      this.terminateInstallProcess(active.child);
    }
    return true;
  }

  public dispose(): void {
    this.cancelInstall();
  }

  private emitInstall(event: EnvironmentInstallEvent): void {
    try {
      this.onInstallEvent(event);
    } catch {
      // ignore listener errors
    }
  }

  private async runInstallLifecycle(
    dependencyId: EnvironmentDependencyId,
    recipe: InstallRecipe,
    abortController: AbortController,
  ): Promise<void> {
    let tempDir: string | null = null;
    try {
      const baseEnv = resolveEnhancedProcessEnv(process.env, {
        platform: this.platform,
        homeDir: this.homeDir,
        pathExists: this.pathExists,
      }).env;
      const childEnv: NodeJS.ProcessEnv = {
        ...baseEnv,
        ...recipe.extraEnv,
      };

      let command = recipe.command ?? '';
      let args = recipe.args ?? [];

      if (recipe.useDotnetInstallScript) {
        this.emitInstall({
          dependencyId,
          phase: 'running',
          message: '正在下载 Microsoft 官方 dotnet-install.sh 脚本...',
          logChunk: `[codexflow] Downloading ${DOTNET_INSTALL_SCRIPT_URL}\n`,
        });

        const scriptText = await this.fetchText(DOTNET_INSTALL_SCRIPT_URL, abortController.signal);
        if (this.activeInstall?.cancelled) {
          this.finishCancelled(dependencyId);
          return;
        }

        tempDir = mkdtempSync(join(tmpdir(), 'codexflow-dotnet-install-'));
        const scriptPath = join(tempDir, 'dotnet-install.sh');
        writeFileSync(scriptPath, scriptText, { mode: 0o700, encoding: 'utf-8' });
        command = 'bash';
        args = [scriptPath, '--channel', '10.0', '--install-dir', join(this.homeDir, '.dotnet')];
      }

      if (this.activeInstall?.cancelled) {
        this.finishCancelled(dependencyId);
        return;
      }

      this.emitInstall({
        dependencyId,
        phase: 'running',
        message: `正在执行安装：${recipe.preview ?? command}`,
        logChunk: `$ ${command} ${args.join(' ')}\n`,
      });

      const exitCode = await new Promise<number | null>((resolveExit, rejectExit) => {
        const child = this.spawnProcess(command, args, {
          env: childEnv,
          stdio: ['pipe', 'pipe', 'pipe'],
          detached: this.platform !== 'win32',
          windowsHide: true,
        });

        if (this.activeInstall && this.activeInstall.dependencyId === dependencyId) {
          this.activeInstall.child = child;
        }

        child.stdin.end();
        child.stdout.on('data', (chunk: Buffer) => {
          this.emitInstall({
            dependencyId,
            phase: 'running',
            message: `正在安装 ${dependencyId}...`,
            logChunk: chunk.toString('utf-8'),
          });
        });
        child.stderr.on('data', (chunk: Buffer) => {
          this.emitInstall({
            dependencyId,
            phase: 'running',
            message: `正在安装 ${dependencyId}...`,
            logChunk: chunk.toString('utf-8'),
          });
        });
        child.on('error', (err) => {
          rejectExit(err);
        });
        child.on('exit', (code) => {
          resolveExit(code);
        });
      });

      if (this.activeInstall?.cancelled) {
        this.finishCancelled(dependencyId);
        return;
      }

      if (exitCode !== 0) {
        const refreshed = await this.inspect(true);
        this.emitInstall({
          dependencyId,
          phase: 'failed',
          message: `安装命令退出异常 (code=${String(exitCode)})，请查看日志或尝试手动安装。`,
          exitCode,
          report: refreshed,
        });
        return;
      }

      this.emitInstall({
        dependencyId,
        phase: 'verifying',
        message: `正在刷新环境变量并重新检测 ${dependencyId}...`,
      });

      const refreshed = await this.inspect(true);
      this.emitInstall({
        dependencyId,
        phase: 'succeeded',
        message: `${dependencyId} 安装完成并已刷新环境状态。`,
        exitCode: 0,
        report: refreshed,
      });
    } catch (error) {
      if (this.activeInstall?.cancelled || abortController.signal.aborted) {
        this.finishCancelled(dependencyId);
        return;
      }
      const message = error instanceof Error ? error.message : String(error);
      this.emitInstall({
        dependencyId,
        phase: 'failed',
        message: `安装 ${dependencyId} 失败：${message}`,
        logChunk: `[error] ${message}\n`,
        exitCode: -1,
      });
    } finally {
      if (tempDir) {
        try {
          rmSync(tempDir, { recursive: true, force: true });
        } catch {
          // ignore cleanup errors
        }
      }
      if (this.activeInstall?.dependencyId === dependencyId) {
        this.activeInstall = null;
      }
    }
  }

  private finishCancelled(dependencyId: EnvironmentDependencyId): void {
    this.emitInstall({
      dependencyId,
      phase: 'cancelled',
      message: `已取消 ${dependencyId} 安装。`,
    });
  }

  private terminateInstallProcess(child: ChildProcessWithoutNullStreams): void {
    const pid = child.pid;
    if (!pid) {
      return;
    }
    if (this.platform === 'win32') {
      try {
        spawnSync('taskkill', ['/pid', String(pid), '/T', '/F'], {
          stdio: 'ignore',
          windowsHide: true,
        });
      } catch {
        try {
          child.kill('SIGKILL');
        } catch {
          // already dead
        }
      }
      return;
    }

    try {
      process.kill(-pid, 'SIGTERM');
    } catch {
      try {
        child.kill('SIGTERM');
      } catch {
        // already dead
      }
    }
  }

  private async detectPackageManager(env: NodeJS.ProcessEnv): Promise<EnvironmentPackageManager> {
    if (this.platform === 'darwin') {
      const brew = await this.probeCommand('brew', ['--version'], env);
      return brew.ok ? 'brew' : null;
    }
    if (this.platform === 'win32') {
      const winget = await this.probeCommand('winget', ['--version'], env);
      return winget.ok ? 'winget' : null;
    }
    for (const candidate of ['apt-get', 'dnf', 'pacman'] as const) {
      const probe = await this.probeCommand(candidate, ['--version'], env);
      if (probe.ok) {
        return candidate === 'apt-get' ? 'apt' : candidate;
      }
    }
    return null;
  }

  private async resolveBinaryPath(binaryName: string, env: NodeJS.ProcessEnv): Promise<string | null> {
    const locator = this.platform === 'win32' ? 'where' : 'which';
    const res = await this.probeCommand(locator, [binaryName], env);
    if (!res.ok || !res.stdout) {
      return null;
    }
    return res.stdout.split(/\r?\n/)[0]?.trim() || null;
  }

  private readRequiredDotnetVersion(): string | null {
    if (this.isPackaged) {
      return null;
    }
    try {
      const globalJsonPath = join(this.repoRoot, 'global.json');
      if (!this.pathExists(globalJsonPath)) {
        return null;
      }
      const parsed = JSON.parse(readFileSync(globalJsonPath, 'utf-8')) as {
        sdk?: { version?: string };
      };
      return parsed.sdk?.version?.trim() || null;
    } catch {
      return null;
    }
  }

  private async checkBackend(): Promise<EnvironmentDependencyItem> {
    const exeName = this.platform === 'win32' ? 'CodexFlow.Local.exe' : 'CodexFlow.Local';
    const pathJoin = this.platform === 'win32' ? win32.join : posix.join;
    const backendInfo = this.getBackendInfo();
    const bundled = this.isPackaged;
    const packagedExe = pathJoin(this.resourcesPath, 'backend', exeName);
    const releaseExe = join(this.repoRoot, 'CodexFlow.Local', 'bin', 'Release', 'net10.0', exeName);
    const debugExe = join(this.repoRoot, 'CodexFlow.Local', 'bin', 'Debug', 'net10.0', exeName);

    const resolvedPath = this.isPackaged
      ? this.pathExists(packagedExe)
        ? packagedExe
        : null
      : this.pathExists(releaseExe)
        ? releaseExe
        : this.pathExists(debugExe)
          ? debugExe
          : null;

    if (backendInfo?.status === 'ready') {
      return {
        id: 'backend',
        name: 'CodexFlow 本地后端',
        severity: 'required',
        status: 'ready',
        version: backendInfo.clientVersion ?? null,
        resolvedPath,
        bundled,
        summary: bundled ? '自包含 .NET 10 后端运行正常' : '本地源码后端运行正常',
        detail: backendInfo.targetBaseUrl,
        installStrategy: 'none',
        installCommandPreview: null,
        downloadUrl: null,
      };
    }

    if (!resolvedPath) {
      return {
        id: 'backend',
        name: 'CodexFlow 本地后端',
        severity: 'required',
        status: 'missing',
        version: null,
        resolvedPath: null,
        bundled,
        summary: bundled ? '安装包缺少内置后端程序，请重新下载安装包' : '尚未构建 CodexFlow.Local 后端产物',
        detail: backendInfo?.lastError ?? null,
        installStrategy: bundled ? 'download_url' : 'manual_cmd',
        installCommandPreview: bundled ? null : 'dotnet build CodexFlow.Local -c Release',
        downloadUrl: bundled ? 'https://github.com/iwaitu/codexflow/releases' : null,
      };
    }

    if (backendInfo?.status === 'error') {
      return {
        id: 'backend',
        name: 'CodexFlow 本地后端',
        severity: 'required',
        status: 'error',
        version: backendInfo.clientVersion ?? null,
        resolvedPath,
        bundled,
        summary: '本地托管后端启动失败',
        detail: backendInfo.lastError ?? '请查看 logs/backend.log 获取详细错误信息。',
        installStrategy: 'none',
        installCommandPreview: null,
        downloadUrl: null,
      };
    }

    return {
      id: 'backend',
      name: 'CodexFlow 本地后端',
      severity: 'required',
      status: 'ready',
      version: backendInfo?.clientVersion ?? null,
      resolvedPath,
      bundled,
      summary: bundled ? '已内置自包含后端（正在连接或待命）' : '已检测到后端构建产物',
      detail: backendInfo?.lastError ?? null,
      installStrategy: 'none',
      installCommandPreview: null,
      downloadUrl: null,
    };
  }

  private async checkGit(
    env: NodeJS.ProcessEnv,
    packageManager: EnvironmentPackageManager,
  ): Promise<EnvironmentDependencyItem> {
    const probe = await this.probeCommand('git', ['--version'], env);
    if (probe.ok) {
      const version = probe.stdout.replace(/^git version\s+/i, '').trim() || probe.stdout;
      const resolvedPath = await this.resolveBinaryPath('git', env);
      return {
        id: 'git',
        name: 'Git',
        severity: 'required',
        status: 'ready',
        version,
        resolvedPath,
        bundled: false,
        summary: '代码仓库、变更 Diff 与工作区操作就绪',
        detail: null,
        installStrategy: 'none',
        installCommandPreview: null,
        downloadUrl: null,
      };
    }

    const recipe = resolveInstallRecipe('git', this.platform, packageManager);
    return {
      id: 'git',
      name: 'Git',
      severity: 'required',
      status: 'missing',
      version: null,
      resolvedPath: null,
      bundled: false,
      summary: '未检测到 Git，开发模式下的代码变更对比与仓库功能将受限',
      detail: probe.stderr || null,
      installStrategy: recipe.strategy,
      installCommandPreview: recipe.preview,
      downloadUrl: recipe.downloadUrl,
    };
  }

  private async checkDotnet(
    env: NodeJS.ProcessEnv,
    packageManager: EnvironmentPackageManager,
  ): Promise<EnvironmentDependencyItem> {
    const requiredVersion = this.readRequiredDotnetVersion();
    const probe = await this.probeCommand('dotnet', ['--list-sdks'], env);
    const recipe = resolveInstallRecipe('dotnet', this.platform, packageManager);

    if (probe.ok && probe.stdout) {
      const lines = probe.stdout
        .split(/\r?\n/)
        .map((line) => line.trim())
        .filter(Boolean);
      const versions = lines
        .map((line) => line.split(/\s+/)[0] ?? '')
        .filter((v) => /^\d+\.\d+\.\d+/.test(v));
      const latestVersion = versions[versions.length - 1] ?? null;
      const hasNet10 = versions.some((v) => v.startsWith('10.0.'));
      const resolvedPath = await this.resolveBinaryPath('dotnet', env);

      if (requiredVersion && latestVersion) {
        const reqTriplet = parseSemverTriplet(requiredVersion);
        const matchingNet10 = versions
          .map((v) => ({ raw: v, semver: parseSemverTriplet(v) }))
          .filter((item): item is { raw: string; semver: [number, number, number] } => Boolean(item.semver));
        const satisfiesGlobalJson =
          reqTriplet !== null &&
          matchingNet10.some(
            (item) =>
              item.semver[0] === reqTriplet[0] &&
              item.semver[1] === reqTriplet[1] &&
              compareSemver(item.semver, reqTriplet) >= 0,
          );
        if (!satisfiesGlobalJson) {
          return {
            id: 'dotnet',
            name: '.NET 10 SDK',
            severity: 'recommended',
            status: 'outdated',
            version: latestVersion,
            resolvedPath,
            bundled: false,
            summary: `当前 SDK (${latestVersion}) 低于 global.json 要求的 ${requiredVersion}`,
            detail: `已安装 SDK: ${versions.join(', ')}`,
            installStrategy: recipe.strategy,
            installCommandPreview: recipe.preview,
            downloadUrl: recipe.downloadUrl,
          };
        }
      }

      if (hasNet10) {
        return {
          id: 'dotnet',
          name: '.NET 10 SDK',
          severity: 'recommended',
          status: 'ready',
          version: latestVersion,
          resolvedPath,
          bundled: false,
          summary: '.NET 10 SDK 已就绪',
          detail: versions.length > 1 ? `已安装 SDK: ${versions.join(', ')}` : null,
          installStrategy: 'none',
          installCommandPreview: null,
          downloadUrl: null,
        };
      }

      return {
        id: 'dotnet',
        name: '.NET 10 SDK',
        severity: 'recommended',
        status: 'outdated',
        version: latestVersion,
        resolvedPath,
        bundled: false,
        summary: `仅检测到旧版 .NET SDK (${latestVersion ?? 'unknown'})，建议安装 .NET 10 SDK`,
        detail: null,
        installStrategy: recipe.strategy,
        installCommandPreview: recipe.preview,
        downloadUrl: recipe.downloadUrl,
      };
    }

    return {
      id: 'dotnet',
      name: '.NET 10 SDK',
      severity: 'recommended',
      status: 'missing',
      version: null,
      resolvedPath: null,
      bundled: false,
      summary: this.isPackaged
        ? '未检测到 .NET SDK（应用后端已自包含，开发 C#/.NET 项目时建议安装）'
        : '未检测到 .NET 10 SDK，构建和运行后端源码需要安装',
      detail: probe.stderr || null,
      installStrategy: recipe.strategy,
      installCommandPreview: recipe.preview,
      downloadUrl: recipe.downloadUrl,
    };
  }

  private async checkNode(
    env: NodeJS.ProcessEnv,
    packageManager: EnvironmentPackageManager,
  ): Promise<EnvironmentDependencyItem> {
    const probe = await this.probeCommand('node', ['-v'], env);
    const recipe = resolveInstallRecipe('node', this.platform, packageManager);
    if (probe.ok && probe.stdout) {
      const version = probe.stdout.replace(/^v/i, '').trim();
      const triplet = parseSemverTriplet(version);
      const resolvedPath = await this.resolveBinaryPath('node', env);
      const isOutdated = triplet !== null && triplet[0] < 20;
      return {
        id: 'node',
        name: 'Node.js',
        severity: 'recommended',
        status: isOutdated ? 'outdated' : 'ready',
        version,
        resolvedPath,
        bundled: false,
        summary: isOutdated ? `当前 Node.js (${version}) 版本较旧，建议升级至 22 LTS` : 'Node.js 运行环境已就绪',
        detail: null,
        installStrategy: isOutdated ? recipe.strategy : 'none',
        installCommandPreview: isOutdated ? recipe.preview : null,
        downloadUrl: isOutdated ? recipe.downloadUrl : null,
      };
    }

    return {
      id: 'node',
      name: 'Node.js',
      severity: 'recommended',
      status: 'missing',
      version: null,
      resolvedPath: null,
      bundled: false,
      summary: '未检测到 Node.js（开发前端或 TypeScript/Node 项目时建议安装）',
      detail: probe.stderr || null,
      installStrategy: recipe.strategy,
      installCommandPreview: recipe.preview,
      downloadUrl: recipe.downloadUrl,
    };
  }

  private async checkPython(
    env: NodeJS.ProcessEnv,
    packageManager: EnvironmentPackageManager,
  ): Promise<EnvironmentDependencyItem> {
    const candidates = this.platform === 'win32' ? ['python', 'py'] : ['python3', 'python'];
    for (const bin of candidates) {
      const probe = await this.probeCommand(bin, ['--version'], env);
      if (probe.ok && /Python\s+3\./i.test(probe.stdout || probe.stderr)) {
        const raw = (probe.stdout || probe.stderr).replace(/^Python\s+/i, '').trim();
        const resolvedPath = await this.resolveBinaryPath(bin, env);
        return {
          id: 'python',
          name: 'Python 3',
          severity: 'recommended',
          status: 'ready',
          version: raw,
          resolvedPath,
          bundled: false,
          summary: 'Python 3 脚本与数据处理环境已就绪',
          detail: null,
          installStrategy: 'none',
          installCommandPreview: null,
          downloadUrl: null,
        };
      }
    }

    const recipe = resolveInstallRecipe('python', this.platform, packageManager);
    return {
      id: 'python',
      name: 'Python 3',
      severity: 'recommended',
      status: 'missing',
      version: null,
      resolvedPath: null,
      bundled: false,
      summary: '未检测到 Python 3（运行 Python 项目与辅助脚本时建议安装）',
      detail: null,
      installStrategy: recipe.strategy,
      installCommandPreview: recipe.preview,
      downloadUrl: recipe.downloadUrl,
    };
  }

  private async checkDocker(
    env: NodeJS.ProcessEnv,
    packageManager: EnvironmentPackageManager,
  ): Promise<EnvironmentDependencyItem> {
    const versionProbe = await this.probeCommand('docker', ['--version'], env);
    const recipe = resolveInstallRecipe('docker', this.platform, packageManager);

    if (!versionProbe.ok) {
      return {
        id: 'docker',
        name: 'Docker',
        severity: 'optional',
        status: 'missing',
        version: null,
        resolvedPath: null,
        bundled: false,
        summary: '未安装 Docker（使用容器隔离执行与验收时可选安装）',
        detail: null,
        installStrategy: recipe.strategy,
        installCommandPreview: recipe.preview,
        downloadUrl: recipe.downloadUrl,
      };
    }

    const version = versionProbe.stdout.replace(/^Docker version\s+/i, '').split(',')[0]?.trim() || versionProbe.stdout;
    const resolvedPath = await this.resolveBinaryPath('docker', env);
    const infoProbe = await this.probeCommand('docker', ['info', '--format', '{{.ServerVersion}}'], env, 4_000);
    if (infoProbe.ok && infoProbe.stdout) {
      return {
        id: 'docker',
        name: 'Docker',
        severity: 'optional',
        status: 'ready',
        version,
        resolvedPath,
        bundled: false,
        summary: `Docker 守护进程运行中 (Engine ${infoProbe.stdout})`,
        detail: null,
        installStrategy: 'none',
        installCommandPreview: null,
        downloadUrl: null,
      };
    }

    return {
      id: 'docker',
      name: 'Docker',
      severity: 'optional',
      status: 'stopped',
      version,
      resolvedPath,
      bundled: false,
      summary: '已安装 Docker，但守护进程尚未启动，请先启动 Docker Desktop',
      detail: infoProbe.stderr || null,
      installStrategy: 'none',
      installCommandPreview: null,
      downloadUrl: recipe.downloadUrl,
    };
  }

  private async checkLibreOffice(
    env: NodeJS.ProcessEnv,
    packageManager: EnvironmentPackageManager,
  ): Promise<EnvironmentDependencyItem> {
    const bundledPath =
      process.env.CODEXFLOW_LIBREOFFICE_PATH ||
      resolveBundledLibreOfficePath(this.resourcesPath, this.isPackaged, this.platform, this.pathExists);

    if (bundledPath) {
      return {
        id: 'libreoffice',
        name: 'LibreOffice',
        severity: 'optional',
        status: 'ready',
        version: '内置版本',
        resolvedPath: bundledPath,
        bundled: true,
        summary: '已内置 LibreOffice，Office/PDF 文档解析开箱可用',
        detail: null,
        installStrategy: 'none',
        installCommandPreview: null,
        downloadUrl: null,
      };
    }

    const systemCandidates =
      this.platform === 'darwin'
        ? ['/Applications/LibreOffice.app/Contents/MacOS/soffice', 'soffice']
        : ['soffice', 'libreoffice'];

    for (const candidate of systemCandidates) {
      if (candidate.startsWith('/') && !this.pathExists(candidate)) {
        continue;
      }
      const probe = await this.probeCommand(candidate, ['--version'], env);
      if (probe.ok) {
        const version = probe.stdout.replace(/^LibreOffice\s+/i, '').split(/\s+/)[0] || probe.stdout;
        const resolvedPath = candidate.startsWith('/')
          ? candidate
          : await this.resolveBinaryPath(candidate, env);
        return {
          id: 'libreoffice',
          name: 'LibreOffice',
          severity: 'optional',
          status: 'ready',
          version,
          resolvedPath,
          bundled: false,
          summary: '已检测到系统 LibreOffice，可用于 Office/PDF 文档解析',
          detail: null,
          installStrategy: 'none',
          installCommandPreview: null,
          downloadUrl: null,
        };
      }
    }

    const recipe = resolveInstallRecipe('libreoffice', this.platform, packageManager);
    return {
      id: 'libreoffice',
      name: 'LibreOffice',
      severity: 'optional',
      status: 'missing',
      version: null,
      resolvedPath: null,
      bundled: false,
      summary: '未检测到 LibreOffice（处理旧版 Office 文档转换时可选安装）',
      detail: null,
      installStrategy: recipe.strategy,
      installCommandPreview: recipe.preview,
      downloadUrl: recipe.downloadUrl,
    };
  }
}
