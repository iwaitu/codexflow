import React, { useEffect, useRef, useState } from 'react';
import { CheckCircle2, Clock, Globe, ShieldAlert, XCircle } from 'lucide-react';
import type { ApprovalDecisionWire, BrowserApprovalRequestDto } from '@shared/ipc';

interface BrowserApprovalCardProps {
  approval: BrowserApprovalRequestDto;
  controlPlaneAvailable: boolean;
  onResolved: (approvalId: string) => void;
}

export const BrowserApprovalCard: React.FC<BrowserApprovalCardProps> = ({
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
        setError(res.errorMessage || `提交浏览器审批失败 (${res.status})`);
        return;
      }
      onResolved(approvalId);
    } catch (err) {
      setError(err instanceof Error ? err.message : '提交浏览器审批失败');
    } finally {
      submittingRef.current = false;
      setBusyDecision(null);
    }
  };

  const displayOrigin = approval.targetOrigin || approval.targetUrl || '-';
  const showFullUrl =
    Boolean(approval.targetUrl) && approval.targetUrl !== approval.targetOrigin;
  const actionDisabled = !controlPlaneAvailable || busyDecision !== null || isExpired;

  return (
    <div
      data-testid={`browser-approval-card-${approvalId}`}
      className="space-y-3 rounded-xl border border-sky-500/50 bg-sky-950/25 p-4 text-xs shadow-lg"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2 font-semibold text-sky-200">
          <ShieldAlert className="h-4 w-4 text-sky-400" />
          <span>浏览器安全操作确认</span>
        </div>
        {remainingText && (
          <span
            className={`inline-flex items-center gap-1 font-mono text-[11px] ${
              isExpired ? 'text-rose-300' : 'text-sky-300/80'
            }`}
          >
            <Clock className="h-3 w-3" />
            <span>{remainingText}</span>
          </span>
        )}
      </div>

      <div className="space-y-1.5 rounded-lg border border-slate-800 bg-slate-950/70 p-3">
        <div className="flex items-center gap-1.5 text-slate-300">
          <Globe className="h-3.5 w-3.5 shrink-0 text-sky-400" />
          <span className="font-mono text-[11px] text-slate-100 break-all">
            {displayOrigin}
          </span>
        </div>
        {showFullUrl && (
          <div className="text-[11px] text-slate-300 break-all">
            <span className="text-slate-400">目标 URL：</span>
            <span className="font-mono text-slate-200" data-testid="browser-approval-target-url">
              {approval.targetUrl}
            </span>
          </div>
        )}
        <div className="text-slate-200">
          <span className="text-slate-400">待执行动作：</span>
          <span className="font-medium">{approval.targetDescription || `${approval.tool} · ${approval.action}`}</span>
        </div>
        {approval.expressionOrPath && (
          <div className="text-[11px] text-slate-300 break-all">
            <span className="text-slate-400">操作详情：</span>
            <span className="font-mono text-slate-200" data-testid="browser-approval-expression">
              {approval.expressionOrPath}
            </span>
          </div>
        )}
        {approval.riskReason && (
          <div className="text-amber-200/90">
            <span className="text-slate-400">风险提示：</span>
            <span>{approval.riskReason}</span>
          </div>
        )}
      </div>

      {!controlPlaneAvailable && (
        <div className="rounded-lg border border-rose-500/40 bg-rose-950/40 px-3 py-2 text-rose-200">
          当前处于未启用控制面的独立运行模式，无法批准高风险浏览器操作。请切换回托管模式后重试。
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
          className="min-w-[160px] flex-1 rounded-lg border border-slate-700 bg-slate-900 px-2.5 py-1.5 text-xs text-slate-100 placeholder:text-slate-500 focus:border-sky-500 focus:outline-none disabled:opacity-40"
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
            <span>{busyDecision === 'allow_session' ? '提交中…' : '本会话允许'}</span>
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
