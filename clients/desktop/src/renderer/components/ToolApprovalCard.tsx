import React, { useState } from 'react';
import { AlertTriangle, ShieldAlert, Terminal } from 'lucide-react';
import type { ApprovalDecisionWire } from '@shared/ipc';
import type { UiPendingApproval } from '../lib/sessionRecovery';

interface ToolApprovalCardProps {
  approval: UiPendingApproval;
  controlPlaneAvailable: boolean;
  onResolved: (approvalId: string) => void;
  onRequestSwitchManaged?: () => void;
}

const DECISION_LABELS: Record<ApprovalDecisionWire, string> = {
  allow_once: '允许一次',
  allow_session: '本会话允许',
  allow_always: '始终允许',
  deny: '拒绝',
};

export const ToolApprovalCard: React.FC<ToolApprovalCardProps> = ({
  approval,
  controlPlaneAvailable,
  onResolved,
  onRequestSwitchManaged,
}) => {
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Defensively withhold long-term allow decisions when workspace code execution is involved,
  // regardless of backend filtering.
  const rawAllowed = new Set(approval.allowedDecisions as ApprovalDecisionWire[]);
  const allowed = new Set<ApprovalDecisionWire>(
    Array.from(rawAllowed).filter((d) =>
      approval.executesWorkspaceCode ? d === 'allow_once' || d === 'deny' : true,
    ),
  );
  const isLocal = approval.backend === 'Local';

  const handleDecision = async (decision: ApprovalDecisionWire) => {
    setSubmitting(true);
    setError(null);
    const res = await window.codexflow.approveToolExecution({
      approvalId: approval.approvalId,
      requestDigest: approval.requestDigest,
      decision,
    });
    setSubmitting(false);
    if (res.ok) {
      onResolved(approval.approvalId);
      return;
    }
    setError(
      res.status === 409 && res.expectedRequestDigest
        ? `请求摘要已变化 (${res.errorMessage})，请重新核对后操作。`
        : res.errorMessage || `审批失败 (${res.status})`,
    );
  };

  return (
    <div
      data-testid={`tool-approval-card-${approval.approvalId}`}
      className="rounded-xl border border-amber-500/40 bg-amber-950/20 p-4 text-xs shadow-lg"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <Terminal className="h-4 w-4 text-amber-400" />
          <span className="text-sm font-semibold text-amber-200">需要执行审批：{approval.tool}</span>
          <span className="rounded bg-slate-800 px-2 py-0.5 font-mono text-[11px] text-slate-200">
            {approval.backend}
          </span>
        </div>
        <span className="font-mono text-[11px] text-slate-400">
          摘要 {approval.requestDigest.slice(0, 12)}…
        </span>
      </div>

      {isLocal && (
        <div className="mt-2 flex items-start gap-2 rounded-lg border border-amber-500/30 bg-amber-500/10 p-2 text-amber-200">
          <ShieldAlert className="mt-0.5 h-4 w-4 shrink-0 text-amber-400" />
          <div>
            本地执行 = 以当前用户权限直接运行（网络：{approval.network}，文件系统：{approval.filesystem}）。
          </div>
        </div>
      )}

      {approval.executesWorkspaceCode && (
        <div className="mt-2 flex items-start gap-2 rounded-lg border border-rose-500/30 bg-rose-500/10 p-2 text-rose-200">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-rose-400" />
          <div>
            此命令为解释器/包管理器/构建工具，批准后<strong>会读取并执行工作区内的项目代码</strong>（仅限允许一次）。
          </div>
        </div>
      )}

      <dl className="mt-3 grid grid-cols-1 gap-1.5 rounded-lg bg-slate-950/70 p-3 font-mono text-[11px] text-slate-300">
        <div>
          <dt className="inline text-slate-500">可执行文件：</dt>
          <dd className="inline break-all text-slate-100">{approval.executable}</dd>
        </div>
        {approval.interpreterChain.length > 0 && (
          <div>
            <dt className="inline text-slate-500">解释器链：</dt>
            <dd className="inline break-all">{approval.interpreterChain.join(' -> ')}</dd>
          </div>
        )}
        <div>
          <dt className="inline text-slate-500">参数：</dt>
          <dd className="inline break-all">{JSON.stringify(approval.argv)}</dd>
        </div>
        <div>
          <dt className="inline text-slate-500">工作目录：</dt>
          <dd className="inline break-all">{approval.cwd}</dd>
        </div>
        <div>
          <dt className="inline text-slate-500">触发原因：</dt>
          <dd className="inline">{approval.reason}</dd>
        </div>
        {(approval.stdinSha256 || approval.codeSha256 || approval.scriptSha256) && (
          <div>
            <dt className="inline text-slate-500">输入摘要：</dt>
            <dd className="inline break-all">
              {[
                approval.stdinSha256 ? `stdin=${approval.stdinSha256.slice(0, 12)}` : null,
                approval.codeSha256 ? `code=${approval.codeSha256.slice(0, 12)}` : null,
                approval.scriptSha256 ? `script=${approval.scriptSha256.slice(0, 12)}` : null,
              ]
                .filter(Boolean)
                .join(' ')}
            </dd>
          </div>
        )}
      </dl>

      {!controlPlaneAvailable && (
        <div className="mt-3 flex items-center justify-between rounded-lg border border-rose-500/40 bg-rose-950/30 p-2.5 text-rose-200">
          <span>独立运行 Local 后端默认无控制面凭据，无法审批执行。请切换到托管模式。</span>
          {onRequestSwitchManaged && (
            <button
              type="button"
              onClick={onRequestSwitchManaged}
              className="rounded bg-rose-600 px-2.5 py-1 text-xs font-medium text-white hover:bg-rose-500"
            >
              切换托管模式
            </button>
          )}
        </div>
      )}

      {error && <div className="mt-2 text-xs text-rose-300">{error}</div>}

      <div className="mt-3 flex flex-wrap items-center gap-2">
        {(['allow_once', 'allow_session', 'allow_always', 'deny'] as ApprovalDecisionWire[]).map((decision) => {
          if (!allowed.has(decision)) {
            return null;
          }
          const isDeny = decision === 'deny';
          const isOnce = decision === 'allow_once';
          return (
            <button
              key={decision}
              type="button"
              disabled={submitting || !controlPlaneAvailable}
              onClick={() => void handleDecision(decision)}
              className={
                isDeny
                  ? 'rounded-lg border border-rose-500/40 bg-rose-500/15 px-3 py-1.5 font-medium text-rose-200 hover:bg-rose-500/25 disabled:opacity-40'
                  : isOnce
                    ? 'rounded-lg bg-emerald-600 px-3 py-1.5 font-medium text-white hover:bg-emerald-500 disabled:opacity-40'
                    : 'rounded-lg border border-slate-700 bg-slate-800 px-3 py-1.5 font-medium text-slate-200 hover:bg-slate-700 disabled:opacity-40'
              }
            >
              {DECISION_LABELS[decision]}
            </button>
          );
        })}
        {!allowed.has('allow_always') && (
          <span className="text-[11px] text-slate-400">
            注：该命令不提供“始终允许”（避免信任项目内任意代码）
          </span>
        )}
      </div>
    </div>
  );
};
