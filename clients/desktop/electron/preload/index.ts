import { contextBridge, ipcRenderer, webUtils } from 'electron';
import {
  IPC_CHANNELS,
  type AppUpdateState,
  type BackendConnectionPublicInfo,
  type BusinessSkillRetentionPruneRequest,
  type ComputerControlOverlayState,
  type ComputerEmergencyStoppedEvent,
  type ConnectStandalonePayload,
  type CreateBusinessPluginRequest,
  type CredentialSlotKind,
  type DesktopBridge,
  type DesktopCommandAction,
  type DesktopNotificationPayload,
  type DesktopPreferencesPayload,
  type DroppedFileClassifyInput,
  type EnvironmentDependencyId,
  type EnvironmentInstallEvent,
  type GenerateSkillCandidateRequest,
  type OpenLocalWorkspacePayload,
  type PlanChatControlPayload,
  type PublishSkillDraftRequest,
  type PutDraftSelectionRequest,
  type SetPlanModePayload,
  type SetPluginCredentialRequest,
  type StartSkillRunRequest,
  type SubmitToolApprovalPayload,
  type UpdateBrowserSettingsRequest,
  type UpdateCapabilityRiskRequest,
  type UpdateComputerSettingsRequest,
  type UpdateExecutionSettingsPayload,
  type UpdateModelSettingsPayload,
  type UpdatePluginConnectionRequest,
  type WorkspaceFileContextMenuPayload,
  type WorkspaceTerminalEvent,
} from '../../src/shared/ipc';

const api: DesktopBridge = {
  getBackendConnection: () => ipcRenderer.invoke(IPC_CHANNELS.getBackendConnection),
  onBackendConnectionChanged: (listener) => {
    const handler = (_event: Electron.IpcRendererEvent, info: BackendConnectionPublicInfo) => {
      listener(info);
    };
    ipcRenderer.on(IPC_CHANNELS.backendConnectionChanged, handler);
    return () => {
      ipcRenderer.removeListener(IPC_CHANNELS.backendConnectionChanged, handler);
    };
  },
  approveToolExecution: (payload: SubmitToolApprovalPayload) =>
    ipcRenderer.invoke(IPC_CHANNELS.approveToolExecution, payload),
  getApprovalScript: (approvalId: string) =>
    ipcRenderer.invoke(IPC_CHANNELS.getApprovalScript, approvalId),
  setSessionPlanMode: (payload: SetPlanModePayload) =>
    ipcRenderer.invoke(IPC_CHANNELS.setSessionPlanMode, payload),
  submitPlanChatAction: (payload: PlanChatControlPayload) =>
    ipcRenderer.invoke(IPC_CHANNELS.submitPlanChatAction, payload),
  updateExecutionSettings: (payload: UpdateExecutionSettingsPayload) =>
    ipcRenderer.invoke(IPC_CHANNELS.updateExecutionSettings, payload),
  updateModelSettings: (payload: UpdateModelSettingsPayload) =>
    ipcRenderer.invoke(IPC_CHANNELS.updateModelSettings, payload),
  updateBrowserSettings: (payload: UpdateBrowserSettingsRequest) =>
    ipcRenderer.invoke(IPC_CHANNELS.updateBrowserSettings, payload),
  closeBrowserSession: (sessionId: string) =>
    ipcRenderer.invoke(IPC_CHANNELS.closeBrowserSession, sessionId),
  startBrowserTakeover: (sessionId: string, reason?: string) =>
    ipcRenderer.invoke(IPC_CHANNELS.startBrowserTakeover, sessionId, reason),
  controlBrowserPanel: (sessionId, request) =>
    ipcRenderer.invoke(IPC_CHANNELS.controlBrowserPanel, sessionId, request),
  completeBrowserTakeover: (sessionId: string, note?: string) =>
    ipcRenderer.invoke(IPC_CHANNELS.completeBrowserTakeover, sessionId, note),
  updateComputerSettings: (payload: UpdateComputerSettingsRequest) =>
    ipcRenderer.invoke(IPC_CHANNELS.updateComputerSettings, payload),
  closeComputerSession: (sessionId: string) =>
    ipcRenderer.invoke(IPC_CHANNELS.closeComputerSession, sessionId),
  startComputerTakeover: (sessionId: string, reason?: string) =>
    ipcRenderer.invoke(IPC_CHANNELS.startComputerTakeover, sessionId, reason),
  completeComputerTakeover: (sessionId: string, note?: string) =>
    ipcRenderer.invoke(IPC_CHANNELS.completeComputerTakeover, sessionId, note),
  emergencyStopComputer: (payload?: { sessionId?: string; reason?: string }) =>
    ipcRenderer.invoke(IPC_CHANNELS.emergencyStopComputer, payload ?? {}),
  avoidHostWindowForComputer: () =>
    ipcRenderer.invoke(IPC_CHANNELS.avoidHostWindowForComputer),
  restoreHostWindowForComputer: () =>
    ipcRenderer.invoke(IPC_CHANNELS.restoreHostWindowForComputer),
  setComputerControlActive: (state: ComputerControlOverlayState) =>
    ipcRenderer.invoke(IPC_CHANNELS.setComputerControlActive, state),
  onComputerEmergencyStopped: (listener) => {
    const handler = (_event: Electron.IpcRendererEvent, eventPayload: ComputerEmergencyStoppedEvent) => {
      listener(eventPayload);
    };
    ipcRenderer.on(IPC_CHANNELS.computerEmergencyStopped, handler);
    return () => {
      ipcRenderer.removeListener(IPC_CHANNELS.computerEmergencyStopped, handler);
    };
  },
  listPermissionRules: () => ipcRenderer.invoke(IPC_CHANNELS.listPermissionRules),
  deletePermissionRule: (ruleId: string) =>
    ipcRenderer.invoke(IPC_CHANNELS.deletePermissionRule, ruleId),
  openLocalWorkspace: (payload: OpenLocalWorkspacePayload) =>
    ipcRenderer.invoke(IPC_CHANNELS.openLocalWorkspace, payload),
  createBusinessPlugin: (payload: CreateBusinessPluginRequest) =>
    ipcRenderer.invoke(IPC_CHANNELS.createBusinessPlugin, payload),
  updateBusinessPluginConnection: (payload: { pluginId: string } & UpdatePluginConnectionRequest) =>
    ipcRenderer.invoke(IPC_CHANNELS.updateBusinessPluginConnection, payload),
  setBusinessPluginCredential: (payload: { pluginId: string; slot: CredentialSlotKind } & SetPluginCredentialRequest) =>
    ipcRenderer.invoke(IPC_CHANNELS.setBusinessPluginCredential, payload),
  deleteBusinessPluginCredential: (payload: { pluginId: string; slot: CredentialSlotKind; unbindIfReferenced?: boolean }) =>
    ipcRenderer.invoke(IPC_CHANNELS.deleteBusinessPluginCredential, payload),
  disableBusinessPlugin: (pluginId: string) =>
    ipcRenderer.invoke(IPC_CHANNELS.disableBusinessPlugin, pluginId),
  discoverBusinessPlugin: (payload: { pluginId: string; clientRequestId: string }) =>
    ipcRenderer.invoke(IPC_CHANNELS.discoverBusinessPlugin, payload),
  updateBusinessCapabilityRisk: (payload: { pluginId: string } & UpdateCapabilityRiskRequest) =>
    ipcRenderer.invoke(IPC_CHANNELS.updateBusinessCapabilityRisk, payload),
  updateSkillDraftSelection: (payload: { draftId: string } & PutDraftSelectionRequest) =>
    ipcRenderer.invoke(IPC_CHANNELS.updateSkillDraftSelection, payload),
  generateSkillDraftCandidate: (payload: { draftId: string } & GenerateSkillCandidateRequest) =>
    ipcRenderer.invoke(IPC_CHANNELS.generateSkillDraftCandidate, payload),
  startSkillDraftTestRun: (payload: { draftId: string } & StartSkillRunRequest) =>
    ipcRenderer.invoke(IPC_CHANNELS.startSkillDraftTestRun, payload),
  publishSkillDraft: (payload: { draftId: string } & PublishSkillDraftRequest) =>
    ipcRenderer.invoke(IPC_CHANNELS.publishSkillDraft, payload),
  activateSkillVersion: (payload: { skillId: string; version: number }) =>
    ipcRenderer.invoke(IPC_CHANNELS.activateSkillVersion, payload),
  startPublishedSkillRun: (payload: { skillId: string } & StartSkillRunRequest) =>
    ipcRenderer.invoke(IPC_CHANNELS.startPublishedSkillRun, payload),
  cancelBusinessSkillRun: (runId: string) =>
    ipcRenderer.invoke(IPC_CHANNELS.cancelBusinessSkillRun, runId),
  pruneBusinessSkillRetention: (payload?: BusinessSkillRetentionPruneRequest) =>
    ipcRenderer.invoke(IPC_CHANNELS.pruneBusinessSkillRetention, payload ?? {}),
  pickFolder: () => ipcRenderer.invoke(IPC_CHANNELS.pickFolder),
  getPathForFile: (file: File) => {
    try {
      const resolved = webUtils.getPathForFile(file);
      return resolved || null;
    } catch {
      return null;
    }
  },
  classifyDroppedFiles: (payload: DroppedFileClassifyInput) =>
    ipcRenderer.invoke(IPC_CHANNELS.classifyDroppedFiles, payload),
  pickStandaloneTokenFile: (kind: 'access' | 'control') =>
    ipcRenderer.invoke(IPC_CHANNELS.pickStandaloneTokenFile, kind),
  discoverStandaloneInstance: (dataDirectory?: string) =>
    ipcRenderer.invoke(IPC_CHANNELS.discoverStandaloneInstance, dataDirectory),
  connectStandalone: (payload: ConnectStandalonePayload) =>
    ipcRenderer.invoke(IPC_CHANNELS.connectStandalone, payload),
  switchToManaged: () => ipcRenderer.invoke(IPC_CHANNELS.switchToManaged),
  restartBackend: () => ipcRenderer.invoke(IPC_CHANNELS.restartBackend),
  openExternal: (url: string) => ipcRenderer.invoke(IPC_CHANNELS.openExternal, url),
  showWorkspaceFileContextMenu: (payload: WorkspaceFileContextMenuPayload) =>
    ipcRenderer.invoke(IPC_CHANNELS.showWorkspaceFileContextMenu, payload),
  openWorkspaceTerminal: (workspacePath: string) =>
    ipcRenderer.invoke(IPC_CHANNELS.openWorkspaceTerminal, workspacePath),
  startWorkspaceTerminal: (request) => ipcRenderer.invoke(IPC_CHANNELS.startWorkspaceTerminal, request),
  actOnWorkspaceTerminal: (action) => ipcRenderer.send(IPC_CHANNELS.workspaceTerminalAction, action),
  onWorkspaceTerminalEvent: (listener) => {
    const handler = (_event: Electron.IpcRendererEvent, event: WorkspaceTerminalEvent) => listener(event);
    ipcRenderer.on(IPC_CHANNELS.workspaceTerminalEvent, handler);
    return () => ipcRenderer.removeListener(IPC_CHANNELS.workspaceTerminalEvent, handler);
  },
  showNotification: (payload: DesktopNotificationPayload) =>
    ipcRenderer.invoke(IPC_CHANNELS.showNotification, payload),
  setDesktopPreferences: (payload: DesktopPreferencesPayload) =>
    ipcRenderer.invoke(IPC_CHANNELS.setDesktopPreferences, payload),
  onCommandAction: (listener) => {
    const handler = (_event: Electron.IpcRendererEvent, action: DesktopCommandAction) => {
      listener(action);
    };
    ipcRenderer.on(IPC_CHANNELS.commandAction, handler);
    return () => {
      ipcRenderer.removeListener(IPC_CHANNELS.commandAction, handler);
    };
  },
  getUpdateState: () => ipcRenderer.invoke(IPC_CHANNELS.getUpdateState),
  onUpdateStateChanged: (listener) => {
    const handler = (_event: Electron.IpcRendererEvent, state: AppUpdateState) => {
      listener(state);
    };
    ipcRenderer.on(IPC_CHANNELS.updateStateChanged, handler);
    return () => {
      ipcRenderer.removeListener(IPC_CHANNELS.updateStateChanged, handler);
    };
  },
  checkForUpdates: () => ipcRenderer.invoke(IPC_CHANNELS.checkForUpdates),
  downloadUpdate: () => ipcRenderer.invoke(IPC_CHANNELS.downloadUpdate),
  quitAndInstall: () => ipcRenderer.invoke(IPC_CHANNELS.quitAndInstall),
  inspectEnvironment: (forceRefresh?: boolean) =>
    ipcRenderer.invoke(IPC_CHANNELS.inspectEnvironment, Boolean(forceRefresh)),
  installEnvironmentDependency: (dependencyId: EnvironmentDependencyId) =>
    ipcRenderer.invoke(IPC_CHANNELS.installEnvironmentDependency, dependencyId),
  cancelEnvironmentInstall: () => ipcRenderer.invoke(IPC_CHANNELS.cancelEnvironmentInstall),
  onEnvironmentInstallEvent: (listener) => {
    const handler = (_event: Electron.IpcRendererEvent, payload: EnvironmentInstallEvent) => {
      listener(payload);
    };
    ipcRenderer.on(IPC_CHANNELS.environmentInstallEvent, handler);
    return () => {
      ipcRenderer.removeListener(IPC_CHANNELS.environmentInstallEvent, handler);
    };
  },
};

contextBridge.exposeInMainWorld('codexflow', api);
