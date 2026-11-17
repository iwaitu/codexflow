import type { components } from '@shared/contracts/api.generated';

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

export interface SessionBootstrapResponse {
  sessionId: string;
  workspaceName: string;
  created: boolean;
  workspacePath: string;
  isDefault: boolean;
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
  method: 'GET' | 'POST' | 'PATCH' | 'DELETE',
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

  listSessions(auth: ApiAuthContext, page = 1, pageSize = 50, includeArchived = false): Promise<PagedSessions> {
    return requestJson<PagedSessions>(
      auth,
      'GET',
      `/api/gateway/sessions?page=${page}&pageSize=${pageSize}&includeArchived=${includeArchived ? 'true' : 'false'}`,
    );
  },

  createSession(auth: ApiAuthContext, name: string): Promise<{ sessionId: string }> {
    return requestJson<{ sessionId: string }>(auth, 'POST', `/api/gateway/session?name=${encodeURIComponent(name)}`);
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

  getContextWindow(auth: ApiAuthContext, sessionId: string, signal?: AbortSignal): Promise<SessionContextWindow> {
    return requestJson<SessionContextWindow>(
      auth,
      'GET',
      `/api/gateway/session/${encodeURIComponent(sessionId)}/context-window`,
      undefined,
      signal,
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
};
