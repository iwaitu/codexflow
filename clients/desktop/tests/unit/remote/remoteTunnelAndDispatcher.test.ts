import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import {
  EMPTY_BODY_SHA256_HEX,
  TUNNEL_MAX_PAYLOAD_BYTES,
  TunnelFrameFlags,
  TunnelFrameType,
  buildApprovalEnvV1,
  buildBioPopV1,
  buildPairV1,
  buildRequestSigV1,
  deserializeJsonPayload,
  encodeFrame,
  encodeJsonFrame,
  encodeWindowUpdate,
  tryDecodeFrame,
  tryDecodeWindowUpdate,
  type HostEventPayload,
  type HostStatePayload,
  type PairRequestPayload,
  type ReqStartPayload,
  type ResHeadPayload,
  type RevokeNoticePayload,
  type TunnelFrame,
} from '@codexflow-shared/remoteProtocol';
import { RemoteConfigStore, type SecretProtector } from '../../../electron/main/remote/remoteConfigStore';
import {
  generateP256KeyPair,
  randomHex32,
  sha256HexLower,
  signP256LowS,
  verifyP256LowS,
} from '../../../electron/main/remote/remoteCrypto';
import type { WsTransportSession } from '../../../electron/main/remote/remoteDialController';
import { RemoteHostDispatcher } from '../../../electron/main/remote/remoteHostDispatcher';
import { RemoteTunnelManager } from '../../../electron/main/remote/remoteTunnelManager';
import { RemoteUploadStaging } from '../../../electron/main/remote/remoteUploadStaging';
import { ReplayNonceGuard } from '../../../electron/main/remote/replayNonceGuard';

function createMemoryProtector(): SecretProtector {
  return {
    isEncryptionAvailable: () => true,
    encryptString: (s) => `enc:${Buffer.from(s, 'utf8').toString('base64')}`,
    decryptString: (c) => Buffer.from(c.slice(4), 'base64').toString('utf8'),
  };
}

async function readStreamText(stream: AsyncIterable<Uint8Array>): Promise<string> {
  const parts: Buffer[] = [];
  for await (const chunk of stream) {
    parts.push(Buffer.from(chunk));
  }
  return Buffer.concat(parts).toString('utf8');
}

class MockWsTransport implements WsTransportSession {
  public readonly sentRaw: Uint8Array[] = [];
  private binaryCb: ((data: Uint8Array) => void) | null = null;
  private pingCb: ((data: Uint8Array) => void) | null = null;
  private pongCb: ((data: Uint8Array) => void) | null = null;
  private closeCb: ((code: number, reason: string) => void) | null = null;
  private errorCb: ((err: Error) => void) | null = null;

  public sendBinary(payload: Uint8Array): void {
    this.sentRaw.push(payload.slice());
  }

  public sendPing(): void {}
  public sendPong(): void {}

  public close(code = 1000, reason = ''): void {
    this.closeCb?.(code, reason);
  }

  public onBinaryMessage(cb: (data: Uint8Array) => void): void {
    this.binaryCb = cb;
  }

  public onPing(cb: (data: Uint8Array) => void): void {
    this.pingCb = cb;
  }

  public onPong(cb: (data: Uint8Array) => void): void {
    this.pongCb = cb;
  }

  public onClose(cb: (code: number, reason: string) => void): void {
    this.closeCb = cb;
  }

  public onError(cb: (err: Error) => void): void {
    this.errorCb = cb;
  }

  public emitServerFrame(raw: Uint8Array): void {
    this.binaryCb?.(raw);
  }

  public triggerPing(data: Uint8Array): void {
    this.pingCb?.(data);
  }

  public triggerPong(data: Uint8Array): void {
    this.pongCb?.(data);
  }

  public triggerError(err: Error): void {
    this.errorCb?.(err);
  }

  public decodeSentFrames(): TunnelFrame[] {
    return this.sentRaw.map((raw) => {
      const dec = tryDecodeFrame(raw);
      if (!dec.ok) {
        throw new Error(dec.error);
      }
      return dec.frame;
    });
  }
}

describe('RemoteHostDispatcher & RemoteTunnelManager Integration', () => {
  it('RemoteHostDispatcher enforces allowlist, CF-SIG-V1, biometric key on approvals, and strips SSE images', async () => {
    const tempDir = mkdtempSync(join(tmpdir(), 'cf-rdisp-'));
    try {
      const store = new RemoteConfigStore({
        userDataDir: tempDir,
        secretProtector: createMemoryProtector(),
      });
      const desktopKeys = generateP256KeyPair();
      const sessionKeys = generateP256KeyPair();
      const bioKeys = generateP256KeyPair();
      const deviceId = '11111111-1111-4111-8111-111111111111';
      const mobileId = '22222222-2222-4222-8222-222222222222';
      const pairingId = '33333333-3333-4333-8333-333333333333';
      const sessionEpochHex32 = 'a'.repeat(32);
      const nowSec = 1_750_000_000;

      store.commitDesktopBinding({
        portalUrl: 'https://portal.codexflow.example',
        userId: 'user-disp',
        accountEmail: 'disp@example.com',
        deviceId,
        deviceName: 'DispHost',
        hostInstanceId: 'inst-1',
        bindingVersion: 1,
        dataDirHash: 'c'.repeat(64),
        desktopSpkiB64: desktopKeys.spkiB64,
        desktopKeyId: desktopKeys.keyId,
        desktopSpkiSha256: desktopKeys.spkiSha256,
        desktopPrivateKeyPem: desktopKeys.privateKeyPem,
        deviceToken: 'cfd_tok',
        revocationSeq: 1,
      });

      store.upsertActivePairing({
        pairingId,
        mobileId,
        platform: 'ios',
        deviceName: 'iPhone 16',
        bindingVersion: 1,
        sessionSpkiB64: sessionKeys.spkiB64,
        sessionKeyId: sessionKeys.keyId,
        bioSpkiB64: bioKeys.spkiB64,
        bioKeyId: bioKeys.keyId,
        status: 'active',
        pairedAtUtc: new Date(nowSec * 1000).toISOString(),
      });

      const upstreamSpy = vi.fn(
        async (input: Parameters<typeof fetch>[0], init?: Parameters<typeof fetch>[1]) => {
        const urlStr = String(input);
        if (urlStr.endsWith('/api/gateway/sse?sessionId=s1')) {
          const sseBytes = Buffer.from(
            'event: approval\ndata: {"approvalId":"a1","previewImageBase64":"PIXEL_SECRET","status":"pending"}\n\n',
            'utf8',
          );
          const stream = new ReadableStream<Uint8Array>({
            start(controller) {
              controller.enqueue(sseBytes);
              controller.close();
            },
          });
          return new Response(stream, {
            status: 200,
            headers: { 'content-type': 'text/event-stream; charset=utf-8' },
          });
        }
        expect((init?.headers as Record<string, string>)['Authorization']).toBe('Bearer ctrl-secret');
        expect((init?.headers as Record<string, string>)['X-CF-Verified-Envelope-Kind']).toBe(
          'approval',
        );
        return new Response(JSON.stringify({ success: true }), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        });
      });

      const dispatcher = new RemoteHostDispatcher({
        configStore: store,
        nonceGuard: new ReplayNonceGuard(),
        uploadStaging: new RemoteUploadStaging({ userDataDir: tempDir }),
        getDispatchTarget: () => ({
          baseUrl: 'http://127.0.0.1:51234',
          accessToken: 'acc-secret',
          controlToken: 'ctrl-secret',
          remoteBridgeToken: 'bridge-secret',
          instanceId: 'inst-1',
          dataDirectory: tempDir,
        }),
        getValidEpochCerts: () => [
          {
            deviceId,
            bindingVersion: 1,
            sessionEpochHex32,
            issuedAtUnix: nowSec - 10,
            expiresAtUnix: nowSec + 3500,
            signatureB64U: 'sig',
          },
        ],
        upstreamFetch: upstreamSpy as unknown as typeof fetch,
        nowMs: () => nowSec * 1000,
      });

      const signHeaders = (params: {
        keyId: string;
        privateKeyPem: string;
        keyPurpose: 'device_session' | 'biometric_approval';
        method: string;
        path: string;
        query: string;
        bodyBytes?: Uint8Array;
        contentType?: string;
        nonce: string;
      }) => {
        const bodyLen = params.bodyBytes?.byteLength ?? 0;
        const bodySha =
          bodyLen > 0 ? sha256HexLower(params.bodyBytes!) : EMPTY_BODY_SHA256_HEX;
        const text = buildRequestSigV1({
          deviceId,
          bindingVersion: 1,
          sessionEpochHex32,
          mobileId,
          keyId: params.keyId,
          keyPurpose: params.keyPurpose,
          methodUpper: params.method,
          canonicalPath: params.path,
          canonicalQuery: params.query,
          ifNoneMatchOrEmpty: '',
          uploadIdOrEmpty: '',
          fileSha256OrEmpty: '',
          clientOpIdOrEmpty: '',
          canonicalContentType: params.contentType ?? '',
          declaredContentLength: bodyLen,
          bodySha256LowerHex: bodySha,
          unixTimestampSeconds: nowSec,
          nonceHex32: params.nonce,
        });
        const sig = signP256LowS(params.privateKeyPem, text);
        return [
          { name: 'X-CF-Mobile-Id', value: mobileId },
          { name: 'X-CF-Key-Id', value: params.keyId },
          { name: 'X-CF-Key-Purpose', value: params.keyPurpose },
          { name: 'X-CF-Session-Epoch', value: sessionEpochHex32 },
          { name: 'X-CF-Timestamp', value: String(nowSec) },
          { name: 'X-CF-Nonce', value: params.nonce },
          { name: 'X-CF-Sig', value: sig },
          ...(params.contentType ? [{ name: 'Content-Type', value: params.contentType }] : []),
          ...(bodyLen > 0
            ? [
                { name: 'Content-Length', value: String(bodyLen) },
                { name: 'X-CF-Body-Sha256', value: bodySha },
              ]
            : []),
        ];
      };

      // 1. Disallowed route rejected (403)
      const disallowed = await dispatcher.dispatch({
        method: 'POST',
        path: '/api/gateway/workspaces',
        query: '',
        headers: signHeaders({
          keyId: sessionKeys.keyId,
          privateKeyPem: sessionKeys.privateKeyPem,
          keyPurpose: 'device_session',
          method: 'POST',
          path: '/api/gateway/workspaces',
          query: '',
          nonce: randomHex32(),
        }),
        bodyChunks: (async function* () {})(),
        signal: new AbortController().signal,
      });
      expect(disallowed.statusCode).toBe(403);

      // 2. Approval with device_session key must fail (403 biometric_key_required)
      const reqDigest = 'd'.repeat(64);
      const envText = buildApprovalEnvV1(
        deviceId,
        1,
        's1',
        'command',
        'apr-9',
        'e'.repeat(64),
        'f'.repeat(64),
        reqDigest,
        nowSec + 60,
      );
      const approvalBody = Buffer.from(
        JSON.stringify({
          requestDigest: reqDigest,
          decision: 'allow_once',
          signedEnvelope: {
            sessionId: 's1',
            approvalKind: 'command',
            textSummarySha256: 'e'.repeat(64),
            payloadSha256: 'f'.repeat(64),
            expiresAtUnix: nowSec + 60,
            sigB64U: signP256LowS(desktopKeys.privateKeyPem, envText),
          },
        }),
        'utf8',
      );

      const nonBioApproval = await dispatcher.dispatch({
        method: 'POST',
        path: '/api/gateway/approvals/apr-9',
        query: '',
        headers: signHeaders({
          keyId: sessionKeys.keyId,
          privateKeyPem: sessionKeys.privateKeyPem,
          keyPurpose: 'device_session',
          method: 'POST',
          path: '/api/gateway/approvals/apr-9',
          query: '',
          bodyBytes: approvalBody,
          contentType: 'application/json',
          nonce: randomHex32(),
        }),
        bodyChunks: (async function* () {
          yield approvalBody;
        })(),
        signal: new AbortController().signal,
      });
      expect(nonBioApproval.statusCode).toBe(403);

      // 3. Approval with biometric_approval key + valid CF-APPROVAL-ENV-V1 succeeds
      const bioApproval = await dispatcher.dispatch({
        method: 'POST',
        path: '/api/gateway/approvals/apr-9',
        query: '',
        headers: signHeaders({
          keyId: bioKeys.keyId,
          privateKeyPem: bioKeys.privateKeyPem,
          keyPurpose: 'biometric_approval',
          method: 'POST',
          path: '/api/gateway/approvals/apr-9',
          query: '',
          bodyBytes: approvalBody,
          contentType: 'application/json',
          nonce: randomHex32(),
        }),
        bodyChunks: (async function* () {
          yield approvalBody;
        })(),
        signal: new AbortController().signal,
      });
      expect(bioApproval.statusCode).toBe(200);

      // 4. SSE strips previewImageBase64
      const sseRes = await dispatcher.dispatch({
        method: 'GET',
        path: '/api/gateway/sse',
        query: 'sessionId=s1',
        headers: signHeaders({
          keyId: sessionKeys.keyId,
          privateKeyPem: sessionKeys.privateKeyPem,
          keyPurpose: 'device_session',
          method: 'GET',
          path: '/api/gateway/sse',
          query: 'sessionId=s1',
          nonce: randomHex32(),
        }),
        bodyChunks: (async function* () {})(),
        signal: new AbortController().signal,
      });
      expect(sseRes.statusCode).toBe(200);
      const sseBody = await readStreamText(sseRes.bodyStream);
      expect(sseBody).not.toContain('PIXEL_SECRET');
      expect(sseBody).toContain('"status":"pending"');
    } finally {
      rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('RemoteTunnelManager executes bind, Hello->HelloAck->epoch_cert, PairRequest SAS approve, host-events, credit-controlled relay, and RevokeNotice', async () => {
    const tempDir = mkdtempSync(join(tmpdir(), 'cf-rtun-'));
    try {
      const store = new RemoteConfigStore({
        userDataDir: tempDir,
        secretProtector: createMemoryProtector(),
      });
      store.setPortalUrl('https://portal.codexflow.example');

      let currentDir = tempDir;
      const switchCalls: Array<string | null> = [];
      const mockTransport = new MockWsTransport();
      const revocationPushes: Array<Record<string, unknown>> = [];

      const sessionKeys = generateP256KeyPair();
      const bioKeys = generateP256KeyPair();
      const mobileId = '44444444-4444-4444-8444-444444444444';
      const pairingId = '55555555-5555-4555-8555-555555555555';
      const ticketId = '66666666-6666-4666-8666-666666666666';

      const largeJsonText = JSON.stringify({
        status: 'ok',
        padding: 'a'.repeat(TUNNEL_MAX_PAYLOAD_BYTES + 2048),
      });
      const largeBody = Buffer.from(largeJsonText, 'utf8');
      const expectedTailBytes = largeBody.byteLength - TUNNEL_MAX_PAYLOAD_BYTES;
      let boundDeviceId = '';
      let boundDesktopSpkiSha256 = '';
      let boundDesktopKeyId = '';
      let boundDesktopSpkiB64 = '';

      const fetchMock = vi.fn(
        async (input: Parameters<typeof fetch>[0], init?: Parameters<typeof fetch>[1]) => {
        const url = String(input);
        if (url.endsWith('/api/auth/otp/send')) {
          return new Response(JSON.stringify({ message: 'OTP dispatched' }), { status: 202 });
        }
        if (url.endsWith('/api/auth/otp/verify')) {
          const parsed = JSON.parse(String(init?.body ?? '{}')) as {
            desktopDeviceId: string;
            desktopSpkiSha256: string;
          };
          boundDeviceId = parsed.desktopDeviceId;
          boundDesktopSpkiSha256 = parsed.desktopSpkiSha256;
          return new Response(
            JSON.stringify({
              purpose: 'desktop_bind',
              token: 'jwt_bind_once',
              expiresAt: new Date(Date.now() + 60_000).toISOString(),
              userId: 'user-remote-1',
            }),
            { status: 200 },
          );
        }
        if (url.endsWith('/api/devices/desktop/bind')) {
          const parsed = JSON.parse(String(init?.body ?? '{}')) as {
            deviceId: string;
            desktopSpkiB64: string;
          };
          boundDeviceId = parsed.deviceId;
          boundDesktopSpkiB64 = parsed.desktopSpkiB64;
          boundDesktopKeyId = boundDesktopSpkiSha256.slice(0, 32);
          return new Response(
            JSON.stringify({
              deviceId: parsed.deviceId,
              userId: 'user-remote-1',
              bindingVersion: 1,
              revocationSeq: 3,
              deviceToken: 'cfd_bound_token',
              desktopKeyId: boundDesktopKeyId,
              desktopSpkiSha256: boundDesktopSpkiSha256,
            }),
            { status: 200 },
          );
        }
        if (url.includes('/api/internal/remote/revocations')) {
          revocationPushes.push(JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>);
          return new Response(JSON.stringify({ applied: true, cancelledActiveRemoteTurns: 0 }), {
            status: 200,
          });
        }
        if (url.includes('/api/internal/remote/host-events')) {
          return new Response(
            JSON.stringify({
              summaryEpoch: 9,
              events: [
                {
                  eventId: 77,
                  summaryEpoch: 9,
                  sourceKind: 'approval',
                  opaqueRefId: 'apr-77',
                  sessionId: 'sess-1',
                  status: 'pending',
                  isTerminal: false,
                  summary: 'Pending command approval',
                  createdAtUtc: '2026-11-22T00:00:00Z',
                  expiresAtUtc: '2026-11-22T00:05:00Z',
                },
              ],
            }),
            { status: 200 },
          );
        }
        if (url.includes('/api/system/status')) {
          return new Response(largeBody, {
            status: 200,
            headers: { 'content-type': 'application/json' },
          });
        }
        return new Response(JSON.stringify({ ok: true }), { status: 200 });
      });

      const mgr = new RemoteTunnelManager({
        configStore: store,
        nonceGuard: new ReplayNonceGuard(),
        uploadStaging: new RemoteUploadStaging({ userDataDir: tempDir }),
        getDispatchTarget: () => ({
          baseUrl: 'http://127.0.0.1:52345',
          accessToken: 'acc-t',
          controlToken: 'ctrl-t',
          remoteBridgeToken: 'bridge-t',
          instanceId: 'inst-1',
          dataDirectory: currentDir,
        }),
        getDataDirectory: () => currentDir,
        switchAccountDataDirectory: async (hash) => {
          switchCalls.push(hash);
          currentDir = hash ? join(tempDir, 'remote-profiles', hash, 'host-data') : tempDir;
        },
        dialWebSocket: async () => ({
          ok: true,
          transport: mockTransport,
        }),
        fetchFn: fetchMock as unknown as typeof fetch,
        epochRotateIntervalMs: 600_000,
        pingIntervalMs: 600_000,
        hostEventPollIntervalMs: 600_000,
      });

      await mgr.sendBindOtp('RemoteUser@Example.com', 'Studio-PC');
      const bindRes = await mgr.verifyBindOtpAndBind({
        email: 'RemoteUser@Example.com',
        code: '123456',
      });
      expect(bindRes.status).toBe('bound');
      expect(switchCalls).toHaveLength(1);

      // Wait for async dial + Hello frame
      await vi.waitFor(() => {
        expect(mockTransport.sentRaw.length).toBeGreaterThanOrEqual(1);
      });

      const firstFrames = mockTransport.decodeSentFrames();
      expect(firstFrames[0]!.header.frameType).toBe(TunnelFrameType.Hello);
      expect(firstFrames[0]!.header.connEpoch).toBe(0);

      // Portal sends HelloAck(connEpoch=4)
      mockTransport.emitServerFrame(
        encodeJsonFrame(TunnelFrameType.HelloAck, TunnelFrameFlags.Priority, 4, 0, {
          connEpoch: 4,
          latestAccountRevSeq: 2,
          latestDeviceRevSeq: 4,
          activePairings: [],
          revokedMobileIds: [],
        }),
      );

      await vi.waitFor(() => {
        const frames = mockTransport.decodeSentFrames();
        expect(
          frames.some(
            (f) =>
              f.header.frameType === TunnelFrameType.HostState &&
              deserializeJsonPayload<HostStatePayload>(f.payload).state === 'epoch_cert',
          ),
        ).toBe(true);
        expect(frames.some((f) => f.header.frameType === TunnelFrameType.HostEvent)).toBe(true);
      });

      const framesAfterAck = mockTransport.decodeSentFrames();
      const epochState = deserializeJsonPayload<HostStatePayload>(
        framesAfterAck.find((f) => f.header.frameType === TunnelFrameType.HostState)!.payload,
      );
      const epochCert = epochState.sessionEpochCert!;
      const hostEvent = deserializeJsonPayload<HostEventPayload>(
        framesAfterAck.find((f) => f.header.frameType === TunnelFrameType.HostEvent)!.payload,
      );
      expect(hostEvent.summaryEpoch).toBe('9');
      expect(hostEvent.opaqueRefId).toBe('apr-77');

      // Portal sends PairRequest; verify SAS6 + PairAck decision === 'approve'
      const pairNonce = randomHex32();
      const pairExp = Math.floor(Date.now() / 1000) + 180;
      const pairV1 = buildPairV1(
        ticketId,
        boundDeviceId,
        1,
        mobileId,
        boundDesktopKeyId,
        boundDesktopSpkiSha256,
        sessionKeys.keyId,
        sessionKeys.spkiSha256,
        bioKeys.keyId,
        bioKeys.spkiSha256,
        pairNonce,
        pairExp,
      );
      const bioPop = buildBioPopV1(mobileId, ticketId, bioKeys.keyId, pairNonce, pairExp);
      const pairReqPayload: PairRequestPayload = {
        ticketId,
        pairingId,
        mobileId,
        platform: 'ios',
        deviceName: 'Test Phone',
        bindingVersion: 1,
        sessionSpkiB64: sessionKeys.spkiB64,
        bioSpkiB64: bioKeys.spkiB64,
        pairRecordV1Text: pairV1,
        pairRecordSigB64U: signP256LowS(sessionKeys.privateKeyPem, pairV1),
        bioPopV1Text: bioPop,
        bioPopSigB64U: signP256LowS(bioKeys.privateKeyPem, bioPop),
        hmacHex: '0'.repeat(64),
        expiresAtUnix: pairExp,
      };
      mockTransport.emitServerFrame(
        encodeJsonFrame(TunnelFrameType.PairRequest, TunnelFrameFlags.Priority, 4, 0, pairReqPayload),
      );

      expect(mgr.getStatusSnapshot().pendingPairRequests).toHaveLength(1);
      await mgr.respondToPairRequest(pairingId, true);

      const pairAckFrame = mockTransport
        .decodeSentFrames()
        .filter((f) => f.header.frameType === TunnelFrameType.HostState)
        .map((f) => deserializeJsonPayload<HostStatePayload>(f.payload))
        .find((s) => s.state === 'pair_ack');
      expect(pairAckFrame?.pairAck?.decision).toBe('approve');
      expect(
        verifyP256LowS(
          boundDesktopSpkiB64,
          pairAckFrame!.pairAck!.pairAckV1Text!,
          pairAckFrame!.pairAck!.pairAckSigB64U!,
        ),
      ).toBe(true);

      // Test ReqStart + WindowUpdate backpressure on response > 64 KiB
      const nowSec = Math.floor(Date.now() / 1000);
      const nonceGet = randomHex32();
      const sigText = buildRequestSigV1({
        deviceId: boundDeviceId,
        bindingVersion: 1,
        sessionEpochHex32: epochCert.sessionEpochHex32,
        mobileId,
        keyId: sessionKeys.keyId,
        keyPurpose: 'device_session',
        methodUpper: 'GET',
        canonicalPath: '/api/system/status',
        canonicalQuery: '',
        ifNoneMatchOrEmpty: '',
        uploadIdOrEmpty: '',
        fileSha256OrEmpty: '',
        clientOpIdOrEmpty: '',
        canonicalContentType: '',
        declaredContentLength: 0,
        bodySha256LowerHex: EMPTY_BODY_SHA256_HEX,
        unixTimestampSeconds: nowSec,
        nonceHex32: nonceGet,
      });
      const reqStart: ReqStartPayload = {
        method: 'GET',
        path: '/api/system/status',
        query: '',
        headers: [
          { name: 'X-CF-Mobile-Id', value: mobileId },
          { name: 'X-CF-Key-Id', value: sessionKeys.keyId },
          { name: 'X-CF-Key-Purpose', value: 'device_session' },
          { name: 'X-CF-Session-Epoch', value: epochCert.sessionEpochHex32 },
          { name: 'X-CF-Timestamp', value: String(nowSec) },
          { name: 'X-CF-Nonce', value: nonceGet },
          { name: 'X-CF-Sig', value: signP256LowS(sessionKeys.privateKeyPem, sigText) },
        ],
      };

      mockTransport.emitServerFrame(
        encodeJsonFrame(TunnelFrameType.ReqStart, TunnelFrameFlags.Fin, 4, 1, reqStart),
      );

      // Wait until the first 64 KiB ResChunk (non-Fin) goes out; the remaining 2 KiB waits for WindowUpdate
      await vi.waitFor(() => {
        const resChunks = mockTransport
          .decodeSentFrames()
          .filter((f) => f.header.streamId === 1 && f.header.frameType === TunnelFrameType.ResChunk);
        expect(resChunks.length).toBe(1);
        expect(resChunks[0]!.isFin).toBe(false);
        expect(resChunks[0]!.payload.byteLength).toBe(65536);
      });

      const resHead = mockTransport
        .decodeSentFrames()
        .find((f) => f.header.streamId === 1 && f.header.frameType === TunnelFrameType.ResHead)!;
      expect(deserializeJsonPayload<ResHeadPayload>(resHead.payload).statusCode).toBe(200);

      // Grant WindowUpdate so remaining tail bytes finish with Fin
      mockTransport.emitServerFrame(encodeWindowUpdate(4, 1, 65536, 1));
      await vi.waitFor(() => {
        const resChunks = mockTransport
          .decodeSentFrames()
          .filter((f) => f.header.streamId === 1 && f.header.frameType === TunnelFrameType.ResChunk);
        expect(resChunks.length).toBe(2);
        expect(resChunks[1]!.isFin).toBe(true);
        expect(resChunks[1]!.payload.byteLength).toBe(expectedTailBytes);
      });

      // Verify ReqBody triggers desktop WindowUpdate feedback back to Portal
      const req2 = encodeJsonFrame(TunnelFrameType.ReqStart, TunnelFrameFlags.None, 4, 2, {
        ...reqStart,
        path: '/api/nonexistent/route',
      });
      mockTransport.emitServerFrame(req2);
      mockTransport.emitServerFrame(
        encodeFrame(TunnelFrameType.ReqBody, TunnelFrameFlags.Fin, 4, 2, new Uint8Array([1, 2, 3, 4])),
      );
      await vi.waitFor(() => {
        const wuFrames = mockTransport
          .decodeSentFrames()
          .filter((f) => f.header.streamId === 2 && f.header.frameType === TunnelFrameType.WindowUpdate);
        if (wuFrames.length > 0) {
          expect(tryDecodeWindowUpdate(wuFrames[0]!.payload)).toEqual({
            ok: true,
            deltaBytes: 4,
            deltaChunks: 1,
          });
        }
      });

      // Verify RevokeNotice(pairing) updates watermarks, pushes local revocations, and replies RevokeNotice(Fin|Priority)
      const revokeNotice: RevokeNoticePayload = {
        targetRevSeq: 15,
        scope: 'pairing',
        targetIds: [pairingId],
      };
      mockTransport.emitServerFrame(
        encodeJsonFrame(TunnelFrameType.RevokeNotice, TunnelFrameFlags.Priority, 4, 0, revokeNotice),
      );
      await vi.waitFor(() => {
        const sentRevokes = mockTransport
          .decodeSentFrames()
          .filter((f) => f.header.frameType === TunnelFrameType.RevokeNotice);
        expect(sentRevokes.length).toBe(1);
        expect(sentRevokes[0]!.isFin).toBe(true);
        expect(sentRevokes[0]!.isPriority).toBe(true);
      });
      expect(mgr.getStatusSnapshot().config.watermarks.deviceRevocationSeq).toBe(15);
      expect(mgr.getStatusSnapshot().config.pairings[0]?.status).toBe('revoked');
      expect(revocationPushes.length).toBeGreaterThanOrEqual(2);

      mgr.stop();
    } finally {
      rmSync(tempDir, { recursive: true, force: true });
    }
  });
});
