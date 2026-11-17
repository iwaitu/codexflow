import { expect, test, _electron as electron } from '@playwright/test';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const currentDir = dirname(fileURLToPath(import.meta.url));

function isPidAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

test('Electron MVP cold start, security isolation, permission rule revoke, approval flow, crash & snapshot recovery, and clean process-tree shutdown', async () => {
  const tempRoot = mkdtempSync(join(tmpdir(), 'codexflow-e2e-'));
  const pidFile = join(tempRoot, 'backend.pids');
  const childPidFile = join(tempRoot, 'backend-children.pids');
  const stateFile = join(tempRoot, 'backend-state.json');
  const dataDir = join(tempRoot, 'host-data');
  const desktopRoot = resolve(currentDir, '../..');
  const mainEntry = resolve(desktopRoot, 'out/main/index.js');
  const mockBackend = resolve(currentDir, 'mock-backend.mjs');

  const electronApp = await electron.launch({
    args: [mainEntry],
    env: {
      ...process.env,
      CODEXFLOW_BACKEND_BIN: mockBackend,
      CODEXFLOW_DATA_DIR: dataDir,
      MOCK_PID_FILE: pidFile,
      MOCK_CHILD_PID_FILE: childPidFile,
      MOCK_STATE_FILE: stateFile,
    },
  });

  const pageErrors: string[] = [];
  const cspErrors: string[] = [];

  try {
    const page = await electronApp.firstWindow();
    page.on('console', (msg) => {
      if (msg.type() === 'error' && msg.text().includes('Content Security Policy')) {
        cspErrors.push(msg.text());
      }
    });
    page.on('pageerror', (err) => {
      pageErrors.push(err.message);
    });
    await page.waitForLoadState('domcontentloaded');

    // 1. Verify renderer has NO controlToken anywhere on window or bridge response, and baseUrl is pinned proxy
    const bridgeAudit = await page.evaluate(async () => {
      const bridge = (globalThis as unknown as {
        codexflow: { getBackendConnection: () => Promise<Record<string, unknown>> };
      }).codexflow;
      const conn = await bridge.getBackendConnection();
      return {
        keys: Object.keys(bridge),
        hasControlTokenInConn: 'controlToken' in conn,
        serialized: JSON.stringify(conn),
        baseUrl: conn.baseUrl,
        targetBaseUrl: conn.targetBaseUrl,
      };
    });
    expect(bridgeAudit.hasControlTokenInConn).toBe(false);
    expect(bridgeAudit.keys).not.toContain('controlToken');
    expect(bridgeAudit.serialized).not.toContain('controlToken');

    await expect(page.getByPlaceholder(/输入指令或问题/)).toBeEnabled({ timeout: 15_000 });

    // 2. B5 E2E: Open Settings -> 始终允许规则 -> revoke seeded rule by real backend `id`
    await page.getByRole('button', { name: '设置' }).click();
    await page.getByRole('button', { name: '始终允许规则' }).click();
    await expect(page.getByText('dotnet.exe dotnet --info')).toBeVisible({ timeout: 5_000 });
    await page.getByTitle('撤销规则').click();
    await expect(page.getByText('规则已撤销。')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText('dotnet.exe dotnet --info')).toHaveCount(0);
    await page.keyboard.press('Escape');

    // 3. Send chat message -> triggers command approval card -> approve via control-plane IPC
    const textarea = page.getByPlaceholder(/输入指令或问题/);
    await textarea.fill('请运行 dotnet --version');
    await page.getByRole('button', { name: '发送' }).click();

    await expect(page.getByTestId('tool-approval-card-appr-e2e-1')).toBeVisible({ timeout: 10_000 });
    await page.getByRole('button', { name: '允许一次' }).click();

    await expect(page.getByText('命令已批准并在本地执行完成。').first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('unverified-isolation-badge').first()).toBeVisible();

    // 4. B7: Manual restart via IPC kills previous backend + grandchild cleanly WITHOUT spawning duplicate instances
    await page.evaluate(async () => {
      const bridge = (globalThis as unknown as {
        codexflow: { restartBackend: () => Promise<Record<string, unknown>> };
      }).codexflow;
      await bridge.restartBackend();
    });

    await expect
      .poll(
        () => {
          if (!existsSync(pidFile)) return 0;
          return readFileSync(pidFile, 'utf-8').trim().split(/\r?\n/).filter(Boolean).length;
        },
        { timeout: 15_000 },
      )
      .toBe(2);

    // Proof that manual restart killed instance 1 and its grandchild without leaving them running
    const pidsAfterRestart = readFileSync(pidFile, 'utf-8')
      .trim()
      .split(/\r?\n/)
      .map((s) => Number.parseInt(s, 10));
    const childPidsAfterRestart = readFileSync(childPidFile, 'utf-8')
      .trim()
      .split(/\r?\n/)
      .map((s) => Number.parseInt(s, 10));
    expect(pidsAfterRestart).toHaveLength(2);
    expect(isPidAlive(pidsAfterRestart[0]!)).toBe(false);
    expect(isPidAlive(childPidsAfterRestart[0]!)).toBe(false);

    // Recovered state from NEW backend /snapshot must display server-injected recovery marker!
    await expect(page.getByText('快照恢复新增消息-epoch2').first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText('命令已批准并在本地执行完成。').first()).toBeVisible();

    // Verify pinned proxy origin stayed identical while target backend port changed
    const secondConn = await page.evaluate(async () => {
      const bridge = (globalThis as unknown as {
        codexflow: { getBackendConnection: () => Promise<Record<string, unknown>> };
      }).codexflow;
      return bridge.getBackendConnection();
    });
    expect(secondConn.baseUrl).toBe(bridgeAudit.baseUrl);
    expect(secondConn.targetBaseUrl).not.toBe(bridgeAudit.targetBaseUrl);

    // 5. Simulate unexpected crash (SIGKILL backend #2) -> verify automatic backoff restart (#3) & snapshot recovery
    process.kill(pidsAfterRestart[1]!, 'SIGKILL');
    await expect
      .poll(
        () => {
          if (!existsSync(pidFile)) return 0;
          return readFileSync(pidFile, 'utf-8').trim().split(/\r?\n/).filter(Boolean).length;
        },
        { timeout: 15_000 },
      )
      .toBe(3);

    await expect(page.getByText('快照恢复新增消息-epoch2').first()).toBeVisible({ timeout: 10_000 });

    // F3: Product crash cleanup must have already terminated the orphaned grandchild from crashed backend #2!
    const childPidsAfterCrash = readFileSync(childPidFile, 'utf-8')
      .trim()
      .split(/\r?\n/)
      .map((s) => Number.parseInt(s, 10));
    expect(childPidsAfterCrash.length).toBeGreaterThanOrEqual(3);
    expect(isPidAlive(childPidsAfterCrash[1]!)).toBe(false);

    // 6. Click "新建会话", verify thread appears as "新会话", send message, and verify SSE auto-title update
    await page.getByRole('button', { name: '新建会话' }).click();
    await expect(page.getByText('新会话')).toBeVisible({ timeout: 5_000 });
    await textarea.fill('验证重启后新端口通信');
    await page.getByRole('button', { name: '发送' }).click();
    await expect(page.getByText('收到重启后请求: 验证重启后新端口通信').first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText('会话标题已总结')).toBeVisible({ timeout: 10_000 });

    // 7. P5 Workspace Inspector: toggle Files & Changes tabs
    await page.getByRole('button', { name: '工作区文件' }).click();
    await expect(page.getByTestId('workspace-inspector')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText('README.md').first()).toBeVisible({ timeout: 5_000 });
    await page.getByRole('button', { name: '代码变更' }).click();
    await expect(page.getByText('+# CodexFlow')).toBeVisible({ timeout: 5_000 });

    // 8. Switch to "插件与 Skill" hub & verify tree collapse/expand on sidebar
    await page.getByRole('button', { name: '插件与 Skill' }).click();
    await expect(page.getByTestId('business-skills-hub')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByRole('button', { name: /接入新插件/ })).toBeEnabled();
    await page.getByRole('button', { name: '工作区对话' }).click();
    await expect(page.getByText('会话标题已总结').first()).toBeVisible({ timeout: 5_000 });
    await page.getByRole('button', { name: '折叠 默认工作区' }).click();
    await expect(page.getByText('会话标题已总结')).toHaveCount(0);
    await page.getByRole('button', { name: '展开 默认工作区' }).click();
    await expect(page.getByText('会话标题已总结').first()).toBeVisible({ timeout: 5_000 });

    expect(cspErrors).toEqual([]);
    expect(pageErrors).toEqual([]);
  } finally {
    await electronApp.close();

    // 7. B8 & F3: Strictly verify ALL backend processes AND ALL grandchild processes (including crashed instance #2) have exited!
    await new Promise((r) => setTimeout(r, 600));
    for (const file of [pidFile, childPidFile]) {
      if (existsSync(file)) {
        const pids = readFileSync(file, 'utf-8')
          .trim()
          .split(/\r?\n/)
          .map((s) => Number.parseInt(s, 10))
          .filter((n) => Number.isFinite(n));
        for (const pid of pids) {
          expect(isPidAlive(pid)).toBe(false);
        }
      }
    }

    rmSync(tempRoot, { recursive: true, force: true });
  }
});
