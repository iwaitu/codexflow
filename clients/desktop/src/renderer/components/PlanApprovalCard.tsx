import React, { useState } from 'react';
import { Check, FileText, Play, X } from 'lucide-react';
import type { SessionPlan } from '../lib/apiClient';
import { MarkdownRenderer } from './MarkdownRenderer';
import { VerificationBadge } from './VerificationBadge';

interface PlanApprovalCardProps {
  sessionId: string;
  conversationId: string | null;
  plan: SessionPlan;
  executionBackend: string | null;
  controlPlaneAvailable: boolean;
  onSubmitted: () => void;
  onRequestSwitchManaged?: () => void;
}

export const PlanApprovalCard: React.FC<PlanApprovalCardProps> = ({
  sessionId,
  conversationId,
  plan,
  executionBackend,
  controlPlaneAvailable,
  onSubmitted,
  onRequestSwitchManaged,
}) => {
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const isActionable = plan.status === 'ReadyForApproval' || plan.status === 'Approved';
  const isLocal = executionBackend === 'Local';

  const submitAction = async (
    action: 'approve' | 'reject' | 'execute' | 'planOnly',
    startExecution = true,
  ) => {
    setSubmitting(true);
    setError(null);
    const labelMap = {
      approve: startExecution ? '批准并开始执行计划。' : '仅批准计划，暂不执行。',
      execute: '开始执行已批准计划。',
      reject: '驳回当前计划，请重新调整方案。',
      planOnly: '仅保留计划，不执行。',
    };
    try {
      const res = await window.codexflow.submitPlanChatAction({
        sessionId,
        conversationId: conversationId ?? undefined,
        message: labelMap[action],
        structuredAction: {
          action,
          planArtifactId: plan.planArtifactId,
          planVersion: plan.version,
          planHash: plan.markdownHash,
          idempotencyKey: `${plan.planArtifactId}-${plan.version}-${action}-${Date.now()}`,
          startExecution,
        },
      });
      if (!res.ok) {
        setError(res.errorMessage || `提交计划操作失败 (${res.status})`);
        return;
      }
      onSubmitted();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div
      data-testid="plan-approval-card"
      className="rounded-xl border border-indigo-500/40 bg-indigo-950/20 p-4 shadow-lg"
    >
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-indigo-500/20 pb-3">
        <div className="flex items-center gap-2">
          <FileText className="h-4 w-4 text-indigo-400" />
          <span className="text-sm font-semibold text-indigo-100">执行计划</span>
          <span className="rounded bg-indigo-500/20 px-2 py-0.5 text-xs text-indigo-200">{plan.status}</span>
          {isLocal && (plan.status === 'Completed' || plan.status === 'Executing') && (
            <VerificationBadge unverified notice="本地执行模式下未经过容器隔离验证" />
          )}
        </div>
        <span className="font-mono text-[11px] text-slate-400">
          v{plan.version} · {plan.markdownHash.slice(0, 10)}
        </span>
      </div>

      <div className="mt-3 max-h-80 overflow-y-auto pr-1">
        <MarkdownRenderer content={plan.markdown} />
      </div>

      {plan.tasks.length > 0 && (
        <div className="mt-3 space-y-1 border-t border-indigo-500/20 pt-2">
          <div className="text-xs font-medium text-slate-300">任务列表 ({plan.tasks.length})</div>
          {plan.tasks.map((t) => (
            <div key={t.id} className="flex items-center justify-between rounded bg-slate-900/70 px-2.5 py-1.5 text-xs">
              <span className="truncate text-slate-200">
                <span className="mr-2 font-mono text-indigo-300">{t.id}</span>
                {t.title}
              </span>
              <span className="ml-2 shrink-0 rounded bg-slate-800 px-1.5 py-0.5 text-[11px] text-slate-300">
                {t.status}
              </span>
            </div>
          ))}
        </div>
      )}

      {error && <div className="mt-2 text-xs text-rose-300">{error}</div>}

      {isActionable && (
        <div className="mt-3 flex flex-wrap items-center gap-2 border-t border-indigo-500/20 pt-3">
          {plan.status === 'ReadyForApproval' && (
            <>
              <button
                type="button"
                disabled={submitting || !controlPlaneAvailable}
                onClick={() => void submitAction('approve', true)}
                className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
              >
                <Play className="h-3.5 w-3.5" />
                批准并执行
              </button>
              <button
                type="button"
                disabled={submitting || !controlPlaneAvailable}
                onClick={() => void submitAction('approve', false)}
                className="inline-flex items-center gap-1.5 rounded-lg border border-slate-700 bg-slate-800 px-3 py-1.5 text-xs font-medium text-slate-200 hover:bg-slate-700 disabled:opacity-40"
              >
                <Check className="h-3.5 w-3.5" />
                仅批准
              </button>
              <button
                type="button"
                disabled={submitting}
                onClick={() => void submitAction('reject', false)}
                className="inline-flex items-center gap-1.5 rounded-lg border border-rose-500/40 bg-rose-500/10 px-3 py-1.5 text-xs font-medium text-rose-200 hover:bg-rose-500/20 disabled:opacity-40"
              >
                <X className="h-3.5 w-3.5" />
                驳回
              </button>
            </>
          )}
          {plan.status === 'Approved' && (
            <button
              type="button"
              disabled={submitting || !controlPlaneAvailable}
              onClick={() => void submitAction('execute', true)}
              className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
            >
              <Play className="h-3.5 w-3.5" />
              开始执行
            </button>
          )}
          {!controlPlaneAvailable && (
            <div className="mt-1 flex w-full flex-wrap items-center gap-2 rounded-lg border border-amber-500/30 bg-slate-950/60 px-2.5 py-1.5 text-[11px] text-amber-200">
              <span>当前处于只读连接（无控制面），无法批准或执行开发计划。</span>
              {onRequestSwitchManaged && (
                <button
                  type="button"
                  onClick={onRequestSwitchManaged}
                  className="rounded border border-amber-400/40 bg-amber-500/20 px-2 py-0.5 font-medium text-amber-100 hover:bg-amber-500/30"
                >
                  切换为托管模式
                </button>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
};
