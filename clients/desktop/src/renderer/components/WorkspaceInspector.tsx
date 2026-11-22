import React, { useEffect, useRef, useState } from 'react';
import { Cpu, FileDiff, FolderTree, Globe, Monitor, X } from 'lucide-react';
import type { BrowserSessionStateDto, ComputerSessionStateDto } from '@shared/ipc';
import type {
  ApiAuthContext,
  BackgroundJob,
  SessionWorkMode,
  VerificationReceipt,
  WorkspaceChanges,
} from '../lib/apiClient';
import type { InspectorTab } from '../stores/useAppStore';
import { BrowserPanel } from './BrowserPanel';
import { ComputerPanel } from './ComputerPanel';
import { JobsPanel } from './JobsPanel';
import { WorkspaceChangesPanel } from './WorkspaceChangesPanel';
import { WorkspaceFilesPanel } from './WorkspaceFilesPanel';

interface WorkspaceInspectorProps {
  open: boolean;
  activeTab: InspectorTab;
  workMode?: SessionWorkMode;
  auth: ApiAuthContext | null;
  sessionId: string | null;
  workspacePath?: string | null;
  workspaceRevision: number;
  changes: WorkspaceChanges | null | undefined;
  changesLoading: boolean;
  changesFetching: boolean;
  changesError: string | null;
  jobs: BackgroundJob[];
  verificationReceipts: Record<string, VerificationReceipt>;
  executionBackend: string | null;
  browserState?: BrowserSessionStateDto | null;
  computerState?: ComputerSessionStateDto | null;
  controlPlaneAvailable?: boolean;
  onBrowserStateUpdated?: (next: BrowserSessionStateDto) => void;
  onComputerStateUpdated?: (next: ComputerSessionStateDto) => void;
  onComputerEmergencyStopped?: () => void;
  onSelectTab: (tab: InspectorTab) => void;
  onClose: () => void;
  onRefreshChanges: () => void;
  onAttachWorkspaceFile?: (file: { name: string; path: string; sizeBytes: number }) => void;
  onVerifyJob: (jobId: string) => Promise<void>;
  onJobsMutated: () => Promise<void>;
}

const MIN_INSPECTOR_WIDTH = 320;
const INITIAL_INSPECTOR_WIDTH = 420;
const INSPECTOR_WIDTH_KEY = 'codexflow.workspaceInspectorWidth';

export const WorkspaceInspector: React.FC<WorkspaceInspectorProps> = ({
  open,
  activeTab,
  workMode = 'Development',
  auth,
  sessionId,
  workspacePath,
  workspaceRevision,
  changes,
  changesLoading,
  changesFetching,
  changesError,
  jobs,
  verificationReceipts,
  executionBackend,
  browserState = null,
  computerState = null,
  controlPlaneAvailable = false,
  onBrowserStateUpdated,
  onComputerStateUpdated,
  onComputerEmergencyStopped,
  onSelectTab,
  onClose,
  onRefreshChanges,
  onAttachWorkspaceFile,
  onVerifyJob,
  onJobsMutated,
}) => {
  const panelRef = useRef<HTMLElement>(null);
  const drag = useRef<{ pointerId: number; x: number; width: number } | null>(null);
  const [width, setWidth] = useState(() => {
    const saved = Number(localStorage.getItem(INSPECTOR_WIDTH_KEY));
    return Number.isFinite(saved) && saved >= MIN_INSPECTOR_WIDTH ? saved : INITIAL_INSPECTOR_WIDTH;
  });
  const [maxWidth, setMaxWidth] = useState(Math.round(window.innerWidth * 0.46));
  const displayedWidth = Math.min(width, maxWidth);
  const minWidth = Math.min(MIN_INSPECTOR_WIDTH, maxWidth);
  const resize = (next: number) => setWidth(Math.max(minWidth, Math.min(next, maxWidth)));

  useEffect(() => {
    const aside = panelRef.current;
    const container = aside?.parentElement;
    const main = aside?.previousElementSibling;
    if (!open || !container || !main) return;
    const measure = () => {
      const available = container.getBoundingClientRect().right - main.getBoundingClientRect().left;
      setMaxWidth(Math.max(0, Math.floor(available - Math.min(360, available / 2))));
    };
    measure();
    if (typeof ResizeObserver === 'undefined') {
      window.addEventListener('resize', measure);
      return () => window.removeEventListener('resize', measure);
    }
    const observer = new ResizeObserver(measure);
    observer.observe(container);
    if (container.firstElementChild) observer.observe(container.firstElementChild);
    return () => observer.disconnect();
  }, [open]);

  useEffect(() => {
    if (width >= MIN_INSPECTOR_WIDTH) localStorage.setItem(INSPECTOR_WIDTH_KEY, String(width));
  }, [width]);

  if (!open) {
    return null;
  }

  const changedCount = changes?.files?.length ?? 0;
  const isBrowserRunning = browserState?.status === 'running';
  const isComputerRunning = Boolean(computerState?.active && !computerState?.emergencyStopped);
  const showEngineeringTabs = workMode !== 'Daily';

  return (
    <aside
      ref={panelRef}
      data-testid="workspace-inspector"
      className="relative flex h-full min-w-0 shrink-0 flex-col border-l border-slate-800 bg-slate-950/95"
      style={{ width: displayedWidth }}
    >
      <div
        role="separator"
        aria-label="调整右侧栏宽度"
        aria-orientation="vertical"
        aria-valuemin={minWidth}
        aria-valuemax={maxWidth}
        aria-valuenow={displayedWidth}
        tabIndex={0}
        className="absolute inset-y-0 -left-1 z-10 w-2 touch-none select-none cursor-col-resize outline-none hover:bg-indigo-500/60 focus:bg-indigo-500/60"
        onPointerDown={(event) => {
          if (event.button !== 0) return;
          drag.current = { pointerId: event.pointerId, x: event.clientX, width: displayedWidth };
          event.currentTarget.setPointerCapture(event.pointerId);
          event.currentTarget.focus();
        }}
        onPointerMove={(event) => {
          const start = drag.current;
          if (start?.pointerId === event.pointerId) resize(start.width + start.x - event.clientX);
        }}
        onLostPointerCapture={() => { drag.current = null; }}
        onKeyDown={(event) => {
          const step = event.shiftKey ? 80 : 20;
          switch (event.key) {
            case 'ArrowLeft': resize(displayedWidth + step); break;
            case 'ArrowRight': resize(displayedWidth - step); break;
            case 'Home': resize(minWidth); break;
            case 'End': resize(maxWidth); break;
            default: return;
          }
          event.preventDefault();
        }}
      />
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
          {showEngineeringTabs && (
            <>
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
            </>
          )}
          <button
            type="button"
            data-testid="inspector-tab-browser"
            onClick={() => onSelectTab('browser')}
            className={`inline-flex items-center gap-1.5 rounded-md px-2.5 py-1 text-xs font-medium ${
              activeTab === 'browser'
                ? 'bg-slate-800 text-slate-100'
                : 'text-slate-400 hover:bg-slate-900 hover:text-slate-200'
            }`}
          >
            <Globe className="h-3.5 w-3.5" />
            <span>浏览器</span>
            {isBrowserRunning && <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" />}
          </button>
          <button
            type="button"
            data-testid="inspector-tab-computer"
            onClick={() => onSelectTab('computer')}
            className={`inline-flex items-center gap-1.5 rounded-md px-2.5 py-1 text-xs font-medium ${
              activeTab === 'computer'
                ? 'bg-slate-800 text-slate-100'
                : 'text-slate-400 hover:bg-slate-900 hover:text-slate-200'
            }`}
          >
            <Monitor className="h-3.5 w-3.5" />
            <span>桌面</span>
            {isComputerRunning && <span className="h-1.5 w-1.5 rounded-full bg-rose-400 animate-pulse" />}
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
            workspacePath={workspacePath}
            workspaceRevision={workspaceRevision}
            onAttachWorkspaceFile={onAttachWorkspaceFile}
          />
        )}
        {activeTab === 'changes' && showEngineeringTabs && (
          <WorkspaceChangesPanel
            sessionId={sessionId}
            changes={changes}
            isLoading={changesLoading}
            isFetching={changesFetching}
            error={changesError}
            onRefresh={onRefreshChanges}
          />
        )}
        {activeTab === 'jobs' && showEngineeringTabs && (
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
        {activeTab === 'browser' && (
          <BrowserPanel
            auth={auth}
            sessionId={sessionId}
            browserState={browserState}
            controlPlaneAvailable={controlPlaneAvailable}
            onStateUpdated={(next) => onBrowserStateUpdated?.(next)}
          />
        )}
        {activeTab === 'computer' && (
          <ComputerPanel
            auth={auth}
            sessionId={sessionId}
            computerState={computerState}
            controlPlaneAvailable={controlPlaneAvailable}
            onStateUpdated={(next) => onComputerStateUpdated?.(next)}
            onEmergencyStopped={onComputerEmergencyStopped}
          />
        )}
      </div>
    </aside>
  );
};
