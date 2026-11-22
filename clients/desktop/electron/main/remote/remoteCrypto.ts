import {
  createHash,
  createHmac,
  createPrivateKey,
  createPublicKey,
  createSign,
  createVerify,
  generateKeyPairSync,
  randomBytes,
} from 'node:crypto';
import {
  base64UrlToBytes,
  buildApprovalEnvV1,
  buildBioPopV1,
  buildChatOpV1Payload,
  buildEpochCertV1,
  buildExchangeV1,
  buildPairAckV1,
  buildPairV1,
  buildPlanEnvV1,
  buildRequestSigV1,
  buildSasV1Input,
  bytesToBase64Url,
  isLowerHex,
  sas6FromSha256Digest,
  type KeyPurpose,
  type SessionEpochCertDto,
} from '@codexflow-shared/remoteProtocol';

const P256_ORDER_N =
  0xffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551n;
const P256_HALF_N = P256_ORDER_N >> 1n;

export interface GeneratedP256KeyPair {
  readonly privateKeyPem: string;
  readonly spkiB64: string;
  readonly spkiSha256: string;
  readonly keyId: string;
}

export interface ValidatedSpkiInfo {
  readonly spkiSha256: string;
  readonly keyId: string;
}

function bigIntFromUnsignedBE(bytes: Uint8Array): bigint {
  let acc = 0n;
  for (let i = 0; i < bytes.length; i += 1) {
    acc = (acc << 8n) | BigInt(bytes[i]!);
  }
  return acc;
}

function bigIntToFixed32BE(value: bigint): Buffer {
  const out = Buffer.alloc(32);
  let v = value;
  for (let i = 31; i >= 0; i -= 1) {
    out[i] = Number(v & 0xffn);
    v >>= 8n;
  }
  return out;
}

export function sha256HexLower(data: Uint8Array | string): string {
  const buf = typeof data === 'string' ? Buffer.from(data, 'utf8') : data;
  return createHash('sha256').update(buf).digest('hex').toLowerCase();
}

export function sha256Bytes(data: Uint8Array | string): Buffer {
  const buf = typeof data === 'string' ? Buffer.from(data, 'utf8') : data;
  return createHash('sha256').update(buf).digest();
}

export function hmacSha256HexLower(key: Uint8Array | string, data: Uint8Array | string): string {
  const keyBuf = typeof key === 'string' ? Buffer.from(key, 'utf8') : key;
  const dataBuf = typeof data === 'string' ? Buffer.from(data, 'utf8') : data;
  return createHmac('sha256', keyBuf).update(dataBuf).digest('hex').toLowerCase();
}

export function randomHex32(): string {
  return randomBytes(16).toString('hex').toLowerCase();
}

export function randomHex64(): string {
  return randomBytes(32).toString('hex').toLowerCase();
}

export function tryValidateP256Spki(spkiB64: string): ValidatedSpkiInfo | null {
  if (!spkiB64 || typeof spkiB64 !== 'string') {
    return null;
  }
  try {
    const der = Buffer.from(spkiB64, 'base64');
    if (der.length === 0 || der.toString('base64') !== spkiB64.trim()) {
      return null;
    }
    const pubKey = createPublicKey({ key: der, format: 'der', type: 'spki' });
    if (
      pubKey.asymmetricKeyType !== 'ec' ||
      pubKey.asymmetricKeyDetails?.namedCurve !== 'prime256v1'
    ) {
      return null;
    }
    const spkiSha256 = sha256HexLower(der);
    return {
      spkiSha256,
      keyId: spkiSha256.slice(0, 32),
    };
  } catch {
    return null;
  }
}

export function generateP256KeyPair(): GeneratedP256KeyPair {
  const { publicKey, privateKey } = generateKeyPairSync('ec', {
    namedCurve: 'prime256v1',
    publicKeyEncoding: { type: 'spki', format: 'der' },
    privateKeyEncoding: { type: 'pkcs8', format: 'pem' },
  });
  const spkiB64 = Buffer.from(publicKey).toString('base64');
  const spkiSha256 = sha256HexLower(publicKey);
  return {
    privateKeyPem: privateKey,
    spkiB64,
    spkiSha256,
    keyId: spkiSha256.slice(0, 32),
  };
}

export function signP256LowS(
  privateKeyPem: string,
  payload: Uint8Array | string,
): string {
  const data = typeof payload === 'string' ? Buffer.from(payload, 'utf8') : payload;
  const keyObj = createPrivateKey(privateKeyPem);
  const signer = createSign('SHA256');
  signer.update(data);
  signer.end();

  const rawSig = Buffer.from(
    signer.sign({
      key: keyObj,
      dsaEncoding: 'ieee-p1363',
    }),
  );
  if (rawSig.length !== 64) {
    throw new Error(`Unexpected IEEE P1363 signature length: ${rawSig.length}`);
  }

  const s = bigIntFromUnsignedBE(rawSig.subarray(32, 64));
  if (s > P256_HALF_N) {
    const lowS = bigIntToFixed32BE(P256_ORDER_N - s);
    lowS.copy(rawSig, 32);
  }
  return bytesToBase64Url(rawSig);
}

export function verifyP256LowS(
  spkiB64: string,
  payload: Uint8Array | string,
  sigB64U: string,
): boolean {
  if (!spkiB64 || !sigB64U) {
    return false;
  }

  let sigBytes: Uint8Array;
  try {
    sigBytes = base64UrlToBytes(sigB64U);
  } catch {
    return false;
  }

  if (sigBytes.byteLength !== 64) {
    return false;
  }

  const r = bigIntFromUnsignedBE(sigBytes.subarray(0, 32));
  const s = bigIntFromUnsignedBE(sigBytes.subarray(32, 64));
  // Strictly enforce canonical low-S signatures to neutralize ECDSA malleability.
  if (r <= 0n || r >= P256_ORDER_N || s <= 0n || s > P256_HALF_N) {
    return false;
  }

  if (!tryValidateP256Spki(spkiB64)) {
    return false;
  }

  try {
    const der = Buffer.from(spkiB64, 'base64');
    const pubKey = createPublicKey({ key: der, format: 'der', type: 'spki' });
    const data = typeof payload === 'string' ? Buffer.from(payload, 'utf8') : payload;
    const verifier = createVerify('SHA256');
    verifier.update(data);
    verifier.end();
    return verifier.verify({ key: pubKey, dsaEncoding: 'ieee-p1363' }, sigBytes);
  } catch {
    return false;
  }
}

export function computeSas6Digits(pairRecordV1Text: string): string {
  const input = buildSasV1Input(pairRecordV1Text);
  return sas6FromSha256Digest(sha256Bytes(input));
}

export function computeChatOpDigestV1(params: {
  sessionId: string;
  conversationIdOrEmpty?: string | null;
  conversationMode: string;
  uploadIds: readonly string[];
  canonicalStructuredActionOrEmpty?: string | null;
  message: string;
}): string {
  const payload = buildChatOpV1Payload(
    params.sessionId,
    params.conversationIdOrEmpty,
    params.conversationMode,
    params.uploadIds,
    params.canonicalStructuredActionOrEmpty,
    params.message,
  );
  return sha256HexLower(payload);
}

export function createSignedEpochCert(params: {
  deviceId: string;
  bindingVersion: number;
  privateKeyPem: string;
  sessionEpochHex32?: string;
  issuedAtUnix?: number;
  ttlSeconds?: number;
}): SessionEpochCertDto {
  const sessionEpochHex32 = params.sessionEpochHex32 ?? randomHex32();
  const issuedAtUnix = params.issuedAtUnix ?? Math.floor(Date.now() / 1000);
  const ttlSeconds = params.ttlSeconds ?? 3600;
  const expiresAtUnix = issuedAtUnix + ttlSeconds;

  const text = buildEpochCertV1(
    params.deviceId,
    params.bindingVersion,
    sessionEpochHex32,
    issuedAtUnix,
    expiresAtUnix,
  );
  const signatureB64U = signP256LowS(params.privateKeyPem, text);
  return {
    deviceId: params.deviceId,
    bindingVersion: params.bindingVersion,
    sessionEpochHex32,
    issuedAtUnix,
    expiresAtUnix,
    signatureB64U,
  };
}

export function verifySignedEpochCert(
  desktopSpkiB64: string,
  cert: SessionEpochCertDto,
  nowUnix = Math.floor(Date.now() / 1000),
): boolean {
  if (!cert || cert.expiresAtUnix <= nowUnix || cert.expiresAtUnix <= cert.issuedAtUnix) {
    return false;
  }
  try {
    const text = buildEpochCertV1(
      cert.deviceId,
      cert.bindingVersion,
      cert.sessionEpochHex32,
      cert.issuedAtUnix,
      cert.expiresAtUnix,
    );
    return verifyP256LowS(desktopSpkiB64, text, cert.signatureB64U);
  } catch {
    return false;
  }
}

export function createSignedPairAck(params: {
  pairingId: string;
  pairRecordV1Text: string;
  acceptedAtUnix?: number;
  desktopPrivateKeyPem: string;
}): { pairAckV1Text: string; pairAckSigB64U: string } {
  const pairRecordSha256 = sha256HexLower(params.pairRecordV1Text);
  const acceptedAtUnix = params.acceptedAtUnix ?? Math.floor(Date.now() / 1000);
  const pairAckV1Text = buildPairAckV1(params.pairingId, pairRecordSha256, acceptedAtUnix);
  const pairAckSigB64U = signP256LowS(params.desktopPrivateKeyPem, pairAckV1Text);
  return { pairAckV1Text, pairAckSigB64U };
}

export function verifyRequestSigV1(params: {
  spkiB64: string;
  deviceId: string;
  bindingVersion: number;
  sessionEpochHex32: string;
  mobileId: string;
  keyId: string;
  keyPurpose: KeyPurpose;
  methodUpper: string;
  canonicalPath: string;
  canonicalQuery: string;
  ifNoneMatchOrEmpty: string;
  uploadIdOrEmpty: string;
  fileSha256OrEmpty: string;
  clientOpIdOrEmpty: string;
  canonicalContentType: string;
  declaredContentLength: number;
  bodySha256LowerHex: string;
  unixTimestampSeconds: number;
  nonceHex32: string;
  sigB64U: string;
}): boolean {
  const spkiInfo = tryValidateP256Spki(params.spkiB64);
  if (!spkiInfo || spkiInfo.keyId !== params.keyId) {
    return false;
  }
  try {
    const text = buildRequestSigV1(params);
    return verifyP256LowS(params.spkiB64, text, params.sigB64U);
  } catch {
    return false;
  }
}

export function verifyApprovalEnvelopeV1(params: {
  desktopSpkiB64: string;
  deviceId: string;
  bindingVersion: number;
  sessionId: string;
  approvalKind: string;
  approvalId: string;
  textSummarySha256: string;
  payloadSha256: string;
  requestDigest: string;
  expiresAtUnix: number;
  sigB64U: string;
  nowUnix?: number;
}): boolean {
  const nowUnix = params.nowUnix ?? Math.floor(Date.now() / 1000);
  if (params.expiresAtUnix <= nowUnix) {
    return false;
  }
  try {
    const text = buildApprovalEnvV1(
      params.deviceId,
      params.bindingVersion,
      params.sessionId,
      params.approvalKind,
      params.approvalId,
      params.textSummarySha256,
      params.payloadSha256,
      params.requestDigest,
      params.expiresAtUnix,
    );
    return verifyP256LowS(params.desktopSpkiB64, text, params.sigB64U);
  } catch {
    return false;
  }
}

export function verifyPlanEnvelopeV1(params: {
  desktopSpkiB64: string;
  deviceId: string;
  bindingVersion: number;
  sessionId: string;
  planArtifactId: string;
  planVersion: string | number;
  planMarkdownSha256: string;
  allowedActionsCsv: string;
  startExecution: boolean;
  expiresAtUnix: number;
  sigB64U: string;
  nowUnix?: number;
}): boolean {
  const nowUnix = params.nowUnix ?? Math.floor(Date.now() / 1000);
  if (params.expiresAtUnix <= nowUnix) {
    return false;
  }
  try {
    const text = buildPlanEnvV1(
      params.deviceId,
      params.bindingVersion,
      params.sessionId,
      params.planArtifactId,
      params.planVersion,
      params.planMarkdownSha256,
      params.allowedActionsCsv,
      params.startExecution,
      params.expiresAtUnix,
    );
    return verifyP256LowS(params.desktopSpkiB64, text, params.sigB64U);
  } catch {
    return false;
  }
}

export interface SignedApprovalEnvelopeDto {
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

export function createSignedApprovalEnvelopeV1(params: {
  desktopPrivateKeyPem: string;
  deviceId: string;
  bindingVersion: number;
  sessionId: string;
  approvalKind: string;
  approvalId: string;
  textSummarySha256: string;
  payloadSha256: string;
  requestDigest: string;
  expiresAtUnix: number;
}): SignedApprovalEnvelopeDto {
  const text = buildApprovalEnvV1(
    params.deviceId,
    params.bindingVersion,
    params.sessionId,
    params.approvalKind,
    params.approvalId,
    params.textSummarySha256,
    params.payloadSha256,
    params.requestDigest,
    params.expiresAtUnix,
  );
  const sigB64U = signP256LowS(params.desktopPrivateKeyPem, text);
  return {
    deviceId: params.deviceId,
    bindingVersion: params.bindingVersion,
    sessionId: params.sessionId,
    approvalKind: params.approvalKind,
    approvalId: params.approvalId,
    textSummarySha256: params.textSummarySha256,
    payloadSha256: params.payloadSha256,
    expiresAtUnix: params.expiresAtUnix,
    sigB64U,
  };
}

export interface SignedPlanEnvelopeDto {
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

export function createSignedPlanEnvelopeV1(params: {
  desktopPrivateKeyPem: string;
  deviceId: string;
  bindingVersion: number;
  sessionId: string;
  planArtifactId: string;
  planVersion: string;
  planMarkdownSha256: string;
  allowedActionsCsv: string;
  startExecution: boolean;
  expiresAtUnix: number;
}): SignedPlanEnvelopeDto {
  const text = buildPlanEnvV1(
    params.deviceId,
    params.bindingVersion,
    params.sessionId,
    params.planArtifactId,
    params.planVersion,
    params.planMarkdownSha256,
    params.allowedActionsCsv,
    params.startExecution,
    params.expiresAtUnix,
  );
  const sigB64U = signP256LowS(params.desktopPrivateKeyPem, text);
  return {
    deviceId: params.deviceId,
    bindingVersion: params.bindingVersion,
    sessionId: params.sessionId,
    planArtifactId: params.planArtifactId,
    planVersion: params.planVersion,
    planHash: params.planMarkdownSha256,
    allowedActionsCsv: params.allowedActionsCsv,
    startExecution: params.startExecution,
    acceptExistingDependencyDebt: false,
    approveDependencyUpgrades: [],
    expiresAtUnix: params.expiresAtUnix,
    sigB64U,
  };
}

export function verifyExchangeSigV1(params: {
  sessionSpkiB64: string;
  pairingId: string;
  mobileId: string;
  nonceHex32: string;
  unixTimestamp: number;
  sigB64U: string;
}): boolean {
  try {
    const text = buildExchangeV1(
      params.pairingId,
      params.mobileId,
      params.nonceHex32,
      params.unixTimestamp,
    );
    return verifyP256LowS(params.sessionSpkiB64, text, params.sigB64U);
  } catch {
    return false;
  }
}

export function verifyPairRequestCryptographicIntegrity(params: {
  expectedTicketId?: string;
  deviceId: string;
  bindingVersion: number;
  desktopKeyId: string;
  desktopSpkiSha256: string;
  mobileId: string;
  sessionSpkiB64: string;
  bioSpkiB64?: string | null;
  pairRecordV1Text: string;
  pairRecordSigB64U: string;
  bioPopV1Text?: string | null;
  bioPopSigB64U?: string | null;
  pairingNonceHex32: string;
  expiresAtUnix: number;
  nowUnix?: number;
}): { ok: true; sessionKeyId: string; sessionSpkiSha256: string; bioKeyId: string | null; bioSpkiSha256: string | null } | { ok: false; reason: string } {
  const nowUnix = params.nowUnix ?? Math.floor(Date.now() / 1000);
  if (params.expiresAtUnix <= nowUnix) {
    return { ok: false, reason: 'pair_request_expired' };
  }

  const sessInfo = tryValidateP256Spki(params.sessionSpkiB64);
  if (!sessInfo) {
    return { ok: false, reason: 'invalid_session_spki' };
  }

  let bioInfo: ValidatedSpkiInfo | null = null;
  if (params.bioSpkiB64) {
    bioInfo = tryValidateP256Spki(params.bioSpkiB64);
    if (!bioInfo) {
      return { ok: false, reason: 'invalid_bio_spki' };
    }
  }

  const lines = params.pairRecordV1Text.split('\n');
  if (lines.length !== 13 || lines[0] !== 'CF-PAIR-V1') {
    return { ok: false, reason: 'malformed_pair_v1_lines' };
  }
  const ticketId = lines[1]!;
  if (params.expectedTicketId && ticketId !== params.expectedTicketId) {
    return { ok: false, reason: 'ticket_id_mismatch' };
  }
  if (!isLowerHex(params.pairingNonceHex32, 32)) {
    return { ok: false, reason: 'invalid_pairing_nonce' };
  }

  const reconstructed = buildPairV1(
    ticketId,
    params.deviceId,
    params.bindingVersion,
    params.mobileId,
    params.desktopKeyId,
    params.desktopSpkiSha256,
    sessInfo.keyId,
    sessInfo.spkiSha256,
    bioInfo?.keyId ?? null,
    bioInfo?.spkiSha256 ?? null,
    params.pairingNonceHex32,
    params.expiresAtUnix,
  );
  if (reconstructed !== params.pairRecordV1Text) {
    return { ok: false, reason: 'pair_v1_field_mismatch' };
  }

  if (!verifyP256LowS(params.sessionSpkiB64, params.pairRecordV1Text, params.pairRecordSigB64U)) {
    return { ok: false, reason: 'invalid_pair_v1_signature' };
  }

  if (bioInfo) {
    if (!params.bioPopV1Text || !params.bioPopSigB64U) {
      return { ok: false, reason: 'missing_bio_pop' };
    }
    const expectedPop = buildBioPopV1(
      params.mobileId,
      ticketId,
      bioInfo.keyId,
      params.pairingNonceHex32,
      params.expiresAtUnix,
    );
    if (expectedPop !== params.bioPopV1Text) {
      return { ok: false, reason: 'bio_pop_field_mismatch' };
    }
    if (!verifyP256LowS(params.bioSpkiB64!, params.bioPopV1Text, params.bioPopSigB64U)) {
      return { ok: false, reason: 'invalid_bio_pop_signature' };
    }
  }

  return {
    ok: true,
    sessionKeyId: sessInfo.keyId,
    sessionSpkiSha256: sessInfo.spkiSha256,
    bioKeyId: bioInfo?.keyId ?? null,
    bioSpkiSha256: bioInfo?.spkiSha256 ?? null,
  };
}
