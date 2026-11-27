import React, { useState } from 'react';
import {
  Ban,
  Cpu,
  Play,
  RefreshCw,
  RotateCcw,
  ShieldCheck,
} from 'lucide-react';
import { apiClient, type ApiAuthContext, type BackgroundJob, type VerificationReceipt } from '../lib/apiClient';
import { JobActivity, JobFailureDetails } from './JobActivity';
import { isUnverifiedWithoutIsolation, resolveJobVerificationKind, VerificationBadge } from './VerificationBadge';

interface JobsPanelProps {
  auth: ApiAuthContext | null;
  sessionId: string | null;
  jobs: BackgroundJob[];
  verificationReceipts: Record<string, VerificationReceipt>;
  executionBackend: string | null;
  onVerifyJob: (jobId: string) => Promise<void>;
  onJobsMutated: () => Promise<void>;
}

const CANCELLABLE_STATUSES = new Set(['Queued', 'Running', 'WaitingUser', 'Suspended']);

export const JobsPanel: React.FC<JobsPanelProps> = ({
  auth,
  sessionId,
  jobs,
  verificationReceipts,
  executionBackend,
  onVerifyJob,
  onJobsMutated,
}) => {
  const [busyJobId, setBusyJobId] = useState<string | null>(null);
  const [continueTargetJobId, setContinueTargetJobId] = useState<string | null>(null);
  const [continuePrompt, setContinuePrompt] = useState('');
  const [actionError, setActionError] = useState<string | null>(null);

  if (!sessionId) {
    return <div className="p-4 text-xs text-slate-500">请先选择一个工作区。</div>;
  }

  const runAction = async (jobId: string, fn: () => Promise<void>) => {
    setBusyJobId(jobId);
    setActionError(null);
    try {
      await fn();
      await onJobsMutated();
    } catch (error) {
      setActionError(error instanceof Error ? error.message : String(error));
    } finally {
      setBusyJobId(null);
    }
  };

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex items-center justify-between gap-2 border-b border-slate-800/80 px-3 py-2 text-xs">
        <div className="flex items-center gap-1.5 text-slate-300">
          <Cpu className="h-3.5 w-3.5 text-indigo-400" />
          <span>后台作业 ({jobs.length})</span>
        </div>
        <button
          type="button"
          onClick={() => void onJobsMutated()}
          title="刷新作业状态"
          aria-label="刷新作业状态"
          className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
        >
          <RefreshCw className="h-3.5 w-3.5" />
        </button>
      </div>

      {actionError && (
        <div className="mx-3 mt-2 rounded-lg border border-rose-500/40 bg-rose-950/30 px-2.5 py-2 text-[11px] text-rose-200">
          {actionError}
        </div>
      )}

      <div className="min-h-0 flex-1 space-y-2.5 overflow-y-auto p-3">
        {jobs.length === 0 ? (
          <div className="rounded-lg border border-dashed border-slate-800 px-3 py-4 text-xs text-slate-500">
            当前工作区暂无后台作业。在主对话批准计划并开始执行后，Worker 作业将在此展示。
          </div>
        ) : (
          jobs.map((job) => {
            const jobId = job.jobId ?? '';
            const status = job.status ?? 'Unknown';
            const receipt = jobId ? verificationReceipts[jobId] : undefined;
            const rawKind = resolveJobVerificationKind(job, receipt);
            const badgeKind =
              rawKind === 'unverified' && isUnverifiedWithoutIsolation(job, receipt, executionBackend)
                ? 'verified_unisolated'
                : rawKind;
            const numericProgress = Number(job.progress ?? 0);
            const showProgress = Number.isFinite(numericProgress) && numericProgress > 0 && numericProgress < 100;
            const isBusy = busyJobId === jobId;

            const canCancel = Boolean(auth && jobId && CANCELLABLE_STATUSES.has(status));
            const canResume = Boolean(auth && jobId && (job.waitingUser || job.recoveryNeeded || status === 'WaitingUser'));
            const canRetry = Boolean(auth && jobId && status === 'Failed' && job.failure?.retryable !== false);
            const canContinue = Boolean(
              auth && jobId && (status === 'Completed' || status === 'Failed' || status === 'Cancelled' || job.recoveryNeeded),
            );
            const canVerify = Boolean(jobId && status === 'Completed' && job.jobType === 'ForgeWorker');

            return (
              <div
                key={jobId || `${job.taskId}-${status}`}
                data-testid={`job-panel-item-${jobId || 'unknown'}`}
                className="rounded-xl border border-slate-800 bg-slate-900/70 p-3 text-xs"
              >
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div className="min-w-0">
                    <div className="truncate font-mono text-[11px] font-semibold text-slate-100">
                      {job.taskId || jobId || job.jobType}
                    </div>
                    <div className="text-[10px] text-slate-500">
                      {job.workerType || job.jobType}
                      {job.runnerId ? ` · ${job.runnerId}` : ''}
                    </div>
                  </div>
                  <span className="shrink-0 rounded bg-slate-800 px-2 py-0.5 text-[11px] font-medium text-slate-200">
                    {status}
                  </span>
                </div>

                {showProgress && (
                  <div className="mt-2 h-1.5 overflow-hidden rounded-full bg-slate-800">
                    <div
                      className="h-full bg-indigo-500 transition-all"
                      style={{ width: `${Math.min(100, Math.max(0, numericProgress))}%` }}
                    />
                  </div>
                )}

                {status === 'Running' ? (
                  <div className="mt-2">
                    <JobActivity jobId={jobId} />
                  </div>
                ) : (job.latestMessage || job.summary || job.waitingReason) && (
                  <p className="mt-2 text-[11px] leading-relaxed text-slate-300">
                    {job.waitingReason || job.latestMessage || job.summary}
                  </p>
                )}

                {job.failure && (
                  <div className="mt-2">
                    <JobFailureDetails failure={job.failure} />
                  </div>
                )}

                {receipt?.failureCode && (
                  <p className="mt-2 break-words text-[11px] text-rose-200">
                    验收失败 · {receipt.failureCode}
                    {receipt.message && receipt.message !== receipt.failureCode ? ` · ${receipt.message}` : ''}
                  </p>
                )}

                {job.resumeGuidance && (
                  <div className="mt-1.5 text-[11px] text-amber-300/90">恢复提示：{job.resumeGuidance}</div>
                )}

                {(status === 'Completed' || status === 'Failed' || Boolean(receipt)) && (
                  <div className="mt-2">
                    <VerificationBadge
                      kind={badgeKind}
                      notice={receipt?.verificationNotice || receipt?.message || job.verification?.summary}
                    />
                  </div>
                )}

                <div className="mt-2.5 flex flex-wrap items-center gap-1.5">
                  {canCancel && (
                    <button
                      type="button"
                      disabled={isBusy}
                      onClick={() => void runAction(jobId, async () => {
                        await apiClient.cancelJob(auth!, jobId);
                      })}
                      className="inline-flex items-center gap-1 rounded border border-rose-500/40 bg-rose-950/40 px-2 py-1 text-[11px] text-rose-200 hover:bg-rose-900/50 disabled:opacity-40"
                    >
                      <Ban className="h-3 w-3" />
                      取消作业
                    </button>
                  )}

                  {canResume && (
                    <button
                      type="button"
                      disabled={isBusy}
                      onClick={() => void runAction(jobId, async () => {
                        await apiClient.resumeJob(auth!, jobId, job.resumeToken);
                      })}
                      className="inline-flex items-center gap-1 rounded border border-indigo-500/40 bg-indigo-500/20 px-2 py-1 text-[11px] text-indigo-200 hover:bg-indigo-500/30 disabled:opacity-40"
                    >
                      <Play className="h-3 w-3" />
                      恢复执行
                    </button>
                  )}

                  {canRetry && (
                    <button
                      type="button"
                      disabled={isBusy}
                      onClick={() => void runAction(jobId, async () => {
                        await apiClient.retryJob(auth!, jobId);
                      })}
                      className="inline-flex items-center gap-1 rounded border border-amber-500/40 bg-amber-500/15 px-2 py-1 text-[11px] text-amber-200 hover:bg-amber-500/25 disabled:opacity-40"
                    >
                      <RotateCcw className="h-3 w-3" />
                      重试
                    </button>
                  )}

                  {canContinue && (
                    <button
                      type="button"
                      disabled={isBusy}
                      onClick={() => {
                        setContinueTargetJobId((prev) => (prev === jobId ? null : jobId));
                        setContinuePrompt('');
                      }}
                      className="inline-flex items-center gap-1 rounded border border-slate-700 bg-slate-800 px-2 py-1 text-[11px] text-slate-200 hover:bg-slate-700 disabled:opacity-40"
                    >
                      <Play className="h-3 w-3" />
                      继续执行
                    </button>
                  )}

                  {canVerify && (
                    <button
                      type="button"
                      disabled={isBusy}
                      onClick={() => void runAction(jobId, () => onVerifyJob(jobId))}
                      className="inline-flex items-center gap-1 rounded border border-slate-700 bg-slate-800 px-2 py-1 text-[11px] text-slate-200 hover:bg-slate-700 disabled:opacity-40"
                    >
                      <ShieldCheck className="h-3 w-3" />
                      核验产物
                    </button>
                  )}
                </div>

                {continueTargetJobId === jobId && (
                  <div className="mt-2.5 space-y-1.5 border-t border-slate-800 pt-2">
                    <textarea
                      rows={2}
                      value={continuePrompt}
                      onChange={(e) => setContinuePrompt(e.target.value)}
                      placeholder="可选：输入补充要求（留空则沿用默认继续指令）"
                      className="w-full rounded border border-slate-700 bg-slate-950 px-2 py-1 text-[11px] text-slate-100 placeholder:text-slate-500 focus:border-indigo-500 focus:outline-none"
                    />
                    <div className="flex justify-end gap-1.5">
                      <button
                        type="button"
                        onClick={() => setContinueTargetJobId(null)}
                        className="rounded px-2 py-1 text-[11px] text-slate-400 hover:bg-slate-800"
                      >
                        收起
                      </button>
                      <button
                        type="button"
                        disabled={isBusy}
                        onClick={() =>
                          void runAction(jobId, async () => {
                            await apiClient.continueJob(auth!, jobId, continuePrompt, job.resumeToken);
                            setContinueTargetJobId(null);
                            setContinuePrompt('');
                          })
                        }
                        className="rounded bg-indigo-600 px-2.5 py-1 text-[11px] font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
                      >
                        确认继续
                      </button>
                    </div>
                  </div>
                )}
              </div>
            );
          })
        )}
      </div>
    </div>
  );
};
