import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import { buildSingleFileMultipart } from '@codexflow-shared/multipartBuilder';
import {
  BackendManager,
  ensureRemoteProfileOwnerAnchor,
} from '../../../electron/main/backendManager';
import { RemoteConfigStore, type SecretProtector } from '../../../electron/main/remote/remoteConfigStore';
import { sha256HexLower } from '../../../electron/main/remote/remoteCrypto';
import {
  RemoteDialController,
  parseCustomProxyUrl,
  parsePacProxyDirective,
  type WsTransportSession,
} from '../../../electron/main/remote/remoteDialController';
import { RemoteUploadStaging } from '../../../electron/main/remote/remoteUploadStaging';
import { ReplayNonceGuard } from '../../../electron/main/remote/replayNonceGuard';

function createMemoryProtector(available = true): SecretProtector {
  return {
    isEncryptionAvailable: () => available,
    encryptString: (plain) => `enc:${Buffer.from(plain, 'utf8').toString('base64')}`,
    decryptString: (cipher) => {
      if (!cipher.startsWith('enc:')) {
        throw new Error('corrupt cipher');
      }
      return Buffer.from(cipher.slice(4), 'base64').toString('utf8');
    },
  };
}

function createNoopTransport(): WsTransportSession {
  return {
    sendBinary: vi.fn(),
    sendPing: vi.fn(),
    sendPong: vi.fn(),
    close: vi.fn(),
    onBinaryMessage: vi.fn(),
    onPing: vi.fn(),
    onPong: vi.fn(),
    onClose: vi.fn(),
    onError: vi.fn(),
  };
}

describe('Remote Core Runtime Modules (Config, Nonce, Staging, Dial, Isolation)', () => {
  it('RemoteConfigStore protects secrets, enforces origin/proxy validation, and advances watermarks monotonically', () => {
    const tempDir = mkdtempSync(join(tmpdir(), 'cf-rcfg-'));
    try {
      const store = new RemoteConfigStore({
        userDataDir: tempDir,
        secretProtector: createMemoryProtector(true),
      });

      expect(() => store.setPortalUrl('http://remote.example.com')).toThrow(/HTTPS/);
      expect(() => store.setPortalUrl('https://user:pass@remote.example.com')).toThrow(
        /credentials/,
      );
      expect(store.setPortalUrl('http://127.0.0.1:5050/')).toBe('http://127.0.0.1:5050');
      expect(store.setPortalUrl('https://portal.codexflow.example/api')).toBe(
        'https://portal.codexflow.example',
      );

      const pending = store.preparePendingDesktopIdentity({
        deviceName: 'Test PC',
        platform: 'win32',
        dataDirectory: tempDir,
      });

      store.commitDesktopBinding({
        portalUrl: 'https://portal.codexflow.example',
        userId: 'user-1',
        accountEmail: 'alice@example.com',
        deviceId: pending.deviceId,
        deviceName: 'Test PC',
        hostInstanceId: pending.hostInstanceId,
        bindingVersion: 2,
        dataDirHash: pending.dataDirHash,
        desktopSpkiB64: pending.desktopSpkiB64,
        desktopKeyId: pending.desktopKeyId,
        desktopSpkiSha256: pending.desktopSpkiSha256,
        desktopPrivateKeyPem: pending.desktopPrivateKeyPem,
        deviceToken: 'cfd_super_secret_device_token',
        revocationSeq: 5,
      });

      store.setProxySettings({
        mode: 'custom',
        customUrl: 'http://127.0.0.1:7890',
        username: 'proxy-user',
        password: 'super-secret-proxy-password',
      });

      const snap = store.getPublicSnapshot();
      const snapJson = JSON.stringify(snap);
      expect(snap.bound).toBe(true);
      expect(snap.hasDeviceToken).toBe(true);
      expect(snap.proxy.hasPassword).toBe(true);
      expect(snapJson).not.toContain('cfd_super_secret_device_token');
      expect(snapJson).not.toContain('PRIVATE KEY');
      expect(snapJson).not.toContain('super-secret-proxy-password');

      const rawDisk = readFileSync(store.getConfigPath(), 'utf8');
      expect(rawDisk).not.toContain('cfd_super_secret_device_token');
      expect(rawDisk).not.toContain('super-secret-proxy-password');

      store.advanceWatermarks(10, 12);
      store.advanceWatermarks(3, 4); // stale seq must be ignored
      expect(store.getPublicSnapshot().watermarks).toEqual({
        accountRevocationSeq: 10,
        deviceRevocationSeq: 12,
      });
    } finally {
      rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('ReplayNonceGuard enforces 60s skew, rejects replayed nonces, and caps buckets', () => {
    const guard = new ReplayNonceGuard({ ttlMs: 10_000, maxSlotsPerBucket: 3, maxSkewSeconds: 60 });
    const t0Ms = 1_700_000_000_000;
    const t0Sec = Math.floor(t0Ms / 1000);

    expect(
      guard.checkAndConsume({
        mobileId: 'mob-1',
        keyId: 'a'.repeat(32),
        sessionEpochHex32: 'b'.repeat(32),
        nonceHex32: 'c'.repeat(32),
        unixTimestampSeconds: t0Sec + 61,
        nowMs: t0Ms,
      }).ok,
    ).toBe(false);

    expect(
      guard.checkAndConsume({
        mobileId: 'mob-1',
        keyId: 'a'.repeat(32),
        sessionEpochHex32: 'b'.repeat(32),
        nonceHex32: 'c'.repeat(32),
        unixTimestampSeconds: t0Sec,
        nowMs: t0Ms,
      }).ok,
    ).toBe(true);

    const replay = guard.checkAndConsume({
      mobileId: 'mob-1',
      keyId: 'a'.repeat(32),
      sessionEpochHex32: 'b'.repeat(32),
      nonceHex32: 'c'.repeat(32),
      unixTimestampSeconds: t0Sec,
      nowMs: t0Ms,
    });
    expect(replay.ok).toBe(false);
    if (!replay.ok) {
      expect(replay.code).toBe('replay_nonce_detected');
    }

    // Evicts oldest when exceeding maxSlotsPerBucket
    for (let i = 1; i <= 4; i += 1) {
      guard.checkAndConsume({
        mobileId: 'mob-1',
        keyId: 'a'.repeat(32),
        sessionEpochHex32: 'b'.repeat(32),
        nonceHex32: String(i).padStart(32, '0'),
        unixTimestampSeconds: t0Sec,
        nowMs: t0Ms + i,
      });
    }
    expect(
      guard.checkAndConsume({
        mobileId: 'mob-1',
        keyId: 'a'.repeat(32),
        sessionEpochHex32: 'b'.repeat(32),
        nonceHex32: '0'.repeat(31) + '4',
        unixTimestampSeconds: t0Sec,
        nowMs: t0Ms + 5,
      }).ok,
    ).toBe(false);
  });

  it('RemoteUploadStaging cleans orphans, enforces disk/quota limits, and streams/reassembles valid multipart uploads', async () => {
    const tempDir = mkdtempSync(join(tmpdir(), 'cf-ustage-'));
    try {
      const preDir = join(tempDir, 'remote-upload-staging');
      mkdirSync(preDir, { recursive: true });
      const orphanFile = join(preDir, '.stg_orphan_1234.tmp');
      writeFileSync(orphanFile, 'stale');

      const staging = new RemoteUploadStaging({
        userDataDir: tempDir,
        maxGlobalBytes: 1024 * 1024,
        maxSingleFileBytes: 1024,
        minFreeDiskBytes: 500,
        getFreeDiskBytes: async () => 10_000,
      });

      const removed = staging.cleanupOrphansOnStartup();
      expect(removed).toBe(1);
      expect(existsSync(orphanFile)).toBe(false);

      const fileBytes = Buffer.from('hello staged upload payload', 'utf8');
      const fileSha = sha256HexLower(fileBytes);
      const bodySha = sha256HexLower(
        buildSingleFileMultipart({
          fieldName: 'file',
          fileName: 'note.txt',
          contentType: 'text/plain',
          fileBytes,
          boundary: 'stage-b-1',
        }).bodyBytes,
      );
      const mp = buildSingleFileMultipart({
        fieldName: 'file',
        fileName: 'note.txt',
        contentType: 'text/plain',
        fileBytes,
        boundary: 'stage-b-1',
      });

      const lowDiskStaging = new RemoteUploadStaging({
        userDataDir: tempDir,
        minFreeDiskBytes: 500,
        getFreeDiskBytes: async () => 100,
      });
      const lowDiskRes = await lowDiskStaging.stageAndVerifyMultipartUpload({
        pairingId: 'pair-1',
        uploadId: 'up-1',
        declaredContentLength: mp.bodyBytes.byteLength,
        declaredBodySha256: bodySha,
        declaredFileSha256: fileSha,
        contentTypeHeader: mp.contentType,
        chunks: (async function* () {
          yield mp.bodyBytes;
        })(),
        signal: new AbortController().signal,
      });
      expect(lowDiskRes.ok).toBe(false);
      if (!lowDiskRes.ok) {
        expect(lowDiskRes.statusCode).toBe(507);
      }

      const tooLarge = await staging.stageAndVerifyMultipartUpload({
        pairingId: 'pair-1',
        uploadId: 'up-2',
        declaredContentLength: 200 * 1024,
        declaredBodySha256: bodySha,
        declaredFileSha256: fileSha,
        contentTypeHeader: mp.contentType,
        chunks: (async function* () {})(),
        signal: new AbortController().signal,
      });
      expect(tooLarge.ok).toBe(false);
      if (!tooLarge.ok) {
        expect(tooLarge.statusCode).toBe(413);
      }

      const okRes = await staging.stageAndVerifyMultipartUpload({
        pairingId: 'pair-1',
        uploadId: 'up-3',
        declaredContentLength: mp.bodyBytes.byteLength,
        declaredBodySha256: bodySha,
        declaredFileSha256: fileSha,
        contentTypeHeader: mp.contentType,
        chunks: (async function* () {
          yield mp.bodyBytes.subarray(0, 20);
          yield mp.bodyBytes.subarray(20);
        })(),
        signal: new AbortController().signal,
      });
      expect(okRes.ok).toBe(true);
      if (okRes.ok) {
        expect(okRes.forward.fileSha256).toBe(fileSha);
        expect(okRes.forward.fileSizeBytes).toBe(fileBytes.byteLength);
      }
      expect(staging.getActiveStagedBytes()).toBe(0);
    } finally {
      rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('RemoteDialController resolves PAC/custom proxies, halts terminally on 407, and guards dialGeneration races', async () => {
    expect(parsePacProxyDirective('DIRECT; PROXY 127.0.0.1:8080')).toBeNull();
    expect(parsePacProxyDirective('SOCKS5 10.0.0.5:1080; DIRECT')?.rawUrl).toBe(
      'socks5://10.0.0.5:1080',
    );
    expect(parseCustomProxyUrl('http://proxy.internal:3128')?.kind).toBe('http');

    let currentProxyMode: 'system' | 'direct' | 'custom' = 'system';
    const dialCalls: Array<{ proxyRaw: string | null }> = [];
    const dial = new RemoteDialController({
      resolveSystemProxy: async () => 'PROXY 127.0.0.1:8888',
      dialWebSocket: async (params) => {
        dialCalls.push({ proxyRaw: params.proxy?.rawUrl ?? null });
        if (currentProxyMode === 'system') {
          return {
            ok: false,
            terminal: true,
            state: 'auth_required_407',
            statusCode: 407,
            message: 'Proxy authentication required (407).',
          };
        }
        return {
          ok: true,
          transport: createNoopTransport(),
        };
      },
      getDialParams: () => ({
        portalUrl: 'https://portal.codexflow.example',
        deviceToken: 'cfd_tok',
        manualPaused: false,
        proxyMode: currentProxyMode,
        customProxyUrl: '',
        proxyUsername: '',
        proxyPassword: null,
      }),
      onSessionConnected: async () => new Promise<void>(() => {}),
      baseBackoffMs: 10,
    });

    dial.startOrReconnect();
    await new Promise((r) => setTimeout(r, 30));
    expect(dial.getStatus().state).toBe('auth_required_407');
    expect(dialCalls).toHaveLength(1);
    expect(dialCalls[0]?.proxyRaw).toBe('http://127.0.0.1:8888');

    dial.handlePowerSuspend();
    expect(dial.getStatus().state).toBe('paused');

    currentProxyMode = 'direct';
    dial.handlePowerResume();
    await new Promise((r) => setTimeout(r, 30));
    expect(dial.getStatus().state).toBe('connected');
    dial.stop();
  });

  it('BackendManager isolates account profiles via CODEXFLOW_HOME + .codexflow-remote-owner.json and never leaks bridge/control tokens', async () => {
    const tempDir = mkdtempSync(join(tmpdir(), 'cf-bmiso-'));
    const userAHash = 'a'.repeat(32);
    const userBHash = 'b'.repeat(32);
    try {
      const bm = new BackendManager({
        userDataDir: tempDir,
        resourcesPath: tempDir,
        isPackaged: false,
        isDev: false,
        initialRemoteUserIdHash: userAHash,
        getProxyOrigin: () => null,
        onChange: () => {},
      });

      expect(bm.getCodexflowHomeOverride()).toBe(bm.resolveRemoteAccountHomeDir(userAHash));
      expect(bm.getDataDirectory()).toBe(bm.resolveRemoteAccountDataDir(userAHash));
      expect(bm.getActiveRemoteUserIdHash()).toBe(userAHash);

      const ownerAnchor = join(tempDir, 'remote-profiles', userAHash, '.codexflow-remote-owner.json');
      expect(existsSync(ownerAnchor)).toBe(true);
      expect(() => ensureRemoteProfileOwnerAnchor(join(tempDir, 'remote-profiles', userAHash), userBHash)).toThrow(
        /owner mismatch/,
      );

      const publicJson = JSON.stringify(bm.getPublicInfo());
      expect(publicJson).not.toContain('remoteBridgeToken');
      expect(publicJson).not.toContain('controlToken');

      await bm.dispose();
    } finally {
      rmSync(tempDir, { recursive: true, force: true });
    }
  });
});
