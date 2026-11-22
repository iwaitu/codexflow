import type { components } from '@shared/contracts/api.generated';
import type {
  BusinessApprovalRequestDto,
  BusinessPluginDto,
  CapabilityDetailDto,
  CapabilityPageResponseDto,
  CreateSkillDraftRequest,
  PluginOperationStatusDto,
  PublishedSkillDto,
  SkillDraftDto,
  SkillMarkdownPreviewDto,
  SkillRunDto,
  UpdateSkillDraftRequest,
} from '@shared/ipc';

export type SystemStatusSummary = components['schemas']['SystemStatusSummaryDto'];
export type ExecutionSettings = components['schemas']['ExecutionSettingsDto'];
export type ModelSettings = components['schemas']['ModelSettingsDto'];
export type SessionSummary = components['schemas']['SessionSummaryDto'];
export type ConversationSummary = components['schemas']['ConversationSummaryDto'];
export type SessionPlan = components['schemas']['SessionPlanDto'];
export type SessionSnapshot = components['schemas']['SessionSnapshotDto'];
export type PendingApprovalItem = components['schemas']['PendingApprovalItem'];
export type PendingApprovalsResponse = {
  sessionId: string;
  count: number;
  items: PendingApprovalItem[];
  controlPlaneAvailable: boolean;
  selfApprovalProtection: boolean;
  businessApprovals?: BusinessApprovalRequestDto[];
  browserApprovals?: components['schemas']['BrowserApprovalRequestDto'][];
  computerApprovals?: components['schemas']['ComputerApprovalRequestDto'][];
};
export type BackgroundJob = components['schemas']['BackgroundJobDto'];
export type WorkspaceFileEntry = components['schemas']['WorkspaceFileEntryDto'];
export type WorkspaceDirectoryListing = components['schemas']['WorkspaceDirectoryListingDto'];
export type WorkspaceFileContent = components['schemas']['WorkspaceFileContentDto'];
export type WorkspaceChangedFile = components['schemas']['WorkspaceChangedFileDto'];
export type WorkspaceChanges = components['schemas']['WorkspaceChangesDto'];
export type ChatAttachment = components['schemas']['ChatAttachmentDto'];
export type UploadedAttachment = components['schemas']['UploadedAttachmentDto'];

export interface SessionContextWindow {
  sessionId: string;
  estimatedTokens: number;
  estimatedChars: number;
  recentTurns: number;
  summaryChars: number;
  totalPromptTokens: number;
  totalCachedPromptTokens: number;
  totalCompletionTokens: number;
  totalSessionTokens: number;
  modelMaxTokens: number;
  compressionTriggerTokens: number;
  usageRatio: number;
  triggerRatio: number;
  updatedAtUtc?: string | null;
}

export interface CompactContextResponse {
  sessionId: string;
  compressed: boolean;
  status: string;
  compressedTurnCount: number;
  estimatedTokensBefore: number;
  estimatedTokensAfter: number;
  summaryChars: number;
  lastCompressedAtUtc?: string | null;
  contextWindow: SessionContextWindow;
}

export interface SessionBootstrapResponse {
  sessionId: string;
  workspaceName: string;
  created: boolean;
  workspacePath: string;
  isDefault: boolean;
  workMode?: 'Development' | 'Daily' | string;
  modeVersion?: number;
  planModeActive?: boolean;
  planModeVersion?: number;
}

export type SessionWorkMode = 'Development' | 'Daily';

export interface PlanModeExitRequestState {
  sessionId: string;
  planModeVersion: number;
  planSummary: string;
  reason?: string | null;
}

export interface VerificationReceipt {
  identityVerified?: boolean;
  buildPassed?: boolean;
  testsPassed?: boolean;
  verificationImage?: string | null;
  verificationImageId?: string | null;
  audit?: { passed?: boolean } | null;
  failureCode?: string | null;
  message?: string | null;
  verificationStatus?: string | null;
  verifiedWithoutIsolation?: boolean;
  verificationNotice?: string | null;
}

export interface HistoryMessage {
  id: string;
  role: 'user' | 'assistant' | 'system';
  content: string;
  thinkingContent?: string | null;
  messageType?: string | null;
  notificationMeta?: string | null;
  toolCallResult?: string | null;
  truncated?: boolean;
  createdUtc?: string;
}

export interface PagedSessions {
  items: SessionSummary[];
  total: number;
  page: number;
  pageSize: number;
}

export interface PagedConversations {
  items: ConversationSummary[];
  total: number;
  page: number;
  pageSize: number;
}

export class ApiError extends Error {
  public readonly status: number;
  public readonly code: string;

  constructor(status: number, code: string, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
  }
}

export interface ApiAuthContext {
  baseUrl: string;
  accessToken: string;
}

async function parseApiResponse<T>(response: Response, fallbackMessage: string): Promise<T> {
  if (response.status === 204) {
    return undefined as T;
  }

  const rawText = await response.text();
  let parsed: Record<string, unknown> | undefined;
  if (rawText) {
    try {
      parsed = JSON.parse(rawText) as Record<string, unknown>;
    } catch {
      parsed = undefined;
    }
  }

  if (!response.ok) {
    const code =
      (typeof parsed?.code === 'string' && parsed.code) ||
      (typeof parsed?.reason === 'string' && parsed.reason) ||
      `http_${response.status}`;
    const message =
      (typeof parsed?.message === 'string' && parsed.message) ||
      rawText ||
      response.statusText ||
      `${fallbackMessage} (${response.status})`;
    throw new ApiError(response.status, code, message);
  }

  return (parsed as T) ?? (undefined as T);
}

async function requestJson<T>(
  auth: ApiAuthContext,
  method: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE',
  path: string,
  body?: unknown,
  signal?: AbortSignal,
): Promise<T> {
  const response = await fetch(`${auth.baseUrl}${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${auth.accessToken}`,
      ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
    signal,
  });

  return parseApiResponse<T>(response, '请求失败');
}

export const apiClient = {
  getSystemStatus(auth: ApiAuthContext, refresh = false): Promise<SystemStatusSummary> {
    return requestJson<SystemStatusSummary>(auth, 'GET', `/api/system/status?refresh=${refresh ? 'true' : 'false'}`);
  },

  getExecutionSettings(auth: ApiAuthContext): Promise<ExecutionSettings> {
    return requestJson<ExecutionSettings>(auth, 'GET', '/api/settings/execution');
  },

  getModelSettings(auth: ApiAuthContext): Promise<ModelSettings> {
    return requestJson<ModelSettings>(auth, 'GET', '/api/settings/model');
  },

  bootstrapSession(auth: ApiAuthContext, targetSessionId?: string | null): Promise<SessionBootstrapResponse> {
    return requestJson<SessionBootstrapResponse>(auth, 'POST', '/api/gateway/session/bootstrap', {
      targetSessionId: targetSessionId || undefined,
    });
  },

  getOrCreateDailyAssistant(auth: ApiAuthContext): Promise<SessionBootstrapResponse> {
    return requestJson<SessionBootstrapResponse>(auth, 'POST', '/api/gateway/session/daily-assistant');
  },

  listSessions(auth: ApiAuthContext, page = 1, pageSize = 50, includeArchived = false): Promise<PagedSessions> {
    return requestJson<PagedSessions>(
      auth,
      'GET',
      `/api/gateway/sessions?page=${page}&pageSize=${pageSize}&includeArchived=${includeArchived ? 'true' : 'false'}`,
    );
  },

  createSession(
    auth: ApiAuthContext,
    name: string,
    workMode?: 'development' | 'daily',
  ): Promise<{ sessionId: string; workMode?: string; modeVersion?: number; planModeActive?: boolean; planModeVersion?: number }> {
    const suffix = workMode ? `&workMode=${encodeURIComponent(workMode)}` : '';
    return requestJson(auth, 'POST', `/api/gateway/session?name=${encodeURIComponent(name)}${suffix}`);
  },

  enterPlanMode(auth: ApiAuthContext, sessionId: string, reason?: string): Promise<components['schemas']['PlanModeResponse']> {
    return requestJson<components['schemas']['PlanModeResponse']>(
      auth,
      'POST',
      `/api/gateway/session/${encodeURIComponent(sessionId)}/plan-mode`,
      { active: true, reason },
    );
  },

  updateSession(auth: ApiAuthContext, sessionId: string, body: { name?: string; isArchived?: boolean }): Promise<SessionSummary> {
    return requestJson<SessionSummary>(auth, 'PATCH', `/api/gateway/session/${encodeURIComponent(sessionId)}`, body);
  },

  deleteSession(auth: ApiAuthContext, sessionId: string): Promise<{ deleted: boolean; id: string }> {
    return requestJson<{ deleted: boolean; id: string }>(auth, 'DELETE', `/api/gateway/session/${encodeURIComponent(sessionId)}`);
  },

  listConversations(auth: ApiAuthContext, sessionId: string, page = 1, pageSize = 50): Promise<PagedConversations> {
    return requestJson<PagedConversations>(
      auth,
      'GET',
      `/api/gateway/session/${encodeURIComponent(sessionId)}/conversations?page=${page}&pageSize=${pageSize}`,
    );
  },

  createConversation(auth: ApiAuthContext, sessionId: string, name?: string): Promise<ConversationSummary> {
    return requestJson<ConversationSummary>(
      auth,
      'POST',
      `/api/gateway/session/${encodeURIComponent(sessionId)}/conversations`,
      name?.trim() ? { name: name.trim() } : {},
    );
  },

  updateConversation(auth: ApiAuthContext, conversationId: string, body: { name?: string; isArchived?: boolean }): Promise<ConversationSummary> {
    return requestJson<ConversationSummary>(auth, 'PATCH', `/api/gateway/conversations/${encodeURIComponent(conversationId)}`, body);
  },

  deleteConversation(auth: ApiAuthContext, conversationId: string): Promise<{ deleted: boolean; id: string }> {
    return requestJson<{ deleted: boolean; id: string }>(auth, 'DELETE', `/api/gateway/conversations/${encodeURIComponent(conversationId)}`);
  },

  getHistory(auth: ApiAuthContext, conversationId: string): Promise<HistoryMessage[]> {
    return requestJson<HistoryMessage[]>(auth, 'GET', `/api/gateway/history/${encodeURIComponent(conversationId)}`);
  },

  getSnapshot(auth: ApiAuthContext, sessionId: string, signal?: AbortSignal): Promise<SessionSnapshot> {
    return requestJson<SessionSnapshot>(
      auth,
      'GET',
      `/api/gateway/session/${encodeURIComponent(sessionId)}/snapshot`,
      undefined,
      signal,
    );
  },

  getPlan(auth: ApiAuthContext, sessionId: string): Promise<SessionPlan> {
    return requestJson<SessionPlan>(auth, 'GET', `/api/gateway/session/${encodeURIComponent(sessionId)}/plan`);
  },

  getPendingApprovals(auth: ApiAuthContext, sessionId: string, signal?: AbortSignal): Promise<PendingApprovalsResponse> {
    return requestJson<PendingApprovalsResponse>(
      auth,
      'GET',
      `/api/gateway/session/${encodeURIComponent(sessionId)}/approvals`,
      undefined,
      signal,
    );
  },

  getContextWindow(
    auth: ApiAuthContext,
    sessionId: string,
    signal?: AbortSignal,
    conversationId?: string,
  ): Promise<SessionContextWindow> {
    const query = conversationId ? `?conversationId=${encodeURIComponent(conversationId)}` : '';
    return requestJson<SessionContextWindow>(
      auth,
      'GET',
      `/api/gateway/session/${encodeURIComponent(sessionId)}/context-window${query}`,
      undefined,
      signal,
    );
  },

  compactContext(
    auth: ApiAuthContext,
    sessionId: string,
    payload?: { conversationId?: string; focus?: string },
  ): Promise<CompactContextResponse> {
    return requestJson<CompactContextResponse>(
      auth,
      'POST',
      `/api/gateway/session/${encodeURIComponent(sessionId)}/compact`,
      payload ?? {},
    );
  },

  listJobs(auth: ApiAuthContext, sessionId: string): Promise<BackgroundJob[]> {
    return requestJson<BackgroundJob[]>(auth, 'GET', `/api/jobs/session/${encodeURIComponent(sessionId)}`);
  },

  sendChat(
    auth: ApiAuthContext,
    payload: {
      sessionId: string;
      message: string;
      conversationId?: string;
      conversationMode?: string;
      structuredAction?: components['schemas']['GatewayStructuredActionRequest'];
      attachments?: ChatAttachment[];
    },
  ): Promise<{ messageId: string; sessionId: string }> {
    return requestJson<{ messageId: string; sessionId: string }>(auth, 'POST', '/api/gateway/chat', payload);
  },

  async uploadAttachment(auth: ApiAuthContext, sessionId: string, file: File): Promise<UploadedAttachment> {
    const form = new FormData();
    form.append('file', file, file.name);
    const response = await fetch(`${auth.baseUrl}/api/gateway/session/${encodeURIComponent(sessionId)}/attachments`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${auth.accessToken}`,
      },
      body: form,
    });

    return parseApiResponse<UploadedAttachment>(response, '上传附件失败');
  },

  cancelTurn(auth: ApiAuthContext, sessionId: string): Promise<{ sessionId: string; cancelled: boolean; status: string }> {
    return requestJson<{ sessionId: string; cancelled: boolean; status: string }>(
      auth,
      'POST',
      `/api/gateway/session/${encodeURIComponent(sessionId)}/cancel`,
    );
  },

  verifyArtifact(auth: ApiAuthContext, sessionId: string, jobId: string): Promise<VerificationReceipt> {
    return requestJson<VerificationReceipt>(auth, 'POST', '/api/gateway/artifacts/verify', { sessionId, jobId });
  },

  listFiles(auth: ApiAuthContext, sessionId: string, path = ''): Promise<WorkspaceDirectoryListing> {
    return requestJson<WorkspaceDirectoryListing>(
      auth,
      'GET',
      `/api/gateway/session/${encodeURIComponent(sessionId)}/files?path=${encodeURIComponent(path)}`,
    );
  },

  readWorkspaceFileContent(auth: ApiAuthContext, sessionId: string, path: string): Promise<WorkspaceFileContent> {
    return requestJson<WorkspaceFileContent>(
      auth,
      'GET',
      `/api/gateway/session/${encodeURIComponent(sessionId)}/files/content?path=${encodeURIComponent(path)}`,
    );
  },

  getChanges(auth: ApiAuthContext, sessionId: string): Promise<WorkspaceChanges> {
    return requestJson<WorkspaceChanges>(auth, 'GET', `/api/gateway/session/${encodeURIComponent(sessionId)}/changes`);
  },

  cancelJob(auth: ApiAuthContext, jobId: string): Promise<{ success: boolean; noOp?: boolean }> {
    return requestJson<{ success: boolean; noOp?: boolean }>(
      auth,
      'POST',
      `/api/jobs/${encodeURIComponent(jobId)}/cancel`,
      {},
    );
  },

  retryJob(
    auth: ApiAuthContext,
    jobId: string,
  ): Promise<{ success: boolean; noOp?: boolean; failureSummary?: string | null }> {
    return requestJson<{ success: boolean; noOp?: boolean; failureSummary?: string | null }>(
      auth,
      'POST',
      `/api/jobs/${encodeURIComponent(jobId)}/retry`,
      {},
    );
  },

  resumeJob(
    auth: ApiAuthContext,
    jobId: string,
    resumeToken?: string | null,
  ): Promise<{ success: boolean; noOp?: boolean }> {
    return requestJson<{ success: boolean; noOp?: boolean }>(
      auth,
      'POST',
      `/api/jobs/${encodeURIComponent(jobId)}/resume`,
      resumeToken ? { resumeToken } : {},
    );
  },

  continueJob(
    auth: ApiAuthContext,
    jobId: string,
    prompt?: string,
    resumeToken?: string | null,
  ): Promise<{ success: boolean; mode?: string; jobId?: string; newJobId?: string | null }> {
    return requestJson<{ success: boolean; mode?: string; jobId?: string; newJobId?: string | null }>(
      auth,
      'POST',
      `/api/jobs/${encodeURIComponent(jobId)}/continue`,
      {
        ...(prompt?.trim() ? { prompt: prompt.trim() } : {}),
        ...(resumeToken ? { resumeToken } : {}),
      },
    );
  },

  listBusinessPlugins(auth: ApiAuthContext): Promise<BusinessPluginDto[]> {
    return requestJson<BusinessPluginDto[]>(auth, 'GET', '/api/plugins');
  },

  queryPluginCapabilities(
    auth: ApiAuthContext,
    pluginId: string,
    params: {
      snapshotId?: string;
      search?: string;
      groupTag?: string;
      executableOnly?: boolean;
      page?: number;
      pageSize?: number;
    } = {},
  ): Promise<CapabilityPageResponseDto> {
    const qs = new URLSearchParams();
    if (params.snapshotId) qs.set('snapshotId', params.snapshotId);
    if (params.search?.trim()) qs.set('search', params.search.trim());
    if (params.groupTag?.trim()) qs.set('groupTag', params.groupTag.trim());
    if (params.executableOnly) qs.set('executableOnly', 'true');
    if (params.page !== undefined) qs.set('page', String(params.page));
    if (params.pageSize !== undefined) qs.set('pageSize', String(params.pageSize));
    const suffix = qs.toString() ? `?${qs.toString()}` : '';
    return requestJson<CapabilityPageResponseDto>(
      auth,
      'GET',
      `/api/plugins/${encodeURIComponent(pluginId)}/capabilities${suffix}`,
    );
  },

  getPluginCapabilityDetail(
    auth: ApiAuthContext,
    pluginId: string,
    capabilityId: string,
    snapshotId?: string,
  ): Promise<CapabilityDetailDto> {
    const qs = new URLSearchParams({ capabilityId });
    if (snapshotId) qs.set('snapshotId', snapshotId);
    return requestJson<CapabilityDetailDto>(
      auth,
      'GET',
      `/api/plugins/${encodeURIComponent(pluginId)}/capabilities/detail?${qs.toString()}`,
    );
  },

  getPluginOperationStatus(auth: ApiAuthContext, operationId: string): Promise<PluginOperationStatusDto> {
    return requestJson<PluginOperationStatusDto>(
      auth,
      'GET',
      `/api/plugin-operations/${encodeURIComponent(operationId)}`,
    );
  },

  createSkillDraft(auth: ApiAuthContext, payload: CreateSkillDraftRequest): Promise<SkillDraftDto> {
    return requestJson<SkillDraftDto>(auth, 'POST', '/api/skill-drafts', payload);
  },

  getSkillDraft(auth: ApiAuthContext, draftId: string): Promise<SkillDraftDto> {
    return requestJson<SkillDraftDto>(auth, 'GET', `/api/skill-drafts/${encodeURIComponent(draftId)}`);
  },

  updateSkillDraft(
    auth: ApiAuthContext,
    draftId: string,
    payload: UpdateSkillDraftRequest,
  ): Promise<SkillDraftDto> {
    return requestJson<SkillDraftDto>(
      auth,
      'PUT',
      `/api/skill-drafts/${encodeURIComponent(draftId)}`,
      payload,
    );
  },

  validateSkillDraft(auth: ApiAuthContext, draftId: string): Promise<SkillDraftDto> {
    return requestJson<SkillDraftDto>(
      auth,
      'POST',
      `/api/skill-drafts/${encodeURIComponent(draftId)}/validate`,
      {},
    );
  },

  listPublishedSkills(auth: ApiAuthContext): Promise<PublishedSkillDto[]> {
    return requestJson<PublishedSkillDto[]>(auth, 'GET', '/api/skills');
  },

  getPublishedSkill(auth: ApiAuthContext, skillId: string): Promise<PublishedSkillDto> {
    return requestJson<PublishedSkillDto>(auth, 'GET', `/api/skills/${encodeURIComponent(skillId)}`);
  },

  getPublishedSkillMarkdown(
    auth: ApiAuthContext,
    skillId: string,
    version?: number,
  ): Promise<SkillMarkdownPreviewDto> {
    const suffix = version !== undefined ? `?version=${version}` : '';
    return requestJson<SkillMarkdownPreviewDto>(
      auth,
      'GET',
      `/api/skills/${encodeURIComponent(skillId)}/markdown${suffix}`,
    );
  },

  listSkillRuns(auth: ApiAuthContext, skillId: string): Promise<SkillRunDto[]> {
    return requestJson<SkillRunDto[]>(auth, 'GET', `/api/skills/${encodeURIComponent(skillId)}/runs`);
  },

  getSkillRun(auth: ApiAuthContext, runId: string): Promise<SkillRunDto> {
    return requestJson<SkillRunDto>(auth, 'GET', `/api/skill-runs/${encodeURIComponent(runId)}`);
  },

  getBrowserSettings(auth: ApiAuthContext): Promise<components['schemas']['BrowserSettingsDto']> {
    return requestJson<components['schemas']['BrowserSettingsDto']>(auth, 'GET', '/api/settings/browser');
  },

  getBrowserState(
    auth: ApiAuthContext,
    sessionId: string,
    signal?: AbortSignal,
  ): Promise<components['schemas']['BrowserSessionStateDto']> {
    return requestJson<components['schemas']['BrowserSessionStateDto']>(
      auth,
      'GET',
      `/api/gateway/session/${encodeURIComponent(sessionId)}/browser`,
      undefined,
      signal,
    );
  },

  async getBrowserScreenshotBlob(
    auth: ApiAuthContext,
    sessionId: string,
    signal?: AbortSignal,
  ): Promise<{ blob: Blob; seq: number } | null> {
    const response = await fetch(
      `${auth.baseUrl}/api/gateway/session/${encodeURIComponent(sessionId)}/browser/screenshot`,
      {
        method: 'GET',
        headers: {
          Authorization: `Bearer ${auth.accessToken}`,
        },
        signal,
      },
    );
    if (response.status === 204) {
      return null;
    }
    if (!response.ok) {
      throw new ApiError(response.status, 'browser_screenshot_failed', `获取浏览器截图失败 (${response.status})`);
    }
    const seqRaw = response.headers.get('X-Screenshot-Seq');
    const seq = seqRaw ? Number(seqRaw) : 0;
    const blob = await response.blob();
    return { blob, seq: Number.isFinite(seq) ? seq : 0 };
  },

  getComputerSettings(auth: ApiAuthContext): Promise<components['schemas']['ComputerSettingsDto']> {
    return requestJson<components['schemas']['ComputerSettingsDto']>(auth, 'GET', '/api/settings/computer');
  },

  getComputerState(
    auth: ApiAuthContext,
    sessionId: string,
    signal?: AbortSignal,
  ): Promise<components['schemas']['ComputerSessionStateDto']> {
    return requestJson<components['schemas']['ComputerSessionStateDto']>(
      auth,
      'GET',
      `/api/gateway/session/${encodeURIComponent(sessionId)}/computer`,
      undefined,
      signal,
    );
  },

  async getComputerScreenshotBlob(
    auth: ApiAuthContext,
    sessionId: string,
    signal?: AbortSignal,
    ifNoneMatch?: string | null,
  ): Promise<{ notModified: true } | { notModified?: false; blob: Blob; seq: number; etag: string | null } | null> {
    const headers: Record<string, string> = {
      Authorization: `Bearer ${auth.accessToken}`,
    };
    if (ifNoneMatch) {
      headers['If-None-Match'] = ifNoneMatch;
    }
    const response = await fetch(
      `${auth.baseUrl}/api/gateway/session/${encodeURIComponent(sessionId)}/computer/screenshot`,
      {
        method: 'GET',
        headers,
        signal,
      },
    );
    if (response.status === 204) {
      return null;
    }
    if (response.status === 304) {
      return { notModified: true };
    }
    if (!response.ok) {
      throw new ApiError(response.status, 'computer_screenshot_failed', `获取桌面截图失败 (${response.status})`);
    }
    const seqRaw = response.headers.get('X-Screenshot-Seq');
    const seq = seqRaw ? Number(seqRaw) : 0;
    const etag = response.headers.get('ETag');
    const blob = await response.blob();
    return { blob, seq: Number.isFinite(seq) ? seq : 0, etag };
  },
};
