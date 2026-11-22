/* eslint-disable */
// AUTO-GENERATED from docs/contracts/sse-events.schema.json by scripts/generate-contracts.mjs
// Do not edit manually.

export const SSE_SCHEMA_VERSION = "1.3.0" as const;

export const SSE_KNOWN_EVENTS = [
  "heartbeat",
  "content",
  "thinking_start",
  "thinking_content",
  "thinking_end",
  "tool_call",
  "tool_started",
  "tool_output",
  "tool_approval_request",
  "tool_approval_resolved",
  "task_status",
  "setconversationid",
  "updateconversationtitle",
  "system_message",
  "next_task_started",
  "retry_notice",
  "plan_change_request",
  "plan_approval_requested",
  "planning_summary",
  "worker_started",
  "worker_waiting_user",
  "worker_completed",
  "worker_failed",
  "file_tree_updated",
  "error",
  "done",
  "replay_gap",
  "business_skill_run_updated",
  "business_skill_approval_requested",
  "business_skill_approval_resolved",
  "plan_mode_changed",
  "plan_mode_exit_requested",
  "browser_state_updated",
  "browser_approval_requested",
  "browser_approval_resolved",
  "computer_state_updated",
  "computer_approval_requested",
  "computer_approval_resolved",
] as const;

export type KnownSseEventName = (typeof SSE_KNOWN_EVENTS)[number];

export interface SseHeartbeatPayload {
  sessionId: string;
  status: "connected" | "alive";
  subscriberCount: number;
  epoch?: string | null;
  intervalSeconds?: number | null;
  pendingApprovals: number;
  [key: string]: unknown;
}

export interface SseContentPayload {
  type: "content";
  content: string;
  text: string;
  [key: string]: unknown;
}

export interface SseThinkingStartPayload {
  type: "thinking_start";
  [key: string]: unknown;
}

export interface SseThinkingContentPayload {
  type: "thinking_content";
  content: string;
  text: string;
  [key: string]: unknown;
}

export interface SseThinkingEndPayload {
  type: "thinking_end";
  thinking_content: string;
  [key: string]: unknown;
}

export interface SseToolCallPayload {
  type: "tool_call";
  name: string;
  callId?: string | null;
  arguments?: Record<string, unknown> | null;
  argumentsTruncated?: boolean;
  [key: string]: unknown;
}

export interface SseToolStartedPayload {
  type: "tool_started";
  name: string;
  callId?: string | null;
  [key: string]: unknown;
}

export interface SseToolOutputPayload {
  tool: string;
  commandTaskId: string;
  stdout?: string | null;
  stderr?: string | null;
  throttled: boolean;
  suppressedLines?: number | null;
  [key: string]: unknown;
}

export interface SseToolApprovalRequestPayload {
  approvalId: string;
  requestDigest: string;
  sessionId: string;
  tool: string;
  executable: string;
  interpreterChain: Array<string>;
  argv: Array<string>;
  cwd: string;
  stdinSha256?: string | null;
  codeSha256?: string | null;
  scriptSha256?: string | null;
  backend: string;
  network: string;
  filesystem: string;
  reason: string;
  expiresAt: string;
  allowedDecisions: Array<string>;
  executesWorkspaceCode: boolean;
  trustedOriginVerified: boolean;
  selfApprovalProtection: boolean;
  origin?: string | null;
  policyVersion?: number | null;
  wrapperSha256?: string | null;
  exportScopeSha256?: string | null;
  timeoutSeconds?: number | null;
  imageDigest?: string | null;
  shellKind?: string | null;
  shell?: string | null;
  scriptPreview?: string | null;
  scriptLength?: number | null;
  backendNotice?: string | null;
  [key: string]: unknown;
}

export interface SseToolApprovalResolvedPayload {
  approvalId: string;
  requestDigest: string;
  sessionId: string;
  decision: string;
  decidedBy?: string | null;
  note?: string | null;
  resolvedAt: string;
  [key: string]: unknown;
}

export interface SseTaskStatusPayload {
  type: string;
  name?: string | null;
  callId?: string | null;
  success?: boolean | null;
  result?: string | null;
  [key: string]: unknown;
}

export interface SseSetconversationidPayload {
  conversationId: string;
  [key: string]: unknown;
}

export interface SseUpdateconversationtitlePayload {
  conversationId: string;
  title: string;
  [key: string]: unknown;
}

export interface SseSystemMessagePayload {
  type?: string | null;
  noticeType?: string | null;
  content: string;
  text?: string | null;
  [key: string]: unknown;
}

export interface SseNextTaskStartedPayload {
  taskId: string;
  title: string;
  jobId: string;
  jobType: string;
  workerType?: string | null;
  status?: string | null;
  [key: string]: unknown;
}

export interface SseRetryNoticePayload {
  taskId: string;
  retryCount: number;
  maxRetries: number;
  reason?: string | null;
  [key: string]: unknown;
}

export interface SsePlanChangeRequestPayload {
  taskId: string;
  failureCount: number;
  maxRetries: number;
  canRetry: boolean;
  lastError?: string | null;
  [key: string]: unknown;
}

export interface SsePlanApprovalRequestedPayload {
  type: "plan_approval_requested";
  planArtifactId: string;
  version: string;
  markdownHash: string;
  goal?: string | null;
  status: string;
  taskCount: number;
  [key: string]: unknown;
}

export interface SsePlanningSummaryPayload {
  summary?: string | null;
  content?: string | null;
  [key: string]: unknown;
}

export interface SseWorkerStartedPayload {
  type: "worker_started";
  content?: string | null;
  text?: string | null;
  [key: string]: unknown;
}

export interface SseWorkerWaitingUserPayload {
  type: "worker_waiting_user";
  content?: string | null;
  text?: string | null;
  [key: string]: unknown;
}

export interface SseWorkerCompletedPayload {
  type: "worker_completed";
  content?: string | null;
  text?: string | null;
  [key: string]: unknown;
}

export interface SseWorkerFailedPayload {
  type: "worker_failed";
  content?: string | null;
  text?: string | null;
  [key: string]: unknown;
}

export interface SseFileTreeUpdatedPayload {
  tool: string;
  [key: string]: unknown;
}

export interface SseErrorPayload {
  messageId?: string | null;
  error: string;
  message?: string | null;
  detail?: string | null;
  [key: string]: unknown;
}

export interface SseDonePayload {
  type?: string | null;
  messageId?: string | null;
  source?: string | null;
  [key: string]: unknown;
}

export interface SseReplayGapPayload {
  reason: "buffer_evicted" | "subscriber_overflow" | "sequence_reset";
  epoch?: string | null;
  fromSeq?: number | null;
  toSeq?: number | null;
  previousLastEventId?: number | null;
  oldestAvailableSeq?: number | null;
  [key: string]: unknown;
}

export interface SseBusinessSkillRunUpdatedPayload {
  runId: string;
  skillId: string;
  skillVersion: number;
  status: string;
  stopRequested: boolean;
  updatedAtUtc: string;
  [key: string]: unknown;
}

export interface SseBusinessSkillApprovalRequestedPayload {
  approvalId: string;
  requestDigest: string;
  sessionId: string;
  runId: string;
  stepId: string;
  kind: string;
  allowedDecisions: Array<string>;
  expiresAtUtc: string;
  [key: string]: unknown;
}

export interface SseBusinessSkillApprovalResolvedPayload {
  approvalId: string;
  requestDigest: string;
  runId: string;
  decision: string;
  decidedBy?: string | null;
  resolvedAt: string;
  [key: string]: unknown;
}

export interface SsePlanModeChangedPayload {
  type?: "plan_mode_changed";
  sessionId: string;
  workMode?: string | null;
  modeVersion?: number | null;
  planModeActive: boolean;
  planModeVersion: number;
  changedBy?: string | null;
  changedAtUtc?: string | null;
  reason?: string | null;
  objective?: string | null;
  [key: string]: unknown;
}

export interface SsePlanModeExitRequestedPayload {
  type?: "plan_mode_exit_requested";
  sessionId: string;
  planModeVersion: number;
  planSummary: string;
  reason?: string | null;
  [key: string]: unknown;
}

export interface SseBrowserStateUpdatedPayload {
  sessionId: string;
  status: string;
  headless: boolean;
  profileMode: string;
  url?: string | null;
  title?: string | null;
  screenshotSeq: number;
  consoleErrorCount: number;
  updatedAtUtc?: string | null;
  takeoverActive?: boolean;
  takeoverReason?: string | null;
  takeoverStartedAtUtc?: string | null;
  isRemote?: boolean | null;
  panelControlAvailable?: boolean | null;
  panelControlActive?: boolean | null;
  [key: string]: unknown;
}

export interface SseBrowserApprovalRequestedPayload {
  approvalId: string;
  requestDigest: string;
  sessionId: string;
  tool: string;
  action: string;
  targetUrl: string;
  targetOrigin: string;
  targetDescription: string;
  riskReason: string;
  expressionOrPath?: string | null;
  expiresAtUtc: string;
  allowedDecisions: Array<string>;
  controlPlaneAvailable: boolean;
  selfApprovalProtection: boolean;
  [key: string]: unknown;
}

export interface SseBrowserApprovalResolvedPayload {
  approvalId: string;
  sessionId: string;
  decision: string;
  decidedBy?: string | null;
  note?: string | null;
  resolvedAtUtc: string;
  [key: string]: unknown;
}

export interface SseComputerStateUpdatedPayload {
  sessionId: string;
  status: string;
  active: boolean;
  enabled: boolean;
  pinnedWindowHandle?: string | null;
  pinnedProcessName?: string | null;
  pinnedWindowTitle?: string | null;
  screenshotSeq: number;
  imageWidth?: number | null;
  imageHeight?: number | null;
  coordinateMode: string;
  lastActionSummary?: string | null;
  takeoverPending?: boolean;
  takeoverReason?: string | null;
  takeoverStartedAtUtc?: string | null;
  emergencyStopped?: boolean;
  hostWindowAvoidanceRequested?: boolean;
  updatedAtUtc?: string | null;
  [key: string]: unknown;
}

export interface SseComputerApprovalRequestedPayload {
  approvalId: string;
  requestDigest: string;
  sessionId: string;
  tool: string;
  action: string;
  targetProcessName: string;
  targetWindowTitle: string;
  targetWindowHandle: string;
  actionSummary: string;
  riskReason: string;
  targetX?: number | null;
  targetY?: number | null;
  expiresAtUtc: string;
  allowedDecisions: Array<string>;
  controlPlaneAvailable: boolean;
  selfApprovalProtection: boolean;
  previewImageBase64?: string | null;
  [key: string]: unknown;
}

export interface SseComputerApprovalResolvedPayload {
  approvalId: string;
  sessionId: string;
  decision: string;
  decidedBy?: string | null;
  note?: string | null;
  resolvedAtUtc: string;
  [key: string]: unknown;
}

export interface SseEventPayloadMap {
  "heartbeat": SseHeartbeatPayload;
  "content": SseContentPayload;
  "thinking_start": SseThinkingStartPayload;
  "thinking_content": SseThinkingContentPayload;
  "thinking_end": SseThinkingEndPayload;
  "tool_call": SseToolCallPayload;
  "tool_started": SseToolStartedPayload;
  "tool_output": SseToolOutputPayload;
  "tool_approval_request": SseToolApprovalRequestPayload;
  "tool_approval_resolved": SseToolApprovalResolvedPayload;
  "task_status": SseTaskStatusPayload;
  "setconversationid": SseSetconversationidPayload;
  "updateconversationtitle": SseUpdateconversationtitlePayload;
  "system_message": SseSystemMessagePayload;
  "next_task_started": SseNextTaskStartedPayload;
  "retry_notice": SseRetryNoticePayload;
  "plan_change_request": SsePlanChangeRequestPayload;
  "plan_approval_requested": SsePlanApprovalRequestedPayload;
  "planning_summary": SsePlanningSummaryPayload;
  "worker_started": SseWorkerStartedPayload;
  "worker_waiting_user": SseWorkerWaitingUserPayload;
  "worker_completed": SseWorkerCompletedPayload;
  "worker_failed": SseWorkerFailedPayload;
  "file_tree_updated": SseFileTreeUpdatedPayload;
  "error": SseErrorPayload;
  "done": SseDonePayload;
  "replay_gap": SseReplayGapPayload;
  "business_skill_run_updated": SseBusinessSkillRunUpdatedPayload;
  "business_skill_approval_requested": SseBusinessSkillApprovalRequestedPayload;
  "business_skill_approval_resolved": SseBusinessSkillApprovalResolvedPayload;
  "plan_mode_changed": SsePlanModeChangedPayload;
  "plan_mode_exit_requested": SsePlanModeExitRequestedPayload;
  "browser_state_updated": SseBrowserStateUpdatedPayload;
  "browser_approval_requested": SseBrowserApprovalRequestedPayload;
  "browser_approval_resolved": SseBrowserApprovalResolvedPayload;
  "computer_state_updated": SseComputerStateUpdatedPayload;
  "computer_approval_requested": SseComputerApprovalRequestedPayload;
  "computer_approval_resolved": SseComputerApprovalResolvedPayload;
}

export type KnownSseEvent = {
  [K in keyof SseEventPayloadMap]: {
    id?: number;
    event: K;
    data: SseEventPayloadMap[K];
  };
}[keyof SseEventPayloadMap];

export interface UnknownSseEvent {
  id?: number;
  event: string;
  data: Record<string, unknown>;
}

export type ParsedSseEvent = KnownSseEvent | UnknownSseEvent;
