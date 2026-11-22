import { SseStreamParser, type SseEventFrame } from '@codexflow-shared/sseStreamParser';
import { RemoteApiException } from './errorClassifier';
import type { RelayClient } from './relayClient';

export interface SseSubscriptionHandle {
  close(): void;
}

export function subscribeSessionSse(params: {
  relayClient: RelayClient;
  sessionId: string;
  initialLastEventId?: number;
  initialEpoch?: string | null;
  onEvent: (frame: SseEventFrame) => void;
  onStatusChange?: (status: 'connecting' | 'connected' | 'reconnecting' | 'stopped') => void;
  onError?: (err: unknown) => void;
  reconnectBaseMs?: number;
}): SseSubscriptionHandle {
  const controller = new AbortController();
  let lastEventId = params.initialLastEventId ?? 0;
  let epoch = params.initialEpoch ?? null;
  const baseMs = params.reconnectBaseMs ?? 1200;

  const pump = async () => {
    let attempt = 0;
    while (!controller.signal.aborted) {
      params.onStatusChange?.(attempt === 0 ? 'connecting' : 'reconnecting');
      const parser = new SseStreamParser();
      const queryPairs: Array<[string, string]> = [['sessionId', params.sessionId]];
      if (lastEventId > 0) {
        queryPairs.push(['lastEventId', String(lastEventId)]);
        if (epoch) {
          queryPairs.push(['epoch', epoch]);
        }
      }

      try {
        const resp = await params.relayClient.openSignedStream({
          method: 'GET',
          path: '/api/gateway/sse',
          queryPairs,
          keyPurpose: 'device_session',
          signal: controller.signal,
        });

        epoch = params.relayClient.getCurrentEpochHex() ?? epoch;
        attempt = 0;
        params.onStatusChange?.('connected');

        if (!resp.body) {
          throw new Error('SSE 响应不包含可读流。');
        }
        const reader = resp.body.getReader();
        const decoder = new TextDecoder('utf-8');
        try {
          while (!controller.signal.aborted) {
            const { value, done } = await reader.read();
            if (done) {
              break;
            }
            const text = decoder.decode(value, { stream: true });
            for (const frame of parser.push(text)) {
              if (frame.id && /^\d+$/.test(frame.id)) {
                const parsedId = Number.parseInt(frame.id, 10);
                if (parsedId > lastEventId) {
                  lastEventId = parsedId;
                }
              }
              params.onEvent(frame);
            }
          }
          const tail = decoder.decode();
          if (tail) {
            for (const frame of parser.push(tail)) {
              if (frame.id && /^\d+$/.test(frame.id)) {
                const parsedId = Number.parseInt(frame.id, 10);
                if (parsedId > lastEventId) {
                  lastEventId = parsedId;
                }
              }
              params.onEvent(frame);
            }
          }
          parser.flush();
        } finally {
          reader.releaseLock();
        }
      } catch (err) {
        if (controller.signal.aborted || (err instanceof DOMException && err.name === 'AbortError')) {
          break;
        }
        params.onError?.(err);
        if (err instanceof RemoteApiException && !err.appError.recoverable) {
          break;
        }
      }

      if (controller.signal.aborted) {
        break;
      }
      const waitMs = Math.min(15_000, baseMs * Math.pow(2, Math.min(attempt, 4)));
      attempt += 1;
      await new Promise<void>((resolve) => {
        const timer = setTimeout(resolve, waitMs);
        controller.signal.addEventListener(
          'abort',
          () => {
            clearTimeout(timer);
            resolve();
          },
          { once: true },
        );
      });
    }

    params.onStatusChange?.('stopped');
  };

  void pump();

  return {
    close() {
      controller.abort();
    },
  };
}
