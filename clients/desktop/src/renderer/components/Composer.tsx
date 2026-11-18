import React, { useEffect, useMemo, useRef, useState } from 'react';
import { ArrowUp, Image as ImageIcon, Plus, ShieldCheck, Square, Upload, X } from 'lucide-react';
import type { SessionWorkMode } from '../lib/apiClient';
import {
  createAttachmentPreviewUrl,
  extractClipboardImageFiles,
  formatFileSize,
  getAttachmentVisualMeta,
  isImageAttachmentFile,
  MAX_ATTACHMENT_BYTES,
  MAX_ATTACHMENTS_PER_MESSAGE,
  revokeAttachmentPreviewUrl,
  type PendingAttachment,
} from '../lib/attachments';
import { useI18n } from '../lib/i18n';
import { AttachmentTypeIcon } from './AttachmentTypeIcon';
import { WorkModeMenu } from './WorkModeMenu';

export interface WorkspaceAttachmentRequest {
  nonce: number;
  name: string;
  workspaceRelativePath: string;
  sizeBytes: number;
}

export interface SlashCommandItem {
  id: 'plan-on' | 'plan-off' | 'daily' | 'dev' | 'escape-plan';
  command: string;
  title: string;
  description: string;
}

const SLASH_COMMANDS: SlashCommandItem[] = [
  { id: 'plan-on', command: '/plan', title: '进入 Plan Mode', description: '切换为只读规划模式，禁用脚本与代码修改工具' },
  { id: 'plan-off', command: '/plan off', title: '退出 Plan Mode', description: '经控制面确认退出只读模式，恢复当前模式工具' },
  { id: 'daily', command: '/daily', title: '新建日常助手对话', description: '创建一个不可变 Daily 模式新工作区会话' },
  { id: 'dev', command: '/dev', title: '新建开发模式对话', description: '创建一个不可变 Development 模式新工作区会话' },
  { id: 'escape-plan', command: '//plan', title: '发送字面 /plan 文本', description: '转义并作为普通聊天内容发送，不触发模式切换' },
];

interface ComposerProps {
  disabled: boolean;
  disabledReason?: string;
  isTurnRunning: boolean;
  cancelling?: boolean;
  turnNotice: string | null;
  workspacePath?: string | null;
  supportsMultimodal?: boolean;
  externalAttachment?: WorkspaceAttachmentRequest | null;
  workMode?: SessionWorkMode;
  planModeActive?: boolean;
  controlPlaneAvailable?: boolean;
  onSend: (text: string, attachments?: PendingAttachment[]) => Promise<void>;
  onCancel: () => Promise<void>;
  onSlashCommand?: (action: { kind: 'plan-on' | 'plan-off' | 'daily' | 'dev'; argument?: string }) => Promise<void>;
  onOpenDailyAssistant?: () => Promise<void>;
}

export const Composer: React.FC<ComposerProps> = ({
  disabled,
  disabledReason,
  isTurnRunning,
  cancelling = false,
  turnNotice,
  workspacePath = null,
  supportsMultimodal = false,
  externalAttachment = null,
  workMode = 'Development',
  planModeActive = false,
  controlPlaneAvailable = true,
  onSend,
  onCancel,
  onSlashCommand,
  onOpenDailyAssistant,
}) => {
  const { t } = useI18n();
  const [text, setText] = useState('');
  const [attachments, setAttachments] = useState<PendingAttachment[]>([]);
  const [attachmentError, setAttachmentError] = useState<string | null>(null);
  const [isDraggingOver, setIsDraggingOver] = useState(false);
  const [sending, setSending] = useState(false);
  const [isComposing, setIsComposing] = useState(false);
  const [slashDismissed, setSlashDismissed] = useState(false);
  const [selectedSlashIdx, setSelectedSlashIdx] = useState(0);
  const dragCounterRef = useRef(0);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);
  const lastExternalNonceRef = useRef<number>(0);
  const attachmentsRef = useRef<PendingAttachment[]>([]);
  const [lightboxImage, setLightboxImage] = useState<{ name: string; url: string } | null>(null);
  attachmentsRef.current = attachments;

  useEffect(() => {
    return () => {
      for (const item of attachmentsRef.current) {
        revokeAttachmentPreviewUrl(item.previewUrl);
      }
    };
  }, []);

  useEffect(() => {
    const el = textareaRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(180, Math.max(40, el.scrollHeight))}px`;
  }, [text]);

  useEffect(() => {
    if (!lightboxImage) return undefined;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setLightboxImage(null);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [lightboxImage]);

  const slashQuery = useMemo(() => {
    if (isComposing || slashDismissed) return null;
    if (!text.startsWith('/')) return null;
    if (text.startsWith('//')) {
      return text.includes(' ') ? null : text.toLowerCase();
    }
    const spaceIdx = text.indexOf(' ');
    if (spaceIdx > 0 && !text.toLowerCase().startsWith('/plan ')) {
      return null;
    }
    return text.toLowerCase();
  }, [text, isComposing, slashDismissed]);

  const filteredSlashCommands = useMemo(() => {
    if (slashQuery === null) return [];
    if (slashQuery === '/') return SLASH_COMMANDS;
    return SLASH_COMMANDS.filter((item) => item.command.toLowerCase().startsWith(slashQuery));
  }, [slashQuery]);

  const clampedSlashIdx =
    filteredSlashCommands.length > 0 ? Math.min(selectedSlashIdx, filteredSlashCommands.length - 1) : 0;

  useEffect(() => {
    if (!externalAttachment || externalAttachment.nonce === lastExternalNonceRef.current) {
      return;
    }
    lastExternalNonceRef.current = externalAttachment.nonce;
    setAttachmentError(null);
    if (!supportsMultimodal && isImageAttachmentFile(externalAttachment.name)) {
      setAttachmentError(
        `当前模型未启用多模态能力，无法读取图片“${externalAttachment.name}”。请在「设置 -> 模型配置」中配置支持多模态的模型。`,
      );
      return;
    }
    setAttachments((prev) => {
      if (prev.some((a) => a.workspaceRelativePath === externalAttachment.workspaceRelativePath)) {
        return prev;
      }
      if (prev.length >= MAX_ATTACHMENTS_PER_MESSAGE) {
        setAttachmentError(`单次最多添加 ${MAX_ATTACHMENTS_PER_MESSAGE} 个附件。`);
        return prev;
      }
      return [
        ...prev,
        {
          id: `ws-${externalAttachment.nonce}`,
          name: externalAttachment.name,
          sizeBytes: externalAttachment.sizeBytes,
          contentType: 'application/octet-stream',
          file: new File([], externalAttachment.name),
          workspaceRelativePath: externalAttachment.workspaceRelativePath,
        },
      ];
    });
  }, [externalAttachment, supportsMultimodal]);

  const addFiles = async (fileList: FileList | File[]) => {
    const incoming = Array.from(fileList);
    if (incoming.length === 0) return;

    setAttachmentError(null);

    const nativePaths = incoming.map((file) => window.codexflow?.getPathForFile?.(file) ?? null);
    const pathsToClassify = nativePaths.filter((p): p is string => Boolean(p));
    let classifiedByPath = new Map<
      string,
      { isDirectory: boolean; workspaceRelativePath: string | null; exists: boolean }
    >();
    if (pathsToClassify.length > 0 && window.codexflow?.classifyDroppedFiles) {
      try {
        const classified = await window.codexflow.classifyDroppedFiles({
          filePaths: pathsToClassify,
          workspacePath,
        });
        classifiedByPath = new Map(classified.map((item) => [item.filePath, item]));
      } catch {
        classifiedByPath = new Map();
      }
    }

    const errors: string[] = [];
    setAttachments((prev) => {
      const next = [...prev];
      const existingKeys = new Set(
        prev.map((a) =>
          a.workspaceRelativePath
            ? `ws:${a.workspaceRelativePath}`
            : `file:${a.name}:${a.sizeBytes}:${a.file.lastModified}`,
        ),
      );

      for (let i = 0; i < incoming.length; i++) {
        const file = incoming[i]!;
        const nativePath = nativePaths[i];
        const info = nativePath ? classifiedByPath.get(nativePath) : undefined;

        if (info?.isDirectory) {
          errors.push(`“${file.name}”是文件夹，暂仅支持添加文件。`);
          continue;
        }
        if (file.size <= 0) {
          errors.push(`“${file.name}”为空文件（0 B），已跳过。`);
          continue;
        }
        if (file.size > MAX_ATTACHMENT_BYTES) {
          errors.push(`“${file.name}”超过 ${MAX_ATTACHMENT_BYTES / (1024 * 1024)} MB 上限。`);
          continue;
        }
        if (!supportsMultimodal && isImageAttachmentFile(file.name, file.type)) {
          errors.push(
            `当前模型未启用多模态能力，无法读取图片“${file.name}”。请在「设置 -> 模型配置」中配置支持多模态的模型。`,
          );
          continue;
        }
        if (next.length >= MAX_ATTACHMENTS_PER_MESSAGE) {
          errors.push(`单次最多添加 ${MAX_ATTACHMENTS_PER_MESSAGE} 个附件。`);
          break;
        }

        const relPath = info?.workspaceRelativePath ?? null;
        const dedupeKey = relPath
          ? `ws:${relPath}`
          : `file:${file.name}:${file.size}:${file.lastModified}`;
        if (existingKeys.has(dedupeKey)) {
          continue;
        }
        existingKeys.add(dedupeKey);

        next.push({
          id: `${Date.now()}-${i}-${Math.random().toString(36).slice(2, 8)}`,
          name: file.name || 'attachment',
          sizeBytes: file.size,
          contentType: file.type || 'application/octet-stream',
          file,
          workspaceRelativePath: relPath,
          previewUrl: createAttachmentPreviewUrl(file),
        });
      }
      return next;
    });

    if (errors.length > 0) {
      setAttachmentError(errors.join(' '));
    }
  };

  const removeAttachment = (id: string) => {
    setAttachments((prev) => {
      const removed = prev.find((item) => item.id === id);
      revokeAttachmentPreviewUrl(removed?.previewUrl);
      return prev.filter((item) => item.id !== id);
    });
    setAttachmentError(null);
  };

  const clearAttachmentsWithUrls = () => {
    setAttachments((prev) => {
      for (const item of prev) {
        revokeAttachmentPreviewUrl(item.previewUrl);
      }
      return [];
    });
  };

  const executeSlashCommand = async (trimmed: string): Promise<boolean> => {
    if (!onSlashCommand) return false;
    const lower = trimmed.toLowerCase();
    if (lower === '/plan' || lower === '/plan on') {
      await onSlashCommand({ kind: 'plan-on' });
      setText('');
      return true;
    }
    if (lower === '/plan off') {
      await onSlashCommand({ kind: 'plan-off' });
      setText('');
      return true;
    }
    if (lower === '/daily' || lower.startsWith('/daily ')) {
      await onSlashCommand({ kind: 'daily', argument: trimmed.slice(6).trim() || undefined });
      setText('');
      return true;
    }
    if (lower === '/dev' || lower.startsWith('/dev ')) {
      await onSlashCommand({ kind: 'dev', argument: trimmed.slice(4).trim() || undefined });
      setText('');
      return true;
    }
    return false;
  };

  const applySlashSelection = async (item: SlashCommandItem, mode: 'fill' | 'execute' = 'execute') => {
    setSlashDismissed(true);
    if (item.id === 'escape-plan') {
      setText('/plan ');
      return;
    }
    if (mode === 'fill') {
      setText(item.id === 'plan-off' ? '/plan off' : `${item.command} `);
      return;
    }
    if (onSlashCommand && (item.id === 'plan-on' || item.id === 'plan-off' || item.id === 'daily' || item.id === 'dev')) {
      if (attachments.length > 0) {
        setAttachmentError('当前已附加文件，请先移除附件后再执行斜杠命令，或使用 //plan 发送普通消息。');
        return;
      }
      setSending(true);
      try {
        await onSlashCommand({ kind: item.id });
        setText('');
      } catch (err) {
        setAttachmentError(err instanceof Error ? err.message : String(err));
      } finally {
        setSending(false);
      }
      return;
    }
    setText(item.command);
  };

  const handleSubmit = async () => {
    const trimmed = text.trim();
    if ((!trimmed && attachments.length === 0) || disabled || isTurnRunning || sending) {
      return;
    }

    if (trimmed.startsWith('//')) {
      const unescaped = trimmed.slice(1);
      setSending(true);
      setAttachmentError(null);
      const batch = attachments;
      try {
        await onSend(unescaped, batch);
        setText('');
        clearAttachmentsWithUrls();
      } catch (err) {
        setAttachmentError(err instanceof Error ? err.message : String(err));
      } finally {
        setSending(false);
      }
      return;
    }

    if (trimmed.startsWith('/') && onSlashCommand) {
      if (attachments.length > 0) {
        setAttachmentError('携带文件附件时不能触发 /plan、/daily、/dev 命令；请先移除附件，或输入 //plan ... 作为普通文本发送。');
        return;
      }
      setSending(true);
      setAttachmentError(null);
      try {
        const handled = await executeSlashCommand(trimmed);
        if (handled) {
          return;
        }
      } catch (err) {
        setAttachmentError(err instanceof Error ? err.message : String(err));
        return;
      } finally {
        setSending(false);
      }
    }

    setSending(true);
    setAttachmentError(null);
    const batch = attachments;
    try {
      await onSend(trimmed, batch);
      setText('');
      clearAttachmentsWithUrls();
    } catch (err) {
      if (err instanceof Error && err.message && batch.length > 0) {
        setAttachmentError(err.message);
      }
      // Preserve draft text & attachments when send fails so user can retry.
    } finally {
      setSending(false);
    }
  };

  const handleDragEnter = (e: React.DragEvent<HTMLDivElement>) => {
    if (!e.dataTransfer?.types?.includes('Files')) return;
    e.preventDefault();
    e.stopPropagation();
    dragCounterRef.current += 1;
    setIsDraggingOver(true);
  };

  const handleDragOver = (e: React.DragEvent<HTMLDivElement>) => {
    if (!e.dataTransfer?.types?.includes('Files')) return;
    e.preventDefault();
    e.stopPropagation();
    e.dataTransfer.dropEffect = 'copy';
  };

  const handleDragLeave = (e: React.DragEvent<HTMLDivElement>) => {
    if (!e.dataTransfer?.types?.includes('Files')) return;
    e.preventDefault();
    e.stopPropagation();
    dragCounterRef.current = Math.max(0, dragCounterRef.current - 1);
    if (dragCounterRef.current === 0) {
      setIsDraggingOver(false);
    }
  };

  const handleDrop = (e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    e.stopPropagation();
    dragCounterRef.current = 0;
    setIsDraggingOver(false);
    if (disabled) return;
    if (e.dataTransfer?.files && e.dataTransfer.files.length > 0) {
      void addFiles(e.dataTransfer.files);
    }
  };

  const handlePaste = (e: React.ClipboardEvent<HTMLTextAreaElement>) => {
    if (disabled || sending) return;
    const imageFiles = extractClipboardImageFiles(e.clipboardData);
    if (imageFiles.length === 0) return;

    const plainText = e.clipboardData?.getData?.('text/plain') ?? '';
    if (!plainText.trim()) {
      e.preventDefault();
    }
    void addFiles(imageFiles);
  };

  const canSend = !disabled && !sending && (Boolean(text.trim()) || attachments.length > 0);

  return (
    <div className="border-t border-slate-800 bg-slate-950/90 px-4 py-3">
      {turnNotice && (
        <div className="mb-2 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-1.5 text-xs text-amber-200">
          {turnNotice}
        </div>
      )}

      {attachmentError && (
        <div
          role="alert"
          className="mb-2 rounded-lg border border-rose-500/30 bg-rose-500/10 px-3 py-1.5 text-xs text-rose-200"
        >
          {attachmentError}
        </div>
      )}

      <div
        data-testid="composer-dropzone"
        onDragEnter={handleDragEnter}
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
        className={`relative rounded-2xl border px-3 py-2.5 shadow-sm transition-colors ${
          isDraggingOver
            ? 'border-dashed border-sky-400 bg-sky-950/30'
            : 'border-slate-800 bg-slate-900/90 focus-within:border-indigo-500/60'
        }`}
      >
        {isDraggingOver && (
          <div className="pointer-events-none absolute inset-0 z-10 flex items-center justify-center rounded-2xl bg-slate-950/80 backdrop-blur-[1px]">
            <div className="flex items-center gap-2 rounded-lg border border-sky-400/50 bg-sky-500/15 px-3 py-2 text-xs font-medium text-sky-200">
              <Upload className="h-4 w-4 text-sky-400" />
              <span>松开以将本地文件附加到当前会话</span>
            </div>
          </div>
        )}

        {attachments.length > 0 && (
          <div data-testid="composer-attachments" className="mb-2 flex flex-wrap items-center gap-2 pt-0.5">
            {attachments.map((item) => {
              const isImage = Boolean(item.previewUrl) || isImageAttachmentFile(item.name, item.contentType);
              const tooltipTitle = item.workspaceRelativePath
                ? `${item.name} · ${formatFileSize(item.sizeBytes)}（工作区内: ${item.workspaceRelativePath}）`
                : `${item.name} · ${formatFileSize(item.sizeBytes)}`;

              if (isImage) {
                return (
                  <div
                    key={item.id}
                    data-testid={`image-attachment-card-${item.name}`}
                    title={tooltipTitle}
                    className="group relative flex h-24 min-w-[78px] max-w-[168px] items-center justify-center overflow-hidden rounded-xl border border-slate-700/80 bg-slate-950 shadow-sm"
                  >
                    {item.previewUrl ? (
                      <button
                        type="button"
                        aria-label={`放大预览 ${item.name}`}
                        onClick={() => setLightboxImage({ name: item.name, url: item.previewUrl! })}
                        className="h-full w-full cursor-zoom-in focus:outline-none"
                      >
                        <img
                          src={item.previewUrl}
                          alt={item.name}
                          className="h-full w-auto max-w-[168px] object-cover"
                        />
                      </button>
                    ) : (
                      <div className="flex h-full w-24 items-center justify-center bg-slate-900/80 text-sky-400">
                        <ImageIcon className="h-6 w-6 opacity-80" />
                      </div>
                    )}

                    <button
                      type="button"
                      aria-label={`移除附件 ${item.name}`}
                      onClick={() => removeAttachment(item.id)}
                      className="absolute right-1.5 top-1.5 flex h-5 w-5 items-center justify-center rounded-full bg-slate-950/85 text-slate-200 opacity-0 ring-1 ring-slate-600/60 transition hover:bg-slate-900 hover:text-slate-100 group-hover:opacity-100 group-focus-within:opacity-100"
                    >
                      <X className="h-3 w-3" />
                    </button>

                    <div className="pointer-events-none absolute inset-x-0 bottom-0 bg-gradient-to-t from-slate-950/95 via-slate-950/75 to-transparent px-2 pb-1.5 pt-4 opacity-0 transition-opacity duration-150 group-hover:opacity-100 group-focus-within:opacity-100">
                      <div className="truncate text-[11px] font-medium text-white">{item.name}</div>
                      <div className="text-[10px] text-white/80">
                        {formatFileSize(item.sizeBytes)}
                        {item.workspaceRelativePath ? ' · 工作区' : ''}
                      </div>
                    </div>
                  </div>
                );
              }

              const visualMeta = getAttachmentVisualMeta(item.name, item.contentType);
              return (
                <div
                  key={item.id}
                  data-testid={`file-attachment-card-${item.name}`}
                  data-attachment-category={visualMeta.category}
                  title={tooltipTitle}
                  className="group relative flex h-24 w-32 flex-col justify-between overflow-hidden rounded-xl border border-slate-700/80 bg-slate-950/95 text-slate-200 shadow-sm"
                >
                  <div className="relative flex flex-1 items-center justify-center bg-slate-950/80 px-2 pt-1">
                    <div
                      className={`flex h-10 w-10 items-center justify-center rounded-xl ${visualMeta.surfaceBgClass}`}
                    >
                      <AttachmentTypeIcon
                        category={visualMeta.category}
                        className={`h-5 w-5 ${visualMeta.iconColorClass}`}
                      />
                    </div>
                    <span
                      className={`absolute bottom-1.5 right-2 rounded border px-1 py-0.2 font-mono text-[9px] font-semibold ${visualMeta.badgeClass}`}
                    >
                      {visualMeta.badgeLabel}
                    </span>
                  </div>

                  {item.workspaceRelativePath && (
                    <span className="absolute left-1.5 top-1.5 rounded bg-emerald-500/20 px-1.5 py-0.2 text-[9px] font-medium text-emerald-300 ring-1 ring-emerald-500/30">
                      工作区
                    </span>
                  )}

                  <button
                    type="button"
                    aria-label={`移除附件 ${item.name}`}
                    onClick={() => removeAttachment(item.id)}
                    className="absolute right-1.5 top-1.5 flex h-5 w-5 items-center justify-center rounded-full bg-slate-950/85 text-slate-200 opacity-0 ring-1 ring-slate-600/60 transition hover:bg-slate-900 hover:text-slate-100 group-hover:opacity-100 group-focus-within:opacity-100"
                  >
                    <X className="h-3 w-3" />
                  </button>

                  <div className="flex items-center gap-1.5 border-t border-slate-800/80 bg-slate-900/90 px-2 py-1.5">
                    <AttachmentTypeIcon
                      category={visualMeta.category}
                      className={`h-3.5 w-3.5 shrink-0 ${visualMeta.iconColorClass}`}
                    />
                    <span className="truncate text-[11px] font-medium text-slate-200">{item.name}</span>
                  </div>
                </div>
              );
            })}
          </div>
        )}

        {planModeActive && (
          <div
            data-testid="plan-mode-banner"
            className="mx-2 mb-1.5 flex items-center justify-between rounded-lg border border-amber-500/40 bg-amber-500/10 px-2.5 py-1.5 text-xs text-amber-200"
          >
            <div className="flex items-center gap-1.5">
              <ShieldCheck className="h-3.5 w-3.5 text-amber-400" />
              <span>Plan Mode（只读规划模式）：已禁用脚本与代码修改工具，输入 `/plan off` 或点击右侧按钮可退出。</span>
            </div>
            {onSlashCommand && (
              <button
                type="button"
                disabled={!controlPlaneAvailable || sending}
                title={controlPlaneAvailable ? '经控制面确认退出只读规划模式' : '当前为只读连接，无法退出 Plan Mode'}
                onClick={() => void onSlashCommand({ kind: 'plan-off' })}
                className="shrink-0 rounded border border-amber-500/40 bg-amber-500/20 px-2 py-0.5 text-[11px] font-medium text-amber-100 hover:bg-amber-500/30 disabled:opacity-40"
              >
                退出 Plan Mode
              </button>
            )}
          </div>
        )}

        {filteredSlashCommands.length > 0 && (
          <div
            role="listbox"
            aria-label="斜杠命令自动补全"
            className="mx-2 mb-2 rounded-lg border border-slate-700 bg-slate-950 p-1 shadow-xl"
          >
            {filteredSlashCommands.map((item, idx) => (
              <button
                key={item.id}
                type="button"
                role="option"
                aria-selected={idx === clampedSlashIdx}
                onMouseDown={(e) => {
                  e.preventDefault();
                  void applySlashSelection(item);
                }}
                className={`flex w-full items-center justify-between rounded-md px-2.5 py-1.5 text-left text-xs transition ${
                  idx === clampedSlashIdx ? 'bg-indigo-600/30 text-indigo-100' : 'text-slate-300 hover:bg-slate-800'
                }`}
              >
                <span className="font-mono font-semibold text-indigo-300">{item.command}</span>
                <span className="ml-3 truncate text-[11px] text-slate-400">
                  {item.title} · {item.description}
                </span>
              </button>
            ))}
          </div>
        )}

        <textarea
          ref={textareaRef}
          rows={1}
          value={text}
          disabled={disabled}
          onPaste={handlePaste}
          onCompositionStart={() => setIsComposing(true)}
          onCompositionEnd={() => setIsComposing(false)}
          onChange={(e) => {
            setText(e.target.value);
            setSlashDismissed(false);
            setSelectedSlashIdx(0);
          }}
          onKeyDown={(e) => {
            if (isComposing || e.nativeEvent.isComposing || e.keyCode === 229) {
              return;
            }
            if (filteredSlashCommands.length > 0) {
              if (e.key === 'ArrowDown') {
                e.preventDefault();
                setSelectedSlashIdx((prev) => (prev + 1) % filteredSlashCommands.length);
                return;
              }
              if (e.key === 'ArrowUp') {
                e.preventDefault();
                setSelectedSlashIdx((prev) => (prev - 1 + filteredSlashCommands.length) % filteredSlashCommands.length);
                return;
              }
              if (e.key === 'Escape') {
                e.preventDefault();
                setSlashDismissed(true);
                return;
              }
              if (e.key === 'Tab' || (e.key === 'Enter' && !e.shiftKey)) {
                const chosen = filteredSlashCommands[clampedSlashIdx];
                if (chosen) {
                  e.preventDefault();
                  void applySlashSelection(chosen, e.key === 'Tab' ? 'fill' : 'execute');
                  return;
                }
              }
            }
            if (e.key === 'Enter' && !e.shiftKey) {
              e.preventDefault();
              void handleSubmit();
            }
          }}
          placeholder={
            disabled
              ? disabledReason || '当前会话暂不可用，请先选择或新建工作区...'
              : t('composer.placeholder')
          }
          className="max-h-[180px] min-h-[40px] w-full resize-none overflow-y-auto bg-transparent px-1 py-1 text-sm leading-relaxed text-slate-100 placeholder:text-slate-500 focus:outline-none disabled:opacity-50"
        />

        <input
          ref={fileInputRef}
          type="file"
          multiple
          aria-label="选择本地文件附件"
          className="hidden"
          onChange={(e) => {
            if (e.target.files && e.target.files.length > 0) {
              void addFiles(e.target.files);
            }
            e.target.value = '';
          }}
        />

        <div className="mt-1.5 flex items-center justify-between pt-0.5">
          <div className="flex items-center gap-1.5 text-xs text-slate-400">
            <button
              type="button"
              disabled={disabled || sending}
              onClick={() => fileInputRef.current?.click()}
              title="添加文件（也可拖拽文件或 Ctrl+V 粘贴截图）"
              aria-label="添加文件"
              className="inline-flex h-7 w-7 items-center justify-center rounded-full text-slate-300 transition hover:bg-slate-800 hover:text-slate-100 disabled:opacity-40"
            >
              <Plus className="h-4 w-4" />
            </button>
            <WorkModeMenu
              workMode={workMode}
              planModeActive={planModeActive}
              controlPlaneAvailable={controlPlaneAvailable}
              placement="top"
              disabled={sending}
              testId="composer-work-mode-badge"
              onOpenDailyAssistant={onOpenDailyAssistant}
              onCreateDailySession={onSlashCommand ? () => onSlashCommand({ kind: 'daily' }) : undefined}
              onCreateDevSession={onSlashCommand ? () => onSlashCommand({ kind: 'dev' }) : undefined}
              onTogglePlanMode={
                onSlashCommand
                  ? () => onSlashCommand({ kind: planModeActive ? 'plan-off' : 'plan-on' })
                  : undefined
              }
            />
          </div>

          <div className="flex items-center gap-2">
            {isTurnRunning ? (
              <button
                type="button"
                disabled={cancelling}
                onClick={() => void onCancel()}
                aria-label={cancelling ? '停止中' : t('composer.stop')}
                title={cancelling ? '停止中…' : t('composer.stop')}
                className="inline-flex h-8 w-8 items-center justify-center rounded-full bg-rose-600 text-white transition hover:bg-rose-500 disabled:opacity-50"
              >
                <Square className="h-3.5 w-3.5 fill-current" />
                <span className="sr-only">{cancelling ? '停止中…' : t('composer.stop')}</span>
              </button>
            ) : (
              <button
                type="button"
                disabled={!canSend}
                onClick={() => void handleSubmit()}
                aria-label={t('composer.send')}
                title={t('composer.send')}
                className="inline-flex h-8 w-8 items-center justify-center rounded-full bg-indigo-600 text-white transition hover:bg-indigo-500 disabled:bg-slate-800 disabled:text-slate-500 disabled:opacity-70"
              >
                <ArrowUp className="h-4 w-4" />
                <span className="sr-only">{t('composer.send')}</span>
              </button>
            )}
          </div>
        </div>
      </div>

      {lightboxImage && (
        <div
          role="dialog"
          aria-modal="true"
          aria-label={`预览图片 ${lightboxImage.name}`}
          onClick={() => setLightboxImage(null)}
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 p-6 backdrop-blur-sm"
        >
          <div
            onClick={(e) => e.stopPropagation()}
            className="relative max-h-[85vh] max-w-[85vw] overflow-hidden rounded-2xl border border-slate-700 bg-slate-950 p-2 shadow-2xl"
          >
            <button
              type="button"
              aria-label="关闭图片预览"
              onClick={() => setLightboxImage(null)}
              className="absolute right-3 top-3 flex h-7 w-7 items-center justify-center rounded-full bg-slate-900/90 text-slate-200 ring-1 ring-slate-600 hover:bg-slate-800 hover:text-slate-100"
            >
              <X className="h-4 w-4" />
            </button>
            <img
              src={lightboxImage.url}
              alt={lightboxImage.name}
              className="max-h-[78vh] max-w-[80vw] rounded-xl object-contain"
            />
            <div className="mt-1.5 truncate px-2 text-center text-xs text-slate-400">
              {lightboxImage.name}
            </div>
          </div>
        </div>
      )}
    </div>
  );
};
