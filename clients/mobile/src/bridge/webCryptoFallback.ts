// ONLY FOR DEV / VITEST NON-PRODUCTION ENVIRONMENTS.
// Production builds exclude this module via import.meta.env.DEV / MODE guards.

import {
  base64UrlToBytes,
  buildBioPopV1,
  buildExchangeV1,
  buildPairV1,
  buildRequestSigV1,
  buildSasV1Input,
  bytesToBase64,
  bytesToBase64Url,
  sas6FromSha256Digest,
  utf8Encode,
} from '@codexflow-shared/remoteProtocol';
import { hmacSha256HexLower, sha256Bytes, sha256HexLower } from '@/crypto/incrementalSha256';
import { normalizeP1363LowS } from '@/crypto/p256Verify';
import type {
  CodexFlowCryptoPlugin,
  DeviceKeyMaterialSummary,
  NativeCryptoCapabilities,
  SignPairingBundleParams,
  SignedPairingBundle,
  StructuredRelaySignParams,
} from './nativeCryptoTypes';

const BIOMETRIC_ROUTE_REGEXES: readonly RegExp[] = [
  /^\/api\/gateway\/approvals\/[^/]+$/,
  /^\/api\/gateway\/chat$/,
  /^\/api\/gateway\/session\/[^/]+\/plan-mode$/,
  /^\/api\/gateway\/session\/[^/]+\/browser\/close$/,
  /^\/api\/gateway\/session\/[^/]+\/computer\/close$/,
  /^\/api\/gateway\/computer\/emergency-stop$/,
];

async function generateEcdsaP256Key(): Promise<{
  keyPair: CryptoKeyPair;
  spkiB64: string;
  spkiSha256: string;
  keyId: string;
}> {
  const keyPair = await globalThis.crypto.subtle.generateKey(
    { name: 'ECDSA', namedCurve: 'P-256' },
    false,
    ['sign', 'verify'],
  );
  const spkiAb = await globalThis.crypto.subtle.exportKey('spki', keyPair.publicKey);
  const spkiBytes = new Uint8Array(spkiAb);
  const spkiB64 = bytesToBase64(spkiBytes);
  const spkiSha256 = sha256HexLower(spkiBytes);
  return {
    keyPair,
    spkiB64,
    spkiSha256,
    keyId: spkiSha256.slice(0, 32),
  };
}

async function signP256LowS(privateKey: CryptoKey, payload: string): Promise<string> {
  const data = utf8Encode(payload);
  const copy = new Uint8Array(data.byteLength);
  copy.set(data);
  const rawSig = await globalThis.crypto.subtle.sign(
    { name: 'ECDSA', hash: 'SHA-256' },
    privateKey,
    copy.buffer,
  );
  const lowS = normalizeP1363LowS(new Uint8Array(rawSig));
  return bytesToBase64Url(lowS);
}

export class WebCryptoFallbackPlugin implements CodexFlowCryptoPlugin {
  private mobileId: string | null = null;
  private sessionKey: Awaited<ReturnType<typeof generateEcdsaP256Key>> | null = null;
  private bioKey: Awaited<ReturnType<typeof generateEcdsaP256Key>> | null = null;
  private secureStateJson: string | null = null;
  private biometricAvailable = true;

  public constructor(options?: { biometricAvailable?: boolean }) {
    if (options?.biometricAvailable !== undefined) {
      this.biometricAvailable = options.biometricAvailable;
    }
  }

  public async getCapabilities(): Promise<NativeCryptoCapabilities> {
    return {
      platform: 'web',
      hardwareBacked: false,
      biometricAvailable: this.biometricAvailable,
      qrScannerAvailable: false,
    };
  }

  public async ensureDeviceKeys(options: {
    requireBiometricKey: boolean;
  }): Promise<DeviceKeyMaterialSummary> {
    if (!this.mobileId) {
      this.mobileId = globalThis.crypto.randomUUID();
    }
    if (!this.sessionKey) {
      this.sessionKey = await generateEcdsaP256Key();
    }
    if (options.requireBiometricKey && this.biometricAvailable && !this.bioKey) {
      this.bioKey = await generateEcdsaP256Key();
    }
    return {
      mobileId: this.mobileId,
      sessionSpkiB64: this.sessionKey.spkiB64,
      sessionKeyId: this.sessionKey.keyId,
      bioSpkiB64: this.bioKey?.spkiB64 ?? null,
      bioKeyId: this.bioKey?.keyId ?? null,
    };
  }

  public async signPairingBundle(params: SignPairingBundleParams): Promise<SignedPairingBundle> {
    const keys = await this.ensureDeviceKeys({ requireBiometricKey: true });
    if (!this.sessionKey) {
      throw new Error('Session key uninitialized.');
    }

    const pairRecordV1Text = buildPairV1(
      params.ticketId,
      params.deviceId,
      params.bindingVersion,
      keys.mobileId,
      params.desktopKeyId,
      params.desktopSpkiSha256,
      this.sessionKey.keyId,
      this.sessionKey.spkiSha256,
      this.bioKey?.keyId ?? null,
      this.bioKey?.spkiSha256 ?? null,
      params.pairingNonceHex32,
      params.expiresAtUnix,
    );
    const pairRecordSigB64U = await signP256LowS(this.sessionKey.keyPair.privateKey, pairRecordV1Text);

    let bioPopV1Text: string | null = null;
    let bioPopSigB64U: string | null = null;
    if (this.bioKey) {
      bioPopV1Text = buildBioPopV1(
        keys.mobileId,
        params.ticketId,
        this.bioKey.keyId,
        params.pairingNonceHex32,
        params.expiresAtUnix,
      );
      bioPopSigB64U = await signP256LowS(this.bioKey.keyPair.privateKey, bioPopV1Text);
    }

    const secretBytes = base64UrlToBytes(params.pairingSecretB64U);
    if (secretBytes.byteLength !== 32) {
      throw new Error('pairingSecret must decode to 32 bytes.');
    }
    const hmacHex = hmacSha256HexLower(secretBytes, utf8Encode(pairRecordV1Text));
    const sas6 = sas6FromSha256Digest(sha256Bytes(buildSasV1Input(pairRecordV1Text)));

    return {
      mobileId: keys.mobileId,
      sessionSpkiB64: keys.sessionSpkiB64,
      bioSpkiB64: keys.bioSpkiB64,
      pairRecordV1Text,
      pairRecordSigB64U,
      bioPopV1Text,
      bioPopSigB64U,
      hmacHex,
      sas6,
    };
  }

  public async signExchange(params: {
    pairingId: string;
    nonceHex32: string;
    unixTimestamp: number;
  }): Promise<{ signatureB64U: string }> {
    if (!this.sessionKey || !this.mobileId) {
      throw new Error('Device keys not initialized.');
    }
    const text = buildExchangeV1(
      params.pairingId,
      this.mobileId,
      params.nonceHex32,
      params.unixTimestamp,
    );
    return {
      signatureB64U: await signP256LowS(this.sessionKey.keyPair.privateKey, text),
    };
  }

  public async signRelayRequest(
    params: StructuredRelaySignParams,
  ): Promise<{ keyId: string; sigB64U: string }> {
    if (!this.sessionKey) {
      throw new Error('Device session key not initialized.');
    }
    if (params.keyPurpose === 'biometric_approval') {
      if (!this.bioKey) {
        throw new Error('biometric_key_unavailable');
      }
      if (!BIOMETRIC_ROUTE_REGEXES.some((rx) => rx.test(params.canonicalPath))) {
        throw new Error('Route not permitted for biometric signing key.');
      }
    }

    const activeKey =
      params.keyPurpose === 'biometric_approval' ? this.bioKey! : this.sessionKey;
    const text = buildRequestSigV1({
      deviceId: params.deviceId,
      bindingVersion: params.bindingVersion,
      sessionEpochHex32: params.sessionEpochHex32,
      mobileId: params.mobileId,
      keyId: activeKey.keyId,
      keyPurpose: params.keyPurpose,
      methodUpper: params.methodUpper,
      canonicalPath: params.canonicalPath,
      canonicalQuery: params.canonicalQuery,
      ifNoneMatchOrEmpty: params.ifNoneMatchOrEmpty,
      uploadIdOrEmpty: params.uploadIdOrEmpty,
      fileSha256OrEmpty: params.fileSha256OrEmpty,
      clientOpIdOrEmpty: params.clientOpIdOrEmpty,
      canonicalContentType: params.canonicalContentType,
      declaredContentLength: params.declaredContentLength,
      bodySha256LowerHex: params.bodySha256LowerHex,
      unixTimestampSeconds: params.unixTimestampSeconds,
      nonceHex32: params.nonceHex32,
    });
    const sigB64U = await signP256LowS(activeKey.keyPair.privateKey, text);
    return { keyId: activeKey.keyId, sigB64U };
  }

  public async saveSecureState(params: { stateJson: string }): Promise<void> {
    this.secureStateJson = params.stateJson;
  }

  public async loadSecureState(): Promise<{ stateJson: string | null }> {
    return { stateJson: this.secureStateJson };
  }

  public async clearAllSecureState(): Promise<void> {
    this.mobileId = null;
    this.sessionKey = null;
    this.bioKey = null;
    this.secureStateJson = null;
  }

  public async scanQrCode(): Promise<{ rawUri: string }> {
    throw new Error('QR camera scanning requires iOS/Android native build.');
  }
}
