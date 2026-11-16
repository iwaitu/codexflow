import type { ParsedSseEvent } from '@shared/contracts/sse.generated';

export interface SseEventMeta {
  streamEpoch: string | null;
}

export interface SseClientOptions {
  baseUrl: string;
  accessToken: string;
  sessionId: string;
  initialLastEventId?: number;
  initialEpoch?: string | null;
  heartbeatTimeoutMs?: number;
  fetchImpl?: typeof fetch;
  onEvent: (event: ParsedSseEvent, meta?: SseEventMeta) => void;
  onReconcileRequired: (reason: 'replay_gap' | 'epoch_changed' | 'reconnected' | 'heartbeat_mismatch', info?: { epoch?: string | null }) => void;
  onConnectionStateChange?: (state: 'connecting' | 'connected' | 'reconnecting' | 'closed') => void;
}

export interface ParsedRawFrame {
  id?: number;
  retry?: number;
  event: string;
  data: string;
}

/**
 * Stateful incremental SSE parser per WHATWG EventSource line-ending rules:
 * `\r\n`, `\n`, and `\r` are each treated as a single EOL. `\r` commits immediately
 * and skips an immediately following `\n` (even across chunk boundaries) so
 * CR-only frames like `data: {}\r\r` dispatch immediately without waiting for EOF.
 */
export class SseStreamParser {
  private lineBuffer = '';
  private skipNextLf = false;
  private event = 'message';
  private id: number | undefined = undefined;
  private retry: number | undefined = undefined;
  private latestRetryMs: number | undefined = undefined;
  private dataLines: string[] = [];
  private hasEventField = false;

  public push(chunk: string): ParsedRawFrame[] {
    const frames: ParsedRawFrame[] = [];
    for (let i = 0; i < chunk.length; i += 1) {
      const ch = chunk[i]!;
      if (this.skipNextLf) {
        this.skipNextLf = false;
        if (ch === '\n') {
          continue;
        }
      }

      if (ch === '\r') {
        this.skipNextLf = true;
        this.commitLine(frames);
      } else if (ch === '\n') {
        this.commitLine(frames);
      } else {
        this.lineBuffer += ch;
      }
    }
    return frames;
  }

  public consumeRetryMs(): number | undefined {
    const value = this.latestRetryMs;
    this.latestRetryMs = undefined;
    return value;
  }

  public flush(): ParsedRawFrame[] {
    const frames: ParsedRawFrame[] = [];
    this.skipNextLf = false;
    if (this.lineBuffer.length > 0) {
      this.commitLine(frames);
    }
    return frames;
  }

  private commitLine(frames: ParsedRawFrame[]): void {
    const line = this.lineBuffer;
    this.lineBuffer = '';

    if (line === '') {
      if (this.retry !== undefined) {
        this.latestRetryMs = this.retry;
      }
      if (this.dataLines.length > 0 || this.hasEventField) {
        frames.push({
          id: this.id,
          retry: this.retry,
          event: this.event,
          data: this.dataLines.join('\n'),
        });
      }
      this.event = 'message';
      this.id = undefined;
      this.retry = undefined;
      this.dataLines = [];
      this.hasEventField = false;
      return;
    }

    if (line.startsWith(':')) {
      return;
    }

    const colonIdx = line.indexOf(':');
    const field = colonIdx === -1 ? line : line.slice(0, colonIdx);
    const rawVal = colonIdx === -1 ? '' : line.slice(colonIdx + 1);
    const val = rawVal.startsWith(' ') ? rawVal.slice(1) : rawVal;

    if (field === 'event') {
      this.event = val || 'message';
      this.hasEventField = true;
    } else if (field === 'id') {
      if (!val.includes('\0')) {
        const parsed = Number.parseInt(val, 10);
        if (/^\d+$/.test(val.trim()) && Number.isFinite(parsed) && parsed >= 0) {
          this.id = parsed;
        }
      }
    } else if (field === 'retry') {
      if (/^\d+$/.test(val.trim())) {
        const parsedRetry = Number.parseInt(val.trim(), 10);
        if (Number.isFinite(parsedRetry) && parsedRetry >= 0) {
          this.retry = parsedRetry;
          this.latestRetryMs = parsedRetry;
        }
      }
    } else if (field === 'data') {
      this.dataLines.push(val);
    }
  }
}

/**
 * Convenience helper for one-shot SSE buffer parsing, returning complete frames and raw incomplete tail.
 */
export function parseSseFrames(buffer: string): { frames: ParsedRawFrame[]; rest: string } {
  // Never normalize a lone trailing `\r` across chunk boundaries before seeing whether `\n` follows.
  const hasTrailingCr = buffer.endsWith('\r');
  const safePrefix = hasTrailingCr ? buffer.slice(0, -1) : buffer;
  const parser = new SseStreamParser();
  const normalized = safePrefix.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
  const sepIdx = normalized.lastIndexOf('\n\n');
  if (sepIdx === -1) {
    return { frames: [], rest: buffer };
  }
  const completeNormalized = normalized.slice(0, sepIdx + 2);
  const restNormalized = normalized.slice(sepIdx + 2) + (hasTrailingCr ? '\r' : '');
  return {
    frames: parser.push(completeNormalized),
    rest: restNormalized,
  };
}

export class SessionSseClient {
  private readonly options: SseClientOptions;
  private readonly fetchImpl: typeof fetch;
  private readonly heartbeatTimeoutMs: number;
  private abortController: AbortController | null = null;
  private stopped = false;
  private lastEventId: number;
  private epoch: string | null;
  private serverRetryMs: number | null = null;
  private seenIds = new Set<number>();
  private seenQueue: number[] = [];
  private attempt = 0;
  private hasConnectedOnce = false;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private heartbeatTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(options: SseClientOptions) {
    this.options = options;
    this.fetchImpl = options.fetchImpl ?? ((input, init) => fetch(input, init));
    this.heartbeatTimeoutMs = options.heartbeatTimeoutMs ?? 35_000;
    this.lastEventId = options.initialLastEventId ?? 0;
    this.epoch = options.initialEpoch ?? null;
  }

  public start(): void {
    this.stopped = false;
    void this.connectLoop();
  }

  public stop(): void {
    this.stopped = true;
    this.clearTimers();
    this.abortController?.abort();
    this.abortController = null;
    this.options.onConnectionStateChange?.('closed');
  }

  public updateCursor(lastEventId: number, epoch?: string | null): void {
    if (epoch !== undefined && epoch !== this.epoch) {
      this.epoch = epoch;
      this.seenIds.clear();
      this.seenQueue = [];
      this.lastEventId = lastEventId;
    } else if (lastEventId > this.lastEventId) {
      this.lastEventId = lastEventId;
    }
    if (lastEventId > 0) {
      this.rememberId(lastEventId);
    }
  }

  private async connectLoop(): Promise<void> {
    while (!this.stopped) {
      this.options.onConnectionStateChange?.(this.hasConnectedOnce ? 'reconnecting' : 'connecting');
      const controller = new AbortController();
      this.abortController = controller;

      try {
        const params = new URLSearchParams({
          sessionId: this.options.sessionId,
          lastEventId: String(this.lastEventId),
        });
        if (this.epoch) {
          params.set('epoch', this.epoch);
        }

        const headers: Record<string, string> = {
          Authorization: `Bearer ${this.options.accessToken}`,
          Accept: 'text/event-stream',
        };
        if (this.lastEventId > 0) {
          headers['Last-Event-ID'] = String(this.lastEventId);
        }

        const response = await this.fetchImpl(`${this.options.baseUrl}/api/gateway/sse?${params.toString()}`, {
          method: 'GET',
          headers,
          signal: controller.signal,
        });

        if (!response.ok || !response.body) {
          throw new Error(`SSE connect failed (${response.status})`);
        }

        const wasReconnect = this.hasConnectedOnce;
        this.hasConnectedOnce = true;
        this.attempt = 0;
        this.options.onConnectionStateChange?.('connected');
        if (wasReconnect) {
          this.options.onReconcileRequired('reconnected', { epoch: this.epoch });
        }

        this.armHeartbeatTimeout(controller);
        await this.readStream(response.body, controller);
      } catch (err) {
        if (this.stopped) {
          return;
        }
        console.warn('[SSE] stream error, will reconnect:', err);
      } finally {
        this.clearHeartbeatTimer();
      }

      if (this.stopped) {
        return;
      }

      this.attempt += 1;
      const baseDelay = this.serverRetryMs ?? 500;
      const delayMs =
        this.serverRetryMs !== null && this.attempt === 1
          ? Math.min(60_000, Math.max(200, baseDelay))
          : Math.min(15_000, 500 * 2 ** Math.min(5, this.attempt - 1));
      this.options.onConnectionStateChange?.('reconnecting');
      await new Promise<void>((resolveDelay) => {
        this.reconnectTimer = setTimeout(() => {
          this.reconnectTimer = null;
          resolveDelay();
        }, delayMs);
      });
    }
  }

  private async readStream(stream: ReadableStream<Uint8Array>, controller: AbortController): Promise<void> {
    const reader = stream.getReader();
    const decoder = new TextDecoder();
    const parser = new SseStreamParser();

    try {
      while (!this.stopped) {
        const { value, done } = await reader.read();
        if (done) {
          const tail = decoder.decode();
          if (tail) {
            for (const frame of parser.push(tail)) {
              this.handleFrame(frame);
            }
          }
          for (const frame of parser.flush()) {
            this.handleFrame(frame);
          }
          const updatedRetry = parser.consumeRetryMs();
          if (updatedRetry !== undefined) {
            this.serverRetryMs = updatedRetry;
          }
          break;
        }
        this.armHeartbeatTimeout(controller);
        const decoded = decoder.decode(value, { stream: true });
        for (const frame of parser.push(decoded)) {
          this.handleFrame(frame);
        }
        const updatedRetry = parser.consumeRetryMs();
        if (updatedRetry !== undefined) {
          this.serverRetryMs = updatedRetry;
        }
      }
    } finally {
      reader.releaseLock();
    }
  }

  private handleFrame(frame: ParsedRawFrame): void {
    if (frame.retry !== undefined) {
      this.serverRetryMs = frame.retry;
    }

    let dataObj: Record<string, unknown> = {};
    if (frame.data) {
      try {
        const parsed = JSON.parse(frame.data) as unknown;
        if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
          dataObj = parsed as Record<string, unknown>;
        } else {
          dataObj = { value: parsed };
        }
      } catch {
        dataObj = { raw: frame.data };
      }
    }

    const incomingEpoch = typeof dataObj.epoch === 'string' ? dataObj.epoch : null;
    if (incomingEpoch && this.epoch && incomingEpoch !== this.epoch) {
      this.epoch = incomingEpoch;
      this.seenIds.clear();
      this.seenQueue = [];
      this.lastEventId = frame.id ?? 0;
      this.options.onReconcileRequired('epoch_changed', { epoch: incomingEpoch });
    } else if (incomingEpoch && !this.epoch) {
      this.epoch = incomingEpoch;
    }

    if (frame.event === 'replay_gap') {
      if (frame.id !== undefined) {
        this.rememberId(frame.id);
      }
      this.options.onEvent(
        {
          id: frame.id,
          event: 'replay_gap',
          data: dataObj,
        } as ParsedSseEvent,
        { streamEpoch: this.epoch },
      );
      this.options.onReconcileRequired('replay_gap', { epoch: this.epoch });
      return;
    }

    if (frame.id !== undefined && frame.id > 0) {
      if (this.seenIds.has(frame.id)) {
        return;
      }
      this.rememberId(frame.id);
    }

    this.options.onEvent(
      {
        id: frame.id,
        event: frame.event,
        data: dataObj,
      } as ParsedSseEvent,
      { streamEpoch: this.epoch },
    );
  }

  private rememberId(id: number): void {
    if (id > this.lastEventId) {
      this.lastEventId = id;
    }
    if (this.seenIds.has(id)) {
      return;
    }
    this.seenIds.add(id);
    this.seenQueue.push(id);
    if (this.seenQueue.length > 8192) {
      const oldest = this.seenQueue.shift();
      if (oldest !== undefined) {
        this.seenIds.delete(oldest);
      }
    }
  }

  private armHeartbeatTimeout(controller: AbortController): void {
    this.clearHeartbeatTimer();
    this.heartbeatTimer = setTimeout(() => {
      controller.abort();
    }, this.heartbeatTimeoutMs);
  }

  private clearHeartbeatTimer(): void {
    if (this.heartbeatTimer) {
      clearTimeout(this.heartbeatTimer);
      this.heartbeatTimer = null;
    }
  }

  private clearTimers(): void {
    this.clearHeartbeatTimer();
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }
}
