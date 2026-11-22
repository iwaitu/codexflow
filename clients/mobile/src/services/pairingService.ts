import { Capacitor } from '@capacitor/core';
import {
  buildSasV1Input,
  bytesToHexLower,
  parsePairingQrUri,
  sas6FromSha256Digest,
} from '@codexflow-shared/remoteProtocol';
import { getNativeCryptoPlugin } from '@/bridge/nativeCryptoBridge';
import type { PersistedSecurePairingState } from '@/bridge/nativeCryptoTypes';
import { sha256Bytes, sha256HexLower } from '@/crypto/incrementalSha256';
import { computeSpkiSha256AndKeyId, verifyPairAckSignatureAsync } from '@/crypto/p256Verify';
import { classifyRemoteError, RemoteApiException } from './errorClassifier';
import { normalizePortalOrigin } from './portalConfig';

export type PairingProgressStage =
  | 'scanning_qr'
  | 'activating_ticket'
  | 'awaiting_desktop_sas'
  | 'exchanging_tokens'
  | 'completed';

export interface ActivePairingPreview {
  readonly pairingId: string;
  readonly mobileId: string;
  readonly deviceId: string;
  readonly bindingVersion: number;
  readonly desktopSpkiB64: string;
  readonly desktopSpkiSha256: string;
  readonly desktopKeyId: string;
  readonly sas6: string;
  readonly biometricEnrolled: boolean;
  readonly expiresAt: string;
}

interface QrActivateApiResponse {
  readonly pairingId: string;
  readonly pairingPollToken: string;
  readonly expiresAt: string;
  readonly desktopSpkiB64: string;
  readonly desktopKeyId: string;
  readonly sasDigits: string;
}

interface PairingStatusApiResponse {
  readonly pairingId: string;
  readonly status: string;
  readonly expiresAt: string;
  readonly pairAckV1Text?: string | null;
  readonly pairAckSigB64U?: string | null;
}

interface ExchangeApiResponse {
  readonly accessToken: string;
  readonly accessExpiresAt: string;
  readonly refreshToken: string;
  readonly refreshExpiresAt: string;
  readonly pairingId: string;
  readonly deviceId: string;
  readonly mobileId: string;
  readonly bindingVersion: number;
}

function randomHex32(): string {
  const bytes = new Uint8Array(16);
  globalThis.crypto.getRandomValues(bytes);
  return bytesToHexLower(bytes);
}

async function parseErrorResponse(res: Response): Promise<never> {
  let code = `http_${res.status}`;
  let message = res.statusText || '请求失败';
  try {
    const json = (await res.json()) as { code?: string; error?: string; message?: string };
    code = json.code ?? json.error ?? code;
    message = json.message ?? message;
  } catch {
    // ignore non-json error body
  }
  throw new RemoteApiException(classifyRemoteError(code, res.status, message));
}

function resolvePlatformLabel(explicit?: 'ios' | 'android'): 'ios' | 'android' {
  if (explicit) {
    return explicit;
  }
  return Capacitor.getPlatform() === 'android' ? 'android' : 'ios';
}

export async function runQrPairingFlow(params: {
  rawQrUri: string;
  portalUrl: string;
  mobileDeviceName: string;
  platform?: 'ios' | 'android';
  fetchFn?: typeof fetch;
  pollIntervalMs?: number;
  signal?: AbortSignal;
  onStageChange?: (stage: PairingProgressStage, preview?: ActivePairingPreview) => void;
}): Promise<PersistedSecurePairingState> {
  const fetchImpl = params.fetchFn ?? fetch;
  const portalOrigin = normalizePortalOrigin(params.portalUrl);
  const parsedQr = parsePairingQrUri(params.rawQrUri);
  const desktopKeyId = parsedQr.desktopSpkiSha256.slice(0, 32);

  params.onStageChange?.('activating_ticket');
  const cryptoPlugin = await getNativeCryptoPlugin();
  const keySummary = await cryptoPlugin.ensureDeviceKeys({ requireBiometricKey: true });

  const nowUnix = Math.floor(Date.now() / 1000);
  const expiresAtUnix = nowUnix + 300;
  const pairingNonceHex32 = randomHex32();

  const bundle = await cryptoPlugin.signPairingBundle({
    ticketId: parsedQr.ticketId,
    deviceId: parsedQr.deviceId,
    bindingVersion: parsedQr.bindingVersion,
    desktopKeyId,
    desktopSpkiSha256: parsedQr.desktopSpkiSha256,
    pairingNonceHex32,
    expiresAtUnix,
    pairingSecretB64U: parsedQr.pairingSecretB64U,
  });

  // 绝不盲信桥接外或服务端回显 SAS：前端用冻结公式二次验证
  const recomputedSas = sas6FromSha256Digest(sha256Bytes(buildSasV1Input(bundle.pairRecordV1Text)));
  if (recomputedSas !== bundle.sas6) {
    throw new RemoteApiException(
      classifyRemoteError('sas_mismatch', 400, '本地 SAS 摘要计算不一致。'),
    );
  }

  const activateRes = await fetchImpl(`${portalOrigin}/api/auth/mobile/qr-activate`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      ticketId: parsedQr.ticketId,
      qrBootstrapToken: parsedQr.qrBootstrapToken,
      deviceId: parsedQr.deviceId,
      bindingVersion: parsedQr.bindingVersion,
      mobileId: bundle.mobileId,
      platform: resolvePlatformLabel(params.platform),
      deviceName: params.mobileDeviceName.trim().slice(0, 120) || 'CodexFlow Mobile',
      sessionSpkiB64: bundle.sessionSpkiB64,
      bioSpkiB64: bundle.bioSpkiB64,
      pairingNonceHex32,
      expiresAtUnix,
      pairRecordV1Text: bundle.pairRecordV1Text,
      pairRecordSigB64U: bundle.pairRecordSigB64U,
      bioPopV1Text: bundle.bioPopV1Text,
      bioPopSigB64U: bundle.bioPopSigB64U,
      hmacHex: bundle.hmacHex,
      ...(parsedQr.reviewSessionId ? { reviewSessionId: parsedQr.reviewSessionId } : {}),
    }),
    signal: params.signal,
  });

  if (!activateRes.ok) {
    await parseErrorResponse(activateRes);
  }

  const activateJson = (await activateRes.json()) as QrActivateApiResponse;

  // 严格锁定服务端返回的 desktopSpkiB64 == QR 中的 sha256 指纹
  const computedDesktop = computeSpkiSha256AndKeyId(activateJson.desktopSpkiB64);
  if (computedDesktop.spkiSha256 !== parsedQr.desktopSpkiSha256) {
    throw new RemoteApiException(
      classifyRemoteError(
        'desktop_spki_fingerprint_mismatch',
        403,
        '服务端下发的桌面公钥与扫码指纹不匹配，疑似中间人篡改，已中止配对。',
      ),
    );
  }

  if (activateJson.sasDigits !== bundle.sas6) {
    throw new RemoteApiException(
      classifyRemoteError('sas_mismatch', 403, '服务端返回的 SAS 校验码与本地计算不一致。'),
    );
  }

  const preview: ActivePairingPreview = {
    pairingId: activateJson.pairingId,
    mobileId: bundle.mobileId,
    deviceId: parsedQr.deviceId,
    bindingVersion: parsedQr.bindingVersion,
    desktopSpkiB64: activateJson.desktopSpkiB64,
    desktopSpkiSha256: computedDesktop.spkiSha256,
    desktopKeyId: computedDesktop.keyId,
    sas6: bundle.sas6,
    biometricEnrolled: Boolean(bundle.bioSpkiB64 && keySummary.bioKeyId),
    expiresAt: activateJson.expiresAt,
  };
  params.onStageChange?.('awaiting_desktop_sas', preview);

  // 轮询桌面 SAS 确认状态直到 active + 验证 CF-PAIR-ACK-V1
  const pollIntervalMs = params.pollIntervalMs ?? 1000;
  const pairRecordSha256 = sha256HexLower(bundle.pairRecordV1Text);
  let approvedStatus: PairingStatusApiResponse | null = null;
  const pollDeadlineMs = Math.max(Date.parse(activateJson.expiresAt) || Date.now() + 120_000, Date.now() + 10_000);

  while (Date.now() < pollDeadlineMs) {
    if (params.signal?.aborted) {
      throw new DOMException('Pairing aborted', 'AbortError');
    }

    const statusRes = await fetchImpl(
      `${portalOrigin}/api/auth/mobile/pairings/${encodeURIComponent(activateJson.pairingId)}/status`,
      {
        method: 'GET',
        headers: {
          Authorization: `Bearer ${activateJson.pairingPollToken}`,
        },
        signal: params.signal,
      },
    );
    if (!statusRes.ok) {
      await parseErrorResponse(statusRes);
    }

    const st = (await statusRes.json()) as PairingStatusApiResponse;
    const statusLower = st.status.toLowerCase();
    if (statusLower === 'active') {
      approvedStatus = st;
      break;
    }
    if (statusLower === 'revoked' || statusLower === 'expired' || statusLower === 'denied') {
      throw new RemoteApiException(
        classifyRemoteError('pairing_denied_or_expired', 403, `桌面配对已被拒绝或已过期（${st.status}）。`),
      );
    }

    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => {
        params.signal?.removeEventListener('abort', onAbort);
        resolve();
      }, pollIntervalMs);
      const onAbort = () => {
        clearTimeout(timer);
        reject(new DOMException('Pairing aborted', 'AbortError'));
      };
      params.signal?.addEventListener('abort', onAbort, { once: true });
    });
  }

  if (!approvedStatus) {
    throw new RemoteApiException(
      classifyRemoteError('pairing_timeout', 408, '等待桌面端确认 6 位验证码超时，请重新扫码。'),
    );
  }

  if (!approvedStatus.pairAckV1Text || !approvedStatus.pairAckSigB64U) {
    throw new RemoteApiException(
      classifyRemoteError('pair_ack_signature_invalid', 403, '桌面确认回执缺少数字签名。'),
    );
  }

  const ackOk = await verifyPairAckSignatureAsync({
    desktopSpkiB64: activateJson.desktopSpkiB64,
    pairingId: activateJson.pairingId,
    pairRecordSha256,
    pairAckV1Text: approvedStatus.pairAckV1Text,
    pairAckSigB64U: approvedStatus.pairAckSigB64U,
  });
  if (!ackOk) {
    throw new RemoteApiException(
      classifyRemoteError('pair_ack_signature_invalid', 403, 'CF-PAIR-ACK-V1 桌面签名验签失败。'),
    );
  }

  params.onStageChange?.('exchanging_tokens', preview);
  const exNonce = randomHex32();
  const exTs = Math.floor(Date.now() / 1000);
  const { signatureB64U } = await cryptoPlugin.signExchange({
    pairingId: activateJson.pairingId,
    nonceHex32: exNonce,
    unixTimestamp: exTs,
  });

  // 同 nonce+ts 在 60s 幂等重试窗口内支持弱网重试
  let exchangeJson: ExchangeApiResponse | null = null;
  let lastErr: unknown = null;
  for (let attempt = 0; attempt < 3; attempt += 1) {
    try {
      const exRes = await fetchImpl(
        `${portalOrigin}/api/auth/mobile/pairings/${encodeURIComponent(activateJson.pairingId)}/exchange`,
        {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${activateJson.pairingPollToken}`,
          },
          body: JSON.stringify({
            mobileId: bundle.mobileId,
            nonceHex32: exNonce,
            unixTimestamp: exTs,
            signatureB64U,
          }),
          signal: params.signal,
        },
      );
      if (!exRes.ok) {
        if (exRes.status >= 500 && attempt < 2) {
          await new Promise((r) => setTimeout(r, 400 * (attempt + 1)));
          continue;
        }
        await parseErrorResponse(exRes);
      }
      exchangeJson = (await exRes.json()) as ExchangeApiResponse;
      break;
    } catch (err) {
      if (err instanceof RemoteApiException) {
        throw err;
      }
      lastErr = err;
      if (attempt < 2 && !params.signal?.aborted) {
        await new Promise((r) => setTimeout(r, 400 * (attempt + 1)));
        continue;
      }
    }
  }

  if (!exchangeJson) {
    throw lastErr instanceof Error ? lastErr : new Error('配对兑换正式凭据失败。');
  }

  const persisted: PersistedSecurePairingState = {
    portalUrl: portalOrigin,
    deviceId: parsedQr.deviceId,
    desktopName: '桌面工作站',
    bindingVersion: exchangeJson.bindingVersion,
    desktopSpkiB64: activateJson.desktopSpkiB64,
    desktopSpkiSha256: computedDesktop.spkiSha256,
    desktopKeyId: computedDesktop.keyId,
    pairingId: exchangeJson.pairingId,
    mobileId: exchangeJson.mobileId,
    accessToken: exchangeJson.accessToken,
    accessExpiresAt: exchangeJson.accessExpiresAt,
    refreshToken: exchangeJson.refreshToken,
    refreshExpiresAt: exchangeJson.refreshExpiresAt,
    biometricEnrolled: preview.biometricEnrolled,
    apnsPushEnabled: false,
  };

  await cryptoPlugin.saveSecureState({ stateJson: JSON.stringify(persisted) });
  params.onStageChange?.('completed', preview);
  return persisted;
}
