import { useState } from 'react';
import {
  Check,
  Code2,
  Lock,
  RefreshCw,
  ShieldAlert,
  ShieldCheck,
  XCircle,
} from 'lucide-react';
import { mapSourceKindToApprovalKind } from '@/services/pendingSyncService';
import { useMobileStore } from '@/store/useMobileStore';

const SOURCE_KIND_LABELS: Record<string, string> = {
  command: '命令执行',
  business: '业务技能',
  browser: '浏览器操作',
  computer: '桌面操控',
};

export function ApprovalsView() {
  const pairingState = useMobileStore((s) => s.pairingState);
  const pendingTodos = useMobileStore((s) => s.pendingTodos);
  const pendingCursor = useMobileStore((s) => s.pendingCursor);
  const loadingTodos = useMobileStore((s) => s.loadingTodos);
  const syncPendingTodos = useMobileStore((s) => s.syncPendingTodos);
  const inspectCommandScript = useMobileStore((s) => s.inspectCommandScript);
  const decideTodoApproval = useMobileStore((s) => s.decideTodoApproval);
  const setError = useMobileStore((s) => s.setError);

  const [expandedScripts, setExpandedScripts] = useState<Record<string, string>>({});
  const [scriptLoadingId, setScriptLoadingId] = useState<string | null>(null);
  const [notesByRef, setNotesByRef] = useState<Record<string, string>>({});
  const [decidingRef, setDecidingRef] = useState<string | null>(null);

  const biometricOk = Boolean(pairingState?.biometricEnrolled);

  const handleInspectScript = async (approvalId: string) => {
    setScriptLoadingId(approvalId);
    try {
      const text = await inspectCommandScript(approvalId);
      setExpandedScripts((prev) => ({ ...prev, [approvalId]: text }));
    } catch (err) {
      setError(err);
    } finally {
      setScriptLoadingId(null);
    }
  };

  const handleDecision = async (
    item: (typeof pendingTodos)[number],
    decision: 'allow_once' | 'deny',
  ) => {
    setDecidingRef(item.opaqueRefId);
    try {
      await decideTodoApproval(item, decision, notesByRef[item.opaqueRefId]);
    } finally {
      setDecidingRef(null);
    }
  };

  return (
    <div className="flex flex-1 flex-col overflow-y-auto px-4 py-4">
      <div className="mx-auto w-full max-w-2xl space-y-4">
        <div className="flex flex-wrap items-center justify-between gap-2 rounded-2xl border border-slate-800 bg-slate-900/80 p-3.5">
          <div>
            <h1 className="text-sm font-semibold text-slate-100">
              待审批与异常通知（{pendingTodos.length}）
            </h1>
            <div className="mt-0.5 text-[11px] text-slate-400">
              epoch={pendingCursor?.summaryEpoch ?? 0} · eventId={pendingCursor?.lastEventId ?? 0}
            </div>
          </div>

          <div className="flex items-center gap-2">
            <button
              type="button"
              disabled={loadingTodos}
              onClick={() => void syncPendingTodos(false)}
              className="flex min-h-[40px] items-center gap-1 rounded-xl border border-slate-700 bg-slate-800 px-3 text-xs font-medium text-slate-200 hover:bg-slate-700 disabled:opacity-50"
            >
              <RefreshCw className={`h-3.5 w-3.5 ${loadingTodos ? 'animate-spin' : ''}`} />
              <span>增量同步</span>
            </button>
            <button
              type="button"
              disabled={loadingTodos}
              onClick={() => void syncPendingTodos(true)}
              className="flex min-h-[40px] items-center rounded-xl bg-sky-500 px-3 text-xs font-semibold text-slate-950 hover:bg-sky-400 disabled:opacity-50"
            >
              全量快照
            </button>
          </div>
        </div>

        {!biometricOk ? (
          <div className="flex items-start gap-2 rounded-xl border border-amber-500/40 bg-amber-950/40 p-3 text-xs text-amber-200">
            <ShieldAlert className="mt-0.5 h-4 w-4 shrink-0 text-amber-400" />
            <span>
              当前设备尚未注册硬件生物识别密钥（biometric_approval），移动端仅可浏览待审批事项，禁止签批高危请求。
            </span>
          </div>
        ) : null}

        {pendingTodos.length === 0 ? (
          <div className="rounded-2xl border border-dashed border-slate-800 p-8 text-center text-sm text-slate-400">
            {loadingTodos ? '正在同步桌面端待审批列表...' : '当前没有待处理审批项。'}
          </div>
        ) : (
          <div className="space-y-3">
            {pendingTodos.map((item) => {
              const kind = mapSourceKindToApprovalKind(item.sourceKind);
              const kindLabel = SOURCE_KIND_LABELS[kind] ?? item.sourceKind;
              const isCommand = kind === 'command' && Boolean(item.approvalId);
              const loadedScript = item.approvalId ? expandedScripts[item.approvalId] : undefined;
              const canApprove =
                !item.readOnlyInRemote &&
                !item.isTerminal &&
                Boolean(item.signedEnvelope) &&
                biometricOk &&
                decidingRef !== item.opaqueRefId;

              return (
                <article
                  key={`${item.sourceKind}:${item.opaqueRefId}`}
                  className="space-y-3 rounded-2xl border border-slate-800 bg-slate-900/80 p-4"
                >
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <div className="flex items-center gap-2">
                      <span className="rounded-md bg-sky-500/15 px-2 py-0.5 text-[11px] font-semibold text-sky-300">
                        {kindLabel}
                      </span>
                      {item.readOnlyInRemote ? (
                        <span className="inline-flex items-center gap-1 rounded-md bg-slate-800 px-2 py-0.5 text-[11px] text-slate-300">
                          <Lock className="h-3 w-3" />
                          仅桌面可批
                        </span>
                      ) : item.signedEnvelope ? (
                        <span className="inline-flex items-center gap-1 rounded-md bg-emerald-500/15 px-2 py-0.5 text-[11px] text-emerald-300">
                          <ShieldCheck className="h-3 w-3" />
                          WYSIWYS 已签名
                        </span>
                      ) : (
                        <span className="inline-flex items-center gap-1 rounded-md bg-amber-500/15 px-2 py-0.5 text-[11px] text-amber-300">
                          缺少桌面签名
                        </span>
                      )}
                    </div>
                    <span className="text-[11px] font-mono text-slate-500">
                      #{item.eventId}
                    </span>
                  </div>

                  <div>
                    <h2 className="text-sm font-semibold text-slate-100">
                      {item.title || '待确认操作'}
                    </h2>
                    <p className="mt-1 whitespace-pre-wrap text-xs leading-relaxed text-slate-300">
                      {item.summary}
                    </p>
                  </div>

                  <div className="flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-slate-400">
                    <span>会话：{item.sessionId.slice(0, 12)}</span>
                    <span>截止：{item.expiresAtUtc}</span>
                  </div>

                  {isCommand && item.approvalId ? (
                    <div className="space-y-2">
                      <button
                        type="button"
                        disabled={scriptLoadingId === item.approvalId}
                        onClick={() => void handleInspectScript(item.approvalId!)}
                        className="inline-flex min-h-[38px] items-center gap-1.5 rounded-xl border border-slate-700 bg-slate-800 px-3 text-xs font-medium text-slate-200 hover:bg-slate-700 disabled:opacity-50"
                      >
                        <Code2 className="h-3.5 w-3.5 text-sky-400" />
                        <span>
                          {scriptLoadingId === item.approvalId
                            ? '正在验证脚本签名...'
                            : loadedScript !== undefined
                              ? '重新校验完整脚本'
                              : '查看完整脚本'}
                        </span>
                      </button>

                      {loadedScript !== undefined ? (
                        <pre className="max-h-56 overflow-auto rounded-xl border border-emerald-500/30 bg-slate-950 p-3 font-mono text-xs text-emerald-200">
                          {loadedScript}
                        </pre>
                      ) : null}
                    </div>
                  ) : null}

                  {item.readOnlyInRemote ? (
                    <div className="rounded-xl border border-slate-800 bg-slate-950/70 px-3 py-2 text-xs text-slate-400">
                      当前操作类型受桌面策略保护（readOnlyInRemote），请在桌面端主界面审批。
                    </div>
                  ) : (
                    <div className="space-y-2 pt-1">
                      <input
                        type="text"
                        value={notesByRef[item.opaqueRefId] ?? ''}
                        onChange={(e) =>
                          setNotesByRef((prev) => ({
                            ...prev,
                            [item.opaqueRefId]: e.target.value,
                          }))
                        }
                        placeholder="审批备注（可选）"
                        aria-label={`审批备注 ${item.title || item.opaqueRefId}`}
                        className="min-h-[40px] w-full rounded-xl border border-slate-800 bg-slate-950 px-3 text-xs text-slate-100 placeholder:text-slate-500 focus:border-sky-500 focus:outline-none"
                      />
                      <div className="flex gap-2">
                        <button
                          type="button"
                          disabled={!canApprove}
                          onClick={() => void handleDecision(item, 'allow_once')}
                          className="flex min-h-[44px] flex-1 items-center justify-center gap-1.5 rounded-xl bg-emerald-500 px-3 text-xs font-semibold text-slate-950 hover:bg-emerald-400 disabled:cursor-not-allowed disabled:opacity-40"
                        >
                          <Check className="h-4 w-4" />
                          <span>允许一次</span>
                        </button>
                        <button
                          type="button"
                          disabled={!canApprove}
                          onClick={() => void handleDecision(item, 'deny')}
                          className="flex min-h-[44px] flex-1 items-center justify-center gap-1.5 rounded-xl border border-rose-500/40 bg-rose-500/15 px-3 text-xs font-semibold text-rose-200 hover:bg-rose-500/25 disabled:cursor-not-allowed disabled:opacity-40"
                        >
                          <XCircle className="h-4 w-4" />
                          <span>拒绝</span>
                        </button>
                      </div>
                    </div>
                  )}
                </article>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
