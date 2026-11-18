import React from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { BackendConnectionPublicInfo, DesktopBridge } from '../../src/shared/ipc';
import { ChatView } from '../../src/renderer/components/ChatView';
import { Composer } from '../../src/renderer/components/Composer';
import { Sidebar } from '../../src/renderer/components/Sidebar';
import { ToolApprovalCard } from '../../src/renderer/components/ToolApprovalCard';
import { useAppStore } from '../../src/renderer/stores/useAppStore';

function installMockBridge(overrides: Partial<DesktopBridge> = {}): DesktopBridge {
  const bridge: DesktopBridge = {
    getBackendConnection: vi.fn().mockResolvedValue({
      mode: 'managed',
      status: 'ready',
      baseUrl: 'http://127.0.0.1:59999',
      targetBaseUrl: 'http://127.0.0.1:5206',
      accessToken: 'normal-token-only',
      instanceId: 'inst-1',
      controlPlaneAvailable: true,
      selfApprovalProtection: true,
      dataDirectory: 'D:/data',
      lastError: null,
      restartCount: 0,
    } satisfies BackendConnectionPublicInfo),
    onBackendConnectionChanged: vi.fn().mockReturnValue(() => {}),
    approveToolExecution: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    getApprovalScript: vi.fn().mockResolvedValue({ ok: false, status: 404 }),
    setSessionPlanMode: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    submitPlanChatAction: vi.fn().mockResolvedValue({ ok: true, status: 202 }),
    updateExecutionSettings: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    updateModelSettings: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    listPermissionRules: vi.fn().mockResolvedValue({ ok: true, status: 200, data: { count: 0, rules: [] } }),
    deletePermissionRule: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    openLocalWorkspace: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    createBusinessPlugin: vi.fn().mockResolvedValue({ ok: true, status: 201 }),
    updateBusinessPluginConnection: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    setBusinessPluginCredential: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    deleteBusinessPluginCredential: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    disableBusinessPlugin: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    discoverBusinessPlugin: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    updateBusinessCapabilityRisk: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    updateSkillDraftSelection: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    generateSkillDraftCandidate: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    startSkillDraftTestRun: vi.fn().mockResolvedValue({ ok: true, status: 202 }),
    publishSkillDraft: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    activateSkillVersion: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    startPublishedSkillRun: vi.fn().mockResolvedValue({ ok: true, status: 202 }),
    cancelBusinessSkillRun: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    pruneBusinessSkillRetention: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    pickFolder: vi.fn().mockResolvedValue(null),
    getPathForFile: vi.fn().mockReturnValue(null),
    classifyDroppedFiles: vi.fn().mockResolvedValue([]),
    pickStandaloneTokenFile: vi.fn().mockResolvedValue(null),
    discoverStandaloneInstance: vi.fn().mockResolvedValue(null),
    connectStandalone: vi.fn(),
    switchToManaged: vi.fn(),
    restartBackend: vi.fn(),
    openExternal: vi.fn().mockResolvedValue(true),
    showNotification: vi.fn().mockResolvedValue(undefined),
    setDesktopPreferences: vi.fn().mockResolvedValue(undefined),
    onCommandAction: vi.fn().mockReturnValue(() => {}),
    getUpdateState: vi.fn().mockResolvedValue({
      status: 'unavailable',
      currentVersion: '0.1.7',
      availableVersion: null,
      downloadProgress: null,
      message: null,
    }),
    onUpdateStateChanged: vi.fn().mockReturnValue(() => {}),
    checkForUpdates: vi.fn().mockResolvedValue({
      status: 'unavailable',
      currentVersion: '0.1.7',
      availableVersion: null,
      downloadProgress: null,
      message: null,
    }),
    downloadUpdate: vi.fn().mockResolvedValue({
      status: 'unavailable',
      currentVersion: '0.1.7',
      availableVersion: null,
      downloadProgress: null,
      message: null,
    }),
    quitAndInstall: vi.fn().mockResolvedValue(false),
    ...overrides,
  };
  window.codexflow = bridge;
  return bridge;
}

async function sha256Hex(text: string): Promise<string> {
  const bytes = new TextEncoder().encode(text);
  const digest = await globalThis.crypto.subtle.digest('SHA-256', bytes);
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('');
}

describe('Daily/Development WorkMode, Slash /plan, and Script SHA-256 Approval', () => {
  beforeEach(() => {
    installMockBridge();
    useAppStore.getState().setActiveSession(null);
  });

  it('Composer omits legacy thread-mode dropdown and shows WorkMode badge, Plan banner, IME-safe slash autocomplete, and //plan escape', async () => {
    const onSend = vi.fn().mockResolvedValue(undefined);
    const onSlash = vi.fn().mockResolvedValue(undefined);

    render(
      <Composer
        disabled={false}
        isTurnRunning={false}
        turnNotice={null}
        workMode="Daily"
        planModeActive
        onSend={onSend}
        onCancel={vi.fn().mockResolvedValue(undefined)}
        onSlashCommand={onSlash}
      />,
    );

    expect(screen.queryByLabelText('对话线程')).not.toBeInTheDocument();
    expect(screen.getByTestId('composer-work-mode-badge')).toHaveTextContent('日常模式');
    expect(screen.getByTestId('plan-mode-banner')).toBeInTheDocument();

    const textarea = screen.getByRole('textbox');

    // Type / to trigger autocomplete listbox
    fireEvent.change(textarea, { target: { value: '/' } });
    expect(screen.getByRole('listbox', { name: '斜杠命令自动补全' })).toBeInTheDocument();

    // IME composing Enter should NOT submit
    fireEvent.compositionStart(textarea);
    fireEvent.keyDown(textarea, { key: 'Enter', isComposing: true });
    expect(onSlash).not.toHaveBeenCalled();
    fireEvent.compositionEnd(textarea);

    // /plan off executes exit plan mode command
    fireEvent.change(textarea, { target: { value: '/plan off' } });
    await act(async () => {
      fireEvent.keyDown(textarea, { key: 'Enter' });
    });
    expect(onSlash).toHaveBeenCalledWith({ kind: 'plan-off' });
    expect(onSend).not.toHaveBeenCalled();

    // //plan escapes to literal /plan text
    fireEvent.change(textarea, { target: { value: '//plan explain' } });
    await act(async () => {
      fireEvent.keyDown(textarea, { key: 'Enter' });
    });
    expect(onSend).toHaveBeenCalledWith('/plan explain', []);
  });

  it('ToolApprovalCard requires matching WebCrypto SHA-256 script snapshot before enabling 允许一次', async () => {
    const scriptText = 'Get-ChildItem outputs\nWrite-Output "ok"';
    const expectedSha = await sha256Hex(scriptText);

    const getScriptMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      data: {
        approvalId: 'appr-shell-1',
        sessionId: 's-1',
        requestDigest: 'digest-123',
        scriptSha256: expectedSha,
        shell: 'powershell',
        scriptText,
        scriptLength: scriptText.length,
        expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
      },
    });
    installMockBridge({ getApprovalScript: getScriptMock });

    render(
      <ToolApprovalCard
        approval={{
          approvalId: 'appr-shell-1',
          requestDigest: 'digest-123',
          sessionId: 's-1',
          tool: 'run_shell',
          executable: 'powershell.exe',
          interpreterChain: [],
          argv: ['-NoProfile'],
          cwd: 'D:/ws',
          scriptSha256: expectedSha,
          shellKind: 'powershell',
          backend: 'Local',
          network: 'Host',
          filesystem: 'LocalOutputsOnly',
          reason: 'daily script',
          expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
          allowedDecisions: ['allow_once', 'deny'],
          executesWorkspaceCode: true,
          trustedOriginVerified: true,
        }}
        controlPlaneAvailable
        onResolved={vi.fn()}
      />,
    );

    await waitFor(() => expect(screen.getByTestId('script-sha-verified')).toBeInTheDocument());
    expect(screen.getByTestId('approval-script-preview')).toHaveTextContent('Get-ChildItem outputs');
    expect(screen.getByRole('button', { name: '允许一次' })).not.toBeDisabled();

    // Now test mismatched SHA-256 keeps 允许一次 disabled
    installMockBridge({
      getApprovalScript: vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        data: {
          approvalId: 'appr-shell-bad',
          sessionId: 's-1',
          requestDigest: 'digest-bad',
          scriptSha256: expectedSha,
          shell: 'powershell',
          scriptText: 'rm -rf / # tampered',
          scriptLength: 19,
          expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
        },
      }),
    });

    render(
      <ToolApprovalCard
        approval={{
          approvalId: 'appr-shell-bad',
          requestDigest: 'digest-bad',
          sessionId: 's-1',
          tool: 'run_shell',
          executable: 'powershell.exe',
          interpreterChain: [],
          argv: ['-NoProfile'],
          cwd: 'D:/ws',
          scriptSha256: expectedSha,
          backend: 'Local',
          network: 'Host',
          filesystem: 'LocalOutputsOnly',
          reason: 'daily script',
          expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
          allowedDecisions: ['allow_once', 'deny'],
          executesWorkspaceCode: true,
          trustedOriginVerified: true,
        }}
        controlPlaneAvailable
        onResolved={vi.fn()}
      />,
    );

    await waitFor(() => expect(screen.getByTestId('script-verify-error')).toBeInTheDocument());
    const allowButtons = screen.getAllByRole('button', { name: '允许一次' });
    expect(allowButtons[1]).toBeDisabled();
  });

  it('Sidebar renders 日常助手 shortcut and protects 日常助手 from rename/delete', () => {
    const onDailyAssistant = vi.fn().mockResolvedValue(undefined);
    render(
      <Sidebar
        sessions={[
          {
            sessionId: 's-daily',
            name: '日常助手',
            workspacePath: 'D:/ws/daily',
            currentStage: 1,
            isArchived: false,
            isExternalWorkspace: false,
            createdUtc: '2026-11-18T00:00:00Z',
            currentPlanArtifactId: null,
            activeTaskId: null,
            workMode: 'Daily',
            modeVersion: 1,
            planModeActive: false,
            planModeVersion: 1,
          },
        ]}
        conversations={[]}
        activeSessionId="s-daily"
        activeConversationId={null}
        controlPlaneAvailable
        onSelectSession={vi.fn()}
        onSelectConversation={vi.fn()}
        onCreateSession={vi.fn().mockResolvedValue(undefined)}
        onOpenDailyAssistant={onDailyAssistant}
        onOpenFolder={vi.fn().mockResolvedValue(undefined)}
        onRenameSession={vi.fn().mockResolvedValue(undefined)}
        onArchiveSession={vi.fn().mockResolvedValue(undefined)}
        onDeleteSession={vi.fn().mockResolvedValue(undefined)}
        onRenameConversation={vi.fn().mockResolvedValue(undefined)}
        onDeleteConversation={vi.fn().mockResolvedValue(undefined)}
        onOpenSettings={vi.fn()}
        workspaceError={null}
      />,
    );

    fireEvent.click(screen.getByTestId('daily-assistant-shortcut'));
    expect(onDailyAssistant).toHaveBeenCalledTimes(1);
    expect(screen.queryByTitle('重命名')).not.toBeInTheDocument();
    expect(screen.queryByTitle('删除')).not.toBeInTheDocument();
  });

  it('useAppStore tracks WorkMode and plan_mode_changed / plan_mode_exit_requested SSE events', () => {
    useAppStore.getState().setActiveSession('s-1');
    useAppStore.getState().applySseEvent({
      event: 'plan_mode_changed',
      data: {
        sessionId: 's-1',
        workMode: 'Daily',
        modeVersion: 1,
        planModeActive: true,
        planModeVersion: 2,
      },
    });
    expect(useAppStore.getState().activeWorkMode).toBe('Daily');
    expect(useAppStore.getState().planModeActive).toBe(true);
    expect(useAppStore.getState().planModeVersion).toBe(2);

    useAppStore.getState().applySseEvent({
      event: 'plan_mode_exit_requested',
      data: {
        sessionId: 's-1',
        planModeVersion: 2,
        planSummary: 'Ready to run cleanup script.',
      },
    });
    expect(useAppStore.getState().planModeExitRequest?.planSummary).toBe('Ready to run cleanup script.');
  });

  it('WorkMode badge opens interactive mode menu and ChatView distinguishes Daily vs Development welcome cards', async () => {
    const onSlash = vi.fn().mockResolvedValue(undefined);
    const onDaily = vi.fn().mockResolvedValue(undefined);

    render(
      <Composer
        disabled={false}
        isTurnRunning={false}
        turnNotice={null}
        workMode="Development"
        planModeActive={false}
        onSend={vi.fn().mockResolvedValue(undefined)}
        onCancel={vi.fn().mockResolvedValue(undefined)}
        onSlashCommand={onSlash}
        onOpenDailyAssistant={onDaily}
      />,
    );

    fireEvent.click(screen.getByTestId('composer-work-mode-badge'));
    expect(screen.getByRole('menu', { name: '工作模式菜单' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('menuitem', { name: /切换到「日常助手」/ }));
    expect(onDaily).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByTestId('composer-work-mode-badge'));
    fireEvent.click(screen.getByRole('menuitem', { name: /新建日常模式对话/ }));
    expect(onSlash).toHaveBeenCalledWith({ kind: 'daily' });

    const { rerender } = render(
      <ChatView
        sessionId="s-daily"
        conversationId={null}
        messages={[]}
        messagesTruncated={false}
        streamingContent=""
        streamingThinking=""
        isThinkingStreaming={false}
        isTurnRunning={false}
        toolCalls={[]}
        pendingApprovals={[]}
        currentPlan={null}
        jobs={[]}
        verificationReceipts={{}}
        workerBar={{ status: 'idle', message: null }}
        executionBackend="Local"
        controlPlaneAvailable
        workMode="Daily"
        planModeActive={false}
        onApprovalResolved={vi.fn()}
        onPlanActionSubmitted={vi.fn()}
        onVerifyJob={vi.fn()}
        onSwitchManaged={vi.fn()}
      />,
    );
    expect(screen.getByTestId('empty-session-welcome')).toHaveTextContent('日常助手已就绪（日常模式）');

    rerender(
      <ChatView
        sessionId="s-dev"
        conversationId={null}
        messages={[]}
        messagesTruncated={false}
        streamingContent=""
        streamingThinking=""
        isThinkingStreaming={false}
        isTurnRunning={false}
        toolCalls={[]}
        pendingApprovals={[]}
        currentPlan={null}
        jobs={[]}
        verificationReceipts={{}}
        workerBar={{ status: 'idle', message: null }}
        executionBackend="Local"
        controlPlaneAvailable
        workMode="Development"
        planModeActive={false}
        onApprovalResolved={vi.fn()}
        onPlanActionSubmitted={vi.fn()}
        onVerifyJob={vi.fn()}
        onSwitchManaged={vi.fn()}
      />,
    );
    expect(screen.getByTestId('empty-session-welcome')).toHaveTextContent('开发模式已就绪（Development）');
  });

  it('Sidebar supports inline conversation rename and delete confirmation without window.prompt', async () => {
    const onRenameConversation = vi.fn().mockResolvedValue(undefined);
    const onDeleteConversation = vi.fn().mockResolvedValue(undefined);

    render(
      <Sidebar
        sessions={[
          {
            sessionId: 's-1',
            name: '项目空间',
            workspacePath: 'D:/repo',
            currentStage: 1,
            isArchived: false,
            isExternalWorkspace: true,
            createdUtc: '2026-11-18T00:00:00Z',
            currentPlanArtifactId: null,
            activeTaskId: null,
            workMode: 'Development',
            modeVersion: 1,
            planModeActive: false,
            planModeVersion: 1,
          },
        ]}
        conversations={[
          {
            conversationId: 'c-1',
            sessionId: 's-1',
            name: '旧对话名称',
            threadType: 'Chat',
            isArchived: false,
            taskId: null,
            createdUtc: '2026-11-18T00:00:00Z',
            updatedAtUtc: '2026-11-18T00:00:00Z',
          },
        ]}
        activeSessionId="s-1"
        activeConversationId="c-1"
        controlPlaneAvailable
        onSelectSession={vi.fn()}
        onSelectConversation={vi.fn()}
        onCreateSession={vi.fn().mockResolvedValue(undefined)}
        onOpenFolder={vi.fn().mockResolvedValue(undefined)}
        onRenameSession={vi.fn().mockResolvedValue(undefined)}
        onArchiveSession={vi.fn().mockResolvedValue(undefined)}
        onDeleteSession={vi.fn().mockResolvedValue(undefined)}
        onRenameConversation={onRenameConversation}
        onDeleteConversation={onDeleteConversation}
        onOpenSettings={vi.fn()}
        workspaceError={null}
      />,
    );

    fireEvent.click(screen.getByLabelText('重命名对话 旧对话名称'));
    const input = screen.getByLabelText('对话新名称');
    fireEvent.change(input, { target: { value: '新对话名称' } });
    await act(async () => {
      fireEvent.keyDown(input, { key: 'Enter' });
    });
    expect(onRenameConversation).toHaveBeenCalledWith('c-1', '新对话名称');

    fireEvent.click(screen.getByLabelText('删除对话 旧对话名称'));
    expect(onDeleteConversation).not.toHaveBeenCalled();
    expect(screen.getByTestId('confirm-delete-conv-c-1')).toBeInTheDocument();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '确认' }));
    });
    expect(onDeleteConversation).toHaveBeenCalledWith('c-1');
  });

  it('Composer Tab autocomplete fills command without firing, and ToolApprovalCard flags BiDi + offers script retry', async () => {
    const onSlash = vi.fn().mockResolvedValue(undefined);
    render(
      <Composer
        disabled={false}
        isTurnRunning={false}
        turnNotice={null}
        workMode="Development"
        planModeActive={false}
        onSend={vi.fn().mockResolvedValue(undefined)}
        onCancel={vi.fn().mockResolvedValue(undefined)}
        onSlashCommand={onSlash}
      />,
    );

    const textarea = screen.getByRole('textbox') as HTMLTextAreaElement;
    fireEvent.change(textarea, { target: { value: '/da' } });
    fireEvent.keyDown(textarea, { key: 'Tab' });
    expect(onSlash).not.toHaveBeenCalled();
    expect(textarea.value).toBe('/daily ');

    const bidiScript = 'Write-Output "safe\u202eevil"';
    const bidiSha = await sha256Hex(bidiScript);
    const getScriptMock = vi
      .fn()
      .mockResolvedValueOnce({ ok: false, status: 500, errorMessage: '临时拉取失败' })
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        data: {
          approvalId: 'appr-bidi',
          sessionId: 's-1',
          requestDigest: 'digest-bidi',
          scriptSha256: bidiSha,
          shell: 'powershell',
          scriptText: bidiScript,
          scriptLength: bidiScript.length,
          expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
        },
      });
    installMockBridge({ getApprovalScript: getScriptMock });

    render(
      <ToolApprovalCard
        approval={{
          approvalId: 'appr-bidi',
          requestDigest: 'digest-bidi',
          sessionId: 's-1',
          tool: 'run_shell',
          executable: 'powershell.exe',
          interpreterChain: [],
          argv: ['-NoProfile'],
          cwd: 'D:/ws',
          scriptSha256: bidiSha,
          backend: 'Local',
          network: 'Host',
          filesystem: 'LocalOutputsOnly',
          reason: 'daily script',
          expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
          allowedDecisions: ['allow_once', 'deny'],
          executesWorkspaceCode: true,
          trustedOriginVerified: true,
        }}
        controlPlaneAvailable
        onResolved={vi.fn()}
      />,
    );

    await waitFor(() => expect(screen.getByRole('button', { name: '重试拉取脚本' })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: '重试拉取脚本' }));
    await waitFor(() => expect(screen.getByTestId('script-sha-verified')).toBeInTheDocument());
    expect(screen.getByText(/脚本包含不可见或控制字符/)).toBeInTheDocument();
  });

  it('Composer supports pasting clipboard screenshots and image files when supportsMultimodal is enabled, and warns when disabled', async () => {
    const createObjUrlSpy = vi.fn(() => 'blob:mock-preview-1');
    const revokeObjUrlSpy = vi.fn();
    const origCreate = URL.createObjectURL;
    const origRevoke = URL.revokeObjectURL;
    URL.createObjectURL = createObjUrlSpy;
    URL.revokeObjectURL = revokeObjUrlSpy;

    try {
      const onSend = vi.fn().mockResolvedValue(undefined);
      const { rerender } = render(
        <Composer
          disabled={false}
          isTurnRunning={false}
          turnNotice={null}
          supportsMultimodal
          onSend={onSend}
          onCancel={vi.fn().mockResolvedValue(undefined)}
        />,
      );

      const textarea = screen.getByRole('textbox');
      const screenshot = new File(['png-data'], 'image.png', { type: 'image/png' });
      await act(async () => {
        fireEvent.paste(textarea, {
          clipboardData: {
            getData: () => '',
            items: [
              {
                kind: 'file',
                type: 'image/png',
                getAsFile: () => screenshot,
              },
            ],
            files: [],
          },
        });
      });

      await waitFor(() => expect(screen.getByTestId('composer-attachments')).toBeInTheDocument());
      const pastedNameEl = screen.getByText(/^clipboard-\d{6}-1\.png$/);
      expect(pastedNameEl.closest('.opacity-0')).not.toBeNull();
      expect(screen.getByRole('img', { name: pastedNameEl.textContent ?? '' })).toHaveAttribute(
        'src',
        'blob:mock-preview-1',
      );

      fireEvent.click(screen.getByLabelText(`放大预览 ${pastedNameEl.textContent}`));
      expect(screen.getByRole('dialog', { name: `预览图片 ${pastedNameEl.textContent}` })).toBeInTheDocument();
      fireEvent.keyDown(window, { key: 'Escape' });
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

      const namedFile = new File(['webp-data'], 'diagram.webp', { type: 'image/webp' });
      await act(async () => {
        fireEvent.paste(textarea, {
          clipboardData: {
            getData: () => '',
            items: [],
            files: [namedFile],
          },
        });
      });
      await waitFor(() => expect(screen.getByTestId('image-attachment-card-diagram.webp')).toBeInTheDocument());

      await act(async () => {
        fireEvent.click(screen.getByRole('button', { name: '发送' }));
      });
      expect(onSend).toHaveBeenCalledWith(
        '',
        expect.arrayContaining([
          expect.objectContaining({ contentType: 'image/png' }),
          expect.objectContaining({ name: 'diagram.webp', contentType: 'image/webp' }),
        ]),
      );
      expect(revokeObjUrlSpy).toHaveBeenCalled();

      rerender(
        <Composer
          disabled={false}
          isTurnRunning={false}
          turnNotice={null}
          supportsMultimodal={false}
          onSend={onSend}
          onCancel={vi.fn().mockResolvedValue(undefined)}
        />,
      );

      const blockedShot = new File(['png-data'], 'image.png', { type: 'image/png' });
      await act(async () => {
        fireEvent.paste(textarea, {
          clipboardData: {
            getData: () => '',
            items: [{ kind: 'file', type: 'image/png', getAsFile: () => blockedShot }],
            files: [],
          },
        });
      });
      await waitFor(() =>
        expect(screen.getByText(/当前模型未启用多模态能力，无法读取图片/)).toBeInTheDocument(),
      );
    } finally {
      URL.createObjectURL = origCreate;
      URL.revokeObjectURL = origRevoke;
    }
  });

  it('Composer renders image previews vs category-specific document icons and badges', async () => {
    render(
      <Composer
        disabled={false}
        isTurnRunning={false}
        turnNotice={null}
        supportsMultimodal
        onSend={vi.fn().mockResolvedValue(undefined)}
        onCancel={vi.fn().mockResolvedValue(undefined)}
      />,
    );

    const fileInput = screen.getByLabelText('选择本地文件附件') as HTMLInputElement;
    const files = [
      new File(['%PDF-1.4'], 'spec.pdf', { type: 'application/pdf' }),
      new File(['word'], 'report.docx', {
        type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
      }),
      new File(['sheet'], 'budget.xlsx', {
        type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      }),
      new File(['const a = 1;'], 'app.ts', { type: 'text/typescript' }),
      new File(['zip'], 'bundle.zip', { type: 'application/zip' }),
    ];

    await act(async () => {
      fireEvent.change(fileInput, { target: { files } });
    });

    await waitFor(() => expect(screen.getByTestId('file-attachment-card-spec.pdf')).toBeInTheDocument());
    expect(screen.getByTestId('file-attachment-card-spec.pdf')).toHaveAttribute('data-attachment-category', 'pdf');
    expect(screen.getByTestId('file-attachment-card-report.docx')).toHaveAttribute('data-attachment-category', 'word');
    expect(screen.getByTestId('file-attachment-card-budget.xlsx')).toHaveAttribute('data-attachment-category', 'spreadsheet');
    expect(screen.getByTestId('file-attachment-card-app.ts')).toHaveAttribute('data-attachment-category', 'code');
    expect(screen.getByTestId('file-attachment-card-bundle.zip')).toHaveAttribute('data-attachment-category', 'archive');
    expect(screen.getByText('PDF')).toBeInTheDocument();
    expect(screen.getByText('DOCX')).toBeInTheDocument();
    expect(screen.getByText('XLSX')).toBeInTheDocument();
    expect(screen.getByText('TS')).toBeInTheDocument();
    expect(screen.getByText('ZIP')).toBeInTheDocument();
  });
});
