import type { KeyPurpose } from '@codexflow-shared/remoteProtocol';

export interface NativeCryptoCapabilities {
  readonly platform: 'ios' | 'android' | 'web';
  readonly hardwareBacked: boolean;
  readonly biometricAvailable: boolean;
  readonly qrScannerAvailable: boolean;
}

export interface DeviceKeyMaterialSummary {
  readonly mobileId: string;
  readonly sessionSpkiB64: string;
  readonly sessionKeyId: string;
  readonly bioSpkiB64: string | null;
  readonly bioKeyId: string | null;
}

export interface SignPairingBundleParams {
  readonly ticketId: string;
  readonly deviceId: string;
  readonly bindingVersion: number;
  readonly desktopKeyId: string;
  readonly desktopSpkiSha256: string;
  readonly pairingNonceHex32: string;
  readonly expiresAtUnix: number;
  readonly pairingSecretB64U: string;
}

export interface SignedPairingBundle {
  readonly mobileId: string;
  readonly sessionSpkiB64: string;
  readonly bioSpkiB64: string | null;
  readonly pairRecordV1Text: string;
  readonly pairRecordSigB64U: string;
  readonly bioPopV1Text: string | null;
  readonly bioPopSigB64U: string | null;
  readonly hmacHex: string;
  readonly sas6: string;
}

export interface StructuredRelaySignParams {
  readonly deviceId: string;
  readonly bindingVersion: number;
  readonly sessionEpochHex32: string;
  readonly mobileId: string;
  readonly keyPurpose: KeyPurpose;
  readonly methodUpper: string;
  readonly canonicalPath: string;
  readonly canonicalQuery: string;
  readonly ifNoneMatchOrEmpty: string;
  readonly uploadIdOrEmpty: string;
  readonly fileSha256OrEmpty: string;
  readonly clientOpIdOrEmpty: string;
  readonly canonicalContentType: string;
  readonly declaredContentLength: number;
  readonly bodySha256LowerHex: string;
  readonly unixTimestampSeconds: number;
  readonly nonceHex32: string;
  readonly promptReason?: string;
}

export interface CodexFlowCryptoPlugin {
  getCapabilities(): Promise<NativeCryptoCapabilities>;
  ensureDeviceKeys(options: { requireBiometricKey: boolean }): Promise<DeviceKeyMaterialSummary>;
  signPairingBundle(params: SignPairingBundleParams): Promise<SignedPairingBundle>;
  signExchange(params: {
    pairingId: string;
    nonceHex32: string;
    unixTimestamp: number;
  }): Promise<{ signatureB64U: string }>;
  signRelayRequest(params: StructuredRelaySignParams): Promise<{ keyId: string; sigB64U: string }>;
  saveSecureState(params: { stateJson: string }): Promise<void>;
  loadSecureState(): Promise<{ stateJson: string | null }>;
  clearAllSecureState(): Promise<void>;
  scanQrCode(): Promise<{ rawUri: string }>;
}

export interface PersistedSecurePairingState {
  readonly portalUrl: string;
  readonly deviceId: string;
  readonly desktopName: string;
  readonly bindingVersion: number;
  readonly desktopSpkiB64: string;
  readonly desktopSpkiSha256: string;
  readonly desktopKeyId: string;
  readonly pairingId: string;
  readonly mobileId: string;
  readonly accessToken: string;
  readonly accessExpiresAt: string;
  readonly refreshToken: string;
  readonly refreshExpiresAt: string;
  readonly biometricEnrolled: boolean;
  readonly apnsPushEnabled: boolean;
}
