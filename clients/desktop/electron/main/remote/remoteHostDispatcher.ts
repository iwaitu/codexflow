import {
  EMPTY_BODY_SHA256_HEX,
  buildApprovalCanonicalPayloadV1,
  buildApprovalCanonicalTextV1,
  canonicalizeContentType,
  canonicalizeQuery,
  isLowerHex,
  sanitizeSingleLineSummaryText,
  type KeyPurpose,
  type SessionEpochCertDto,
  type TunnelHttpHeader,
} from '@codexflow-shared/remoteProtocol';
import {
  formatSseEvent,
  sanitizeSseDataJson,
  SseStreamParser,
  stripSensitiveImageFields,
} from '@codexflow-shared/sseStreamParser';
import { RemoteDispatchTarget } from '../backendManager';
import { RemoteConfigStore } from './remoteConfigStore';
import {
  createSignedApprovalEnvelopeV1,
  createSignedPlanEnvelopeV1,
  sha256HexLower,
  verifyApprovalEnvelopeV1,
  verifyPlanEnvelopeV1,
  verifyRequestSigV1,
} from './remoteCrypto';
import { RemoteUploadStaging } from './remoteUploadStaging';
import { ReplayNonceGuard } from './replayNonceGuard';

const MAX_STANDARD_JSON_BODY_BYTES = 4 * 1024 * 1024;
const ALLOWED_RESPONSE_HEADERS = new Set([
  'content-type',
  'cache-control',
  'etag',
  'x-screenshot-seq',
]);

export interface IncomingTunnelRequest {
  readonly method: string;
  readonly path: string;
  readonly query: string;
  readonly headers: readonly TunnelHttpHeader[];
  readonly bodyChunks: AsyncIterable<Uint8Array>;
  readonly signal: AbortSignal;
}

export interface OutgoingTunnelResponse {
  readonly statusCode: number;
  readonly headers: TunnelHttpHeader[];
  readonly bodyStream: AsyncIterable<Uint8Array>;
}

export interface RemoteHostDispatcherDeps {
  readonly configStore: RemoteConfigStore;
  readonly nonceGuard: ReplayNonceGuard;
  readonly uploadStaging: RemoteUploadStaging;
  readonly getDispatchTarget: () => RemoteDispatchTarget | null;
  readonly getValidEpochCerts: () => readonly SessionEpochCertDto[];
  readonly upstreamFetch?: typeof fetch;
  readonly nowMs?: () => number;
}

type QueryValidator = (queryMap: Map<string, string>) => boolean;

interface RoutePolicy {
  readonly id: string;
  readonly method: string;
  readonly pathRegex: RegExp;
  readonly allowedQueryKeys: ReadonlySet<string>;
  readonly requiredQueryKeys: ReadonlySet<string>;
  readonly allowIfNoneMatch?: boolean;
  readonly requiredKeyPurpose: KeyPurpose;
  readonly useControlToken: boolean;
  readonly queryValidator?: QueryValidator;
}

const SAFE_ID = '[A-Za-z0-9_-]{1,128}';

function isNonNegIntString(val: string): boolean {
  return /^(0|[1-9][0-9]{0,18})$/.test(val);
}

function isPositiveIntString(val: string): boolean {
  return /^[1-9][0-9]{0,18}$/.test(val);
}

const STATIC_ROUTE_POLICIES: readonly RoutePolicy[] = [
  {
    id: 'system_status',
    method: 'GET',
    pathRegex: /^\/api\/system\/status$/,
    allowedQueryKeys: new Set(['refresh']),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'settings_execution_get',
    method: 'GET',
    pathRegex: /^\/api\/settings\/execution$/,
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'settings_model_get',
    method: 'GET',
    pathRegex: /^\/api\/settings\/model$/,
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_workspaces_get',
    method: 'GET',
    pathRegex: /^\/api\/gateway\/workspaces$/,
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_sessions_list',
    method: 'GET',
    pathRegex: /^\/api\/gateway\/sessions$/,
    allowedQueryKeys: new Set(['page', 'pageSize', 'includeArchived']),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_session_conversations_list',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/conversations$`),
    allowedQueryKeys: new Set(['page', 'pageSize', 'includeArchived']),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_history_get',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/history/${SAFE_ID}$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_plan_get',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/plan$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_tasklist_get',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/tasklist$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_context_window_get',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/context-window$`),
    allowedQueryKeys: new Set(['conversationId']),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_changes_get',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/changes$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_approvals_get',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/approvals$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_files_list',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/files$`),
    allowedQueryKeys: new Set(['path']),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_files_content',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/files/content$`),
    allowedQueryKeys: new Set(['path']),
    requiredQueryKeys: new Set(['path']),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_snapshot_get',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/snapshot$`),
    allowedQueryKeys: new Set(['conversationId']),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'jobs_session_list',
    method: 'GET',
    pathRegex: new RegExp(`^/api/jobs/session/${SAFE_ID}$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'jobs_get',
    method: 'GET',
    pathRegex: new RegExp(`^/api/jobs/${SAFE_ID}$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'jobs_status_get',
    method: 'GET',
    pathRegex: new RegExp(`^/api/jobs/${SAFE_ID}/status$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'jobs_history_get',
    method: 'GET',
    pathRegex: new RegExp(`^/api/jobs/${SAFE_ID}/history$`),
    allowedQueryKeys: new Set(['afterSeq']),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
    queryValidator: (q) => !q.has('afterSeq') || isNonNegIntString(q.get('afterSeq')!),
  },
  {
    id: 'gateway_browser_state',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/browser$`),
    allowedQueryKeys: new Set(['afterSeq']),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
    queryValidator: (q) => !q.has('afterSeq') || isNonNegIntString(q.get('afterSeq')!),
  },
  {
    id: 'gateway_computer_state',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/computer$`),
    allowedQueryKeys: new Set(['afterSeq']),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
    queryValidator: (q) => !q.has('afterSeq') || isNonNegIntString(q.get('afterSeq')!),
  },
  {
    id: 'gateway_sse',
    method: 'GET',
    pathRegex: /^\/api\/gateway\/sse$/,
    allowedQueryKeys: new Set(['sessionId', 'lastEventId', 'epoch']),
    requiredQueryKeys: new Set(['sessionId']),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
    queryValidator: (q) => !q.has('lastEventId') || isNonNegIntString(q.get('lastEventId')!),
  },
  {
    id: 'gateway_events_summary',
    method: 'GET',
    pathRegex: /^\/api\/gateway\/events\/summary$/,
    allowedQueryKeys: new Set(['mode', 'snapshotId', 'pageToken', 'afterEventId', 'limit']),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
    queryValidator: (q) => {
      if (q.has('mode') && q.get('mode') !== 'snapshot' && q.get('mode') !== 'delta') {
        return false;
      }
      if (q.has('afterEventId') && !isNonNegIntString(q.get('afterEventId')!)) {
        return false;
      }
      if (q.has('limit') && !isPositiveIntString(q.get('limit')!)) {
        return false;
      }
      return true;
    },
  },
  {
    id: 'gateway_browser_screenshot',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/browser/screenshot$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    allowIfNoneMatch: true,
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_upload_status',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/attachments/uploads/${SAFE_ID}$`),
    allowedQueryKeys: new Set(['fileSha256']),
    requiredQueryKeys: new Set(['fileSha256']),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
    queryValidator: (q) => isLowerHex(q.get('fileSha256'), 64),
  },
  {
    id: 'gateway_chat_op_status',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/ops/${SAFE_ID}$`),
    allowedQueryKeys: new Set(['requestDigest']),
    requiredQueryKeys: new Set(['requestDigest']),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
    queryValidator: (q) => isLowerHex(q.get('requestDigest'), 64),
  },
  {
    id: 'gateway_approval_script',
    method: 'GET',
    pathRegex: new RegExp(`^/api/gateway/approvals/${SAFE_ID}/script$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: true,
  },
  {
    id: 'gateway_session_create',
    method: 'POST',
    pathRegex: /^\/api\/gateway\/session$/,
    allowedQueryKeys: new Set(['name', 'workMode']),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_session_bootstrap',
    method: 'POST',
    pathRegex: /^\/api\/gateway\/session\/bootstrap$/,
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_session_daily',
    method: 'POST',
    pathRegex: /^\/api\/gateway\/session\/daily-assistant$/,
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_conversation_create',
    method: 'POST',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/conversations$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_session_compact',
    method: 'POST',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/compact$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_session_patch',
    method: 'PATCH',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_session_delete',
    method: 'DELETE',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_conversation_patch',
    method: 'PATCH',
    pathRegex: new RegExp(`^/api/gateway/conversations/${SAFE_ID}$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_conversation_delete',
    method: 'DELETE',
    pathRegex: new RegExp(`^/api/gateway/conversations/${SAFE_ID}$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_attachments_upload',
    method: 'POST',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/attachments$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_session_cancel',
    method: 'POST',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/cancel$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'jobs_cancel_post',
    method: 'POST',
    pathRegex: new RegExp(`^/api/jobs/${SAFE_ID}/cancel$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'jobs_cancel_delete',
    method: 'DELETE',
    pathRegex: new RegExp(`^/api/jobs/${SAFE_ID}$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'jobs_session_cancel_all',
    method: 'POST',
    pathRegex: new RegExp(`^/api/jobs/session/${SAFE_ID}/cancel-all$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'settings_execution_put',
    method: 'PUT',
    pathRegex: /^\/api\/settings\/execution$/,
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session',
    useControlToken: false,
  },
  {
    id: 'gateway_chat_post',
    method: 'POST',
    pathRegex: /^\/api\/gateway\/chat$/,
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session', // dynamically upgraded to biometric_approval for approve/execute
    useControlToken: false,
  },
  {
    id: 'gateway_plan_mode_post',
    method: 'POST',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/plan-mode$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'device_session', // dynamically upgraded to biometric_approval for active=false
    useControlToken: false,
  },
  {
    id: 'gateway_approvals_submit',
    method: 'POST',
    pathRegex: new RegExp(`^/api/gateway/approvals/${SAFE_ID}$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'biometric_approval',
    useControlToken: true,
  },
  {
    id: 'gateway_computer_emergency_stop',
    method: 'POST',
    pathRegex: /^\/api\/gateway\/computer\/emergency-stop$/,
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'biometric_approval',
    useControlToken: true,
  },
  {
    id: 'gateway_browser_close',
    method: 'POST',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/browser/close$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'biometric_approval',
    useControlToken: true,
  },
  {
    id: 'gateway_computer_close',
    method: 'POST',
    pathRegex: new RegExp(`^/api/gateway/session/${SAFE_ID}/computer/close$`),
    allowedQueryKeys: new Set(),
    requiredQueryKeys: new Set(),
    requiredKeyPurpose: 'biometric_approval',
    useControlToken: true,
  },
];

function jsonResponse(statusCode: number, code: string, message: string): OutgoingTunnelResponse {
  const bytes = Buffer.from(JSON.stringify({ error: code, code, message }), 'utf8');
  return {
    statusCode,
    headers: [{ name: 'content-type', value: 'application/json; charset=utf-8' }],
    bodyStream: singleChunkIterable(bytes),
  };
}

async function* singleChunkIterable(bytes: Uint8Array): AsyncIterable<Uint8Array> {
  if (bytes.byteLength > 0) {
    yield bytes;
  }
}

async function readBoundedBytes(
  chunks: AsyncIterable<Uint8Array>,
  maxBytes: number,
  declaredLen: number,
  signal: AbortSignal,
): Promise<Uint8Array> {
  if (declaredLen > maxBytes) {
    throw new Error('payload_too_large');
  }
  const parts: Buffer[] = [];
  let total = 0;
  for await (const chunk of chunks) {
    if (signal.aborted) {
      throw new Error('aborted');
    }
    total += chunk.byteLength;
    if (total > maxBytes || total > declaredLen) {
      throw new Error('content_length_mismatch');
    }
    parts.push(Buffer.from(chunk));
  }
  if (total !== declaredLen) {
    throw new Error('content_length_mismatch');
  }
  return Buffer.concat(parts, total);
}

const SINGLE_VALUE_STANDARD_HEADERS = new Set(['content-type', 'content-length', 'if-none-match']);

function normalizeHeaderMap(
  headers: readonly TunnelHttpHeader[],
): { ok: true; map: Map<string, string> } | { ok: false; duplicateKey: string } {
  const map = new Map<string, string>();
  for (const h of headers) {
    const key = h.name.trim().toLowerCase();
    const isSingleValue = key.startsWith('x-cf-') || SINGLE_VALUE_STANDARD_HEADERS.has(key);
    if (map.has(key)) {
      if (isSingleValue) {
        return { ok: false, duplicateKey: key };
      }
      continue;
    }
    map.set(key, h.value);
  }
  return { ok: true, map };
}

function parseRawQueryPairs(rawQuery: string): { pairs: Array<[string, string]>; map: Map<string, string> } | null {
  if (!rawQuery) {
    return { pairs: [], map: new Map() };
  }
  const cleaned = rawQuery.startsWith('?') ? rawQuery.slice(1) : rawQuery;
  if (!cleaned) {
    return { pairs: [], map: new Map() };
  }
  const pairs: Array<[string, string]> = [];
  const map = new Map<string, string>();
  const sp = new URLSearchParams(cleaned);
  for (const [k, v] of sp.entries()) {
    if (map.has(k)) {
      return null; // Reject duplicate query parameters
    }
    pairs.push([k, v]);
    map.set(k, v);
  }
  return { pairs, map };
}

function isPlainRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function hasOnlyAllowedKeys(obj: Record<string, unknown>, allowed: ReadonlySet<string>): boolean {
  for (const key of Object.keys(obj)) {
    if (!allowed.has(key)) {
      return false;
    }
  }
  return true;
}

export class RemoteHostDispatcher {
  private readonly deps: RemoteHostDispatcherDeps;

  constructor(deps: RemoteHostDispatcherDeps) {
    this.deps = deps;
  }

  public async dispatch(req: IncomingTunnelRequest): Promise<OutgoingTunnelResponse> {
    const nowMs = this.deps.nowMs ? this.deps.nowMs() : Date.now();
    const methodUpper = req.method.trim().toUpperCase();
    const rawPath = req.path.trim();

    if (rawPath.includes('..') || rawPath.includes('//') || !rawPath.startsWith('/')) {
      return jsonResponse(403, 'remote_endpoint_forbidden', 'Invalid canonical path.');
    }

    const headerRes = normalizeHeaderMap(req.headers);
    if (!headerRes.ok) {
      return jsonResponse(
        400,
        'duplicate_signature_header',
        `Duplicate header '${headerRes.duplicateKey}' is forbidden.`,
      );
    }
    const headerMap = headerRes.map;
    const mobileId = (headerMap.get('x-cf-mobile-id') ?? '').trim();
    const keyId = (headerMap.get('x-cf-key-id') ?? '').trim();
    const keyPurposeRaw = (headerMap.get('x-cf-key-purpose') ?? '').trim();
    const sessionEpoch = (headerMap.get('x-cf-session-epoch') ?? '').trim();
    const timestampRaw = (headerMap.get('x-cf-timestamp') ?? '').trim();
    const nonce = (headerMap.get('x-cf-nonce') ?? '').trim();
    const sig = (headerMap.get('x-cf-sig') ?? '').trim();

    if (
      !mobileId ||
      !isLowerHex(keyId, 32) ||
      (keyPurposeRaw !== 'device_session' && keyPurposeRaw !== 'biometric_approval') ||
      !isLowerHex(sessionEpoch, 32) ||
      !/^\d{1,15}$/.test(timestampRaw) ||
      !isLowerHex(nonce, 32) ||
      !sig
    ) {
      return jsonResponse(401, 'missing_or_invalid_sig_headers', 'Missing or malformed X-CF signature headers.');
    }

    const snap = this.deps.configStore.getPublicSnapshot();
    if (!snap.bound || !snap.device) {
      return jsonResponse(503, 'desktop_not_bound', 'Desktop remote control is not bound.');
    }

    const nowSec = Math.floor(nowMs / 1000);
    const validEpochCerts = this.deps.getValidEpochCerts();
    const matchingEpoch = validEpochCerts.find(
      (c) => c.sessionEpochHex32 === sessionEpoch && c.expiresAtUnix > nowSec,
    );
    if (!matchingEpoch) {
      return jsonResponse(401, 'invalid_session_epoch', 'Session epoch is unknown or expired.');
    }

    const pairingMatch = this.deps.configStore.findActivePairingForMobileKey(
      mobileId,
      keyId,
      keyPurposeRaw,
    );
    if (!pairingMatch || pairingMatch.pairing.bindingVersion !== snap.device.bindingVersion) {
      return jsonResponse(403, 'pairing_or_key_revoked', 'Mobile pairing or key is inactive or revoked.');
    }

    const unixTimestampSeconds = Number.parseInt(timestampRaw, 10);
    if (Math.abs(nowSec - unixTimestampSeconds) > 60) {
      return jsonResponse(401, 'timestamp_skew_exceeded', 'Request timestamp skew exceeds 60 seconds.');
    }

    const parsedQuery = parseRawQueryPairs(req.query);
    if (!parsedQuery) {
      return jsonResponse(403, 'remote_endpoint_forbidden', 'Duplicate query keys are forbidden.');
    }
    const canonicalQuery = canonicalizeQuery(parsedQuery.pairs);

    const ifNoneMatchOrEmpty = headerMap.get('if-none-match') ?? '';
    const uploadIdOrEmpty = (headerMap.get('x-cf-upload-id') ?? '').trim();
    const fileSha256OrEmpty = (headerMap.get('x-cf-file-sha256') ?? '').trim();
    const clientOpIdOrEmpty = (headerMap.get('x-cf-client-op-id') ?? '').trim();
    const canonicalContentType = canonicalizeContentType(headerMap.get('content-type'));

    const contentLenRaw = headerMap.get('content-length');
    const declaredContentLength =
      contentLenRaw !== undefined && contentLenRaw.trim() !== ''
        ? Number.parseInt(contentLenRaw.trim(), 10)
        : 0;
    if (!Number.isInteger(declaredContentLength) || declaredContentLength < 0) {
      return jsonResponse(400, 'invalid_content_length', 'Invalid Content-Length header.');
    }

    const bodyShaHeader = (headerMap.get('x-cf-body-sha256') ?? '').trim();
    const bodySha256LowerHex =
      bodyShaHeader.length > 0
        ? bodyShaHeader
        : declaredContentLength === 0
          ? EMPTY_BODY_SHA256_HEX
          : '';
    if (!isLowerHex(bodySha256LowerHex, 64)) {
      return jsonResponse(400, 'invalid_body_sha256', 'Missing or invalid X-CF-Body-Sha256 header.');
    }

    // Step 1: Verify 19-line CF-SIG-V1 BEFORE nonce consumption, staging, or quota allocation
    const sigOk = verifyRequestSigV1({
      spkiB64: pairingMatch.spkiB64,
      deviceId: snap.device.deviceId,
      bindingVersion: snap.device.bindingVersion,
      sessionEpochHex32: sessionEpoch,
      mobileId,
      keyId,
      keyPurpose: keyPurposeRaw,
      methodUpper,
      canonicalPath: rawPath,
      canonicalQuery,
      ifNoneMatchOrEmpty,
      uploadIdOrEmpty,
      fileSha256OrEmpty,
      clientOpIdOrEmpty,
      canonicalContentType,
      declaredContentLength,
      bodySha256LowerHex,
      unixTimestampSeconds,
      nonceHex32: nonce,
      sigB64U: sig,
    });
    if (!sigOk) {
      return jsonResponse(401, 'invalid_request_signature', 'CF-SIG-V1 verification failed.');
    }

    // Step 2: Replay protection across ALL signed routes (including GET)
    const nonceRes = this.deps.nonceGuard.checkAndConsume({
      mobileId,
      keyId,
      sessionEpochHex32: sessionEpoch,
      nonceHex32: nonce,
      unixTimestampSeconds,
      nowMs,
    });
    if (!nonceRes.ok) {
      return jsonResponse(401, nonceRes.code, nonceRes.message);
    }

    // Step 3: Default-deny Section 7.3 route, query, header & keyPurpose check
    const policy = STATIC_ROUTE_POLICIES.find(
      (p) => p.method === methodUpper && p.pathRegex.test(rawPath),
    );
    if (!policy) {
      return jsonResponse(403, 'remote_endpoint_forbidden', 'Route is not permitted via remote control.');
    }

    for (const k of parsedQuery.map.keys()) {
      if (!policy.allowedQueryKeys.has(k)) {
        return jsonResponse(
          403,
          'remote_endpoint_forbidden',
          `Query parameter '${k}' is not permitted on this route.`,
        );
      }
    }
    for (const reqKey of policy.requiredQueryKeys) {
      if (!parsedQuery.map.has(reqKey) || !parsedQuery.map.get(reqKey)) {
        return jsonResponse(
          403,
          'remote_endpoint_forbidden',
          `Required query parameter '${reqKey}' missing.`,
        );
      }
    }
    if (policy.queryValidator && !policy.queryValidator(parsedQuery.map)) {
      return jsonResponse(403, 'remote_endpoint_forbidden', 'Invalid query parameter value.');
    }
    if (!policy.allowIfNoneMatch && ifNoneMatchOrEmpty.length > 0) {
      return jsonResponse(
        403,
        'remote_endpoint_forbidden',
        'If-None-Match is only permitted on browser/screenshot.',
      );
    }
    if (policy.requiredKeyPurpose === 'biometric_approval' && keyPurposeRaw !== 'biometric_approval') {
      return jsonResponse(
        403,
        'biometric_key_required',
        'This action requires a signature produced by the biometric_approval key.',
      );
    }

    const target = this.deps.getDispatchTarget();
    if (!target) {
      return jsonResponse(503, 'host_backend_unavailable', 'CodexFlow.Local managed backend is not ready.');
    }

    // Reconstruct canonical outbound headers ONLY from validated inputs
    const upstreamHeaders: Record<string, string> = {
      'X-CF-Control-Channel': 'Remote',
      'X-CF-Remote-Bridge-Token': target.remoteBridgeToken,
      'X-CF-Pairing-Id': pairingMatch.pairing.pairingId,
      'X-CF-Binding-Version': String(snap.device.bindingVersion),
      'X-CF-Account-Rev-Seq': String(snap.watermarks.accountRevocationSeq),
      'X-CF-Device-Rev-Seq': String(snap.watermarks.deviceRevocationSeq),
    };
    let useControlToken = policy.useControlToken;

    if (policy.allowIfNoneMatch && ifNoneMatchOrEmpty) {
      upstreamHeaders['If-None-Match'] = ifNoneMatchOrEmpty;
    }

    // Route-specific header & body validation
    let upstreamBody: Uint8Array | undefined;
    if (policy.id === 'gateway_upload_status') {
      if (!uploadIdOrEmpty || !rawPath.endsWith(`/${uploadIdOrEmpty}`)) {
        return jsonResponse(400, 'invalid_upload_id', 'X-CF-Upload-Id must match route uploadId.');
      }
      upstreamHeaders['X-CF-Upload-Id'] = uploadIdOrEmpty;
    }

    if (policy.id === 'gateway_attachments_upload') {
      if (!uploadIdOrEmpty || !isLowerHex(fileSha256OrEmpty, 64)) {
        return jsonResponse(400, 'missing_upload_headers', 'Upload requires X-CF-Upload-Id and X-CF-File-Sha256.');
      }
      const staged = await this.deps.uploadStaging.stageAndVerifyMultipartUpload({
        pairingId: pairingMatch.pairing.pairingId,
        uploadId: uploadIdOrEmpty,
        declaredContentLength,
        declaredBodySha256: bodySha256LowerHex,
        declaredFileSha256: fileSha256OrEmpty,
        contentTypeHeader: canonicalContentType,
        chunks: req.bodyChunks,
        signal: req.signal,
      });
      if (!staged.ok) {
        return jsonResponse(staged.statusCode, staged.code, staged.message);
      }
      upstreamHeaders['Content-Type'] = staged.forward.contentType;
      upstreamHeaders['X-CF-Upload-Id'] = uploadIdOrEmpty;
      upstreamHeaders['X-CF-File-Sha256'] = staged.forward.fileSha256;
      upstreamBody = staged.forward.bodyBytes;
    } else if (methodUpper === 'POST' || methodUpper === 'PUT' || methodUpper === 'PATCH') {
      let bodyBytes: Uint8Array;
      try {
        bodyBytes = await readBoundedBytes(
          req.bodyChunks,
          MAX_STANDARD_JSON_BODY_BYTES,
          declaredContentLength,
          req.signal,
        );
      } catch (err) {
        const msg = err instanceof Error ? err.message : 'bad_body';
        return jsonResponse(
          msg === 'payload_too_large' ? 413 : 400,
          msg,
          'Invalid or oversized request body.',
        );
      }

      if (sha256HexLower(bodyBytes) !== bodySha256LowerHex) {
        return jsonResponse(400, 'body_sha256_mismatch', 'Actual body SHA-256 differs from X-CF-Body-Sha256.');
      }

      const bodyCheck = this.validateAndRebuildJsonBody({
        policyId: policy.id,
        rawPath,
        keyPurpose: keyPurposeRaw,
        desktopSpkiB64: snap.device.desktopSpkiB64,
        deviceId: snap.device.deviceId,
        bindingVersion: snap.device.bindingVersion,
        clientOpId: clientOpIdOrEmpty,
        bodyBytes,
        nowSec,
      });
      if (!bodyCheck.ok) {
        return jsonResponse(bodyCheck.statusCode, bodyCheck.code, bodyCheck.message);
      }
      useControlToken = bodyCheck.useControlTokenOverride ?? useControlToken;
      if (bodyCheck.extraUpstreamHeaders) {
        Object.assign(upstreamHeaders, bodyCheck.extraUpstreamHeaders);
      }
      if (bodyCheck.rebuiltJson !== undefined) {
        upstreamHeaders['Content-Type'] = 'application/json; charset=utf-8';
        upstreamBody = Buffer.from(bodyCheck.rebuiltJson, 'utf8');
      }
    }

    upstreamHeaders['Authorization'] = `Bearer ${useControlToken ? target.controlToken : target.accessToken}`;

    const upstreamQuery = parsedQuery.pairs
      .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`)
      .join('&');
    const targetUrl = `${target.baseUrl}${rawPath}${upstreamQuery ? `?${upstreamQuery}` : ''}`;
    const fetchImpl = this.deps.upstreamFetch ?? fetch;

    let upstreamResp: Response;
    try {
      upstreamResp = await fetchImpl(targetUrl, {
        method: methodUpper,
        headers: upstreamHeaders,
        body: upstreamBody ? Buffer.from(upstreamBody) : undefined,
        signal: req.signal,
      });
    } catch (err) {
      if (req.signal.aborted) {
        return jsonResponse(499, 'client_closed', 'Request aborted.');
      }
      return jsonResponse(
        502,
        'local_backend_unreachable',
        err instanceof Error ? err.message : 'Failed reaching CodexFlow.Local.',
      );
    }

    const filteredHeaders: TunnelHttpHeader[] = [];
    for (const [k, v] of upstreamResp.headers.entries()) {
      const lower = k.toLowerCase();
      if (ALLOWED_RESPONSE_HEADERS.has(lower)) {
        filteredHeaders.push({ name: lower, value: v });
      }
    }

    const respContentType = (upstreamResp.headers.get('content-type') ?? '').toLowerCase();
    if (respContentType.includes('text/event-stream') && upstreamResp.body) {
      return {
        statusCode: upstreamResp.status,
        headers: filteredHeaders,
        bodyStream: this.projectSseResponseStream(upstreamResp.body),
      };
    }

    if (respContentType.includes('application/json') || respContentType.includes('+json')) {
      const text = await upstreamResp.text();
      if (!text) {
        return {
          statusCode: upstreamResp.status,
          headers: filteredHeaders,
          bodyStream: singleChunkIterable(new Uint8Array(0)),
        };
      }
      try {
        const parsed: unknown = JSON.parse(text);
        const projected = stripSensitiveImageFields(parsed);
        if (upstreamResp.ok) {
          const desktopPrivateKeyPem = this.deps.configStore.getDesktopPrivateKeyPem();
          if (desktopPrivateKeyPem) {
            this.signRemoteEnvelopesOnResponse({
              policyId: policy.id,
              rawPath,
              body: projected,
              desktopPrivateKeyPem,
              deviceId: snap.device.deviceId,
              bindingVersion: snap.device.bindingVersion,
              nowSec,
            });
          }
        }
        const bytes = Buffer.from(JSON.stringify(projected), 'utf8');
        return {
          statusCode: upstreamResp.status,
          headers: filteredHeaders,
          bodyStream: singleChunkIterable(bytes),
        };
      } catch {
        return jsonResponse(502, 'invalid_upstream_json', 'Malformed JSON from local host.');
      }
    }

    if (!upstreamResp.body) {
      return {
        statusCode: upstreamResp.status,
        headers: filteredHeaders,
        bodyStream: singleChunkIterable(new Uint8Array(0)),
      };
    }

    return {
      statusCode: upstreamResp.status,
      headers: filteredHeaders,
      bodyStream: this.readableToAsyncIterable(upstreamResp.body),
    };
  }

  private async *readableToAsyncIterable(body: ReadableStream<Uint8Array>): AsyncIterable<Uint8Array> {
    const reader = body.getReader();
    try {
      while (true) {
        const { value, done } = await reader.read();
        if (done) {
          break;
        }
        if (value && value.byteLength > 0) {
          yield value;
        }
      }
    } finally {
      reader.releaseLock();
    }
  }

  private async *projectSseResponseStream(body: ReadableStream<Uint8Array>): AsyncIterable<Uint8Array> {
    const reader = body.getReader();
    const decoder = new TextDecoder('utf-8');
    const parser = new SseStreamParser();
    try {
      while (true) {
        const { value, done } = await reader.read();
        if (done) {
          break;
        }
        const textChunk = decoder.decode(value, { stream: true });
        for (const frame of parser.push(textChunk)) {
          const sanitizedData = sanitizeSseDataJson(frame.data);
          yield Buffer.from(formatSseEvent({ ...frame, data: sanitizedData }), 'utf8');
        }
      }
      const tail = decoder.decode();
      if (tail) {
        for (const frame of parser.push(tail)) {
          const sanitizedData = sanitizeSseDataJson(frame.data);
          yield Buffer.from(formatSseEvent({ ...frame, data: sanitizedData }), 'utf8');
        }
      }
      for (const frame of parser.flush()) {
        const sanitizedData = sanitizeSseDataJson(frame.data);
        yield Buffer.from(formatSseEvent({ ...frame, data: sanitizedData }), 'utf8');
      }
    } finally {
      reader.releaseLock();
    }
  }

  private validateAndRebuildJsonBody(params: {
    policyId: string;
    rawPath: string;
    keyPurpose: KeyPurpose;
    desktopSpkiB64: string;
    deviceId: string;
    bindingVersion: number;
    clientOpId: string;
    bodyBytes: Uint8Array;
    nowSec: number;
  }):
    | {
        ok: true;
        rebuiltJson?: string;
        useControlTokenOverride?: boolean;
        extraUpstreamHeaders?: Record<string, string>;
      }
    | { ok: false; statusCode: number; code: string; message: string } {
    if (params.bodyBytes.byteLength === 0) {
      if (
        params.policyId === 'gateway_chat_post' ||
        params.policyId === 'gateway_plan_mode_post' ||
        params.policyId === 'settings_execution_put' ||
        params.policyId === 'gateway_approvals_submit' ||
        params.policyId === 'gateway_computer_emergency_stop' ||
        params.policyId === 'gateway_session_patch' ||
        params.policyId === 'gateway_conversation_patch'
      ) {
        return { ok: false, statusCode: 400, code: 'body_required', message: 'JSON body is required.' };
      }
      return { ok: true };
    }

    let rawJson: unknown;
    try {
      rawJson = JSON.parse(Buffer.from(params.bodyBytes).toString('utf8'));
    } catch {
      return { ok: false, statusCode: 400, code: 'invalid_json', message: 'Malformed JSON body.' };
    }

    if (!isPlainRecord(rawJson)) {
      return { ok: false, statusCode: 400, code: 'invalid_json_object', message: 'Body must be a JSON object.' };
    }

    switch (params.policyId) {
      case 'gateway_session_bootstrap': {
        if (!hasOnlyAllowedKeys(rawJson, new Set(['targetSessionId']))) {
          return { ok: false, statusCode: 403, code: 'remote_endpoint_forbidden', message: 'Unexpected JSON field.' };
        }
        return { ok: true, rebuiltJson: JSON.stringify(rawJson) };
      }

      case 'gateway_session_create':
      case 'gateway_session_daily':
      case 'gateway_session_cancel':
      case 'jobs_cancel_post':
      case 'jobs_session_cancel_all':
      case 'gateway_browser_close':
      case 'gateway_computer_close': {
        if (!hasOnlyAllowedKeys(rawJson, new Set())) {
          return { ok: false, statusCode: 403, code: 'remote_endpoint_forbidden', message: 'No body fields allowed on this endpoint.' };
        }
        return { ok: true, rebuiltJson: '{}' };
      }

      case 'gateway_conversation_create': {
        if (!hasOnlyAllowedKeys(rawJson, new Set(['title']))) {
          return { ok: false, statusCode: 403, code: 'remote_endpoint_forbidden', message: 'Unexpected JSON field.' };
        }
        return { ok: true, rebuiltJson: JSON.stringify(rawJson) };
      }

      case 'gateway_session_compact': {
        if (!hasOnlyAllowedKeys(rawJson, new Set(['conversationId', 'focusInstruction']))) {
          return { ok: false, statusCode: 403, code: 'remote_endpoint_forbidden', message: 'Unexpected JSON field.' };
        }
        return { ok: true, rebuiltJson: JSON.stringify(rawJson) };
      }

      case 'gateway_session_patch': {
        if (!hasOnlyAllowedKeys(rawJson, new Set(['name', 'isArchived']))) {
          return { ok: false, statusCode: 403, code: 'remote_endpoint_forbidden', message: 'Only name and isArchived may be patched.' };
        }
        return { ok: true, rebuiltJson: JSON.stringify(rawJson) };
      }

      case 'gateway_conversation_patch': {
        if (!hasOnlyAllowedKeys(rawJson, new Set(['title', 'isArchived']))) {
          return { ok: false, statusCode: 403, code: 'remote_endpoint_forbidden', message: 'Only title and isArchived may be patched.' };
        }
        return { ok: true, rebuiltJson: JSON.stringify(rawJson) };
      }

      case 'settings_execution_put': {
        if (!hasOnlyAllowedKeys(rawJson, new Set(['permissionMode'])) || typeof rawJson.permissionMode !== 'string') {
          return { ok: false, statusCode: 403, code: 'remote_endpoint_forbidden', message: 'Only permissionMode is allowed on remote PUT /api/settings/execution.' };
        }
        return {
          ok: true,
          useControlTokenOverride: false,
          rebuiltJson: JSON.stringify({ permissionMode: rawJson.permissionMode }),
        };
      }

      case 'gateway_plan_mode_post': {
        if (!hasOnlyAllowedKeys(rawJson, new Set(['active', 'expectedPlanModeVersion'])) || typeof rawJson.active !== 'boolean') {
          return { ok: false, statusCode: 403, code: 'remote_endpoint_forbidden', message: 'Invalid plan-mode request body.' };
        }
        if (rawJson.active === false) {
          if (params.keyPurpose !== 'biometric_approval') {
            return { ok: false, statusCode: 403, code: 'biometric_key_required', message: 'Exiting Plan Mode requires biometric_approval key.' };
          }
          if (!Number.isInteger(rawJson.expectedPlanModeVersion) || (rawJson.expectedPlanModeVersion as number) < 1) {
            return { ok: false, statusCode: 400, code: 'expected_plan_version_required', message: 'Exiting Plan Mode requires positive expectedPlanModeVersion.' };
          }
          return {
            ok: true,
            useControlTokenOverride: true,
            rebuiltJson: JSON.stringify({
              active: false,
              expectedPlanModeVersion: rawJson.expectedPlanModeVersion,
            }),
          };
        }
        return {
          ok: true,
          useControlTokenOverride: false,
          rebuiltJson: JSON.stringify({
            active: true,
            ...(rawJson.expectedPlanModeVersion !== undefined ? { expectedPlanModeVersion: rawJson.expectedPlanModeVersion } : {}),
          }),
        };
      }

      case 'gateway_computer_emergency_stop': {
        if (
          !hasOnlyAllowedKeys(rawJson, new Set(['sessionId'])) ||
          typeof rawJson.sessionId !== 'string' ||
          !/^[A-Za-z0-9_-]{1,128}$/.test(rawJson.sessionId.trim())
        ) {
          return { ok: false, statusCode: 403, code: 'remote_endpoint_forbidden', message: 'Emergency stop requires valid sessionId.' };
        }
        return {
          ok: true,
          useControlTokenOverride: true,
          rebuiltJson: JSON.stringify({ sessionId: rawJson.sessionId.trim() }),
        };
      }

      case 'gateway_approvals_submit': {
        const approvalId = params.rawPath.split('/').pop() ?? '';
        const allowed = new Set(['requestDigest', 'decision', 'note', 'signedEnvelope']);
        if (!hasOnlyAllowedKeys(rawJson, allowed)) {
          return { ok: false, statusCode: 403, code: 'remote_endpoint_forbidden', message: 'Unexpected field on approval submission.' };
        }
        if (typeof rawJson.requestDigest !== 'string' || !rawJson.requestDigest) {
          return { ok: false, statusCode: 400, code: 'invalid_request_digest', message: 'requestDigest is required.' };
        }
        if (rawJson.decision !== 'allow_once' && rawJson.decision !== 'deny') {
          return { ok: false, statusCode: 403, code: 'decision_not_allowed', message: 'Remote approvals only permit allow_once or deny.' };
        }
        if (!isPlainRecord(rawJson.signedEnvelope)) {
          return { ok: false, statusCode: 403, code: 'missing_signed_envelope', message: 'signedEnvelope is required for remote approvals.' };
        }
        const env = rawJson.signedEnvelope;
        if (
          typeof env.sessionId !== 'string' ||
          typeof env.approvalKind !== 'string' ||
          typeof env.textSummarySha256 !== 'string' ||
          typeof env.payloadSha256 !== 'string' ||
          !Number.isInteger(env.expiresAtUnix) ||
          typeof env.sigB64U !== 'string'
        ) {
          return { ok: false, statusCode: 403, code: 'invalid_signed_envelope', message: 'Malformed approval signedEnvelope.' };
        }
        const envValid = verifyApprovalEnvelopeV1({
          desktopSpkiB64: params.desktopSpkiB64,
          deviceId: params.deviceId,
          bindingVersion: params.bindingVersion,
          sessionId: env.sessionId,
          approvalKind: env.approvalKind,
          approvalId,
          textSummarySha256: env.textSummarySha256,
          payloadSha256: env.payloadSha256,
          requestDigest: rawJson.requestDigest,
          expiresAtUnix: env.expiresAtUnix as number,
          sigB64U: env.sigB64U,
          nowUnix: params.nowSec,
        });
        if (!envValid) {
          return { ok: false, statusCode: 403, code: 'invalid_envelope_signature', message: 'CF-APPROVAL-ENV-V1 verification failed or expired.' };
        }
        const envHash = sha256HexLower(rawJson.requestDigest);
        return {
          ok: true,
          useControlTokenOverride: true,
          extraUpstreamHeaders: {
            'X-CF-Verified-Envelope-Kind': 'approval',
            'X-CF-Verified-Envelope-Hash': envHash,
          },
          rebuiltJson: JSON.stringify({
            requestDigest: rawJson.requestDigest,
            decision: rawJson.decision,
            ...(typeof rawJson.note === 'string' ? { note: rawJson.note } : {}),
          }),
        };
      }

      case 'gateway_chat_post': {
        if (!params.clientOpId) {
          return { ok: false, statusCode: 400, code: 'client_op_id_required', message: 'X-CF-Client-Op-Id is required for remote chat.' };
        }
        const chatAllowed = new Set([
          'sessionId',
          'message',
          'conversationId',
          'taskId',
          'replyId',
          'conversationMode',
          'attachments',
          'structuredAction',
          'signedEnvelope',
        ]);
        if (!hasOnlyAllowedKeys(rawJson, chatAllowed) || typeof rawJson.sessionId !== 'string' || !rawJson.sessionId) {
          return { ok: false, statusCode: 403, code: 'remote_endpoint_forbidden', message: 'Disallowed field or missing sessionId on remote chat.' };
        }

        let sanitizedAttachments: Array<Record<string, unknown>> | undefined;
        if (rawJson.attachments !== undefined) {
          if (!Array.isArray(rawJson.attachments)) {
            return { ok: false, statusCode: 400, code: 'invalid_attachments', message: 'attachments must be an array.' };
          }
          const attAllowed = new Set(['name', 'relativePath', 'sizeBytes', 'contentType', 'isWorkspaceReference']);
          sanitizedAttachments = [];
          for (const item of rawJson.attachments) {
            if (!isPlainRecord(item) || !hasOnlyAllowedKeys(item, attAllowed)) {
              return { ok: false, statusCode: 403, code: 'remote_endpoint_forbidden', message: 'Unexpected field inside chat attachment.' };
            }
            if (item.isWorkspaceReference === true) {
              return { ok: false, statusCode: 400, code: 'workspace_reference_forbidden', message: 'Remote chat cannot reference arbitrary workspace files.' };
            }
            sanitizedAttachments.push({
              name: item.name,
              relativePath: item.relativePath,
              sizeBytes: item.sizeBytes,
              ...(item.contentType !== undefined ? { contentType: item.contentType } : {}),
              isWorkspaceReference: false,
            });
          }
        }

        let useControlForChat = false;
        let sanitizedAction: Record<string, unknown> | undefined;
        let extraHeaders: Record<string, string> = { 'X-CF-Client-Op-Id': params.clientOpId };

        if (rawJson.structuredAction !== undefined && rawJson.structuredAction !== null) {
          if (!isPlainRecord(rawJson.structuredAction)) {
            return { ok: false, statusCode: 400, code: 'invalid_structured_action', message: 'structuredAction must be an object.' };
          }
          const actionAllowed = new Set([
            'action',
            'planArtifactId',
            'planVersion',
            'planHash',
            'idempotencyKey',
            'startExecution',
            'acceptExistingDependencyDebt',
            'approveDependencyUpgrades',
          ]);
          if (!hasOnlyAllowedKeys(rawJson.structuredAction, actionAllowed) || typeof rawJson.structuredAction.action !== 'string') {
            return { ok: false, statusCode: 403, code: 'remote_endpoint_forbidden', message: 'Disallowed field in structuredAction.' };
          }
          const actLower = rawJson.structuredAction.action.trim().toLowerCase();
          if (actLower === 'approve' || actLower === 'execute') {
            if (params.keyPurpose !== 'biometric_approval') {
              return { ok: false, statusCode: 403, code: 'biometric_key_required', message: 'Plan approve/execute requires biometric_approval key.' };
            }
            if (
              rawJson.structuredAction.acceptExistingDependencyDebt === true ||
              (Array.isArray(rawJson.structuredAction.approveDependencyUpgrades) &&
                rawJson.structuredAction.approveDependencyUpgrades.length > 0)
            ) {
              return {
                ok: false,
                statusCode: 403,
                code: 'remote_dependency_override_forbidden',
                message: 'Dependency debt/upgrade overrides are forbidden over remote channel.',
              };
            }
            if (!isPlainRecord(rawJson.signedEnvelope)) {
              return { ok: false, statusCode: 403, code: 'missing_signed_envelope', message: 'signedEnvelope required for plan approve/execute.' };
            }
            const env = rawJson.signedEnvelope;
            const planArtifactId = rawJson.structuredAction.planArtifactId;
            const planVersionRaw = rawJson.structuredAction.planVersion;
            const planHash = rawJson.structuredAction.planHash;
            const startExecution = rawJson.structuredAction.startExecution;

            if (
              typeof planArtifactId !== 'string' ||
              !planArtifactId ||
              typeof planVersionRaw !== 'string' ||
              planVersionRaw.length === 0 ||
              planVersionRaw.length > 128 ||
              /[\r\n\0]/.test(planVersionRaw) ||
              typeof planHash !== 'string' ||
              !isLowerHex(planHash, 64) ||
              typeof startExecution !== 'boolean' ||
              typeof env.allowedActionsCsv !== 'string' ||
              !Number.isInteger(env.expiresAtUnix) ||
              typeof env.sigB64U !== 'string'
            ) {
              return { ok: false, statusCode: 403, code: 'invalid_plan_envelope', message: 'Incomplete plan identity or signedEnvelope.' };
            }
            if (planArtifactId.trim().toLowerCase() === 'session-plan') {
              return { ok: false, statusCode: 403, code: 'unsupported_ephemeral_plan', message: 'Remote approval requires a persisted PlanArtifact.' };
            }
            const allowedCsvParts = env.allowedActionsCsv.split(',').map((s) => s.trim().toLowerCase());
            if (!allowedCsvParts.includes(actLower)) {
              return { ok: false, statusCode: 403, code: 'plan_action_not_in_envelope', message: 'Action is not allowed by signedEnvelope.' };
            }

            const planEnvValid = verifyPlanEnvelopeV1({
              desktopSpkiB64: params.desktopSpkiB64,
              deviceId: params.deviceId,
              bindingVersion: params.bindingVersion,
              sessionId: rawJson.sessionId,
              planArtifactId,
              planVersion: planVersionRaw,
              planMarkdownSha256: planHash,
              allowedActionsCsv: env.allowedActionsCsv,
              startExecution,
              expiresAtUnix: env.expiresAtUnix as number,
              sigB64U: env.sigB64U,
              nowUnix: params.nowSec,
            });
            if (!planEnvValid) {
              return { ok: false, statusCode: 403, code: 'invalid_plan_envelope_signature', message: 'CF-PLAN-ENV-V1 verification failed or expired.' };
            }

            useControlForChat = true;
            extraHeaders = {
              ...extraHeaders,
              'X-CF-Verified-Envelope-Kind': 'plan',
              'X-CF-Verified-Envelope-Hash': planHash.toLowerCase(),
            };
          }

          sanitizedAction = {
            ...rawJson.structuredAction,
            acceptExistingDependencyDebt: false,
            approveDependencyUpgrades: [],
          };
        }

        const rebuiltChat: Record<string, unknown> = {
          sessionId: rawJson.sessionId,
          message: typeof rawJson.message === 'string' ? rawJson.message : '',
          ...(rawJson.conversationId !== undefined ? { conversationId: rawJson.conversationId } : {}),
          ...(rawJson.taskId !== undefined ? { taskId: rawJson.taskId } : {}),
          ...(rawJson.replyId !== undefined ? { replyId: rawJson.replyId } : {}),
          ...(rawJson.conversationMode !== undefined ? { conversationMode: rawJson.conversationMode } : {}),
          ...(sanitizedAttachments !== undefined ? { attachments: sanitizedAttachments } : {}),
          ...(sanitizedAction !== undefined ? { structuredAction: sanitizedAction } : {}),
        };

        return {
          ok: true,
          useControlTokenOverride: useControlForChat,
          extraUpstreamHeaders: extraHeaders,
          rebuiltJson: JSON.stringify(rebuiltChat),
        };
      }

      default:
        return { ok: true, rebuiltJson: JSON.stringify(rawJson) };
    }
  }

  private signRemoteEnvelopesOnResponse(params: {
    policyId: string;
    rawPath: string;
    body: unknown;
    desktopPrivateKeyPem: string;
    deviceId: string;
    bindingVersion: number;
    nowSec: number;
  }): void {
    if (!isPlainRecord(params.body)) {
      return;
    }

    const extractSessionIdFromPath = (): string => {
      const m = /^\/api\/gateway\/session\/([^/]+)(?:\/|$)/.exec(params.rawPath);
      return m?.[1] ?? '';
    };

    const parseExpiresAtUnix = (raw: unknown): number | null => {
      if (typeof raw === 'string' && raw.trim()) {
        const ms = Date.parse(raw);
        if (Number.isFinite(ms)) {
          const sec = Math.floor(ms / 1000);
          return sec > params.nowSec ? sec : null;
        }
      }
      return params.nowSec + 600;
    };

    const strOrEmpty = (v: unknown): string => (typeof v === 'string' ? v : '');

    const trySignApproval = (input: {
      approvalKind: string;
      sessionId: string;
      approvalId: string;
      requestDigest: string;
      title: string;
      summary: string;
      payloadText: string;
      expiresAtUtc: unknown;
    }) => {
      if (
        !input.sessionId ||
        !input.approvalId ||
        !input.requestDigest ||
        /[\r\n\0]/.test(input.sessionId) ||
        /[\r\n\0]/.test(input.approvalId) ||
        /[\r\n\0]/.test(input.requestDigest)
      ) {
        return null;
      }
      const expiresAtUnix = parseExpiresAtUnix(input.expiresAtUtc);
      if (expiresAtUnix === null) {
        return null;
      }
      try {
        const canonicalText = buildApprovalCanonicalTextV1({
          approvalKind: input.approvalKind,
          approvalId: input.approvalId,
          sessionId: input.sessionId,
          title: sanitizeSingleLineSummaryText(input.title, 200) || input.approvalKind,
          summary: sanitizeSingleLineSummaryText(input.summary, 600) || input.approvalId,
        });
        const canonicalPayload = buildApprovalCanonicalPayloadV1({
          approvalKind: input.approvalKind,
          requestDigest: input.requestDigest,
          payloadText: sanitizeSingleLineSummaryText(input.payloadText, 4096),
        });
        return createSignedApprovalEnvelopeV1({
          desktopPrivateKeyPem: params.desktopPrivateKeyPem,
          deviceId: params.deviceId,
          bindingVersion: params.bindingVersion,
          sessionId: input.sessionId,
          approvalKind: input.approvalKind,
          approvalId: input.approvalId,
          textSummarySha256: sha256HexLower(canonicalText),
          payloadSha256: sha256HexLower(canonicalPayload),
          requestDigest: input.requestDigest,
          expiresAtUnix,
        });
      } catch {
        return null;
      }
    };

    switch (params.policyId) {
      case 'gateway_approvals_get': {
        const fallbackSessionId = strOrEmpty(params.body.sessionId) || extractSessionIdFromPath();
        if (Array.isArray(params.body.items)) {
          for (const rawItem of params.body.items) {
            if (!isPlainRecord(rawItem)) continue;
            const env = trySignApproval({
              approvalKind: 'command',
              sessionId: strOrEmpty(rawItem.sessionId) || fallbackSessionId,
              approvalId: strOrEmpty(rawItem.approvalId),
              requestDigest: strOrEmpty(rawItem.requestDigest),
              title: strOrEmpty(rawItem.tool) || strOrEmpty(rawItem.executable) || 'command',
              summary: [
                strOrEmpty(rawItem.executable),
                ...(Array.isArray(rawItem.argv) ? rawItem.argv.map((x) => String(x)) : []),
                strOrEmpty(rawItem.cwd) ? `@${strOrEmpty(rawItem.cwd)}` : '',
                strOrEmpty(rawItem.reason),
              ]
                .filter(Boolean)
                .join(' '),
              payloadText: JSON.stringify({
                tool: rawItem.tool ?? null,
                executable: rawItem.executable ?? null,
                argv: rawItem.argv ?? null,
                cwd: rawItem.cwd ?? null,
                reason: rawItem.reason ?? null,
                scriptPreview: rawItem.scriptPreview ?? null,
              }),
              expiresAtUtc: rawItem.expiresAtUtc,
            });
            if (env) {
              rawItem.signedEnvelope = env;
            }
          }
        }
        if (Array.isArray(params.body.businessApprovals)) {
          for (const rawItem of params.body.businessApprovals) {
            if (!isPlainRecord(rawItem)) continue;
            const sumObj = isPlainRecord(rawItem.summary) ? rawItem.summary : null;
            const env = trySignApproval({
              approvalKind: 'business',
              sessionId: strOrEmpty(rawItem.sessionId) || fallbackSessionId,
              approvalId: strOrEmpty(rawItem.approvalId),
              requestDigest: strOrEmpty(rawItem.requestDigest),
              title:
                (sumObj ? strOrEmpty(sumObj.title) : '') ||
                strOrEmpty(rawItem.kind) ||
                'business',
              summary:
                (sumObj ? strOrEmpty(sumObj.summary) : strOrEmpty(rawItem.summary)) ||
                strOrEmpty(rawItem.kind),
              payloadText: JSON.stringify({
                kind: rawItem.kind ?? null,
                summary: rawItem.summary ?? null,
              }),
              expiresAtUtc: rawItem.expiresAtUtc,
            });
            if (env) {
              rawItem.signedEnvelope = env;
            }
          }
        }
        if (Array.isArray(params.body.browserApprovals)) {
          for (const rawItem of params.body.browserApprovals) {
            if (!isPlainRecord(rawItem)) continue;
            const env = trySignApproval({
              approvalKind: 'browser',
              sessionId: strOrEmpty(rawItem.sessionId) || fallbackSessionId,
              approvalId: strOrEmpty(rawItem.approvalId),
              requestDigest: strOrEmpty(rawItem.requestDigest),
              title: strOrEmpty(rawItem.action) || strOrEmpty(rawItem.tool) || 'browser',
              summary: [
                strOrEmpty(rawItem.targetUrl),
                strOrEmpty(rawItem.targetDescription),
                strOrEmpty(rawItem.riskReason),
              ]
                .filter(Boolean)
                .join(' | '),
              payloadText: JSON.stringify({
                tool: rawItem.tool ?? null,
                action: rawItem.action ?? null,
                targetUrl: rawItem.targetUrl ?? null,
                targetDescription: rawItem.targetDescription ?? null,
                riskReason: rawItem.riskReason ?? null,
              }),
              expiresAtUtc: rawItem.expiresAtUtc,
            });
            if (env) {
              rawItem.signedEnvelope = env;
            }
          }
        }
        if (Array.isArray(params.body.computerApprovals)) {
          for (const rawItem of params.body.computerApprovals) {
            if (!isPlainRecord(rawItem)) continue;
            const env = trySignApproval({
              approvalKind: 'computer',
              sessionId: strOrEmpty(rawItem.sessionId) || fallbackSessionId,
              approvalId: strOrEmpty(rawItem.approvalId),
              requestDigest: strOrEmpty(rawItem.requestDigest),
              title: strOrEmpty(rawItem.action) || strOrEmpty(rawItem.tool) || 'computer',
              summary: [
                strOrEmpty(rawItem.targetProcessName),
                strOrEmpty(rawItem.targetWindowTitle),
                strOrEmpty(rawItem.actionSummary),
                strOrEmpty(rawItem.riskReason),
              ]
                .filter(Boolean)
                .join(' | '),
              payloadText: JSON.stringify({
                tool: rawItem.tool ?? null,
                action: rawItem.action ?? null,
                targetProcessName: rawItem.targetProcessName ?? null,
                targetWindowTitle: rawItem.targetWindowTitle ?? null,
                actionSummary: rawItem.actionSummary ?? null,
                riskReason: rawItem.riskReason ?? null,
              }),
              expiresAtUtc: rawItem.expiresAtUtc,
            });
            if (env) {
              rawItem.signedEnvelope = env;
            }
          }
        }
        break;
      }

      case 'gateway_approval_script': {
        const approvalId = strOrEmpty(params.body.approvalId);
        const sessionId = strOrEmpty(params.body.sessionId);
        const requestDigest = strOrEmpty(params.body.requestDigest);
        const shell = strOrEmpty(params.body.shell) || 'shell';
        const scriptSha256 = strOrEmpty(params.body.scriptSha256);
        const scriptText = typeof params.body.scriptText === 'string' ? params.body.scriptText : '';
        const env = trySignApproval({
          approvalKind: 'command',
          sessionId,
          approvalId,
          requestDigest,
          title: `script:${shell}`,
          summary: `scriptSha256:${scriptSha256}`,
          payloadText: scriptText,
          expiresAtUtc: params.body.expiresAtUtc,
        });
        if (env) {
          params.body.signedEnvelope = env;
        }
        break;
      }

      case 'gateway_events_summary': {
        if (params.body.bindingVersion === undefined || params.body.bindingVersion === null) {
          params.body.bindingVersion = params.bindingVersion;
        }
        if (Array.isArray(params.body.items)) {
          for (const rawItem of params.body.items) {
            if (!isPlainRecord(rawItem)) continue;
            if (rawItem.readOnlyInRemote === true || rawItem.isTerminal === true) {
              continue;
            }
            const rawKind = strOrEmpty(rawItem.sourceKind).trim().toLowerCase();
            const mappedKind =
              rawKind === 'execution' || rawKind === 'command'
                ? 'command'
                : rawKind === 'business' || rawKind === 'browser' || rawKind === 'computer'
                  ? rawKind
                  : null;
            if (!mappedKind) {
              continue;
            }
            const approvalId = strOrEmpty(rawItem.approvalId);
            const requestDigest = strOrEmpty(rawItem.requestDigest);
            const sessionId = strOrEmpty(rawItem.sessionId);
            if (!approvalId || !requestDigest || !sessionId) {
              continue;
            }
            const env = trySignApproval({
              approvalKind: mappedKind,
              sessionId,
              approvalId,
              requestDigest,
              title: strOrEmpty(rawItem.title) || mappedKind,
              summary: strOrEmpty(rawItem.summary) || approvalId,
              payloadText: JSON.stringify({
                sourceKind: rawItem.sourceKind ?? null,
                approvalId,
                sessionId,
                title: rawItem.title ?? null,
                summary: rawItem.summary ?? null,
              }),
              expiresAtUtc: rawItem.expiresAtUtc,
            });
            if (env) {
              rawItem.signedEnvelope = env;
            }
          }
        }
        break;
      }

      case 'gateway_plan_get': {
        const sessionId = extractSessionIdFromPath();
        const planArtifactId = strOrEmpty(params.body.planArtifactId).trim();
        const planVersion = strOrEmpty(params.body.version).trim();
        const markdown = typeof params.body.markdown === 'string' ? params.body.markdown : '';
        if (
          !sessionId ||
          !planArtifactId ||
          planArtifactId.toLowerCase() === 'session-plan' ||
          !planVersion ||
          /[\r\n\0]/.test(planVersion) ||
          !markdown ||
          params.body.hostRefusal
        ) {
          break;
        }
        const rawHash = strOrEmpty(params.body.markdownHash).toLowerCase();
        const planMarkdownSha256 = isLowerHex(rawHash, 64) ? rawHash : sha256HexLower(markdown);
        const expiresAtUnix = params.nowSec + 600;
        try {
          params.body.signedEnvelope = createSignedPlanEnvelopeV1({
            desktopPrivateKeyPem: params.desktopPrivateKeyPem,
            deviceId: params.deviceId,
            bindingVersion: params.bindingVersion,
            sessionId,
            planArtifactId,
            planVersion,
            planMarkdownSha256,
            allowedActionsCsv: 'approve,execute',
            startExecution: true,
            expiresAtUnix,
          });
          params.body.signedEnvelopeApproveOnly = createSignedPlanEnvelopeV1({
            desktopPrivateKeyPem: params.desktopPrivateKeyPem,
            deviceId: params.deviceId,
            bindingVersion: params.bindingVersion,
            sessionId,
            planArtifactId,
            planVersion,
            planMarkdownSha256,
            allowedActionsCsv: 'approve',
            startExecution: false,
            expiresAtUnix,
          });
        } catch {
          // Skip invalid plan envelope inputs rather than failing read response
        }
        break;
      }

      default:
        break;
    }
  }
}
