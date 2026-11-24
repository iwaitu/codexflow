import { expect, test, _electron as electron } from '@playwright/test';
import { createServer } from 'node:http';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import type { BackendConnectionPublicInfo, UpdateBrowserSettingsRequest } from '../../src/shared/ipc';

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
  test.setTimeout(100_000);
  test.skip(!nativeBackend, 'Supply the published Windows x64 CodexFlow.Local.exe');
  expect(existsSync(nativeBackend!)).toBe(true);

  const root = mkdtempSync(join(tmpdir(), 'codexflow-native-e2e-'));
  const profile = join(root, 'profile');
  const data = join(root, 'host-data');
  const settings = join(root, 'global');
  mkdirSync(profile);
  mkdirSync(settings);
  let panelText = '';
  let panelScroll = 0;
  let panelSize = { width: 0, height: 0, wide: false };
  let panelBreakpoint = 385;
  let nativeSkillOutput = '';
  const model = createServer((request, response) => {
    if (request.url?.endsWith('/chat/completions')) {
      let body = '';
      request.setEncoding('utf8');
      request.on('data', (chunk: string) => { body += chunk; });
      request.on('end', () => {
        const chat = JSON.parse(body) as {
          stream?: boolean;
          messages: Array<{ role: string; tool_call_id?: string; content?: string }>;
          tools?: Array<{ function: { name: string } }>;
        };
        const result = chat.messages.find((message) => message.role === 'tool' && message.tool_call_id === 'read-frontend-skill');
        if (result?.content) {
          const envelope = JSON.parse(result.content) as { output: string };
          nativeSkillOutput = envelope.output;
        }
        const canReadSkill = chat.tools?.some((tool) => tool.function.name === 'read_builtin_skill');
        const callSkill = canReadSkill && !result;
        const message = callSkill
          ? { role: 'assistant', tool_calls: [{ index: 0, id: 'read-frontend-skill', type: 'function', function: {
            name: 'read_builtin_skill', arguments: JSON.stringify({ name: 'frontend-browser-debug' }),
          } }] }
          : { role: 'assistant', content: result ? '已读取前端调试流程' : 'Native smoke' };
        const finishReason = callSkill ? 'tool_calls' : 'stop';
        const completion = { id: 'native-smoke', model: 'native-smoke', created: 0 };
        if (!chat.stream) {
          response.setHeader('Content-Type', 'application/json');
          response.end(JSON.stringify({ ...completion, object: 'chat.completion', choices: [{ index: 0, message, finish_reason: finishReason }] }));
          return;
        }
        response.setHeader('Content-Type', 'text/event-stream');
        response.end(`data: ${JSON.stringify({ ...completion, object: 'chat.completion.chunk', choices: [{ index: 0, delta: message, finish_reason: finishReason }] })}\n\ndata: [DONE]\n\n`);
      });
      return;
    }
    if (request.url?.startsWith('/panel-fixture')) {
      const variant = new URL(request.url, 'http://127.0.0.1').searchParams.get('tab');
      const title = variant === 'two' ? 'Second panel page' : variant === 'next' ? 'Second page history' : 'Browser panel fixture';
      const wideQuery = `(min-width: ${panelBreakpoint}px)`;
      response.setHeader('Content-Type', 'text/html; charset=utf-8');
      response.end(`<!doctype html><title>${title}</title>` +
        '<style>body{min-height:1800px}input{position:fixed;left:30px;top:20px;width:170px;height:40px}' +
        `@media ${wideQuery}{input{left:200px}}</style><body>` +
        '<input ' +
        'oninput="fetch(\'/panel-value?text=\' + encodeURIComponent(this.value))">' +
        '<script>const reportSize = () => fetch(`/panel-size?w=${innerWidth}&h=${innerHeight}&wide=${matchMedia(' +
        JSON.stringify(wideQuery) + ').matches}`);' +
        'new ResizeObserver(reportSize).observe(document.documentElement);addEventListener("resize",reportSize);' +
        'addEventListener("scroll", () => fetch("/panel-scroll?y=" + scrollY));</script></body>');
      return;
    }
    if (request.url?.startsWith('/panel-size?')) {
      const params = new URL(request.url, 'http://127.0.0.1').searchParams;
      panelSize = { width: Number(params.get('w')), height: Number(params.get('h')), wide: params.get('wide') === 'true' };
      response.writeHead(204).end();
      return;
    }
    if (request.url?.startsWith('/panel-scroll?')) {
      panelScroll = Number(new URL(request.url, 'http://127.0.0.1').searchParams.get('y'));
      response.writeHead(204).end();
      return;
    }
    if (request.url?.startsWith('/panel-value?')) {
      panelText = new URL(request.url, 'http://127.0.0.1').searchParams.get('text') ?? '';
      response.writeHead(204).end();
      return;
    }
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
      Browser: { AllowLocalhostInDailyMode: true, DevelopmentAllowedLoopbackPorts: [address.port] },
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

    await test.step('Fill the sidebar and reflow the managed page when the sidebar resizes', async () => {
      await page.getByRole('complementary').getByRole('button', { name: '新建日常对话', exact: true }).click();
      await expect(page.getByTestId('composer-work-mode-badge')).toHaveText('日常模式');
      await page.getByRole('button', { name: '打开右侧栏' }).click();
      const sidebar = page.getByTestId('workspace-inspector');
      await sidebar.getByTestId('inspector-tab-browser').click();
      const area = sidebar.getByTestId('browser-page-area');
      const viewport = sidebar.getByTestId('browser-live-viewport');
      const bar = sidebar.getByRole('textbox', { name: '浏览器地址栏' });
      const newTab = sidebar.getByRole('button', { name: '新建浏览器标签页' });
      const url = `http://127.0.0.1:${address.port}/panel-fixture`;
      const navigate = async (target: string, title: string) => {
        await bar.fill(target);
        await bar.press('Enter');
        await expect(viewport).toHaveAttribute('alt', title);
      };
      const expectFullViewport = async () => {
        await expect.poll(async () => {
          const canvasBounds = await area.boundingBox();
          const imageBounds = await viewport.boundingBox();
          if (!canvasBounds || !imageBounds) return { canvasBounds, imageBounds, panelSize };
          const natural = await viewport.evaluate((element) => {
            const image = element as { naturalWidth: number; naturalHeight: number };
            return [image.naturalWidth, image.naturalHeight];
          });
          const fits = Math.abs(panelSize.width - canvasBounds.width) <= 1 && Math.abs(panelSize.height - canvasBounds.height) <= 1 &&
            natural[0] === panelSize.width && natural[1] === panelSize.height &&
            Math.abs(canvasBounds.x - imageBounds.x) < 1 && Math.abs(canvasBounds.y - imageBounds.y) < 1 &&
            Math.abs(canvasBounds.width - imageBounds.width) < 1 && Math.abs(canvasBounds.height - imageBounds.height) < 1;
          return fits ? 'fits' : { canvasBounds, imageBounds, panelSize, natural };
        }).toBe('fits');
      };

      await newTab.click();
      await expect.poll(async () => {
        const errors = await sidebar.getByRole('alert').allTextContents();
        return errors.length > 0 ? errors : sidebar.getByRole('button', { name: /^关闭标签页/ }).count();
      }, { timeout: 20_000 }).toBe(1);
      const separator = sidebar.getByRole('separator', { name: '调整右侧栏宽度' });
      await separator.press('End');
      const before = (await sidebar.boundingBox())!.width;
      const minimum = Number(await separator.getAttribute('aria-valuemin'));
      const dragDistance = Math.min(70, Math.floor(before - minimum));
      expect(dragDistance).toBeGreaterThan(16);
      // Use a breakpoint inside the actual drag range on this runner.
      panelBreakpoint = Math.floor((await area.boundingBox())!.width - dragDistance / 2);
      await navigate(url, 'Browser panel fixture');
      await expectFullViewport();
      await expect.poll(() => panelSize.wide).toBe(true);

      const handle = (await separator.boundingBox())!;
      const dragX = handle.x + handle.width / 2;
      const dragY = handle.y + handle.height / 2;
      await page.mouse.move(dragX, dragY);
      await page.mouse.down();
      await page.mouse.move(dragX + dragDistance, dragY, { steps: 7 });
      await page.mouse.up();
      await expect.poll(async () => (await sidebar.boundingBox())!.width).toBeLessThanOrEqual(before - dragDistance + 1);
      await expectFullViewport();
      await expect.poll(() => panelSize.wide).toBe(false);
      const fullHeight = panelSize.height;
      const diagnostics = sidebar.getByRole('button', { name: /操作轨迹/ });
      await diagnostics.click();
      await expectFullViewport();
      expect(panelSize.height).toBeLessThan(fullHeight);
      await diagnostics.click();
      await expectFullViewport();
      expect(panelSize.height).toBe(fullHeight);

      await viewport.click({ position: { x: 100, y: 40 } });
      await page.keyboard.insertText('侧栏实测');
      await expect.poll(() => panelText).toBe('侧栏实测');
      await viewport.hover();
      await page.mouse.wheel(0, 350);
      await expect.poll(() => panelScroll).toBeGreaterThan(0);

      await newTab.click();
      await expect(newTab).toBeEnabled();
      expect(await sidebar.getByRole('alert').allTextContents()).toEqual([]);
      await expect(sidebar.getByRole('button', { name: /^关闭标签页/ })).toHaveCount(2);
      await navigate(`${url}?tab=two`, 'Second panel page');
      await navigate(`${url}?tab=next`, 'Second page history');
      await sidebar.getByRole('button', { name: 'Browser panel fixture', exact: true }).click();
      await expect(viewport).toHaveAttribute('alt', 'Browser panel fixture');
      await expect(bar).toHaveValue(url);
      await sidebar.getByRole('button', { name: 'Second page history', exact: true }).click();
      await expect(viewport).toHaveAttribute('alt', 'Second page history');
      await sidebar.getByRole('button', { name: '返回', exact: true }).click();
      await expect(viewport).toHaveAttribute('alt', 'Second panel page');
      await newTab.click();
      await expect(viewport).toHaveAttribute('alt', '受管网页');
      await expect(sidebar.getByRole('button', { name: /^关闭标签页/ })).toHaveCount(3);
      await sidebar.getByRole('button', { name: '关闭标签页 Second panel page' }).click();
      await expect(sidebar.getByRole('button', { name: /^关闭标签页/ })).toHaveCount(2);
      await expect(viewport).toHaveAttribute('alt', '受管网页');
      await sidebar.getByRole('button', { name: '关闭标签页 新标签页' }).click();
      await expect(viewport).toHaveAttribute('alt', 'Browser panel fixture');
      await expect(sidebar.getByRole('button', { name: /^关闭标签页/ })).toHaveCount(1);
      await sidebar.getByRole('button', { name: '关闭标签页 Browser panel fixture' }).click();
      await expect(sidebar.getByRole('button', { name: /^关闭标签页/ })).toHaveCount(1);
      await expect(viewport).toHaveAttribute('alt', '受管网页');
      await expect(bar).toHaveValue('');

      await sidebar.getByRole('button', { name: '交回 Agent', exact: true }).click();
      await expect(viewport).toHaveCount(0);
      await sidebar.getByRole('button', { name: '关闭', exact: true }).click();
      await expect(sidebar.getByText('浏览器空闲', { exact: true })).toBeVisible();
      await bar.fill(url);
      await bar.press('Enter');
      await expect(viewport).toHaveAttribute('alt', 'Browser panel fixture', { timeout: 20_000 });
      await sidebar.getByRole('button', { name: '关闭', exact: true }).click();
      await expect(sidebar.getByText('浏览器空闲', { exact: true })).toBeVisible();
    });

    await test.step('Save browser settings through Electron with CDP unset, configured and cleared', async () => {
      const updateBrowserSettings = (payload: UpdateBrowserSettingsRequest) => page.evaluate(async (input) => {
        const { codexflow } = globalThis as unknown as {
          codexflow: { updateBrowserSettings: (request: UpdateBrowserSettingsRequest) => Promise<{ ok: boolean }> };
        };
        return await codexflow.updateBrowserSettings(input);
      }, payload);
      const domains = { allowedDomains: ['example.com'], blockedDomains: ['blocked.example.com'] };
      const readSavedBrowserSettings = () => JSON.parse(readFileSync(join(settings, 'appsettings.json'), 'utf8')).Browser;

      const initial = await updateBrowserSettings({
        remoteCdpWsUrl: undefined, allowedDomains: [], blockedDomains: [], headless: true,
      });
      expect(initial.ok).toBe(true);
      expect(readSavedBrowserSettings()).not.toHaveProperty('RemoteCdpWsUrl');

      const remoteCdpWsUrl = 'wss://grid.example.com/devtools/browser/test';
      expect((await updateBrowserSettings({ ...domains, remoteCdpWsUrl })).ok).toBe(true);
      expect(readSavedBrowserSettings()).toMatchObject({
        RemoteCdpWsUrl: remoteCdpWsUrl, AllowedDomains: domains.allowedDomains, BlockedDomains: domains.blockedDomains,
      });

      expect((await updateBrowserSettings({ remoteCdpWsUrl: '' })).ok).toBe(true);
      expect(readSavedBrowserSettings()).not.toHaveProperty('RemoteCdpWsUrl');
      expect(readSavedBrowserSettings()).toMatchObject({
        AllowedDomains: domains.allowedDomains, BlockedDomains: domains.blockedDomains,
      });
    });

    await test.step('Show the native built-in skill catalog in both work modes', async () => {
      const modeBadge = page.getByTestId('composer-work-mode-badge');
      const openCatalog = async () => {
        await page.getByRole('button', { name: '插件与 Skill' }).click();
        await expect(page.getByRole('heading', { name: '内置 Skill' })).toBeVisible();
        await expect(page.getByRole('heading', { name: '前端浏览器调试' })).toBeVisible();
      };

      // Reuse the daily session created above so a pending session switch cannot hide the catalog.
      await expect(modeBadge).toHaveText('日常模式');
      await openCatalog();

      await page.getByRole('button', { name: '工作区对话' }).click();
      await modeBadge.click();
      await page.getByRole('menuitem', { name: /新建开发模式对话/ }).click();
      await expect(modeBadge).toHaveText('开发模式');
      await openCatalog();
    });

    await test.step('Load the embedded debugging instructions through a native tool call', async () => {
      await page.getByRole('button', { name: '工作区对话' }).click();
      await page.locator('textarea').fill('请读取前端浏览器调试 Skill');
      await page.getByRole('button', { name: '发送', exact: true }).click();
      await expect.poll(() => nativeSkillOutput, { timeout: 15_000 }).toContain('确认入口和预期');
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
