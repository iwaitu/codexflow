import { createHmac, randomBytes, timingSafeEqual } from 'node:crypto';
import { hostname } from 'node:os';
import {
  TUNNEL_MAX_PAYLOAD_BYTES,
  TunnelFrameFlags,
  TunnelFrameType,
  StreamRstErrorCode,
  buildPairingQrUri,
  bytesToBase64Url,
  deserializeJsonPayload,
  encodeJsonFrame,
  encodePingOrPong,
  encodeStreamRst,
  encodeWindowUpdate,
  isLowerHex,
  tryDecodeFrame,
  tryDecodePingOrPong,
  tryDecodeStreamRst,
  tryDecodeWindowUpdate,
  encodeFrame,
  type HelloAckPayload,
  type HelloPayload,
  type HostEventPayload,
  type HostStatePayload,
  type PairAckDto,
  type PairRequestPayload,
  type ReqStartPayload,
  type ResHeadPayload,
  type RevokeNoticePayload,
  type SessionEpochCertDto,
  type TunnelFrame,
} from '@codexflow-shared/remoteProtocol';
import type { RemoteDispatchTarget } from '../backendManager';
import {
  RemoteConfigStore,
  type RemoteConfigPublicSnapshot,
  type RemoteProxyMode,
  type StoredRemotePairing,
} from './remoteConfigStore';
import {
  computeSas6Digits,
  createSignedEpochCert,
  createSignedPairAck,
  sha256HexLower,
  verifyPairRequestCryptographicIntegrity,
} from './remoteCrypto';
import {
  RemoteDialController,
  type DialConnectionState,
  type RemoteDialControllerOptions,
  type RemoteDialStatusSnapshot,
  type WsTransportSession,
} from './remoteDialController';
import { RemoteHostDispatcher } from './remoteHostDispatcher';
import { RemoteUploadStaging } from './remoteUploadStaging';
import { ReplayNonceGuard } from './replayNonceGuard';

const DEFAULT_HELLO_ACK_TIMEOUT_MS = 10_000;
const DEFAULT_PING_INTERVAL_MS = 25_000;
const DEFAULT_EPOCH_ROTATE_INTERVAL_MS = 30 * 60 * 1000;
const DEFAULT_EPOCH_TTL_SECONDS = 3600;
const DEFAULT_HOST_EVENT_POLL_INTERVAL_MS = 2_500;
const PORTAL_INITIAL_DOWNSTREAM_WINDOW_BYTES = 64 * 1024;
const PORTAL_INITIAL_DOWNSTREAM_WINDOW_CHUNKS = 256;
const MAX_PENDING_PAIR_PROMPTS = 8;

type SupportedDesktopPlatform = 'win32' | 'darwin' | 'linux';

export interface PendingPairPromptView {
  readonly pairingId: string;
  readonly ticketId: string;
  readonly mobileId: string;
  readonly platform: string;
  readonly deviceName: string;
  readonly sas6: string;
  readonly expiresAtUnix: number;
  readonly receivedAtUtc: string;
}

export interface RemoteManagerStatusSnapshot {
  readonly config: RemoteConfigPublicSnapshot;
  readonly dial: RemoteDialStatusSnapshot;
  readonly connEpoch: number | null;
  readonly activeEpochExpiresAtUnix: number | null;
  readonly lastPongAtUtc: string | null;
  readonly pendingPairRequests: PendingPairPromptView[];
  readonly activeStreamCount: number;
  readonly safeStorageAvailable: boolean;
  readonly lastHostEventId: number;
}

export interface RemoteSendOtpResult {
  readonly message: string;
}

export type RemoteBindAndVerifyResult =
  | {
      readonly status: 'bound';
      readonly snapshot: RemoteManagerStatusSnapshot;
    }
  | {
      readonly status: 'requires_account_switch_confirmation';
      readonly previousAccountEmail: string | null;
      readonly nextUserId: string;
    };

export interface RemoteQrTicketResult {
  readonly ticketId: string;
  readonly qrBootstrapToken: string;
  readonly bindingVersion: number;
  readonly expiresAt: string;
  readonly qrUri: string;
}

export interface RemoteWorkspaceAuthorizationEntry {
  readonly workspaceRealPath: string;
  readonly sessionId: string | null;
  readonly authorizedAtUtc: string;
}

export interface RemoteTunnelManagerDeps {
  readonly configStore: RemoteConfigStore;
  readonly nonceGuard: ReplayNonceGuard;
  readonly uploadStaging: RemoteUploadStaging;
  readonly getDispatchTarget: () => RemoteDispatchTarget | null;
  readonly getDataDirectory: () => string;
  readonly switchAccountDataDirectory: (userIdHash: string | null) => Promise<unknown>;
  readonly resolveSystemProxy?: (targetUrl: string) => Promise<string | null>;
  readonly dialWebSocket?: RemoteDialControllerOptions['dialWebSocket'];
  readonly fetchFn?: typeof fetch;
  readonly getDeviceName?: () => string;
  readonly getPlatform?: () => SupportedDesktopPlatform;
  readonly nowMs?: () => number;
  readonly onStateChanged?: (snapshot: RemoteManagerStatusSnapshot) => void;
  readonly helloAckTimeoutMs?: number;
  readonly pingIntervalMs?: number;
  readonly epochRotateIntervalMs?: number;
  readonly hostEventPollIntervalMs?: number;
}

type PendingDesktopIdentity = ReturnType<RemoteConfigStore['preparePendingDesktopIdentity']>;

interface PendingBindTokenCache {
  readonly email: string;
  readonly token: string;
  readonly userId: string;
  readonly expiresAtMs: number;
  readonly identity: PendingDesktopIdentity;
}

interface PendingPairEntry {
  readonly payload: PairRequestPayload;
  readonly sessionKeyId: string;
  readonly bioKeyId: string | null;
  readonly sas6: string;
  readonly receivedAtUtc: string;
  readonly expiryTimer: NodeJS.Timeout;
}

interface InternalHostEventRecord {
  readonly eventId: number;
  readonly summaryEpoch: number;
  readonly sourceKind: string;
  readonly opaqueRefId: string;
  readonly sessionId: string;
  readonly status: string;
  readonly isTerminal: boolean;
  readonly summary: string;
  readonly createdAtUtc: string;
  readonly expiresAtUtc: string;
  readonly resolvedReason?: string | null;
}

class AsyncByteQueue implements AsyncIterable<Uint8Array> {
  private readonly chunks: Uint8Array[] = [];
  private ended = false;
  private failure: Error | null = null;
  private waiter: (() => void) | null = null;
  private readonly onConsume?: (byteLength: number) => void;

  constructor(onConsume?: (byteLength: number) => void) {
    this.onConsume = onConsume;
  }

  public push(chunk: Uint8Array): void {
    if (this.ended || this.failure) {
      return;
    }
    if (chunk.byteLength > 0) {
      this.chunks.push(chunk);
    }
    this.wake();
  }

  public end(): void {
    if (!this.ended) {
      this.ended = true;
      this.wake();
    }
  }

  public fail(err: Error): void {
    if (!this.failure && !this.ended) {
      this.failure = err;
      this.wake();
    }
  }

  private wake(): void {
    const w = this.waiter;
    this.waiter = null;
    if (w) {
      w();
    }
  }

  public [Symbol.asyncIterator](): AsyncIterator<Uint8Array> {
    return {
      next: async (): Promise<IteratorResult<Uint8Array>> => {
        while (true) {
          if (this.chunks.length > 0) {
            const nextChunk = this.chunks.shift()!;
            this.onConsume?.(nextChunk.byteLength);
            return { done: false, value: nextChunk };
          }
          if (this.failure) {
            throw this.failure;
          }
          if (this.ended) {
            return { done: true, value: undefined };
          }
          await new Promise<void>((resolve) => {
            this.waiter = resolve;
          });
        }
      },
    };
  }
}

interface ActiveRelayStream {
  readonly streamId: number;
  readonly mobileId: string;
  readonly abortController: AbortController;
  readonly reqBodyQueue: AsyncByteQueue;
  reqBodyEnded: boolean;
  bytesCredit: number;
  chunksCredit: number;
  readonly creditWaiters: Set<() => void>;
}

function normalizePlatform(raw?: string): SupportedDesktopPlatform {
  const val = (raw ?? process.platform).toLowerCase();
  if (val === 'win32' || val === 'darwin' || val === 'linux') {
    return val;
  }
  return 'linux';
}

function resolveHeaderValue(headers: ReadonlyArray<{ name: string; value: string }>, targetName: string): string {
  const targetLower = targetName.toLowerCase();
  for (const h of headers) {
    if (h.name.toLowerCase() === targetLower) {
      return h.value;
    }
  }
  return '';
}

function extractPortalErrorMessage(bodyText: string, status: number): string {
  if (!bodyText) {
    return `Portal returned HTTP ${status}.`;
  }
  try {
    const parsed = JSON.parse(bodyText) as {
      error?: string;
      message?: string;
      detail?: string;
      title?: string;
    };
    const code = parsed.error ?? parsed.title ?? '';
    const msg = parsed.message ?? parsed.detail ?? bodyText;
    return code ? `${code}: ${msg}` : msg;
  } catch {
    return bodyText.slice(0, 240);
  }
}

function defaultDeviceName(): string {
  try {
    const h = hostname().trim();
    return h ? h.slice(0, 120) : 'CodexFlow Desktop';
  } catch {
    return 'CodexFlow Desktop';
  }
}

export class RemoteTunnelManager {
  private readonly deps: RemoteTunnelManagerDeps;
  private readonly fetchImpl: typeof fetch;
  private readonly nowMs: () => number;
  private readonly helloAckTimeoutMs: number;
  private readonly pingIntervalMs: number;
  private readonly epochRotateIntervalMs: number;
  private readonly hostEventPollIntervalMs: number;

  private readonly dialController: RemoteDialController;
  private readonly dispatcher: RemoteHostDispatcher;

  private readonly pendingIdentities = new Map<string, PendingDesktopIdentity>();
  private readonly pendingBindTokens = new Map<string, PendingBindTokenCache>();
  private readonly pendingPairPrompts = new Map<string, PendingPairEntry>();
  private readonly issuedQrSecrets = new Map<string, { secretBytes: Buffer; expiresAtMs: number }>();
  private readonly activeStreams = new Map<number, ActiveRelayStream>();

  private epochCerts: SessionEpochCertDto[] = [];
  private activeSession: WsTransportSession | null = null;
  private activeGeneration = 0;
  private activeConnEpoch: number | null = null;
  private helloAckReceived = false;
  private lastPongAtUtc: string | null = null;
  private lastHostEventId = 0;
  private lastHostSummaryEpoch: number | null = null;
  private hostEventPollInFlight = false;

  private helloAckTimer: NodeJS.Timeout | null = null;
  private pingTimer: NodeJS.Timeout | null = null;
  private epochRotateTimer: NodeJS.Timeout | null = null;
  private hostEventPollTimer: NodeJS.Timeout | null = null;
  private sessionSettler: ((result: { terminalCloseState?: DialConnectionState; message?: string }) => void) | null =
    null;

  constructor(deps: RemoteTunnelManagerDeps) {
    this.deps = deps;
    this.fetchImpl = deps.fetchFn ?? fetch;
    this.nowMs = deps.nowMs ?? (() => Date.now());
    this.helloAckTimeoutMs = deps.helloAckTimeoutMs ?? DEFAULT_HELLO_ACK_TIMEOUT_MS;
    this.pingIntervalMs = deps.pingIntervalMs ?? DEFAULT_PING_INTERVAL_MS;
    this.epochRotateIntervalMs = deps.epochRotateIntervalMs ?? DEFAULT_EPOCH_ROTATE_INTERVAL_MS;
    this.hostEventPollIntervalMs = deps.hostEventPollIntervalMs ?? DEFAULT_HOST_EVENT_POLL_INTERVAL_MS;

    this.dispatcher = new RemoteHostDispatcher({
      configStore: deps.configStore,
      nonceGuard: deps.nonceGuard,
      uploadStaging: deps.uploadStaging,
      getDispatchTarget: deps.getDispatchTarget,
      getValidEpochCerts: () => this.getValidEpochCerts(),
      upstreamFetch: this.fetchImpl,
      nowMs: this.nowMs,
    });

    this.dialController = new RemoteDialController({
      resolveSystemProxy: deps.resolveSystemProxy,
      dialWebSocket: deps.dialWebSocket,
      getDialParams: () => {
        const snap = this.deps.configStore.getPublicSnapshot();
        const proxy = this.deps.configStore.getProxySettings();
        return {
          portalUrl: snap.portalUrl,
          deviceToken: this.deps.configStore.getDeviceToken(),
          manualPaused: snap.manualRemotePaused,
          proxyMode: proxy.mode,
          customProxyUrl: proxy.customUrl,
          proxyUsername: proxy.username,
          proxyPassword: proxy.password,
        };
      },
      onSessionConnected: (transport, generation) => this.handleSessionConnected(transport, generation),
      onStateChanged: () => this.emitSnapshot(),
    });
  }

  public getStatusSnapshot(): RemoteManagerStatusSnapshot {
    const config = this.deps.configStore.getPublicSnapshot();
    const validCerts = this.getValidEpochCerts();
    const newestCert = validCerts.length > 0 ? validCerts[validCerts.length - 1] : undefined;

    return {
      config,
      dial: this.dialController.getStatus(),
      connEpoch: this.activeConnEpoch,
      activeEpochExpiresAtUnix: newestCert?.expiresAtUnix ?? null,
      lastPongAtUtc: this.lastPongAtUtc,
      pendingPairRequests: Array.from(this.pendingPairPrompts.values()).map((entry) => ({
        pairingId: entry.payload.pairingId,
        ticketId: entry.payload.ticketId,
        mobileId: entry.payload.mobileId,
        platform: entry.payload.platform,
        deviceName: entry.payload.deviceName,
        sas6: entry.sas6,
        expiresAtUnix: entry.payload.expiresAtUnix,
        receivedAtUtc: entry.receivedAtUtc,
      })),
      activeStreamCount: this.activeStreams.size,
      safeStorageAvailable: this.deps.configStore.isEncryptionAvailable(),
      lastHostEventId: this.lastHostEventId,
    };
  }

  public async start(): Promise<void> {
    this.deps.uploadStaging.cleanupOrphansOnStartup();

    const snap = this.deps.configStore.getPublicSnapshot();
    if (snap.bound && snap.account) {
      await this.deps.switchAccountDataDirectory(snap.account.userIdHash);
    }

    await this.pushLocalRevocationsBestEffort();

    if (snap.bound && snap.portalUrl && !snap.manualRemotePaused) {
      this.dialController.startOrReconnect();
    } else if (snap.manualRemotePaused) {
      this.dialController.setManualPaused(true);
    }

    this.emitSnapshot();
  }

  public stop(): void {
    this.abortAndFinishSession({ terminalCloseState: undefined, message: 'Stopped by application.' });
    this.clearPendingPairPrompts();
    this.issuedQrSecrets.clear();
    this.dialController.stop('Stopped by application.');
    this.emitSnapshot();
  }

  public handlePowerSuspend(): void {
    this.abortAndFinishSession({ terminalCloseState: undefined, message: 'System suspended.' });
    this.dialController.handlePowerSuspend();
    this.emitSnapshot();
  }

  public handlePowerResume(): void {
    this.dialController.handlePowerResume();
    this.emitSnapshot();
  }

  public async notifyBackendReady(): Promise<void> {
    await this.pushLocalRevocationsBestEffort();
    if (this.helloAckReceived && this.activeSession) {
      await this.pollAndForwardHostEvents();
    }
  }

  public setPortalUrl(rawUrl: string): RemoteManagerStatusSnapshot {
    this.deps.configStore.setPortalUrl(rawUrl);
    const snap = this.deps.configStore.getPublicSnapshot();
    if (snap.bound && !snap.manualRemotePaused) {
      this.dialController.startOrReconnect();
    }
    this.emitSnapshot();
    return this.getStatusSnapshot();
  }

  public setManualPaused(paused: boolean): RemoteManagerStatusSnapshot {
    this.deps.configStore.setManualPaused(paused);
    if (paused) {
      this.abortAndFinishSession({ terminalCloseState: undefined, message: 'Paused by user.' });
    }
    this.dialController.setManualPaused(paused);
    this.emitSnapshot();
    return this.getStatusSnapshot();
  }

  public setProxySettings(params: {
    mode: RemoteProxyMode;
    customUrl?: string;
    username?: string;
    password?: string | null;
    clearPassword?: boolean;
  }): RemoteManagerStatusSnapshot {
    this.deps.configStore.setProxySettings(params);
    const snap = this.deps.configStore.getPublicSnapshot();
    if (snap.bound && snap.portalUrl && !snap.manualRemotePaused) {
      this.dialController.startOrReconnect();
    }
    this.emitSnapshot();
    return this.getStatusSnapshot();
  }

  public async sendBindOtp(rawEmail: string, deviceName?: string): Promise<RemoteSendOtpResult> {
    const portalUrl = this.requirePortalUrl();
    const email = normalizeEmail(rawEmail);
    const resolvedName = (deviceName ?? '').trim() || (this.deps.getDeviceName?.() ?? defaultDeviceName());
    const platform = normalizePlatform(this.deps.getPlatform?.());

    const identity = this.deps.configStore.preparePendingDesktopIdentity({
      deviceName: resolvedName,
      platform,
      dataDirectory: this.deps.getDataDirectory(),
    });
    this.pendingIdentities.set(email, identity);
    this.pendingBindTokens.delete(email);

    const res = await this.fetchImpl(`${portalUrl}/api/auth/otp/send`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        email,
        purpose: 'desktop_bind',
        desktopDeviceId: identity.deviceId,
        desktopSpkiSha256: identity.desktopSpkiSha256,
      }),
    });

    const bodyText = await res.text();
    if (!res.ok) {
      throw new Error(extractPortalErrorMessage(bodyText, res.status));
    }

    let message = 'OTP sent.';
    if (bodyText) {
      try {
        const parsed = JSON.parse(bodyText) as { message?: string };
        if (parsed.message) {
          message = parsed.message;
        }
      } catch {
        // use fallback message
      }
    }
    return { message };
  }

  public async verifyBindOtpAndBind(params: {
    email: string;
    code: string;
    deviceName?: string;
    confirmAccountSwitch?: boolean;
  }): Promise<RemoteBindAndVerifyResult> {
    const portalUrl = this.requirePortalUrl();
    const email = normalizeEmail(params.email);
    const now = this.nowMs();
    const resolvedName = (params.deviceName ?? '').trim() || (this.deps.getDeviceName?.() ?? defaultDeviceName());
    const platform = normalizePlatform(this.deps.getPlatform?.());

    let cached = this.pendingBindTokens.get(email);
    if (cached && cached.expiresAtMs - now < 5000) {
      this.pendingBindTokens.delete(email);
      cached = undefined;
    }

    let token: string;
    let userId: string;
    let identity: PendingDesktopIdentity;

    if (cached && params.confirmAccountSwitch) {
      token = cached.token;
      userId = cached.userId;
      identity = cached.identity;
    } else {
      identity =
        this.pendingIdentities.get(email) ??
        this.deps.configStore.preparePendingDesktopIdentity({
          deviceName: resolvedName,
          platform,
          dataDirectory: this.deps.getDataDirectory(),
        });

      const verifyRes = await this.fetchImpl(`${portalUrl}/api/auth/otp/verify`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          email,
          purpose: 'desktop_bind',
          code: params.code.trim(),
          desktopDeviceId: identity.deviceId,
          desktopSpkiSha256: identity.desktopSpkiSha256,
        }),
      });

      const verifyText = await verifyRes.text();
      if (!verifyRes.ok) {
        throw new Error(extractPortalErrorMessage(verifyText, verifyRes.status));
      }

      const verifyJson = JSON.parse(verifyText) as {
        purpose: string;
        token: string;
        expiresAt: string;
        userId: string;
      };
      if (!verifyJson.token || !verifyJson.userId) {
        throw new Error('Malformed otp/verify response from Portal.');
      }

      token = verifyJson.token;
      userId = verifyJson.userId;
      const expiresAtMs = Date.parse(verifyJson.expiresAt);
      const effectiveExpMs = Number.isFinite(expiresAtMs) ? expiresAtMs : now + 55_000;

      if (this.deps.configStore.willSwitchAccountOwner(userId) && !params.confirmAccountSwitch) {
        this.pendingBindTokens.set(email, {
          email,
          token,
          userId,
          expiresAtMs: effectiveExpMs,
          identity,
        });
        const currentSnap = this.deps.configStore.getPublicSnapshot();
        return {
          status: 'requires_account_switch_confirmation',
          previousAccountEmail: currentSnap.account?.accountEmail ?? null,
          nextUserId: userId,
        };
      }
    }

    this.pendingBindTokens.delete(email);
    this.pendingIdentities.delete(email);

    // Stop active remote tunnel before switching isolated account directory.
    this.abortAndFinishSession({ terminalCloseState: undefined, message: 'Binding desktop account.' });
    this.dialController.stop('Binding desktop account.');
    this.clearPendingPairPrompts();

    const userIdHash = this.deps.configStore.computeUserIdHash(userId);
    await this.deps.switchAccountDataDirectory(userIdHash);
    const activeDataDir = this.deps.getDataDirectory();
    const effectiveDataDirHash = sha256HexLower(activeDataDir.toLowerCase());

    const bindRes = await this.fetchImpl(`${portalUrl}/api/devices/desktop/bind`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify({
        deviceId: identity.deviceId,
        deviceName: resolvedName,
        platform,
        hostInstanceId: identity.hostInstanceId,
        dataDirHash: effectiveDataDirHash,
        desktopSpkiB64: identity.desktopSpkiB64,
      }),
    });

    const bindText = await bindRes.text();
    if (!bindRes.ok) {
      throw new Error(extractPortalErrorMessage(bindText, bindRes.status));
    }

    const bindJson = JSON.parse(bindText) as {
      deviceId: string;
      userId: string;
      bindingVersion: number;
      revocationSeq: number;
      deviceToken: string;
      desktopKeyId: string;
      desktopSpkiSha256: string;
    };

    this.deps.configStore.commitDesktopBinding({
      portalUrl,
      userId: bindJson.userId,
      accountEmail: email,
      deviceId: bindJson.deviceId,
      deviceName: resolvedName,
      hostInstanceId: identity.hostInstanceId,
      bindingVersion: bindJson.bindingVersion,
      dataDirHash: effectiveDataDirHash,
      desktopSpkiB64: identity.desktopSpkiB64,
      desktopKeyId: bindJson.desktopKeyId,
      desktopSpkiSha256: bindJson.desktopSpkiSha256,
      desktopPrivateKeyPem: identity.desktopPrivateKeyPem,
      deviceToken: bindJson.deviceToken,
      revocationSeq: bindJson.revocationSeq,
    });

    this.lastHostEventId = 0;
    this.lastHostSummaryEpoch = null;
    this.issuedQrSecrets.clear();
    await this.pushLocalRevocationsBestEffort();

    if (!this.deps.configStore.getManualPaused()) {
      this.dialController.startOrReconnect();
    }

    this.emitSnapshot();
    return {
      status: 'bound',
      snapshot: this.getStatusSnapshot(),
    };
  }

  public async issuePairingQrTicket(options?: {
    reviewSessionId?: string;
  }): Promise<RemoteQrTicketResult> {
    const portalUrl = this.requirePortalUrl();
    const snap = this.deps.configStore.getPublicSnapshot();
    const deviceToken = this.deps.configStore.getDeviceToken();
    if (!snap.bound || !snap.device || !deviceToken) {
      throw new Error('Desktop device is not bound to a Portal account.');
    }

    const res = await this.fetchImpl(
      `${portalUrl}/api/devices/desktop/${encodeURIComponent(snap.device.deviceId)}/qr-ticket`,
      {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${deviceToken}`,
        },
        body: JSON.stringify(
          options?.reviewSessionId ? { reviewSessionId: options.reviewSessionId } : {},
        ),
      },
    );

    const bodyText = await res.text();
    if (!res.ok) {
      throw new Error(extractPortalErrorMessage(bodyText, res.status));
    }

    const parsed = JSON.parse(bodyText) as {
      ticketId: string;
      qrBootstrapToken: string;
      bindingVersion: number;
      expiresAt: string;
    };

    const nowMs = this.nowMs();
    for (const [tid, entry] of this.issuedQrSecrets) {
      if (entry.expiresAtMs <= nowMs) {
        this.issuedQrSecrets.delete(tid);
      }
    }
    while (this.issuedQrSecrets.size >= 32) {
      const oldestKey = this.issuedQrSecrets.keys().next().value;
      if (!oldestKey) {
        break;
      }
      this.issuedQrSecrets.delete(oldestKey);
    }

    const secretBytes = randomBytes(32);
    const pairingSecretB64U = bytesToBase64Url(secretBytes);
    const parsedExpMs = Date.parse(parsed.expiresAt);
    const expiresAtMs = Number.isFinite(parsedExpMs) ? parsedExpMs : nowMs + 120_000;
    this.issuedQrSecrets.set(parsed.ticketId.toLowerCase(), {
      secretBytes,
      expiresAtMs,
    });

    const qrUri = buildPairingQrUri({
      ticketId: parsed.ticketId,
      qrBootstrapToken: parsed.qrBootstrapToken,
      deviceId: snap.device.deviceId,
      bindingVersion: parsed.bindingVersion,
      desktopSpkiSha256: snap.device.desktopSpkiSha256,
      pairingSecretB64U,
      reviewSessionId: options?.reviewSessionId ?? null,
    });

    return {
      ticketId: parsed.ticketId,
      qrBootstrapToken: parsed.qrBootstrapToken,
      bindingVersion: parsed.bindingVersion,
      expiresAt: parsed.expiresAt,
      qrUri,
    };
  }

  public async respondToPairRequest(
    pairingId: string,
    approved: boolean,
  ): Promise<RemoteManagerStatusSnapshot> {
    const entry = this.pendingPairPrompts.get(pairingId.toLowerCase());
    if (!entry) {
      throw new Error('Pending pairing request not found or already expired.');
    }

    const nowSec = Math.floor(this.nowMs() / 1000);
    if (entry.payload.expiresAtUnix <= nowSec) {
      this.removePendingPairEntry(pairingId);
      this.emitSnapshot();
      throw new Error('Pairing request has already expired.');
    }

    const snap = this.deps.configStore.getPublicSnapshot();
    const privKeyPem = this.deps.configStore.getDesktopPrivateKeyPem();
    if (!snap.bound || !snap.device || !privKeyPem) {
      throw new Error('Desktop binding identity is unavailable.');
    }

    if (!this.activeSession || !this.helloAckReceived || !this.activeConnEpoch) {
      throw new Error('Remote tunnel is not currently connected to Portal.');
    }

    clearTimeout(entry.expiryTimer);
    this.pendingPairPrompts.delete(pairingId.toLowerCase());

    if (approved) {
      const signedAck = createSignedPairAck({
        pairingId: entry.payload.pairingId,
        pairRecordV1Text: entry.payload.pairRecordV1Text,
        acceptedAtUnix: nowSec,
        desktopPrivateKeyPem: privKeyPem,
      });

      const stored: StoredRemotePairing = {
        pairingId: entry.payload.pairingId,
        mobileId: entry.payload.mobileId,
        platform: entry.payload.platform,
        deviceName: entry.payload.deviceName,
        bindingVersion: entry.payload.bindingVersion,
        sessionSpkiB64: entry.payload.sessionSpkiB64,
        sessionKeyId: entry.sessionKeyId,
        bioSpkiB64: entry.payload.bioSpkiB64 ?? null,
        bioKeyId: entry.bioKeyId,
        status: 'active',
        pairedAtUtc: new Date(this.nowMs()).toISOString(),
        revokedAtUtc: null,
      };
      this.deps.configStore.upsertActivePairing(stored);

      this.sendPairAckFrame(this.activeSession, this.activeConnEpoch, {
        pairingId: entry.payload.pairingId,
        decision: 'approve',
        pairAckV1Text: signedAck.pairAckV1Text,
        pairAckSigB64U: signedAck.pairAckSigB64U,
      });
    } else {
      this.sendPairAckFrame(this.activeSession, this.activeConnEpoch, {
        pairingId: entry.payload.pairingId,
        decision: 'deny',
      });
    }

    this.emitSnapshot();
    return this.getStatusSnapshot();
  }

  public async revokePairing(pairingId: string): Promise<RemoteManagerStatusSnapshot> {
    this.deps.configStore.markPairingRevoked(pairingId);
    const snap = this.deps.configStore.getPublicSnapshot();
    const target = snap.pairings.find(
      (p) => p.pairingId.toLowerCase() === pairingId.toLowerCase(),
    );

    if (target) {
      this.abortStreamsForMobile(target.mobileId);
    }

    await this.pushLocalRevocationsBestEffort([pairingId]);
    this.emitSnapshot();
    return this.getStatusSnapshot();
  }

  public async unbindDesktop(): Promise<RemoteManagerStatusSnapshot> {
    const snap = this.deps.configStore.getPublicSnapshot();
    const activePairingIds = snap.pairings
      .filter((p) => p.status === 'active')
      .map((p) => p.pairingId);

    this.abortAllActiveStreams(StreamRstErrorCode.Revoked, 'desktop_unbound');
    this.abortAndFinishSession({ terminalCloseState: undefined, message: 'Unbound by user.' });
    this.dialController.stop('Unbound by user.');
    this.clearPendingPairPrompts();
    this.issuedQrSecrets.clear();
    this.epochCerts = [];
    this.lastHostEventId = 0;
    this.lastHostSummaryEpoch = null;

    if (activePairingIds.length > 0) {
      await this.pushLocalRevocationsBestEffort(activePairingIds);
    }

    this.deps.configStore.clearBindingKeepProxyAndUrl();
    await this.deps.switchAccountDataDirectory(null);

    this.emitSnapshot();
    return this.getStatusSnapshot();
  }

  public async listExternalWorkspaceAuthorizations(): Promise<RemoteWorkspaceAuthorizationEntry[]> {
    const target = this.deps.getDispatchTarget();
    if (!target) {
      throw new Error('Local backend host is not ready.');
    }
    const res = await this.fetchImpl(`${target.baseUrl}/api/internal/remote/workspace-authorizations`, {
      method: 'GET',
      headers: {
        Authorization: `Bearer ${target.controlToken}`,
        'X-CF-Remote-Bridge-Token': target.remoteBridgeToken,
      },
    });
    const text = await res.text();
    if (!res.ok) {
      throw new Error(extractPortalErrorMessage(text, res.status));
    }
    const parsed = JSON.parse(text) as { items?: RemoteWorkspaceAuthorizationEntry[] };
    return Array.isArray(parsed.items) ? parsed.items : [];
  }

  public async setExternalWorkspaceAuthorization(params: {
    workspacePath: string;
    sessionId?: string;
    authorized: boolean;
  }): Promise<{ workspaceRealPath: string; authorized: boolean }> {
    const target = this.deps.getDispatchTarget();
    if (!target) {
      throw new Error('Local backend host is not ready.');
    }
    const res = await this.fetchImpl(`${target.baseUrl}/api/internal/remote/workspace-authorizations`, {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${target.controlToken}`,
        'X-CF-Remote-Bridge-Token': target.remoteBridgeToken,
      },
      body: JSON.stringify({
        workspacePath: params.workspacePath,
        ...(params.sessionId ? { sessionId: params.sessionId } : {}),
        authorized: params.authorized,
      }),
    });
    const text = await res.text();
    if (!res.ok) {
      throw new Error(extractPortalErrorMessage(text, res.status));
    }
    return JSON.parse(text) as { workspaceRealPath: string; authorized: boolean };
  }

  private getValidEpochCerts(): SessionEpochCertDto[] {
    const nowSec = Math.floor(this.nowMs() / 1000);
    return this.epochCerts.filter((c) => c.expiresAtUnix > nowSec);
  }

  private requirePortalUrl(): string {
    const url = this.deps.configStore.getPortalUrl();
    if (!url) {
      throw new Error('Portal URL is not configured.');
    }
    return url;
  }

  private handleSessionConnected(
    transport: WsTransportSession,
    generation: number,
  ): Promise<{ terminalCloseState?: DialConnectionState; message?: string }> {
    const snap = this.deps.configStore.getPublicSnapshot();
    const privKey = this.deps.configStore.getDesktopPrivateKeyPem();
    if (!snap.bound || !snap.device || !privKey) {
      transport.close(1008, 'desktop_not_bound');
      return Promise.resolve({
        terminalCloseState: 'rebind_required',
        message: 'Desktop device binding is missing.',
      });
    }

    this.activeSession = transport;
    this.activeGeneration = generation;
    this.activeConnEpoch = null;
    this.helloAckReceived = false;
    this.epochCerts = [];

    return new Promise((resolveSession) => {
      let settled = false;
      const finishSession = (result: { terminalCloseState?: DialConnectionState; message?: string }) => {
        if (settled) {
          return;
        }
        settled = true;
        if (this.activeGeneration === generation) {
          this.clearSessionTimers();
          this.abortAllActiveStreams(StreamRstErrorCode.Cancel, 'tunnel_closed');
          this.activeSession = null;
          this.activeConnEpoch = null;
          this.helloAckReceived = false;
          this.sessionSettler = null;
        }
        this.emitSnapshot();
        resolveSession(result);
      };

      this.sessionSettler = finishSession;

      const device = snap.device!;
      const helloPayload: HelloPayload = {
        deviceId: device.deviceId,
        hostInstanceId: device.hostInstanceId,
        bindingVersion: device.bindingVersion,
        lastAckRevocationSeq: Math.max(
          snap.watermarks.accountRevocationSeq,
          snap.watermarks.deviceRevocationSeq,
        ),
      };

      try {
        transport.sendBinary(
          encodeJsonFrame(TunnelFrameType.Hello, TunnelFrameFlags.Priority, 0, 0, helloPayload),
        );
      } catch (err) {
        finishSession({ message: err instanceof Error ? err.message : String(err) });
        return;
      }

      this.helloAckTimer = setTimeout(() => {
        this.helloAckTimer = null;
        if (!this.helloAckReceived && this.activeGeneration === generation) {
          try {
            transport.close(1002, 'hello_ack_timeout');
          } catch {
            // ignore close error
          }
          finishSession({ message: 'Portal HelloAck timeout.' });
        }
      }, this.helloAckTimeoutMs);
      this.helloAckTimer.unref();

      transport.onBinaryMessage((rawBytes) => {
        if (settled || this.activeGeneration !== generation) {
          return;
        }
        const decoded = tryDecodeFrame(rawBytes);
        if (!decoded.ok) {
          try {
            transport.close(1002, 'malformed_tunnel_frame');
          } catch {
            // ignore
          }
          finishSession({ message: decoded.error });
          return;
        }
        void this.handleInboundFrame(decoded.frame, transport, generation, finishSession);
      });

      transport.onPing((data) => {
        if (!settled && this.activeGeneration === generation) {
          try {
            transport.sendPong(data);
          } catch {
            // ignore
          }
        }
      });

      transport.onPong(() => {
        if (!settled && this.activeGeneration === generation) {
          this.lastPongAtUtc = new Date(this.nowMs()).toISOString();
          this.emitSnapshot();
        }
      });

      transport.onError((err) => {
        finishSession({ message: err.message });
      });

      transport.onClose((code, reason) => {
        finishSession(classifyWsCloseOutcome(code, reason));
      });
    });
  }

  private async handleInboundFrame(
    frame: TunnelFrame,
    transport: WsTransportSession,
    generation: number,
    finishSession: (res: { terminalCloseState?: DialConnectionState; message?: string }) => void,
  ): Promise<void> {
    const ft = frame.header.frameType;

    if (ft === TunnelFrameType.HelloAck) {
      if (this.helloAckReceived) {
        transport.close(1002, 'duplicate_hello_ack');
        finishSession({ message: 'Duplicate HelloAck frame received.' });
        return;
      }
      this.handleHelloAckFrame(frame, transport, generation, finishSession);
      return;
    }

    if (!this.helloAckReceived || !this.activeConnEpoch) {
      transport.close(1002, 'expected_hello_ack_first');
      finishSession({ message: 'Received frame before HelloAck.' });
      return;
    }

    if (frame.header.connEpoch !== this.activeConnEpoch) {
      transport.close(1002, 'conn_epoch_mismatch');
      finishSession({ message: 'Connection epoch mismatch.' });
      return;
    }

    switch (ft) {
      case TunnelFrameType.Ping: {
        const ts = tryDecodePingOrPong(frame.payload);
        if (ts !== null) {
          this.safeSendBinary(transport, encodePingOrPong(true, this.activeConnEpoch, ts));
        }
        break;
      }
      case TunnelFrameType.Pong: {
        this.lastPongAtUtc = new Date(this.nowMs()).toISOString();
        this.emitSnapshot();
        break;
      }
      case TunnelFrameType.PairRequest:
        this.handlePairRequestFrame(frame, transport, this.activeConnEpoch);
        break;
      case TunnelFrameType.RevokeNotice:
        await this.handleRevokeNoticeFrame(frame, transport, this.activeConnEpoch, finishSession);
        break;
      case TunnelFrameType.ReqStart:
        this.handleReqStartFrame(frame, transport, this.activeConnEpoch);
        break;
      case TunnelFrameType.ReqBody:
        this.handleReqBodyFrame(frame);
        break;
      case TunnelFrameType.WindowUpdate:
        this.handleWindowUpdateFrame(frame);
        break;
      case TunnelFrameType.StreamRst:
        this.handleStreamRstFrame(frame);
        break;
      default:
        transport.close(1002, 'unexpected_downstream_frame');
        finishSession({ message: `Unexpected frame type 0x${ft.toString(16)}.` });
        break;
    }
  }

  private handleHelloAckFrame(
    frame: TunnelFrame,
    transport: WsTransportSession,
    generation: number,
    finishSession: (res: { terminalCloseState?: DialConnectionState; message?: string }) => void,
  ): void {
    let ack: HelloAckPayload;
    try {
      ack = deserializeJsonPayload<HelloAckPayload>(frame.payload);
    } catch {
      transport.close(1007, 'malformed_hello_ack');
      finishSession({ message: 'Malformed HelloAck payload.' });
      return;
    }

    if (!Number.isInteger(ack.connEpoch) || ack.connEpoch < 1) {
      transport.close(1002, 'invalid_hello_ack_epoch');
      finishSession({ message: 'Invalid connEpoch in HelloAck.' });
      return;
    }

    if (this.helloAckTimer) {
      clearTimeout(this.helloAckTimer);
      this.helloAckTimer = null;
    }

    this.helloAckReceived = true;
    this.activeConnEpoch = ack.connEpoch;

    this.deps.configStore.advanceWatermarks(ack.latestAccountRevSeq, ack.latestDeviceRevSeq);
    this.deps.configStore.reconcilePairingsOnHelloAck(
      ack.activePairings ?? [],
      ack.revokedMobileIds ?? [],
    );
    void this.pushLocalRevocationsBestEffort();

    if (!this.issueAndSendEpochCert(transport, ack.connEpoch)) {
      transport.close(1008, 'missing_signing_identity');
      finishSession({
        terminalCloseState: 'rebind_required',
        message: 'Missing desktop signing private key.',
      });
      return;
    }

    this.startSessionLoops(transport, ack.connEpoch, generation);
    void this.pollAndForwardHostEvents();
    this.emitSnapshot();
  }

  private issueAndSendEpochCert(transport: WsTransportSession, connEpoch: number): boolean {
    const snap = this.deps.configStore.getPublicSnapshot();
    const privKey = this.deps.configStore.getDesktopPrivateKeyPem();
    if (!snap.bound || !snap.device || !privKey) {
      return false;
    }

    const nowSec = Math.floor(this.nowMs() / 1000);
    const cert = createSignedEpochCert({
      deviceId: snap.device.deviceId,
      bindingVersion: snap.device.bindingVersion,
      privateKeyPem: privKey,
      issuedAtUnix: nowSec,
      ttlSeconds: DEFAULT_EPOCH_TTL_SECONDS,
    });

    const filtered = this.epochCerts.filter((c) => c.expiresAtUnix > nowSec);
    filtered.push(cert);
    while (filtered.length > 2) {
      filtered.shift();
    }
    this.epochCerts = filtered;

    const statePayload: HostStatePayload = {
      state: 'epoch_cert',
      sessionEpochCert: cert,
    };

    return this.safeSendBinary(
      transport,
      encodeJsonFrame(TunnelFrameType.HostState, TunnelFrameFlags.Priority, connEpoch, 0, statePayload),
    );
  }

  private startSessionLoops(transport: WsTransportSession, connEpoch: number, generation: number): void {
    this.clearSessionTimers();

    this.pingTimer = setInterval(() => {
      if (this.activeGeneration === generation && this.helloAckReceived) {
        this.safeSendBinary(transport, encodePingOrPong(false, connEpoch, this.nowMs()));
      }
    }, this.pingIntervalMs);
    this.pingTimer.unref();

    this.epochRotateTimer = setInterval(() => {
      if (this.activeGeneration === generation && this.helloAckReceived) {
        this.issueAndSendEpochCert(transport, connEpoch);
        this.emitSnapshot();
      }
    }, this.epochRotateIntervalMs);
    this.epochRotateTimer.unref();

    this.hostEventPollTimer = setInterval(() => {
      if (this.activeGeneration === generation && this.helloAckReceived) {
        void this.pollAndForwardHostEvents();
      }
    }, this.hostEventPollIntervalMs);
    this.hostEventPollTimer.unref();
  }

  private handlePairRequestFrame(
    frame: TunnelFrame,
    transport: WsTransportSession,
    connEpoch: number,
  ): void {
    let payload: PairRequestPayload;
    try {
      payload = deserializeJsonPayload<PairRequestPayload>(frame.payload);
    } catch {
      return;
    }

    const snap = this.deps.configStore.getPublicSnapshot();
    const device = snap.device;
    if (!snap.bound || !device) {
      this.sendPairAckFrame(transport, connEpoch, {
        pairingId: payload.pairingId,
        decision: 'deny',
      });
      return;
    }

    const nowMs = this.nowMs();
    const ticketKey = typeof payload.ticketId === 'string' ? payload.ticketId.trim().toLowerCase() : '';
    const qrSecretEntry = ticketKey ? this.issuedQrSecrets.get(ticketKey) : undefined;
    let hmacValid = false;
    if (
      qrSecretEntry &&
      qrSecretEntry.expiresAtMs > nowMs &&
      isLowerHex(payload.hmacHex, 64) &&
      typeof payload.pairRecordV1Text === 'string' &&
      payload.pairRecordV1Text.length > 0
    ) {
      const expectedHmac = createHmac('sha256', qrSecretEntry.secretBytes)
        .update(payload.pairRecordV1Text, 'utf8')
        .digest();
      const providedHmac = Buffer.from(payload.hmacHex, 'hex');
      hmacValid =
        providedHmac.length === expectedHmac.length &&
        timingSafeEqual(expectedHmac, providedHmac);
    }

    if (qrSecretEntry && (hmacValid || qrSecretEntry.expiresAtMs <= nowMs)) {
      this.issuedQrSecrets.delete(ticketKey);
    }

    if (!hmacValid) {
      this.sendPairAckFrame(transport, connEpoch, {
        pairingId: payload.pairingId,
        decision: 'deny',
      });
      return;
    }

    const lines = typeof payload.pairRecordV1Text === 'string' ? payload.pairRecordV1Text.split('\n') : [];
    const pairingNonceHex32 = lines.length === 13 ? (lines[11] ?? '') : '';

    const integrity = verifyPairRequestCryptographicIntegrity({
      expectedTicketId: payload.ticketId,
      deviceId: device.deviceId,
      bindingVersion: device.bindingVersion,
      desktopKeyId: device.desktopKeyId,
      desktopSpkiSha256: device.desktopSpkiSha256,
      mobileId: payload.mobileId,
      sessionSpkiB64: payload.sessionSpkiB64,
      bioSpkiB64: payload.bioSpkiB64 ?? null,
      pairRecordV1Text: payload.pairRecordV1Text,
      pairRecordSigB64U: payload.pairRecordSigB64U,
      bioPopV1Text: payload.bioPopV1Text ?? null,
      bioPopSigB64U: payload.bioPopSigB64U ?? null,
      pairingNonceHex32,
      expiresAtUnix: payload.expiresAtUnix,
      nowUnix: Math.floor(nowMs / 1000),
    });

    if (!integrity.ok) {
      this.sendPairAckFrame(transport, connEpoch, {
        pairingId: payload.pairingId,
        decision: 'deny',
      });
      return;
    }

    const sas6 = computeSas6Digits(payload.pairRecordV1Text);
    const key = payload.pairingId.toLowerCase();
    const existing = this.pendingPairPrompts.get(key);
    if (existing) {
      clearTimeout(existing.expiryTimer);
      this.pendingPairPrompts.delete(key);
    }

    if (this.pendingPairPrompts.size >= MAX_PENDING_PAIR_PROMPTS) {
      const oldestKey = this.pendingPairPrompts.keys().next().value;
      if (oldestKey) {
        this.removePendingPairEntry(oldestKey);
      }
    }

    const ttlMs = Math.max(1000, payload.expiresAtUnix * 1000 - this.nowMs());
    const expiryTimer = setTimeout(() => {
      if (this.pendingPairPrompts.delete(key)) {
        this.emitSnapshot();
      }
    }, ttlMs);
    expiryTimer.unref();

    this.pendingPairPrompts.set(key, {
      payload,
      sessionKeyId: integrity.sessionKeyId,
      bioKeyId: integrity.bioKeyId,
      sas6,
      receivedAtUtc: new Date(this.nowMs()).toISOString(),
      expiryTimer,
    });
    this.emitSnapshot();
  }

  private async handleRevokeNoticeFrame(
    frame: TunnelFrame,
    transport: WsTransportSession,
    connEpoch: number,
    finishSession: (res: { terminalCloseState?: DialConnectionState; message?: string }) => void,
  ): Promise<void> {
    let notice: RevokeNoticePayload;
    try {
      notice = deserializeJsonPayload<RevokeNoticePayload>(frame.payload);
    } catch {
      return;
    }

    const rawScope = (notice.scope ?? '').trim().toLowerCase();
    const targetIds = Array.isArray(notice.targetIds) ? notice.targetIds : [];

    const canonicalScope: 'pairing' | 'mobile' | 'desktop' | 'account' | 'unknown' =
      rawScope === 'pairing' || rawScope === 'pairing_revoked'
        ? 'pairing'
        : rawScope === 'mobile' || rawScope === 'token_reuse_detected'
          ? 'mobile'
          : rawScope === 'desktop' || rawScope === 'desktop_revoked' || rawScope === 'review_session_reset'
            ? 'desktop'
            : rawScope === 'account' ||
                rawScope === 'account_revoked' ||
                rawScope === 'account_deleted' ||
                rawScope === 'remote_disabled'
              ? 'account'
              : 'unknown';

    if (canonicalScope === 'account') {
      this.deps.configStore.advanceWatermarks(notice.targetRevSeq, undefined);
    } else if (typeof notice.targetRevSeq === 'number' && notice.targetRevSeq > 0) {
      this.deps.configStore.advanceWatermarks(undefined, notice.targetRevSeq);
    }

    const revokedPairingIds: string[] = [];

    if (canonicalScope === 'pairing') {
      const snap = this.deps.configStore.getPublicSnapshot();
      for (const pid of targetIds) {
        this.deps.configStore.markPairingRevoked(pid);
        revokedPairingIds.push(pid);
        const match = snap.pairings.find((p) => p.pairingId.toLowerCase() === pid.toLowerCase());
        if (match) {
          this.abortStreamsForMobile(match.mobileId);
        }
      }
    } else if (canonicalScope === 'mobile') {
      for (const mid of targetIds) {
        const affected = this.deps.configStore.markMobileRevoked(mid);
        revokedPairingIds.push(...affected);
        this.abortStreamsForMobile(mid);
      }
    } else if (canonicalScope === 'desktop' || canonicalScope === 'account') {
      this.abortAllActiveStreams(StreamRstErrorCode.Revoked, `revoked_${canonicalScope}`);
    } else {
      this.abortAllActiveStreams(StreamRstErrorCode.Revoked, 'revoked_unknown_scope');
    }

    await this.pushLocalRevocationsBestEffort(revokedPairingIds);

    this.safeSendBinary(
      transport,
      encodeJsonFrame(
        TunnelFrameType.RevokeNotice,
        TunnelFrameFlags.Fin | TunnelFrameFlags.Priority,
        connEpoch,
        0,
        notice,
      ),
    );

    if (canonicalScope === 'desktop' || canonicalScope === 'account') {
      this.clearPendingPairPrompts();
      this.issuedQrSecrets.clear();
      this.epochCerts = [];
      this.lastHostEventId = 0;
      this.lastHostSummaryEpoch = null;
      this.deps.configStore.clearBindingKeepProxyAndUrl();
      await this.deps.switchAccountDataDirectory(null);
      finishSession({
        terminalCloseState: 'rebind_required',
        message: `Remote binding revoked (${canonicalScope}).`,
      });
      try {
        transport.close(4003, `revoked_${canonicalScope}`);
      } catch {
        // ignore close error
      }
      return;
    }

    if (canonicalScope === 'unknown') {
      this.clearPendingPairPrompts();
      this.issuedQrSecrets.clear();
      this.epochCerts = [];
      finishSession({
        terminalCloseState: 'rebind_required',
        message: `Remote session halted due to unknown revocation scope (${rawScope || 'empty'}).`,
      });
      try {
        transport.close(4003, 'revoked_unknown_scope');
      } catch {
        // ignore close error
      }
      return;
    }

    this.emitSnapshot();
  }

  private handleReqStartFrame(
    frame: TunnelFrame,
    transport: WsTransportSession,
    connEpoch: number,
  ): void {
    const streamId = frame.header.streamId;
    if (this.activeStreams.has(streamId)) {
      this.safeSendBinary(
        transport,
        encodeStreamRst(connEpoch, streamId, StreamRstErrorCode.ProtocolError, 'duplicate_stream_id'),
      );
      return;
    }

    let reqStart: ReqStartPayload;
    try {
      reqStart = deserializeJsonPayload<ReqStartPayload>(frame.payload);
    } catch {
      this.safeSendBinary(
        transport,
        encodeStreamRst(connEpoch, streamId, StreamRstErrorCode.ProtocolError, 'malformed_req_start'),
      );
      return;
    }

    const abortController = new AbortController();
    const queue = new AsyncByteQueue((consumedLen) => {
      if (consumedLen > 0 && !abortController.signal.aborted && this.activeSession === transport) {
        this.safeSendBinary(transport, encodeWindowUpdate(connEpoch, streamId, consumedLen, 1));
      }
    });

    if (frame.isFin) {
      queue.end();
    }

    const mobileId = resolveHeaderValue(reqStart.headers ?? [], 'X-CF-Mobile-Id').toLowerCase();
    const stream: ActiveRelayStream = {
      streamId,
      mobileId,
      abortController,
      reqBodyQueue: queue,
      reqBodyEnded: frame.isFin,
      bytesCredit: PORTAL_INITIAL_DOWNSTREAM_WINDOW_BYTES,
      chunksCredit: PORTAL_INITIAL_DOWNSTREAM_WINDOW_CHUNKS,
      creditWaiters: new Set(),
    };
    this.activeStreams.set(streamId, stream);
    this.emitSnapshot();

    void this.runRelayStream(stream, reqStart, transport, connEpoch);
  }

  private handleReqBodyFrame(frame: TunnelFrame): void {
    const stream = this.activeStreams.get(frame.header.streamId);
    if (!stream || stream.reqBodyEnded) {
      return;
    }
    if (frame.payload.byteLength > 0) {
      stream.reqBodyQueue.push(frame.payload);
    }
    if (frame.isFin) {
      stream.reqBodyEnded = true;
      stream.reqBodyQueue.end();
    }
  }

  private handleWindowUpdateFrame(frame: TunnelFrame): void {
    const decoded = tryDecodeWindowUpdate(frame.payload);
    if (!decoded.ok) {
      return;
    }
    const stream = this.activeStreams.get(frame.header.streamId);
    if (!stream) {
      return;
    }
    stream.bytesCredit += decoded.deltaBytes;
    stream.chunksCredit += decoded.deltaChunks;
    this.wakeCreditWaiters(stream);
  }

  private handleStreamRstFrame(frame: TunnelFrame): void {
    const decoded = tryDecodeStreamRst(frame.payload);
    const reason = decoded.ok ? decoded.reason || `rst_${decoded.errorCode}` : 'stream_reset';
    const stream = this.activeStreams.get(frame.header.streamId);
    if (!stream) {
      return;
    }
    this.abortRelayStream(stream, reason);
  }

  private async runRelayStream(
    stream: ActiveRelayStream,
    reqStart: ReqStartPayload,
    transport: WsTransportSession,
    connEpoch: number,
  ): Promise<void> {
    try {
      const response = await this.dispatcher.dispatch({
        method: reqStart.method,
        path: reqStart.path,
        query: reqStart.query ?? '',
        headers: reqStart.headers ?? [],
        bodyChunks: stream.reqBodyQueue,
        signal: stream.abortController.signal,
      });

      if (stream.abortController.signal.aborted || this.activeSession !== transport) {
        return;
      }

      const contentType = resolveHeaderValue(response.headers, 'content-type').toLowerCase();
      const isSse = contentType.includes('text/event-stream');
      const resHeadPayload: ResHeadPayload = {
        statusCode: response.statusCode,
        headers: response.headers,
      };

      const iterator = response.bodyStream[Symbol.asyncIterator]();

      if (isSse) {
        // SSE must deliver ResHead immediately without waiting for the first event chunk.
        const headOk = this.safeSendBinary(
          transport,
          encodeJsonFrame(
            TunnelFrameType.ResHead,
            TunnelFrameFlags.None,
            connEpoch,
            stream.streamId,
            resHeadPayload,
          ),
        );
        if (!headOk) {
          return;
        }

        while (!stream.abortController.signal.aborted && this.activeSession === transport) {
          const next = await iterator.next();
          if (next.done) {
            break;
          }
          if (next.value.byteLength === 0) {
            continue;
          }
          const sent = await this.sendChunkSliceWithCredit(
            stream,
            transport,
            connEpoch,
            next.value,
            false,
          );
          if (!sent) {
            return;
          }
        }

        if (!stream.abortController.signal.aborted && this.activeSession === transport) {
          await this.waitForChunkCredit(stream, 1, transport);
          if (!stream.abortController.signal.aborted && this.activeSession === transport && stream.chunksCredit >= 1) {
            stream.chunksCredit -= 1;
            this.safeSendBinary(
              transport,
              encodeFrame(
                TunnelFrameType.ResChunk,
                TunnelFrameFlags.Fin,
                connEpoch,
                stream.streamId,
                new Uint8Array(0),
              ),
            );
          }
        }
        return;
      }

      // Non-SSE responses use 1-chunk lookahead so empty or single-chunk bodies avoid trailing empty frames.
      let bufferedChunk = await nextNonEmptyChunk(iterator);
      if (!bufferedChunk) {
        if (!stream.abortController.signal.aborted && this.activeSession === transport) {
          this.safeSendBinary(
            transport,
            encodeJsonFrame(
              TunnelFrameType.ResHead,
              TunnelFrameFlags.Fin,
              connEpoch,
              stream.streamId,
              resHeadPayload,
            ),
          );
        }
        return;
      }

      const headSent = this.safeSendBinary(
        transport,
        encodeJsonFrame(
          TunnelFrameType.ResHead,
          TunnelFrameFlags.None,
          connEpoch,
          stream.streamId,
          resHeadPayload,
        ),
      );
      if (!headSent) {
        return;
      }

      while (bufferedChunk && !stream.abortController.signal.aborted && this.activeSession === transport) {
        const followingChunk = await nextNonEmptyChunk(iterator);
        const isLast = followingChunk === null;
        const ok = await this.sendChunkSliceWithCredit(
          stream,
          transport,
          connEpoch,
          bufferedChunk,
          isLast,
        );
        if (!ok) {
          return;
        }
        bufferedChunk = followingChunk;
      }
    } catch {
      if (!stream.abortController.signal.aborted && this.activeSession === transport) {
        this.safeSendBinary(
          transport,
          encodeStreamRst(connEpoch, stream.streamId, StreamRstErrorCode.Cancel, 'dispatcher_error'),
        );
      }
    } finally {
      this.cleanupStream(stream);
    }
  }

  private async sendChunkSliceWithCredit(
    stream: ActiveRelayStream,
    transport: WsTransportSession,
    connEpoch: number,
    data: Uint8Array,
    attachFinOnFinalSlice: boolean,
  ): Promise<boolean> {
    let offset = 0;
    while (offset < data.byteLength) {
      if (stream.abortController.signal.aborted || this.activeSession !== transport) {
        return false;
      }

      await this.waitForChunkAndByteCredit(stream, transport);
      if (stream.abortController.signal.aborted || this.activeSession !== transport) {
        return false;
      }

      const maxSend = Math.min(
        data.byteLength - offset,
        TUNNEL_MAX_PAYLOAD_BYTES,
        stream.bytesCredit,
      );
      if (maxSend <= 0 || stream.chunksCredit <= 0) {
        continue;
      }

      const slice = data.subarray(offset, offset + maxSend);
      offset += maxSend;
      stream.bytesCredit -= maxSend;
      stream.chunksCredit -= 1;

      const isFinalSlice = attachFinOnFinalSlice && offset >= data.byteLength;
      const sent = this.safeSendBinary(
        transport,
        encodeFrame(
          TunnelFrameType.ResChunk,
          isFinalSlice ? TunnelFrameFlags.Fin : TunnelFrameFlags.None,
          connEpoch,
          stream.streamId,
          slice,
        ),
      );
      if (!sent) {
        return false;
      }
    }
    return true;
  }

  private async waitForChunkAndByteCredit(
    stream: ActiveRelayStream,
    transport: WsTransportSession,
  ): Promise<void> {
    while (
      (stream.bytesCredit <= 0 || stream.chunksCredit <= 0) &&
      !stream.abortController.signal.aborted &&
      this.activeSession === transport
    ) {
      await this.waitCreditSignal(stream);
    }
  }

  private async waitForChunkCredit(
    stream: ActiveRelayStream,
    neededChunks: number,
    transport: WsTransportSession,
  ): Promise<void> {
    while (
      stream.chunksCredit < neededChunks &&
      !stream.abortController.signal.aborted &&
      this.activeSession === transport
    ) {
      await this.waitCreditSignal(stream);
    }
  }

  private waitCreditSignal(stream: ActiveRelayStream): Promise<void> {
    return new Promise<void>((resolve) => {
      stream.creditWaiters.add(resolve);
    });
  }

  private wakeCreditWaiters(stream: ActiveRelayStream): void {
    for (const waiter of stream.creditWaiters) {
      waiter();
    }
    stream.creditWaiters.clear();
  }

  private async pollAndForwardHostEvents(): Promise<void> {
    if (
      this.hostEventPollInFlight ||
      !this.helloAckReceived ||
      !this.activeSession ||
      !this.activeConnEpoch
    ) {
      return;
    }

    const snap = this.deps.configStore.getPublicSnapshot();
    const target = this.deps.getDispatchTarget();
    if (!snap.bound || !snap.device || !target) {
      return;
    }

    this.hostEventPollInFlight = true;
    const sessionAtStart = this.activeSession;
    const epochAtStart = this.activeConnEpoch;
    const bindingVersion = snap.device.bindingVersion;

    try {
      for (let attempt = 0; attempt < 2; attempt += 1) {
        if (this.activeSession !== sessionAtStart) {
          return;
        }
        const res = await this.fetchImpl(
          `${target.baseUrl}/api/internal/remote/host-events?afterEventId=${this.lastHostEventId}&limit=100`,
          {
            method: 'GET',
            headers: {
              Authorization: `Bearer ${target.controlToken}`,
              'X-CF-Remote-Bridge-Token': target.remoteBridgeToken,
            },
          },
        );
        if (!res.ok) {
          return;
        }

        const payload = (await res.json()) as {
          summaryEpoch?: number;
          events?: InternalHostEventRecord[];
        };

        if (typeof payload.summaryEpoch === 'number') {
          if (
            this.lastHostSummaryEpoch !== null &&
            payload.summaryEpoch !== this.lastHostSummaryEpoch
          ) {
            this.lastHostSummaryEpoch = payload.summaryEpoch;
            this.lastHostEventId = 0;
            if (attempt === 0) {
              continue;
            }
          } else {
            this.lastHostSummaryEpoch = payload.summaryEpoch;
          }
        }

        const events = Array.isArray(payload.events) ? payload.events : [];
        if (events.length === 0 || this.activeSession !== sessionAtStart) {
          return;
        }

        for (const ev of events) {
          if (this.activeSession !== sessionAtStart) {
            break;
          }
          const hostEvent: HostEventPayload = {
            eventId: ev.eventId,
            summaryEpoch: String(ev.summaryEpoch),
            bindingVersion,
            eventKind: ev.status,
            targetKind: ev.sourceKind,
            opaqueRefId: ev.opaqueRefId,
            occurredAtUtc: ev.createdAtUtc,
            expiresAtUtc: ev.expiresAtUtc,
            isTerminal: ev.isTerminal,
          };
          const sent = this.safeSendBinary(
            sessionAtStart,
            encodeJsonFrame(
              TunnelFrameType.HostEvent,
              TunnelFrameFlags.Priority,
              epochAtStart,
              0,
              hostEvent,
            ),
          );
          if (!sent) {
            break;
          }
          if (ev.eventId > this.lastHostEventId) {
            this.lastHostEventId = ev.eventId;
          }
        }
        break;
      }
    } catch {
      // Transient backend poll error; retried on next tick.
    } finally {
      this.hostEventPollInFlight = false;
    }
  }

  private async pushLocalRevocationsBestEffort(explicitPairingIds: readonly string[] = []): Promise<void> {
    const target = this.deps.getDispatchTarget();
    if (!target) {
      return;
    }

    const snap = this.deps.configStore.getPublicSnapshot();
    const revokedInStore = snap.pairings
      .filter((p) => p.status === 'revoked')
      .map((p) => p.pairingId);
    const mergedPairingIds = Array.from(new Set([...revokedInStore, ...explicitPairingIds]));

    try {
      await this.fetchImpl(`${target.baseUrl}/api/internal/remote/revocations`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${target.controlToken}`,
          'X-CF-Remote-Bridge-Token': target.remoteBridgeToken,
        },
        body: JSON.stringify({
          minAccountRevocationSeq: snap.watermarks.accountRevocationSeq,
          minDeviceRevocationSeq: snap.watermarks.deviceRevocationSeq,
          revokedPairingIds: mergedPairingIds,
        }),
      });
    } catch {
      // Backend may be restarting during account switch; synced again on notifyBackendReady().
    }
  }

  private sendPairAckFrame(transport: WsTransportSession, connEpoch: number, pairAck: PairAckDto): void {
    const payload: HostStatePayload = {
      state: 'pair_ack',
      pairAck,
    };
    this.safeSendBinary(
      transport,
      encodeJsonFrame(TunnelFrameType.HostState, TunnelFrameFlags.Priority, connEpoch, 0, payload),
    );
  }

  private safeSendBinary(transport: WsTransportSession, bytes: Uint8Array): boolean {
    try {
      transport.sendBinary(bytes);
      return true;
    } catch {
      return false;
    }
  }

  private abortStreamsForMobile(mobileId: string): void {
    const targetLower = mobileId.toLowerCase();
    for (const stream of Array.from(this.activeStreams.values())) {
      if (stream.mobileId === targetLower) {
        if (this.activeSession && this.activeConnEpoch) {
          this.safeSendBinary(
            this.activeSession,
            encodeStreamRst(this.activeConnEpoch, stream.streamId, StreamRstErrorCode.Revoked, 'mobile_revoked'),
          );
        }
        this.abortRelayStream(stream, 'mobile_revoked');
      }
    }
  }

  private abortAllActiveStreams(errorCode: StreamRstErrorCode, reason: string): void {
    for (const stream of Array.from(this.activeStreams.values())) {
      if (this.activeSession && this.activeConnEpoch) {
        this.safeSendBinary(
          this.activeSession,
          encodeStreamRst(this.activeConnEpoch, stream.streamId, errorCode, reason),
        );
      }
      this.abortRelayStream(stream, reason);
    }
  }

  private abortRelayStream(stream: ActiveRelayStream, reason: string): void {
    if (!stream.abortController.signal.aborted) {
      stream.abortController.abort(reason);
    }
    stream.reqBodyQueue.fail(new Error(reason));
    this.wakeCreditWaiters(stream);
    this.cleanupStream(stream);
  }

  private cleanupStream(stream: ActiveRelayStream): void {
    if (this.activeStreams.delete(stream.streamId)) {
      this.wakeCreditWaiters(stream);
      this.emitSnapshot();
    }
  }

  private abortAndFinishSession(result: {
    terminalCloseState?: DialConnectionState;
    message?: string;
  }): void {
    this.clearSessionTimers();
    const transport = this.activeSession;
    const settler = this.sessionSettler;
    this.activeSession = null;
    this.activeConnEpoch = null;
    this.helloAckReceived = false;
    this.sessionSettler = null;
    this.abortAllActiveStreams(StreamRstErrorCode.Cancel, result.message ?? 'session_closed');
    if (transport) {
      try {
        transport.close(1000, 'client_reset');
      } catch {
        // ignore close failure
      }
    }
    settler?.(result);
  }

  private clearSessionTimers(): void {
    if (this.helloAckTimer) {
      clearTimeout(this.helloAckTimer);
      this.helloAckTimer = null;
    }
    if (this.pingTimer) {
      clearInterval(this.pingTimer);
      this.pingTimer = null;
    }
    if (this.epochRotateTimer) {
      clearInterval(this.epochRotateTimer);
      this.epochRotateTimer = null;
    }
    if (this.hostEventPollTimer) {
      clearInterval(this.hostEventPollTimer);
      this.hostEventPollTimer = null;
    }
  }

  private removePendingPairEntry(pairingId: string): void {
    const key = pairingId.toLowerCase();
    const entry = this.pendingPairPrompts.get(key);
    if (entry) {
      clearTimeout(entry.expiryTimer);
      this.pendingPairPrompts.delete(key);
    }
  }

  private clearPendingPairPrompts(): void {
    for (const entry of this.pendingPairPrompts.values()) {
      clearTimeout(entry.expiryTimer);
    }
    this.pendingPairPrompts.clear();
  }

  private emitSnapshot(): void {
    this.deps.onStateChanged?.(this.getStatusSnapshot());
  }
}

async function nextNonEmptyChunk(iterator: AsyncIterator<Uint8Array>): Promise<Uint8Array | null> {
  while (true) {
    const step = await iterator.next();
    if (step.done) {
      return null;
    }
    if (step.value.byteLength > 0) {
      return step.value;
    }
  }
}

function normalizeEmail(rawEmail: string): string {
  const trimmed = rawEmail.trim().toLowerCase();
  if (!trimmed || !trimmed.includes('@') || trimmed.length > 256) {
    throw new Error('Please enter a valid account email address.');
  }
  return trimmed;
}

function classifyWsCloseOutcome(
  code: number,
  reason: string,
): { terminalCloseState?: DialConnectionState; message?: string } {
  const lowerReason = (reason || '').toLowerCase();
  if (code === 4009) {
    if (lowerReason.includes('reauth')) {
      return {
        terminalCloseState: 'unauthorized_401',
        message: 'Portal requires device re-authentication.',
      };
    }
    return {
      terminalCloseState: 'rebind_required',
      message: reason || 'Device binding revoked or rebind required.',
    };
  }
  if (code === 4003) {
    return {
      terminalCloseState: 'forbidden_403',
      message: reason || 'Remote access forbidden.',
    };
  }
  return {
    message: reason ? `Tunnel closed (${code}): ${reason}` : `Tunnel closed (${code}).`,
  };
}
