import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import {
  buildSingleFileMultipart,
  extractMultipartBoundary,
  parseSingleFileMultipart,
} from '@codexflow-shared/multipartBuilder';
import {
  EMPTY_BODY_SHA256_HEX,
  StreamRstErrorCode,
  TUNNEL_HEADER_SIZE,
  TUNNEL_MAX_PAYLOAD_BYTES,
  TunnelFrameFlags,
  TunnelFrameType,
  base64UrlToBytes,
  buildApprovalCanonicalPayloadV1,
  buildApprovalCanonicalTextV1,
  buildApprovalEnvV1,
  buildBioPopV1,
  buildCanonicalStructuredActionV1,
  buildEpochCertV1,
  buildExchangeV1,
  buildPairAckV1,
  buildPairV1,
  buildPairingQrUri,
  buildPlanEnvV1,
  buildRequestSigV1,
  bytesToBase64Url,
  bytesToHexLower,
  canonicalizeContentType,
  canonicalizeQuery,
  encodeFrame,
  encodeWindowUpdate,
  hexToBytes,
  parsePairingQrUri,
  tryDecodeFrame,
  tryDecodeWindowUpdate,
} from '@codexflow-shared/remoteProtocol';
import {
  SseStreamParser,
  formatSseEvent,
  sanitizeSseDataJson,
} from '@codexflow-shared/sseStreamParser';
import {
  computeChatOpDigestV1,
  computeSas6Digits,
  createSignedApprovalEnvelopeV1,
  createSignedPlanEnvelopeV1,
  generateP256KeyPair,
  sha256HexLower,
  signP256LowS,
  verifyApprovalEnvelopeV1,
  verifyExchangeSigV1,
  verifyP256LowS,
  verifyPairRequestCryptographicIntegrity,
  verifyPlanEnvelopeV1,
  verifyRequestSigV1,
  verifySignedEpochCert,
} from '../../../electron/main/remote/remoteCrypto';

interface FrozenCryptoVectors {
  ids: {
    ticketId: string;
    deviceId: string;
    mobileId: string;
    pairingId: string;
    bindingVersion: number;
    nonceHex32: string;
    sessionEpochHex32: string;
    expiresAtUnix: number;
  };
  keys: {
    desktop: { spkiB64: string; spkiSha256: string; keyId: string };
    mobileSession: { spkiB64: string; spkiSha256: string; keyId: string };
    mobileBio: { spkiB64: string; spkiSha256: string; keyId: string };
  };
  domains: {
    pairV1: { text: string; sha256: string; sas6: string; sigB64U: string };
    bioPopV1: { text: string; sigB64U: string };
    pairAckV1: { text: string; sigB64U: string };
    exchangeV1: { text: string; sigB64U: string };
    epochCertV1: { text: string; sigB64U: string };
    approvalEnvV1: { text: string; sigB64U: string };
    planEnvV1: { text: string; sigB64U: string };
    sigV1GetScreenshot: { text: string; sigB64U: string };
    sigV1Upload: { text: string; bodySha256: string; fileSha256: string; sigB64U: string };
  };
  frames: {
    windowUpdateHex: string;
  };
  chatOpV1: {
    digestHex64: string;
  };
}

const P256_ORDER_N =
  0xffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551n;

const currentDir = dirname(fileURLToPath(import.meta.url));
const vectorsPath = resolve(
  currentDir,
  '../../../../../tests/vectors/crypto-protocol-vectors.v1.json',
);

function loadVectors(): FrozenCryptoVectors {
  return JSON.parse(readFileSync(vectorsPath, 'utf8')) as FrozenCryptoVectors;
}

describe('Remote Protocol & Frozen Cross-Stack Vectors (v1)', () => {
  it('matches every frozen domain string, SHA-256/SAS6, ECDSA signature, WindowUpdate frame, and CF-CHAT-OP-V1 digest', () => {
    const v = loadVectors();

    // 1. CF-PAIR-V1 + SAS6 + CF-BIO-POP-V1
    const builtPairV1 = buildPairV1(
      v.ids.ticketId,
      v.ids.deviceId,
      v.ids.bindingVersion,
      v.ids.mobileId,
      v.keys.desktop.keyId,
      v.keys.desktop.spkiSha256,
      v.keys.mobileSession.keyId,
      v.keys.mobileSession.spkiSha256,
      v.keys.mobileBio.keyId,
      v.keys.mobileBio.spkiSha256,
      v.ids.nonceHex32,
      v.ids.expiresAtUnix,
    );
    expect(builtPairV1).toBe(v.domains.pairV1.text);
    expect(sha256HexLower(builtPairV1)).toBe(v.domains.pairV1.sha256);
    expect(computeSas6Digits(builtPairV1)).toBe(v.domains.pairV1.sas6);
    expect(
      verifyP256LowS(v.keys.mobileSession.spkiB64, builtPairV1, v.domains.pairV1.sigB64U),
    ).toBe(true);

    const builtBioPop = buildBioPopV1(
      v.ids.mobileId,
      v.ids.ticketId,
      v.keys.mobileBio.keyId,
      v.ids.nonceHex32,
      v.ids.expiresAtUnix,
    );
    expect(builtBioPop).toBe(v.domains.bioPopV1.text);
    expect(
      verifyP256LowS(v.keys.mobileBio.spkiB64, builtBioPop, v.domains.bioPopV1.sigB64U),
    ).toBe(true);

    const integrity = verifyPairRequestCryptographicIntegrity({
      expectedTicketId: v.ids.ticketId,
      deviceId: v.ids.deviceId,
      bindingVersion: v.ids.bindingVersion,
      desktopKeyId: v.keys.desktop.keyId,
      desktopSpkiSha256: v.keys.desktop.spkiSha256,
      mobileId: v.ids.mobileId,
      sessionSpkiB64: v.keys.mobileSession.spkiB64,
      bioSpkiB64: v.keys.mobileBio.spkiB64,
      pairRecordV1Text: v.domains.pairV1.text,
      pairRecordSigB64U: v.domains.pairV1.sigB64U,
      bioPopV1Text: v.domains.bioPopV1.text,
      bioPopSigB64U: v.domains.bioPopV1.sigB64U,
      pairingNonceHex32: v.ids.nonceHex32,
      expiresAtUnix: v.ids.expiresAtUnix,
      nowUnix: v.ids.expiresAtUnix - 10,
    });
    expect(integrity).toEqual({
      ok: true,
      sessionKeyId: v.keys.mobileSession.keyId,
      sessionSpkiSha256: v.keys.mobileSession.spkiSha256,
      bioKeyId: v.keys.mobileBio.keyId,
      bioSpkiSha256: v.keys.mobileBio.spkiSha256,
    });

    // 2. CF-PAIR-ACK-V1 & CF-EXCHANGE-V1 & CF-EPOCH-CERT-V1
    const builtPairAck = buildPairAckV1(v.ids.pairingId, v.domains.pairV1.sha256, 1795299500);
    expect(builtPairAck).toBe(v.domains.pairAckV1.text);
    expect(verifyP256LowS(v.keys.desktop.spkiB64, builtPairAck, v.domains.pairAckV1.sigB64U)).toBe(
      true,
    );

    const builtExchange = buildExchangeV1(
      v.ids.pairingId,
      v.ids.mobileId,
      v.ids.nonceHex32,
      1795299550,
    );
    expect(builtExchange).toBe(v.domains.exchangeV1.text);
    expect(
      verifyExchangeSigV1({
        sessionSpkiB64: v.keys.mobileSession.spkiB64,
        pairingId: v.ids.pairingId,
        mobileId: v.ids.mobileId,
        nonceHex32: v.ids.nonceHex32,
        unixTimestamp: 1795299550,
        sigB64U: v.domains.exchangeV1.sigB64U,
      }),
    ).toBe(true);

    const builtEpoch = buildEpochCertV1(
      v.ids.deviceId,
      v.ids.bindingVersion,
      v.ids.sessionEpochHex32,
      1795299000,
      1795313400,
    );
    expect(builtEpoch).toBe(v.domains.epochCertV1.text);
    expect(
      verifySignedEpochCert(
        v.keys.desktop.spkiB64,
        {
          deviceId: v.ids.deviceId,
          bindingVersion: v.ids.bindingVersion,
          sessionEpochHex32: v.ids.sessionEpochHex32,
          issuedAtUnix: 1795299000,
          expiresAtUnix: 1795313400,
          signatureB64U: v.domains.epochCertV1.sigB64U,
        },
        1795299100,
      ),
    ).toBe(true);

    // 3. CF-APPROVAL-ENV-V1 & CF-PLAN-ENV-V1
    const builtApproval = buildApprovalEnvV1(
      v.ids.deviceId,
      v.ids.bindingVersion,
      'sess-001',
      'command',
      'apr-001',
      'a'.repeat(64),
      'b'.repeat(64),
      'c'.repeat(64),
      v.ids.expiresAtUnix,
    );
    expect(builtApproval).toBe(v.domains.approvalEnvV1.text);
    expect(
      verifyApprovalEnvelopeV1({
        desktopSpkiB64: v.keys.desktop.spkiB64,
        deviceId: v.ids.deviceId,
        bindingVersion: v.ids.bindingVersion,
        sessionId: 'sess-001',
        approvalKind: 'command',
        approvalId: 'apr-001',
        textSummarySha256: 'a'.repeat(64),
        payloadSha256: 'b'.repeat(64),
        requestDigest: 'c'.repeat(64),
        expiresAtUnix: v.ids.expiresAtUnix,
        sigB64U: v.domains.approvalEnvV1.sigB64U,
        nowUnix: v.ids.expiresAtUnix - 1,
      }),
    ).toBe(true);

    const builtPlan = buildPlanEnvV1(
      v.ids.deviceId,
      v.ids.bindingVersion,
      'sess-001',
      'plan-001',
      7,
      'd'.repeat(64),
      'approve,execute',
      true,
      v.ids.expiresAtUnix,
    );
    expect(builtPlan).toBe(v.domains.planEnvV1.text);
    expect(
      verifyPlanEnvelopeV1({
        desktopSpkiB64: v.keys.desktop.spkiB64,
        deviceId: v.ids.deviceId,
        bindingVersion: v.ids.bindingVersion,
        sessionId: 'sess-001',
        planArtifactId: 'plan-001',
        planVersion: 7,
        planMarkdownSha256: 'd'.repeat(64),
        allowedActionsCsv: 'approve,execute',
        startExecution: true,
        expiresAtUnix: v.ids.expiresAtUnix,
        sigB64U: v.domains.planEnvV1.sigB64U,
        nowUnix: v.ids.expiresAtUnix - 1,
      }),
    ).toBe(true);

    // 4. 19-line CF-SIG-V1 (GET screenshot with If-None-Match + POST upload)
    const builtGetSig = buildRequestSigV1({
      deviceId: v.ids.deviceId,
      bindingVersion: v.ids.bindingVersion,
      sessionEpochHex32: v.ids.sessionEpochHex32,
      mobileId: v.ids.mobileId,
      keyId: v.keys.mobileSession.keyId,
      keyPurpose: 'device_session',
      methodUpper: 'GET',
      canonicalPath: '/api/gateway/session/sess-001/browser/screenshot',
      canonicalQuery: canonicalizeQuery([]),
      ifNoneMatchOrEmpty: '"etag-v1"',
      uploadIdOrEmpty: '',
      fileSha256OrEmpty: '',
      clientOpIdOrEmpty: '',
      canonicalContentType: '',
      declaredContentLength: 0,
      bodySha256LowerHex: EMPTY_BODY_SHA256_HEX,
      unixTimestampSeconds: 1795299600,
      nonceHex32: v.ids.nonceHex32,
    });
    expect(builtGetSig).toBe(v.domains.sigV1GetScreenshot.text);
    expect(
      verifyRequestSigV1({
        spkiB64: v.keys.mobileSession.spkiB64,
        deviceId: v.ids.deviceId,
        bindingVersion: v.ids.bindingVersion,
        sessionEpochHex32: v.ids.sessionEpochHex32,
        mobileId: v.ids.mobileId,
        keyId: v.keys.mobileSession.keyId,
        keyPurpose: 'device_session',
        methodUpper: 'GET',
        canonicalPath: '/api/gateway/session/sess-001/browser/screenshot',
        canonicalQuery: '',
        ifNoneMatchOrEmpty: '"etag-v1"',
        uploadIdOrEmpty: '',
        fileSha256OrEmpty: '',
        clientOpIdOrEmpty: '',
        canonicalContentType: '',
        declaredContentLength: 0,
        bodySha256LowerHex: EMPTY_BODY_SHA256_HEX,
        unixTimestampSeconds: 1795299600,
        nonceHex32: v.ids.nonceHex32,
        sigB64U: v.domains.sigV1GetScreenshot.sigB64U,
      }),
    ).toBe(true);

    const builtUploadSig = buildRequestSigV1({
      deviceId: v.ids.deviceId,
      bindingVersion: v.ids.bindingVersion,
      sessionEpochHex32: v.ids.sessionEpochHex32,
      mobileId: v.ids.mobileId,
      keyId: v.keys.mobileSession.keyId,
      keyPurpose: 'device_session',
      methodUpper: 'POST',
      canonicalPath: '/api/gateway/session/sess-001/attachments',
      canonicalQuery: '',
      ifNoneMatchOrEmpty: '',
      uploadIdOrEmpty: '9f8e7d6c-5b4a-4321-8765-abcdef012345',
      fileSha256OrEmpty: v.domains.sigV1Upload.fileSha256,
      clientOpIdOrEmpty: '',
      canonicalContentType: canonicalizeContentType('Multipart/Form-Data; boundary=boundary-123'),
      declaredContentLength: 109,
      bodySha256LowerHex: v.domains.sigV1Upload.bodySha256,
      unixTimestampSeconds: 1795299601,
      nonceHex32: v.ids.nonceHex32,
    });
    expect(builtUploadSig).toBe(v.domains.sigV1Upload.text);
    expect(
      verifyP256LowS(v.keys.mobileSession.spkiB64, builtUploadSig, v.domains.sigV1Upload.sigB64U),
    ).toBe(true);

    // 5. WindowUpdate 16+8 byte hex & CF-CHAT-OP-V1 digest
    const wuRaw = encodeWindowUpdate(12, 3, 65536, 256);
    expect(bytesToHexLower(wuRaw)).toBe(v.frames.windowUpdateHex);
    const decodedWu = tryDecodeFrame(hexToBytes(v.frames.windowUpdateHex));
    expect(decodedWu.ok).toBe(true);
    if (decodedWu.ok) {
      expect(decodedWu.frame.header.frameType).toBe(TunnelFrameType.WindowUpdate);
      expect(decodedWu.frame.header.connEpoch).toBe(12);
      expect(decodedWu.frame.header.streamId).toBe(3);
      expect(tryDecodeWindowUpdate(decodedWu.frame.payload)).toEqual({
        ok: true,
        deltaBytes: 65536,
        deltaChunks: 256,
      });
    }

    const digest = computeChatOpDigestV1({
      sessionId: 'sess-001',
      conversationIdOrEmpty: 'conv-001',
      conversationMode: 'default',
      uploadIds: ['up-2', 'up-1'],
      canonicalStructuredActionOrEmpty: null,
      message: 'Line 1\nLine 2',
    });
    expect(digest).toBe(v.chatOpV1.digestHex64);
  });

  it('rejects malleable High-S P-256 ECDSA signatures while accepting Low-S signatures', () => {
    const kp = generateP256KeyPair();
    const message = 'CF-EXCHANGE-V1\n1\n2\n0123456789abcdef0123456789abcdef\n1795300000';
    const lowSSigB64U = signP256LowS(kp.privateKeyPem, message);
    expect(verifyP256LowS(kp.spkiB64, message, lowSSigB64U)).toBe(true);

    const raw = Buffer.from(base64UrlToBytes(lowSSigB64U));
    let s = 0n;
    for (let i = 32; i < 64; i += 1) {
      s = (s << 8n) | BigInt(raw[i]!);
    }
    const highS = P256_ORDER_N - s;
    let temp = highS;
    for (let i = 63; i >= 32; i -= 1) {
      raw[i] = Number(temp & 0xffn);
      temp >>= 8n;
    }
    const highSSigB64U = bytesToBase64Url(raw);
    expect(verifyP256LowS(kp.spkiB64, message, highSSigB64U)).toBe(false);
  });

  it('enforces 14-frame structural invariants, strict single-file multipart rules, and recursive SSE sanitization', () => {
    // Control frame with non-zero streamId must throw on encode and fail decode
    expect(() =>
      encodeFrame(TunnelFrameType.Ping, TunnelFrameFlags.Priority, 1, 7, new Uint8Array(8)),
    ).toThrow(/StreamId=0/);

    // Data frame with streamId=0 must fail decode
    const badStreamZero = encodeFrame(TunnelFrameType.ReqBody, TunnelFrameFlags.Fin, 1, 1, new Uint8Array([1, 2]));
    badStreamZero[11] = 0; // force streamId=0
    expect(tryDecodeFrame(badStreamZero).ok).toBe(false);

    // Non-zero reserved header byte must fail decode
    const badReserved = encodeFrame(TunnelFrameType.ResChunk, TunnelFrameFlags.Fin, 1, 9, new Uint8Array([42]));
    badReserved[3] = 0x01;
    expect(tryDecodeFrame(badReserved).ok).toBe(false);

    // Payload exceeding 65536 bytes rejected
    expect(() =>
      encodeFrame(
        TunnelFrameType.ResChunk,
        TunnelFrameFlags.None,
        1,
        5,
        new Uint8Array(TUNNEL_MAX_PAYLOAD_BYTES + 1),
      ),
    ).toThrow();

    // Header shorter than 16 bytes rejected
    expect(tryDecodeFrame(new Uint8Array(TUNNEL_HEADER_SIZE - 1)).ok).toBe(false);
    expect(StreamRstErrorCode.Revoked).toBe(0x05);

    // Multipart round-trip & malformed boundary rejection
    const built = buildSingleFileMultipart({
      fieldName: 'file',
      fileName: '../../evil\\path/report.txt',
      contentType: 'text/plain',
      fileBytes: new Uint8Array([104, 101, 108, 108, 111]),
      boundary: 'cf-test-boundary',
    });
    expect(extractMultipartBoundary(built.contentType)).toBe('cf-test-boundary');
    const parsed = parseSingleFileMultipart(built.contentType, built.bodyBytes);
    expect(parsed.fileName).toBe('report.txt');
    expect(Buffer.from(parsed.fileBytes).toString('utf8')).toBe('hello');
    expect(() => parseSingleFileMultipart('text/plain', built.bodyBytes)).toThrow();

    // SSE chunked incremental parsing + image field stripping
    const parser = new SseStreamParser();
    const evText = formatSseEvent({
      id: '42',
      event: 'approval',
      data: JSON.stringify({
        approvalId: 'a-1',
        previewImageBase64: 'SECRET_PIXELS_1',
        nested: { preview_image_base64: 'SECRET_PIXELS_2', safe: 'ok' },
      }),
    });
    const part1 = parser.push(evText.slice(0, 17));
    expect(part1).toHaveLength(0);
    const part2 = parser.push(evText.slice(17));
    expect(part2).toHaveLength(1);
    const sanitized = sanitizeSseDataJson(part2[0]!.data);
    expect(sanitized).not.toContain('SECRET_PIXELS');
    expect(sanitized).toContain('"safe":"ok"');
  });

  it('supports Phase M0 SSE CRLF splitting, string planVersion envelopes, WYSIWYS approval helpers, and frozen QR codec', () => {
    // 1. SSE cross-chunk \r\n and incomplete tail discarded on flush
    const parser = new SseStreamParser();
    expect(parser.push('id: 7\r')).toEqual([]);
    expect(parser.push('\nevent: token\r')).toEqual([]);
    expect(parser.push('\ndata: hello\r')).toEqual([]);
    const frames = parser.push('\n\r');
    expect(frames).toEqual([{ id: '7', event: 'token', data: 'hello' }]);
    expect(parser.push('\ndata: partial-without-blank-line')).toEqual([]);
    expect(parser.flush()).toEqual([]);

    // 2. QR URI codec
    const secretB64U = bytesToBase64Url(new Uint8Array(32).fill(0x2a));
    const ticketId = '11111111-2222-4333-8444-555555555555';
    const deviceId = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';
    const reviewId = '99999999-8888-4777-8666-555555555555';
    const uri = buildPairingQrUri({
      ticketId,
      qrBootstrapToken: 'boot-tok-123',
      deviceId,
      bindingVersion: 4,
      desktopSpkiSha256: 'c'.repeat(64),
      pairingSecretB64U: secretB64U,
      reviewSessionId: reviewId,
    });
    expect(uri.startsWith('codexflow://pair?')).toBe(true);
    expect(uri).not.toContain('portal=');
    expect(parsePairingQrUri(uri)).toEqual({
      ticketId,
      qrBootstrapToken: 'boot-tok-123',
      deviceId,
      bindingVersion: 4,
      desktopSpkiSha256: 'c'.repeat(64),
      pairingSecretB64U: secretB64U,
      reviewSessionId: reviewId,
    });
    expect(() => parsePairingQrUri(`${uri}&portal=https://evil.test`)).toThrow();

    // 3. String planVersion envelope + WYSIWYS approval envelope + CanonicalStructuredAction
    const kp = generateP256KeyPair();
    const nowUnix = 1_750_000_000;
    const planEnv = createSignedPlanEnvelopeV1({
      desktopPrivateKeyPem: kp.privateKeyPem,
      deviceId,
      bindingVersion: 4,
      sessionId: 'sess-1',
      planArtifactId: 'plan-art-1',
      planVersion: 'a1b2c3d4',
      planMarkdownSha256: 'd'.repeat(64),
      allowedActionsCsv: 'approve',
      startExecution: false,
      expiresAtUnix: nowUnix + 300,
    });
    expect(
      verifyPlanEnvelopeV1({
        desktopSpkiB64: kp.spkiB64,
        deviceId,
        bindingVersion: 4,
        sessionId: 'sess-1',
        planArtifactId: 'plan-art-1',
        planVersion: 'a1b2c3d4',
        planMarkdownSha256: 'd'.repeat(64),
        allowedActionsCsv: 'approve',
        startExecution: false,
        expiresAtUnix: nowUnix + 300,
        sigB64U: planEnv.sigB64U,
        nowUnix,
      }),
    ).toBe(true);

    const textSha = sha256HexLower(
      buildApprovalCanonicalTextV1({
        approvalKind: 'command',
        approvalId: 'apr-1',
        sessionId: 'sess-1',
        title: 'Run tests',
        summary: 'npm test',
      }),
    );
    const payloadSha = sha256HexLower(
      buildApprovalCanonicalPayloadV1({
        approvalKind: 'command',
        requestDigest: 'e'.repeat(64),
        payloadText: 'npm test',
      }),
    );
    const approvalEnv = createSignedApprovalEnvelopeV1({
      desktopPrivateKeyPem: kp.privateKeyPem,
      deviceId,
      bindingVersion: 4,
      sessionId: 'sess-1',
      approvalKind: 'command',
      approvalId: 'apr-1',
      textSummarySha256: textSha,
      payloadSha256: payloadSha,
      requestDigest: 'e'.repeat(64),
      expiresAtUnix: nowUnix + 300,
    });
    expect(
      verifyApprovalEnvelopeV1({
        desktopSpkiB64: kp.spkiB64,
        deviceId,
        bindingVersion: 4,
        sessionId: 'sess-1',
        approvalKind: 'command',
        approvalId: 'apr-1',
        textSummarySha256: textSha,
        payloadSha256: payloadSha,
        requestDigest: 'e'.repeat(64),
        expiresAtUnix: nowUnix + 300,
        sigB64U: approvalEnv.sigB64U,
        nowUnix,
      }),
    ).toBe(true);

    expect(
      buildCanonicalStructuredActionV1({
        action: ' Approve ',
        planArtifactId: 'plan-1',
        planVersion: 'a1b2c3d4',
        planHash: 'f'.repeat(64),
        idempotencyKey: 'idem-1',
        startExecution: false,
      }),
    ).toBe(`approve|plan-1|a1b2c3d4|${'f'.repeat(64)}|idem-1|false`);
  });
});
