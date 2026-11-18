import React, { useEffect, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { AlertTriangle, ArrowDown, Bot, CheckCircle2, Cpu, FolderPlus, Loader2, Plus, RefreshCw, ShieldCheck } from 'lucide-react';
import type { BackgroundJob, PlanModeExitRequestState, SessionPlan, SessionWorkMode, VerificationReceipt } from '../lib/apiClient';
import type { UiMessage, UiPendingApproval, UiToolCall, WorkerBarState } from '../lib/sessionRecovery';
import { MarkdownRenderer } from './MarkdownRenderer';
import { MessageItem } from './MessageItem';
import { PlanApprovalCard } from './PlanApprovalCard';
import { ToolApprovalCard } from './ToolApprovalCard';
import { ToolCallCard } from './ToolCallCard';
import { isUnverifiedWithoutIsolation, resolveJobVerificationKind, VerificationBadge } from './VerificationBadge';

interface ChatViewProps {
  sessionId: string | null;
  conversationId: string | null;
  messages: UiMessage[];
  messagesTruncated?: boolean;
  streamingContent: string;
  streamingThinking: string;
  isThinkingStreaming: boolean;
  isTurnRunning: boolean;
  toolCalls: UiToolCall[];
  pendingApprovals: UiPendingApproval[];
  currentPlan: SessionPlan | null;
  jobs: BackgroundJob[];
  verificationReceipts: Record<string, VerificationReceipt>;
  workerBar: WorkerBarState;
  executionBackend: string | null;
  controlPlaneAvailable: boolean;
  workMode?: SessionWorkMode;
  planModeActive?: boolean;
  planModeExitRequest?: PlanModeExitRequestState | null;
  bootstrapState?: 'idle' | 'loading' | 'error';
  bootstrapError?: string | null;
  sseState?: 'connecting' | 'connected' | 'reconnecting' | 'closed';
  onRetryBootstrap?: () => void;
  onOpenDailyAssistant?: () => Promise<void>;
  onCreateDailySession?: () => Promise<void>;
  onOpenFolder?: () => Promise<void>;
  onApprovalResolved: (approvalId: string) => void;
  onPlanActionSubmitted: () => void;
  onConfirmExitPlanMode?: () => Promise<void>;
  onDismissPlanModeExitRequest?: () => void;
  onVerifyJob: (jobId: string) => Promise<void>;
  onSwitchManaged: () => void;
}

export const ChatView: React.FC<ChatViewProps> = ({
  sessionId,
  conversationId,
  messages,
  messagesTruncated,
  streamingContent,
  streamingThinking,
  isThinkingStreaming,
  isTurnRunning,
  toolCalls,
  pendingApprovals,
  currentPlan,
  jobs,
  verificationReceipts,
  workerBar,
  executionBackend,
  controlPlaneAvailable,
  workMode = 'Development',
  planModeActive = false,
  planModeExitRequest = null,
  bootstrapState = 'idle',
  bootstrapError = null,
  sseState = 'connected',
  onRetryBootstrap,
  onOpenDailyAssistant,
  onCreateDailySession,
  onOpenFolder,
  onApprovalResolved,
  onPlanActionSubmitted,
  onConfirmExitPlanMode,
  onDismissPlanModeExitRequest,
  onVerifyJob,
  onSwitchManaged,
}) => {
  const scrollContainerRef = useRef<HTMLDivElement>(null);
  const isNearBottomRef = useRef(true);
  const [showScrollBottom, setShowScrollBottom] = useState(false);

  const virtualizer = useVirtualizer({
    count: messages.length,
    getScrollElement: () => scrollContainerRef.current,
    estimateSize: () => 110,
    overscan: 6,
  });

  const handleScroll = () => {
    const el = scrollContainerRef.current;
    if (!el) return;
    const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
    const nearBottom = distanceFromBottom <= 80;
    isNearBottomRef.current = nearBottom;
    setShowScrollBottom(!nearBottom && (isTurnRunning || messages.length > 4));
  };

  useEffect(() => {
    if (messages.length > 0 && isNearBottomRef.current) {
      virtualizer.scrollToIndex(messages.length - 1, { align: 'end' });
    }
  }, [messages.length, virtualizer]);

  useEffect(() => {
    const el = scrollContainerRef.current;
    if (el && isNearBottomRef.current && (isTurnRunning || pendingApprovals.length > 0)) {
      el.scrollTop = el.scrollHeight;
    }
  }, [streamingContent, streamingThinking, toolCalls.length, pendingApprovals.length, isTurnRunning]);

  const scrollToBottom = () => {
    const el = scrollContainerRef.current;
    if (!el) return;
    isNearBottomRef.current = true;
    setShowScrollBottom(false);
    el.scrollTop = el.scrollHeight;
  };

  return (
    <div className="relative flex-1 overflow-hidden">
      <div
        ref={scrollContainerRef}
        onScroll={handleScroll}
        className="h-full overflow-y-auto px-6 py-4"
      >
        {sseState === 'reconnecting' && (
          <div className="mb-3 flex items-center gap-2 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-1.5 text-xs text-amber-200">
            <Loader2 className="h-3.5 w-3.5 animate-spin text-amber-300" />
            <span>实时事件流连接中断，正在自动重连并同步最新状态…</span>
          </div>
        )}

        {messagesTruncated && (
          <div
            data-testid="messages-truncated-banner"
            className="mb-3 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-1.5 text-xs text-amber-200"
          >
            快照恢复中部分工具输出或消息内容超出上限已被截断，当前仅展示摘要与最近数据。
          </div>
        )}

        {messages.length === 0 && !isTurnRunning && !currentPlan && pendingApprovals.length === 0 && (
          !sessionId ? (
            <div
              data-testid="empty-session-welcome"
              className="mx-auto mt-16 max-w-xl rounded-2xl border border-slate-800/80 bg-slate-900/50 p-6 text-center"
            >
              <div className="mx-auto mb-3 flex h-10 w-10 items-center justify-center rounded-xl bg-indigo-500/20 text-indigo-300">
                {bootstrapState === 'loading' ? (
                  <Loader2 className="h-5 w-5 animate-spin" />
                ) : bootstrapState === 'error' ? (
                  <AlertTriangle className="h-5 w-5 text-rose-400" />
                ) : (
                  <Bot className="h-5 w-5" />
                )}
              </div>
              <h2 className="text-base font-semibold text-slate-100">
                {bootstrapState === 'loading'
                  ? '正在初始化工作空间…'
                  : bootstrapState === 'error'
                    ? '工作空间初始化失败'
                    : '尚未选择工作区'}
              </h2>
              <p className="mt-2 text-xs leading-relaxed text-slate-400">
                {bootstrapState === 'error'
                  ? bootstrapError || '无法连接到后端默认工作空间，请确认后端已就绪后重试。'
                  : bootstrapState === 'loading'
                    ? '正在连接本地托管宿主并恢复工作空间配置，请稍候。'
                    : '点击下方快捷入口开启日常办公，或打开一个本地项目文件夹开始研发协作。'}
              </p>
              <div className="mt-4 flex flex-wrap items-center justify-center gap-2 text-xs">
                {bootstrapState === 'error' && onRetryBootstrap && (
                  <button
                    type="button"
                    onClick={onRetryBootstrap}
                    className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 font-medium text-white hover:bg-indigo-500"
                  >
                    <RefreshCw className="h-3.5 w-3.5" />
                    重试连接
                  </button>
                )}
                {onOpenDailyAssistant && (
                  <button
                    type="button"
                    onClick={() => void onOpenDailyAssistant()}
                    className="inline-flex items-center gap-1.5 rounded-lg border border-emerald-500/40 bg-emerald-500/15 px-3 py-1.5 font-medium text-emerald-200 hover:bg-emerald-500/25"
                  >
                    <Bot className="h-3.5 w-3.5" />
                    打开日常助手
                  </button>
                )}
                {onCreateDailySession && (
                  <button
                    type="button"
                    onClick={() => void onCreateDailySession()}
                    className="inline-flex items-center gap-1.5 rounded-lg border border-slate-700 bg-slate-800 px-3 py-1.5 font-medium text-slate-200 hover:bg-slate-700"
                  >
                    <Plus className="h-3.5 w-3.5" />
                    新建日常对话
                  </button>
                )}
                {onOpenFolder && (
                  <button
                    type="button"
                    disabled={!controlPlaneAvailable}
                    onClick={() => void onOpenFolder()}
                    className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
                  >
                    <FolderPlus className="h-3.5 w-3.5" />
                    打开本地目录
                  </button>
                )}
              </div>
            </div>
          ) : (
            <div
              data-testid="empty-session-welcome"
              className={`mx-auto mt-16 max-w-xl rounded-2xl border p-6 text-center ${
                workMode === 'Daily'
                  ? 'border-emerald-500/30 bg-emerald-950/15'
                  : 'border-slate-800/80 bg-slate-900/50'
              }`}
            >
              <div
                className={`mx-auto mb-3 flex h-10 w-10 items-center justify-center rounded-xl ${
                  workMode === 'Daily'
                    ? 'bg-emerald-500/20 text-emerald-300'
                    : 'bg-indigo-500/20 text-indigo-300'
                }`}
              >
                <Bot className="h-5 w-5" />
              </div>
              <div className="flex items-center justify-center gap-2">
                <h2 className="text-base font-semibold text-slate-100">
                  {workMode === 'Daily' ? '日常助手已就绪（日常模式）' : '开发模式已就绪（Development）'}
                </h2>
                {planModeActive && (
                  <span className="rounded border border-amber-500/40 bg-amber-500/15 px-1.5 py-0.5 text-[10px] font-medium text-amber-200">
                    只读 Plan Mode
                  </span>
                )}
              </div>
              <p className="mt-2 text-xs leading-relaxed text-slate-400">
                {workMode === 'Daily'
                  ? controlPlaneAvailable
                    ? '适用于日常办公、文档整理、授权目录浏览（list_authorized_directory）与按次确认的本地 Shell 指令（run_shell）。需要切换工程代码开发时可点击左下角“日常模式”菜单或输入 /dev。'
                    : '适用于日常办公、文档整理与授权目录浏览。当前为只读连接（无控制面），本地 Shell 审批暂不可用，可在底部状态栏切换为托管模式后启用。'
                  : '适用于代码分析、多代理规划与文件修改验证。如需进入“日常助手”处理日常办公或受控 Shell 任务，可点击左侧“日常助手”、左下角“开发模式”菜单或输入 /daily；输入 /plan 可开启只读规划模式。'}
              </p>
            </div>
          )
        )}

      {messages.length > 0 && (
        <div
          style={{
            height: `${virtualizer.getTotalSize()}px`,
            width: '100%',
            position: 'relative',
          }}
        >
          {virtualizer.getVirtualItems().map((virtualRow) => {
            const msg = messages[virtualRow.index];
            if (!msg) return null;
            return (
              <div
                key={msg.id}
                data-index={virtualRow.index}
                ref={virtualizer.measureElement}
                style={{
                  position: 'absolute',
                  top: 0,
                  left: 0,
                  width: '100%',
                  transform: `translateY(${virtualRow.start}px)`,
                }}
              >
                <MessageItem message={msg} />
              </div>
            );
          })}
        </div>
      )}

      {(isTurnRunning || streamingContent || streamingThinking) && (
        <div className="mt-3 space-y-3">
          {isTurnRunning && toolCalls.length > 0 && (
            <div className="space-y-2">
              {toolCalls.map((t) => (
                <ToolCallCard key={t.key} tool={t} />
              ))}
            </div>
          )}

          {(streamingContent || streamingThinking || isTurnRunning) && (
            <div className="flex gap-3">
              <div className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-indigo-600/20 text-indigo-300">
                {isTurnRunning ? <Loader2 className="h-4 w-4 animate-spin" /> : <Bot className="h-4 w-4" />}
              </div>
              <div className="max-w-[85%] flex-1 rounded-xl border border-slate-800/80 bg-slate-900/80 px-4 py-3">
                {streamingThinking && (
                  <details open={isThinkingStreaming} className="mb-2 rounded-lg border border-slate-800 bg-slate-950/60">
                    <summary className="cursor-pointer px-2.5 py-1.5 text-xs text-slate-400">
                      {isThinkingStreaming ? '正在思考...' : '思考过程'}
                    </summary>
                    <div className="border-t border-slate-800 px-3 py-2 text-xs text-slate-300 whitespace-pre-wrap">
                      {streamingThinking}
                    </div>
                  </details>
                )}
                {streamingContent ? (
                  <MarkdownRenderer content={streamingContent} />
                ) : (
                  <span className="text-xs text-slate-400">正在处理请求...</span>
                )}
              </div>
            </div>
          )}
        </div>
      )}

      {pendingApprovals.length > 0 && (
        <div className="mt-4 space-y-3">
          {pendingApprovals.map((approval) => (
            <ToolApprovalCard
              key={approval.approvalId}
              approval={approval}
              controlPlaneAvailable={controlPlaneAvailable}
              onResolved={onApprovalResolved}
              onRequestSwitchManaged={onSwitchManaged}
            />
          ))}
        </div>
      )}

      {planModeActive && planModeExitRequest && (
        <div
          data-testid="plan-mode-exit-card"
          className="mt-4 rounded-xl border border-amber-500/40 bg-amber-950/20 p-4 text-xs shadow-lg"
        >
          <div className="flex items-center gap-2 font-semibold text-amber-200">
            <ShieldCheck className="h-4 w-4 text-amber-400" />
            <span>助手请求退出 Plan Mode（需用户通过控制面确认）</span>
          </div>
          {planModeExitRequest.planSummary && (
            <div className="mt-2 whitespace-pre-wrap rounded-lg border border-slate-800 bg-slate-950/70 p-2.5 text-slate-200">
              {planModeExitRequest.planSummary}
            </div>
          )}
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <button
              type="button"
              disabled={!controlPlaneAvailable || !onConfirmExitPlanMode}
              onClick={() => void onConfirmExitPlanMode?.()}
              className="rounded-lg bg-emerald-600 px-3 py-1.5 font-medium text-white hover:bg-emerald-500 disabled:opacity-40"
            >
              确认退出 Plan Mode
            </button>
            {onDismissPlanModeExitRequest && (
              <button
                type="button"
                onClick={onDismissPlanModeExitRequest}
                className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-1.5 font-medium text-slate-200 hover:bg-slate-700"
              >
                继续保持只读
              </button>
            )}
          </div>
          {!controlPlaneAvailable && (
            <div className="mt-2 flex flex-wrap items-center gap-2 rounded-lg border border-amber-500/30 bg-slate-950/60 px-2.5 py-1.5 text-[11px] text-amber-200">
              <span>当前处于只读连接（无控制面），无法授权退出 Plan Mode。</span>
              <button
                type="button"
                onClick={onSwitchManaged}
                className="rounded border border-amber-400/40 bg-amber-500/20 px-2 py-0.5 font-medium text-amber-100 hover:bg-amber-500/30"
              >
                切换为托管模式
              </button>
            </div>
          )}
        </div>
      )}

      {sessionId && workMode !== 'Daily' && currentPlan && (
        <div className="mt-4">
          <PlanApprovalCard
            sessionId={sessionId}
            conversationId={conversationId}
            plan={currentPlan}
            executionBackend={executionBackend}
            controlPlaneAvailable={controlPlaneAvailable}
            onSubmitted={onPlanActionSubmitted}
            onRequestSwitchManaged={onSwitchManaged}
          />
        </div>
      )}

      {workerBar.status !== 'idle' && (
        <div className="mt-4 flex items-center gap-2 rounded-lg border border-slate-800 bg-slate-900/80 px-3 py-2 text-xs text-slate-200">
          <Cpu className="h-4 w-4 text-indigo-400" />
          <span>{workerBar.message}</span>
        </div>
      )}

      {jobs.length > 0 && (
        <div className="mt-4 space-y-2 rounded-xl border border-slate-800 bg-slate-900/50 p-3">
          <div className="text-xs font-medium text-slate-300">后台作业 ({jobs.length})</div>
          {jobs.map((job) => {
            const jobId = job.jobId ?? '';
            const receipt = jobId ? verificationReceipts[jobId] : undefined;
            const rawKind = resolveJobVerificationKind(job, receipt);
            const badgeKind =
              rawKind === 'unverified' && isUnverifiedWithoutIsolation(job, receipt, executionBackend)
                ? 'verified_unisolated'
                : rawKind;
            return (
              <div
                key={jobId || `${job.taskId}-${job.status}`}
                className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-slate-800 bg-slate-950/70 px-3 py-2 text-xs"
              >
                <div className="flex flex-wrap items-center gap-2">
                  <CheckCircle2 className="h-3.5 w-3.5 text-slate-400" />
                  <span className="font-mono text-slate-200">{job.taskId || jobId}</span>
                  <span className="rounded bg-slate-800 px-1.5 py-0.5 text-[11px] text-slate-300">
                    {job.status}
                  </span>
                  {(job.status === 'Completed' || job.status === 'Failed' || Boolean(receipt)) && (
                    <VerificationBadge
                      kind={badgeKind}
                      notice={receipt?.verificationNotice || receipt?.message || job.verification?.summary}
                    />
                  )}
                </div>

                {job.status === 'Completed' && job.jobType === 'ForgeWorker' && jobId && (
                  <button
                    type="button"
                    onClick={() => void onVerifyJob(jobId)}
                    className="inline-flex items-center gap-1 rounded border border-slate-700 bg-slate-800 px-2 py-1 text-[11px] text-slate-200 hover:bg-slate-700"
                  >
                    <ShieldCheck className="h-3 w-3" />
                    触发验收
                  </button>
                )}
              </div>
            );
          })}
        </div>
      )}
      </div>

      {showScrollBottom && (
        <button
          type="button"
          onClick={scrollToBottom}
          aria-label="回到底部"
          className="absolute bottom-4 right-8 inline-flex items-center gap-1 rounded-full border border-slate-700 bg-slate-900/95 px-3 py-1.5 text-xs font-medium text-slate-200 shadow-lg hover:bg-slate-800"
        >
          <ArrowDown className="h-3.5 w-3.5" />
          回到底部
        </button>
      )}
    </div>
  );
};
