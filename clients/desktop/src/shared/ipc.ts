import type { components } from './contracts/api.generated';

export type BackendMode = 'managed' | 'standalone';

export type BackendConnectionStatus =
  | 'starting'
  | 'ready'
  | 'reconnecting'
  | 'error'
  | 'disconnected';

export type ExecutionBackendKind = 'Local' | 'Docker';
export type ExecutionPermissionModeKind = 'Ask' | 'AutoInWorkspace' | 'FullAuto' | 'Deny';
export type ApprovalDecisionWire = 'allow_once' | 'allow_session' | 'allow_always' | 'deny';

/**
 * Public connection metadata exposed from main -> preload -> renderer.
 * NEVER contains `controlToken`.
 * `baseUrl` points to the main-process pinned local gateway proxy so renderer CSP stays locked to a single origin.
 */
export interface BackendConnectionPublicInfo {
  mode: BackendMode;
  status: BackendConnectionStatus;
  clientVersion?: string | null;
  baseUrl: string | null;
  targetBaseUrl: string | null;
  accessToken: string | null;
  instanceId: string | null;
  controlPlaneAvailable: boolean;
  selfApprovalProtection: boolean;
  dataDirectory: string | null;
  lastError: string | null;
  restartCount: number;
}

export interface ControlPlaneCallResult<T> {
  ok: boolean;
  status: number;
  data?: T;
  errorCode?: string;
  errorMessage?: string;
  expectedRequestDigest?: string;
}

export interface SubmitToolApprovalPayload {
  approvalId: string;
  requestDigest: string;
  decision: ApprovalDecisionWire;
  note?: string;
}

export interface SubmitToolApprovalResult {
  success: boolean;
  approvalId: string;
  decision: string;
  message: string;
}

export interface StructuredPlanActionPayload {
  action: 'approve' | 'reject' | 'execute' | 'stop' | 'planOnly';
  planArtifactId?: string;
  planVersion?: string;
  planHash?: string;
  idempotencyKey?: string;
  startExecution?: boolean;
  acceptExistingDependencyDebt?: boolean;
  approveDependencyUpgrades?: string[];
}

export interface PlanChatControlPayload {
  sessionId: string;
  message: string;
  conversationId?: string;
  conversationMode?: string;
  structuredAction: StructuredPlanActionPayload;
}

export interface PlanChatAcceptedResult {
  messageId: string;
  sessionId: string;
}

export interface ExecutionSettingsResult {
  backend: string;
  permissionMode: string;
  controlPlaneAvailable: boolean;
  selfApprovalProtection: boolean;
  tokenDeliveryMode: string;
}

export interface UpdateExecutionSettingsPayload {
  backend?: ExecutionBackendKind;
  permissionMode?: ExecutionPermissionModeKind;
}

export interface ModelSettingsResult {
  apiUrl: string | null;
  apiMode: string | null;
  model: string | null;
  maxTokensLength: number | null;
  temperature: number | null;
  topP: number | null;
  apiKeySet: boolean;
  apiKeyLast4: string | null;
}

export interface UpdateModelSettingsPayload {
  apiUrl?: string;
  apiMode?: string;
  model?: string;
  maxTokensLength?: number;
  temperature?: number;
  topP?: number;
  apiKey?: string;
}

export type PersistedPermissionRule = components['schemas']['PersistedPermissionRule'];

export interface PermissionRuleListResult {
  count: number;
  rules: PersistedPermissionRule[];
}

export interface DeletePermissionRuleResult {
  removed: boolean;
  ruleId: string;
}

export interface OpenLocalWorkspacePayload {
  path: string;
  name?: string;
}

export interface OpenLocalWorkspaceResult {
  sessionId: string;
  workspaceName: string;
  workspacePath: string;
  created: boolean;
  addedToAllowedRoots: boolean;
}

export interface StandaloneDiscoveryResult {
  discoveryHandle: string;
  baseUrl: string;
  port: number;
  pid: number;
  pidAlive: boolean;
  instanceId: string;
  dataDirectory: string;
  hasAccessToken: boolean;
  hasControlToken: boolean;
}

export interface PickedTokenFileResult {
  handle: string;
  displayName: string;
}

export interface ConnectStandalonePayload {
  baseUrl: string;
  discoveryHandle?: string;
  accessTokenHandle?: string;
  accessToken?: string;
  controlTokenHandle?: string;
}

export interface DesktopBridge {
  getBackendConnection(): Promise<BackendConnectionPublicInfo>;
  onBackendConnectionChanged(listener: (info: BackendConnectionPublicInfo) => void): () => void;
  approveToolExecution(payload: SubmitToolApprovalPayload): Promise<ControlPlaneCallResult<SubmitToolApprovalResult>>;
  submitPlanChatAction(payload: PlanChatControlPayload): Promise<ControlPlaneCallResult<PlanChatAcceptedResult>>;
  updateExecutionSettings(payload: UpdateExecutionSettingsPayload): Promise<ControlPlaneCallResult<ExecutionSettingsResult>>;
  updateModelSettings(payload: UpdateModelSettingsPayload): Promise<ControlPlaneCallResult<ModelSettingsResult>>;
  listPermissionRules(): Promise<ControlPlaneCallResult<PermissionRuleListResult>>;
  deletePermissionRule(ruleId: string): Promise<ControlPlaneCallResult<DeletePermissionRuleResult>>;
  openLocalWorkspace(payload: OpenLocalWorkspacePayload): Promise<ControlPlaneCallResult<OpenLocalWorkspaceResult>>;
  pickFolder(): Promise<string | null>;
  pickStandaloneTokenFile(kind: 'access' | 'control'): Promise<PickedTokenFileResult | null>;
  discoverStandaloneInstance(dataDirectory?: string): Promise<StandaloneDiscoveryResult | null>;
  connectStandalone(payload: ConnectStandalonePayload): Promise<BackendConnectionPublicInfo>;
  switchToManaged(): Promise<BackendConnectionPublicInfo>;
  restartBackend(): Promise<BackendConnectionPublicInfo>;
  openExternal(url: string): Promise<boolean>;
  showNotification(payload: { title: string; body: string }): Promise<void>;
}

export const IPC_CHANNELS = {
  getBackendConnection: 'codexflow:get-backend-connection',
  backendConnectionChanged: 'codexflow:backend-connection-changed',
  approveToolExecution: 'codexflow:approve-tool-execution',
  submitPlanChatAction: 'codexflow:submit-plan-chat-action',
  updateExecutionSettings: 'codexflow:update-execution-settings',
  updateModelSettings: 'codexflow:update-model-settings',
  listPermissionRules: 'codexflow:list-permission-rules',
  deletePermissionRule: 'codexflow:delete-permission-rule',
  openLocalWorkspace: 'codexflow:open-local-workspace',
  pickFolder: 'codexflow:pick-folder',
  pickStandaloneTokenFile: 'codexflow:pick-standalone-token-file',
  discoverStandaloneInstance: 'codexflow:discover-standalone-instance',
  connectStandalone: 'codexflow:connect-standalone',
  switchToManaged: 'codexflow:switch-to-managed',
  restartBackend: 'codexflow:restart-backend',
  openExternal: 'codexflow:open-external',
  showNotification: 'codexflow:show-notification',
} as const;
