import React from 'react';
import { Cpu, FileDiff, FolderTree, X } from 'lucide-react';
import type {
  ApiAuthContext,
  BackgroundJob,
  VerificationReceipt,
  WorkspaceChanges,
} from '../lib/apiClient';
import type { InspectorTab } from '../stores/useAppStore';
import { JobsPanel } from './JobsPanel';
import { WorkspaceChangesPanel } from './WorkspaceChangesPanel';
import { WorkspaceFilesPanel } from './WorkspaceFilesPanel';

interface WorkspaceInspectorProps {
  open: boolean;
  activeTab: InspectorTab;
  auth: ApiAuthContext | null;
  sessionId: string | null;
  workspaceRevision: number;
  changes: WorkspaceChanges | null | undefined;
  changesLoading: boolean;
  changesFetching: boolean;
  changesError: string | null;
  jobs: BackgroundJob[];
  verificationReceipts: Record<string, VerificationReceipt>;
  executionBackend: string | null;
  onSelectTab: (tab: InspectorTab) => void;
  onClose: () => void;
  onRefreshChanges: () => void;
  onAttachWorkspaceFile?: (file: { name: string; path: string; sizeBytes: number }) => void;
  onVerifyJob: (jobId: string) => Promise<void>;
  onJobsMutated: () => Promise<void>;
}

export const WorkspaceInspector: React.FC<WorkspaceInspectorProps> = ({
  open,
  activeTab,
  auth,
  sessionId,
  workspaceRevision,
  changes,
  changesLoading,
  changesFetching,
  changesError,
  jobs,
  verificationReceipts,
  executionBackend,
  onSelectTab,
  onClose,
  onRefreshChanges,
  onAttachWorkspaceFile,
  onVerifyJob,
  onJobsMutated,
}) => {
  if (!open) {
    return null;
  }

  const changedCount = changes?.files?.length ?? 0;

  return (
    <aside
      data-testid="workspace-inspector"
      className="flex h-full w-[420px] max-w-[46vw] shrink-0 flex-col border-l border-slate-800 bg-slate-950/95"
    >
      <div className="flex items-center justify-between border-b border-slate-800 px-2.5 py-2">
        <div className="flex items-center gap-1">
          <button
            type="button"
            onClick={() => onSelectTab('files')}
            className={`inline-flex items-center gap-1.5 rounded-md px-2.5 py-1 text-xs font-medium ${
              activeTab === 'files'
                ? 'bg-slate-800 text-slate-100'
                : 'text-slate-400 hover:bg-slate-900 hover:text-slate-200'
            }`}
          >
            <FolderTree className="h-3.5 w-3.5" />
            <span>文件</span>
          </button>
          <button
            type="button"
            onClick={() => onSelectTab('changes')}
            className={`inline-flex items-center gap-1.5 rounded-md px-2.5 py-1 text-xs font-medium ${
              activeTab === 'changes'
                ? 'bg-slate-800 text-slate-100'
                : 'text-slate-400 hover:bg-slate-900 hover:text-slate-200'
            }`}
          >
            <FileDiff className="h-3.5 w-3.5" />
            <span>变更{changedCount > 0 ? ` (${changedCount})` : ''}</span>
          </button>
          <button
            type="button"
            onClick={() => onSelectTab('jobs')}
            className={`inline-flex items-center gap-1.5 rounded-md px-2.5 py-1 text-xs font-medium ${
              activeTab === 'jobs'
                ? 'bg-slate-800 text-slate-100'
                : 'text-slate-400 hover:bg-slate-900 hover:text-slate-200'
            }`}
          >
            <Cpu className="h-3.5 w-3.5" />
            <span>作业{jobs.length > 0 ? ` (${jobs.length})` : ''}</span>
          </button>
        </div>

        <button
          type="button"
          onClick={onClose}
          title="收起工作台"
          aria-label="收起工作台"
          className="rounded-md p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      <div className="min-h-0 flex-1 overflow-hidden">
        {activeTab === 'files' && (
          <WorkspaceFilesPanel
            auth={auth}
            sessionId={sessionId}
            workspaceRevision={workspaceRevision}
            onAttachWorkspaceFile={onAttachWorkspaceFile}
          />
        )}
        {activeTab === 'changes' && (
          <WorkspaceChangesPanel
            sessionId={sessionId}
            changes={changes}
            isLoading={changesLoading}
            isFetching={changesFetching}
            error={changesError}
            onRefresh={onRefreshChanges}
          />
        )}
        {activeTab === 'jobs' && (
          <JobsPanel
            auth={auth}
            sessionId={sessionId}
            jobs={jobs}
            verificationReceipts={verificationReceipts}
            executionBackend={executionBackend}
            onVerifyJob={onVerifyJob}
            onJobsMutated={onJobsMutated}
          />
        )}
      </div>
    </aside>
  );
};
