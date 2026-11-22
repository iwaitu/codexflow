import {
  EMPTY_BODY_SHA256_HEX,
  bytesToHexLower,
  canonicalizeContentType,
  canonicalizeQuery,
  utf8Encode,
  type KeyPurpose,
  type SessionEpochCertDto,
} from '@codexflow-shared/remoteProtocol';
import { getNativeCryptoPlugin } from '@/bridge/nativeCryptoBridge';
import type { PersistedSecurePairingState } from '@/bridge/nativeCryptoTypes';
import { sha256HexLower } from '@/crypto/incrementalSha256';
import { verifyEpochCertAsync } from '@/crypto/p256Verify';
import {
  classifyRemoteError,
  isTerminalRevocationCode,
  RemoteApiException,
  type MobileAppError,
} from './errorClassifier';

function randomHex32(): string {
  const bytes = new Uint8Array(16);
  globalThis.crypto.getRandomValues(bytes);
  return bytesToHexLower(bytes);
}

interface RefreshApiResponse {
  readonly accessToken: string;
  readonly accessExpiresAt: string;
  readonly refreshToken: string;
  readonly refreshExpiresAt: string;
  readonly pairingId: string;
  readonly deviceId: string;
  readonly mobileId: string;
  readonly bindingVersion: number;
}

export interface SignedRelayCallOptions {
  readonly method: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  readonly path: string;
  readonly queryPairs?: ReadonlyArray<readonly [string, string]>;
  readonly keyPurpose?: KeyPurpose;
  readonly jsonBody?: unknown;
  readonly rawBody?: Blob | Uint8Array;
  readonly precomputedBodySha256?: string;
  readonly contentType?: string;
  readonly ifNoneMatch?: string;
  readonly uploadId?: string;
  readonly fileSha256?: string;
  readonly clientOpId?: string;
  readonly promptReason?: string;
  readonly signal?: AbortSignal;
}

export interface RawRelayResponse {
  readonly status: number;
  readonly ok: boolean;
  readonly headers: Headers;
  readonly response: Response;
}

export class RelayClient {
  private readonly fetchImpl: typeof fetch;
  private readonly nowMs: () => number;
  private state: PersistedSecurePairingState | null = null;
  private epochCert: SessionEpochCertDto | null = null;
  private clockOffsetSec = 0;
  private refreshInFlight: Promise<PersistedSecurePairingState> | null = null;
  private epochInFlight: Promise<SessionEpochCertDto> | null = null;
  private readonly onSessionRevoked?: (err: MobileAppError) => void;
  private readonly onStateUpdated?: (next: PersistedSecurePairingState | null) => void;

  public constructor(options?: {
    initialState?: PersistedSecurePairingState | null;
    fetchFn?: typeof fetch;
    nowMs?: () => number;
    onSessionRevoked?: (err: MobileAppError) => void;
    onStateUpdated?: (next: PersistedSecurePairingState | null) => void;
  }) {
    this.fetchImpl = options?.fetchFn ?? fetch;
    this.nowMs = options?.nowMs ?? (() => Date.now());
    this.state = options?.initialState ?? null;
    this.onSessionRevoked = options?.onSessionRevoked;
    this.onStateUpdated = options?.onStateUpdated;
  }

  public getState(): PersistedSecurePairingState | null {
    return this.state;
  }

  public getCurrentEpochHex(): string | null {
    return this.epochCert?.sessionEpochHex32 ?? null;
  }

  public setState(next: PersistedSecurePairingState | null): void {
    this.state = next;
    this.epochCert = null;
    this.onStateUpdated?.(next);
  }

  public async loadPersistedState(): Promise<PersistedSecurePairingState | null> {
    const plugin = await getNativeCryptoPlugin();
    const { stateJson } = await plugin.loadSecureState();
    if (!stateJson) {
      this.state = null;
      return null;
    }
    try {
      const parsed = JSON.parse(stateJson) as PersistedSecurePairingState;
      this.state = parsed;
      return parsed;
    } catch {
      this.state = null;
      return null;
    }
  }

  public async logoutAndClear(): Promise<void> {
    const current = this.state;
    if (current) {
      try {
        await this.fetchImpl(`${current.portalUrl}/api/auth/logout`, {
          method: 'POST',
          headers: {
            Authorization: `Bearer ${current.accessToken}`,
          },
        });
      } catch {
        // 清理本地凭据优先于网络失败
      }
    }
    const plugin = await getNativeCryptoPlugin();
    await plugin.clearAllSecureState();
    this.state = null;
    this.epochCert = null;
    this.onStateUpdated?.(null);
  }

  public async requestJson<T>(options: SignedRelayCallOptions): Promise<T> {
    const res = await this.sendSigned(options);
    if (res.status === 204) {
      return undefined as T;
    }
    const text = await res.response.text();
    if (!res.ok) {
      throw this.buildApiException(res.status, text);
    }
    if (!text) {
      return undefined as T;
    }
    return JSON.parse(text) as T;
  }

  public async openSignedStream(options: SignedRelayCallOptions): Promise<Response> {
    const res = await this.sendSigned(options);
    if (!res.ok) {
      const text = await res.response.text();
      throw this.buildApiException(res.status, text);
    }
    return res.response;
  }

  public async sendRaw(options: SignedRelayCallOptions): Promise<RawRelayResponse> {
    return this.sendSigned(options);
  }

  public async updateApnsPushToken(params: {
    pushPlatform: 'apns' | null;
    pushToken: string | null;
  }): Promise<void> {
    const current = this.requireState();
    const doPut = async (token: string) =>
      this.fetchImpl(`${current.portalUrl}/api/mobile/push-token`, {
        method: 'PUT',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${token}`,
        },
        body: JSON.stringify({
          pushPlatform: params.pushPlatform,
          pushToken: params.pushToken,
        }),
      });

    let resp = await doPut(current.accessToken);
    if (resp.status === 401) {
      const next = await this.refreshTokensSingleFlight();
      resp = await doPut(next.accessToken);
    }
    if (!resp.ok && resp.status !== 204) {
      const text = await resp.text();
      throw this.buildApiException(resp.status, text);
    }
  }

  private requireState(): PersistedSecurePairingState {
    if (!this.state) {
      throw new RemoteApiException(classifyRemoteError('not_paired', 401, '设备尚未配对。'));
    }
    return this.state;
  }

  private observeServerDate(headers: Headers): void {
    const dateVal = headers.get('date');
    if (!dateVal) {
      return;
    }
    const serverMs = Date.parse(dateVal);
    if (Number.isFinite(serverMs)) {
      this.clockOffsetSec = Math.round((serverMs - this.nowMs()) / 1000);
    }
  }

  private effectiveNowSec(): number {
    return Math.floor(this.nowMs() / 1000) + this.clockOffsetSec;
  }

  private buildApiException(status: number, rawText: string): RemoteApiException {
    let code = `http_${status}`;
    let message = '';
    if (rawText) {
      try {
        const parsed = JSON.parse(rawText) as { code?: string; error?: string; message?: string };
        code = parsed.code ?? parsed.error ?? code;
        message = parsed.message ?? rawText;
      } catch {
        message = rawText.slice(0, 300);
      }
    }
    return new RemoteApiException(classifyRemoteError(code, status, message));
  }

  private async handleTerminalRevocation(err: MobileAppError): Promise<never> {
    const plugin = await getNativeCryptoPlugin();
    await plugin.clearAllSecureState();
    this.state = null;
    this.epochCert = null;
    this.onStateUpdated?.(null);
    this.onSessionRevoked?.(err);
    throw new RemoteApiException(err);
  }

  public async refreshTokensSingleFlight(): Promise<PersistedSecurePairingState> {
    if (this.refreshInFlight) {
      return this.refreshInFlight;
    }
    this.refreshInFlight = this.performRefresh().finally(() => {
      this.refreshInFlight = null;
    });
    return this.refreshInFlight;
  }

  private async performRefresh(): Promise<PersistedSecurePairingState> {
    const current = this.requireState();
    const res = await this.fetchImpl(`${current.portalUrl}/api/auth/token/refresh`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: current.refreshToken }),
    });
    this.observeServerDate(res.headers);

    if (!res.ok) {
      const text = await res.text();
      const ex = this.buildApiException(res.status, text);
      if (res.status === 401 || res.status === 403) {
        await this.handleTerminalRevocation(ex.appError);
      }
      throw ex;
    }

    const refreshed = (await res.json()) as RefreshApiResponse;
    const nextState: PersistedSecurePairingState = {
      ...current,
      bindingVersion: refreshed.bindingVersion,
      accessToken: refreshed.accessToken,
      accessExpiresAt: refreshed.accessExpiresAt,
      refreshToken: refreshed.refreshToken,
      refreshExpiresAt: refreshed.refreshExpiresAt,
    };
    const plugin = await getNativeCryptoPlugin();
    await plugin.saveSecureState({ stateJson: JSON.stringify(nextState) });
    this.state = nextState;
    this.onStateUpdated?.(nextState);
    return nextState;
  }

  public async ensureValidEpochCert(forceRefresh = false): Promise<SessionEpochCertDto> {
    this.requireState();
    const nowSec = this.effectiveNowSec();
    if (!forceRefresh && this.epochCert && this.epochCert.expiresAtUnix > nowSec + 300) {
      return this.epochCert;
    }

    if (this.epochInFlight && !forceRefresh) {
      return this.epochInFlight;
    }

    const fetchCert = async (): Promise<SessionEpochCertDto> => {
      const active = this.requireState();
      const url = `${active.portalUrl}/relay/${encodeURIComponent(active.deviceId)}/meta/session-epoch`;
      let resp = await this.fetchImpl(url, {
        method: 'GET',
        headers: { Authorization: `Bearer ${active.accessToken}` },
      });
      this.observeServerDate(resp.headers);

      if (resp.status === 401) {
        const errText = await resp.text();
        const ex = this.buildApiException(resp.status, errText);
        if (isTerminalRevocationCode(ex.appError.code)) {
          await this.handleTerminalRevocation(ex.appError);
        }
        const refreshed = await this.refreshTokensSingleFlight();
        resp = await this.fetchImpl(url, {
          method: 'GET',
          headers: { Authorization: `Bearer ${refreshed.accessToken}` },
        });
        this.observeServerDate(resp.headers);
      }

      if (!resp.ok) {
        const text = await resp.text();
        throw this.buildApiException(resp.status, text);
      }

      const cert = (await resp.json()) as SessionEpochCertDto;
      const latestState = this.requireState();
      const valid = await verifyEpochCertAsync({
        desktopSpkiB64: latestState.desktopSpkiB64,
        cert,
        expectedDeviceId: latestState.deviceId,
        expectedBindingVersion: latestState.bindingVersion,
        nowSec: this.effectiveNowSec(),
      });
      if (!valid) {
        throw new RemoteApiException(
          classifyRemoteError('invalid_session_epoch', 403, '桌面会话代次证书 CF-EPOCH-CERT-V1 验签失败或已过期。'),
        );
      }
      this.epochCert = cert;
      return cert;
    };

    this.epochInFlight = fetchCert().finally(() => {
      this.epochInFlight = null;
    });
    return this.epochInFlight;
  }

  private async sendSigned(options: SignedRelayCallOptions): Promise<RawRelayResponse> {
    let didRefreshToken = false;
    let didRefreshEpoch = false;
    let didAdjustClock = false;

    while (true) {
      const activeState = this.requireState();
      const cert = await this.ensureValidEpochCert(didRefreshEpoch);
      const keyPurpose = options.keyPurpose ?? 'device_session';
      const queryPairs = options.queryPairs ?? [];
      const canonicalQuery = canonicalizeQuery(queryPairs);
      const wireQuery = queryPairs
        .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`)
        .join('&');

      let bodyToSend: Blob | Uint8Array | undefined;
      let declaredContentLength = 0;
      let bodySha256LowerHex = EMPTY_BODY_SHA256_HEX;
      let rawContentType = options.contentType ?? '';

      if (options.rawBody !== undefined) {
        bodyToSend = options.rawBody;
        if (options.rawBody instanceof Blob) {
          declaredContentLength = options.rawBody.size;
          if (!options.precomputedBodySha256 && declaredContentLength > 0) {
            throw new Error('Blob uploads must provide precomputedBodySha256.');
          }
          bodySha256LowerHex = options.precomputedBodySha256 ?? EMPTY_BODY_SHA256_HEX;
        } else {
          declaredContentLength = options.rawBody.byteLength;
          bodySha256LowerHex =
            declaredContentLength > 0 ? sha256HexLower(options.rawBody) : EMPTY_BODY_SHA256_HEX;
        }
      } else if (options.jsonBody !== undefined) {
        const jsonBytes = utf8Encode(JSON.stringify(options.jsonBody));
        bodyToSend = jsonBytes;
        declaredContentLength = jsonBytes.byteLength;
        bodySha256LowerHex = sha256HexLower(jsonBytes);
        rawContentType = rawContentType || 'application/json; charset=utf-8';
      }

      const canonicalContentType = canonicalizeContentType(rawContentType);
      const unixTimestampSeconds = this.effectiveNowSec();
      const nonceHex32 = randomHex32();

      const plugin = await getNativeCryptoPlugin();
      const { keyId, sigB64U } = await plugin.signRelayRequest({
        deviceId: activeState.deviceId,
        bindingVersion: activeState.bindingVersion,
        sessionEpochHex32: cert.sessionEpochHex32,
        mobileId: activeState.mobileId,
        keyPurpose,
        methodUpper: options.method,
        canonicalPath: options.path,
        canonicalQuery,
        ifNoneMatchOrEmpty: options.ifNoneMatch ?? '',
        uploadIdOrEmpty: options.uploadId ?? '',
        fileSha256OrEmpty: options.fileSha256 ?? '',
        clientOpIdOrEmpty: options.clientOpId ?? '',
        canonicalContentType,
        declaredContentLength,
        bodySha256LowerHex,
        unixTimestampSeconds,
        nonceHex32,
        promptReason: options.promptReason,
      });

      const headers: Record<string, string> = {
        Authorization: `Bearer ${activeState.accessToken}`,
        'X-CF-Mobile-Id': activeState.mobileId,
        'X-CF-Key-Id': keyId,
        'X-CF-Key-Purpose': keyPurpose,
        'X-CF-Session-Epoch': cert.sessionEpochHex32,
        'X-CF-Timestamp': String(unixTimestampSeconds),
        'X-CF-Nonce': nonceHex32,
        'X-CF-Sig': sigB64U,
      };
      if (rawContentType) {
        headers['Content-Type'] = rawContentType;
      }
      if (declaredContentLength > 0) {
        headers['X-CF-Body-Sha256'] = bodySha256LowerHex;
      }
      if (options.ifNoneMatch) {
        headers['If-None-Match'] = options.ifNoneMatch;
      }
      if (options.uploadId) {
        headers['X-CF-Upload-Id'] = options.uploadId;
      }
      if (options.fileSha256) {
        headers['X-CF-File-Sha256'] = options.fileSha256;
      }
      if (options.clientOpId) {
        headers['X-CF-Client-Op-Id'] = options.clientOpId;
      }

      const targetUrl = `${activeState.portalUrl}/relay/${encodeURIComponent(activeState.deviceId)}${options.path}${wireQuery ? `?${wireQuery}` : ''}`;
      let fetchBody: BodyInit | undefined;
      if (bodyToSend !== undefined) {
        if (bodyToSend instanceof Blob) {
          fetchBody = bodyToSend;
        } else {
          const copy = new Uint8Array(bodyToSend.byteLength);
          copy.set(bodyToSend);
          fetchBody = copy;
        }
      }

      const resp = await this.fetchImpl(targetUrl, {
        method: options.method,
        headers,
        body: fetchBody,
        signal: options.signal,
      });
      this.observeServerDate(resp.headers);

      if (resp.status === 401) {
        const errText = await resp.text();
        const ex = this.buildApiException(resp.status, errText);
        const code = ex.appError.code;

        if (isTerminalRevocationCode(code)) {
          await this.handleTerminalRevocation(ex.appError);
        }
        if (code === 'invalid_session_epoch' && !didRefreshEpoch) {
          didRefreshEpoch = true;
          this.epochCert = null;
          continue;
        }
        if (code === 'timestamp_skew_exceeded' && !didAdjustClock) {
          didAdjustClock = true;
          continue;
        }
        if (!didRefreshToken) {
          didRefreshToken = true;
          await this.refreshTokensSingleFlight();
          continue;
        }
        throw ex;
      }

      if (resp.status === 403) {
        const cloned = await resp.clone().text();
        const ex = this.buildApiException(resp.status, cloned);
        if (isTerminalRevocationCode(ex.appError.code)) {
          await this.handleTerminalRevocation(ex.appError);
        }
      }

      return {
        status: resp.status,
        ok: resp.ok || resp.status === 304,
        headers: resp.headers,
        response: resp,
      };
    }
  }
}
