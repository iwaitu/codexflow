import type { PendingApprovalItem, SessionPlan, SessionSnapshot } from './apiClient';
import type { SseToolApprovalRequestPayload } from '@shared/contracts/sse.generated';

export interface UiMessage {
  id: string;
  role: 'user' | 'assistant' | 'system';
  content: string;
  thinkingContent?: string | null;
  messageType?: string | null;
  notificationMeta?: string | null;
  truncated?: boolean;
  streaming?: boolean;
  createdUtc?: string;
}

export interface UiToolCall {
  key: string;
  callId: string | null;
  name: string;
  arguments?: Record<string, unknown> | null;
  argumentsTruncated?: boolean;
  status: 'called' | 'running' | 'completed' | 'failed';
  outputStdout: string;
  outputStderr: string;
  outputTruncated: boolean;
  suppressedLines: number;
}

export interface UiPendingApproval {
  approvalId: string;
  requestDigest: string;
  sessionId: string;
  tool: string;
  executable: string;
  interpreterChain: string[];
  argv: string[];
  cwd: string;
  stdinSha256?: string | null;
  codeSha256?: string | null;
  scriptSha256?: string | null;
  backend: string;
  network: string;
  filesystem: string;
  reason: string;
  expiresAtUtc: string;
  allowedDecisions: string[];
  executesWorkspaceCode: boolean;
  trustedOriginVerified: boolean;
  selfApprovalProtection?: boolean;
  origin?: string | null;
  policyVersion?: number | null;
  wrapperSha256?: string | null;
  exportScopeSha256?: string | null;
  timeoutSeconds?: number | null;
  imageDigest?: string | null;
  shellKind?: string | null;
}

export interface WorkerBarState {
  status: 'idle' | 'started' | 'waiting_user' | 'completed' | 'failed';
  message: string | null;
}

export function normalizeApprovalFromRest(item: PendingApprovalItem): UiPendingApproval {
  const extra = item as PendingApprovalItem & {
    policyVersion?: number | null;
    wrapperSha256?: string | null;
    exportScopeSha256?: string | null;
    timeoutSeconds?: number | null;
    imageDigest?: string | null;
    shellKind?: string | null;
  };
  return {
    approvalId: item.approvalId,
    requestDigest: item.requestDigest,
    sessionId: item.sessionId,
    tool: item.tool,
    executable: item.executable,
    interpreterChain: [...(item.interpreterChain ?? [])],
    argv: [...(item.argv ?? [])],
    cwd: item.cwd,
    stdinSha256: item.stdinSha256 ?? null,
    codeSha256: item.codeSha256 ?? null,
    scriptSha256: item.scriptSha256 ?? null,
    backend: item.backend,
    network: item.network,
    filesystem: item.filesystem,
    reason: item.reason,
    expiresAtUtc: item.expiresAtUtc,
    allowedDecisions: [...(item.allowedDecisions ?? [])],
    executesWorkspaceCode: item.executesWorkspaceCode,
    trustedOriginVerified: Boolean((item as { trustedOriginVerified?: boolean }).trustedOriginVerified ?? false),
    origin: item.origin ?? null,
    policyVersion: extra.policyVersion ?? null,
    wrapperSha256: extra.wrapperSha256 ?? null,
    exportScopeSha256: extra.exportScopeSha256 ?? null,
    timeoutSeconds: extra.timeoutSeconds ?? null,
    imageDigest: extra.imageDigest ?? null,
    shellKind: item.shell ?? extra.shellKind ?? null,
  };
}

export function normalizeApprovalFromSse(payload: SseToolApprovalRequestPayload): UiPendingApproval {
  return {
    approvalId: payload.approvalId,
    requestDigest: payload.requestDigest,
    sessionId: payload.sessionId,
    tool: payload.tool,
    executable: payload.executable,
    interpreterChain: [...(payload.interpreterChain ?? [])],
    argv: [...(payload.argv ?? [])],
    cwd: payload.cwd,
    stdinSha256: payload.stdinSha256 ?? null,
    codeSha256: payload.codeSha256 ?? null,
    scriptSha256: payload.scriptSha256 ?? null,
    backend: payload.backend,
    network: payload.network,
    filesystem: payload.filesystem,
    reason: payload.reason,
    expiresAtUtc: payload.expiresAt,
    allowedDecisions: [...(payload.allowedDecisions ?? [])],
    executesWorkspaceCode: payload.executesWorkspaceCode,
    trustedOriginVerified: payload.trustedOriginVerified,
    selfApprovalProtection: payload.selfApprovalProtection,
    origin: payload.origin ?? null,
    policyVersion: payload.policyVersion ?? null,
    wrapperSha256: payload.wrapperSha256 ?? null,
    exportScopeSha256: payload.exportScopeSha256 ?? null,
    timeoutSeconds: payload.timeoutSeconds ?? null,
    imageDigest: payload.imageDigest ?? null,
    shellKind: payload.shellKind ?? null,
  };
}

type ToolResult = NonNullable<SessionSnapshot['activeToolResults']>[number];
type ToolOutput = NonNullable<SessionSnapshot['activeToolOutputs']>[number];

function recoverToolCalls(results: ToolResult[], outputs: ToolOutput[]): UiToolCall[] {
  const toolCalls: UiToolCall[] = [];
  for (const r of results) {
    toolCalls.push({
      key: r.callId || `snap-result-${toolCalls.length}`,
      callId: r.callId ?? null,
      name: r.toolName,
      status: r.success ? 'completed' : 'failed',
      outputStdout: '',
      outputStderr: '',
      outputTruncated: r.truncated,
      suppressedLines: 0,
    });
  }

  for (const o of outputs) {
    const taskId = o.commandTaskId ?? null;
    const byId = taskId ? toolCalls.find((t) => t.callId === taskId) : undefined;
    const runningSameName = !byId
      ? toolCalls.filter((t) => t.name === o.tool && t.status === 'running')
      : [];
    const existing = byId ?? (runningSameName.length === 1 ? runningSameName[0] : undefined);

    if (existing) {
      existing.outputStdout += o.stdout ?? '';
      existing.outputStderr += o.stderr ?? '';
      existing.outputTruncated = existing.outputTruncated || o.throttled;
      existing.suppressedLines += Number(o.suppressedLines ?? 0);
    } else {
      toolCalls.push({
        key: taskId || `snap-out-${toolCalls.length}`,
        callId: taskId,
        name: o.tool,
        status: 'running',
        outputStdout: o.stdout ?? '',
        outputStderr: o.stderr ?? '',
        outputTruncated: o.throttled,
        suppressedLines: Number(o.suppressedLines ?? 0),
      });
    }
  }

  return toolCalls;
}

export function toChatMessages(messages: readonly (Omit<UiMessage, 'role'> & { role: string })[]): UiMessage[] {
  return messages
    .filter((message) => message.role !== 'assistant' || Boolean(message.content || message.thinkingContent))
    .map((message) => ({
      id: message.id,
      role: message.role === 'user' || message.role === 'assistant' ? message.role : 'system',
      content: message.content,
      thinkingContent: message.thinkingContent ?? null,
      messageType: message.messageType ?? null,
      notificationMeta: message.notificationMeta ?? null,
      truncated: message.truncated,
      createdUtc: message.createdUtc,
    }));
}

export function buildRecoveredSessionState(snapshot: SessionSnapshot): {
  asOfEventId: number;
  channelEpoch: string | null;
  activeConversationId: string | null;
  isTurnRunning: boolean;
  activeMessageId: string | null;
  streamingContent: string;
  streamingThinking: string;
  streamingTruncated: boolean;
  messagesTruncated: boolean;
  messages: UiMessage[];
  pendingApprovals: UiPendingApproval[];
  currentPlan: SessionPlan | null;
  toolCalls: UiToolCall[];
} {
  const messages = toChatMessages(snapshot.messages ?? []);

  const isTurnRunning = Boolean(snapshot.activeTurn?.isRunning);
  return {
    asOfEventId: Number(snapshot.asOfEventId ?? 0),
    channelEpoch: snapshot.channelEpoch ?? null,
    activeConversationId: snapshot.activeConversationId ?? null,
    isTurnRunning,
    activeMessageId: snapshot.activeTurn?.messageId ?? null,
    streamingContent: snapshot.activeTurn?.partialContent ?? '',
    streamingThinking: snapshot.activeTurn?.partialThinking ?? '',
    streamingTruncated: Boolean(snapshot.activeTurn?.contentTruncated),
    messagesTruncated: messages.some((m) => m.truncated),
    messages,
    pendingApprovals: (snapshot.pendingApprovals ?? []).map(normalizeApprovalFromRest),
    currentPlan: snapshot.currentPlan ?? null,
    toolCalls: isTurnRunning ? recoverToolCalls(snapshot.activeToolResults ?? [], snapshot.activeToolOutputs ?? []) : [],
  };
}
