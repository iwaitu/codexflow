import {
  base64ToBytes,
  base64UrlToBytes,
  buildApprovalCanonicalPayloadV1,
  buildApprovalCanonicalTextV1,
  buildApprovalEnvV1,
  buildEpochCertV1,
  buildPairAckV1,
  buildPlanEnvV1,
  isLowerHex,
  utf8Encode,
  type SessionEpochCertDto,
} from '@codexflow-shared/remoteProtocol';
import { sha256HexLower } from './incrementalSha256';

export const P256_ORDER_N =
  0xffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551n;
export const P256_HALF_ORDER_N = P256_ORDER_N >> 1n;

function decodeBigEndianBigInt(bytes: Uint8Array): bigint {
  let acc = 0n;
  for (let i = 0; i < bytes.byteLength; i += 1) {
    acc = (acc << 8n) | BigInt(bytes[i]!);
  }
  return acc;
}

function encodeBigEndianBigInt32(value: bigint): Uint8Array {
  const out = new Uint8Array(32);
  let cur = value;
  for (let i = 31; i >= 0; i -= 1) {
    out[i] = Number(cur & 0xffn);
    cur >>= 8n;
  }
  return out;
}

export function isLowSP1363Signature(sigBytes: Uint8Array): boolean {
  if (sigBytes.byteLength !== 64) {
    return false;
  }
  const r = decodeBigEndianBigInt(sigBytes.subarray(0, 32));
  const s = decodeBigEndianBigInt(sigBytes.subarray(32, 64));
  return r >= 1n && r < P256_ORDER_N && s >= 1n && s <= P256_HALF_ORDER_N;
}

export function normalizeP1363LowS(sigBytes: Uint8Array): Uint8Array {
  if (sigBytes.byteLength !== 64) {
    throw new Error('P-256 IEEE P1363 signature must be exactly 64 bytes.');
  }
  const r = decodeBigEndianBigInt(sigBytes.subarray(0, 32));
  const s = decodeBigEndianBigInt(sigBytes.subarray(32, 64));
  if (r < 1n || r >= P256_ORDER_N || s < 1n || s >= P256_ORDER_N) {
    throw new Error('P-256 signature scalar out of range.');
  }
  if (s <= P256_HALF_ORDER_N) {
    return sigBytes.slice();
  }
  const normalized = new Uint8Array(64);
  normalized.set(sigBytes.subarray(0, 32), 0);
  normalized.set(encodeBigEndianBigInt32(P256_ORDER_N - s), 32);
  return normalized;
}

function toArrayBuffer(bytes: Uint8Array): ArrayBuffer {
  const copy = new Uint8Array(bytes.byteLength);
  copy.set(bytes);
  return copy.buffer;
}

export function computeSpkiSha256AndKeyId(spkiB64: string): {
  spkiSha256: string;
  keyId: string;
} {
  const spkiBytes = base64ToBytes(spkiB64.trim());
  if (spkiBytes.byteLength < 64) {
    throw new Error('Invalid P-256 SPKI Base64 length.');
  }
  const spkiSha256 = sha256HexLower(spkiBytes);
  return {
    spkiSha256,
    keyId: spkiSha256.slice(0, 32),
  };
}

export async function verifyP256LowSAsync(
  spkiB64: string,
  utf8Payload: string,
  sigB64U: string,
): Promise<boolean> {
  try {
    const sigBytes = base64UrlToBytes(sigB64U);
    if (!isLowSP1363Signature(sigBytes)) {
      return false;
    }
    const spkiBytes = base64ToBytes(spkiB64.trim());
    const key = await globalThis.crypto.subtle.importKey(
      'spki',
      toArrayBuffer(spkiBytes),
      { name: 'ECDSA', namedCurve: 'P-256' },
      false,
      ['verify'],
    );
    const data = utf8Encode(utf8Payload);
    return await globalThis.crypto.subtle.verify(
      { name: 'ECDSA', hash: 'SHA-256' },
      key,
      toArrayBuffer(sigBytes),
      toArrayBuffer(data),
    );
  } catch {
    return false;
  }
}

export async function verifyPairAckSignatureAsync(params: {
  desktopSpkiB64: string;
  pairingId: string;
  pairRecordSha256: string;
  pairAckV1Text: string;
  pairAckSigB64U: string;
}): Promise<boolean> {
  const lines = params.pairAckV1Text.split('\n');
  if (lines.length !== 4 || !/^\d+$/.test(lines[3] ?? '')) {
    return false;
  }
  const acceptedAtUnix = Number.parseInt(lines[3]!, 10);
  const expectedText = buildPairAckV1(
    params.pairingId,
    params.pairRecordSha256,
    acceptedAtUnix,
  );
  if (params.pairAckV1Text !== expectedText) {
    return false;
  }
  return verifyP256LowSAsync(
    params.desktopSpkiB64,
    params.pairAckV1Text,
    params.pairAckSigB64U,
  );
}

export async function verifyEpochCertAsync(params: {
  desktopSpkiB64: string;
  cert: SessionEpochCertDto;
  expectedDeviceId: string;
  expectedBindingVersion: number;
  nowSec: number;
}): Promise<boolean> {
  const { cert } = params;
  if (
    cert.deviceId.toLowerCase() !== params.expectedDeviceId.toLowerCase() ||
    cert.bindingVersion !== params.expectedBindingVersion ||
    !isLowerHex(cert.sessionEpochHex32, 32) ||
    !Number.isInteger(cert.issuedAtUnix) ||
    !Number.isInteger(cert.expiresAtUnix) ||
    cert.expiresAtUnix <= params.nowSec ||
    cert.expiresAtUnix <= cert.issuedAtUnix ||
    cert.expiresAtUnix - cert.issuedAtUnix > 4 * 3600 + 60
  ) {
    return false;
  }

  let canonical: string;
  try {
    canonical = buildEpochCertV1(
      cert.deviceId,
      cert.bindingVersion,
      cert.sessionEpochHex32,
      cert.issuedAtUnix,
      cert.expiresAtUnix,
    );
  } catch {
    return false;
  }
  return verifyP256LowSAsync(params.desktopSpkiB64, canonical, cert.signatureB64U);
}

export interface SignedApprovalEnvelopeInput {
  readonly deviceId: string;
  readonly bindingVersion: number;
  readonly sessionId: string;
  readonly approvalKind: string;
  readonly approvalId: string;
  readonly textSummarySha256: string;
  readonly payloadSha256: string;
  readonly expiresAtUnix: number;
  readonly sigB64U: string;
}

export async function verifyApprovalEnvelopeAsync(params: {
  desktopSpkiB64: string;
  envelope: SignedApprovalEnvelopeInput;
  expectedDeviceId: string;
  expectedBindingVersion: number;
  sessionId: string;
  approvalKind: string;
  approvalId: string;
  requestDigest: string;
  title: string;
  summary: string;
  payloadText: string;
  nowSec: number;
}): Promise<boolean> {
  const { envelope } = params;
  if (
    envelope.deviceId.toLowerCase() !== params.expectedDeviceId.toLowerCase() ||
    envelope.bindingVersion !== params.expectedBindingVersion ||
    envelope.sessionId !== params.sessionId ||
    envelope.approvalKind !== params.approvalKind ||
    envelope.approvalId !== params.approvalId ||
    !Number.isInteger(envelope.expiresAtUnix) ||
    envelope.expiresAtUnix <= params.nowSec
  ) {
    return false;
  }

  try {
    const expectedText = buildApprovalCanonicalTextV1({
      approvalKind: params.approvalKind,
      approvalId: params.approvalId,
      sessionId: params.sessionId,
      title: params.title,
      summary: params.summary,
    });
    const expectedPayload = buildApprovalCanonicalPayloadV1({
      approvalKind: params.approvalKind,
      requestDigest: params.requestDigest,
      payloadText: params.payloadText,
    });

    if (
      sha256HexLower(expectedText) !== envelope.textSummarySha256.toLowerCase() ||
      sha256HexLower(expectedPayload) !== envelope.payloadSha256.toLowerCase()
    ) {
      return false;
    }

    const envText = buildApprovalEnvV1(
      envelope.deviceId,
      envelope.bindingVersion,
      params.sessionId,
      params.approvalKind,
      params.approvalId,
      envelope.textSummarySha256.toLowerCase(),
      envelope.payloadSha256.toLowerCase(),
      params.requestDigest,
      envelope.expiresAtUnix,
    );
    return await verifyP256LowSAsync(params.desktopSpkiB64, envText, envelope.sigB64U);
  } catch {
    return false;
  }
}

export interface SignedPlanEnvelopeInput {
  readonly deviceId: string;
  readonly bindingVersion: number;
  readonly sessionId: string;
  readonly planArtifactId: string;
  readonly planVersion: string;
  readonly planHash: string;
  readonly allowedActionsCsv: string;
  readonly startExecution: boolean;
  readonly acceptExistingDependencyDebt: false;
  readonly approveDependencyUpgrades: readonly string[];
  readonly expiresAtUnix: number;
  readonly sigB64U: string;
}

export async function verifyPlanEnvelopeAsync(params: {
  desktopSpkiB64: string;
  envelope: SignedPlanEnvelopeInput;
  expectedDeviceId: string;
  expectedBindingVersion: number;
  sessionId: string;
  planArtifactId: string;
  planVersion: string;
  planHash: string;
  markdown: string;
  action: 'approve' | 'execute';
  startExecution: boolean;
  nowSec: number;
}): Promise<boolean> {
  const { envelope } = params;
  if (
    !params.planArtifactId.trim() ||
    params.planArtifactId.trim().toLowerCase() === 'session-plan' ||
    envelope.deviceId.toLowerCase() !== params.expectedDeviceId.toLowerCase() ||
    envelope.bindingVersion !== params.expectedBindingVersion ||
    envelope.sessionId !== params.sessionId ||
    envelope.planArtifactId !== params.planArtifactId ||
    envelope.planVersion !== params.planVersion ||
    envelope.planHash.toLowerCase() !== params.planHash.toLowerCase() ||
    envelope.startExecution !== params.startExecution ||
    envelope.acceptExistingDependencyDebt !== false ||
    !Array.isArray(envelope.approveDependencyUpgrades) ||
    envelope.approveDependencyUpgrades.length !== 0 ||
    !Number.isInteger(envelope.expiresAtUnix) ||
    envelope.expiresAtUnix <= params.nowSec
  ) {
    return false;
  }

  const allowedActions = envelope.allowedActionsCsv
    .split(',')
    .map((s) => s.trim().toLowerCase());
  if (!allowedActions.includes(params.action)) {
    return false;
  }

  if (sha256HexLower(params.markdown) !== params.planHash.toLowerCase()) {
    return false;
  }

  try {
    const canonical = buildPlanEnvV1(
      envelope.deviceId,
      envelope.bindingVersion,
      params.sessionId,
      params.planArtifactId,
      params.planVersion,
      params.planHash.toLowerCase(),
      envelope.allowedActionsCsv,
      params.startExecution,
      envelope.expiresAtUnix,
    );
    return await verifyP256LowSAsync(params.desktopSpkiB64, canonical, envelope.sigB64U);
  } catch {
    return false;
  }
}
