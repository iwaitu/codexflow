import React, { useEffect, useRef, useState } from 'react';
import { CheckCircle2, Clock, Monitor, ShieldAlert, XCircle } from 'lucide-react';
import type { ApprovalDecisionWire, ComputerApprovalRequestDto } from '@shared/ipc';

interface ComputerApprovalCardProps {
  approval: ComputerApprovalRequestDto;
  controlPlaneAvailable: boolean;
  onResolved: (approvalId: string) => void;
}

export const ComputerApprovalCard: React.FC<ComputerApprovalCardProps> = ({
  approval,
  controlPlaneAvailable,
  onResolved,
}) => {
  const [note, setNote] = useState('');
  const [busyDecision, setBusyDecision] = useState<ApprovalDecisionWire | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [nowMs, setNowMs] = useState(() => Date.now());
  const submittingRef = useRef(false);

  const approvalId = approval.approvalId ?? '';
  const requestDigest = approval.requestDigest ?? '';
  const allowedDecisions = approval.allowedDecisions ?? ['allow_once', 'deny'];
  const allowSession = allowedDecisions.includes('allow_session');

  useEffect(() => {
    if (!approval.expiresAtUtc) return undefined;
    const timer = window.setInterval(() => setNowMs(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [approval.expiresAtUtc]);

  const expiresMs = approval.expiresAtUtc ? Date.parse(approval.expiresAtUtc) : Number.NaN;
  const diffSec = Number.isNaN(expiresMs) ? null : Math.floor((expiresMs - nowMs) / 1000);
  const isExpired = diffSec !== null && diffSec <= 0;

  const remainingText = (() => {
    if (diffSec === null) return null;
    if (diffSec <= 0) return '审批已过期';
    const mins = Math.floor(diffSec / 60);
    const secs = diffSec % 60;
    return `剩余 ${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
  })();

  const handleDecision = async (decision: ApprovalDecisionWire) => {
    if (!window.codexflow || !approvalId || !requestDigest || isExpired || submittingRef.current) return;
    submittingRef.current = true;
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
        if (res.status === 409 || res.status === 404) {
          onResolved(approvalId);
          return;
        }
        setError(res.errorMessage || `提交桌面操控审批失败 (${res.status})`);
        return;
      }
      onResolved(approvalId);
    } catch (err) {
      setError(err instanceof Error ? err.message : '提交桌面操控审批失败');
    } finally {
      submittingRef.current = false;
      setBusyDecision(null);
    }
  };

  const hasCoords = approval.targetX !== null && approval.targetX !== undefined &&
    approval.targetY !== null && approval.targetY !== undefined;
  const previewSrc = approval.previewImageBase64
    ? `data:image/png;base64,${approval.previewImageBase64}`
    : null;
  const actionDisabled = !controlPlaneAvailable || busyDecision !== null || isExpired;

  return (
    <div
      data-testid={`computer-approval-card-${approvalId}`}
      className="space-y-3 rounded-xl border border-rose-500/50 bg-rose-950/20 p-4 text-xs shadow-lg"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2 font-semibold text-rose-200">
          <ShieldAlert className="h-4 w-4 text-rose-400" />
          <span>桌面键鼠操控安全确认（Computer Use）</span>
        </div>
        {remainingText && (
          <span
            className={`inline-flex items-center gap-1 font-mono text-[11px] ${
              isExpired ? 'text-rose-300' : 'text-amber-300/90'
            }`}
          >
            <Clock className="h-3 w-3" />
            <span>{remainingText}</span>
          </span>
        )}
      </div>

      <div className="space-y-1.5 rounded-lg border border-slate-800 bg-slate-950/75 p-3">
        <div className="flex items-center gap-1.5 text-slate-200">
          <Monitor className="h-3.5 w-3.5 shrink-0 text-rose-400" />
          <span className="font-mono text-[11px] font-semibold text-slate-100">
            {approval.targetProcessName || '未知进程'}
          </span>
          {approval.targetWindowTitle && (
            <span className="truncate text-[11px] text-slate-400" title={approval.targetWindowTitle}>
              · {approval.targetWindowTitle}
            </span>
          )}
        </div>
        <div className="text-slate-200">
          <span className="text-slate-400">待执行动作：</span>
          <span className="font-medium" data-testid="computer-approval-action-summary">
            {approval.actionSummary || `${approval.tool} · ${approval.action}`}
          </span>
        </div>
        {hasCoords && (
          <div className="text-[11px] text-slate-300">
            <span className="text-slate-400">屏幕落点坐标：</span>
            <span className="font-mono text-amber-200" data-testid="computer-approval-coords">
              ({approval.targetX}, {approval.targetY})
            </span>
          </div>
        )}
        {approval.riskReason && (
          <div className="text-amber-200/90">
            <span className="text-slate-400">风险提示：</span>
            <span>{approval.riskReason}</span>
          </div>
        )}
        {previewSrc && (
          <div className="pt-1.5">
            <div className="mb-1 text-[11px] text-slate-400">目标区域红圈落点预览（等待确认期间已冻结桌面变更检测）：</div>
            <img
              src={previewSrc}
              alt="待确认点击位置预览"
              data-testid="computer-approval-preview"
              className="max-h-40 rounded-md border border-rose-500/40 object-contain bg-slate-900"
            />
          </div>
        )}
      </div>

      {!controlPlaneAvailable && (
        <div className="rounded-lg border border-rose-500/40 bg-rose-950/40 px-3 py-2 text-rose-200">
          当前处于未启用控制面的独立运行模式，无法批准桌面键鼠操控请求。请切换回托管模式后重试。
        </div>
      )}

      {error && (
        <div className="rounded-lg border border-rose-500/40 bg-rose-950/40 px-3 py-2 text-rose-200">
          {error}
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2">
        <input
          type="text"
          value={note}
          disabled={isExpired}
          onChange={(e) => setNote(e.target.value)}
          placeholder="可选：审批备注"
          className="min-w-[160px] flex-1 rounded-lg border border-slate-700 bg-slate-900 px-2.5 py-1.5 text-xs text-slate-100 placeholder:text-slate-500 focus:border-rose-500 focus:outline-none disabled:opacity-40"
        />

        <button
          type="button"
          disabled={actionDisabled}
          onClick={() => void handleDecision('allow_once')}
          className="inline-flex items-center gap-1 rounded-lg bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white transition-colors hover:bg-emerald-500 disabled:opacity-40"
        >
          <CheckCircle2 className="h-3.5 w-3.5" />
          <span>{busyDecision === 'allow_once' ? '提交中…' : '允许一次'}</span>
        </button>

        {allowSession && (
          <button
            type="button"
            disabled={actionDisabled}
            onClick={() => void handleDecision('allow_session')}
            className="inline-flex items-center gap-1 rounded-lg border border-sky-500/60 bg-sky-900/50 px-3 py-1.5 text-xs font-medium text-sky-100 transition-colors hover:bg-sky-800/60 disabled:opacity-40"
          >
            <span>{busyDecision === 'allow_session' ? '提交中…' : '本会话允许此应用'}</span>
          </button>
        )}

        <button
          type="button"
          disabled={actionDisabled}
          onClick={() => void handleDecision('deny')}
          className="inline-flex items-center gap-1 rounded-lg border border-rose-500/50 bg-rose-950/50 px-3 py-1.5 text-xs font-medium text-rose-200 transition-colors hover:bg-rose-900/60 disabled:opacity-40"
        >
          <XCircle className="h-3.5 w-3.5" />
          <span>{busyDecision === 'deny' ? '提交中…' : '拒绝'}</span>
        </button>
      </div>
    </div>
  );
};
