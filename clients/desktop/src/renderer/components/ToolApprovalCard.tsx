import React, { useEffect, useMemo, useState } from 'react';
import { AlertTriangle, Check, Clock, Copy, RefreshCw, ShieldAlert, Terminal } from 'lucide-react';
import type { ApprovalDecisionWire, PendingApprovalScriptSnapshot } from '@shared/ipc';
import type { UiPendingApproval } from '../lib/sessionRecovery';

async function sha256HexUtf8(text: string): Promise<string> {
  const bytes = new TextEncoder().encode(text);
  const digest = await globalThis.crypto.subtle.digest('SHA-256', bytes);
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('');
}

function hasSuspiciousControlCharacters(text: string): boolean {
  for (let i = 0; i < text.length; i += 1) {
    const code = text.charCodeAt(i);
    if ((code < 32 && code !== 9 && code !== 10 && code !== 13) || code === 127) {
      return true;
    }
    // 检测 Unicode 双向控制字符（BiDi Trojan Source）与零宽字符
    if (
      code === 0x061c ||
      (code >= 0x200b && code <= 0x200f) ||
      (code >= 0x202a && code <= 0x202e) ||
      (code >= 0x2066 && code <= 0x2069) ||
      code === 0xfeff
    ) {
      return true;
    }
  }
  return false;
}

function formatRemainingTime(expiresAtUtc: string | undefined, nowMs: number): { label: string; expired: boolean } | null {
  if (!expiresAtUtc) return null;
  const targetMs = Date.parse(expiresAtUtc);
  if (Number.isNaN(targetMs)) return null;
  const diffSec = Math.floor((targetMs - nowMs) / 1000);
  if (diffSec <= 0) {
    return { label: '审批已过期', expired: true };
  }
  const mins = Math.floor(diffSec / 60);
  const secs = diffSec % 60;
  return {
    label: `剩余 ${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`,
    expired: false,
  };
}

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
  const [scriptSnapshot, setScriptSnapshot] = useState<PendingApprovalScriptSnapshot | null>(null);
  const [scriptVerifyState, setScriptVerifyState] = useState<'none' | 'loading' | 'verified' | 'mismatch' | 'error'>(
    approval.scriptSha256 ? 'loading' : 'none',
  );
  const [scriptError, setScriptError] = useState<string | null>(null);
  const [scriptExpanded, setScriptExpanded] = useState(false);
  const [copiedScript, setCopiedScript] = useState(false);
  const [scriptRetryTick, setScriptRetryTick] = useState(0);
  const [nowMs, setNowMs] = useState(() => Date.now());

  const requiresScriptVerification = Boolean(approval.scriptSha256);
  const isDailyShell = approval.tool === 'run_shell';
  const remaining = useMemo(
    () => formatRemainingTime(approval.expiresAtUtc, nowMs),
    [approval.expiresAtUtc, nowMs],
  );

  useEffect(() => {
    if (!approval.expiresAtUtc) return undefined;
    const timer = window.setInterval(() => setNowMs(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [approval.expiresAtUtc]);

  useEffect(() => {
    if (!approval.scriptSha256) {
      setScriptVerifyState('none');
      setScriptSnapshot(null);
      setScriptError(null);
      return;
    }

    let cancelled = false;
    setScriptVerifyState('loading');
    setScriptError(null);

    void window.codexflow
      .getApprovalScript(approval.approvalId)
      .then(async (res) => {
        if (cancelled) return;
        if (!res.ok || !res.data) {
          setScriptVerifyState('error');
          setScriptError(res.errorMessage || `无法获取完整脚本快照 (${res.status})`);
          return;
        }
        const snap = res.data;
        setScriptSnapshot(snap);
        if (snap.requestDigest !== approval.requestDigest) {
          setScriptVerifyState('mismatch');
          setScriptError('脚本快照与审批请求摘要不匹配，已禁止批准。');
          return;
        }
        try {
          const actualSha = await sha256HexUtf8(snap.scriptText);
          if (cancelled) return;
          if (actualSha.toLowerCase() !== approval.scriptSha256?.toLowerCase()) {
            setScriptVerifyState('mismatch');
            setScriptError('脚本内容 SHA-256 校验失败，可能已被篡改，已禁止批准。');
            return;
          }
          setScriptVerifyState('verified');
        } catch {
          if (!cancelled) {
            setScriptVerifyState('error');
            setScriptError('浏览器 WebCrypto SHA-256 不可用，无法校验脚本完整性。');
          }
        }
      })
      .catch((err) => {
        if (!cancelled) {
          setScriptVerifyState('error');
          setScriptError(err instanceof Error ? err.message : String(err));
        }
      });

    return () => {
      cancelled = true;
    };
  }, [approval.approvalId, approval.requestDigest, approval.scriptSha256, scriptRetryTick]);

  const suspiciousControlChars = useMemo(
    () => (scriptSnapshot ? hasSuspiciousControlCharacters(scriptSnapshot.scriptText) : false),
    [scriptSnapshot],
  );

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
    try {
      const res = await window.codexflow.approveToolExecution({
        approvalId: approval.approvalId,
        requestDigest: approval.requestDigest,
        decision,
      });
      if (res.ok) {
        onResolved(approval.approvalId);
        return;
      }
      setError(
        res.status === 409 && res.expectedRequestDigest
          ? `请求摘要已变化 (${res.errorMessage})，请重新核对后操作。`
          : res.errorMessage || `审批失败 (${res.status})`,
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSubmitting(false);
    }
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
        <div className="flex items-center gap-2">
          {remaining && (
            <span
              className={`inline-flex items-center gap-1 rounded px-1.5 py-0.5 font-mono text-[11px] ${
                remaining.expired ? 'bg-rose-500/20 text-rose-300' : 'bg-slate-800/80 text-slate-300'
              }`}
            >
              <Clock className="h-3 w-3" />
              {remaining.label}
            </span>
          )}
          <span className="font-mono text-[11px] text-slate-400">
            摘要 {approval.requestDigest.slice(0, 12)}…
          </span>
        </div>
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
            {isDailyShell ? (
              <>此操作为本机 Shell 脚本执行，请逐行审阅下方完整脚本内容（仅限<strong>允许一次</strong>）。</>
            ) : (
              <>此命令为解释器/包管理器/构建工具，批准后<strong>会读取并执行工作区内的项目代码</strong>（仅限允许一次）。</>
            )}
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

        {isLocal && approval.tool === 'run_shell' && (
          <div className="mt-2 rounded-lg border border-sky-500/30 bg-sky-500/10 p-2 text-sky-200">
            日常脚本沙箱说明：Local 模式下 <code>HOME/USERPROFILE</code> 指向临时目录；仅工作区 <code>outputs/</code> 下的产物会持久化保留并可导出。
          </div>
        )}

        {requiresScriptVerification && (
          <div className="mt-3 rounded-lg border border-slate-700 bg-slate-950/90 p-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div className="flex items-center gap-2 text-[11px] font-medium text-slate-200">
                <span>待审批完整脚本</span>
                {approval.shellKind && (
                  <span className="rounded bg-slate-800 px-1.5 py-0.5 font-mono text-[10px] text-indigo-300">
                    {approval.shellKind}
                  </span>
                )}
                {scriptVerifyState === 'verified' && (
                  <span
                    data-testid="script-sha-verified"
                    className="rounded bg-emerald-500/15 px-1.5 py-0.5 text-[10px] text-emerald-300"
                  >
                    SHA-256 已校验
                  </span>
                )}
                {scriptVerifyState === 'loading' && (
                  <span className="text-[10px] text-amber-300">正在拉取并校验脚本…</span>
                )}
              </div>

              {scriptSnapshot && (
                <div className="flex items-center gap-1.5">
                  <button
                    type="button"
                    onClick={() => {
                      void navigator.clipboard?.writeText(scriptSnapshot.scriptText).then(() => {
                        setCopiedScript(true);
                        setTimeout(() => setCopiedScript(false), 1500);
                      });
                    }}
                    className="inline-flex items-center gap-1 rounded border border-slate-700 px-2 py-0.5 text-[10px] text-slate-300 hover:bg-slate-800"
                  >
                    {copiedScript ? <Check className="h-3 w-3 text-emerald-400" /> : <Copy className="h-3 w-3" />}
                    <span>{copiedScript ? '已复制' : '复制脚本'}</span>
                  </button>
                  {scriptSnapshot.scriptText.split('\n').length > 10 && (
                    <button
                      type="button"
                      onClick={() => setScriptExpanded((prev) => !prev)}
                      className="rounded border border-slate-700 px-2 py-0.5 text-[10px] text-slate-300 hover:bg-slate-800"
                    >
                      {scriptExpanded ? '收起' : '展开全部'}
                    </button>
                  )}
                </div>
              )}
            </div>

            {suspiciousControlChars && (
              <div className="mt-2 rounded border border-rose-500/40 bg-rose-500/10 px-2 py-1 text-[11px] text-rose-200">
                警告：脚本包含不可见或控制字符（例如 ANSI 转义序列），请仔细核对后再决定是否允许。
              </div>
            )}

            {scriptError && (
              <div
                data-testid="script-verify-error"
                className="mt-2 flex flex-wrap items-center justify-between gap-2 text-[11px] text-rose-300"
              >
                <span>{scriptError}</span>
                {scriptVerifyState === 'error' && (
                  <button
                    type="button"
                    onClick={() => setScriptRetryTick((t) => t + 1)}
                    className="inline-flex items-center gap-1 rounded border border-rose-500/40 bg-rose-500/15 px-2 py-0.5 text-[10px] font-medium text-rose-200 hover:bg-rose-500/25"
                  >
                    <RefreshCw className="h-3 w-3" />
                    重试拉取脚本
                  </button>
                )}
              </div>
            )}

            {scriptSnapshot && (
              <pre
                data-testid="approval-script-preview"
                className={`mt-2 overflow-x-auto whitespace-pre-wrap break-all rounded border border-slate-800 bg-slate-900 p-2 font-mono text-[11px] text-slate-100 ${
                  scriptExpanded ? 'max-h-[420px] overflow-y-auto' : 'max-h-48 overflow-y-auto'
                }`}
              >
                {scriptSnapshot.scriptText}
              </pre>
            )}
          </div>
        )}

        <div className="mt-3 flex flex-wrap items-center gap-2">
          {(['allow_once', 'allow_session', 'allow_always', 'deny'] as ApprovalDecisionWire[]).map((decision) => {
            if (!allowed.has(decision)) {
              return null;
            }
            const isDeny = decision === 'deny';
            const isOnce = decision === 'allow_once';
            const allowDisabled =
              !isDeny && requiresScriptVerification && scriptVerifyState !== 'verified';
            return (
              <button
                key={decision}
                type="button"
                disabled={submitting || !controlPlaneAvailable || allowDisabled}
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
            {isDailyShell
              ? '注：日常模式 Shell 指令强制单次授权，不提供“始终允许”'
              : '注：该命令不提供“始终允许”（避免信任项目内任意代码）'}
          </span>
        )}
      </div>
    </div>
  );
};
