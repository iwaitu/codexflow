import { readdirSync, readFileSync, statSync } from 'node:fs';
import { resolve } from 'node:path';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PersistedSecurePairingState } from '@/bridge/nativeCryptoTypes';
import { ApprovalsView } from '@/views/ApprovalsView';
import { ChatView } from '@/views/ChatView';
import { PairView } from '@/views/PairView';
import { SessionsView } from '@/views/SessionsView';
import { SettingsView } from '@/views/SettingsView';
import { useMobileStore } from '@/store/useMobileStore';

const INITIAL_STORE_STATE = useMobileStore.getState();

function collectSourceFiles(dir: string): string[] {
  const results: string[] = [];
  for (const entry of readdirSync(dir)) {
    const full = resolve(dir, entry);
    const st = statSync(full);
    if (st.isDirectory()) {
      results.push(...collectSourceFiles(full));
    } else if (/\.(ts|tsx|css|html)$/.test(entry)) {
      results.push(full);
    }
  }
  return results;
}

const SAMPLE_PAIRING_STATE: PersistedSecurePairingState = {
  portalUrl: 'https://portal.codexflow.example',
  deviceId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
  desktopName: 'StudioMac-Pro',
  bindingVersion: 5,
  desktopSpkiB64: 'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEE6MmWU+YWT2YiyAoYOYqz4LoB4n19Pt8wpEEJpop+HymFDT29k9UmkJ4sbst0xo3IYtOBoDGCe5QG7xrpB0C3Q==',
  desktopSpkiSha256: 'cec39cef492e238714f215e786851049fe302406204b3300e69556a8d76cb945',
  desktopKeyId: 'cec39cef492e238714f215e786851049',
  pairingId: '77777777-6666-5555-4444-333333333333',
  mobileId: '99999999-8888-7777-6666-555555555555',
  accessToken: 'acc-tok',
  accessExpiresAt: '2030-01-01T00:00:00Z',
  refreshToken: 'ref-tok',
  refreshExpiresAt: '2030-02-01T00:00:00Z',
  biometricEnrolled: true,
  apnsPushEnabled: false,
};

describe('移动专版五大核心视图与零商业化合规审计', () => {
  beforeEach(() => {
    useMobileStore.setState(INITIAL_STORE_STATE, true);
  });

  afterEach(() => {
    cleanup();
    useMobileStore.setState(INITIAL_STORE_STATE, true);
    vi.restoreAllMocks();
  });

  it('PairView 展示扫码与手动粘贴 URI 配对，并在无生物识别时展示安全降级说明', () => {
    useMobileStore.setState({
      bootstrapped: true,
      pairingState: null,
      capabilities: {
        platform: 'android',
        hardwareBacked: true,
        biometricAvailable: false,
        qrScannerAvailable: true,
      },
    });

    const { container } = render(<PairView />);
    expect(screen.getByText('配对 CodexFlow 桌面端')).toBeInTheDocument();
    expect(screen.getByText(/当前设备未开启硬件生物识别/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /相机扫码配对/ })).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/订阅|付费|会员|pricing|checkout|stripe/i);
  });

  it('SessionsView 渲染工作区、在线标识并支持触发新建工作区与打开会话', () => {
    const createSpy = vi.fn(async () => 's1');
    const openSpy = vi.fn(async () => undefined);
    useMobileStore.setState({
      pairingState: SAMPLE_PAIRING_STATE,
      desktopOnline: true,
      sessions: [
        {
          sessionId: 'sess-alpha',
          name: 'codexflow-portal',
          workMode: 'Developer',
          updatedLabel: '2026-11-22',
          isExternalWorkspace: false,
          planModeActive: true,
        },
      ],
      createDefaultSession: createSpy,
      openSession: openSpy,
    });

    render(<SessionsView />);
    expect(screen.getByText('StudioMac-Pro')).toBeInTheDocument();
    expect(screen.getByText(/安全隧道在线/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /新建工作区/ }));
    expect(createSpy).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole('button', { name: /codexflow-portal/ }));
    expect(openSpy).toHaveBeenCalledWith('sess-alpha');
  });

  it('ChatView 使用 SafeMarkdown 拦截 javascript: XSS 链接，对 session-plan 禁用批准并对正式 PlanArtifact 放行', async () => {
    const planSpy = vi.fn(async () => undefined);
    useMobileStore.setState({
      pairingState: SAMPLE_PAIRING_STATE,
      selectedSessionId: 'sess-alpha',
      sessions: [
        {
          sessionId: 'sess-alpha',
          name: 'codexflow-portal',
          workMode: 'Developer',
          updatedLabel: '',
          isExternalWorkspace: false,
          planModeActive: true,
        },
      ],
      conversations: [{ conversationId: 'conv-1', name: '主线排查' }],
      selectedConversationId: 'conv-1',
      messages: [
        {
          id: 'm1',
          role: 'assistant',
          content: '请查看 [危险脚本](javascript:alert(1)) 与 [官方站点](https://example.com)',
        },
      ],
      currentPlan: {
        planArtifactId: 'session-plan',
        version: '00000001',
        markdownHash: 'a'.repeat(64),
        status: 'draft',
        goal: '验证旧式草稿只读保护',
        markdown: '1. 步骤一',
        signedEnvelope: null,
        signedEnvelopeApproveOnly: null,
      },
      submitPlanDecision: planSpy,
    });

    const { rerender } = render(<ChatView />);

    // 危险链接被降级成不可点击的 span，合法 https 链接保留 <a>
    const safeLink = screen.getByRole('link', { name: '官方站点' });
    expect(safeLink).toHaveAttribute('href', 'https://example.com/');
    expect(screen.queryByRole('link', { name: '危险脚本' })).toBeNull();
    expect(screen.getByText('危险脚本')).toBeInTheDocument();

    // 打开计划抽屉，验证 session-plan 禁止批准
    fireEvent.click(screen.getByRole('button', { name: '查看执行计划' }));
    expect(screen.getByText(/session-plan 只读草稿/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /批准并开始执行/ })).toBeDisabled();

    // 更新为带有正式 signedEnvelope 的 PlanArtifact
    act(() => {
      useMobileStore.setState({
        currentPlan: {
          planArtifactId: 'plan-artifact-99',
          version: '00000009',
          markdownHash: 'b'.repeat(64),
          status: 'ready',
          goal: '执行正式签批',
          markdown: '1. 构建',
          signedEnvelope: {
            deviceId: SAMPLE_PAIRING_STATE.deviceId,
            bindingVersion: 5,
            sessionId: 'sess-alpha',
            planArtifactId: 'plan-artifact-99',
            planVersion: '00000009',
            planHash: 'b'.repeat(64),
            allowedActionsCsv: 'approve,execute',
            startExecution: true,
            acceptExistingDependencyDebt: false,
            approveDependencyUpgrades: [],
            expiresAtUnix: 1999999999,
            sigB64U: 'sig',
          },
          signedEnvelopeApproveOnly: null,
        },
      });
    });
    rerender(<ChatView />);

    const execBtn = screen.getByRole('button', { name: /批准并开始执行/ });
    expect(execBtn).toBeEnabled();
    await act(async () => {
      fireEvent.click(execBtn);
    });
    expect(planSpy).toHaveBeenCalledWith('execute', true);
  });

  it('ApprovalsView 拦截 readOnlyInRemote 项并允许命令行查看完整脚本与 WYSIWYS 允许签批', async () => {
    const inspectSpy = vi.fn(async () => 'echo "verified script body"');
    const decideSpy = vi.fn(async () => undefined);

    const normalItem = {
      eventId: 101,
      sourceKind: 'command',
      opaqueRefId: 'ref-cmd-1',
      sessionId: 'sess-alpha',
      title: '运行构建命令',
      summary: 'npm run build',
      status: 'pending',
      isTerminal: false,
      readOnlyInRemote: false,
      createdAtUtc: '2026-11-22T00:00:00Z',
      expiresAtUtc: '2030-01-01T00:00:00Z',
      approvalId: 'apr-cmd-1',
      requestDigest: 'c'.repeat(64),
      signedEnvelope: {
        deviceId: SAMPLE_PAIRING_STATE.deviceId,
        bindingVersion: 5,
        sessionId: 'sess-alpha',
        approvalKind: 'command',
        approvalId: 'apr-cmd-1',
        textSummarySha256: 'd'.repeat(64),
        payloadSha256: 'e'.repeat(64),
        allowedActionsCsv: 'allow_once,deny',
        requestDigest: 'c'.repeat(64),
        expiresAtUnix: 1999999999,
        sigB64U: 'sig',
      },
    };

    const readonlyItem = {
      ...normalItem,
      eventId: 102,
      sourceKind: 'business',
      opaqueRefId: 'ref-biz-2',
      title: '敏感凭据修改',
      readOnlyInRemote: true,
      approvalId: 'apr-biz-2',
      signedEnvelope: null,
    };

    useMobileStore.setState({
      pairingState: SAMPLE_PAIRING_STATE,
      pendingTodos: [normalItem, readonlyItem],
      inspectCommandScript: inspectSpy,
      decideTodoApproval: decideSpy,
    });

    render(<ApprovalsView />);

    expect(screen.getByText(/readOnlyInRemote/)).toBeInTheDocument();
    expect(screen.getByText('仅桌面可批')).toBeInTheDocument();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '查看完整脚本' }));
    });
    expect(inspectSpy).toHaveBeenCalledWith('apr-cmd-1');
    expect(await screen.findByText('echo "verified script body"')).toBeInTheDocument();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '允许一次' }));
    });
    expect(decideSpy).toHaveBeenCalledWith(normalItem, 'allow_once', undefined);
  });

  it('SettingsView 展示硬件指纹、执行策略切换与解绑注销，且源码零支付/订阅关键词', () => {
    const updateModeSpy = vi.fn(async () => undefined);
    const logoutSpy = vi.fn(async () => undefined);

    useMobileStore.setState({
      pairingState: SAMPLE_PAIRING_STATE,
      desktopOnline: true,
      executionSettings: {
        backend: 'Local',
        permissionMode: 'Ask',
        controlPlaneAvailable: true,
      },
      updatePermissionMode: updateModeSpy,
      logoutAndUnpair: logoutSpy,
    });

    render(<SettingsView />);
    expect(screen.getByText(SAMPLE_PAIRING_STATE.desktopSpkiSha256)).toBeInTheDocument();

    const select = screen.getByLabelText('桌面执行权限模式');
    fireEvent.change(select, { target: { value: 'AutoInWorkspace' } });
    expect(updateModeSpy).toHaveBeenCalledWith('AutoInWorkspace');

    fireEvent.click(screen.getByRole('button', { name: '退出并解绑设备' }));
    expect(logoutSpy).toHaveBeenCalledTimes(1);

    // 静态审计 clients/mobile/src 全量源文件严禁出现商业化收费/订阅词汇
    const forbiddenPattern = new RegExp(
      [
        '\\bpric' + 'ing\\b',
        '\\bcheck' + 'out\\b',
        '\\bpay' + 'wall\\b',
        '\\bstr' + 'ipe\\b',
        '\\bsubscri' + 'ption\\b',
        '\\bsubscri' + 'be\\b',
        '\\bbill' + 'ing\\b',
        '\u8ba2\u9605',
        '\u4ed8\u8d39',
        '\u652f\u4ed8',
        '\u4f1a\u5458',
      ].join('|'),
      'i',
    );
    const srcFiles = collectSourceFiles(resolve(__dirname, '../../src'));
    expect(srcFiles.length).toBeGreaterThan(10);
    for (const filePath of srcFiles) {
      const content = readFileSync(filePath, 'utf8');
      expect(content, `Unexpected monetization term in ${filePath}`).not.toMatch(forbiddenPattern);
    }
  });
});
