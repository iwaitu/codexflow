import { contextBridge, ipcRenderer, webUtils } from 'electron';
import {
  IPC_CHANNELS,
  type BackendConnectionPublicInfo,
  type ConnectStandalonePayload,
  type DesktopBridge,
  type DroppedFileClassifyInput,
  type OpenLocalWorkspacePayload,
  type PlanChatControlPayload,
  type SubmitToolApprovalPayload,
  type UpdateExecutionSettingsPayload,
  type UpdateModelSettingsPayload,
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
  submitPlanChatAction: (payload: PlanChatControlPayload) =>
    ipcRenderer.invoke(IPC_CHANNELS.submitPlanChatAction, payload),
  updateExecutionSettings: (payload: UpdateExecutionSettingsPayload) =>
    ipcRenderer.invoke(IPC_CHANNELS.updateExecutionSettings, payload),
  updateModelSettings: (payload: UpdateModelSettingsPayload) =>
    ipcRenderer.invoke(IPC_CHANNELS.updateModelSettings, payload),
  listPermissionRules: () => ipcRenderer.invoke(IPC_CHANNELS.listPermissionRules),
  deletePermissionRule: (ruleId: string) =>
    ipcRenderer.invoke(IPC_CHANNELS.deletePermissionRule, ruleId),
  openLocalWorkspace: (payload: OpenLocalWorkspacePayload) =>
    ipcRenderer.invoke(IPC_CHANNELS.openLocalWorkspace, payload),
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
  showNotification: (payload: { title: string; body: string }) =>
    ipcRenderer.invoke(IPC_CHANNELS.showNotification, payload),
};

contextBridge.exposeInMainWorld('codexflow', api);
