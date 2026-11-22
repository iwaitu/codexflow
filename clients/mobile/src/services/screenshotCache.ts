import type { RelayClient } from './relayClient';

interface CachedScreenshotEntry {
  readonly key: string;
  readonly sessionId: string;
  readonly etag: string;
  readonly objectUrl: string;
  readonly screenshotSeq: string | null;
}

export class ScreenshotCache {
  private readonly maxEntries: number;
  private readonly entries = new Map<string, CachedScreenshotEntry>();
  private readonly latestEtagBySession = new Map<string, string>();

  public constructor(maxEntries = 12) {
    this.maxEntries = maxEntries;
  }

  private static makeKey(
    deviceId: string,
    bindingVersion: number,
    sessionId: string,
    etag: string,
  ): string {
    return `${deviceId}\n${bindingVersion}\n${sessionId}\n${etag}`;
  }

  public getCachedForSession(
    deviceId: string,
    bindingVersion: number,
    sessionId: string,
  ): CachedScreenshotEntry | null {
    const etag = this.latestEtagBySession.get(`${deviceId}\n${bindingVersion}\n${sessionId}`);
    if (!etag) {
      return null;
    }
    const key = ScreenshotCache.makeKey(deviceId, bindingVersion, sessionId, etag);
    const hit = this.entries.get(key);
    if (!hit) {
      return null;
    }
    // LRU touch
    this.entries.delete(key);
    this.entries.set(key, hit);
    return hit;
  }

  public async fetchLatestScreenshot(params: {
    relayClient: RelayClient;
    sessionId: string;
    signal?: AbortSignal;
  }): Promise<{ objectUrl: string; etag: string | null; screenshotSeq: string | null } | null> {
    const state = params.relayClient.getState();
    if (!state) {
      return null;
    }

    const existing = this.getCachedForSession(
      state.deviceId,
      state.bindingVersion,
      params.sessionId,
    );

    const res = await params.relayClient.sendRaw({
      method: 'GET',
      path: `/api/gateway/session/${encodeURIComponent(params.sessionId)}/browser/screenshot`,
      keyPurpose: 'device_session',
      ifNoneMatch: existing?.etag,
      signal: params.signal,
    });

    if (res.status === 204) {
      return null;
    }
    if (res.status === 304 && existing) {
      return {
        objectUrl: existing.objectUrl,
        etag: existing.etag,
        screenshotSeq: existing.screenshotSeq,
      };
    }
    if (!res.ok) {
      return null;
    }

    const blob = await res.response.blob();
    if (blob.size === 0) {
      return null;
    }

    const etag = res.headers.get('etag') ?? `seq_${res.headers.get('x-screenshot-seq') ?? Date.now()}`;
    const screenshotSeq = res.headers.get('x-screenshot-seq');
    const objectUrl = URL.createObjectURL(blob);
    const key = ScreenshotCache.makeKey(
      state.deviceId,
      state.bindingVersion,
      params.sessionId,
      etag,
    );

    const old = this.entries.get(key);
    if (old) {
      URL.revokeObjectURL(old.objectUrl);
      this.entries.delete(key);
    }

    while (this.entries.size >= this.maxEntries) {
      const oldestKey = this.entries.keys().next().value;
      if (!oldestKey) {
        break;
      }
      const evicted = this.entries.get(oldestKey);
      this.entries.delete(oldestKey);
      if (evicted) {
        URL.revokeObjectURL(evicted.objectUrl);
      }
    }

    const entry: CachedScreenshotEntry = {
      key,
      sessionId: params.sessionId,
      etag,
      objectUrl,
      screenshotSeq,
    };
    this.entries.set(key, entry);
    this.latestEtagBySession.set(`${state.deviceId}\n${state.bindingVersion}\n${params.sessionId}`, etag);
    return {
      objectUrl,
      etag,
      screenshotSeq,
    };
  }

  public clearAll(): void {
    for (const entry of this.entries.values()) {
      URL.revokeObjectURL(entry.objectUrl);
    }
    this.entries.clear();
    this.latestEtagBySession.clear();
  }
}
