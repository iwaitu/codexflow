import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import {
  BackendManager,
  resolveBundledLibreOfficePath,
  resolveManagedBackendEnvironment,
} from '../../electron/main/backendManager';
import { LocalBackendProxy } from '../../electron/main/localBackendProxy';
import {
  APP_ORIGIN,
  buildContentSecurityPolicy,
  resolveSafeAppPath,
  validateIpcCaller,
} from '../../electron/main/securityUtils';
import { UpdaterManager } from '../../electron/main/updaterManager';

describe('Main-process security boundaries & lifecycle (B1, B3, B4, R3)', () => {
  it('B1: BackendManager blocks control.token in access slots and never leaks controlToken in public info', async () => {
    const tempDir = mkdtempSync(join(tmpdir(), 'cf-b1-'));
    const controlFile = join(tempDir, 'control.token');
    const hostFile = join(tempDir, 'host.token');
    writeFileSync(controlFile, 'SECRET_CONTROL_TOKEN_DO_NOT_LEAK', 'utf-8');
    writeFileSync(hostFile, 'NORMAL_HOST_TOKEN', 'utf-8');

    const originalFetch = globalThis.fetch;
    const fetchSpy = vi.fn();
    globalThis.fetch = fetchSpy as unknown as typeof fetch;

    try {
      const mgr = new BackendManager({
        userDataDir: tempDir,
        resourcesPath: tempDir,
        isPackaged: false,
        isDev: false,
        getProxyOrigin: () => 'http://127.0.0.1:61234',
        onChange: () => {},
      });

      expect(() => mgr.registerPickedTokenFile('access', controlFile)).toThrow(/control\.token/);
      expect(() => mgr.registerPickedTokenFile('control', hostFile)).toThrow(/host\.token/);

      const ctrlPicked = mgr.registerPickedTokenFile('control', controlFile);
      expect(ctrlPicked).not.toBeNull();

      const info = await mgr.connectStandalone({
        baseUrl: 'http://127.0.0.1:59998',
        accessTokenHandle: ctrlPicked!.handle,
      });

      expect(info.status).toBe('error');
      expect(info.accessToken).toBeNull();
      expect(JSON.stringify(info)).not.toContain('SECRET_CONTROL_TOKEN_DO_NOT_LEAK');
      expect(fetchSpy).not.toHaveBeenCalled();

      const hostPicked = mgr.registerPickedTokenFile('access', hostFile);
      writeFileSync(controlFile, 'NORMAL_HOST_TOKEN', 'utf-8');
      const ctrlSame = mgr.registerPickedTokenFile('control', controlFile);
      const dupInfo = await mgr.connectStandalone({
        baseUrl: 'http://127.0.0.1:59998',
        accessTokenHandle: hostPicked!.handle,
        controlTokenHandle: ctrlSame!.handle,
      });
      expect(dupInfo.status).toBe('error');
      expect(dupInfo.accessToken).toBeNull();
      expect(fetchSpy).not.toHaveBeenCalled();

      await mgr.dispose();
    } finally {
      globalThis.fetch = originalFetch;
      rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('B3 & B4: resolveSafeAppPath blocks traversal and LocalBackendProxy enforces origin, access token, and blocks controlToken', async () => {
    const tempDir = mkdtempSync(join(tmpdir(), 'cf-b3-'));
    const upstream = createServer((_req, res) => {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ ok: true }));
    });
    await new Promise<void>((r) => upstream.listen(0, '127.0.0.1', r));
    const addr = upstream.address() as { port: number };

    const proxy = new LocalBackendProxy(
      () => ({
        targetBaseUrl: `http://127.0.0.1:${addr.port}`,
        expectedAccessToken: 'acc-token',
        controlToken: 'ctrl-token',
      }),
      new Set([APP_ORIGIN]),
    );
    const proxyOrigin = await proxy.start();

    try {
      const rootDir = join(tempDir, 'renderer');
      expect(resolveSafeAppPath(rootDir, `/${rootDir.replace(/\\/g, '%5C')}-secret%5Csecret.txt`).ok).toBe(false);
      expect(resolveSafeAppPath(rootDir, '/../etc/passwd').ok).toBe(false);
      expect(resolveSafeAppPath(rootDir, '//server/share/secret.txt').ok).toBe(false);
      expect(resolveSafeAppPath(rootDir, '/assets/index.js').ok).toBe(true);

      const csp = buildContentSecurityPolicy(proxyOrigin, false);
      expect(csp).toContain(`connect-src 'self' ${proxyOrigin}`);
      expect(csp).not.toContain('127.0.0.1:*');
      expect(csp).not.toContain('localhost:*');

      // Real HTTP verification through LocalBackendProxy
      const untrustedOrigin = await fetch(`${proxyOrigin}/api/system/status`, {
        headers: { Origin: 'https://evil.example', Authorization: 'Bearer acc-token' },
      });
      expect(untrustedOrigin.status).toBe(403);

      const controlForbidden = await fetch(`${proxyOrigin}/api/system/status`, {
        headers: { Origin: APP_ORIGIN, Authorization: 'Bearer ctrl-token' },
      });
      expect(controlForbidden.status).toBe(403);

      const badAuth = await fetch(`${proxyOrigin}/api/system/status`, {
        headers: { Origin: APP_ORIGIN, Authorization: 'Bearer wrong' },
      });
      expect(badAuth.status).toBe(401);

      const okRes = await fetch(`${proxyOrigin}/api/system/status`, {
        headers: { Origin: APP_ORIGIN, Authorization: 'Bearer acc-token' },
      });
      expect(okRes.status).toBe(200);
      expect(await okRes.json()).toEqual({ ok: true });
    } finally {
      await proxy.close();
      await new Promise<void>((r) => upstream.close(() => r()));
      rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('R3: READY timeout kills hanging backend in both starting and reconnecting phases without wedging lifecycleQueue', async () => {
    const tempDir = mkdtempSync(join(tmpdir(), 'cf-r3-'));
    const hangScript = join(tempDir, 'hang.mjs');
    // Read stdin until end, then silently hang forever without emitting CODEXFLOW_READY
    writeFileSync(
      hangScript,
      'process.stdin.resume(); process.stdin.on("end", () => { setInterval(() => {}, 60000); });\n',
      'utf-8',
    );

    const prevBin = process.env.CODEXFLOW_BACKEND_BIN;
    process.env.CODEXFLOW_BACKEND_BIN = hangScript;
    const states: string[] = [];

    const mgr = new BackendManager({
      userDataDir: tempDir,
      resourcesPath: tempDir,
      isPackaged: false,
      isDev: false,
      readyTimeoutMs: 120,
      getProxyOrigin: () => 'http://127.0.0.1:61234',
      onChange: (info) => states.push(info.status),
    });

    try {
      const info = await mgr.startManaged();
      expect(info.status).toBe('reconnecting');
      expect(info.lastError).toContain('超时');

      // Wait for automatic retry in 'reconnecting' phase to also hit READY timeout and emit second 'reconnecting'
      await vi.waitFor(
        () => {
          expect(mgr.getPublicInfo().restartCount).toBeGreaterThanOrEqual(1);
          expect(states.filter((s) => s === 'reconnecting').length).toBeGreaterThanOrEqual(2);
        },
        { timeout: 10000 },
      );

      // Ensure lifecycle queue is NOT wedged and dispose completes promptly
      await mgr.dispose();
    } finally {
      if (prevBin === undefined) {
        delete process.env.CODEXFLOW_BACKEND_BIN;
      } else {
        process.env.CODEXFLOW_BACKEND_BIN = prevBin;
      }
      rmSync(tempDir, { recursive: true, force: true });
    }
  }, 15_000);

  it('B2: validateIpcCaller rejects non-main WebContents, subframes, and untrusted origins while accepting app://codexflow', () => {
    const allowed = new Set([APP_ORIGIN]);
    expect(() =>
      validateIpcCaller({
        hasWindow: true,
        windowDestroyed: false,
        senderMatchesWindow: true,
        hasSenderFrame: true,
        senderFrameMatchesMainFrame: true,
        frameUrl: `${APP_ORIGIN}/index.html`,
        allowedOrigins: allowed,
      }),
    ).not.toThrow();

    expect(() =>
      validateIpcCaller({
        hasWindow: true,
        windowDestroyed: false,
        senderMatchesWindow: false,
        hasSenderFrame: true,
        senderFrameMatchesMainFrame: true,
        frameUrl: `${APP_ORIGIN}/index.html`,
        allowedOrigins: allowed,
      }),
    ).toThrow(/WebContents/);

    expect(() =>
      validateIpcCaller({
        hasWindow: true,
        windowDestroyed: false,
        senderMatchesWindow: true,
        hasSenderFrame: true,
        senderFrameMatchesMainFrame: false,
        frameUrl: `${APP_ORIGIN}/index.html`,
        allowedOrigins: allowed,
      }),
    ).toThrow(/non-main frame/);

    expect(() =>
      validateIpcCaller({
        hasWindow: true,
        windowDestroyed: false,
        senderMatchesWindow: true,
        hasSenderFrame: true,
        senderFrameMatchesMainFrame: true,
        frameUrl: 'https://evil.example/phish.html',
        allowedOrigins: allowed,
      }),
    ).toThrow(/untrusted frame origin/);
  });

  it('F2: LocalBackendProxy.close() immediately aborts active SSE streams without blocking shutdown', async () => {
    const sseSockets = new Set<import('node:http').ServerResponse>();
    const upstream = createServer((_req, res) => {
      res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' });
      res.write(': heartbeat\n\n');
      sseSockets.add(res);
    });
    await new Promise<void>((r) => upstream.listen(0, '127.0.0.1', r));
    const addr = upstream.address() as { port: number };

    const proxy = new LocalBackendProxy(
      () => ({
        targetBaseUrl: `http://127.0.0.1:${addr.port}`,
        expectedAccessToken: 'acc-tok',
        controlToken: 'ctrl-tok',
      }),
      new Set([APP_ORIGIN]),
    );
    const proxyOrigin = await proxy.start();

    try {
      const streamRes = await fetch(`${proxyOrigin}/api/gateway/sse`, {
        headers: { Origin: APP_ORIGIN, Authorization: 'Bearer acc-tok' },
      });
      expect(streamRes.status).toBe(200);
      const reader = streamRes.body!.getReader();
      await reader.read(); // Consume initial heartbeat so SSE connection is actively held open

      const closeStart = Date.now();
      await proxy.close();
      const elapsed = Date.now() - closeStart;
      expect(elapsed).toBeLessThan(1500);

      // Downstream reader must terminate (done or aborted error) instead of hanging once proxy closes
      const terminated = await Promise.race([
        reader.read().then((chunk) => chunk.done === true, () => true),
        new Promise<boolean>((r) => setTimeout(() => r(false), 1000)),
      ]);
      expect(terminated).toBe(true);
    } finally {
      for (const s of sseSockets) {
        s.destroy();
      }
      await new Promise<void>((r) => upstream.close(() => r()));
    }
  });

  it('resolveManagedBackendEnvironment forces Production when packaged and defaults to Development in dev', () => {
    const packagedEnv = resolveManagedBackendEnvironment(
      { isPackaged: true, isDev: false },
      { ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development', CUSTOM_VAR: 'keep' },
    );
    expect(packagedEnv.ASPNETCORE_ENVIRONMENT).toBe('Production');
    expect(packagedEnv.DOTNET_ENVIRONMENT).toBe('Production');
    expect(packagedEnv.CUSTOM_VAR).toBe('keep');

    const devEnvDefault = resolveManagedBackendEnvironment(
      { isPackaged: false, isDev: true },
      {},
    );
    expect(devEnvDefault.ASPNETCORE_ENVIRONMENT).toBe('Development');
    expect(devEnvDefault.DOTNET_ENVIRONMENT).toBe('Development');

    const devEnvOverride = resolveManagedBackendEnvironment(
      { isPackaged: false, isDev: true },
      { ASPNETCORE_ENVIRONMENT: 'Production', DOTNET_ENVIRONMENT: 'Production' },
    );
    expect(devEnvOverride.ASPNETCORE_ENVIRONMENT).toBe('Production');
    expect(devEnvOverride.DOTNET_ENVIRONMENT).toBe('Production');
  });

  it('UpdaterManager safely degrades in unpackaged mode without throwing', async () => {
    const manager = new UpdaterManager({
      currentVersion: '0.1.5',
      isPackaged: false,
      onStateChange: vi.fn(),
      onPrepareInstall: vi.fn(),
    });
    expect(manager.getState().status).toBe('unavailable');
    const checked = await manager.checkForUpdates();
    expect(checked.status).toBe('unavailable');
    expect(manager.quitAndInstall()).toBe(false);
  });

  it('resolves bundled LibreOffice soffice executable in packaged resources', () => {
    const tempDir = mkdtempSync(join(tmpdir(), 'cf-lo-'));
    try {
      const programDir = join(tempDir, 'libreoffice', 'program');
      mkdirSync(programDir, { recursive: true });
      const winExe = join(programDir, 'soffice.exe');
      writeFileSync(winExe, 'stub');

      expect(resolveBundledLibreOfficePath(tempDir, true, 'win32')).toBe(winExe);
      expect(
        resolveBundledLibreOfficePath(
          '/tmp/cf-lo-posix',
          true,
          'win32',
          (candidate) => candidate === '/tmp/cf-lo-posix/libreoffice/program/soffice.exe',
        ),
      ).toBe('/tmp/cf-lo-posix/libreoffice/program/soffice.exe');
    } finally {
      rmSync(tempDir, { recursive: true, force: true });
    }
  });
});
