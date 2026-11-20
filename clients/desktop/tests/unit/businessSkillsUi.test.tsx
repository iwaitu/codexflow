import React from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type {
  BackendConnectionPublicInfo,
  BusinessPluginDto,
  DesktopBridge,
  PublishedSkillDto,
  SkillDraftDto,
  SkillRunDto,
} from '../../src/shared/ipc';
import { ApiError, apiClient } from '../../src/renderer/lib/apiClient';
import { isBusinessSkillsDisabledError } from '../../src/renderer/components/business-skills/businessSkillHelpers';
import { BusinessSkillsHub } from '../../src/renderer/components/business-skills/BusinessSkillsHub';
import { PluginCatalogPanel } from '../../src/renderer/components/business-skills/PluginCatalogPanel';
import { SkillBuilderWizard } from '../../src/renderer/components/business-skills/SkillBuilderWizard';
import { SkillRunCard } from '../../src/renderer/components/business-skills/SkillRunCard';

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
    updateBrowserSettings: vi.fn().mockResolvedValue({ ok: true, status: 200 }),
    closeBrowserSession: vi.fn().mockResolvedValue({ ok: true, status: 200, data: { closed: true, sessionId: 's1' } }),
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
    showWorkspaceFileContextMenu: vi.fn().mockResolvedValue({ ok: true, cancelled: true }),
    showNotification: vi.fn().mockResolvedValue(undefined),
    setDesktopPreferences: vi.fn().mockResolvedValue(undefined),
    onCommandAction: vi.fn().mockReturnValue(() => {}),
    getUpdateState: vi.fn().mockResolvedValue({
      status: 'unavailable',
      currentVersion: '0.1.5',
      availableVersion: null,
      downloadProgress: null,
      message: null,
    }),
    onUpdateStateChanged: vi.fn().mockReturnValue(() => {}),
    checkForUpdates: vi.fn().mockResolvedValue({
      status: 'unavailable',
      currentVersion: '0.1.5',
      availableVersion: null,
      downloadProgress: null,
      message: null,
    }),
    downloadUpdate: vi.fn().mockResolvedValue({
      status: 'unavailable',
      currentVersion: '0.1.5',
      availableVersion: null,
      downloadProgress: null,
      message: null,
    }),
    quitAndInstall: vi.fn().mockResolvedValue(false),
    inspectEnvironment: vi.fn().mockResolvedValue({
      platform: 'darwin',
      arch: 'arm64',
      isPackaged: false,
      packageManager: null,
      shellPathEnhanced: false,
      checkedAtIso: '2026-11-19T00:00:00.000Z',
      items: [],
    }),
    installEnvironmentDependency: vi.fn().mockResolvedValue({ accepted: false }),
    cancelEnvironmentInstall: vi.fn().mockResolvedValue(false),
    onEnvironmentInstallEvent: vi.fn(() => () => undefined),
    ...overrides,
  };
  window.codexflow = bridge;
  return bridge;
}

const AUTH = { baseUrl: 'http://127.0.0.1:59999', accessToken: 'access-token-1' };

const SAMPLE_PLUGIN: BusinessPluginDto = {
  id: 'plugin-1',
  name: '订单系统',
  sourceType: 'OpenApi',
  enabled: true,
  activeSnapshotId: 'snap-1',
  capabilityCount: 3,
  connection: {
    id: 'conn-1',
    sourceUrl: 'https://orders.internal.example/openapi.json',
    environment: 'Sandbox',
    revision: 1,
    isEncryptedTransport: true,
    endpointPolicy: {
      allowedOrigin: 'https://orders.internal.example',
      allowPlainHttpForIntranet: false,
    },
  },
};

describe('Built-in Enterprise Plugin & Skill Builder UI', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('shows feature disabled banner on HTTP 503 BusinessSkills:Enabled=false', async () => {
    installMockBridge();
    vi.spyOn(apiClient, 'listBusinessPlugins').mockRejectedValue(
      new ApiError(503, 'FeatureDisabled', 'BusinessSkills:Enabled=false 未开启'),
    );
    vi.spyOn(apiClient, 'listPublishedSkills').mockResolvedValue([]);

    render(
      <BusinessSkillsHub
        auth={AUTH}
        instanceKey="inst-1"
        activeSessionId="sess-1"
        controlPlaneAvailable
        modelConfigured
      />,
    );

    expect(await screen.findByTestId('business-skills-disabled-banner')).toBeInTheDocument();
    expect(screen.getAllByText(/BusinessSkills:Enabled/).length).toBeGreaterThan(0);
  });

  it('shows read-only banner and disables write buttons when controlPlaneAvailable=false', async () => {
    installMockBridge();
    vi.spyOn(apiClient, 'listBusinessPlugins').mockResolvedValue([SAMPLE_PLUGIN]);
    vi.spyOn(apiClient, 'listPublishedSkills').mockResolvedValue([]);
    vi.spyOn(apiClient, 'queryPluginCapabilities').mockResolvedValue({
      pluginId: 'plugin-1',
      snapshotId: 'snap-1',
      complete: true,
      totalCount: 0,
      filteredCount: 0,
      executableCount: 0,
      needsConfigurationCount: 0,
      unsupportedCount: 0,
      page: 1,
      pageSize: 20,
      items: [],
    });

    render(
      <BusinessSkillsHub
        auth={AUTH}
        instanceKey="inst-1"
        activeSessionId="sess-1"
        controlPlaneAvailable={false}
        modelConfigured
      />,
    );

    expect(await screen.findByTestId('control-plane-readonly-banner')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /接入新插件/ })).toBeDisabled();
    expect(await screen.findByRole('button', { name: /刷新发现目录/ })).toBeDisabled();
  });

  it('clears secret input immediately after submitting credential and blocks remote images in OpenAPI markdown', async () => {
    const setCredFn = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      data: { authKind: 'Bearer', credentialRef: 'cred-1', maskedHint: '***7890' },
    });
    installMockBridge({ setBusinessPluginCredential: setCredFn });

    vi.spyOn(apiClient, 'queryPluginCapabilities').mockResolvedValue({
      pluginId: 'plugin-1',
      snapshotId: 'snap-1',
      complete: true,
      totalCount: 2,
      filteredCount: 2,
      executableCount: 2,
      needsConfigurationCount: 0,
      unsupportedCount: 0,
      page: 1,
      pageSize: 20,
      items: [
        {
          id: 'cap-md',
          snapshotId: 'snap-1',
          title: '查询订单',
          nativeIdentity: 'GET /orders',
          risk: 'Read',
          executability: 'Executable',
          description: '查看订单 ![tracker](https://evil.example/pixel.png)',
          sourceDescriptionMissing: false,
        },
        {
          id: 'cap-missing',
          snapshotId: 'snap-1',
          title: '无说明接口',
          nativeIdentity: 'POST /orders/cancel',
          risk: 'Unknown',
          executability: 'Executable',
          description: '',
          sourceDescriptionMissing: true,
        },
      ],
    });

    vi.spyOn(apiClient, 'getPluginCapabilityDetail').mockImplementation(
      async (_auth, _pid, capId) => {
        if (capId === 'cap-md') {
          return {
            summary: {
              id: 'cap-md',
              title: '查询订单',
              nativeIdentity: 'GET /orders',
              risk: 'Read',
              description: '查看订单 ![tracker](https://evil.example/pixel.png)',
              sourceDescriptionMissing: false,
            },
            invocation: { httpMethod: 'GET', normalizedPath: '/orders' },
          };
        }
        return {
          summary: {
            id: 'cap-missing',
            title: '无说明接口',
            nativeIdentity: 'POST /orders/cancel',
            risk: 'Unknown',
            description: '',
            sourceDescriptionMissing: true,
          },
          invocation: { httpMethod: 'POST', normalizedPath: '/orders/cancel' },
        };
      },
    );

    const { container } = render(
      <PluginCatalogPanel
        auth={AUTH}
        plugins={[SAMPLE_PLUGIN]}
        controlPlaneAvailable
        onRefreshPlugins={vi.fn().mockResolvedValue(undefined)}
      />,
    );

    const tokenInput = await screen.findByPlaceholderText(/输入后仅通过控制面 IPC 落盘/);
    await act(async () => {
      fireEvent.change(tokenInput, { target: { value: 'super-secret-token-7890' } });
      fireEvent.click(screen.getByRole('button', { name: '保存凭证到控制面' }));
    });

    await waitFor(() => expect(setCredFn).toHaveBeenCalledTimes(1));
    expect(setCredFn).toHaveBeenCalledWith({
      pluginId: 'plugin-1',
      slot: 'execution',
      authKind: 'Bearer',
      secretPayload: JSON.stringify({ token: 'super-secret-token-7890' }),
    });
    expect((tokenInput as HTMLInputElement).value).toBe('');

    const capMdBtn = await screen.findByText('查询订单');
    await act(async () => {
      fireEvent.click(capMdBtn);
    });
    await waitFor(() => expect(screen.getByText(/已禁用远程图片/)).toBeInTheDocument());
    expect(container.querySelector('img')).toBeNull();

    await act(async () => {
      fireEvent.click(screen.getByText('无说明接口'));
    });
    expect(await screen.findByText(/源接口未提供说明/)).toBeInTheDocument();
  });

  it('supports 5-step wizard with cross-page selection, model disclosure confirmation, validate, test run, and publish', async () => {
    const updateSelectionFn = vi.fn();
    const generateFn = vi.fn();
    const startTestRunFn = vi.fn();
    const publishFn = vi.fn();

    installMockBridge({
      updateSkillDraftSelection: updateSelectionFn,
      generateSkillDraftCandidate: generateFn,
      startSkillDraftTestRun: startTestRunFn,
      publishSkillDraft: publishFn,
    });

    vi.spyOn(apiClient, 'queryPluginCapabilities').mockImplementation(
      async (_auth, _pid, params = {}) => {
        if ((params.page ?? 1) === 1 && (params.pageSize ?? 20) === 200) {
          return {
            pluginId: 'plugin-1',
            snapshotId: 'snap-1',
            complete: true,
            totalCount: 2,
            filteredCount: 2,
            executableCount: 2,
            needsConfigurationCount: 0,
            unsupportedCount: 0,
            page: 1,
            pageSize: 200,
            items: [
              { id: 'cap-1', title: '查订单', nativeIdentity: 'GET /orders', risk: 'Read', executability: 'Executable' },
              { id: 'cap-2', title: '关订单', nativeIdentity: 'POST /orders/close', risk: 'Write', executability: 'Executable' },
            ],
          };
        }
        return {
          pluginId: 'plugin-1',
          snapshotId: 'snap-1',
          complete: true,
          totalCount: 2,
          filteredCount: 2,
          executableCount: 2,
          needsConfigurationCount: 0,
          unsupportedCount: 0,
          page: params.page ?? 1,
          pageSize: params.pageSize ?? 20,
          items: [
            { id: 'cap-1', title: '查订单', nativeIdentity: 'GET /orders', risk: 'Read', executability: 'Executable' },
          ],
        };
      },
    );

    const initialDraft: SkillDraftDto = {
      id: 'draft-1',
      pluginId: 'plugin-1',
      catalogSnapshotId: 'snap-1',
      name: '订单系统自动化 Skill',
      businessGoal: '按选择能力自动化处理业务流程',
      revision: 1,
      selectedCapabilityIds: ['cap-1', 'cap-2'],
      needsRevalidation: true,
      validationErrors: [],
    };
    const generatedDraft: SkillDraftDto = {
      ...initialDraft,
      name: '超时订单归档',
      businessGoal: '查询超时订单并汇总',
      revision: 2,
      needsRevalidation: false,
      candidateDefinition: {
        skillId: 'draft-1',
        name: '超时订单归档',
        version: 1,
        inputs: { type: 'object', properties: { days: { type: 'integer' } }, required: ['days'] },
        steps: [{ id: 's1', type: 'tool', title: '查订单', capabilityId: 'cap-1' }],
        budgets: { maxExternalRequests: 10, maxReadRecords: 100, maxWriteBatchItems: 20 },
        validationEvidence: {},
      },
    };
    const verifiedDraft: SkillDraftDto = {
      ...generatedDraft,
      revision: 3,
      needsRevalidation: false,
      candidateDefinition: {
        ...generatedDraft.candidateDefinition!,
        validationEvidence: { readOnlyTestRunId: 'run-ro-1', writeStepVerified: false },
      },
    };
    const publishedSkill: PublishedSkillDto = {
      id: 'skill-pub-1',
      name: '超时订单归档',
      activeVersion: 1,
      versions: [],
    };

    vi.spyOn(apiClient, 'createSkillDraft').mockResolvedValue(initialDraft);
    vi.spyOn(apiClient, 'updateSkillDraft').mockResolvedValue({ ...initialDraft, revision: 2 });
    vi.spyOn(apiClient, 'validateSkillDraft').mockResolvedValue(generatedDraft);
    vi.spyOn(apiClient, 'getSkillDraft').mockResolvedValue(verifiedDraft);
    vi.spyOn(apiClient, 'getSkillRun').mockResolvedValue({
      runId: 'run-ro-1',
      status: 'Succeeded',
      isTestRun: true,
      externalRequestCount: 1,
      readOnlyRetryCount: 0,
      outputPayload: { count: 3 },
      steps: [],
    });

    generateFn.mockResolvedValue({
      ok: true,
      status: 200,
      data: {
        disclosedDataCategories: ['capability_metadata', 'business_goal'],
        draft: generatedDraft,
        operation: { kind: 'GenerateSkill', operationId: 'op-1', reusedFromClientRequestId: false, sequence: 1, status: 'Succeeded', version: 1 },
      },
    });
    startTestRunFn.mockResolvedValue({
      ok: true,
      status: 202,
      data: {
        runId: 'run-ro-1',
        status: 'Succeeded',
        isTestRun: true,
        externalRequestCount: 1,
        readOnlyRetryCount: 0,
        outputPayload: { count: 3 },
        steps: [],
      },
    });
    publishFn.mockResolvedValue({ ok: true, status: 200, data: publishedSkill });

    const onPublished = vi.fn().mockResolvedValue(undefined);
    render(
      <SkillBuilderWizard
        auth={AUTH}
        plugins={[SAMPLE_PLUGIN]}
        initialPluginId="plugin-1"
        activeSessionId="sess-1"
        controlPlaneAvailable
        modelConfigured
        onRefreshPlugins={vi.fn().mockResolvedValue(undefined)}
        onPublished={onPublished}
      />,
    );

    // Step 1 -> Step 2
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /下一步：选择能力/ }));
    });
    await waitFor(() => expect(screen.getByText(/第 2 步/)).toBeInTheDocument());

    // 跨页全选可用能力
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '选择全部可用能力' }));
    });
    await waitFor(() => expect(screen.getByText(/已选 2 项/)).toBeInTheDocument());

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '保存选择并进入生成' }));
    });
    await waitFor(() => expect(screen.getByText(/第 3 步/)).toBeInTheDocument());

    // 必须勾选模型外发范围确认
    const confirmCheckbox = screen.getByRole('checkbox', { name: /我已确认本次发送给模型的范围仅限/ });
    await act(async () => {
      fireEvent.change(screen.getByPlaceholderText(/超时未发货订单/), { target: { value: '超时订单归档' } });
      fireEvent.change(screen.getByPlaceholderText(/按订单日期查询/), { target: { value: '查询超时订单并汇总' } });
      fireEvent.click(screen.getByRole('button', { name: '生成候选方案' }));
    });
    expect(await screen.findByText(/请先阅读并勾选/)).toBeInTheDocument();
    expect(generateFn).not.toHaveBeenCalled();

    await act(async () => {
      fireEvent.click(confirmCheckbox);
      fireEvent.click(screen.getByRole('button', { name: '生成候选方案' }));
    });

    await waitFor(() => expect(generateFn).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByText(/第 4 步/)).toBeInTheDocument());

    // 校验 + 只读试运行
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '校验方案' }));
    });
    await act(async () => {
      fireEvent.change(screen.getByPlaceholderText('days'), { target: { value: '7' } });
      fireEvent.click(screen.getByRole('button', { name: '只读试运行' }));
    });
    await waitFor(() => expect(startTestRunFn).toHaveBeenCalledTimes(1));
    expect(startTestRunFn).toHaveBeenCalledWith(
      expect.objectContaining({
        draftId: 'draft-1',
        isTestRun: true,
        allowWriteInTestRun: false,
        inputs: { days: 7 },
      }),
    );

    // Step 5 Publish
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /下一步：发布不可变版本/ }));
    });
    await waitFor(() => expect(screen.getByRole('button', { name: '发布不可变版本' })).not.toBeDisabled());
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '发布不可变版本' }));
    });
    await waitFor(() => expect(publishFn).toHaveBeenCalledTimes(1));
    expect(onPublished).toHaveBeenCalledWith(publishedSkill);
  });

  it('SkillRunCard renders 12-factor summary, WriteStepUnverified warning, and ONLY allow_once + deny buttons', async () => {
    const approveFn = vi.fn().mockResolvedValue({ ok: true, status: 200 });
    installMockBridge({ approveToolExecution: approveFn });

    const waitingRun: SkillRunDto = {
      runId: 'run-write-1',
      skillId: 'skill-1',
      skillVersion: 1,
      status: 'WaitingForApproval',
      isTestRun: false,
      externalRequestCount: 1,
      readOnlyRetryCount: 0,
      steps: [
        { stepId: 'read-orders', stepType: 'tool', state: 'Succeeded' },
        { stepId: 'approve-write', stepType: 'approval', state: 'Prepared' },
      ],
      pendingApproval: {
        approvalId: 'appr-biz-1',
        requestDigest: 'sha256-biz-xyz',
        kind: 'business_skill_write',
        allowedDecisions: ['allow_once', 'allow_always', 'deny'],
        expiresAtUtc: '2026-11-17T18:00:00Z',
        summary: {
          systemName: 'ERP 系统',
          environment: 'Production',
          targetRecordSummary: 'SO-1001 等 3 笔订单',
          plannedMutation: '批量关闭超时订单',
          batchCount: 3,
          keyAmount: 12800,
          currency: 'CNY',
          writeStepUnverified: true,
          requestPreviews: [
            {
              capabilityId: 'cap-close',
              methodOrTool: 'POST',
              targetPath: '/api/orders/close',
              redactedBodyPreview: '{"ids":["SO-1001"],"token":"***"}',
            },
          ],
        },
      },
    };

    vi.spyOn(apiClient, 'getSkillRun').mockResolvedValue({
      ...waitingRun,
      status: 'Succeeded',
      pendingApproval: null,
    });

    render(
      <SkillRunCard
        auth={AUTH}
        runId="run-write-1"
        initialRun={waitingRun}
        controlPlaneAvailable
      />,
    );

    expect(screen.getByTestId('write-step-unverified-banner')).toBeInTheDocument();
    expect(screen.getByText('ERP 系统')).toBeInTheDocument();
    expect(screen.getByText(/12800 CNY/)).toBeInTheDocument();
    expect(screen.getByText('批量关闭超时订单')).toBeInTheDocument();

    // 绝不渲染“始终允许 / 本会话允许”
    expect(screen.queryByText(/始终允许|allow_always|本会话允许|allow_session/i)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: '仅本次批次允许' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '拒绝执行' })).toBeInTheDocument();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '仅本次批次允许' }));
    });

    expect(approveFn).toHaveBeenCalledWith({
      approvalId: 'appr-biz-1',
      requestDigest: 'sha256-biz-xyz',
      decision: 'allow_once',
      note: undefined,
    });
  });

  it('does not misclassify 503 ConnectionUnavailable as BusinessSkills:Enabled=false', () => {
    const connErr = new ApiError(503, 'ConnectionUnavailable', '后端暂不可连接');
    const disabledErr = new ApiError(503, 'FeatureDisabled', 'BusinessSkills:Enabled=false 未开启');
    expect(isBusinessSkillsDisabledError(connErr)).toBe(false);
    expect(isBusinessSkillsDisabledError(disabledErr)).toBe(true);
  });
});
