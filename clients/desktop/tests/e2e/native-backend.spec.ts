import { expect, test, _electron as electron } from '@playwright/test';
import { createServer } from 'node:http';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import type { BackendConnectionPublicInfo } from '../../src/shared/ipc';

const nativeBackend = process.env.CODEXFLOW_NATIVE_BACKEND_BIN;

function isRunning(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

test('Electron starts the native Local backend and its child exits with the parent', async () => {
  test.setTimeout(70_000);
  test.skip(!nativeBackend, 'Supply the published Windows x64 CodexFlow.Local.exe');
  expect(existsSync(nativeBackend!)).toBe(true);

  const root = mkdtempSync(join(tmpdir(), 'codexflow-native-e2e-'));
  const profile = join(root, 'profile');
  const data = join(root, 'host-data');
  const settings = join(root, 'global');
  mkdirSync(profile);
  mkdirSync(settings);
  const model = createServer((request, response) => {
    response.setHeader('Content-Type', 'application/json');
    if (request.url?.endsWith('/models')) {
      response.end(JSON.stringify({ object: 'list', data: [{ id: 'native-smoke', object: 'model' }] }));
    } else {
      response.statusCode = 404;
      response.end('{}');
    }
  });
  const main = resolve(dirname(fileURLToPath(import.meta.url)), '../../out/main/index.js');
  const entry = join(root, 'native-entry.mjs');
  writeFileSync(entry, `import { app } from 'electron';
app.setPath('userData', ${JSON.stringify(profile)});
await import(${JSON.stringify(pathToFileURL(main).href)});
`);

  let desktop: Awaited<ReturnType<typeof electron.launch>> | undefined;
  let desktopStopped = false;
  try {
    await new Promise<void>((ready, reject) => {
      model.once('error', reject);
      model.listen(0, '127.0.0.1', ready);
    });
    const address = model.address();
    expect(address).not.toBeNull();
    if (!address || typeof address === 'string') throw new Error('Model stub has no TCP address');
    const apiUrl = `http://127.0.0.1:${address.port}/v1`;
    writeFileSync(join(settings, 'appsettings.json'), JSON.stringify({
      VllmAgent: { ApiUrl: apiUrl, ApiKey: 'native-smoke-stub', Model: 'native-smoke' },
    }));

    desktop = await electron.launch({
      args: [entry],
      env: {
        ...process.env,
        CODEXFLOW_BACKEND_BIN: resolve(nativeBackend!),
        CODEXFLOW_DATA_DIR: data,
        CODEXFLOW_HOME: settings,
        VllmAgent__ApiUrl: apiUrl,
        VllmAgent__ApiKey: 'native-smoke-stub',
        VllmAgent__Model: 'native-smoke',
      },
    });
    const page = await desktop.firstWindow();
    await page.waitForLoadState('domcontentloaded');

    const discovery = join(data, 'instance.json');
    await expect.poll(() => existsSync(discovery), { timeout: 15_000 }).toBe(true);
    const { port, pid } = JSON.parse(readFileSync(discovery, 'utf8')) as { port: number; pid: number };
    expect(pid).toBeGreaterThan(0);
    expect(port).toBeGreaterThan(0);
    const baseUrl = `http://127.0.0.1:${port}`;
    await test.step('Check Local health, authentication and desktop readiness', async () => {
      expect((await fetch(`${baseUrl}/health`, { signal: AbortSignal.timeout(5_000) })).status).toBe(200);
      expect((await fetch(`${baseUrl}/api/gateway/status`, { signal: AbortSignal.timeout(5_000) })).status).toBe(401);
      await expect.poll(() => page.evaluate(async () => {
        const { codexflow } = globalThis as unknown as {
          codexflow: { getBackendConnection: () => Promise<BackendConnectionPublicInfo> };
        };
        const { status } = await codexflow.getBackendConnection();
        return status;
      }), { timeout: 15_000 }).toBe('ready');
    });

    await test.step('Stop Electron abruptly and wait for Local to exit', async () => {
      // On Windows Playwright launches Electron through a shell. Get the Electron main PID from the app.
      const electronPid = await desktop!.evaluate(() => process.pid);
      expect(process.kill(electronPid, 'SIGKILL')).toBe(true);
      await expect.poll(() => isRunning(electronPid), { timeout: 10_000 }).toBe(false);
      desktopStopped = true;
      await expect.poll(() => isRunning(pid), { timeout: 35_000 }).toBe(false);
    });
  } finally {
    if (!desktopStopped) await desktop?.close().catch(() => undefined);
    model.closeAllConnections();
    if (model.listening) await new Promise<void>((done) => model.close(() => done()));
    rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
});
