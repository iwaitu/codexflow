import { spawn, spawnSync, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { createWriteStream, existsSync, mkdirSync, readFileSync, type WriteStream } from 'node:fs';
import { homedir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import type {
  BackendConnectionPublicInfo,
  ConnectStandalonePayload,
  PickedTokenFileResult,
  StandaloneDiscoveryResult,
} from '../../src/shared/ipc';
import type { ControlPlaneCredentials } from './controlPlaneProxy';
import type { LocalProxyUpstreamState } from './localBackendProxy';
import { APP_ORIGIN } from './protocol';

interface ReadyPayload {
  port: number;
  pid: number;
  instanceId: string;
}

export interface BackendManagerOptions {
  userDataDir: string;
  resourcesPath: string;
  clientVersion?: string;
  isPackaged: boolean;
  isDev: boolean;
  devRendererOrigin?: string | null;
  readyTimeoutMs?: number;
  getProxyOrigin: () => string | null;
  onChange: (info: BackendConnectionPublicInfo) => void;
}

interface ResolvedLaunchTarget {
  command: string;
  args: string[];
  cwd: string;
  extraEnv?: Record<string, string>;
}

interface DiscoveredEntry {
  baseUrl: string;
  instanceId: string;
  pid: number;
  accessTokenPath: string | null;
  controlTokenPath: string | null;
}

interface PickedTokenEntry {
  role: 'access' | 'control';
  filePath: string;
}

const MAX_FAILURES_IN_WINDOW = 5;
const FAILURE_WINDOW_MS = 120_000;
const STABILITY_RESET_MS = 30_000;
const READY_TIMEOUT_MS = 20_000;

function isPidAlive(pid: number): boolean {
  if (!Number.isFinite(pid) || pid <= 0) {
    return false;
  }
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

function resolveGlobalSettingsDirs(): string[] {
  const dirs: string[] = [];
  if (process.platform === 'win32') {
    const appData = process.env.APPDATA || join(homedir(), 'AppData', 'Roaming');
    dirs.push(join(appData, 'CodexFlow'));
  } else {
    const xdg = process.env.XDG_CONFIG_HOME || join(homedir(), '.config');
    dirs.push(join(xdg, 'CodexFlow'));
    if (process.platform === 'darwin') {
      dirs.push(join(homedir(), 'Library', 'Application Support', 'CodexFlow'));
    }
  }
  return dirs;
}

function isProcessGroupAlive(pgid: number): boolean {
  if (!Number.isFinite(pgid) || pgid <= 0) {
    return false;
  }
  try {
    process.kill(-pgid, 0);
    return true;
  } catch {
    return false;
  }
}

export class BackendManager {
  private readonly options: BackendManagerOptions;
  private readonly dataDirectory: string;
  private readonly logDir: string;
  private readonly readyTimeoutMs: number;
  private logStream: WriteStream | null = null;

  private child: ChildProcessWithoutNullStreams | null = null;
  private processGeneration = 0;
  private readonly expectedStopChildren = new WeakSet<ChildProcessWithoutNullStreams>();
  private readonly timedOutChildren = new WeakSet<ChildProcessWithoutNullStreams>();
  private readonly childStartMs = new WeakMap<ChildProcessWithoutNullStreams, number>();
  private lifecycleChain: Promise<unknown> = Promise.resolve();

  private restartTimer: NodeJS.Timeout | null = null;
  private stabilityTimer: NodeJS.Timeout | null = null;
  private shuttingDown = false;
  private manualStop = false;

  private mode: 'managed' | 'standalone' = 'managed';
  private status: BackendConnectionPublicInfo['status'] = 'starting';
  private targetBaseUrl: string | null = null;
  private accessToken: string | null = null;
  private controlToken: string | null = null;
  private instanceId: string | null = null;
  private controlPlaneAvailable = true;
  private selfApprovalProtection = true;
  private lastError: string | null = null;
  private restartCount = 0;
  private consecutiveFailures = 0;
  private failureTimestamps: number[] = [];

  private readonly discoveredInstances = new Map<string, DiscoveredEntry>();
  private readonly pickedTokenHandles = new Map<string, PickedTokenEntry>();

  constructor(options: BackendManagerOptions) {
    this.options = options;
    this.readyTimeoutMs = options.readyTimeoutMs ?? READY_TIMEOUT_MS;
    this.dataDirectory = process.env.CODEXFLOW_DATA_DIR
      ? resolve(process.env.CODEXFLOW_DATA_DIR)
      : join(options.userDataDir, 'host-data');
    this.logDir = join(options.userDataDir, 'logs');
    mkdirSync(this.dataDirectory, { recursive: true });
    mkdirSync(this.logDir, { recursive: true });
  }

  public getPublicInfo(): BackendConnectionPublicInfo {
    const proxyOrigin = this.options.getProxyOrigin();
    return {
      mode: this.mode,
      status: this.status,
      clientVersion: this.options.clientVersion ?? null,
      baseUrl: this.targetBaseUrl ? (proxyOrigin ?? this.targetBaseUrl) : null,
      targetBaseUrl: this.targetBaseUrl,
      accessToken: this.accessToken,
      instanceId: this.instanceId,
      controlPlaneAvailable: this.controlPlaneAvailable,
      selfApprovalProtection: this.selfApprovalProtection,
      dataDirectory: this.mode === 'managed' ? this.dataDirectory : null,
      lastError: this.lastError,
      restartCount: this.restartCount,
    };
  }

  public getControlPlaneCredentials(): ControlPlaneCredentials {
    return {
      baseUrl: this.targetBaseUrl,
      accessToken: this.accessToken,
      controlToken: this.controlToken,
      controlPlaneAvailable: this.controlPlaneAvailable,
    };
  }

  public getUpstreamState(): LocalProxyUpstreamState {
    return {
      targetBaseUrl: this.targetBaseUrl,
      expectedAccessToken: this.accessToken,
      controlToken: this.controlToken,
    };
  }

  public getTargetBaseUrl(): string | null {
    return this.targetBaseUrl;
  }

  private enqueueLifecycle<T>(operation: () => Promise<T>): Promise<T> {
    const run = this.lifecycleChain.then(operation, operation);
    this.lifecycleChain = run.catch(() => undefined);
    return run;
  }

  public registerPickedTokenFile(role: 'access' | 'control', filePath: string): PickedTokenFileResult | null {
    const full = resolve(filePath);
    if (!existsSync(full)) {
      return null;
    }
    const name = basename(full);
    const lowerName = name.toLowerCase();

    // Prevent assigning control credential files into access-token slots or vice versa.
    if (role === 'access' && lowerName === 'control.token') {
      throw new Error('不允许将 control.token 作为普通访问令牌选择。');
    }
    if (role === 'control' && lowerName === 'host.token') {
      throw new Error('host.token 不是控制面凭据文件，请选择 control.token。');
    }

    const handle = randomUUID();
    this.pickedTokenHandles.set(handle, { role, filePath: full });
    return { handle, displayName: name };
  }

  public startManaged(): Promise<BackendConnectionPublicInfo> {
    return this.enqueueLifecycle(() => this.startManagedInternal(false));
  }

  public restartManaged(): Promise<BackendConnectionPublicInfo> {
    return this.enqueueLifecycle(async () => {
      this.consecutiveFailures = 0;
      this.failureTimestamps = [];
      return this.startManagedInternal(this.mode === 'managed');
    });
  }

  private async startManagedInternal(incrementRestartCount: boolean): Promise<BackendConnectionPublicInfo> {
    if (this.shuttingDown) {
      return this.getPublicInfo();
    }

    this.clearRestartTimer();
    this.clearStabilityTimer();
    this.manualStop = true;
    await this.stopActiveChildProcess();

    if (incrementRestartCount) {
      this.restartCount += 1;
    }

    this.mode = 'managed';
    this.manualStop = false;
    this.status = 'starting';
    this.targetBaseUrl = null;
    this.lastError = null;
    this.controlPlaneAvailable = true;
    this.selfApprovalProtection = true;
    this.emitChange();

    await this.spawnManagedProcess();
    return this.getPublicInfo();
  }

  public connectStandalone(payload: ConnectStandalonePayload): Promise<BackendConnectionPublicInfo> {
    return this.enqueueLifecycle(() => this.connectStandaloneInternal(payload));
  }

  private async connectStandaloneInternal(payload: ConnectStandalonePayload): Promise<BackendConnectionPublicInfo> {
    this.clearRestartTimer();
    this.clearStabilityTimer();
    this.manualStop = true;
    await this.stopActiveChildProcess();

    this.mode = 'standalone';
    this.status = 'starting';
    this.targetBaseUrl = null;
    this.accessToken = null;
    this.controlToken = null;
    this.controlPlaneAvailable = false;
    this.lastError = null;
    this.emitChange();

    let parsedUrl: URL;
    try {
      parsedUrl = new URL(payload.baseUrl.trim());
    } catch {
      return this.failStandalone('后端地址格式无效。');
    }

    if ((parsedUrl.protocol !== 'http:' && parsedUrl.protocol !== 'https:') || parsedUrl.username || parsedUrl.password) {
      return this.failStandalone('仅支持无内嵌凭据的 http/https 后端地址。');
    }
    const normalizedUrl = parsedUrl.origin;

    let accessToken: string | null = null;
    let controlToken: string | null = null;
    let discoveredInstanceId: string | null = null;

    if (payload.discoveryHandle) {
      const entry = this.discoveredInstances.get(payload.discoveryHandle);
      if (!entry) {
        return this.failStandalone('独立实例发现句柄无效或已过期，请重新自动发现。');
      }
      if (entry.baseUrl !== normalizedUrl) {
        return this.failStandalone('所选发现句柄与目标地址不匹配，拒绝连接。');
      }
      discoveredInstanceId = entry.instanceId;
      if (entry.accessTokenPath && existsSync(entry.accessTokenPath)) {
        accessToken = readFileSync(entry.accessTokenPath, 'utf-8').trim() || null;
      }
      if (entry.controlTokenPath && existsSync(entry.controlTokenPath)) {
        controlToken = readFileSync(entry.controlTokenPath, 'utf-8').trim() || null;
      }
    }

    if (!accessToken && payload.accessTokenHandle) {
      const picked = this.pickedTokenHandles.get(payload.accessTokenHandle);
      if (!picked || picked.role !== 'access') {
        return this.failStandalone('访问凭据句柄无效或角色不匹配。');
      }
      if (basename(picked.filePath).toLowerCase() === 'control.token') {
        return this.failStandalone('安全限制：禁止将 control.token 作为普通访问令牌使用。');
      }
      accessToken = readFileSync(picked.filePath, 'utf-8').trim() || null;
    }

    if (!accessToken && payload.accessToken) {
      accessToken = payload.accessToken.trim() || null;
    }

    if (payload.controlTokenHandle) {
      const pickedControl = this.pickedTokenHandles.get(payload.controlTokenHandle);
      if (!pickedControl || pickedControl.role !== 'control') {
        return this.failStandalone('控制凭据句柄无效或角色不匹配。');
      }
      controlToken = readFileSync(pickedControl.filePath, 'utf-8').trim() || null;
    }

    if (!accessToken) {
      return this.failStandalone('必须通过自动发现、文件选择或输入提供有效的访问令牌。');
    }

    if (controlToken && controlToken === accessToken) {
      return this.failStandalone('安全限制：访问令牌不得与控制面令牌相同。');
    }

    try {
      const res = await fetch(`${normalizedUrl}/api/system/status`, {
        headers: { Authorization: `Bearer ${accessToken}` },
        signal: AbortSignal.timeout(8_000),
      });
      if (!res.ok) {
        throw new Error(`系统状态探测失败 (${res.status})`);
      }
      const sys = (await res.json()) as {
        controlPlaneAvailable?: boolean;
        selfApprovalProtection?: boolean;
      };

      this.targetBaseUrl = normalizedUrl;
      this.accessToken = accessToken;
      this.controlPlaneAvailable = Boolean(sys.controlPlaneAvailable) && Boolean(controlToken);
      this.controlToken = this.controlPlaneAvailable ? controlToken : null;
      this.selfApprovalProtection = sys.selfApprovalProtection ?? true;
      this.instanceId = discoveredInstanceId ?? `standalone:${normalizedUrl}`;
      this.status = 'ready';
      this.lastError = null;
      this.emitChange();
      return this.getPublicInfo();
    } catch (error) {
      return this.failStandalone(error instanceof Error ? error.message : String(error));
    }
  }

  private failStandalone(message: string): BackendConnectionPublicInfo {
    this.targetBaseUrl = null;
    this.accessToken = null;
    this.controlToken = null;
    this.controlPlaneAvailable = false;
    this.status = 'error';
    this.lastError = message;
    this.emitChange();
    return this.getPublicInfo();
  }

  public discoverStandaloneInstance(customDataDir?: string): StandaloneDiscoveryResult | null {
    const candidates = [
      customDataDir,
      join(homedir(), '.codexflow'),
      resolve(process.cwd(), '.codexflow'),
      this.dataDirectory,
    ].filter((dir): dir is string => Boolean(dir && dir.trim()));

    const globalCredRoots = resolveGlobalSettingsDirs();

    for (const dir of candidates) {
      const fullDir = resolve(dir);
      const discoveryPath = join(fullDir, 'instance.json');
      if (!existsSync(discoveryPath)) {
        continue;
      }

      try {
        const raw = JSON.parse(readFileSync(discoveryPath, 'utf-8')) as {
          port?: number;
          pid?: number;
          instanceId?: string;
        };
        if (!raw.port || !raw.pid) {
          continue;
        }

        const pidAlive = isPidAlive(raw.pid);
        if (!pidAlive) {
          continue;
        }

        const stateHash = createHash('sha256')
          .update(fullDir.toLowerCase(), 'utf-8')
          .digest('hex')
          .slice(0, 16);

        const credDirs = [
          join(fullDir, 'credentials'),
          fullDir,
          ...globalCredRoots.map((root) => join(root, 'credentials', stateHash)),
        ];

        let accessTokenPath: string | null = null;
        let controlTokenPath: string | null = null;

        for (const credDir of credDirs) {
          const hostCandidate = join(credDir, 'host.token');
          if (!accessTokenPath && existsSync(hostCandidate)) {
            accessTokenPath = hostCandidate;
          }
          const controlCandidate = join(credDir, 'control.token');
          if (!controlTokenPath && existsSync(controlCandidate)) {
            controlTokenPath = controlCandidate;
          }
        }

        const baseUrl = `http://127.0.0.1:${raw.port}`;
        const instanceId = raw.instanceId ?? 'unknown';
        const discoveryHandle = randomUUID();
        this.discoveredInstances.set(discoveryHandle, {
          baseUrl,
          instanceId,
          pid: raw.pid,
          accessTokenPath,
          controlTokenPath,
        });

        return {
          discoveryHandle,
          baseUrl,
          port: raw.port,
          pid: raw.pid,
          pidAlive,
          instanceId,
          dataDirectory: fullDir,
          hasAccessToken: Boolean(accessTokenPath),
          hasControlToken: Boolean(controlTokenPath),
        };
      } catch {
        // ignore unreadable discovery file
      }
    }

    return null;
  }

  public dispose(): Promise<void> {
    return this.enqueueLifecycle(async () => {
      this.shuttingDown = true;
      this.manualStop = true;
      this.clearRestartTimer();
      this.clearStabilityTimer();
      await this.stopActiveChildProcess();
      if (this.logStream) {
        const stream = this.logStream;
        this.logStream = null;
        await new Promise<void>((res) => stream.end(res));
      }
    });
  }

  private async spawnManagedProcess(): Promise<void> {
    if (this.shuttingDown || this.manualStop || this.mode !== 'managed') {
      return;
    }

    const gen = ++this.processGeneration;
    const launch = this.resolveLaunchTarget();
    const accessToken = randomBytes(32).toString('hex');
    const controlToken = randomBytes(32).toString('hex');
    this.accessToken = accessToken;
    this.controlToken = controlToken;

    this.ensureLogStream();

    const env: NodeJS.ProcessEnv = {
      ...process.env,
      ...launch.extraEnv,
      Local__Port: '0',
      Local__DataDirectory: this.dataDirectory,
      Local__PreflightMode: 'Report',
      Local__TokenDelivery: 'Pipe',
      Local__ParentPid: String(process.pid),
      Local__AllowedOrigins__0: APP_ORIGIN,
      ...(this.options.isDev && this.options.devRendererOrigin
        ? { Local__AllowedOrigins__1: this.options.devRendererOrigin }
        : {}),
    };

    await new Promise<void>((resolveReady) => {
      let settled = false;
      let readyReceived = false;
      const finish = () => {
        if (!settled) {
          settled = true;
          resolveReady();
        }
      };

      const childLaunchMs = Date.now();
      const child = spawn(launch.command, launch.args, {
        cwd: launch.cwd,
        env,
        stdio: ['pipe', 'pipe', 'pipe'],
        detached: process.platform !== 'win32',
        windowsHide: true,
      });
      this.child = child;
      this.childStartMs.set(child, childLaunchMs);

      const readyTimeout = setTimeout(() => {
        // Trigger on BOTH initial startup ('starting') AND crash recovery ('reconnecting')
        if (!readyReceived && !settled && gen === this.processGeneration) {
          this.appendLog('[main] CODEXFLOW_READY timeout exceeded, terminating process tree');
          this.timedOutChildren.add(child);
          void this.terminateChildTree(child, false).finally(() => finish());
        }
      }, this.readyTimeoutMs);
      readyTimeout.unref();

      // Deliver normal + control credentials via stdin pipe only (never written to disk or env).
      try {
        child.stdin.write(`${JSON.stringify({ accessToken, controlToken })}\n`, 'utf-8');
        child.stdin.end();
      } catch (err) {
        this.appendLog(`[main] failed writing credentials to stdin: ${String(err)}`);
      }

      let stdoutBuffer = '';
      child.stdout.on('data', (chunk: Buffer) => {
        const text = chunk.toString('utf-8');
        this.appendLog(text);
        if (gen !== this.processGeneration) {
          return;
        }
        stdoutBuffer += text;
        let idx = stdoutBuffer.indexOf('\n');
        while (idx !== -1) {
          const line = stdoutBuffer.slice(0, idx).trim();
          stdoutBuffer = stdoutBuffer.slice(idx + 1);
          if (line.startsWith('CODEXFLOW_READY ')) {
            try {
              const payload = JSON.parse(line.slice('CODEXFLOW_READY '.length)) as ReadyPayload;
              readyReceived = true;
              clearTimeout(readyTimeout);
              this.targetBaseUrl = `http://127.0.0.1:${payload.port}`;
              this.instanceId = payload.instanceId;
              this.status = 'ready';
              this.lastError = null;
              this.armStabilityReset(gen);
              this.emitChange();
              finish();
            } catch (err) {
              this.appendLog(`[main] invalid CODEXFLOW_READY payload: ${String(err)}`);
            }
          }
          idx = stdoutBuffer.indexOf('\n');
        }
      });

      child.stderr.on('data', (chunk: Buffer) => {
        this.appendLog(`[stderr] ${chunk.toString('utf-8')}`);
      });

      child.on('error', (err) => {
        clearTimeout(readyTimeout);
        this.appendLog(`[main] backend spawn error: ${err.message}`);
        if (gen !== this.processGeneration) {
          finish();
          return;
        }
        this.lastError = err.message;
        if (!settled) {
          this.status = 'error';
          this.emitChange();
          finish();
        }
      });

      child.on('exit', (code, signal) => {
        clearTimeout(readyTimeout);
        this.appendLog(`[main] backend exited gen=${gen} code=${String(code)} signal=${String(signal)}`);
        if (this.child === child) {
          this.child = null;
        }

        const expectedExit =
          this.expectedStopChildren.has(child) ||
          gen !== this.processGeneration ||
          this.shuttingDown ||
          this.manualStop ||
          this.mode !== 'managed';

        if (expectedExit) {
          if (!settled && gen === this.processGeneration) {
            this.status = 'disconnected';
            this.emitChange();
          }
          finish();
          return;
        }

        // Sweep any surviving child/grandchild processes from the crashed backend process tree before restarting.
        this.sweepResidualProcessTree(child.pid, this.childStartMs.get(child));

        this.clearStabilityTimer();
        const now = Date.now();
        this.failureTimestamps = this.failureTimestamps.filter((ts) => now - ts < FAILURE_WINDOW_MS);
        this.failureTimestamps.push(now);
        this.consecutiveFailures += 1;
        this.targetBaseUrl = null;

        const wasTimeout = this.timedOutChildren.has(child);
        if (this.failureTimestamps.length >= MAX_FAILURES_IN_WINDOW) {
          this.status = 'error';
          this.lastError = wasTimeout
            ? `托管后端连续 ${this.failureTimestamps.length} 次启动超时/异常，已熔断自动重启，请检查日志后手动重启。`
            : `托管后端短时间内连续异常退出 ${this.failureTimestamps.length} 次，已熔断自动重启，请检查日志后手动重启。`;
          this.emitChange();
          finish();
          return;
        }

        this.status = 'reconnecting';
        this.lastError = wasTimeout
          ? '等待后端 CODEXFLOW_READY 超时，正在自动重启...'
          : `托管后端进程意外退出 (code=${String(code)})，正在自动重启...`;
        this.emitChange();
        finish();
        this.scheduleRestart(gen);
      });
    });
  }

  private armStabilityReset(gen: number): void {
    this.clearStabilityTimer();
    this.stabilityTimer = setTimeout(() => {
      this.stabilityTimer = null;
      if (gen === this.processGeneration && this.status === 'ready') {
        this.consecutiveFailures = 0;
        this.failureTimestamps = [];
      }
    }, STABILITY_RESET_MS);
    this.stabilityTimer.unref();
  }

  private clearStabilityTimer(): void {
    if (this.stabilityTimer) {
      clearTimeout(this.stabilityTimer);
      this.stabilityTimer = null;
    }
  }

  private scheduleRestart(failedGen: number): void {
    this.clearRestartTimer();
    if (this.shuttingDown || this.manualStop || this.mode !== 'managed' || failedGen !== this.processGeneration) {
      return;
    }

    const delayMs = Math.min(15_000, 1000 * 2 ** Math.min(4, Math.max(0, this.consecutiveFailures - 1)));
    this.restartTimer = setTimeout(() => {
      this.restartTimer = null;
      void this.enqueueLifecycle(async () => {
        if (this.shuttingDown || this.manualStop || this.mode !== 'managed' || failedGen !== this.processGeneration) {
          return;
        }
        this.restartCount += 1;
        await this.spawnManagedProcess();
      });
    }, delayMs);
  }

  private clearRestartTimer(): void {
    if (this.restartTimer) {
      clearTimeout(this.restartTimer);
      this.restartTimer = null;
    }
  }

  private sweepResidualProcessTree(pid: number | undefined, startedAtMs?: number): void {
    if (!pid) {
      return;
    }
    if (process.platform === 'win32') {
      try {
        const res = spawnSync('taskkill', ['/pid', String(pid), '/T', '/F'], {
          stdio: 'ignore',
          windowsHide: true,
        });
        // If parent had already exited before taskkill ran, `taskkill /T` refuses because the root PID is missing.
        // Fall back to CIM ParentProcessId BFS traversal so orphaned descendants referencing the dead parent PID are killed too (F3).
        if (res.status !== 0 || !isPidAlive(pid)) {
          this.killWindowsOrphanedDescendants(pid, startedAtMs);
        }
      } catch {
        this.killWindowsOrphanedDescendants(pid, startedAtMs);
      }
      return;
    }

    try {
      process.kill(-pid, 'SIGKILL');
    } catch {
      // process group already gone
    }
  }

  private killWindowsOrphanedDescendants(rootPid: number, startedAtMs?: number): void {
    const minStartIso = startedAtMs
      ? new Date(Math.max(0, startedAtMs - 10_000)).toISOString()
      : new Date(0).toISOString();
    const psScript = [
      '$ErrorActionPreference = "SilentlyContinue";',
      `$minDate = [DateTime]::Parse("${minStartIso}").ToUniversalTime();`,
      '$procs = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, CreationDate;',
      `$queue = New-Object System.Collections.Generic.Queue[int];`,
      `$queue.Enqueue(${rootPid});`,
      '$seen = New-Object System.Collections.Generic.HashSet[int];',
      '[void]$seen.Add(' + rootPid + ');',
      '$victims = New-Object System.Collections.Generic.List[int];',
      'while ($queue.Count -gt 0) {',
      '  $cur = $queue.Dequeue();',
      '  foreach ($p in $procs) {',
      '    if ([int]$p.ParentProcessId -eq $cur) {',
      '      $cid = [int]$p.ProcessId;',
      '      if (-not $seen.Contains($cid)) {',
      '        [void]$seen.Add($cid);',
      '        if (-not $p.CreationDate -or $p.CreationDate.ToUniversalTime() -ge $minDate) {',
      '          [void]$victims.Add($cid);',
      '          $queue.Enqueue($cid);',
      '        }',
      '      }',
      '    }',
      '  }',
      '}',
      'foreach ($v in $victims) { Stop-Process -Id $v -Force -ErrorAction SilentlyContinue }',
      'foreach ($v in $victims) {',
      '  $alive = $true;',
      '  for ($i = 0; $i -lt 30; $i++) {',
      '    if (-not (Get-Process -Id $v -ErrorAction SilentlyContinue)) { $alive = $false; break }',
      '    Start-Sleep -Milliseconds 50',
      '  }',
      '  if ($alive) { Write-Output "SURVIVED:$v" } else { Write-Output "KILLED:$v" }',
      '}',
    ].join(' ');

    try {
      const psResult = spawnSync(
        'powershell.exe',
        ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', psScript],
        { encoding: 'utf-8', windowsHide: true },
      );
      if (psResult.status !== 0 || (psResult.stdout && psResult.stdout.includes('SURVIVED:'))) {
        this.appendLog(`[main] Windows orphan cleanup warning status=${String(psResult.status)} out=${(psResult.stdout || '').trim()}`);
      }
    } catch (err) {
      this.appendLog(`[main] Windows orphan cleanup invocation error: ${String(err)}`);
    }
  }

  private async stopActiveChildProcess(): Promise<void> {
    const child = this.child;
    if (!child) {
      return;
    }
    this.child = null;
    await this.terminateChildTree(child, true);
  }

  private async terminateChildTree(
    child: ChildProcessWithoutNullStreams,
    markExpectedStop = true,
  ): Promise<void> {
    if (markExpectedStop) {
      this.expectedStopChildren.add(child);
    }

    if (child.exitCode !== null || child.signalCode !== null) {
      this.sweepResidualProcessTree(child.pid, this.childStartMs.get(child));
      return;
    }

    await new Promise<void>((resolveStop) => {
      let done = false;
      let parentExited = false;
      const finish = () => {
        if (!done) {
          done = true;
          resolveStop();
        }
      };

      child.once('exit', () => {
        parentExited = true;
      });

      const pid = child.pid;
      if (!pid) {
        finish();
        return;
      }

      if (process.platform === 'win32') {
        try {
          const result = spawnSync('taskkill', ['/pid', String(pid), '/T', '/F'], {
            stdio: 'ignore',
            windowsHide: true,
          });
          if (result.status !== 0 && isPidAlive(pid)) {
            try {
              process.kill(pid, 'SIGKILL');
            } catch {
              // ignore if already exited
            }
          }
        } catch {
          try {
            process.kill(pid, 'SIGKILL');
          } catch {
            // ignore
          }
        }

        const startWait = Date.now();
        const pollExit = () => {
          if (done || (parentExited && !isPidAlive(pid)) || Date.now() - startWait > 4000) {
            finish();
            return;
          }
          setTimeout(pollExit, 50).unref();
        };
        setTimeout(pollExit, 30).unref();
        return;
      }

      // POSIX: signal the entire dedicated process group (-pid), and do NOT finish early
      // just because the parent exited while grandchildren in the same group survive.
      try {
        process.kill(-pid, 'SIGTERM');
      } catch {
        try {
          child.kill('SIGTERM');
        } catch {
          finish();
          return;
        }
      }

      const startWait = Date.now();
      let sentSigKill = false;
      const pollGroup = () => {
        if (done) {
          return;
        }
        const groupAlive = isProcessGroupAlive(pid);
        if (parentExited && !groupAlive) {
          finish();
          return;
        }
        const elapsed = Date.now() - startWait;
        if (!sentSigKill && elapsed >= 2500) {
          sentSigKill = true;
          try {
            process.kill(-pid, 'SIGKILL');
          } catch {
            try {
              child.kill('SIGKILL');
            } catch {
              // already gone
            }
          }
        }
        if (elapsed >= 4500) {
          finish();
          return;
        }
        setTimeout(pollGroup, 50).unref();
      };
      setTimeout(pollGroup, 40).unref();
    });
  }

  private resolveLaunchTarget(): ResolvedLaunchTarget {
    if (process.env.CODEXFLOW_BACKEND_BIN) {
      const bin = resolve(process.env.CODEXFLOW_BACKEND_BIN);
      if (bin.endsWith('.js') || bin.endsWith('.mjs')) {
        return {
          command: process.execPath,
          args: [bin],
          cwd: dirname(bin),
          extraEnv: { ELECTRON_RUN_AS_NODE: '1' },
        };
      }
      if (bin.endsWith('.dll')) {
        return { command: 'dotnet', args: [bin], cwd: dirname(bin) };
      }
      return { command: bin, args: [], cwd: dirname(bin) };
    }

    const exeName = process.platform === 'win32' ? 'CodexFlow.Local.exe' : 'CodexFlow.Local';
    if (this.options.isPackaged) {
      const packagedExe = join(this.options.resourcesPath, 'backend', exeName);
      return { command: packagedExe, args: [], cwd: dirname(packagedExe) };
    }

    const repoRoot = resolve(__dirname, '../../../..');
    const releaseExe = join(repoRoot, 'CodexFlow.Local', 'bin', 'Release', 'net10.0', exeName);
    if (existsSync(releaseExe)) {
      return { command: releaseExe, args: [], cwd: dirname(releaseExe) };
    }

    const debugExe = join(repoRoot, 'CodexFlow.Local', 'bin', 'Debug', 'net10.0', exeName);
    if (existsSync(debugExe)) {
      return { command: debugExe, args: [], cwd: dirname(debugExe) };
    }

    const csprojDir = join(repoRoot, 'CodexFlow.Local');
    return {
      command: 'dotnet',
      args: ['run', '--project', csprojDir],
      cwd: csprojDir,
    };
  }

  private ensureLogStream(): void {
    if (this.logStream) {
      return;
    }
    this.logStream = createWriteStream(join(this.logDir, 'backend.log'), { flags: 'a' });
  }

  private appendLog(text: string): void {
    this.ensureLogStream();
    this.logStream?.write(text.endsWith('\n') ? text : `${text}\n`);
  }

  private emitChange(): void {
    this.options.onChange(this.getPublicInfo());
  }
}
