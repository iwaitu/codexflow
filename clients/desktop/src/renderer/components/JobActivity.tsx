import { Loader2 } from 'lucide-react';
import type { BackgroundJob } from '../lib/apiClient';
import { useI18n, type I18nKey } from '../lib/i18n';
import { useAppStore } from '../stores/useAppStore';

export function JobFailureDetails({ failure }: { failure: BackgroundJob['failure'] }) {
  if (!failure) return null;

  return (
    <div className="space-y-1 break-words rounded border border-rose-500/30 bg-rose-950/30 px-2 py-1.5 text-[11px] text-rose-200">
      {failure.message && <p>{failure.message}</p>}
      {failure.remedy && <p>{failure.remedy}</p>}
      {Boolean(failure.evidence?.length) && (
        <ul className="list-disc space-y-0.5 pl-4">
          {failure.evidence?.map((line, index) => <li key={index}>{line}</li>)}
        </ul>
      )}
    </div>
  );
}

const ACTIVITY_LABELS = {
  calling: 'jobs.activityCalling',
  returned: 'jobs.activityReturned',
  failed: 'jobs.activityFailed',
  error: 'jobs.activityError',
} satisfies Record<string, I18nKey>;

export function JobActivity({ jobId, announce = false }: { jobId: string; announce?: boolean }) {
  const activity = useAppStore((state) => state.jobActivity[jobId]);
  const { t } = useI18n();
  const label = activity
    ? t(ACTIVITY_LABELS[activity.kind], 'toolName' in activity ? { tool: activity.toolName } : undefined)
    : t('jobs.activityStarting');

  return (
    <div className="flex items-start gap-1.5 text-[11px] leading-relaxed text-slate-300"
         aria-live={announce ? 'polite' : undefined}
         aria-atomic={announce ? true : undefined}>
      <Loader2 aria-hidden="true" className="mt-0.5 h-3 w-3 shrink-0 text-indigo-400 motion-safe:animate-spin" />
      <span className="min-w-0 break-words">
        {label}
        {activity && (
          <time className="ml-2 text-slate-400" dateTime={new Date(activity.observedAt).toISOString()}>
            {new Date(activity.observedAt).toLocaleTimeString()}
          </time>
        )}
      </span>
    </div>
  );
}
