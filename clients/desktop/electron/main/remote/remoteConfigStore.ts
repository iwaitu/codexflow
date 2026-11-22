import { chmodSync, existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { generateP256KeyPair, randomHex32, sha256HexLower } from './remoteCrypto';

export type RemoteProxyMode = 'system' | 'direct' | 'custom';

export interface StoredRemotePairing {
  readonly pairingId: string;
  readonly mobileId: string;
  readonly platform: string;
  readonly deviceName: string;
  readonly bindingVersion: number;
  readonly sessionSpkiB64: string;
  readonly sessionKeyId: string;
  readonly bioSpkiB64: string | null;
  readonly bioKeyId: string | null;
  readonly status: 'active' | 'revoked';
  readonly pairedAtUtc: string;
  readonly revokedAtUtc?: string | null;
}

export interface RemoteProxySettings {
  readonly mode: RemoteProxyMode;
  readonly customUrl: string;
  readonly username: string;
}

export interface RemoteAccountAnchor {
  readonly userId: string;
  readonly userIdHash: string;
  readonly accountEmail: string;
}

export interface RemoteDeviceBindingMeta {
  readonly deviceId: string;
  readonly deviceName: string;
  readonly hostInstanceId: string;
  readonly bindingVersion: number;
  readonly dataDirHash: string;
  readonly desktopSpkiB64: string;
  readonly desktopKeyId: string;
  readonly desktopSpkiSha256: string;
}

interface EncryptedRemoteSecrets {
  deviceToken?: string;
  desktopPrivateKeyPem?: string;
  proxyPassword?: string;
}

interface PersistedRemoteConfigFile {
  version: 1;
  portalUrl: string;
  account: RemoteAccountAnchor | null;
  device: RemoteDeviceBindingMeta | null;
  manualRemotePaused: boolean;
  watermarks: {
    accountRevocationSeq: number;
    deviceRevocationSeq: number;
  };
  pairings: StoredRemotePairing[];
  proxy: RemoteProxySettings;
  encryptedSecrets: EncryptedRemoteSecrets;
}

export interface RemoteConfigPublicSnapshot {
  readonly portalUrl: string;
  readonly account: RemoteAccountAnchor | null;
  readonly device: RemoteDeviceBindingMeta | null;
  readonly bound: boolean;
  readonly hasDeviceToken: boolean;
  readonly manualRemotePaused: boolean;
  readonly watermarks: {
    readonly accountRevocationSeq: number;
    readonly deviceRevocationSeq: number;
  };
  readonly pairings: readonly StoredRemotePairing[];
  readonly proxy: RemoteProxySettings & { readonly hasPassword: boolean };
}

export interface SecretProtector {
  isEncryptionAvailable(): boolean;
  encryptString(plainText: string): string;
  decryptString(cipherText: string): string;
}

interface ElectronSafeStorageLike {
  isEncryptionAvailable(): boolean;
  encryptString(plainText: string): Buffer;
  decryptString(encrypted: Buffer): string;
}

const nodeRequire = createRequire(import.meta.url);

export function createElectronSafeStorageProtector(): SecretProtector {
  const resolveSafeStorage = (): ElectronSafeStorageLike | null => {
    try {
      const electronModule = nodeRequire('electron') as {
        safeStorage?: ElectronSafeStorageLike;
      };
      return electronModule?.safeStorage ?? null;
    } catch {
      return null;
    }
  };

  return {
    isEncryptionAvailable(): boolean {
      const s = resolveSafeStorage();
      return Boolean(s && s.isEncryptionAvailable());
    },
    encryptString(plainText: string): string {
      const s = resolveSafeStorage();
      if (!s || !s.isEncryptionAvailable()) {
        throw new Error('safe_storage_unavailable');
      }
      return s.encryptString(plainText).toString('base64');
    },
    decryptString(cipherText: string): string {
      const s = resolveSafeStorage();
      if (!s || !s.isEncryptionAvailable()) {
        throw new Error('safe_storage_unavailable');
      }
      return s.decryptString(Buffer.from(cipherText, 'base64'));
    },
  };
}

function defaultPersistedState(): PersistedRemoteConfigFile {
  return {
    version: 1,
    portalUrl: '',
    account: null,
    device: null,
    manualRemotePaused: false,
    watermarks: {
      accountRevocationSeq: 0,
      deviceRevocationSeq: 0,
    },
    pairings: [],
    proxy: {
      mode: 'system',
      customUrl: '',
      username: '',
    },
    encryptedSecrets: {},
  };
}

export function normalizePortalOrigin(rawUrl: string): string {
  const parsed = new URL(rawUrl.trim());
  if (parsed.protocol === 'http:') {
    const host = parsed.hostname.toLowerCase();
    const isLoopback = host === '127.0.0.1' || host === 'localhost' || host === '[::1]' || host === '::1';
    if (!isLoopback) {
      throw new Error('Portal URL must use HTTPS (plain HTTP is only allowed for loopback development).');
    }
  } else if (parsed.protocol !== 'https:') {
    throw new Error('Portal URL must start with https:// or loopback http://.');
  }
  if (parsed.username || parsed.password) {
    throw new Error('Portal URL must not embed credentials.');
  }
  return parsed.origin;
}

export class RemoteConfigStore {
  private readonly filePath: string;
  private readonly protector: SecretProtector;
  private state: PersistedRemoteConfigFile;

  constructor(options: { userDataDir: string; secretProtector?: SecretProtector }) {
    this.filePath = join(options.userDataDir, 'remote-config.json');
    this.protector = options.secretProtector ?? createElectronSafeStorageProtector();
    this.state = this.loadFromDisk();
  }

  public getConfigPath(): string {
    return this.filePath;
  }

  public isEncryptionAvailable(): boolean {
    return this.protector.isEncryptionAvailable();
  }

  public getPublicSnapshot(): RemoteConfigPublicSnapshot {
    return {
      portalUrl: this.state.portalUrl,
      account: this.state.account ? { ...this.state.account } : null,
      device: this.state.device ? { ...this.state.device } : null,
      bound: Boolean(
        this.state.account &&
          this.state.device &&
          this.state.encryptedSecrets.deviceToken &&
          this.state.encryptedSecrets.desktopPrivateKeyPem,
      ),
      hasDeviceToken: Boolean(this.state.encryptedSecrets.deviceToken),
      manualRemotePaused: this.state.manualRemotePaused,
      watermarks: { ...this.state.watermarks },
      pairings: this.state.pairings.map((p) => ({ ...p })),
      proxy: {
        ...this.state.proxy,
        hasPassword: Boolean(this.state.encryptedSecrets.proxyPassword),
      },
    };
  }

  public getPortalUrl(): string {
    return this.state.portalUrl;
  }

  public setPortalUrl(rawUrl: string): string {
    const origin = normalizePortalOrigin(rawUrl);
    this.state.portalUrl = origin;
    this.saveToDisk();
    return origin;
  }

  public getManualPaused(): boolean {
    return this.state.manualRemotePaused;
  }

  public setManualPaused(paused: boolean): void {
    this.state.manualRemotePaused = Boolean(paused);
    this.saveToDisk();
  }

  public willSwitchAccountOwner(nextUserId: string): boolean {
    if (!this.state.account) {
      return false;
    }
    return this.state.account.userId.toLowerCase() !== nextUserId.trim().toLowerCase();
  }

  public computeUserIdHash(userId: string): string {
    return sha256HexLower(userId.trim().toLowerCase()).slice(0, 32);
  }

  public preparePendingDesktopIdentity(params: {
    deviceName: string;
    platform: string;
    dataDirectory: string;
  }): {
    deviceId: string;
    deviceName: string;
    hostInstanceId: string;
    dataDirHash: string;
    desktopSpkiB64: string;
    desktopKeyId: string;
    desktopSpkiSha256: string;
    desktopPrivateKeyPem: string;
  } {
    const existingPrivatePem = this.getDesktopPrivateKeyPem();
    if (this.state.device && existingPrivatePem) {
      const dataDirHash = sha256HexLower(params.dataDirectory.toLowerCase());
      return {
        deviceId: this.state.device.deviceId,
        deviceName: params.deviceName,
        hostInstanceId: this.state.device.hostInstanceId,
        dataDirHash,
        desktopSpkiB64: this.state.device.desktopSpkiB64,
        desktopKeyId: this.state.device.desktopKeyId,
        desktopSpkiSha256: this.state.device.desktopSpkiSha256,
        desktopPrivateKeyPem: existingPrivatePem,
      };
    }

    const kp = generateP256KeyPair();
    const deviceId =
      this.state.device?.deviceId ??
      `${randomHex32().slice(0, 8)}-${randomHex32().slice(8, 12)}-4${randomHex32().slice(13, 16)}-a${randomHex32().slice(17, 20)}-${randomHex32().slice(20, 32)}`;
    const hostInstanceId = this.state.device?.hostInstanceId ?? `desktop-${randomHex32()}`;
    return {
      deviceId,
      deviceName: params.deviceName,
      hostInstanceId,
      dataDirHash: sha256HexLower(params.dataDirectory.toLowerCase()),
      desktopSpkiB64: kp.spkiB64,
      desktopKeyId: kp.keyId,
      desktopSpkiSha256: kp.spkiSha256,
      desktopPrivateKeyPem: kp.privateKeyPem,
    };
  }

  public commitDesktopBinding(params: {
    portalUrl: string;
    userId: string;
    accountEmail: string;
    deviceId: string;
    deviceName: string;
    hostInstanceId: string;
    bindingVersion: number;
    dataDirHash: string;
    desktopSpkiB64: string;
    desktopKeyId: string;
    desktopSpkiSha256: string;
    desktopPrivateKeyPem: string;
    deviceToken: string;
    revocationSeq: number;
  }): RemoteAccountAnchor {
    const account: RemoteAccountAnchor = {
      userId: params.userId,
      userIdHash: this.computeUserIdHash(params.userId),
      accountEmail: params.accountEmail,
    };
    const encryptedToken = this.protector.encryptString(params.deviceToken);
    const encryptedPrivateKey = this.protector.encryptString(params.desktopPrivateKeyPem);

    this.state.portalUrl = normalizePortalOrigin(params.portalUrl);
    const switchedAccount =
      this.state.account && this.state.account.userId !== params.userId;
    this.state.account = account;
    this.state.device = {
      deviceId: params.deviceId,
      deviceName: params.deviceName,
      hostInstanceId: params.hostInstanceId,
      bindingVersion: params.bindingVersion,
      dataDirHash: params.dataDirHash,
      desktopSpkiB64: params.desktopSpkiB64,
      desktopKeyId: params.desktopKeyId,
      desktopSpkiSha256: params.desktopSpkiSha256,
    };
    this.state.watermarks = {
      accountRevocationSeq: switchedAccount ? 0 : this.state.watermarks.accountRevocationSeq,
      deviceRevocationSeq: params.revocationSeq,
    };
    if (switchedAccount) {
      this.state.pairings = [];
    }
    this.state.encryptedSecrets.deviceToken = encryptedToken;
    this.state.encryptedSecrets.desktopPrivateKeyPem = encryptedPrivateKey;
    this.saveToDisk();
    return account;
  }

  public clearBindingKeepProxyAndUrl(): void {
    this.state.account = null;
    this.state.device = null;
    this.state.pairings = [];
    this.state.watermarks = { accountRevocationSeq: 0, deviceRevocationSeq: 0 };
    delete this.state.encryptedSecrets.deviceToken;
    delete this.state.encryptedSecrets.desktopPrivateKeyPem;
    this.saveToDisk();
  }

  public getDeviceToken(): string | null {
    const enc = this.state.encryptedSecrets.deviceToken;
    if (!enc) {
      return null;
    }
    try {
      return this.protector.decryptString(enc);
    } catch {
      return null;
    }
  }

  public getDesktopPrivateKeyPem(): string | null {
    const enc = this.state.encryptedSecrets.desktopPrivateKeyPem;
    if (!enc) {
      return null;
    }
    try {
      return this.protector.decryptString(enc);
    } catch {
      return null;
    }
  }

  public getProxySettings(): RemoteProxySettings & { password: string | null } {
    let password: string | null = null;
    if (this.state.encryptedSecrets.proxyPassword) {
      try {
        password = this.protector.decryptString(this.state.encryptedSecrets.proxyPassword);
      } catch {
        password = null;
      }
    }
    return {
      ...this.state.proxy,
      password,
    };
  }

  public setProxySettings(params: {
    mode: RemoteProxyMode;
    customUrl?: string;
    username?: string;
    password?: string | null;
    clearPassword?: boolean;
  }): void {
    const mode = params.mode;
    let customUrl = (params.customUrl ?? '').trim();
    if (mode === 'custom' && customUrl) {
      const u = new URL(customUrl);
      if (u.protocol !== 'http:' && u.protocol !== 'https:' && u.protocol !== 'socks5:') {
        throw new Error('Proxy URL must use http://, https:// or socks5:// scheme.');
      }
      if (u.username || u.password) {
        throw new Error('Proxy credentials must be provided in username/password fields, not embedded in URL.');
      }
      customUrl = u.toString().replace(/\/$/, '');
    }

    const nextEncrypted: EncryptedRemoteSecrets = { ...this.state.encryptedSecrets };
    if (params.clearPassword) {
      delete nextEncrypted.proxyPassword;
    } else if (typeof params.password === 'string' && params.password.length > 0) {
      nextEncrypted.proxyPassword = this.protector.encryptString(params.password);
    }

    this.state.proxy = {
      mode,
      customUrl,
      username: (params.username ?? '').trim(),
    };
    this.state.encryptedSecrets = nextEncrypted;
    this.saveToDisk();
  }

  public advanceWatermarks(accountSeq?: number | null, deviceSeq?: number | null): void {
    let changed = false;
    if (typeof accountSeq === 'number' && accountSeq > this.state.watermarks.accountRevocationSeq) {
      this.state.watermarks.accountRevocationSeq = accountSeq;
      changed = true;
    }
    if (typeof deviceSeq === 'number' && deviceSeq > this.state.watermarks.deviceRevocationSeq) {
      this.state.watermarks.deviceRevocationSeq = deviceSeq;
      changed = true;
    }
    if (changed) {
      this.saveToDisk();
    }
  }

  public upsertActivePairing(pairing: StoredRemotePairing): void {
    const idx = this.state.pairings.findIndex((p) => p.pairingId === pairing.pairingId);
    if (idx >= 0) {
      this.state.pairings[idx] = { ...pairing };
    } else {
      this.state.pairings.push({ ...pairing });
    }
    this.saveToDisk();
  }

  public reconcilePairingsOnHelloAck(activePairingIds: readonly string[], revokedMobileIds: readonly string[]): void {
    const activeSet = new Set(activePairingIds.map((x) => x.toLowerCase()));
    const revokedMobileSet = new Set(revokedMobileIds.map((x) => x.toLowerCase()));
    const nowIso = new Date().toISOString();
    let changed = false;

    for (let i = 0; i < this.state.pairings.length; i += 1) {
      const p = this.state.pairings[i]!;
      const shouldRevoke =
        p.status === 'active' &&
        (revokedMobileSet.has(p.mobileId.toLowerCase()) || !activeSet.has(p.pairingId.toLowerCase()));
      if (shouldRevoke) {
        this.state.pairings[i] = {
          ...p,
          status: 'revoked',
          revokedAtUtc: nowIso,
        };
        changed = true;
      }
    }

    if (changed) {
      this.saveToDisk();
    }
  }

  public markPairingRevoked(pairingId: string): boolean {
    const idx = this.state.pairings.findIndex(
      (p) => p.pairingId.toLowerCase() === pairingId.toLowerCase(),
    );
    if (idx < 0) {
      return false;
    }
    const cur = this.state.pairings[idx]!;
    if (cur.status === 'revoked') {
      return false;
    }
    this.state.pairings[idx] = {
      ...cur,
      status: 'revoked',
      revokedAtUtc: new Date().toISOString(),
    };
    this.saveToDisk();
    return true;
  }

  public markMobileRevoked(mobileId: string): string[] {
    const revokedPairingIds: string[] = [];
    const nowIso = new Date().toISOString();
    let changed = false;

    for (let i = 0; i < this.state.pairings.length; i += 1) {
      const p = this.state.pairings[i]!;
      if (p.mobileId.toLowerCase() === mobileId.toLowerCase() && p.status === 'active') {
        this.state.pairings[i] = {
          ...p,
          status: 'revoked',
          revokedAtUtc: nowIso,
        };
        revokedPairingIds.push(p.pairingId);
        changed = true;
      }
    }

    if (changed) {
      this.saveToDisk();
    }
    return revokedPairingIds;
  }

  public findActivePairingForMobileKey(
    mobileId: string,
    keyId: string,
    keyPurpose: 'device_session' | 'biometric_approval',
  ): { pairing: StoredRemotePairing; spkiB64: string } | null {
    for (const p of this.state.pairings) {
      if (p.status !== 'active' || p.mobileId.toLowerCase() !== mobileId.toLowerCase()) {
        continue;
      }
      if (keyPurpose === 'device_session' && p.sessionKeyId === keyId) {
        return { pairing: p, spkiB64: p.sessionSpkiB64 };
      }
      if (keyPurpose === 'biometric_approval' && p.bioKeyId === keyId && p.bioSpkiB64) {
        return { pairing: p, spkiB64: p.bioSpkiB64 };
      }
    }
    return null;
  }

  private loadFromDisk(): PersistedRemoteConfigFile {
    if (!existsSync(this.filePath)) {
      return defaultPersistedState();
    }
    try {
      const raw = readFileSync(this.filePath, 'utf8');
      const parsed = JSON.parse(raw) as Partial<PersistedRemoteConfigFile>;
      const def = defaultPersistedState();
      return {
        version: 1,
        portalUrl: typeof parsed.portalUrl === 'string' ? parsed.portalUrl : '',
        account: parsed.account ?? null,
        device: parsed.device ?? null,
        manualRemotePaused: Boolean(parsed.manualRemotePaused),
        watermarks: {
          accountRevocationSeq: Number(parsed.watermarks?.accountRevocationSeq ?? 0),
          deviceRevocationSeq: Number(parsed.watermarks?.deviceRevocationSeq ?? 0),
        },
        pairings: Array.isArray(parsed.pairings) ? parsed.pairings : [],
        proxy: {
          mode:
            parsed.proxy?.mode === 'direct' || parsed.proxy?.mode === 'custom'
              ? parsed.proxy.mode
              : 'system',
          customUrl: typeof parsed.proxy?.customUrl === 'string' ? parsed.proxy.customUrl : '',
          username: typeof parsed.proxy?.username === 'string' ? parsed.proxy.username : '',
        },
        encryptedSecrets: parsed.encryptedSecrets ?? def.encryptedSecrets,
      };
    } catch {
      return defaultPersistedState();
    }
  }

  private saveToDisk(): void {
    const dir = dirname(this.filePath);
    mkdirSync(dir, { recursive: true, mode: 0o700 });
    const tmpPath = `${this.filePath}.tmp-${process.pid}-${Date.now()}`;
    const payload = JSON.stringify(this.state, null, 2);
    writeFileSync(tmpPath, payload, { encoding: 'utf8', mode: 0o600 });
    renameSync(tmpPath, this.filePath);
    if (process.platform !== 'win32') {
      try {
        chmodSync(this.filePath, 0o600);
      } catch {
        // Ignore on filesystems that do not support POSIX chmod
      }
    }
  }
}
