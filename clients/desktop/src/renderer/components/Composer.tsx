import React, { useEffect, useRef, useState } from 'react';
import { FileText, Paperclip, Send, Square, Upload, X } from 'lucide-react';
import {
  formatFileSize,
  isImageAttachmentFile,
  MAX_ATTACHMENT_BYTES,
  MAX_ATTACHMENTS_PER_MESSAGE,
  type PendingAttachment,
} from '../lib/attachments';
import { useI18n } from '../lib/i18n';
import type { ConversationMode } from '../stores/useAppStore';

export interface WorkspaceAttachmentRequest {
  nonce: number;
  name: string;
  workspaceRelativePath: string;
  sizeBytes: number;
}

interface ComposerProps {
  disabled: boolean;
  isTurnRunning: boolean;
  conversationMode: ConversationMode;
  turnNotice: string | null;
  workspacePath?: string | null;
  supportsMultimodal?: boolean;
  externalAttachment?: WorkspaceAttachmentRequest | null;
  onChangeMode: (mode: ConversationMode) => void;
  onSend: (text: string, attachments?: PendingAttachment[]) => Promise<void>;
  onCancel: () => Promise<void>;
}

export const Composer: React.FC<ComposerProps> = ({
  disabled,
  isTurnRunning,
  conversationMode,
  turnNotice,
  workspacePath = null,
  supportsMultimodal = false,
  externalAttachment = null,
  onChangeMode,
  onSend,
  onCancel,
}) => {
  const { t } = useI18n();
  const [text, setText] = useState('');
  const [attachments, setAttachments] = useState<PendingAttachment[]>([]);
  const [attachmentError, setAttachmentError] = useState<string | null>(null);
  const [isDraggingOver, setIsDraggingOver] = useState(false);
  const [sending, setSending] = useState(false);
  const dragCounterRef = useRef(0);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const lastExternalNonceRef = useRef<number>(0);

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
        });
      }
      return next;
    });

    if (errors.length > 0) {
      setAttachmentError(errors.join(' '));
    }
  };

  const removeAttachment = (id: string) => {
    setAttachments((prev) => prev.filter((item) => item.id !== id));
    setAttachmentError(null);
  };

  const handleSubmit = async () => {
    const trimmed = text.trim();
    if ((!trimmed && attachments.length === 0) || disabled || isTurnRunning || sending) {
      return;
    }
    setSending(true);
    setAttachmentError(null);
    const batch = attachments;
    try {
      await onSend(trimmed, batch);
      setText('');
      setAttachments([]);
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
        className={`relative rounded-xl border bg-slate-900/90 p-2 transition-colors ${
          isDraggingOver
            ? 'border-dashed border-sky-400 bg-sky-950/30'
            : 'border-slate-800 focus-within:border-indigo-500/60'
        }`}
      >
        {isDraggingOver && (
          <div className="pointer-events-none absolute inset-0 z-10 flex items-center justify-center rounded-xl bg-slate-950/80 backdrop-blur-[1px]">
            <div className="flex items-center gap-2 rounded-lg border border-sky-400/50 bg-sky-500/15 px-3 py-2 text-xs font-medium text-sky-200">
              <Upload className="h-4 w-4 text-sky-400" />
              <span>松开以将本地文件附加到当前会话</span>
            </div>
          </div>
        )}

        {attachments.length > 0 && (
          <div data-testid="composer-attachments" className="mb-2 flex flex-wrap gap-1.5 px-1 pt-0.5">
            {attachments.map((item) => (
              <div
                key={item.id}
                title={item.workspaceRelativePath ? `工作区内文件: ${item.workspaceRelativePath}` : `${item.name} (发送时上传至会话工作区)`}
                className="inline-flex max-w-full items-center gap-1.5 rounded-lg border border-slate-700/80 bg-slate-950/90 px-2 py-1 text-xs text-slate-200"
              >
                <FileText className="h-3.5 w-3.5 shrink-0 text-sky-400" />
                <span className="max-w-[180px] truncate font-medium">{item.name}</span>
                <span className="shrink-0 text-[10px] text-slate-400">{formatFileSize(item.sizeBytes)}</span>
                {item.workspaceRelativePath && (
                  <span className="shrink-0 rounded bg-emerald-500/15 px-1 py-0.2 text-[10px] text-emerald-300">
                    工作区
                  </span>
                )}
                <button
                  type="button"
                  aria-label={`移除附件 ${item.name}`}
                  onClick={() => removeAttachment(item.id)}
                  className="rounded p-0.5 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
                >
                  <X className="h-3 w-3" />
                </button>
              </div>
            ))}
          </div>
        )}

        <textarea
          rows={3}
          value={text}
          disabled={disabled}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.nativeEvent.isComposing) {
              return;
            }
            if (e.key === 'Enter' && !e.shiftKey) {
              e.preventDefault();
              void handleSubmit();
            }
          }}
          placeholder={
            disabled
              ? '...'
              : t('composer.placeholder')
          }
          className="w-full resize-none bg-transparent px-2 py-1 text-sm text-slate-100 placeholder:text-slate-500 focus:outline-none disabled:opacity-50"
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

        <div className="mt-1.5 flex items-center justify-between border-t border-slate-800/80 pt-2">
          <div className="flex items-center gap-2 text-xs text-slate-400">
            <button
              type="button"
              disabled={disabled || sending}
              onClick={() => fileInputRef.current?.click()}
              title="添加本地文件附件（或直接拖拽文件到输入框）"
              aria-label="添加文件"
              className="inline-flex items-center gap-1 rounded border border-slate-700 bg-slate-950 px-2 py-1 text-xs text-slate-200 hover:bg-slate-800 disabled:opacity-40"
            >
              <Paperclip className="h-3.5 w-3.5" />
              <span>添加文件</span>
            </button>
            <span>会话模式：</span>
            <select
              aria-label="会话模式"
              value={conversationMode}
              onChange={(e) => onChangeMode(e.target.value as ConversationMode)}
              className="rounded border border-slate-700 bg-slate-950 px-2 py-1 text-xs text-slate-200 focus:outline-none"
            >
              <option value="reuse-master">继续主对话 (continue)</option>
              <option value="continue-current">继续当前线程</option>
              <option value="new-thread">新建对话线程 (new thread)</option>
            </select>
          </div>

          <div className="flex items-center gap-2">
            {isTurnRunning ? (
              <button
                type="button"
                onClick={() => void onCancel()}
                className="inline-flex items-center gap-1.5 rounded-lg bg-rose-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-rose-500"
              >
                <Square className="h-3.5 w-3.5 fill-current" />
                {t('composer.stop')}
              </button>
            ) : (
              <button
                type="button"
                disabled={!canSend}
                onClick={() => void handleSubmit()}
                className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
              >
                <Send className="h-3.5 w-3.5" />
                {sending ? '...' : t('composer.send')}
              </button>
            )}
          </div>
        </div>
      </div>
    </div>
  );
};
