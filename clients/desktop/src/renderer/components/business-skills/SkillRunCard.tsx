import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  AlertTriangle,
  CheckCircle2,
  Clock3,
  RefreshCw,
  ShieldAlert,
  Square,
  XCircle,
} from 'lucide-react';
import type { SkillRunDto } from '@shared/ipc';
import { apiClient, type ApiAuthContext } from '../../lib/apiClient';
import {
  extractControlPlaneError,
  formatBusinessSkillError,
  getRunStatusMeta,
  getStepStateBadgeClass,
  isNonTerminalRunStatus,
} from './businessSkillHelpers';

interface SkillRunCardProps {
  auth: ApiAuthContext | null;
  runId?: string | null;
  initialRun?: SkillRunDto | null;
  controlPlaneAvailable: boolean;
  onRunUpdated?: (run: SkillRunDto) => void;
}

export const SkillRunCard: React.FC<SkillRunCardProps> = ({
  auth,
  runId,
  initialRun = null,
  controlPlaneAvailable,
  onRunUpdated,
}) => {
  const [run, setRun] = useState<SkillRunDto | null>(initialRun);
  const [loading, setLoading] = useState(false);
  const [actionBusy, setActionBusy] = useState<'allow_once' | 'deny' | 'cancel' | null>(null);
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [confirmStopOpen, setConfirmStopOpen] = useState(false);
  const [pollFailureCount, setPollFailureCount] = useState(0);
  const onRunUpdatedRef = useRef(onRunUpdated);
  onRunUpdatedRef.current = onRunUpdated;
  const mountedRef = useRef(true);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  useEffect(() => {
    if (initialRun) {
      setRun(initialRun);
    }
  }, [initialRun]);

  const effectiveRunId = runId ?? run?.runId ?? null;

  const refreshRun = useCallback(async () => {
    if (!auth || !effectiveRunId) return;
    setLoading(true);
    try {
      const latest = await apiClient.getSkillRun(auth, effectiveRunId);
      if (!mountedRef.current) return;
      setRun(latest);
      setError(null);
      setPollFailureCount(0);
      onRunUpdatedRef.current?.(latest);
    } catch (err) {
      if (!mountedRef.current) return;
      setPollFailureCount((c) => c + 1);
      setError(formatBusinessSkillError(err, '获取运行状态失败'));
    } finally {
      if (mountedRef.current) {
        setLoading(false);
      }
    }
  }, [auth, effectiveRunId]);

  useEffect(() => {
    if (!auth || !effectiveRunId) return;
    if (!run || run.runId !== effectiveRunId) {
      void refreshRun();
    }
  }, [auth, effectiveRunId, run, refreshRun]);

  // 仅在运行处于非终态且未连续连接失败时按短周期轮询运行快照
  useEffect(() => {
    if (!auth || !effectiveRunId || !isNonTerminalRunStatus(run?.status) || pollFailureCount >= 3) {
      return undefined;
    }
    const timer = window.setInterval(() => {
      void refreshRun();
    }, 1500);
    return () => {
      window.clearInterval(timer);
    };
  }, [auth, effectiveRunId, run?.status, pollFailureCount, refreshRun]);

  const handleApproval = async (decision: 'allow_once' | 'deny') => {
    const approval = run?.pendingApproval;
    if (!approval?.approvalId || !approval.requestDigest || !window.codexflow) {
      return;
    }
    setActionBusy(decision);
    setError(null);
    try {
      const res = await window.codexflow.approveToolExecution({
        approvalId: approval.approvalId,
        requestDigest: approval.requestDigest,
        decision,
        note: note.trim() || undefined,
      });
      if (!res.ok) {
        setError(extractControlPlaneError(res, '提交审批失败'));
        return;
      }
      setNote('');
      await refreshRun();
    } catch (err) {
      setError(formatBusinessSkillError(err, '提交审批失败'));
    } finally {
      setActionBusy(null);
    }
  };

  const handleCancelRun = async () => {
    if (!effectiveRunId || !window.codexflow) return;
    setActionBusy('cancel');
    setError(null);
    try {
      const res = await window.codexflow.cancelBusinessSkillRun(effectiveRunId);
      if (!res.ok) {
        setError(extractControlPlaneError(res, '停止运行失败'));
        return;
      }
      if (res.data) {
        setRun(res.data);
        onRunUpdated?.(res.data);
      }
    } catch (err) {
      setError(formatBusinessSkillError(err, '停止运行失败'));
    } finally {
      setActionBusy(null);
    }
  };

  if (!run) {
    return effectiveRunId ? (
      <div className="rounded-xl border border-slate-800 bg-slate-900/70 p-4 text-xs text-slate-400">
        正在加载 Skill 运行状态 ({effectiveRunId})...
      </div>
    ) : null;
  }

  const statusMeta = getRunStatusMeta(run.status);
  const steps = run.steps ?? [];
  const pendingApproval = run.pendingApproval;
  const approvalSummary = pendingApproval?.summary;
  const previews = approvalSummary?.requestPreviews ?? [];
  const canStop = isNonTerminalRunStatus(run.status) && controlPlaneAvailable;

  return (
    <div
      data-testid="skill-run-card"
      className="space-y-3 rounded-xl border border-slate-800 bg-slate-900/80 p-4"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <span className={`rounded-full border px-2.5 py-0.5 text-xs font-medium ${statusMeta.badgeClass}`}>
            {statusMeta.label}
          </span>
          {run.isTestRun && (
            <span className="rounded-full border border-sky-500/30 bg-sky-500/10 px-2 py-0.5 text-[11px] text-sky-300">
              试运行
            </span>
          )}
          <span className="text-xs text-slate-400">Run ID: {run.runId}</span>
        </div>

        <div className="flex items-center gap-2">
          <span className="text-[11px] text-slate-400">
            外部请求 {run.externalRequestCount ?? 0} 次 · 只读重试 {run.readOnlyRetryCount ?? 0} 次
          </span>
          <button
            type="button"
            onClick={() => {
              setPollFailureCount(0);
              void refreshRun();
            }}
            disabled={loading}
            className="inline-flex items-center gap-1 rounded-lg border border-slate-700 bg-slate-800 px-2.5 py-1 text-xs text-slate-200 hover:bg-slate-700 disabled:opacity-50"
          >
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
            刷新
          </button>
          {isNonTerminalRunStatus(run.status) && (
            <button
              type="button"
              onClick={() => setConfirmStopOpen(true)}
              disabled={!canStop || actionBusy !== null}
              title={controlPlaneAvailable ? '停止后续步骤分发' : '控制面不可用时无法停止'}
              className="inline-flex items-center gap-1 rounded-lg border border-rose-500/40 bg-rose-500/10 px-2.5 py-1 text-xs font-medium text-rose-200 hover:bg-rose-500/20 disabled:cursor-not-allowed disabled:opacity-50"
            >
              <Square className="h-3 w-3" />
              停止后续操作
            </button>
          )}
        </div>
      </div>

      {confirmStopOpen && (
        <div
          role="alertdialog"
          aria-label="确认停止 Skill 运行"
          className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-rose-500/40 bg-rose-950/30 p-2.5 text-xs text-rose-200"
        >
          <span>确认停止后续步骤吗？若外部写入请求已发出，状态可能标记为待人工核对结果。</span>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => {
                setConfirmStopOpen(false);
                void handleCancelRun();
              }}
              className="rounded bg-rose-600 px-2.5 py-1 text-[11px] font-medium text-white hover:bg-rose-500"
            >
              确认停止
            </button>
            <button
              type="button"
              onClick={() => setConfirmStopOpen(false)}
              className="rounded border border-slate-700 px-2.5 py-1 text-[11px] text-slate-300 hover:bg-slate-800"
            >
              取消
            </button>
          </div>
        </div>
      )}

      {pollFailureCount >= 3 && (
        <div className="rounded-lg border border-amber-500/40 bg-amber-950/30 px-3 py-1.5 text-xs text-amber-200">
          连续获取状态失败，已暂停自动刷新，请确认连接正常后点击右上角“刷新”。
        </div>
      )}

      {run.status === 'OutcomeUnknown' && (
        <div className="flex items-start gap-2 rounded-lg border border-orange-500/40 bg-orange-950/30 p-3 text-xs text-orange-200">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-orange-300" />
          <div>
            外部写请求已发出但未收到确定回执（例如超时、进程中断或人工点击停止）。为避免重复写入，系统不会自动重放该步，请先人工核对目标业务系统后再决定是否补跑。
          </div>
        </div>
      )}

      {run.status === 'WaitingForApproval' && pendingApproval && (
        <div
          data-testid="business-write-approval-card"
          className="space-y-3 rounded-xl border border-amber-500/40 bg-amber-950/20 p-4"
        >
          <div className="flex items-center gap-2 text-sm font-semibold text-amber-200">
            <ShieldAlert className="h-4 w-4 text-amber-400" />
            业务写入确认（仅本次批次有效）
          </div>

          {approvalSummary?.writeStepUnverified && (
            <div
              data-testid="write-step-unverified-banner"
              className="flex items-start gap-2 rounded-lg border border-amber-400/50 bg-amber-500/15 px-3 py-2 text-xs text-amber-100"
            >
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-300" />
              <span>
                WriteStepUnverified：当前写入步骤尚未通过完整写入试运行验证，请逐条核对以下即将发送的真实请求摘要。
              </span>
            </div>
          )}

          <dl className="grid grid-cols-2 gap-x-4 gap-y-2 rounded-lg border border-slate-800 bg-slate-950/70 p-3 text-xs md:grid-cols-3">
            <div>
              <dt className="text-slate-400">目标系统</dt>
              <dd className="font-medium text-slate-100">{approvalSummary?.systemName || '-'}</dd>
            </div>
            <div>
              <dt className="text-slate-400">运行环境</dt>
              <dd className="font-medium text-slate-100">{approvalSummary?.environment || '-'}</dd>
            </div>
            <div>
              <dt className="text-slate-400">批量条数</dt>
              <dd className="font-medium text-slate-100">{approvalSummary?.batchCount ?? 1}</dd>
            </div>
            <div>
              <dt className="text-slate-400">计划操作</dt>
              <dd className="font-medium text-slate-100">{approvalSummary?.plannedMutation || '-'}</dd>
            </div>
            <div>
              <dt className="text-slate-400">影响对象摘要</dt>
              <dd className="font-medium text-slate-100">
                {approvalSummary?.targetRecordSummary || '-'}
              </dd>
            </div>
            <div>
              <dt className="text-slate-400">涉及关键金额 / 失效时间</dt>
              <dd className="font-medium text-slate-100">
                {approvalSummary?.keyAmount !== undefined && approvalSummary?.keyAmount !== null
                  ? `${approvalSummary.keyAmount} ${approvalSummary.currency ?? ''}`.trim()
                  : '无金额字段'}
                {pendingApproval.expiresAtUtc
                  ? ` · 截止 ${new Date(pendingApproval.expiresAtUtc).toLocaleTimeString()}`
                  : ''}
              </dd>
            </div>
          </dl>

          {previews.length > 0 && (
            <div className="space-y-1.5">
              <div className="text-xs font-medium text-slate-300">脱敏请求预览</div>
              <div className="max-h-48 space-y-1.5 overflow-y-auto">
                {previews.map((preview, idx) => (
                  <div
                    key={`${preview.capabilityId}-${idx}`}
                    className="rounded-lg border border-slate-800 bg-slate-950/90 p-2 font-mono text-[11px] text-slate-200"
                  >
                    <div className="text-amber-300">
                      {preview.methodOrTool} {preview.targetPath} ({preview.capabilityId})
                    </div>
                    <pre className="mt-1 whitespace-pre-wrap break-all text-slate-400">
                      {preview.redactedBodyPreview}
                    </pre>
                  </div>
                ))}
              </div>
            </div>
          )}

          {!controlPlaneAvailable && (
            <div className="rounded-lg border border-rose-500/40 bg-rose-950/30 px-3 py-2 text-xs text-rose-200">
              当前桌面客户端处于只读模式（controlToken 不可用），无法提交写入审批决策。
            </div>
          )}

          <div className="flex flex-wrap items-center gap-2">
            <input
              type="text"
              value={note}
              onChange={(e) => setNote(e.target.value)}
              placeholder="可选审批备注（审计留痕）"
              className="min-w-[200px] flex-1 rounded-lg border border-slate-700 bg-slate-950 px-3 py-1.5 text-xs text-slate-100 placeholder:text-slate-500 focus:border-amber-400 focus:outline-none"
            />
            <button
              type="button"
              onClick={() => void handleApproval('allow_once')}
              disabled={!controlPlaneAvailable || actionBusy !== null}
              className="inline-flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-amber-500 disabled:cursor-not-allowed disabled:opacity-50"
            >
              <CheckCircle2 className="h-3.5 w-3.5" />
              {actionBusy === 'allow_once' ? '提交中...' : '仅本次批次允许'}
            </button>
            <button
              type="button"
              onClick={() => void handleApproval('deny')}
              disabled={!controlPlaneAvailable || actionBusy !== null}
              className="inline-flex items-center gap-1.5 rounded-lg border border-rose-500/50 bg-rose-500/15 px-3 py-1.5 text-xs font-medium text-rose-200 hover:bg-rose-500/25 disabled:cursor-not-allowed disabled:opacity-50"
            >
              <XCircle className="h-3.5 w-3.5" />
              {actionBusy === 'deny' ? '提交中...' : '拒绝执行'}
            </button>
          </div>
        </div>
      )}

      {run.error && (
        <div className="rounded-lg border border-rose-500/40 bg-rose-950/30 p-3 text-xs text-rose-200">
          <div className="font-medium">
            {run.error.code}: {run.error.message}
          </div>
          {run.error.fieldErrors?.length > 0 && (
            <ul className="mt-1 list-disc space-y-0.5 pl-4 text-rose-300">
              {run.error.fieldErrors.map((fe, idx) => (
                <li key={`${fe.field}-${idx}`}>
                  {fe.field}: {fe.message}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {error && (
        <div className="rounded-lg border border-rose-500/40 bg-rose-950/30 px-3 py-2 text-xs text-rose-200">
          {error}
        </div>
      )}

      {steps.length > 0 && (
        <div className="space-y-1.5">
          <div className="text-xs font-medium text-slate-300">步骤检查点时间线</div>
          <div className="space-y-1.5">
            {steps.map((step, idx) => (
              <div
                key={`${step.stepId ?? 'step'}-${idx}`}
                className="flex flex-wrap items-start justify-between gap-2 rounded-lg border border-slate-800/90 bg-slate-950/60 px-3 py-2 text-xs"
              >
                <div className="space-y-0.5">
                  <div className="flex items-center gap-2">
                    <Clock3 className="h-3.5 w-3.5 text-slate-400" />
                    <span className="font-medium text-slate-100">{step.stepId || `步骤 ${idx + 1}`}</span>
                    <span className="rounded bg-slate-800 px-1.5 py-0.5 text-[10px] text-slate-300">
                      {step.stepType || 'step'}
                    </span>
                  </div>
                  {(step.requestSummary || step.responseSummary || step.evidence) && (
                    <div className="text-[11px] text-slate-400">
                      {step.requestSummary || step.responseSummary || step.evidence}
                    </div>
                  )}
                </div>
                <div className={`text-xs font-medium ${getStepStateBadgeClass(step.state)}`}>
                  {step.state}
                  {step.errorCode ? ` (${step.errorCode})` : ''}
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {run.outputPayload !== undefined && run.outputPayload !== null && (
        <div className="space-y-1">
          <div className="text-xs font-medium text-slate-300">结果输出预览</div>
          <pre className="max-h-64 overflow-auto rounded-lg border border-slate-800 bg-slate-950 p-3 font-mono text-[11px] text-emerald-200">
            {JSON.stringify(run.outputPayload, null, 2)}
          </pre>
        </div>
      )}
    </div>
  );
};
