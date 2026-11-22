export interface SseEventFrame {
  readonly id?: string;
  readonly event?: string;
  readonly data: string;
}

const SENSITIVE_IMAGE_KEY_SET = new Set([
  'previewimagebase64',
  'preview_image_base64',
  'imagebase64',
  'screenshotbase64',
]);

export function stripSensitiveImageFields(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map((item) => stripSensitiveImageFields(item));
  }
  if (value !== null && typeof value === 'object') {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      if (SENSITIVE_IMAGE_KEY_SET.has(k.toLowerCase())) {
        continue;
      }
      out[k] = stripSensitiveImageFields(v);
    }
    return out;
  }
  return value;
}

export function sanitizeSseDataJson(rawData: string): string {
  const trimmed = rawData.trim();
  if (!trimmed.startsWith('{') && !trimmed.startsWith('[')) {
    return rawData;
  }
  try {
    const parsed: unknown = JSON.parse(trimmed);
    const sanitized = stripSensitiveImageFields(parsed);
    return JSON.stringify(sanitized);
  } catch {
    return rawData;
  }
}

export function formatSseEvent(frame: SseEventFrame): string {
  let out = '';
  if (frame.id !== undefined && frame.id !== '') {
    out += `id: ${frame.id}\n`;
  }
  if (frame.event !== undefined && frame.event !== '') {
    out += `event: ${frame.event}\n`;
  }
  const lines = frame.data.split(/\r?\n/);
  for (const line of lines) {
    out += `data: ${line}\n`;
  }
  out += '\n';
  return out;
}

const MAX_SSE_BUFFER_BYTES = 1024 * 1024;

export class SseStreamParser {
  private buffer = '';
  private pendingCr = false;
  private currentId: string | undefined;
  private currentEvent: string | undefined;
  private dataLines: string[] = [];

  public push(chunkText: string): SseEventFrame[] {
    if (!chunkText) {
      return [];
    }

    let text = chunkText;
    if (this.pendingCr) {
      this.pendingCr = false;
      if (text.startsWith('\n')) {
        text = text.slice(1);
      }
    }

    if (text.endsWith('\r')) {
      this.pendingCr = true;
    }

    const normalized = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    if (this.buffer.length + normalized.length > MAX_SSE_BUFFER_BYTES) {
      this.buffer = '';
      this.pendingCr = false;
      this.resetFields();
      throw new Error('SSE frame buffer exceeded 1 MiB safety limit.');
    }

    this.buffer += normalized;
    const emitted: SseEventFrame[] = [];

    let newlineIdx = this.buffer.indexOf('\n');
    while (newlineIdx !== -1) {
      const line = this.buffer.slice(0, newlineIdx);
      this.buffer = this.buffer.slice(newlineIdx + 1);
      const frame = this.processLine(line);
      if (frame) {
        emitted.push(frame);
      }
      newlineIdx = this.buffer.indexOf('\n');
    }

    return emitted;
  }

  public flush(): SseEventFrame[] {
    // WHATWG EventSource: incomplete trailing frames without blank line terminator MUST NOT dispatch.
    this.buffer = '';
    this.pendingCr = false;
    this.resetFields();
    return [];
  }

  private processLine(line: string): SseEventFrame | null {
    if (line === '') {
      return this.dispatchEvent();
    }
    if (line.startsWith(':')) {
      return null;
    }
    const colonIdx = line.indexOf(':');
    const field = colonIdx === -1 ? line : line.slice(0, colonIdx);
    let value = colonIdx === -1 ? '' : line.slice(colonIdx + 1);
    if (value.startsWith(' ')) {
      value = value.slice(1);
    }

    if (field === 'id') {
      if (!value.includes('\0')) {
        this.currentId = value;
      }
    } else if (field === 'event') {
      this.currentEvent = value;
    } else if (field === 'data') {
      this.dataLines.push(value);
    }
    return null;
  }

  private dispatchEvent(): SseEventFrame | null {
    const hasData = this.dataLines.length > 0;
    const hasEvent = this.currentEvent !== undefined && this.currentEvent !== '';
    const hasId = this.currentId !== undefined && this.currentId !== '';
    if (!hasData && !hasEvent && !hasId) {
      this.resetFields();
      return null;
    }

    const frame: SseEventFrame = {
      ...(this.currentId !== undefined ? { id: this.currentId } : {}),
      ...(this.currentEvent !== undefined ? { event: this.currentEvent } : {}),
      data: this.dataLines.join('\n'),
    };
    this.resetFields();
    return frame;
  }

  private resetFields(): void {
    this.currentId = undefined;
    this.currentEvent = undefined;
    this.dataLines = [];
  }
}
