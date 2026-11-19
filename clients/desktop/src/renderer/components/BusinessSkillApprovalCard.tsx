import React, { useState } from 'react';
import { AlertTriangle, CheckCircle2, ShieldAlert, XCircle } from 'lucide-react';
import type { BusinessApprovalRequestDto } from '@shared/ipc';
import { extractControlPlaneError, formatBusinessSkillError } from './business-skills/businessSkillHelpers';

interface BusinessSkillApprovalCardProps {
  approval: BusinessApprovalRequestDto;
  controlPlaneAvailable: boolean;
  onResolved: (approvalId: string) => void;
}

/**
 * 聊天区专用的企业业务 Skill 单次写入批准确认卡。
 * 与脚本执行的 ToolApprovalCard 物理隔离，仅允许 allow_once / deny，不提供 allow_session / allow_always。
 */
export const BusinessSkillApprovalCard: React.FC<BusinessSkillApprovalCardProps> = ({
  approval,
  controlPlaneAvailable,
  onResolved,
}) => {
  const [note, setNote] = useState('');
  const [busyDecision, setBusyDecision] = useState<'allow_once' | 'deny' | null>(null);
  const [error, setError] = useState<string | null>(null);

  const summary = approval.summary;
  const previews = summary?.requestPreviews ?? [];
  const approvalId = approval.approvalId ?? '';
  const requestDigest = approval.requestDigest ?? '';

  const handleDecision = async (decision: 'allow_once' | 'deny') => {
    if (!window.codexflow || !approvalId || !requestDigest) return;
    setBusyDecision(decision);
    setError(null);
    try {
      const res = await window.codexflow.approveToolExecution({
        approvalId,
        requestDigest,
        decision,
        note: note.trim() || undefined,
      });
      if (!res.ok) {
        setError(extractControlPlaneError(res, '提交业务写入审批失败'));
        return;
      }
      onResolved(approvalId);
    } catch (err) {
      setError(formatBusinessSkillError(err, '提交业务写入审批失败'));
    } finally {
      setBusyDecision(null);
    }
  };

  return (
    <div
      data-testid={`business-approval-card-${approvalId}`}
      className="space-y-3 rounded-xl border border-amber-500/50 bg-amber-950/25 p-4 text-xs shadow-lg"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2 font-semibold text-amber-200">
          <ShieldAlert className="h-4 w-4 text-amber-400" />
          <span>业务 Skill 外部写入单次批准确认（仅允许单次批准或拒绝）</span>
        </div>
        <span className="font-mono text-[11px] text-amber-300/80">
          Run {approval.runId ?? '-'} · Step {approval.stepId ?? '-'}
        </span>
      </div>

      {summary?.writeStepUnverified && (
        <div className="flex items-start gap-2 rounded-lg border border-amber-400/50 bg-amber-500/15 p-2.5 text-amber-100">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-300" />
          <div>
            该 Skill 的写入步骤在发布前尚未经过真实试运行验证，请仔细核对下方脱敏请求预览后再决定是否放行。
          </div>
        </div>
      )}

      {summary ? (
        <dl className="grid grid-cols-2 gap-x-4 gap-y-2 rounded-lg border border-slate-800 bg-slate-950/70 p-3 md:grid-cols-3">
          <div>
            <dt className="text-slate-400">目标系统</dt>
            <dd className="font-medium text-slate-100">{summary.systemName || '-'}</dd>
          </div>
          <div>
            <dt className="text-slate-400">运行环境</dt>
            <dd className="font-medium text-slate-100">{summary.environment || '-'}</dd>
          </div>
          <div>
            <dt className="text-slate-400">批量条数</dt>
            <dd className="font-medium text-slate-100">{summary.batchCount ?? 1}</dd>
          </div>
          <div>
            <dt className="text-slate-400">计划操作</dt>
            <dd className="font-medium text-slate-100">{summary.plannedMutation || '-'}</dd>
          </div>
          <div>
            <dt className="text-slate-400">影响对象摘要</dt>
            <dd className="font-medium text-slate-100">{summary.targetRecordSummary || '-'}</dd>
          </div>
          <div>
            <dt className="text-slate-400">关键金额 / 截止时间</dt>
            <dd className="font-medium text-slate-100">
              {summary.keyAmount !== undefined && summary.keyAmount !== null
                ? `${summary.keyAmount} ${summary.currency ?? ''}`.trim()
                : '无金额字段'}
              {approval.expiresAtUtc
                ? ` · 截止 ${new Date(approval.expiresAtUtc).toLocaleTimeString()}`
                : ''}
            </dd>
          </div>
        </dl>
      ) : (
        <div className="rounded-lg border border-slate-800 bg-slate-950/70 px-3 py-2 text-slate-300">
          正在加载写入摘要详情（审批 ID：{approvalId || '-'}）…
        </div>
      )}

      {previews.length > 0 && (
        <div className="space-y-1.5">
          <div className="font-medium text-slate-300">脱敏请求预览</div>
          <div className="max-h-40 space-y-1.5 overflow-y-auto">
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
        <div className="rounded-lg border border-rose-500/40 bg-rose-950/30 px-3 py-2 text-rose-200">
          当前桌面客户端处于只读模式（controlToken 不可用），无法提交写入审批决策。
        </div>
      )}

      {error && (
        <div className="rounded-lg border border-rose-500/40 bg-rose-950/30 px-3 py-2 text-rose-200">
          {error}
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2">
        <input
          type="text"
          value={note}
          onChange={(e) => setNote(e.target.value)}
          placeholder="可选审批备注（审计留痕）"
          className="min-w-[180px] flex-1 rounded-lg border border-slate-700 bg-slate-950 px-3 py-1.5 text-xs text-slate-100 placeholder:text-slate-500 focus:border-amber-400 focus:outline-none"
        />
        <button
          type="button"
          onClick={() => void handleDecision('allow_once')}
          disabled={!controlPlaneAvailable || busyDecision !== null || !summary}
          className="inline-flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 font-medium text-white hover:bg-amber-500 disabled:cursor-not-allowed disabled:opacity-50"
        >
          <CheckCircle2 className="h-3.5 w-3.5" />
          {busyDecision === 'allow_once' ? '提交中...' : '仅本次批次允许'}
        </button>
        <button
          type="button"
          onClick={() => void handleDecision('deny')}
          disabled={!controlPlaneAvailable || busyDecision !== null}
          className="inline-flex items-center gap-1.5 rounded-lg border border-rose-500/50 bg-rose-500/15 px-3 py-1.5 font-medium text-rose-200 hover:bg-rose-500/25 disabled:cursor-not-allowed disabled:opacity-50"
        >
          <XCircle className="h-3.5 w-3.5" />
          {busyDecision === 'deny' ? '提交中...' : '拒绝执行'}
        </button>
      </div>
    </div>
  );
};
