import type { components } from '@shared/contracts/api.generated';

export type ChatAttachment = components['schemas']['ChatAttachmentDto'];
export type UploadedAttachment = components['schemas']['UploadedAttachmentDto'];

export const MAX_ATTACHMENT_BYTES = 20 * 1024 * 1024;
export const MAX_ATTACHMENTS_PER_MESSAGE = 10;
export const DEFAULT_ATTACHMENT_PROMPT = '请查看并处理以下附带的文件。';

export interface PendingAttachment {
  id: string;
  name: string;
  sizeBytes: number;
  contentType: string;
  file: File;
  workspaceRelativePath: string | null;
}

export function formatFileSize(sizeBytes: number | string | null | undefined): string {
  const numeric = typeof sizeBytes === 'string' ? Number(sizeBytes) : (sizeBytes ?? 0);
  if (!Number.isFinite(numeric) || numeric <= 0) {
    return '0 B';
  }
  if (numeric < 1024) {
    return `${numeric} B`;
  }
  if (numeric < 1024 * 1024) {
    return `${(numeric / 1024).toFixed(1)} KB`;
  }
  return `${(numeric / (1024 * 1024)).toFixed(2)} MB`;
}

const ATTACHMENT_TAG_RE = /<codexflow_attachments>([\s\S]*?)<\/codexflow_attachments>/;
const ATTACHMENT_SECTION_RE = /\r?\n*<codexflow_attachments>[\s\S]*?<\/codexflow_attachments>(?:\r?\n\[用户附带文件\][\s\S]*)?$/;

/**
 * Formats an optimistic user message so newly sent attachments render immediately in ChatView
 * using the exact same parser used for persisted snapshots.
 */
export function formatOptimisticMessageWithAttachments(
  messageText: string,
  attachments: ChatAttachment[],
): string {
  const baseText = messageText.trim() || DEFAULT_ATTACHMENT_PROMPT;
  if (attachments.length === 0) {
    return baseText;
  }
  return `${baseText}\n\n<codexflow_attachments>${JSON.stringify(attachments)}</codexflow_attachments>`;
}

/**
 * Extracts structured `<codexflow_attachments>` metadata embedded inside user messages.
 */
export function parseMessageWithAttachments(rawContent: string): {
  text: string;
  attachments: ChatAttachment[];
} {
  if (!rawContent) {
    return { text: '', attachments: [] };
  }

  const match = ATTACHMENT_TAG_RE.exec(rawContent);
  const rawJson = match?.[1];
  if (!rawJson) {
    return { text: rawContent, attachments: [] };
  }

  let attachments: ChatAttachment[] = [];
  try {
    const parsed = JSON.parse(rawJson) as unknown;
    if (Array.isArray(parsed)) {
      attachments = parsed
        .filter((item): item is Record<string, unknown> => Boolean(item && typeof item === 'object'))
        .map((item) => ({
          name: String(item.name ?? 'attachment'),
          relativePath: String(item.relativePath ?? ''),
          sizeBytes: Number(item.sizeBytes ?? 0),
          contentType: typeof item.contentType === 'string' ? item.contentType : 'application/octet-stream',
          isWorkspaceReference: Boolean(item.isWorkspaceReference),
        }))
        .filter((item) => item.relativePath.length > 0);
    }
  } catch {
    attachments = [];
  }

  const cleanedText = rawContent.replace(ATTACHMENT_SECTION_RE, '').trim();
  return {
    text: cleanedText,
    attachments,
  };
}
