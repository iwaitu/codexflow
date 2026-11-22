// Cross-platform zero-dependency protocol codecs and signature domain builders matching
// CodexFlow.Portal.Protocol (TunnelFrames.cs & SignatureDomains.cs) and crypto-protocol-vectors.v1.json.

export const TUNNEL_PROTOCOL_VERSION = 0x01;
export const TUNNEL_HEADER_SIZE = 16;
export const TUNNEL_MAX_PAYLOAD_BYTES = 65536;
export const EMPTY_BODY_SHA256_HEX =
  'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855';

export enum TunnelFrameType {
  Hello = 0x01,
  HelloAck = 0x02,
  Ping = 0x03,
  Pong = 0x04,
  ReqStart = 0x05,
  ReqBody = 0x06,
  ResHead = 0x07,
  ResChunk = 0x08,
  WindowUpdate = 0x09,
  StreamRst = 0x0a,
  HostState = 0x0b,
  HostEvent = 0x0c,
  PairRequest = 0x0d,
  RevokeNotice = 0x0e,
}

export enum TunnelFrameFlags {
  None = 0x00,
  Fin = 0x01,
  Priority = 0x02,
}

export enum StreamRstErrorCode {
  Cancel = 0x01,
  FlowControlError = 0x02,
  Timeout = 0x03,
  ProtocolError = 0x04,
  Revoked = 0x05,
}

export interface TunnelFrameHeader {
  readonly version: number;
  readonly frameType: TunnelFrameType;
  readonly flags: number;
  readonly reserved: number;
  readonly connEpoch: number;
  readonly streamId: number;
  readonly payloadLength: number;
}

export interface TunnelFrame {
  readonly header: TunnelFrameHeader;
  readonly payload: Uint8Array;
  readonly isFin: boolean;
  readonly isPriority: boolean;
}

export interface HelloPayload {
  readonly deviceId: string;
  readonly hostInstanceId: string;
  readonly bindingVersion: number;
  readonly lastAckRevocationSeq: number;
}

export interface HelloAckPayload {
  readonly connEpoch: number;
  readonly latestAccountRevSeq: number;
  readonly latestDeviceRevSeq: number;
  readonly activePairings: string[];
  readonly revokedMobileIds: string[];
}

export interface TunnelHttpHeader {
  readonly name: string;
  readonly value: string;
}

export interface ReqStartPayload {
  readonly method: string;
  readonly path: string;
  readonly query: string;
  readonly headers: TunnelHttpHeader[];
}

export interface ResHeadPayload {
  readonly statusCode: number;
  readonly headers: TunnelHttpHeader[];
}

export interface SessionEpochCertDto {
  readonly deviceId: string;
  readonly bindingVersion: number;
  readonly sessionEpochHex32: string;
  readonly issuedAtUnix: number;
  readonly expiresAtUnix: number;
  readonly signatureB64U: string;
}

export interface PairAckDto {
  readonly pairingId: string;
  readonly decision: 'approve' | 'deny';
  readonly pairAckV1Text?: string | null;
  readonly pairAckSigB64U?: string | null;
}

export interface HostStatePayload {
  readonly state: string;
  readonly resetGen?: number | null;
  readonly sessionEpochCert?: SessionEpochCertDto | null;
  readonly pairAck?: PairAckDto | null;
}

export interface HostEventPayload {
  readonly eventId: number;
  readonly summaryEpoch: string;
  readonly bindingVersion: number;
  readonly eventKind: string;
  readonly targetKind: string;
  readonly opaqueRefId: string;
  readonly occurredAtUtc: string;
  readonly expiresAtUtc: string;
  readonly isTerminal: boolean;
}

export interface PairRequestPayload {
  readonly ticketId: string;
  readonly pairingId: string;
  readonly mobileId: string;
  readonly platform: string;
  readonly deviceName: string;
  readonly bindingVersion: number;
  readonly sessionSpkiB64: string;
  readonly bioSpkiB64?: string | null;
  readonly pairRecordV1Text: string;
  readonly pairRecordSigB64U: string;
  readonly bioPopV1Text?: string | null;
  readonly bioPopSigB64U?: string | null;
  readonly hmacHex: string;
  readonly expiresAtUnix: number;
}

export interface RevokeNoticePayload {
  readonly targetRevSeq: number;
  readonly scope: string;
  readonly targetIds: string[];
}

export type KeyPurpose = 'device_session' | 'biometric_approval';

const utf8Encoder = new TextEncoder();
const utf8Decoder = new TextDecoder('utf-8', { fatal: true });

export function utf8Encode(text: string): Uint8Array {
  return utf8Encoder.encode(text);
}

export function utf8Decode(bytes: Uint8Array): string {
  return utf8Decoder.decode(bytes);
}

export function bytesToHexLower(bytes: Uint8Array): string {
  let out = '';
  for (let i = 0; i < bytes.length; i += 1) {
    const b = bytes[i]!;
    out += b.toString(16).padStart(2, '0');
  }
  return out;
}

export function hexToBytes(hex: string): Uint8Array {
  if (hex.length % 2 !== 0 || !/^[0-9a-fA-F]*$/.test(hex)) {
    throw new Error('Invalid hex string.');
  }
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i += 1) {
    out[i] = Number.parseInt(hex.slice(i * 2, i * 2 + 2), 16);
  }
  return out;
}

export function isLowerHex(value: unknown, expectedLength: number): value is string {
  if (typeof value !== 'string' || value.length !== expectedLength) {
    return false;
  }
  for (let i = 0; i < value.length; i += 1) {
    const c = value.charCodeAt(i);
    const isDigit = c >= 48 && c <= 57;
    const isLowerAlpha = c >= 97 && c <= 102;
    if (!isDigit && !isLowerAlpha) {
      return false;
    }
  }
  return true;
}

export function bytesToBase64(bytes: Uint8Array): string {
  let binary = '';
  const chunkSize = 0x8000;
  for (let i = 0; i < bytes.length; i += chunkSize) {
    const sub = bytes.subarray(i, Math.min(i + chunkSize, bytes.length));
    binary += String.fromCharCode(...sub);
  }
  return btoa(binary);
}

export function base64ToBytes(b64: string): Uint8Array {
  const binary = atob(b64);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) {
    out[i] = binary.charCodeAt(i);
  }
  return out;
}

export function bytesToBase64Url(bytes: Uint8Array): string {
  return bytesToBase64(bytes).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
}

export function base64UrlToBytes(b64u: string): Uint8Array {
  if (!b64u || b64u.includes('+') || b64u.includes('/') || b64u.includes('=')) {
    throw new Error('Invalid unpadded Base64URL string.');
  }
  const mod = b64u.length % 4;
  if (mod === 1) {
    throw new Error('Invalid Base64URL length.');
  }
  const padded = mod === 0 ? b64u : b64u + '='.repeat(4 - mod);
  const standard = padded.replace(/-/g, '+').replace(/_/g, '/');
  return base64ToBytes(standard);
}

export function isControlFrame(frameType: TunnelFrameType): boolean {
  switch (frameType) {
    case TunnelFrameType.Hello:
    case TunnelFrameType.HelloAck:
    case TunnelFrameType.Ping:
    case TunnelFrameType.Pong:
    case TunnelFrameType.HostState:
    case TunnelFrameType.HostEvent:
    case TunnelFrameType.PairRequest:
    case TunnelFrameType.RevokeNotice:
      return true;
    default:
      return false;
  }
}

function isKnownFrameType(value: number): value is TunnelFrameType {
  return value >= TunnelFrameType.Hello && value <= TunnelFrameType.RevokeNotice;
}

function validateFrameSemantics(
  version: number,
  frameType: number,
  flags: number,
  reserved: number,
  streamId: number,
  payloadLen: number,
): string | null {
  if (version !== TUNNEL_PROTOCOL_VERSION) {
    return `Unsupported protocol version: 0x${version.toString(16)}.`;
  }
  if (!isKnownFrameType(frameType)) {
    return `Unknown frame type: 0x${frameType.toString(16)}.`;
  }
  if ((flags & ~0x03) !== 0) {
    return `Unknown frame flags: 0x${flags.toString(16)}.`;
  }
  if (reserved !== 0) {
    return `Reserved header byte must be zero, got 0x${reserved.toString(16)}.`;
  }
  if (!Number.isInteger(payloadLen) || payloadLen < 0 || payloadLen > TUNNEL_MAX_PAYLOAD_BYTES) {
    return `PayloadLen ${payloadLen} exceeds ${TUNNEL_MAX_PAYLOAD_BYTES} bytes.`;
  }
  if (!Number.isInteger(streamId) || streamId < 0 || streamId > 0xffffffff) {
    return `Invalid streamId: ${streamId}.`;
  }
  const control = isControlFrame(frameType);
  if (control && streamId !== 0) {
    return `Control frame 0x${frameType.toString(16)} requires StreamId=0, got ${streamId}.`;
  }
  if (!control && streamId === 0) {
    return `Stream frame 0x${frameType.toString(16)} requires StreamId>=1.`;
  }
  return null;
}

export function encodeFrame(
  frameType: TunnelFrameType,
  flags: number,
  connEpoch: number,
  streamId: number,
  payload: Uint8Array = new Uint8Array(0),
): Uint8Array {
  const err = validateFrameSemantics(
    TUNNEL_PROTOCOL_VERSION,
    frameType,
    flags,
    0,
    streamId,
    payload.byteLength,
  );
  if (err) {
    throw new Error(err);
  }
  if (!Number.isInteger(connEpoch) || connEpoch < 0 || connEpoch > 0xffffffff) {
    throw new Error(`Invalid connEpoch: ${connEpoch}.`);
  }

  const out = new Uint8Array(TUNNEL_HEADER_SIZE + payload.byteLength);
  const view = new DataView(out.buffer, out.byteOffset, out.byteLength);
  out[0] = TUNNEL_PROTOCOL_VERSION;
  out[1] = frameType;
  out[2] = flags;
  out[3] = 0x00;
  view.setUint32(4, connEpoch, false);
  view.setUint32(8, streamId, false);
  view.setUint32(12, payload.byteLength, false);
  out.set(payload, TUNNEL_HEADER_SIZE);
  return out;
}

export function encodeJsonFrame<T>(
  frameType: TunnelFrameType,
  flags: number,
  connEpoch: number,
  streamId: number,
  payload: T,
): Uint8Array {
  const jsonBytes = utf8Encode(JSON.stringify(payload));
  return encodeFrame(frameType, flags, connEpoch, streamId, jsonBytes);
}

export function encodePingOrPong(isPong: boolean, connEpoch: number, utcMillis: number): Uint8Array {
  const payload = new Uint8Array(8);
  const view = new DataView(payload.buffer);
  view.setBigInt64(0, BigInt(Math.trunc(utcMillis)), false);
  return encodeFrame(
    isPong ? TunnelFrameType.Pong : TunnelFrameType.Ping,
    TunnelFrameFlags.Priority,
    connEpoch,
    0,
    payload,
  );
}

export function encodeWindowUpdate(
  connEpoch: number,
  streamId: number,
  deltaBytes: number,
  deltaChunks: number,
): Uint8Array {
  if (
    !Number.isInteger(deltaBytes) ||
    !Number.isInteger(deltaChunks) ||
    deltaBytes < 0 ||
    deltaChunks < 0 ||
    (deltaBytes === 0 && deltaChunks === 0)
  ) {
    throw new Error('At least one of deltaBytes or deltaChunks must be >= 1.');
  }
  const payload = new Uint8Array(8);
  const view = new DataView(payload.buffer);
  view.setUint32(0, deltaBytes, false);
  view.setUint32(4, deltaChunks, false);
  return encodeFrame(TunnelFrameType.WindowUpdate, TunnelFrameFlags.Priority, connEpoch, streamId, payload);
}

export function encodeStreamRst(
  connEpoch: number,
  streamId: number,
  errorCode: StreamRstErrorCode,
  reason?: string,
): Uint8Array {
  let reasonBytes = reason ? utf8Encode(reason) : new Uint8Array(0);
  const maxReason = TUNNEL_MAX_PAYLOAD_BYTES - 2;
  if (reasonBytes.byteLength > maxReason) {
    reasonBytes = reasonBytes.subarray(0, maxReason);
  }
  const payload = new Uint8Array(2 + reasonBytes.byteLength);
  const view = new DataView(payload.buffer);
  view.setUint16(0, errorCode, false);
  payload.set(reasonBytes, 2);
  return encodeFrame(TunnelFrameType.StreamRst, TunnelFrameFlags.Priority, connEpoch, streamId, payload);
}

export function tryDecodeWindowUpdate(
  payload: Uint8Array,
): { ok: true; deltaBytes: number; deltaChunks: number } | { ok: false } {
  if (payload.byteLength !== 8) {
    return { ok: false };
  }
  const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  const deltaBytes = view.getUint32(0, false);
  const deltaChunks = view.getUint32(4, false);
  if (deltaBytes < 1 && deltaChunks < 1) {
    return { ok: false };
  }
  return { ok: true, deltaBytes, deltaChunks };
}

export function tryDecodeStreamRst(
  payload: Uint8Array,
): { ok: true; errorCode: StreamRstErrorCode; reason: string } | { ok: false } {
  if (payload.byteLength < 2) {
    return { ok: false };
  }
  const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  const errorCode = view.getUint16(0, false) as StreamRstErrorCode;
  const reason = payload.byteLength > 2 ? utf8Decode(payload.subarray(2)) : '';
  return { ok: true, errorCode, reason };
}

export function tryDecodePingOrPong(payload: Uint8Array): number | null {
  if (payload.byteLength !== 8) {
    return null;
  }
  const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  return Number(view.getBigInt64(0, false));
}

export function tryDecodeFrame(
  rawFrame: Uint8Array,
): { ok: true; frame: TunnelFrame } | { ok: false; error: string } {
  if (rawFrame.byteLength < TUNNEL_HEADER_SIZE) {
    return { ok: false, error: 'Frame shorter than 16 bytes.' };
  }

  const view = new DataView(rawFrame.buffer, rawFrame.byteOffset, rawFrame.byteLength);
  const version = rawFrame[0]!;
  const frameType = rawFrame[1]!;
  const flags = rawFrame[2]!;
  const reserved = rawFrame[3]!;
  const connEpoch = view.getUint32(4, false);
  const streamId = view.getUint32(8, false);
  const payloadLength = view.getUint32(12, false);

  const semanticErr = validateFrameSemantics(
    version,
    frameType,
    flags,
    reserved,
    streamId,
    payloadLength,
  );
  if (semanticErr) {
    return { ok: false, error: semanticErr };
  }

  if (rawFrame.byteLength !== TUNNEL_HEADER_SIZE + payloadLength) {
    return {
      ok: false,
      error: `Exact single frame length mismatch: expected ${TUNNEL_HEADER_SIZE + payloadLength}, got ${rawFrame.byteLength}.`,
    };
  }

  const payload = rawFrame.slice(TUNNEL_HEADER_SIZE, TUNNEL_HEADER_SIZE + payloadLength);
  switch (frameType) {
    case TunnelFrameType.Ping:
    case TunnelFrameType.Pong:
      if (payload.byteLength !== 8) {
        return { ok: false, error: `Frame 0x${frameType.toString(16)} payload must be 8 bytes.` };
      }
      break;
    case TunnelFrameType.WindowUpdate:
      if (!tryDecodeWindowUpdate(payload).ok) {
        return {
          ok: false,
          error: 'WINDOW_UPDATE must be 8 bytes with deltaBytes>=1 or deltaChunks>=1.',
        };
      }
      break;
    case TunnelFrameType.StreamRst:
      if (!tryDecodeStreamRst(payload).ok) {
        return { ok: false, error: 'STREAM_RST must contain at least 2 bytes for errorCode.' };
      }
      break;
  }

  const header: TunnelFrameHeader = {
    version,
    frameType: frameType as TunnelFrameType,
    flags,
    reserved,
    connEpoch,
    streamId,
    payloadLength,
  };

  return {
    ok: true,
    frame: {
      header,
      payload,
      isFin: (flags & TunnelFrameFlags.Fin) !== 0,
      isPriority: (flags & TunnelFrameFlags.Priority) !== 0,
    },
  };
}

export function deserializeJsonPayload<T>(payload: Uint8Array): T {
  const text = utf8Decode(payload);
  return JSON.parse(text) as T;
}

function ensureSingleLineField(value: string, fieldName: string): string {
  if (typeof value !== 'string') {
    throw new Error(`Field '${fieldName}' must be a string.`);
  }
  if (value.includes('\r') || value.includes('\n') || value.includes('\0')) {
    throw new Error(`Field '${fieldName}' contains forbidden CR/LF/NUL characters.`);
  }
  return value;
}

function ensureNonEmptySingleLineField(value: string, fieldName: string): string {
  ensureSingleLineField(value, fieldName);
  if (value.length === 0) {
    throw new Error(`Field '${fieldName}' must not be empty.`);
  }
  return value;
}

function ensureLowerHex32(value: string, fieldName: string): string {
  if (!isLowerHex(value, 32)) {
    throw new Error(`Field '${fieldName}' must be 32 lowercase hex characters.`);
  }
  return value;
}

function ensureLowerHex64(value: string, fieldName: string): string {
  if (!isLowerHex(value, 64)) {
    throw new Error(`Field '${fieldName}' must be 64 lowercase hex characters.`);
  }
  return value;
}

function ensureInteger(value: number, fieldName: string): string {
  if (!Number.isInteger(value)) {
    throw new Error(`Field '${fieldName}' must be an integer.`);
  }
  return String(value);
}

function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

export function buildPairV1(
  ticketId: string,
  deviceId: string,
  bindingVersion: number,
  mobileId: string,
  desktopKeyId: string,
  desktopSpkiSha256: string,
  sessionKeyId: string,
  sessionSpkiSha256: string,
  bioKeyId: string | null | undefined,
  bioSpkiSha256: string | null | undefined,
  pairingNonceHex32: string,
  expiresAtUnix: number,
): string {
  const bioKey = bioKeyId ?? '';
  const bioHash = bioSpkiSha256 ?? '';
  return [
    'CF-PAIR-V1',
    ensureNonEmptySingleLineField(ticketId, 'ticketId'),
    ensureNonEmptySingleLineField(deviceId, 'deviceId'),
    ensureInteger(bindingVersion, 'bindingVersion'),
    ensureNonEmptySingleLineField(mobileId, 'mobileId'),
    ensureSingleLineField(desktopKeyId, 'desktopKeyId'),
    ensureLowerHex64(desktopSpkiSha256, 'desktopSpkiSha256'),
    ensureSingleLineField(sessionKeyId, 'sessionKeyId'),
    ensureLowerHex64(sessionSpkiSha256, 'sessionSpkiSha256'),
    ensureSingleLineField(bioKey, 'bioKeyId'),
    bioHash.length === 0 ? '' : ensureLowerHex64(bioHash, 'bioSpkiSha256'),
    ensureLowerHex32(pairingNonceHex32, 'pairingNonceHex32'),
    ensureInteger(expiresAtUnix, 'expiresAtUnix'),
  ].join('\n');
}

export function buildBioPopV1(
  mobileId: string,
  ticketId: string,
  bioKeyId: string,
  pairingNonceHex32: string,
  expiresAtUnix: number,
): string {
  return [
    'CF-BIO-POP-V1',
    ensureNonEmptySingleLineField(mobileId, 'mobileId'),
    ensureNonEmptySingleLineField(ticketId, 'ticketId'),
    ensureNonEmptySingleLineField(bioKeyId, 'bioKeyId'),
    ensureLowerHex32(pairingNonceHex32, 'pairingNonceHex32'),
    ensureInteger(expiresAtUnix, 'expiresAtUnix'),
  ].join('\n');
}

export function buildPairAckV1(
  pairingId: string,
  pairRecordSha256LowerHex: string,
  acceptedAtUnix: number,
): string {
  return [
    'CF-PAIR-ACK-V1',
    ensureNonEmptySingleLineField(pairingId, 'pairingId'),
    ensureLowerHex64(pairRecordSha256LowerHex, 'pairRecordSha256LowerHex'),
    ensureInteger(acceptedAtUnix, 'acceptedAtUnix'),
  ].join('\n');
}

export function buildExchangeV1(
  pairingId: string,
  mobileId: string,
  nonceHex32: string,
  unixTimestamp: number,
): string {
  return [
    'CF-EXCHANGE-V1',
    ensureNonEmptySingleLineField(pairingId, 'pairingId'),
    ensureNonEmptySingleLineField(mobileId, 'mobileId'),
    ensureLowerHex32(nonceHex32, 'nonceHex32'),
    ensureInteger(unixTimestamp, 'unixTimestamp'),
  ].join('\n');
}

export function buildEpochCertV1(
  deviceId: string,
  bindingVersion: number,
  sessionEpochHex32: string,
  issuedAtUnix: number,
  expiresAtUnix: number,
): string {
  return [
    'CF-EPOCH-CERT-V1',
    ensureNonEmptySingleLineField(deviceId, 'deviceId'),
    ensureInteger(bindingVersion, 'bindingVersion'),
    ensureLowerHex32(sessionEpochHex32, 'sessionEpochHex32'),
    ensureInteger(issuedAtUnix, 'issuedAtUnix'),
    ensureInteger(expiresAtUnix, 'expiresAtUnix'),
  ].join('\n');
}

export function buildApprovalEnvV1(
  deviceId: string,
  bindingVersion: number,
  sessionId: string,
  approvalKind: string,
  approvalId: string,
  textSummarySha256: string,
  payloadSha256: string,
  requestDigest: string,
  expiresAtUnix: number,
): string {
  return [
    'CF-APPROVAL-ENV-V1',
    ensureNonEmptySingleLineField(deviceId, 'deviceId'),
    ensureInteger(bindingVersion, 'bindingVersion'),
    ensureNonEmptySingleLineField(sessionId, 'sessionId'),
    ensureNonEmptySingleLineField(approvalKind, 'approvalKind'),
    ensureNonEmptySingleLineField(approvalId, 'approvalId'),
    ensureLowerHex64(textSummarySha256, 'textSummarySha256'),
    ensureLowerHex64(payloadSha256, 'payloadSha256'),
    'allow_once,deny',
    ensureNonEmptySingleLineField(requestDigest, 'requestDigest'),
    ensureInteger(expiresAtUnix, 'expiresAtUnix'),
  ].join('\n');
}

export function buildPlanEnvV1(
  deviceId: string,
  bindingVersion: number,
  sessionId: string,
  planArtifactId: string,
  planVersion: string | number,
  planMarkdownSha256: string,
  allowedActionsCsv: string,
  startExecution: boolean,
  expiresAtUnix: number,
): string {
  const versionText =
    typeof planVersion === 'number'
      ? ensureInteger(planVersion, 'planVersion')
      : ensureNonEmptySingleLineField(planVersion, 'planVersion');

  return [
    'CF-PLAN-ENV-V1',
    ensureNonEmptySingleLineField(deviceId, 'deviceId'),
    ensureInteger(bindingVersion, 'bindingVersion'),
    ensureNonEmptySingleLineField(sessionId, 'sessionId'),
    ensureNonEmptySingleLineField(planArtifactId, 'planArtifactId'),
    versionText,
    ensureLowerHex64(planMarkdownSha256, 'planMarkdownSha256'),
    ensureNonEmptySingleLineField(allowedActionsCsv, 'allowedActionsCsv'),
    startExecution ? 'true' : 'false',
    'false',
    '[]',
    ensureInteger(expiresAtUnix, 'expiresAtUnix'),
  ].join('\n');
}

export function sanitizeSingleLineSummaryText(raw: string, maxLen = 512): string {
  return raw.replace(/[\r\n\0]+/g, ' ').trim().slice(0, maxLen);
}

export function buildApprovalCanonicalTextV1(params: {
  approvalKind: string;
  approvalId: string;
  sessionId: string;
  title: string;
  summary: string;
}): string {
  return [
    'CF-APPROVAL-TEXT-V1',
    ensureNonEmptySingleLineField(params.approvalKind, 'approvalKind'),
    ensureNonEmptySingleLineField(params.approvalId, 'approvalId'),
    ensureNonEmptySingleLineField(params.sessionId, 'sessionId'),
    sanitizeSingleLineSummaryText(params.title, 200) || params.approvalKind,
    sanitizeSingleLineSummaryText(params.summary, 600) || params.approvalId,
  ].join('\n');
}

export function buildApprovalCanonicalPayloadV1(params: {
  approvalKind: string;
  requestDigest: string;
  payloadText: string;
}): string {
  return [
    'CF-APPROVAL-PAYLOAD-V1',
    ensureNonEmptySingleLineField(params.approvalKind, 'approvalKind'),
    ensureNonEmptySingleLineField(params.requestDigest, 'requestDigest'),
    sanitizeSingleLineSummaryText(params.payloadText, 4096),
  ].join('\n');
}

export function buildRequestSigV1(params: {
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
}): string {
  if (params.keyPurpose !== 'device_session' && params.keyPurpose !== 'biometric_approval') {
    throw new Error("keyPurpose must be 'device_session' or 'biometric_approval'.");
  }
  if (!Number.isInteger(params.declaredContentLength) || params.declaredContentLength < 0) {
    throw new Error('declaredContentLength must be a non-negative integer.');
  }
  const method = ensureNonEmptySingleLineField(params.methodUpper, 'methodUpper');
  if (method.toUpperCase() !== method) {
    throw new Error('METHOD_UPPER must already be uppercase.');
  }
  if (params.canonicalPath.includes('..') || params.canonicalPath.includes('//')) {
    throw new Error("canonicalPath must not contain '..' or '//'.");
  }
  if (params.canonicalQuery.startsWith('?')) {
    throw new Error("canonicalQuery must not start with '?'.");
  }
  if (params.fileSha256OrEmpty.length > 0) {
    ensureLowerHex64(params.fileSha256OrEmpty, 'fileSha256OrEmpty');
  }

  return [
    'CF-SIG-V1',
    ensureNonEmptySingleLineField(params.deviceId, 'deviceId'),
    ensureInteger(params.bindingVersion, 'bindingVersion'),
    ensureLowerHex32(params.sessionEpochHex32, 'sessionEpochHex32'),
    ensureNonEmptySingleLineField(params.mobileId, 'mobileId'),
    ensureLowerHex32(params.keyId, 'keyId'),
    params.keyPurpose,
    method,
    ensureNonEmptySingleLineField(params.canonicalPath, 'canonicalPath'),
    ensureSingleLineField(params.canonicalQuery, 'canonicalQuery'),
    ensureSingleLineField(params.ifNoneMatchOrEmpty, 'ifNoneMatchOrEmpty'),
    ensureSingleLineField(params.uploadIdOrEmpty, 'uploadIdOrEmpty'),
    params.fileSha256OrEmpty,
    ensureSingleLineField(params.clientOpIdOrEmpty, 'clientOpIdOrEmpty'),
    ensureSingleLineField(params.canonicalContentType, 'canonicalContentType'),
    ensureInteger(params.declaredContentLength, 'declaredContentLength'),
    ensureLowerHex64(params.bodySha256LowerHex, 'bodySha256LowerHex'),
    ensureInteger(params.unixTimestampSeconds, 'unixTimestampSeconds'),
    ensureLowerHex32(params.nonceHex32, 'nonceHex32'),
  ].join('\n');
}

export function buildSasV1Input(pairRecordV1Text: string): string {
  if (!pairRecordV1Text) {
    throw new Error('pairRecordV1Text must not be empty.');
  }
  return `CF-SAS-V1\n${pairRecordV1Text}`;
}

export function sas6FromSha256Digest(digestBytes: Uint8Array): string {
  if (digestBytes.byteLength < 4) {
    throw new Error('SHA-256 digest must be at least 4 bytes.');
  }
  const view = new DataView(digestBytes.buffer, digestBytes.byteOffset, digestBytes.byteLength);
  const u32 = view.getUint32(0, false);
  return String(u32 % 1_000_000).padStart(6, '0');
}

export interface CanonicalStructuredActionInput {
  readonly action: string;
  readonly planArtifactId?: string | null;
  readonly planVersion?: string | null;
  readonly planHash?: string | null;
  readonly idempotencyKey?: string | null;
  readonly startExecution?: boolean | null;
}

export function buildCanonicalStructuredActionV1(
  sa: CanonicalStructuredActionInput | null | undefined,
): string {
  if (!sa) {
    return '';
  }
  const action = ensureNonEmptySingleLineField(sa.action.trim().toLowerCase(), 'structuredAction.action');
  const planArtifactId = ensureSingleLineField(sa.planArtifactId ?? '', 'structuredAction.planArtifactId');
  const planVersion = ensureSingleLineField(sa.planVersion ?? '', 'structuredAction.planVersion');
  const planHash = ensureSingleLineField(sa.planHash ?? '', 'structuredAction.planHash');
  const idempotencyKey = ensureSingleLineField(sa.idempotencyKey ?? '', 'structuredAction.idempotencyKey');
  const startExecution =
    sa.startExecution === undefined || sa.startExecution === null
      ? ''
      : sa.startExecution
        ? 'true'
        : 'false';
  return `${action}|${planArtifactId}|${planVersion}|${planHash}|${idempotencyKey}|${startExecution}`;
}

export function buildChatOpV1Payload(
  sessionId: string,
  conversationIdOrEmpty: string | null | undefined,
  conversationMode: string,
  uploadIds: readonly string[],
  canonicalStructuredActionOrEmpty: string | null | undefined,
  message: string,
): string {
  if (typeof message !== 'string' || message.includes('\0')) {
    throw new Error('message must be a string without null bytes.');
  }
  const sortedUploadIds = uploadIds
    .map((id) => ensureNonEmptySingleLineField(id, 'uploadIds'))
    .sort(ordinalCompare);

  return [
    'CF-CHAT-OP-V1',
    ensureNonEmptySingleLineField(sessionId, 'sessionId'),
    ensureSingleLineField(conversationIdOrEmpty ?? '', 'conversationIdOrEmpty'),
    ensureNonEmptySingleLineField(conversationMode, 'conversationMode'),
    sortedUploadIds.join(','),
    ensureSingleLineField(canonicalStructuredActionOrEmpty ?? '', 'canonicalStructuredActionOrEmpty'),
    message,
  ].join('\n');
}

export function canonicalizeQuery(parameters: Iterable<readonly [string, string]>): string {
  const normalized: Array<[string, string]> = [];
  for (const [k, v] of parameters) {
    normalized.push([
      ensureNonEmptySingleLineField(k, 'queryKey'),
      ensureSingleLineField(v, 'queryValue'),
    ]);
  }
  normalized.sort((a, b) => ordinalCompare(a[0], b[0]) || ordinalCompare(a[1], b[1]));
  return normalized.map(([k, v]) => `${k}=${v}`).join('&');
}

export function canonicalizeContentType(rawHeader: string | null | undefined): string {
  if (!rawHeader) {
    return '';
  }
  const trimmed = rawHeader.trim();
  if (!trimmed) {
    return '';
  }
  const parts = trimmed.split(';');
  const mediaType = ensureSingleLineField(parts[0] ?? '', 'contentType').trim().toLowerCase();
  if (!mediaType) {
    return '';
  }
  if (parts.length === 1) {
    return mediaType;
  }
  const renderedParams: string[] = [];
  for (let i = 1; i < parts.length; i += 1) {
    const seg = (parts[i] ?? '').trim();
    if (!seg) {
      continue;
    }
    const eqIdx = seg.indexOf('=');
    if (eqIdx < 0) {
      renderedParams.push(ensureSingleLineField(seg, 'contentTypeParam').toLowerCase());
      continue;
    }
    const pName = ensureSingleLineField(seg.slice(0, eqIdx), 'contentTypeParamName').trim().toLowerCase();
    const pVal = ensureSingleLineField(seg.slice(eqIdx + 1), 'contentTypeParamValue').trim();
    renderedParams.push(`${pName}=${pVal}`);
  }
  return renderedParams.length > 0 ? `${mediaType}; ${renderedParams.join('; ')}` : mediaType;
}

const UUID_REGEX = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ALLOWED_QR_QUERY_KEYS = new Set(['v', 'i', 't', 'd', 'bv', 'k', 's', 'r']);

export interface ParsedPairingQrUri {
  readonly ticketId: string;
  readonly qrBootstrapToken: string;
  readonly deviceId: string;
  readonly bindingVersion: number;
  readonly desktopSpkiSha256: string;
  readonly pairingSecretB64U: string;
  readonly reviewSessionId?: string;
}

export function buildPairingQrUri(params: {
  ticketId: string;
  qrBootstrapToken: string;
  deviceId: string;
  bindingVersion: number;
  desktopSpkiSha256: string;
  pairingSecretB64U: string;
  reviewSessionId?: string | null;
}): string {
  if (!UUID_REGEX.test(params.ticketId) || !UUID_REGEX.test(params.deviceId)) {
    throw new Error('ticketId and deviceId must be valid UUID strings.');
  }
  ensureLowerHex64(params.desktopSpkiSha256, 'desktopSpkiSha256');
  const secretBytes = base64UrlToBytes(params.pairingSecretB64U);
  if (secretBytes.byteLength !== 32) {
    throw new Error('pairingSecretB64U must decode to 32 bytes.');
  }
  const query = new URLSearchParams({
    v: '1',
    i: params.ticketId.toLowerCase(),
    t: ensureNonEmptySingleLineField(params.qrBootstrapToken, 'qrBootstrapToken'),
    d: params.deviceId.toLowerCase(),
    bv: ensureInteger(params.bindingVersion, 'bindingVersion'),
    k: params.desktopSpkiSha256,
    s: params.pairingSecretB64U,
  });
  if (params.reviewSessionId) {
    if (!UUID_REGEX.test(params.reviewSessionId)) {
      throw new Error('reviewSessionId must be a valid UUID string.');
    }
    query.set('r', params.reviewSessionId.toLowerCase());
  }
  return `codexflow://pair?${query.toString()}`;
}

export function parsePairingQrUri(rawInput: string): ParsedPairingQrUri {
  const trimmed = rawInput.trim();
  if (!trimmed || trimmed.length > 1024 || /[\r\n\0]/.test(trimmed)) {
    throw new Error('Pairing URI is empty, too long, or contains control characters.');
  }
  let parsed: URL;
  try {
    parsed = new URL(trimmed);
  } catch {
    throw new Error('Malformed pairing URI.');
  }
  if (parsed.protocol !== 'codexflow:' || parsed.hostname.toLowerCase() !== 'pair') {
    throw new Error('Unsupported pairing URI scheme or host.');
  }
  if (parsed.username || parsed.password || (parsed.pathname && parsed.pathname !== '/')) {
    throw new Error('Pairing URI must not contain userinfo or extra path segments.');
  }

  const seen = new Map<string, string>();
  for (const [k, v] of parsed.searchParams.entries()) {
    if (!ALLOWED_QR_QUERY_KEYS.has(k)) {
      throw new Error(`Disallowed QR query parameter '${k}'.`);
    }
    if (seen.has(k)) {
      throw new Error(`Duplicate QR query parameter '${k}'.`);
    }
    seen.set(k, v);
  }

  if (seen.get('v') !== '1') {
    throw new Error('Unsupported pairing URI version.');
  }
  const ticketId = seen.get('i') ?? '';
  const qrBootstrapToken = seen.get('t') ?? '';
  const deviceId = seen.get('d') ?? '';
  const bvRaw = seen.get('bv') ?? '';
  const desktopSpkiSha256 = (seen.get('k') ?? '').toLowerCase();
  const pairingSecretB64U = seen.get('s') ?? '';
  const reviewSessionId = seen.get('r');

  if (!UUID_REGEX.test(ticketId) || !UUID_REGEX.test(deviceId)) {
    throw new Error('Invalid ticketId or deviceId inside pairing URI.');
  }
  if (!qrBootstrapToken || qrBootstrapToken.length > 256) {
    throw new Error('Missing or invalid bootstrap token inside pairing URI.');
  }
  if (!/^[1-9][0-9]{0,9}$/.test(bvRaw)) {
    throw new Error('Invalid bindingVersion inside pairing URI.');
  }
  if (!isLowerHex(desktopSpkiSha256, 64)) {
    throw new Error('Invalid desktopSpkiSha256 inside pairing URI.');
  }
  const secretBytes = base64UrlToBytes(pairingSecretB64U);
  if (secretBytes.byteLength !== 32) {
    throw new Error('Invalid pairingSecret length inside pairing URI.');
  }
  if (reviewSessionId !== undefined && !UUID_REGEX.test(reviewSessionId)) {
    throw new Error('Invalid reviewSessionId inside pairing URI.');
  }

  return {
    ticketId: ticketId.toLowerCase(),
    qrBootstrapToken,
    deviceId: deviceId.toLowerCase(),
    bindingVersion: Number.parseInt(bvRaw, 10),
    desktopSpkiSha256,
    pairingSecretB64U,
    ...(reviewSessionId ? { reviewSessionId: reviewSessionId.toLowerCase() } : {}),
  };
}
