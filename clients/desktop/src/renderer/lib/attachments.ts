import type { components } from '@shared/contracts/api.generated';

export type ChatAttachment = components['schemas']['ChatAttachmentDto'];
export type UploadedAttachment = components['schemas']['UploadedAttachmentDto'];

export const MAX_ATTACHMENT_BYTES = 20 * 1024 * 1024;
export const MAX_ATTACHMENTS_PER_MESSAGE = 10;
export const DEFAULT_ATTACHMENT_PROMPT = '请查看并处理以下附带的文件。';

const IMAGE_EXT_RE = /\.(png|jpe?g|webp|gif|bmp)$/i;
const GENERIC_CLIPBOARD_NAME_RE = /^(image|blob|clipboard|pasted[-_ ]?image)(\.[a-z0-9]+)?$/i;

const MIME_TO_EXT: Record<string, string> = {
  'image/png': 'png',
  'image/jpeg': 'jpg',
  'image/jpg': 'jpg',
  'image/webp': 'webp',
  'image/gif': 'gif',
  'image/bmp': 'bmp',
};

export function isImageAttachmentFile(name: string, contentType?: string | null): boolean {
  if (contentType && contentType.trim().toLowerCase().startsWith('image/')) {
    return true;
  }
  return IMAGE_EXT_RE.test(name.trim());
}

export type AttachmentCategory =
  | 'image'
  | 'pdf'
  | 'word'
  | 'spreadsheet'
  | 'presentation'
  | 'json'
  | 'code'
  | 'archive'
  | 'audio'
  | 'video'
  | 'text'
  | 'generic';

export interface AttachmentVisualMeta {
  category: AttachmentCategory;
  badgeLabel: string;
  iconColorClass: string;
  surfaceBgClass: string;
  badgeClass: string;
}

const CATEGORY_SPECS: Record<AttachmentCategory, Omit<AttachmentVisualMeta, 'category' | 'badgeLabel'> & { defaultLabel: string }> = {
  image: {
    defaultLabel: 'IMG',
    iconColorClass: 'text-sky-400',
    surfaceBgClass: 'bg-sky-500/10',
    badgeClass: 'bg-sky-500/20 text-sky-300 border-sky-500/30',
  },
  pdf: {
    defaultLabel: 'PDF',
    iconColorClass: 'text-rose-400',
    surfaceBgClass: 'bg-rose-500/10',
    badgeClass: 'bg-rose-500/20 text-rose-300 border-rose-500/30',
  },
  word: {
    defaultLabel: 'DOC',
    iconColorClass: 'text-blue-400',
    surfaceBgClass: 'bg-blue-500/10',
    badgeClass: 'bg-blue-500/20 text-blue-300 border-blue-500/30',
  },
  spreadsheet: {
    defaultLabel: 'XLS',
    iconColorClass: 'text-emerald-400',
    surfaceBgClass: 'bg-emerald-500/10',
    badgeClass: 'bg-emerald-500/20 text-emerald-300 border-emerald-500/30',
  },
  presentation: {
    defaultLabel: 'PPT',
    iconColorClass: 'text-amber-400',
    surfaceBgClass: 'bg-amber-500/10',
    badgeClass: 'bg-amber-500/20 text-amber-300 border-amber-500/30',
  },
  json: {
    defaultLabel: 'JSON',
    iconColorClass: 'text-cyan-400',
    surfaceBgClass: 'bg-cyan-500/10',
    badgeClass: 'bg-cyan-500/20 text-cyan-300 border-cyan-500/30',
  },
  code: {
    defaultLabel: 'CODE',
    iconColorClass: 'text-violet-400',
    surfaceBgClass: 'bg-violet-500/10',
    badgeClass: 'bg-violet-500/20 text-violet-300 border-violet-500/30',
  },
  archive: {
    defaultLabel: 'ZIP',
    iconColorClass: 'text-yellow-400',
    surfaceBgClass: 'bg-yellow-500/10',
    badgeClass: 'bg-yellow-500/20 text-yellow-300 border-yellow-500/30',
  },
  audio: {
    defaultLabel: 'AUDIO',
    iconColorClass: 'text-pink-400',
    surfaceBgClass: 'bg-pink-500/10',
    badgeClass: 'bg-pink-500/20 text-pink-300 border-pink-500/30',
  },
  video: {
    defaultLabel: 'VIDEO',
    iconColorClass: 'text-indigo-400',
    surfaceBgClass: 'bg-indigo-500/10',
    badgeClass: 'bg-indigo-500/20 text-indigo-300 border-indigo-500/30',
  },
  text: {
    defaultLabel: 'TXT',
    iconColorClass: 'text-sky-300',
    surfaceBgClass: 'bg-sky-500/10',
    badgeClass: 'bg-sky-500/15 text-sky-300 border-sky-500/30',
  },
  generic: {
    defaultLabel: 'FILE',
    iconColorClass: 'text-slate-400',
    surfaceBgClass: 'bg-slate-800/70',
    badgeClass: 'bg-slate-800 text-slate-300 border-slate-700',
  },
};

const EXT_TO_CATEGORY: Record<string, AttachmentCategory> = {
  pdf: 'pdf',
  doc: 'word',
  docx: 'word',
  rtf: 'word',
  odt: 'word',
  xls: 'spreadsheet',
  xlsx: 'spreadsheet',
  xlsm: 'spreadsheet',
  csv: 'spreadsheet',
  tsv: 'spreadsheet',
  ods: 'spreadsheet',
  ppt: 'presentation',
  pptx: 'presentation',
  odp: 'presentation',
  key: 'presentation',
  json: 'json',
  jsonc: 'json',
  jsonl: 'json',
  yaml: 'json',
  yml: 'json',
  xml: 'json',
  toml: 'json',
  ts: 'code',
  tsx: 'code',
  js: 'code',
  jsx: 'code',
  mjs: 'code',
  cjs: 'code',
  cs: 'code',
  py: 'code',
  go: 'code',
  rs: 'code',
  java: 'code',
  kt: 'code',
  cpp: 'code',
  c: 'code',
  h: 'code',
  hpp: 'code',
  html: 'code',
  css: 'code',
  scss: 'code',
  sql: 'code',
  sh: 'code',
  ps1: 'code',
  bat: 'code',
  cmd: 'code',
  vue: 'code',
  svelte: 'code',
  zip: 'archive',
  rar: 'archive',
  '7z': 'archive',
  tar: 'archive',
  gz: 'archive',
  tgz: 'archive',
  mp3: 'audio',
  wav: 'audio',
  m4a: 'audio',
  flac: 'audio',
  ogg: 'audio',
  mp4: 'video',
  mov: 'video',
  mkv: 'video',
  webm: 'video',
  avi: 'video',
  md: 'text',
  markdown: 'text',
  txt: 'text',
  log: 'text',
  ini: 'text',
  conf: 'text',
  env: 'text',
};

/**
 * Classifies an attachment by extension and MIME type so non-image documents display
 * category-specific icons and badges.
 */
export function getAttachmentVisualMeta(
  name: string,
  contentType?: string | null,
): AttachmentVisualMeta {
  const trimmed = name.trim();
  const dotIdx = trimmed.lastIndexOf('.');
  const ext = dotIdx >= 0 && dotIdx < trimmed.length - 1 ? trimmed.slice(dotIdx + 1).toLowerCase() : '';
  const mime = (contentType ?? '').trim().toLowerCase();

  let category: AttachmentCategory = 'generic';
  if (isImageAttachmentFile(trimmed, contentType)) {
    category = 'image';
  } else if (ext && EXT_TO_CATEGORY[ext]) {
    category = EXT_TO_CATEGORY[ext]!;
  } else if (mime === 'application/pdf') {
    category = 'pdf';
  } else if (mime.includes('wordprocessingml') || mime === 'application/msword') {
    category = 'word';
  } else if (mime.includes('spreadsheetml') || mime === 'application/vnd.ms-excel' || mime === 'text/csv') {
    category = 'spreadsheet';
  } else if (mime.includes('presentationml') || mime === 'application/vnd.ms-powerpoint') {
    category = 'presentation';
  } else if (mime.includes('json') || mime.includes('yaml') || mime.includes('xml')) {
    category = 'json';
  } else if (mime.startsWith('audio/')) {
    category = 'audio';
  } else if (mime.startsWith('video/')) {
    category = 'video';
  } else if (mime.startsWith('text/')) {
    category = 'text';
  }

  const spec = CATEGORY_SPECS[category];
  const badgeLabel = ext ? ext.toUpperCase().slice(0, 6) : spec.defaultLabel;
  return {
    category,
    badgeLabel,
    iconColorClass: spec.iconColorClass,
    surfaceBgClass: spec.surfaceBgClass,
    badgeClass: spec.badgeClass,
  };
}

function buildClipboardImageFilename(mimeType: string, index: number): string {
  const normalizedMime = mimeType.trim().toLowerCase();
  const ext = MIME_TO_EXT[normalizedMime] ?? 'png';
  const now = new Date();
  const stamp = [
    String(now.getHours()).padStart(2, '0'),
    String(now.getMinutes()).padStart(2, '0'),
    String(now.getSeconds()).padStart(2, '0'),
  ].join('');
  return `clipboard-${stamp}-${index + 1}.${ext}`;
}

/**
 * Extracts image files from a paste event's DataTransfer, normalizing anonymous screenshot
 * names while preserving original File instances for named files (so Electron path lookup works).
 */
export function extractClipboardImageFiles(clipboardData: DataTransfer | null | undefined): File[] {
  if (!clipboardData) return [];

  const collected: File[] = [];
  const items = clipboardData.items ? Array.from(clipboardData.items) : [];
  for (const item of items) {
    if (item.kind !== 'file' || !item.type.toLowerCase().startsWith('image/')) {
      continue;
    }
    const file = item.getAsFile();
    if (file) {
      collected.push(file);
    }
  }

  if (collected.length === 0 && clipboardData.files && clipboardData.files.length > 0) {
    for (const file of Array.from(clipboardData.files)) {
      if (isImageAttachmentFile(file.name, file.type)) {
        collected.push(file);
      }
    }
  }

  return collected.map((file, idx) => {
    const hasSpecificName =
      Boolean(file.name.trim()) &&
      !GENERIC_CLIPBOARD_NAME_RE.test(file.name.trim()) &&
      IMAGE_EXT_RE.test(file.name.trim());
    if (hasSpecificName || typeof File !== 'function') {
      return file;
    }
    const generatedName = buildClipboardImageFilename(file.type || 'image/png', idx);
    return new File([file], generatedName, {
      type: file.type || 'image/png',
      lastModified: file.lastModified || Date.now(),
    });
  });
}

export function createAttachmentPreviewUrl(file: File): string | undefined {
  if (
    file.size <= 0 ||
    !isImageAttachmentFile(file.name, file.type) ||
    typeof URL === 'undefined' ||
    typeof URL.createObjectURL !== 'function'
  ) {
    return undefined;
  }
  try {
    return URL.createObjectURL(file);
  } catch {
    return undefined;
  }
}

export function revokeAttachmentPreviewUrl(previewUrl?: string): void {
  if (!previewUrl || typeof URL === 'undefined' || typeof URL.revokeObjectURL !== 'function') {
    return;
  }
  try {
    URL.revokeObjectURL(previewUrl);
  } catch {
    // 忽略对象 URL 释放异常
  }
}

export interface PendingAttachment {
  id: string;
  name: string;
  sizeBytes: number;
  contentType: string;
  file: File;
  workspaceRelativePath: string | null;
  previewUrl?: string;
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
