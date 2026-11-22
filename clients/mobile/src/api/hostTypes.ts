import type {
  SignedApprovalEnvelopeInput,
  SignedPlanEnvelopeInput,
} from '@/crypto/p256Verify';

export interface WorkspaceConversationPreview {
  readonly id: string;
  readonly title: string;
  readonly msgCount: number;
}

export interface WorkspaceSummary {
  readonly sessionId: string;
  readonly projectName: string;
  readonly currentStage: number;
  readonly lastActive: string;
  readonly workspacePath: string;
  readonly languageTags?: readonly string[];
  readonly recentConversations?: readonly WorkspaceConversationPreview[];
}

export interface SessionSummary {
  readonly sessionId: string;
  readonly name: string;
  readonly workMode?: string;
  readonly modeVersion?: number;
  readonly planModeActive?: boolean;
  readonly planModeVersion?: number;
  readonly currentPlanArtifactId?: string | null;
  readonly isExternalWorkspace?: boolean;
  readonly isArchived?: boolean;
  readonly createdUtc?: string;
  readonly updatedAtUtc?: string;
}

export interface PagedSessionsResponse {
  readonly items: readonly SessionSummary[];
  readonly total: number;
  readonly page: number;
  readonly pageSize: number;
}

export interface ConversationSummary {
  readonly conversationId: string;
  readonly name: string;
  readonly sessionId?: string | null;
  readonly taskId?: string | null;
  readonly threadType?: string;
  readonly isArchived?: boolean;
  readonly createdUtc?: string;
  readonly updatedAtUtc?: string;
}

export interface PagedConversationsResponse {
  readonly items: readonly ConversationSummary[];
  readonly total: number;
  readonly page: number;
  readonly pageSize: number;
}

export interface HistoryMessage {
  readonly id: string;
  readonly role: 'user' | 'assistant' | 'system';
  readonly content: string;
  readonly thinkingContent?: string | null;
  readonly messageType?: string | null;
  readonly createdUtc?: string;
}

export interface SessionBootstrapResponse {
  readonly sessionId: string;
  readonly workspaceName?: string;
  readonly created?: boolean;
  readonly workMode?: string;
  readonly modeVersion?: number;
  readonly planModeActive?: boolean;
  readonly planModeVersion?: number;
}

export interface PlanTaskItemDto {
  readonly id: string;
  readonly title: string;
  readonly status: string;
}

export interface SessionPlanDto {
  readonly planArtifactId: string;
  readonly version: string;
  readonly markdownHash: string;
  readonly status: string;
  readonly goal: string;
  readonly markdown: string;
  readonly hostRefusal?: string | null;
  readonly updatedAtUtc?: string;
  readonly tasks?: readonly PlanTaskItemDto[];
  readonly signedEnvelope?: SignedPlanEnvelopeInput | null;
  readonly signedEnvelopeApproveOnly?: SignedPlanEnvelopeInput | null;
}

export interface PendingCommandApprovalItem {
  readonly approvalId: string;
  readonly requestDigest: string;
  readonly sessionId: string;
  readonly tool: string;
  readonly executable: string;
  readonly argv: readonly string[];
  readonly cwd: string;
  readonly reason?: string | null;
  readonly scriptPreview?: string | null;
  readonly expiresAtUtc: string;
  readonly allowedDecisions: readonly string[];
  readonly signedEnvelope?: SignedApprovalEnvelopeInput | null;
}

export interface PendingBusinessApprovalItem {
  readonly approvalId: string;
  readonly requestDigest: string;
  readonly sessionId: string;
  readonly kind: string;
  readonly summary?: { readonly title?: string; readonly summary?: string } | null;
  readonly expiresAtUtc: string;
  readonly signedEnvelope?: SignedApprovalEnvelopeInput | null;
}

export interface PendingBrowserApprovalItem {
  readonly approvalId: string;
  readonly requestDigest: string;
  readonly sessionId: string;
  readonly tool: string;
  readonly action: string;
  readonly targetUrl: string;
  readonly targetDescription: string;
  readonly riskReason: string;
  readonly expiresAtUtc: string;
  readonly signedEnvelope?: SignedApprovalEnvelopeInput | null;
}

export interface PendingComputerApprovalItem {
  readonly approvalId: string;
  readonly requestDigest: string;
  readonly sessionId: string;
  readonly tool: string;
  readonly action: string;
  readonly targetProcessName: string;
  readonly targetWindowTitle: string;
  readonly actionSummary: string;
  readonly riskReason: string;
  readonly expiresAtUtc: string;
  readonly signedEnvelope?: SignedApprovalEnvelopeInput | null;
}

export interface PendingApprovalsResponse {
  readonly sessionId: string;
  readonly count: number;
  readonly items: readonly PendingCommandApprovalItem[];
  readonly controlPlaneAvailable: boolean;
  readonly selfApprovalProtection: boolean;
  readonly businessApprovals?: readonly PendingBusinessApprovalItem[];
  readonly browserApprovals?: readonly PendingBrowserApprovalItem[];
  readonly computerApprovals?: readonly PendingComputerApprovalItem[];
}

export interface PendingApprovalScriptSnapshot {
  readonly approvalId: string;
  readonly sessionId: string;
  readonly requestDigest: string;
  readonly scriptSha256: string;
  readonly scriptText: string;
  readonly scriptLength: number;
  readonly shell?: string | null;
  readonly expiresAtUtc: string;
  readonly signedEnvelope?: SignedApprovalEnvelopeInput | null;
}

export interface RemotePendingTodoItem {
  readonly eventId: number;
  readonly sourceKind: string;
  readonly opaqueRefId: string;
  readonly sessionId: string;
  readonly title: string;
  readonly summary: string;
  readonly status: string;
  readonly isTerminal: boolean;
  readonly readOnlyInRemote: boolean;
  readonly createdAtUtc: string;
  readonly expiresAtUtc: string;
  readonly resolvedReason?: string | null;
  readonly approvalId?: string | null;
  readonly requestDigest?: string | null;
  readonly signedEnvelope?: SignedApprovalEnvelopeInput | null;
}

export interface RemotePendingSummarySnapshotResponse {
  readonly mode: string;
  readonly snapshotId?: string | null;
  readonly summaryEpoch: number;
  readonly resumeAfterEventId: number;
  readonly nextPageToken?: string | null;
  readonly snapshotComplete: boolean;
  readonly resetRequired: boolean;
  readonly bindingVersion?: number | null;
  readonly items: readonly RemotePendingTodoItem[];
}

export interface UploadedAttachmentDto {
  readonly name: string;
  readonly relativePath: string;
  readonly sizeBytes: number;
  readonly contentType: string;
}

export interface RemoteAttachmentUploadStatusResponse {
  readonly status: string;
  readonly uploadId: string;
  readonly fileSha256?: string | null;
  readonly attachment?: UploadedAttachmentDto | null;
}

export interface GatewayChatAcceptedResponse {
  readonly messageId: string;
  readonly sessionId: string;
}

export interface RemoteChatOpStatusResponse {
  readonly status: string;
  readonly clientOpId: string;
  readonly conversationId?: string | null;
  readonly messageId?: string | null;
  readonly acceptedResponse?: GatewayChatAcceptedResponse | null;
}

export interface BrowserSessionStateDto {
  readonly sessionId: string;
  readonly status: string;
  readonly headless: boolean;
  readonly screenshotSeq: number;
  readonly takeoverActive: boolean;
  readonly tabs: readonly { readonly id?: string; readonly title?: string; readonly url?: string; readonly active?: boolean }[];
}

export interface ComputerSessionStateDto {
  readonly enabled: boolean;
  readonly active: boolean;
  readonly emergencyStopped: boolean;
  readonly lastActionSummary?: string | null;
  readonly pinnedProcessName?: string | null;
}

export interface ExecutionSettingsDto {
  readonly backend: string;
  readonly permissionMode: string;
  readonly controlPlaneAvailable: boolean;
}
