import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { buildSingleFileMultipart } from '@codexflow-shared/multipartBuilder';
import {
  base64UrlToBytes,
  buildApprovalCanonicalPayloadV1,
  buildApprovalCanonicalTextV1,
  buildApprovalEnvV1,
  buildPlanEnvV1,
  bytesToBase64,
  bytesToBase64Url,
  utf8Encode,
} from '@codexflow-shared/remoteProtocol';
import {
  IncrementalSha256,
  hmacSha256HexLower,
  sha256HexLower,
  sha256MultipartStream,
} from '@/crypto/incrementalSha256';
import {
  computeSpkiSha256AndKeyId,
  isLowSP1363Signature,
  normalizeP1363LowS,
  verifyApprovalEnvelopeAsync,
  verifyP256LowSAsync,
  verifyPlanEnvelopeAsync,
} from '@/crypto/p256Verify';
import { computeMobileChatOpDigest } from '@/services/chatService';

interface FrozenVectors {
  ids: {
    deviceId: string;
    bindingVersion: number;
    nonceHex32: string;
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
  chatOpV1: { digestHex64: string };
}

const vectorsPath = resolve(__dirname, '../../../../tests/vectors/crypto-protocol-vectors.v1.json');
const vectors = JSON.parse(readFileSync(vectorsPath, 'utf8')) as FrozenVectors;

const P256_ORDER = BigInt('0xffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551');

function toHighS(lowSigB64U: string): string {
  const bytes = base64UrlToBytes(lowSigB64U);
  const sBytes = bytes.slice(32, 64);
  let s = 0n;
  for (const b of sBytes) {
    s = (s << 8n) + BigInt(b);
  }
  let highS = P256_ORDER - s;
  const flipped = new Uint8Array(64);
  flipped.set(bytes.slice(0, 32), 0);
  for (let i = 63; i >= 32; i -= 1) {
    flipped[i] = Number(highS & 0xffn);
    highS >>= 8n;
  }
  return bytesToBase64Url(flipped);
}

async function generateDesktopKeyAndSignLowS(payloadText: string): Promise<{
  spkiB64: string;
  sigB64U: string;
  keyPair: CryptoKeyPair;
}> {
  const keyPair = await globalThis.crypto.subtle.generateKey(
    { name: 'ECDSA', namedCurve: 'P-256' },
    true,
    ['sign', 'verify'],
  );
  const spkiAb = await globalThis.crypto.subtle.exportKey('spki', keyPair.publicKey);
  const spkiB64 = bytesToBase64(new Uint8Array(spkiAb));
  const data = utf8Encode(payloadText);
  const copy = new Uint8Array(data.byteLength);
  copy.set(data);
  const rawSig = await globalThis.crypto.subtle.sign(
    { name: 'ECDSA', hash: 'SHA-256' },
    keyPair.privateKey,
    copy.buffer,
  );
  const lowS = normalizeP1363LowS(new Uint8Array(rawSig));
  return { spkiB64, sigB64U: bytesToBase64Url(lowS), keyPair };
}

async function signWithKeyLowS(privateKey: CryptoKey, payloadText: string): Promise<string> {
  const data = utf8Encode(payloadText);
  const copy = new Uint8Array(data.byteLength);
  copy.set(data);
  const rawSig = await globalThis.crypto.subtle.sign(
    { name: 'ECDSA', hash: 'SHA-256' },
    privateKey,
    copy.buffer,
  );
  return bytesToBase64Url(normalizeP1363LowS(new Uint8Array(rawSig)));
}

describe('跨端密码学与冻结协议向量校验', () => {
  it('校验 desktop / mobileSession / mobileBio 的 SPKI SHA-256 与 KeyId 与冻结向量一致', () => {
    for (const keyVec of [vectors.keys.desktop, vectors.keys.mobileSession, vectors.keys.mobileBio]) {
      const actual = computeSpkiSha256AndKeyId(keyVec.spkiB64);
      expect(actual.spkiSha256).toBe(keyVec.spkiSha256);
      expect(actual.keyId).toBe(keyVec.keyId);
    }
  });

  it('校验全部 9 条 P-256 Low-S 冻结签名向量', async () => {
    expect(sha256HexLower(vectors.domains.pairV1.text)).toBe(vectors.domains.pairV1.sha256);

    await expect(
      verifyP256LowSAsync(
        vectors.keys.mobileSession.spkiB64,
        vectors.domains.pairV1.text,
        vectors.domains.pairV1.sigB64U,
      ),
    ).resolves.toBe(true);

    await expect(
      verifyP256LowSAsync(
        vectors.keys.mobileBio.spkiB64,
        vectors.domains.bioPopV1.text,
        vectors.domains.bioPopV1.sigB64U,
      ),
    ).resolves.toBe(true);

    for (const item of [
      vectors.domains.pairAckV1,
      vectors.domains.epochCertV1,
      vectors.domains.approvalEnvV1,
      vectors.domains.planEnvV1,
    ]) {
      await expect(
        verifyP256LowSAsync(vectors.keys.desktop.spkiB64, item.text, item.sigB64U),
      ).resolves.toBe(true);
    }

    for (const item of [
      vectors.domains.exchangeV1,
      vectors.domains.sigV1GetScreenshot,
      vectors.domains.sigV1Upload,
    ]) {
      await expect(
        verifyP256LowSAsync(vectors.keys.mobileSession.spkiB64, item.text, item.sigB64U),
      ).resolves.toBe(true);
    }
  });

  it('显式拒绝 High-S P-256 ECDSA 可延展签名', async () => {
    const highSigB64U = toHighS(vectors.domains.exchangeV1.sigB64U);
    expect(isLowSP1363Signature(base64UrlToBytes(highSigB64U))).toBe(false);
    await expect(
      verifyP256LowSAsync(
        vectors.keys.mobileSession.spkiB64,
        vectors.domains.exchangeV1.text,
        highSigB64U,
      ),
    ).resolves.toBe(false);
  });

  it('校验 IncrementalSha256、HMAC-SHA256 与流式 multipart SHA-256 与共享构建器一致', async () => {
    expect(sha256HexLower('')).toBe('e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855');

    const inc = new IncrementalSha256();
    inc.update(utf8Encode('hello '));
    inc.update(utf8Encode('world'));
    expect(inc.digestHexLower()).toBe(sha256HexLower('hello world'));

    const mac = hmacSha256HexLower(utf8Encode('secret-key'), utf8Encode('message'));
    expect(mac).toMatch(/^[0-9a-f]{64}$/);

    const fileBytes = new Uint8Array(700_000);
    for (let i = 0; i < fileBytes.length; i += 1) {
      fileBytes[i] = (i * 13 + 7) & 0xff;
    }
    const boundary = 'cfmb_testboundary123';
    const fileName = 'archive.tar.gz';
    const built = buildSingleFileMultipart({
      fileName,
      contentType: 'application/gzip',
      fileBytes,
      boundary,
    });
    const head = utf8Encode(
      `--${boundary}\r\n` +
        `Content-Disposition: form-data; name="file"; filename="${fileName}"\r\n` +
        `Content-Type: application/gzip\r\n\r\n`,
    );
    const tail = utf8Encode(`\r\n--${boundary}--\r\n`);
    const streamed = await sha256MultipartStream(head, new Blob([fileBytes]), tail, 128 * 1024);

    expect(streamed.fileSha256).toBe(sha256HexLower(fileBytes));
    expect(streamed.bodySha256).toBe(sha256HexLower(built.bodyBytes));
  });

  it('校验 CF-APPROVAL-ENV-V1 与 CF-PLAN-ENV-V1 WYSIWYS 信封与篡改/过期拒绝', async () => {
    const nowSec = 1_800_000_000;
    const expSec = nowSec + 120;
    const deviceId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
    const sessionId = 'sess-wysiwys-1';
    const approvalId = 'apr-wysiwys-1';
    const requestDigest = '1'.repeat(64);
    const title = '执行构建脚本';
    const summary = 'npm run build';
    const payloadText = JSON.stringify({ approvalId, sessionId, cmd: 'npm run build' });

    const textHash = sha256HexLower(
      buildApprovalCanonicalTextV1({
        approvalKind: 'command',
        approvalId,
        sessionId,
        title,
        summary,
      }),
    );
    const payloadHash = sha256HexLower(
      buildApprovalCanonicalPayloadV1({
        approvalKind: 'command',
        requestDigest,
        payloadText,
      }),
    );

    const envText = buildApprovalEnvV1(
      deviceId,
      2,
      sessionId,
      'command',
      approvalId,
      textHash,
      payloadHash,
      requestDigest,
      expSec,
    );
    const desktop = await generateDesktopKeyAndSignLowS(envText);

    const validEnvelope = {
      deviceId,
      bindingVersion: 2,
      sessionId,
      approvalKind: 'command',
      approvalId,
      textSummarySha256: textHash,
      payloadSha256: payloadHash,
      allowedActionsCsv: 'allow_once,deny',
      requestDigest,
      expiresAtUnix: expSec,
      sigB64U: desktop.sigB64U,
    };

    await expect(
      verifyApprovalEnvelopeAsync({
        desktopSpkiB64: desktop.spkiB64,
        envelope: validEnvelope,
        expectedDeviceId: deviceId,
        expectedBindingVersion: 2,
        sessionId,
        approvalKind: 'command',
        approvalId,
        requestDigest,
        title,
        summary,
        payloadText,
        nowSec,
      }),
    ).resolves.toBe(true);

    // 篡改摘要标题即失败
    await expect(
      verifyApprovalEnvelopeAsync({
        desktopSpkiB64: desktop.spkiB64,
        envelope: validEnvelope,
        expectedDeviceId: deviceId,
        expectedBindingVersion: 2,
        sessionId,
        approvalKind: 'command',
        approvalId,
        requestDigest,
        title: '无害查看文件',
        summary,
        payloadText,
        nowSec,
      }),
    ).resolves.toBe(false);

    // 过期即失败
    await expect(
      verifyApprovalEnvelopeAsync({
        desktopSpkiB64: desktop.spkiB64,
        envelope: validEnvelope,
        expectedDeviceId: deviceId,
        expectedBindingVersion: 2,
        sessionId,
        approvalKind: 'command',
        approvalId,
        requestDigest,
        title,
        summary,
        payloadText,
        nowSec: expSec + 1,
      }),
    ).resolves.toBe(false);

    // PlanArtifact 信封验证与 session-plan 拒绝
    const markdown = '## 实施步骤\n1. 修改代码\n2. 验证测试';
    const planHash = sha256HexLower(markdown);
    const planVersion = '00000007';
    const planCanonical = buildPlanEnvV1(
      deviceId,
      2,
      sessionId,
      'plan-art-1',
      planVersion,
      planHash,
      'approve,execute',
      true,
      expSec,
    );
    const planSig = await signWithKeyLowS(desktop.keyPair.privateKey, planCanonical);

    const planEnvelope = {
      deviceId,
      bindingVersion: 2,
      sessionId,
      planArtifactId: 'plan-art-1',
      planVersion,
      planHash,
      allowedActionsCsv: 'approve,execute',
      startExecution: true,
      acceptExistingDependencyDebt: false as const,
      approveDependencyUpgrades: [],
      expiresAtUnix: expSec,
      sigB64U: planSig,
    };

    await expect(
      verifyPlanEnvelopeAsync({
        desktopSpkiB64: desktop.spkiB64,
        envelope: planEnvelope,
        expectedDeviceId: deviceId,
        expectedBindingVersion: 2,
        sessionId,
        planArtifactId: 'plan-art-1',
        planVersion,
        planHash,
        markdown,
        action: 'execute',
        startExecution: true,
        nowSec,
      }),
    ).resolves.toBe(true);

    // 篡改 markdown 即拒绝
    await expect(
      verifyPlanEnvelopeAsync({
        desktopSpkiB64: desktop.spkiB64,
        envelope: planEnvelope,
        expectedDeviceId: deviceId,
        expectedBindingVersion: 2,
        sessionId,
        planArtifactId: 'plan-art-1',
        planVersion,
        planHash,
        markdown: `${markdown}\n3. rm -rf /`,
        action: 'execute',
        startExecution: true,
        nowSec,
      }),
    ).resolves.toBe(false);

    // session-plan 明确拒绝
    await expect(
      verifyPlanEnvelopeAsync({
        desktopSpkiB64: desktop.spkiB64,
        envelope: { ...planEnvelope, planArtifactId: 'session-plan' },
        expectedDeviceId: deviceId,
        expectedBindingVersion: 2,
        sessionId,
        planArtifactId: 'session-plan',
        planVersion,
        planHash,
        markdown,
        action: 'execute',
        startExecution: true,
        nowSec,
      }),
    ).resolves.toBe(false);
  });

  it('校验 computeMobileChatOpDigest 与冻结 chatOpV1 摘要完全一致', () => {
    const actual = computeMobileChatOpDigest({
      sessionId: 'sess-001',
      conversationId: 'conv-001',
      conversationMode: 'default',
      uploadIds: ['up-2', 'up-1'],
      message: 'Line 1\nLine 2',
    });
    expect(actual).toBe(vectors.chatOpV1.digestHex64);
  });
});
