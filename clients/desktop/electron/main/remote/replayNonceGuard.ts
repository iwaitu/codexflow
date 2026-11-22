import { isLowerHex } from '@codexflow-shared/remoteProtocol';

export const DEFAULT_NONCE_TTL_MS = 180_000;
export const DEFAULT_MAX_SLOTS_PER_BUCKET = 4096;
export const DEFAULT_MAX_TIMESTAMP_SKEW_SECONDS = 60;

export type NonceCheckFailureCode =
  | 'timestamp_skew_exceeded'
  | 'invalid_nonce'
  | 'replay_nonce_detected';

export type NonceCheckResult =
  | { readonly ok: true }
  | { readonly ok: false; readonly code: NonceCheckFailureCode; readonly message: string };

export class ReplayNonceGuard {
  private readonly ttlMs: number;
  private readonly maxSlotsPerBucket: number;
  private readonly maxSkewSec: number;
  private readonly buckets = new Map<string, Map<string, number>>();

  constructor(options?: {
    ttlMs?: number;
    maxSlotsPerBucket?: number;
    maxSkewSeconds?: number;
  }) {
    this.ttlMs = options?.ttlMs ?? DEFAULT_NONCE_TTL_MS;
    this.maxSlotsPerBucket = options?.maxSlotsPerBucket ?? DEFAULT_MAX_SLOTS_PER_BUCKET;
    this.maxSkewSec = options?.maxSkewSeconds ?? DEFAULT_MAX_TIMESTAMP_SKEW_SECONDS;
  }

  public checkAndConsume(params: {
    mobileId: string;
    keyId: string;
    sessionEpochHex32: string;
    nonceHex32: string;
    unixTimestampSeconds: number;
    nowMs?: number;
  }): NonceCheckResult {
    const nowMs = params.nowMs ?? Date.now();
    const nowSec = Math.floor(nowMs / 1000);

    if (
      !Number.isInteger(params.unixTimestampSeconds) ||
      Math.abs(nowSec - params.unixTimestampSeconds) > this.maxSkewSec
    ) {
      return {
        ok: false,
        code: 'timestamp_skew_exceeded',
        message: 'Request timestamp exceeds allowed clock skew window.',
      };
    }

    if (!isLowerHex(params.nonceHex32, 32)) {
      return {
        ok: false,
        code: 'invalid_nonce',
        message: 'X-CF-Nonce must be 32 lowercase hex characters.',
      };
    }

    const bucketKey = `${params.mobileId}:${params.keyId}:${params.sessionEpochHex32}`;
    let bucket = this.buckets.get(bucketKey);
    if (!bucket) {
      bucket = new Map<string, number>();
      this.buckets.set(bucketKey, bucket);
    }

    const existingExp = bucket.get(params.nonceHex32);
    if (existingExp !== undefined) {
      if (existingExp > nowMs) {
        return {
          ok: false,
          code: 'replay_nonce_detected',
          message: 'Nonce has already been consumed for this epoch.',
        };
      }
      bucket.delete(params.nonceHex32);
    }

    // Evict expired entries first, then oldest entries if still at capacity
    if (bucket.size >= this.maxSlotsPerBucket) {
      for (const [k, exp] of bucket) {
        if (exp <= nowMs) {
          bucket.delete(k);
        }
      }
      while (bucket.size >= this.maxSlotsPerBucket) {
        const oldestKey = bucket.keys().next().value;
        if (oldestKey === undefined) {
          break;
        }
        bucket.delete(oldestKey);
      }
    }

    bucket.set(params.nonceHex32, nowMs + this.ttlMs);
    return { ok: true };
  }

  public pruneExpired(nowMs = Date.now()): void {
    for (const [bucketKey, bucket] of this.buckets) {
      for (const [nonce, exp] of bucket) {
        if (exp <= nowMs) {
          bucket.delete(nonce);
        }
      }
      if (bucket.size === 0) {
        this.buckets.delete(bucketKey);
      }
    }
  }

  public clearAll(): void {
    this.buckets.clear();
  }
}
