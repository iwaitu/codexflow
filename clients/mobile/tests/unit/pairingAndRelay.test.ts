import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  buildEpochCertV1,
  buildPairAckV1,
  buildPairingQrUri,
  buildSasV1Input,
  bytesToBase64,
  bytesToBase64Url,
  canonicalizeQuery,
  sas6FromSha256Digest,
  utf8Encode,
} from '@codexflow-shared/remoteProtocol';
import { setTestCryptoPluginOverride } from '@/bridge/nativeCryptoBridge';
import type { PersistedSecurePairingState } from '@/bridge/nativeCryptoTypes';
import { WebCryptoFallbackPlugin } from '@/bridge/webCryptoFallback';
import { sha256Bytes, sha256HexLower } from '@/crypto/incrementalSha256';
import { normalizeP1363LowS } from '@/crypto/p256Verify';
import { RemoteApiException } from '@/services/errorClassifier';
import { runQrPairingFlow } from '@/services/pairingService';
import { syncPendingSummary } from '@/services/pendingSyncService';
import { RelayClient } from '@/services/relayClient';
import { ScreenshotCache } from '@/services/screenshotCache';
import { MAX_MOBILE_UPLOAD_BYTES, uploadSessionAttachment } from '@/services/uploadService';

async function createDesktopSigner() {
  const keyPair = await globalThis.crypto.subtle.generateKey(
    { name: 'ECDSA', namedCurve: 'P-256' },
    true,
    ['sign', 'verify'],
  );
  const spkiBytes = new Uint8Array(await globalThis.crypto.subtle.exportKey('spki', keyPair.publicKey));
  const spkiB64 = bytesToBase64(spkiBytes);
  const spkiSha256 = sha256HexLower(spkiBytes);

  const signLowS = async (payload: string): Promise<string> => {
    const encoded = utf8Encode(payload);
    const buf = new Uint8Array(encoded.byteLength);
    buf.set(encoded);
    const sig = await globalThis.crypto.subtle.sign(
      { name: 'ECDSA', hash: 'SHA-256' },
      keyPair.privateKey,
      buf.buffer,
    );
    return bytesToBase64Url(normalizeP1363LowS(new Uint8Array(sig)));
  };

  return { spkiB64, spkiSha256, signLowS };
}

function makeBaseState(desktopSpkiB64: string, desktopSpkiSha256: string): PersistedSecurePairingState {
  return {
    portalUrl: 'https://portal.codexflow.example',
    deviceId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
    desktopName: 'Workstation-01',
    bindingVersion: 3,
    desktopSpkiB64,
    desktopSpkiSha256,
    desktopKeyId: desktopSpkiSha256.slice(0, 32),
    pairingId: '77777777-6666-5555-4444-333333333333',
    mobileId: '99999999-8888-7777-6666-555555555555',
    accessToken: 'acc-1',
    accessExpiresAt: '2030-01-01T00:00:00Z',
    refreshToken: 'ref-1',
    refreshExpiresAt: '2030-02-01T00:00:00Z',
    biometricEnrolled: true,
    apnsPushEnabled: false,
  };
}

describe('配对流程、RelayClient 与上传/待办/截图缓存', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
  });

  afterEach(() => {
    setTestCryptoPluginOverride(null);
    vi.restoreAllMocks();
  });

  it('4步扫码配对完成校验并安全落库且不污染 Web Storage，同时拒绝 SPKI 与 SAS 篡改', async () => {
    const plugin = new WebCryptoFallbackPlugin();
    setTestCryptoPluginOverride(plugin);
    const desktop = await createDesktopSigner();
    const otherDesktop = await createDesktopSigner();

    const secretBytes = new Uint8Array(32).fill(0xab);
    const rawQrUri = buildPairingQrUri({
      ticketId: '11111111-2222-3333-4444-555555555555',
      qrBootstrapToken: 'bootstrap-token-xyz',
      deviceId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
      bindingVersion: 3,
      desktopSpkiSha256: desktop.spkiSha256,
      pairingSecretB64U: bytesToBase64Url(secretBytes),
    });

    const pairingId = '77777777-6666-5555-4444-333333333333';
    const ackAt = Math.floor(Date.now() / 1000);
    let capturedPairRecordSha256 = '';
    let capturedSas = '';

    const mockFetch: typeof fetch = vi.fn(async (input, init) => {
      const url = String(input);
      if (url.endsWith('/api/auth/mobile/qr-activate')) {
        const body = JSON.parse(String(init?.body)) as { pairRecordV1Text: string };
        capturedPairRecordSha256 = sha256HexLower(body.pairRecordV1Text);
        capturedSas = sas6FromSha256Digest(sha256Bytes(buildSasV1Input(body.pairRecordV1Text)));
        return new Response(
          JSON.stringify({
            pairingId,
            pairingPollToken: 'poll-tok-1',
            expiresAt: new Date(Date.now() + 60_000).toISOString(),
            desktopSpkiB64: desktop.spkiB64,
            desktopKeyId: desktop.spkiSha256.slice(0, 32),
            sasDigits: capturedSas,
          }),
          { status: 200 },
        );
      }
      if (url.endsWith(`/api/auth/mobile/pairings/${pairingId}/status`)) {
        const ackText = buildPairAckV1(pairingId, capturedPairRecordSha256, ackAt);
        const ackSig = await desktop.signLowS(ackText);
        return new Response(
          JSON.stringify({
            pairingId,
            status: 'active',
            expiresAt: new Date(Date.now() + 60_000).toISOString(),
            pairAckV1Text: ackText,
            pairAckSigB64U: ackSig,
          }),
          { status: 200 },
        );
      }
      if (url.endsWith(`/api/auth/mobile/pairings/${pairingId}/exchange`)) {
        return new Response(
          JSON.stringify({
            accessToken: 'acc-final',
            accessExpiresAt: '2030-01-01T00:00:00Z',
            refreshToken: 'ref-final',
            refreshExpiresAt: '2030-02-01T00:00:00Z',
            pairingId,
            deviceId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
            mobileId: 'unused',
            bindingVersion: 3,
          }),
          { status: 200 },
        );
      }
      throw new Error(`Unexpected fetch: ${url}`);
    }) as typeof fetch;

    const saved = await runQrPairingFlow({
      rawQrUri,
      portalUrl: 'https://portal.codexflow.example',
      mobileDeviceName: 'iPhone 测试机',
      fetchFn: mockFetch,
      pollIntervalMs: 1,
    });

    expect(saved.accessToken).toBe('acc-final');
    expect(saved.desktopSpkiSha256).toBe(desktop.spkiSha256);
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);

    // 负向 1：篡改返回的 desktopSpkiB64
    await expect(
      runQrPairingFlow({
        rawQrUri,
        portalUrl: 'https://portal.codexflow.example',
        mobileDeviceName: 'iPhone',
        fetchFn: vi.fn(async () =>
          new Response(
            JSON.stringify({
              pairingId,
              pairingPollToken: 'poll',
              expiresAt: new Date(Date.now() + 60_000).toISOString(),
              desktopSpkiB64: otherDesktop.spkiB64,
              desktopKeyId: otherDesktop.spkiSha256.slice(0, 32),
              sasDigits: '123456',
            }),
            { status: 200 },
          ),
        ) as typeof fetch,
      }),
    ).rejects.toMatchObject({
      appError: { code: 'desktop_spki_fingerprint_mismatch' },
    });

    // 负向 2：篡改服务端 sasDigits
    await expect(
      runQrPairingFlow({
        rawQrUri,
        portalUrl: 'https://portal.codexflow.example',
        mobileDeviceName: 'iPhone',
        fetchFn: vi.fn(async () =>
          new Response(
            JSON.stringify({
              pairingId,
              pairingPollToken: 'poll',
              expiresAt: new Date(Date.now() + 60_000).toISOString(),
              desktopSpkiB64: desktop.spkiB64,
              desktopKeyId: desktop.spkiSha256.slice(0, 32),
              sasDigits: '000000',
            }),
            { status: 200 },
          ),
        ) as typeof fetch,
      }),
    ).rejects.toMatchObject({
      appError: { code: 'sas_mismatch' },
    });
  });

  it('RelayClient 并发 401 单飞刷新、Date 时钟校准、Epoch 刷新、特殊字符 Query 与终态吊销清空', async () => {
    const plugin = new WebCryptoFallbackPlugin();
    setTestCryptoPluginOverride(plugin);
    await plugin.ensureDeviceKeys({ requireBiometricKey: true });

    const desktop = await createDesktopSigner();
    const initialState = makeBaseState(desktop.spkiB64, desktop.spkiSha256);

    const nowSec = Math.floor(Date.now() / 1000);
    const makeEpochCert = async (epochHex: string, issuedAt = nowSec - 10) => {
      const expiresAt = issuedAt + 3600;
      const text = buildEpochCertV1(
        initialState.deviceId,
        initialState.bindingVersion,
        epochHex,
        issuedAt,
        expiresAt,
      );
      return {
        deviceId: initialState.deviceId,
        bindingVersion: initialState.bindingVersion,
        sessionEpochHex32: epochHex,
        issuedAtUnix: issuedAt,
        expiresAtUnix: expiresAt,
        signatureB64U: await desktop.signLowS(text),
      };
    };

    let refreshCalls = 0;
    let epochFetchCount = 0;
    let specialQueryObservedUrl = '';

    const fetchMock: typeof fetch = vi.fn(async (input, init) => {
      const url = String(input);
      if (url.endsWith('/meta/session-epoch')) {
        epochFetchCount += 1;
        const hex =
          epochFetchCount === 1
            ? '11111111111111111111111111111111'
            : '22222222222222222222222222222222';
        return new Response(JSON.stringify(await makeEpochCert(hex)), { status: 200 });
      }
      if (url.endsWith('/api/auth/token/refresh')) {
        refreshCalls += 1;
        await new Promise((r) => setTimeout(r, 20));
        return new Response(
          JSON.stringify({
            accessToken: 'acc-2',
            accessExpiresAt: '2030-01-01T00:00:00Z',
            refreshToken: 'ref-2',
            refreshExpiresAt: '2030-02-01T00:00:00Z',
            pairingId: initialState.pairingId,
            deviceId: initialState.deviceId,
            mobileId: initialState.mobileId,
            bindingVersion: 3,
          }),
          { status: 200 },
        );
      }
      if (url.includes('/api/gateway/sessions')) {
        const auth = (init?.headers as Record<string, string>)['Authorization'];
        if (auth === 'Bearer acc-1') {
          return new Response(JSON.stringify({ code: 'unauthorized', message: 'expired' }), {
            status: 401,
          });
        }
        return new Response(JSON.stringify({ items: [], total: 0, page: 1, pageSize: 20 }), {
          status: 200,
        });
      }
      if (url.includes('/api/gateway/special')) {
        specialQueryObservedUrl = url;
        return new Response(JSON.stringify({ ok: true }), { status: 200 });
      }
      if (url.includes('/api/gateway/skew')) {
        const tsHeader = Number((init?.headers as Record<string, string>)['X-CF-Timestamp']);
        if (Math.abs(tsHeader - (nowSec + 300)) > 60) {
          return new Response(JSON.stringify({ code: 'timestamp_skew_exceeded' }), {
            status: 401,
            headers: { Date: new Date((nowSec + 300) * 1000).toUTCString() },
          });
        }
        return new Response(JSON.stringify({ adjusted: true }), { status: 200 });
      }
      if (url.includes('/api/gateway/epoch-stale')) {
        const epochHeader = (init?.headers as Record<string, string>)['X-CF-Session-Epoch'];
        if (epochHeader === '11111111111111111111111111111111') {
          return new Response(JSON.stringify({ code: 'invalid_session_epoch' }), { status: 401 });
        }
        return new Response(JSON.stringify({ refreshedEpoch: epochHeader }), { status: 200 });
      }
      if (url.includes('/api/gateway/revoked-check')) {
        return new Response(JSON.stringify({ code: 'pairing_revoked' }), { status: 401 });
      }
      throw new Error(`Unhandled url: ${url}`);
    }) as typeof fetch;

    const revokedSpy = vi.fn();
    const client = new RelayClient({
      initialState,
      fetchFn: fetchMock,
      nowMs: () => nowSec * 1000,
      onSessionRevoked: revokedSpy,
    });

    // 1. 并发 401 仅触发 1 次 refresh
    const [r1, r2] = await Promise.all([
      client.requestJson<{ total: number }>({ method: 'GET', path: '/api/gateway/sessions' }),
      client.requestJson<{ total: number }>({ method: 'GET', path: '/api/gateway/sessions' }),
    ]);
    expect(r1.total).toBe(0);
    expect(r2.total).toBe(0);
    expect(refreshCalls).toBe(1);

    // 2. 特殊字符 Query URI 编码与 canonicalizeQuery
    const queryPairs: Array<[string, string]> = [
      ['q', 'a+b & c=1 中文'],
      ['page', '2'],
    ];
    await client.requestJson({ method: 'GET', path: '/api/gateway/special', queryPairs });
    expect(specialQueryObservedUrl).toContain('q=a%2Bb%20%26%20c%3D1%20%E4%B8%AD%E6%96%87&page=2');
    expect(canonicalizeQuery(queryPairs)).toBe('page=2&q=a+b & c=1 中文');

    // 3. Epoch 失效重拉再成功
    const epochRes = await client.requestJson<{ refreshedEpoch: string }>({
      method: 'GET',
      path: '/api/gateway/epoch-stale',
    });
    expect(epochRes.refreshedEpoch).toBe('22222222222222222222222222222222');

    // 4. Date 时钟偏差 300s 自动修正并重试
    const skewRes = await client.requestJson<{ adjusted: boolean }>({
      method: 'GET',
      path: '/api/gateway/skew',
    });
    expect(skewRes.adjusted).toBe(true);

    // 5. 终态吊销清空本地凭证
    await expect(
      client.requestJson({ method: 'GET', path: '/api/gateway/revoked-check' }),
    ).rejects.toBeInstanceOf(RemoteApiException);
    expect(client.getState()).toBeNull();
    expect(revokedSpy).toHaveBeenCalledTimes(1);
    const persisted = await plugin.loadSecureState();
    expect(persisted.stateJson).toBeNull();
  });

  it('附件上传：20 MiB 客户端上限阻断、断网后 GET uploads 幂等恢复与 409 冲突终止', async () => {
    const plugin = new WebCryptoFallbackPlugin();
    setTestCryptoPluginOverride(plugin);
    await plugin.ensureDeviceKeys({ requireBiometricKey: false });
    const desktop = await createDesktopSigner();
    const state = makeBaseState(desktop.spkiB64, desktop.spkiSha256);

    const epochText = buildEpochCertV1(
      state.deviceId,
      state.bindingVersion,
      'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
      Math.floor(Date.now() / 1000) - 5,
      Math.floor(Date.now() / 1000) + 3600,
    );
    const epochCert = {
      deviceId: state.deviceId,
      bindingVersion: state.bindingVersion,
      sessionEpochHex32: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
      issuedAtUnix: Math.floor(Date.now() / 1000) - 5,
      expiresAtUnix: Math.floor(Date.now() / 1000) + 3600,
      signatureB64U: await desktop.signLowS(epochText),
    };

    // 超过 20 MiB
    const oversizedFile = new File([new Uint8Array(10)], 'huge.bin');
    Object.defineProperty(oversizedFile, 'size', { value: MAX_MOBILE_UPLOAD_BYTES + 1 });
    const clientNoNet = new RelayClient({ initialState: state });
    await expect(
      uploadSessionAttachment({ relayClient: clientNoNet, sessionId: 's1', file: oversizedFile }),
    ).rejects.toMatchObject({ appError: { httpStatus: 413 } });

    // POST 503 后查询 status 命中 completed 恢复
    let statusMode: 'completed' | 'conflict' = 'completed';
    const fetchRecovery: typeof fetch = vi.fn(async (input, init) => {
      const url = String(input);
      if (url.endsWith('/meta/session-epoch')) {
        return new Response(JSON.stringify(epochCert), { status: 200 });
      }
      if (url.endsWith('/attachments') && init?.method === 'POST') {
        return new Response(JSON.stringify({ code: 'upstream_timeout' }), { status: 503 });
      }
      if (url.includes('/attachments/uploads/')) {
        if (statusMode === 'conflict') {
          return new Response(JSON.stringify({ code: 'upload_digest_conflict' }), { status: 409 });
        }
        return new Response(
          JSON.stringify({
            status: 'completed',
            uploadId: 'u-1',
            attachment: {
              name: 'note.txt',
              relativePath: '.codexflow-attachments/note.txt',
              sizeBytes: 5,
              contentType: 'text/plain',
            },
          }),
          { status: 200 },
        );
      }
      throw new Error(`Unexpected: ${url}`);
    }) as typeof fetch;

    const clientRecovery = new RelayClient({ initialState: state, fetchFn: fetchRecovery });
    const smallFile = new File(['hello'], 'note.txt', { type: 'text/plain' });
    const res = await uploadSessionAttachment({
      relayClient: clientRecovery,
      sessionId: 's1',
      file: smallFile,
      retryDelayMs: 1,
    });
    expect(res.attachment.relativePath).toBe('.codexflow-attachments/note.txt');

    // 409 立即抛出
    statusMode = 'conflict';
    await expect(
      uploadSessionAttachment({
        relayClient: clientRecovery,
        sessionId: 's1',
        file: smallFile,
        retryDelayMs: 1,
      }),
    ).rejects.toMatchObject({ appError: { httpStatus: 409 } });
  });

  it('待办投影 snapshot 多页合并、delta 终态清理与 resetRequired 重建；截图 Cache 四元组隔离与 revokeObjectURL', async () => {
    const plugin = new WebCryptoFallbackPlugin();
    setTestCryptoPluginOverride(plugin);
    await plugin.ensureDeviceKeys({ requireBiometricKey: false });
    const desktop = await createDesktopSigner();
    const state = makeBaseState(desktop.spkiB64, desktop.spkiSha256);

    const epochText = buildEpochCertV1(
      state.deviceId,
      state.bindingVersion,
      'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
      Math.floor(Date.now() / 1000) - 5,
      Math.floor(Date.now() / 1000) + 3600,
    );
    const epochCert = {
      deviceId: state.deviceId,
      bindingVersion: state.bindingVersion,
      sessionEpochHex32: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
      issuedAtUnix: Math.floor(Date.now() / 1000) - 5,
      expiresAtUnix: Math.floor(Date.now() / 1000) + 3600,
      signatureB64U: await desktop.signLowS(epochText),
    };

    const responses: Array<Record<string, unknown>> = [
      {
        mode: 'snapshot',
        snapshotId: 'snap-1',
        summaryEpoch: 1,
        resumeAfterEventId: 10,
        nextPageToken: 'p2',
        snapshotComplete: false,
        resetRequired: false,
        bindingVersion: 3,
        items: [
          {
            eventId: 5,
            sourceKind: 'command',
            opaqueRefId: 'ref-1',
            sessionId: 's1',
            title: 'A',
            summary: 'run a',
            status: 'pending',
            isTerminal: false,
            readOnlyInRemote: false,
            createdAtUtc: '2026-01-01T00:00:00Z',
            expiresAtUtc: '2030-01-01T00:00:00Z',
          },
        ],
      },
      {
        mode: 'snapshot',
        snapshotId: 'snap-1',
        summaryEpoch: 1,
        resumeAfterEventId: 10,
        nextPageToken: null,
        snapshotComplete: true,
        resetRequired: false,
        bindingVersion: 3,
        items: [
          {
            eventId: 8,
            sourceKind: 'browser',
            opaqueRefId: 'ref-2',
            sessionId: 's1',
            title: 'B',
            summary: 'click b',
            status: 'pending',
            isTerminal: false,
            readOnlyInRemote: false,
            createdAtUtc: '2026-01-01T00:00:00Z',
            expiresAtUtc: '2030-01-01T00:00:00Z',
          },
        ],
      },
      // delta 标记 ref-1 终态并新增 ref-3
      {
        mode: 'delta',
        summaryEpoch: 1,
        resumeAfterEventId: 12,
        nextPageToken: null,
        snapshotComplete: true,
        resetRequired: false,
        bindingVersion: 3,
        items: [
          {
            eventId: 11,
            sourceKind: 'command',
            opaqueRefId: 'ref-1',
            sessionId: 's1',
            title: 'A',
            summary: 'run a',
            status: 'approved',
            isTerminal: true,
            readOnlyInRemote: false,
            createdAtUtc: '2026-01-01T00:00:00Z',
            expiresAtUtc: '2030-01-01T00:00:00Z',
          },
          {
            eventId: 12,
            sourceKind: 'computer',
            opaqueRefId: 'ref-3',
            sessionId: 's1',
            title: 'C',
            summary: 'type c',
            status: 'pending',
            isTerminal: false,
            readOnlyInRemote: false,
            createdAtUtc: '2026-01-01T00:00:00Z',
            expiresAtUtc: '2030-01-01T00:00:00Z',
          },
        ],
      },
      // delta 收到 resetRequired: true，触发重拉新快照 epoch=2
      {
        mode: 'delta',
        summaryEpoch: 2,
        resumeAfterEventId: 20,
        snapshotComplete: true,
        resetRequired: true,
        bindingVersion: 3,
        items: [],
      },
      {
        mode: 'snapshot',
        snapshotId: 'snap-2',
        summaryEpoch: 2,
        resumeAfterEventId: 20,
        nextPageToken: null,
        snapshotComplete: true,
        resetRequired: false,
        bindingVersion: 3,
        items: [
          {
            eventId: 20,
            sourceKind: 'command',
            opaqueRefId: 'ref-new',
            sessionId: 's2',
            title: 'Fresh',
            summary: 'new',
            status: 'pending',
            isTerminal: false,
            readOnlyInRemote: false,
            createdAtUtc: '2026-01-01T00:00:00Z',
            expiresAtUtc: '2030-01-01T00:00:00Z',
          },
        ],
      },
    ];

    let shotCounter = 0;
    const client = new RelayClient({
      initialState: state,
      fetchFn: vi.fn(async (input) => {
        const url = String(input);
        if (url.endsWith('/meta/session-epoch')) {
          return new Response(JSON.stringify(epochCert), { status: 200 });
        }
        if (url.includes('/events/summary')) {
          const next = responses.shift();
          return new Response(JSON.stringify(next), { status: 200 });
        }
        if (url.includes('/browser/screenshot')) {
          shotCounter += 1;
          if (shotCounter === 2) {
            return new Response(null, { status: 304 });
          }
          return new Response(new Uint8Array([1, 2, 3]), {
            status: 200,
            headers: { ETag: '"shot-etag-1"', 'X-Screenshot-Seq': '42' },
          });
        }
        throw new Error(`Unexpected: ${url}`);
      }) as typeof fetch,
    });

    const snap1 = await syncPendingSummary({ relayClient: client, currentItems: [], cursor: null, limit: 10 });
    expect(snap1.mode).toBe('snapshot');
    expect(snap1.items.map((i) => i.opaqueRefId)).toEqual(['ref-1', 'ref-2']);

    const delta1 = await syncPendingSummary({
      relayClient: client,
      currentItems: snap1.items,
      cursor: snap1.cursor,
      limit: 10,
    });
    expect(delta1.mode).toBe('delta');
    expect(delta1.items.map((i) => i.opaqueRefId)).toEqual(['ref-2', 'ref-3']);

    const resetRes = await syncPendingSummary({
      relayClient: client,
      currentItems: delta1.items,
      cursor: delta1.cursor,
      limit: 10,
    });
    expect(resetRes.mode).toBe('snapshot');
    expect(resetRes.cursor.summaryEpoch).toBe(2);
    expect(resetRes.items.map((i) => i.opaqueRefId)).toEqual(['ref-new']);

    // ScreenshotCache 304 复用与 revokeObjectURL
    let urlSeq = 0;
    const createSpy = vi.fn(() => `blob:mock-${++urlSeq}`);
    const revokeSpy = vi.fn();
    vi.stubGlobal('URL', { createObjectURL: createSpy, revokeObjectURL: revokeSpy });

    const cache = new ScreenshotCache(2);
    const first = await cache.fetchLatestScreenshot({ relayClient: client, sessionId: 's1' });
    const cached304 = await cache.fetchLatestScreenshot({ relayClient: client, sessionId: 's1' });
    expect(first?.objectUrl).toBe('blob:mock-1');
    expect(cached304?.objectUrl).toBe('blob:mock-1');

    // 换绑定代次或清缓存应互不串图并正确释放
    client.setState({ ...state, bindingVersion: 4 });
    expect(cache.getCachedForSession(state.deviceId, 4, 's1')).toBeNull();
    cache.clearAll();
    expect(revokeSpy).toHaveBeenCalledWith('blob:mock-1');
  });
});
