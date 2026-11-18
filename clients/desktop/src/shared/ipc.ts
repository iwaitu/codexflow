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

export type PendingApprovalScriptSnapshot = components['schemas']['PendingApprovalScriptSnapshot'];
export type PlanModeResult = components['schemas']['PlanModeResponse'];

export interface SetPlanModePayload {
  sessionId: string;
  active: boolean;
  expectedPlanModeVersion?: number;
  reason?: string;
  objective?: string;
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
  supportsMultimodal?: boolean;
  multimodalProbeStatus?: string | null;
  multimodalProbeMessage?: string | null;
}

export interface UpdateModelSettingsPayload {
  apiUrl?: string;
  apiMode?: string;
  model?: string;
  maxTokensLength?: number;
  temperature?: number;
  topP?: number;
  apiKey?: string;
  supportsMultimodal?: boolean;
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

export interface DroppedFileClassifyInput {
  filePaths: string[];
  workspacePath?: string | null;
}

export interface ClassifiedDroppedFile {
  filePath: string;
  exists: boolean;
  isDirectory: boolean;
  sizeBytes: number;
  workspaceRelativePath: string | null;
}

export type BusinessPluginDto = components['schemas']['BusinessPluginDto'];
export type BusinessPluginSourceType = components['schemas']['BusinessPluginSourceType'];
export type BusinessAuthKind = components['schemas']['BusinessAuthKind'];
export type CapabilityRiskLevel = components['schemas']['CapabilityRiskLevel'];
export type CapabilitySummaryDto = components['schemas']['CapabilitySummaryDto'];
export type CapabilityDetailDto = components['schemas']['CapabilityDetailDto'];
export type CapabilityPageResponseDto = components['schemas']['CapabilityPageResponseDto'];
export type CredentialRefSummaryDto = components['schemas']['CredentialRefSummaryDto'];
export type DeleteCredentialResponseDto = components['schemas']['DeleteCredentialResponseDto'];
export type DiscoverPluginResultDto = components['schemas']['DiscoverPluginResultDto'];
export type CreateBusinessPluginRequest = components['schemas']['CreateBusinessPluginRequest'];
export type UpdatePluginConnectionRequest = components['schemas']['UpdatePluginConnectionRequest'];
export type SetPluginCredentialRequest = components['schemas']['SetPluginCredentialRequest'];
export type UpdateCapabilityRiskRequest = components['schemas']['UpdateCapabilityRiskRequest'];
export type SkillDraftDto = components['schemas']['SkillDraftDto'];
export type CreateSkillDraftRequest = components['schemas']['CreateSkillDraftRequest'];
export type UpdateSkillDraftRequest = components['schemas']['UpdateSkillDraftRequest'];
export type PutDraftSelectionRequest = components['schemas']['PutDraftSelectionRequest'];
export type GenerateSkillCandidateRequest = components['schemas']['GenerateSkillCandidateRequest'];
export type GenerateSkillCandidateResultDto = components['schemas']['GenerateSkillCandidateResultDto'];
export type PublishSkillDraftRequest = components['schemas']['PublishSkillDraftRequest'];
export type PublishedSkillDto = components['schemas']['PublishedSkillDto'];
export type SkillMarkdownPreviewDto = components['schemas']['SkillMarkdownPreviewDto'];
export type StartSkillRunRequest = components['schemas']['StartSkillRunRequest'];
export type SkillRunDto = components['schemas']['SkillRunDto'];
export type DisablePluginResponseDto = components['schemas']['DisablePluginResponseDto'];
export type BusinessSkillRetentionPruneRequest =
  components['schemas']['BusinessSkillRetentionPruneRequest'];
export type BusinessSkillRetentionPruneResultDto =
  components['schemas']['BusinessSkillRetentionPruneResultDto'];
export type PluginOperationStatusDto = components['schemas']['PluginOperationStatusDto'];
export type EndpointPolicyDto = components['schemas']['EndpointPolicyDto'];
export type SkillDefinitionV1 = components['schemas']['SkillDefinitionV1'];
export type SkillInputPropertyDto = components['schemas']['SkillInputPropertyDto'];
export type SkillInputSchemaDto = components['schemas']['SkillInputSchemaDto'];
export type SkillStepDefinition = components['schemas']['SkillStepDefinition'];
export type SkillRunStatus = components['schemas']['SkillRunStatus'];
export type SkillRunStepState = components['schemas']['SkillRunStepState'];
export type BusinessApprovalRequestDto = components['schemas']['BusinessApprovalRequestDto'];

export type CredentialSlotKind = 'document' | 'execution';

export type DesktopTheme = 'dark' | 'light';
export type DesktopLanguage = 'zh-CN' | 'en-US';

export interface DesktopPreferencesPayload {
  theme?: DesktopTheme;
  language?: DesktopLanguage;
  minimizeToTrayOnClose?: boolean;
}

export interface DesktopNotificationPayload {
  title: string;
  body: string;
  onlyWhenUnfocused?: boolean;
}

export const APP_METADATA = {
  name: 'CodexFlow Desktop',
  author: 'iwaitu',
  copyright: 'Copyright © 2026 iwaitu',
  license: 'MPL-2.0',
  projectUrl: 'https://github.com/iwaitu/codexflow',
  releasesUrl: 'https://github.com/iwaitu/codexflow/releases',
} as const;

export type DesktopCommandAction =
  | 'new-conversation'
  | 'new-workspace'
  | 'open-folder'
  | 'open-settings'
  | 'open-updates'
  | 'open-about'
  | 'open-command-palette'
  | 'toggle-theme'
  | 'toggle-inspector-files'
  | 'toggle-inspector-changes'
  | 'toggle-inspector-jobs'
  | 'switch-view-chat'
  | 'switch-view-skills';

export type AppUpdateStatus =
  | 'idle'
  | 'checking'
  | 'available'
  | 'not-available'
  | 'downloading'
  | 'downloaded'
  | 'unavailable'
  | 'error';

export interface AppUpdateState {
  status: AppUpdateStatus;
  currentVersion: string;
  availableVersion: string | null;
  downloadProgress: number | null;
  message: string | null;
}

export interface DesktopBridge {
  getBackendConnection(): Promise<BackendConnectionPublicInfo>;
  onBackendConnectionChanged(listener: (info: BackendConnectionPublicInfo) => void): () => void;
  approveToolExecution(payload: SubmitToolApprovalPayload): Promise<ControlPlaneCallResult<SubmitToolApprovalResult>>;
  getApprovalScript(approvalId: string): Promise<ControlPlaneCallResult<PendingApprovalScriptSnapshot>>;
  setSessionPlanMode(payload: SetPlanModePayload): Promise<ControlPlaneCallResult<PlanModeResult>>;
  submitPlanChatAction(payload: PlanChatControlPayload): Promise<ControlPlaneCallResult<PlanChatAcceptedResult>>;
  updateExecutionSettings(payload: UpdateExecutionSettingsPayload): Promise<ControlPlaneCallResult<ExecutionSettingsResult>>;
  updateModelSettings(payload: UpdateModelSettingsPayload): Promise<ControlPlaneCallResult<ModelSettingsResult>>;
  listPermissionRules(): Promise<ControlPlaneCallResult<PermissionRuleListResult>>;
  deletePermissionRule(ruleId: string): Promise<ControlPlaneCallResult<DeletePermissionRuleResult>>;
  openLocalWorkspace(payload: OpenLocalWorkspacePayload): Promise<ControlPlaneCallResult<OpenLocalWorkspaceResult>>;
  createBusinessPlugin(payload: CreateBusinessPluginRequest): Promise<ControlPlaneCallResult<BusinessPluginDto>>;
  updateBusinessPluginConnection(
    payload: { pluginId: string } & UpdatePluginConnectionRequest,
  ): Promise<ControlPlaneCallResult<BusinessPluginDto>>;
  setBusinessPluginCredential(
    payload: { pluginId: string; slot: CredentialSlotKind } & SetPluginCredentialRequest,
  ): Promise<ControlPlaneCallResult<CredentialRefSummaryDto>>;
  deleteBusinessPluginCredential(payload: {
    pluginId: string;
    slot: CredentialSlotKind;
    unbindIfReferenced?: boolean;
  }): Promise<ControlPlaneCallResult<DeleteCredentialResponseDto>>;
  disableBusinessPlugin(pluginId: string): Promise<ControlPlaneCallResult<DisablePluginResponseDto>>;
  discoverBusinessPlugin(payload: {
    pluginId: string;
    clientRequestId: string;
  }): Promise<ControlPlaneCallResult<DiscoverPluginResultDto>>;
  updateBusinessCapabilityRisk(
    payload: { pluginId: string } & UpdateCapabilityRiskRequest,
  ): Promise<ControlPlaneCallResult<CapabilitySummaryDto>>;
  updateSkillDraftSelection(
    payload: { draftId: string } & PutDraftSelectionRequest,
  ): Promise<ControlPlaneCallResult<SkillDraftDto>>;
  generateSkillDraftCandidate(
    payload: { draftId: string } & GenerateSkillCandidateRequest,
  ): Promise<ControlPlaneCallResult<GenerateSkillCandidateResultDto>>;
  startSkillDraftTestRun(
    payload: { draftId: string } & StartSkillRunRequest,
  ): Promise<ControlPlaneCallResult<SkillRunDto>>;
  publishSkillDraft(
    payload: { draftId: string } & PublishSkillDraftRequest,
  ): Promise<ControlPlaneCallResult<PublishedSkillDto>>;
  activateSkillVersion(payload: {
    skillId: string;
    version: number;
  }): Promise<ControlPlaneCallResult<PublishedSkillDto>>;
  startPublishedSkillRun(
    payload: { skillId: string } & StartSkillRunRequest,
  ): Promise<ControlPlaneCallResult<SkillRunDto>>;
  cancelBusinessSkillRun(runId: string): Promise<ControlPlaneCallResult<SkillRunDto>>;
  pruneBusinessSkillRetention(
    payload?: BusinessSkillRetentionPruneRequest,
  ): Promise<ControlPlaneCallResult<BusinessSkillRetentionPruneResultDto>>;
  pickFolder(): Promise<string | null>;
  getPathForFile(file: File): string | null;
  classifyDroppedFiles(payload: DroppedFileClassifyInput): Promise<ClassifiedDroppedFile[]>;
  pickStandaloneTokenFile(kind: 'access' | 'control'): Promise<PickedTokenFileResult | null>;
  discoverStandaloneInstance(dataDirectory?: string): Promise<StandaloneDiscoveryResult | null>;
  connectStandalone(payload: ConnectStandalonePayload): Promise<BackendConnectionPublicInfo>;
  switchToManaged(): Promise<BackendConnectionPublicInfo>;
  restartBackend(): Promise<BackendConnectionPublicInfo>;
  openExternal(url: string): Promise<boolean>;
  showNotification(payload: DesktopNotificationPayload): Promise<void>;
  setDesktopPreferences(payload: DesktopPreferencesPayload): Promise<void>;
  onCommandAction(listener: (action: DesktopCommandAction) => void): () => void;
  getUpdateState(): Promise<AppUpdateState>;
  onUpdateStateChanged(listener: (state: AppUpdateState) => void): () => void;
  checkForUpdates(): Promise<AppUpdateState>;
  downloadUpdate(): Promise<AppUpdateState>;
  quitAndInstall(): Promise<boolean>;
}

export const IPC_CHANNELS = {
  getBackendConnection: 'codexflow:get-backend-connection',
  backendConnectionChanged: 'codexflow:backend-connection-changed',
  approveToolExecution: 'codexflow:approve-tool-execution',
  getApprovalScript: 'codexflow:get-approval-script',
  setSessionPlanMode: 'codexflow:set-session-plan-mode',
  submitPlanChatAction: 'codexflow:submit-plan-chat-action',
  updateExecutionSettings: 'codexflow:update-execution-settings',
  updateModelSettings: 'codexflow:update-model-settings',
  listPermissionRules: 'codexflow:list-permission-rules',
  deletePermissionRule: 'codexflow:delete-permission-rule',
  openLocalWorkspace: 'codexflow:open-local-workspace',
  createBusinessPlugin: 'codexflow:create-business-plugin',
  updateBusinessPluginConnection: 'codexflow:update-business-plugin-connection',
  setBusinessPluginCredential: 'codexflow:set-business-plugin-credential',
  deleteBusinessPluginCredential: 'codexflow:delete-business-plugin-credential',
  disableBusinessPlugin: 'codexflow:disable-business-plugin',
  discoverBusinessPlugin: 'codexflow:discover-business-plugin',
  updateBusinessCapabilityRisk: 'codexflow:update-business-capability-risk',
  updateSkillDraftSelection: 'codexflow:update-skill-draft-selection',
  generateSkillDraftCandidate: 'codexflow:generate-skill-draft-candidate',
  startSkillDraftTestRun: 'codexflow:start-skill-draft-test-run',
  publishSkillDraft: 'codexflow:publish-skill-draft',
  activateSkillVersion: 'codexflow:activate-skill-version',
  startPublishedSkillRun: 'codexflow:start-published-skill-run',
  cancelBusinessSkillRun: 'codexflow:cancel-business-skill-run',
  pruneBusinessSkillRetention: 'codexflow:prune-business-skill-retention',
  pickFolder: 'codexflow:pick-folder',
  classifyDroppedFiles: 'codexflow:classify-dropped-files',
  pickStandaloneTokenFile: 'codexflow:pick-standalone-token-file',
  discoverStandaloneInstance: 'codexflow:discover-standalone-instance',
  connectStandalone: 'codexflow:connect-standalone',
  switchToManaged: 'codexflow:switch-to-managed',
  restartBackend: 'codexflow:restart-backend',
  openExternal: 'codexflow:open-external',
  showNotification: 'codexflow:show-notification',
  setDesktopPreferences: 'codexflow:set-desktop-preferences',
  commandAction: 'codexflow:command-action',
  getUpdateState: 'codexflow:get-update-state',
  updateStateChanged: 'codexflow:update-state-changed',
  checkForUpdates: 'codexflow:check-for-updates',
  downloadUpdate: 'codexflow:download-update',
  quitAndInstall: 'codexflow:quit-and-install',
} as const;
