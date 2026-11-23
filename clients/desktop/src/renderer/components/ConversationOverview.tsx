import { useId } from 'react';
import { ChevronRight, Cpu, FileDiff, FolderTree, Globe, LayoutDashboard, Monitor, Terminal } from 'lucide-react';
import type { ConversationSummary, SessionContextWindow, SessionWorkMode } from '../lib/apiClient';
import { useI18n } from '../lib/i18n';
import type { InspectorTab } from '../stores/useAppStore';
import { formatContextUsagePercent } from './ContextUsageRing';

interface ConversationOverviewProps {
  conversation: ConversationSummary | null;
  hasConversation: boolean;
  workspaceName: string | null;
  workMode: SessionWorkMode;
  planModeActive: boolean;
  contextWindow: SessionContextWindow | null;
  changedFileCount: number | null;
  jobCount: number;
  activeInspectorTab: InspectorTab | null;
  browserRunning: boolean;
  computerActive: boolean;
  terminalAvailable: boolean;
  onOpenInspector: (tab: InspectorTab) => void;
}

export function ConversationOverview({
  conversation,
  hasConversation,
  workspaceName,
  workMode,
  planModeActive,
  contextWindow,
  changedFileCount,
  jobCount,
  activeInspectorTab,
  browserRunning,
  computerActive,
  terminalAvailable,
  onOpenInspector,
}: ConversationOverviewProps) {
  const { language, t } = useI18n();
  const headingId = useId();
  const inDevelopment = workMode === 'Development';
  const title = t('overview.title');
  const updatedAt = conversation?.updatedAtUtc;
  const updatedTime = updatedAt ? Date.parse(updatedAt) : NaN;
  const browserStatus = browserRunning ? { label: t('overview.browserRunning'), color: 'bg-emerald-400' } : null;
  const computerStatus = computerActive ? { label: t('overview.computerActive'), color: 'bg-rose-400 animate-pulse' } : null;
  const access = [
    { tab: 'files', label: t('workspace.files'), icon: FolderTree, count: null },
    ...(inDevelopment ? [
      { tab: 'changes', label: t('workspace.changes'), icon: FileDiff, count: changedFileCount },
      { tab: 'jobs', label: t('workspace.jobs'), icon: Cpu, count: jobCount },
    ] as const : []),
    { tab: 'browser', label: t('workspace.browser'), icon: Globe, count: null },
    { tab: 'computer', label: t('workspace.computer'), icon: Monitor, count: null },
    { tab: 'terminal', label: t('overview.terminal'), icon: Terminal, count: null },
  ] as const;

  const details = workspaceName ? (
    <>
      <div className="space-y-3 px-3.5 pb-3.5">
        <p className="break-words text-sm font-semibold leading-snug text-slate-100">
          {conversation?.name || t(hasConversation ? 'overview.currentConversation' : 'overview.newConversation')}
        </p>
        <dl className="space-y-1.5 text-xs">
          <div className="flex min-w-0 items-baseline justify-between gap-2">
            <dt className="shrink-0 text-slate-400">{t('overview.workspace')}</dt>
            <dd className="truncate text-right text-slate-200" title={workspaceName}>{workspaceName}</dd>
          </div>
          <div className="flex justify-between gap-2">
            <dt className="text-slate-400">{t('overview.mode')}</dt>
            <dd className="text-right text-slate-200">
              {t(planModeActive ? 'overview.planMode' : inDevelopment ? 'overview.developmentMode' : 'overview.dailyMode')}
            </dd>
          </div>
          {Number.isFinite(updatedTime) && (
            <div className="flex justify-between gap-2">
              <dt className="shrink-0 text-slate-400">{t('overview.updated')}</dt>
              <dd className="text-right text-slate-200">
                <time dateTime={updatedAt}>
                  {new Intl.DateTimeFormat(language, {
                    month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
                  }).format(updatedTime)}
                </time>
              </dd>
            </div>
          )}
        </dl>
        {contextWindow && (
          <div className="space-y-1.5 text-xs">
            <div className="flex justify-between gap-2">
              <span className="text-slate-400">{t('overview.context')}</span>
              <span className="font-medium tabular-nums text-slate-200">
                {formatContextUsagePercent(contextWindow.usageRatio)}
              </span>
            </div>
            <progress
              aria-label={t('overview.context')}
              title={`${contextWindow.estimatedTokens.toLocaleString()} / ${contextWindow.modelMaxTokens.toLocaleString()} tokens`}
              value={contextWindow.usageRatio}
              max={1}
              className="block h-1.5 w-full overflow-hidden rounded-full [&::-webkit-progress-bar]:bg-slate-800 [&::-webkit-progress-value]:rounded-full [&::-webkit-progress-value]:bg-indigo-400"
            />
          </div>
        )}
      </div>
      <div className="space-y-0.5 border-t border-slate-800 px-2 py-2">
        <p className="px-1.5 pb-0.5 text-[11px] font-medium text-slate-400">{t('overview.workspaceActivity')}</p>
        {access.map(({ tab, label, icon: Icon, count }) => {
          const selected = activeInspectorTab === tab;
          const status = tab === 'browser' ? browserStatus : tab === 'computer' ? computerStatus : null;
          return (
            <button
              key={tab}
              type="button"
              aria-current={selected || undefined}
              disabled={tab === 'terminal' && !terminalAvailable}
              title={tab === 'terminal' && !terminalAvailable ? t('overview.terminalLocalOnly') : undefined}
              onClick={() => onOpenInspector(tab)}
              className={`flex w-full items-center gap-2 rounded-md px-1.5 py-1.5 text-left text-xs enabled:hover:bg-slate-800 enabled:hover:text-slate-100 disabled:opacity-50 ${
                selected ? 'bg-slate-800 text-slate-100' : 'text-slate-300'
              }`}
            >
              <Icon className="h-3.5 w-3.5 shrink-0 text-indigo-400" aria-hidden="true" />
              <span className="flex-1">{label}</span>
              {count !== null && <span className="tabular-nums text-slate-400">{count}</span>}
              {status && (
                <span title={status.label} className={`h-1.5 w-1.5 shrink-0 rounded-full ${status.color}`}>
                  <span className="sr-only">{status.label}</span>
                </span>
              )}
              <ChevronRight className="h-3 w-3 shrink-0 text-slate-500" aria-hidden="true" />
            </button>
          );
        })}
      </div>
    </>
  ) : (
    <p className="px-3.5 pb-3.5 text-xs text-slate-400">{t('overview.noWorkspace')}</p>
  );

  return (
    <aside
      className="conversation-overview-rail z-10 mx-2.5 mt-3 min-w-0 max-h-[calc(100%-1.5rem)] self-start overflow-y-auto rounded-xl border border-slate-800 bg-slate-950 text-slate-200 shadow-lg"
      aria-labelledby={headingId}
    >
      <h2 id={headingId} className="flex items-center gap-2 px-3.5 py-3 text-xs font-semibold text-slate-100">
        <LayoutDashboard className="h-3.5 w-3.5 shrink-0 text-indigo-400" aria-hidden="true" />
        {title}
      </h2>
      {details}
    </aside>
  );
}
