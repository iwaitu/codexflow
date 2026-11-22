import { useRef, useState } from 'react';
import {
  ArrowLeft,
  CheckCircle2,
  FileText,
  Globe,
  Monitor,
  Paperclip,
  Plus,
  RefreshCw,
  Send,
  ShieldAlert,
  Square,
  X,
} from 'lucide-react';
import { SafeMarkdown } from '@/components/SafeMarkdown';
import type { MobileChatAttachmentRef } from '@/services/chatService';
import { MAX_MOBILE_UPLOAD_BYTES } from '@/services/uploadService';
import { useMobileStore } from '@/store/useMobileStore';

const SSE_STATUS_LABELS: Record<
  'idle' | 'connecting' | 'connected' | 'reconnecting' | 'stopped',
  { label: string; dotClass: string }
> = {
  idle: { label: '待命', dotClass: 'bg-slate-500' },
  connecting: { label: '连接中', dotClass: 'bg-amber-400 animate-pulse' },
  connected: { label: '实时同步', dotClass: 'bg-emerald-400' },
  reconnecting: { label: '重连中', dotClass: 'bg-amber-400 animate-pulse' },
  stopped: { label: '已断开', dotClass: 'bg-rose-400' },
};

export function ChatView() {
  const pairingState = useMobileStore((s) => s.pairingState);
  const selectedSessionId = useMobileStore((s) => s.selectedSessionId);
  const sessions = useMobileStore((s) => s.sessions);
  const conversations = useMobileStore((s) => s.conversations);
  const selectedConversationId = useMobileStore((s) => s.selectedConversationId);
  const messages = useMobileStore((s) => s.messages);
  const streamingText = useMobileStore((s) => s.streamingText);
  const sseStatus = useMobileStore((s) => s.sseStatus);
  const currentPlan = useMobileStore((s) => s.currentPlan);
  const browserState = useMobileStore((s) => s.browserState);
  const browserScreenshotUrl = useMobileStore((s) => s.browserScreenshotUrl);
  const computerState = useMobileStore((s) => s.computerState);
  const sendingChat = useMobileStore((s) => s.sendingChat);

  const closeOpenSession = useMobileStore((s) => s.closeOpenSession);
  const selectConversation = useMobileStore((s) => s.selectConversation);
  const createNewConversation = useMobileStore((s) => s.createNewConversation);
  const sendMessage = useMobileStore((s) => s.sendMessage);
  const uploadAttachment = useMobileStore((s) => s.uploadAttachment);
  const cancelActiveTurn = useMobileStore((s) => s.cancelActiveTurn);
  const loadSessionPlan = useMobileStore((s) => s.loadSessionPlan);
  const submitPlanDecision = useMobileStore((s) => s.submitPlanDecision);
  const refreshBrowserAndComputer = useMobileStore((s) => s.refreshBrowserAndComputer);
  const closeBrowserSession = useMobileStore((s) => s.closeBrowserSession);
  const emergencyStopComputer = useMobileStore((s) => s.emergencyStopComputer);
  const setError = useMobileStore((s) => s.setError);

  const [draftText, setDraftText] = useState('');
  const [attachments, setAttachments] = useState<MobileChatAttachmentRef[]>([]);
  const [uploadProgress, setUploadProgress] = useState<string | null>(null);
  const [showPlanDrawer, setShowPlanDrawer] = useState(false);
  const [showMonitorDrawer, setShowMonitorDrawer] = useState(false);
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  if (!selectedSessionId) {
    return null;
  }

  const sessionRow = sessions.find((s) => s.sessionId === selectedSessionId);
  const sseBadge = SSE_STATUS_LABELS[sseStatus];
  const biometricOk = Boolean(pairingState?.biometricEnrolled);
  const isLegacySessionPlan = currentPlan?.planArtifactId === 'session-plan';
  const canApproveAndExecute =
    Boolean(currentPlan?.signedEnvelope) && !isLegacySessionPlan && biometricOk && !sendingChat;
  const canApproveOnly =
    Boolean(currentPlan?.signedEnvelopeApproveOnly) && !isLegacySessionPlan && biometricOk && !sendingChat;

  const handleFileChange = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) {
      return;
    }
    if (file.size > MAX_MOBILE_UPLOAD_BYTES) {
      setError(new Error(`附件大小超出 20 MiB 上限（当前 ${(file.size / (1024 * 1024)).toFixed(1)} MiB）。`));
      return;
    }
    try {
      setUploadProgress('准备上传...');
      const uploaded = await uploadAttachment(file, (label) => setUploadProgress(label));
      setAttachments((prev) => [...prev, uploaded]);
    } catch (err) {
      setError(err);
    } finally {
      setUploadProgress(null);
    }
  };

  const handleSend = async (e: React.FormEvent) => {
    e.preventDefault();
    const trimmed = draftText.trim();
    if (!trimmed && attachments.length === 0) {
      return;
    }
    const pendingAttachments = attachments;
    setDraftText('');
    setAttachments([]);
    await sendMessage({
      text: trimmed || '请查看我上传的附件。',
      attachments: pendingAttachments,
    });
  };

  const handleOpenMonitor = async () => {
    const nextOpen = !showMonitorDrawer;
    setShowMonitorDrawer(nextOpen);
    if (nextOpen) {
      await refreshBrowserAndComputer(selectedSessionId);
    }
  };

  return (
    <div className="flex min-h-0 flex-1 flex-col bg-slate-950">
      <header className="pt-safe shrink-0 border-b border-slate-800 bg-slate-950/95 px-3 py-2.5">
        <div className="mx-auto flex max-w-3xl items-center justify-between gap-2">
          <div className="flex min-w-0 items-center gap-2">
            <button
              type="button"
              onClick={closeOpenSession}
              aria-label="返回工作区列表"
              className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl border border-slate-800 bg-slate-900 text-slate-200 hover:bg-slate-800"
            >
              <ArrowLeft className="h-4 w-4" />
            </button>
            <div className="min-w-0">
              <div className="truncate text-sm font-semibold text-slate-100">
                {sessionRow?.name || '远程会话'}
              </div>
              <div className="mt-0.5 flex items-center gap-1.5 text-[11px] text-slate-400">
                <span className={`inline-block h-2 w-2 rounded-full ${sseBadge.dotClass}`} />
                <span>{sseBadge.label}</span>
                {sessionRow?.planModeActive ? (
                  <span className="rounded bg-amber-500/15 px-1.5 py-0.5 text-[10px] text-amber-300">
                    规划模式
                  </span>
                ) : null}
              </div>
            </div>
          </div>

          <div className="flex shrink-0 items-center gap-1.5">
            <button
              type="button"
              onClick={() => setShowPlanDrawer((v) => !v)}
              aria-label="查看执行计划"
              className={`flex h-10 items-center gap-1 rounded-xl border px-2.5 text-xs font-medium transition ${
                showPlanDrawer || currentPlan
                  ? 'border-amber-500/40 bg-amber-500/10 text-amber-300'
                  : 'border-slate-800 bg-slate-900 text-slate-300'
              }`}
            >
              <FileText className="h-3.5 w-3.5" />
              <span>计划</span>
            </button>
            <button
              type="button"
              onClick={() => void handleOpenMonitor()}
              aria-label="查看浏览器与桌面监控"
              className={`flex h-10 items-center gap-1 rounded-xl border px-2.5 text-xs font-medium transition ${
                showMonitorDrawer
                  ? 'border-sky-500/50 bg-sky-500/15 text-sky-300'
                  : 'border-slate-800 bg-slate-900 text-slate-300'
              }`}
            >
              <Monitor className="h-3.5 w-3.5" />
              <span>监控</span>
            </button>
            <button
              type="button"
              onClick={() => void cancelActiveTurn()}
              aria-label="停止当前输出"
              className="flex h-10 w-10 items-center justify-center rounded-xl border border-rose-500/30 bg-rose-500/10 text-rose-300 hover:bg-rose-500/20"
            >
              <Square className="h-3.5 w-3.5 fill-current" />
            </button>
          </div>
        </div>

        <div className="mx-auto mt-2 flex max-w-3xl items-center gap-2">
          <select
            aria-label="选择对话线程"
            value={selectedConversationId ?? ''}
            onChange={(e) => {
              if (e.target.value) {
                void selectConversation(e.target.value);
              }
            }}
            className="h-9 min-w-0 flex-1 rounded-lg border border-slate-800 bg-slate-900 px-2.5 text-xs text-slate-200 focus:border-sky-500 focus:outline-none"
          >
            {conversations.length === 0 ? (
              <option value="">暂无对话记录</option>
            ) : (
              conversations.map((conv) => (
                <option key={conv.conversationId} value={conv.conversationId}>
                  {conv.name || '未命名对话'}
                </option>
              ))
            )}
          </select>
          <button
            type="button"
            onClick={() => void createNewConversation()}
            className="flex h-9 shrink-0 items-center gap-1 rounded-lg border border-slate-700 bg-slate-800 px-2.5 text-xs font-medium text-slate-200 hover:bg-slate-700"
          >
            <Plus className="h-3.5 w-3.5" />
            <span>新对话</span>
          </button>
        </div>
      </header>

      {showPlanDrawer ? (
        <section
          aria-label="计划审批抽屉"
          className="max-h-[45vh] shrink-0 overflow-y-auto border-b border-amber-500/30 bg-slate-900/95 px-4 py-3"
        >
          <div className="mx-auto max-w-3xl space-y-3">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2 text-xs font-semibold text-amber-300">
                <FileText className="h-4 w-4" />
                <span>
                  {currentPlan
                    ? `执行计划 v${currentPlan.version}（${currentPlan.status}）`
                    : '当前会话尚未生成结构化计划'}
                </span>
              </div>
              <button
                type="button"
                onClick={() => void loadSessionPlan(selectedSessionId)}
                aria-label="刷新计划"
                className="flex h-8 items-center gap-1 rounded-lg border border-slate-700 bg-slate-800 px-2 text-xs text-slate-300 hover:bg-slate-700"
              >
                <RefreshCw className="h-3 w-3" />
                <span>刷新</span>
              </button>
            </div>

            {currentPlan ? (
              <>
                {isLegacySessionPlan ? (
                  <div className="flex items-start gap-2 rounded-xl border border-amber-500/40 bg-amber-950/40 p-2.5 text-xs text-amber-200">
                    <ShieldAlert className="mt-0.5 h-4 w-4 shrink-0 text-amber-400" />
                    <span>
                      当前计划未包含结构化 PlanArtifact 签名信封（session-plan 只读草稿），请在桌面端生成正式计划后再签批。
                    </span>
                  </div>
                ) : null}

                {currentPlan.hostRefusal ? (
                  <div className="rounded-xl border border-rose-500/40 bg-rose-950/40 p-2.5 text-xs text-rose-200">
                    桌面拒绝说明：{currentPlan.hostRefusal}
                  </div>
                ) : null}

                <div className="rounded-xl border border-slate-800 bg-slate-950/80 p-3">
                  <div className="mb-1.5 text-xs font-semibold text-slate-200">
                    目标：{currentPlan.goal}
                  </div>
                  <SafeMarkdown content={currentPlan.markdown} />
                </div>

                {!biometricOk ? (
                  <div className="text-xs text-amber-300">
                    当前配对未启用硬件生物识别密钥，无法在移动端签批执行计划。
                  </div>
                ) : null}

                <div className="flex flex-wrap gap-2">
                  <button
                    type="button"
                    disabled={!canApproveAndExecute}
                    onClick={() => void submitPlanDecision('execute', true)}
                    className="flex min-h-[44px] flex-1 items-center justify-center gap-1.5 rounded-xl bg-emerald-500 px-3 text-xs font-semibold text-slate-950 hover:bg-emerald-400 disabled:cursor-not-allowed disabled:opacity-40"
                  >
                    <CheckCircle2 className="h-4 w-4" />
                    <span>批准并开始执行</span>
                  </button>
                  <button
                    type="button"
                    disabled={!canApproveOnly}
                    onClick={() => void submitPlanDecision('approve', false)}
                    className="flex min-h-[44px] flex-1 items-center justify-center rounded-xl border border-slate-700 bg-slate-800 px-3 text-xs font-medium text-slate-100 hover:bg-slate-700 disabled:cursor-not-allowed disabled:opacity-40"
                  >
                    仅批准计划
                  </button>
                </div>
              </>
            ) : null}
          </div>
        </section>
      ) : null}

      {showMonitorDrawer ? (
        <section
          aria-label="托管环境状态监控"
          className="max-h-[45vh] shrink-0 overflow-y-auto border-b border-sky-500/30 bg-slate-900/95 px-4 py-3"
        >
          <div className="mx-auto max-w-3xl space-y-3">
            <div className="flex items-center justify-between">
              <span className="text-xs font-semibold text-sky-300">托管浏览器与桌面控制状态</span>
              <button
                type="button"
                onClick={() => void refreshBrowserAndComputer(selectedSessionId)}
                className="flex h-8 items-center gap-1 rounded-lg border border-slate-700 bg-slate-800 px-2 text-xs text-slate-300 hover:bg-slate-700"
              >
                <RefreshCw className="h-3 w-3" />
                <span>刷新状态</span>
              </button>
            </div>

            <div className="grid gap-3 sm:grid-cols-2">
              <div className="space-y-2 rounded-xl border border-slate-800 bg-slate-950/80 p-3">
                <div className="flex items-center justify-between text-xs font-medium text-slate-200">
                  <span className="flex items-center gap-1.5">
                    <Globe className="h-3.5 w-3.5 text-sky-400" />
                    托管浏览器
                  </span>
                  <span className="text-slate-400">
                    {browserState ? `${browserState.status} · ${browserState.tabs.length} 标签页` : '未启动'}
                  </span>
                </div>
                {browserScreenshotUrl ? (
                  <img
                    src={browserScreenshotUrl}
                    alt="托管浏览器最新画面"
                    className="max-h-40 w-full rounded-lg border border-slate-800 object-contain bg-black"
                  />
                ) : (
                  <div className="rounded-lg border border-dashed border-slate-800 py-4 text-center text-xs text-slate-500">
                    暂无最新截图缓存
                  </div>
                )}
                <button
                  type="button"
                  disabled={!biometricOk || !browserState}
                  onClick={() => void closeBrowserSession(selectedSessionId)}
                  className="flex min-h-[40px] w-full items-center justify-center rounded-lg border border-rose-500/40 bg-rose-500/10 px-3 text-xs font-medium text-rose-200 hover:bg-rose-500/20 disabled:opacity-40"
                >
                  关闭托管浏览器
                </button>
              </div>

              <div className="flex flex-col justify-between space-y-2 rounded-xl border border-slate-800 bg-slate-950/80 p-3">
                <div className="space-y-1 text-xs">
                  <div className="flex items-center justify-between font-medium text-slate-200">
                    <span className="flex items-center gap-1.5">
                      <Monitor className="h-3.5 w-3.5 text-amber-400" />
                      桌面自动化操控
                    </span>
                    <span className="text-slate-400">
                      {computerState?.emergencyStopped
                        ? '已紧急急停'
                        : computerState?.active
                          ? '运行中'
                          : '空闲'}
                    </span>
                  </div>
                  <div className="text-[11px] text-slate-400">
                    锁定进程：{computerState?.pinnedProcessName || '未绑定'}
                  </div>
                  <div className="text-[11px] text-slate-400">
                    最近动作：{computerState?.lastActionSummary || '无'}
                  </div>
                </div>
                <button
                  type="button"
                  disabled={!biometricOk}
                  onClick={() => void emergencyStopComputer(selectedSessionId)}
                  className="flex min-h-[40px] w-full items-center justify-center rounded-lg bg-rose-600 px-3 text-xs font-semibold text-white hover:bg-rose-500 disabled:opacity-40"
                >
                  紧急停止桌面操控
                </button>
              </div>
            </div>
          </div>
        </section>
      ) : null}

      <div className="flex-1 overflow-y-auto px-4 py-4">
        <div className="mx-auto max-w-3xl space-y-4">
          {messages.length === 0 && !streamingText ? (
            <div className="rounded-2xl border border-dashed border-slate-800 p-8 text-center text-sm text-slate-400">
              在下方输入指令与桌面代理协同编码，支持流式输出与附件上传（上限 20 MiB）。
            </div>
          ) : null}

          {messages.map((msg) => {
            const isUser = msg.role === 'user';
            return (
              <div
                key={msg.id}
                className={`flex flex-col ${isUser ? 'items-end' : 'items-start'}`}
              >
                <div
                  className={`max-w-[92%] rounded-2xl px-3.5 py-2.5 text-sm ${
                    isUser
                      ? 'bg-sky-600 text-white'
                      : 'border border-slate-800 bg-slate-900/90 text-slate-100'
                  }`}
                >
                  {msg.thinkingContent ? (
                    <details className="mb-2 rounded-lg border border-slate-700/70 bg-slate-950/60 px-2.5 py-1.5 text-xs text-slate-400">
                      <summary className="cursor-pointer font-medium text-slate-300">
                        查看思考过程
                      </summary>
                      <div className="mt-1.5 whitespace-pre-wrap leading-relaxed">
                        {msg.thinkingContent}
                      </div>
                    </details>
                  ) : null}

                  {isUser ? (
                    <div className="whitespace-pre-wrap break-words leading-relaxed">
                      {msg.content}
                    </div>
                  ) : (
                    <SafeMarkdown content={msg.content} />
                  )}
                </div>
              </div>
            );
          })}

          {streamingText ? (
            <div className="flex flex-col items-start">
              <div className="max-w-[92%] rounded-2xl border border-sky-500/30 bg-slate-900/90 px-3.5 py-2.5 text-sm text-slate-100">
                <div className="mb-1 text-[10px] font-semibold uppercase tracking-wider text-sky-400">
                  正在输出...
                </div>
                <SafeMarkdown content={streamingText} />
              </div>
            </div>
          ) : null}
        </div>
      </div>

      <form
        onSubmit={(e) => void handleSend(e)}
        className="pb-safe shrink-0 border-t border-slate-800 bg-slate-950/95 px-3 py-2.5"
      >
        <div className="mx-auto max-w-3xl space-y-2">
          {uploadProgress ? (
            <div className="text-xs text-sky-400">{uploadProgress}</div>
          ) : null}

          {attachments.length > 0 ? (
            <div className="flex flex-wrap gap-1.5">
              {attachments.map((att) => (
                <span
                  key={att.uploadId}
                  className="inline-flex items-center gap-1 rounded-lg border border-slate-700 bg-slate-900 px-2 py-1 text-xs text-slate-200"
                >
                  <span className="max-w-[160px] truncate">{att.dto.name}</span>
                  <button
                    type="button"
                    aria-label={`移除附件 ${att.dto.name}`}
                    onClick={() =>
                      setAttachments((prev) => prev.filter((item) => item.uploadId !== att.uploadId))
                    }
                    className="text-slate-400 hover:text-rose-400"
                  >
                    <X className="h-3.5 w-3.5" />
                  </button>
                </span>
              ))}
            </div>
          ) : null}

          <div className="flex items-end gap-2">
            <input
              ref={fileInputRef}
              type="file"
              className="hidden"
              onChange={(e) => void handleFileChange(e)}
            />
            <button
              type="button"
              onClick={() => fileInputRef.current?.click()}
              disabled={uploadProgress !== null}
              aria-label="添加附件"
              className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl border border-slate-800 bg-slate-900 text-slate-300 hover:bg-slate-800 disabled:opacity-50"
            >
              <Paperclip className="h-4 w-4" />
            </button>
            <textarea
              rows={1}
              value={draftText}
              onChange={(e) => setDraftText(e.target.value)}
              placeholder="输入指令或问题..."
              aria-label="聊天消息输入框"
              className="min-h-[44px] max-h-32 flex-1 resize-none rounded-xl border border-slate-800 bg-slate-900 px-3 py-2.5 text-sm text-slate-100 placeholder:text-slate-500 focus:border-sky-500 focus:outline-none"
            />
            <button
              type="submit"
              disabled={sendingChat || (!draftText.trim() && attachments.length === 0)}
              aria-label="发送消息"
              className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-sky-500 text-slate-950 hover:bg-sky-400 disabled:opacity-40"
            >
              <Send className="h-4 w-4" />
            </button>
          </div>
        </div>
      </form>
    </div>
  );
}
