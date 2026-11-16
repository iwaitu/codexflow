import React, { useEffect, useRef } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { Bot, CheckCircle2, Cpu, Loader2, ShieldCheck } from 'lucide-react';
import type { BackgroundJob, SessionPlan, VerificationReceipt } from '../lib/apiClient';
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
  onApprovalResolved: (approvalId: string) => void;
  onPlanActionSubmitted: () => void;
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
  onApprovalResolved,
  onPlanActionSubmitted,
  onVerifyJob,
  onSwitchManaged,
}) => {
  const scrollContainerRef = useRef<HTMLDivElement>(null);

  const virtualizer = useVirtualizer({
    count: messages.length,
    getScrollElement: () => scrollContainerRef.current,
    estimateSize: () => 110,
    overscan: 6,
  });

  useEffect(() => {
    if (messages.length > 0) {
      virtualizer.scrollToIndex(messages.length - 1, { align: 'end' });
    }
  }, [messages.length, virtualizer]);

  useEffect(() => {
    const el = scrollContainerRef.current;
    if (el && (isTurnRunning || pendingApprovals.length > 0)) {
      el.scrollTop = el.scrollHeight;
    }
  }, [streamingContent, streamingThinking, toolCalls.length, pendingApprovals.length, isTurnRunning]);

  return (
    <div ref={scrollContainerRef} className="flex-1 overflow-y-auto px-6 py-4">
      {messagesTruncated && (
        <div
          data-testid="messages-truncated-banner"
          className="mb-3 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-1.5 text-xs text-amber-200"
        >
          快照恢复中部分工具输出或消息内容超出上限已被截断，当前仅展示摘要与最近数据。
        </div>
      )}

      {messages.length === 0 && !isTurnRunning && !currentPlan && pendingApprovals.length === 0 && (
        <div className="mx-auto mt-16 max-w-xl rounded-2xl border border-slate-800/80 bg-slate-900/50 p-6 text-center">
          <div className="mx-auto mb-3 flex h-10 w-10 items-center justify-center rounded-xl bg-indigo-500/20 text-indigo-300">
            <Bot className="h-5 w-5" />
          </div>
          <h2 className="text-base font-semibold text-slate-100">CodexFlow 已就绪</h2>
          <p className="mt-1.5 text-xs leading-relaxed text-slate-400">
            选择左侧工作区或打开本地文件夹后，在下方输入任务即可开始分析、规划、分级审批执行与验证。
          </p>
        </div>
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

      {(isTurnRunning || streamingContent || streamingThinking || toolCalls.length > 0) && (
        <div className="mt-3 space-y-3">
          {toolCalls.length > 0 && (
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

      {sessionId && currentPlan && (
        <div className="mt-4">
          <PlanApprovalCard
            sessionId={sessionId}
            conversationId={conversationId}
            plan={currentPlan}
            executionBackend={executionBackend}
            controlPlaneAvailable={controlPlaneAvailable}
            onSubmitted={onPlanActionSubmitted}
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
  );
};
