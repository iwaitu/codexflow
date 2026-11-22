import { createHash, randomBytes } from 'node:crypto';
import * as net from 'node:net';
import * as tls from 'node:tls';
import { RemoteProxyMode } from './remoteConfigStore';

export type DialConnectionState =
  | 'stopped'
  | 'paused'
  | 'dialing'
  | 'connected'
  | 'backoff'
  | 'auth_required_407'
  | 'forbidden_403'
  | 'unauthorized_401'
  | 'rebind_required'
  | 'upgrade_failed'
  | 'network_error';

export interface ResolvedProxyTarget {
  readonly kind: 'direct' | 'http' | 'https' | 'socks5';
  readonly host: string;
  readonly port: number;
  readonly rawUrl: string;
}

export interface WsTransportSession {
  sendBinary(payload: Uint8Array): void;
  sendPing(payload?: Uint8Array): void;
  sendPong(payload?: Uint8Array): void;
  close(code?: number, reason?: string): void;
  onBinaryMessage(cb: (data: Uint8Array) => void): void;
  onPing(cb: (data: Uint8Array) => void): void;
  onPong(cb: (data: Uint8Array) => void): void;
  onClose(cb: (code: number, reason: string) => void): void;
  onError(cb: (err: Error) => void): void;
}

export interface DialSuccessResult {
  readonly ok: true;
  readonly transport: WsTransportSession;
}

export interface DialFailureResult {
  readonly ok: false;
  readonly terminal: boolean;
  readonly state: DialConnectionState;
  readonly statusCode?: number;
  readonly message: string;
}

export type DialAttemptResult = DialSuccessResult | DialFailureResult;

export interface RemoteDialControllerOptions {
  readonly resolveSystemProxy?: (targetUrl: string) => Promise<string | null>;
  readonly dialWebSocket?: (params: {
    wsUrl: string;
    deviceToken: string;
    proxy: ResolvedProxyTarget | null;
    proxyUsername: string;
    proxyPassword: string | null;
    signal: AbortSignal;
  }) => Promise<DialAttemptResult>;
  readonly getDialParams: () => {
    portalUrl: string;
    deviceToken: string | null;
    manualPaused: boolean;
    proxyMode: RemoteProxyMode;
    customProxyUrl: string;
    proxyUsername: string;
    proxyPassword: string | null;
  };
  readonly onSessionConnected: (
    transport: WsTransportSession,
    generation: number,
  ) => Promise<{ terminalCloseState?: DialConnectionState; message?: string } | void>;
  readonly onStateChanged?: (snapshot: RemoteDialStatusSnapshot) => void;
  readonly baseBackoffMs?: number;
  readonly maxBackoffMs?: number;
}

export interface RemoteDialStatusSnapshot {
  readonly state: DialConnectionState;
  readonly dialGeneration: number;
  readonly retryAttempt: number;
  readonly nextRetryAtUtc: string | null;
  readonly lastError: string | null;
  readonly resolvedProxyDescription: string;
}

export function parsePacProxyDirective(pacResult: string | null | undefined): ResolvedProxyTarget | null {
  if (!pacResult || typeof pacResult !== 'string') {
    return null;
  }
  const directives = pacResult
    .split(';')
    .map((s) => s.trim())
    .filter((s) => s.length > 0);

  for (const dir of directives) {
    if (/^DIRECT$/i.test(dir)) {
      return null;
    }
    const parts = dir.split(/\s+/);
    if (parts.length !== 2) {
      continue;
    }
    const keyword = parts[0]!.toUpperCase();
    const authority = parts[1]!;
    let scheme: 'http' | 'https' | 'socks5' | null = null;
    let defaultPort = 80;

    if (keyword === 'PROXY' || keyword === 'HTTP') {
      scheme = 'http';
      defaultPort = 8080;
    } else if (keyword === 'HTTPS') {
      scheme = 'https';
      defaultPort = 443;
    } else if (keyword === 'SOCKS5' || keyword === 'SOCKS' || keyword === 'SOCKS4') {
      scheme = 'socks5';
      defaultPort = 1080;
    }

    if (!scheme) {
      continue;
    }

    try {
      const parsed = new URL(`${scheme}://${authority}`);
      const port = parsed.port ? Number.parseInt(parsed.port, 10) : defaultPort;
      if (parsed.hostname && Number.isInteger(port) && port > 0 && port <= 65535) {
        return {
          kind: scheme,
          host: parsed.hostname,
          port,
          rawUrl: `${scheme}://${parsed.hostname}:${port}`,
        };
      }
    } catch {
      // try next directive in PAC chain
    }
  }
  return null;
}

export function parseCustomProxyUrl(rawProxyUrl: string): ResolvedProxyTarget | null {
  const trimmed = rawProxyUrl.trim();
  if (!trimmed) {
    return null;
  }
  const parsed = new URL(trimmed);
  let kind: ResolvedProxyTarget['kind'];
  let defaultPort: number;
  if (parsed.protocol === 'http:') {
    kind = 'http';
    defaultPort = 8080;
  } else if (parsed.protocol === 'https:') {
    kind = 'https';
    defaultPort = 443;
  } else if (parsed.protocol === 'socks5:') {
    kind = 'socks5';
    defaultPort = 1080;
  } else {
    throw new Error(`Unsupported proxy scheme: ${parsed.protocol}`);
  }
  const port = parsed.port ? Number.parseInt(parsed.port, 10) : defaultPort;
  if (!parsed.hostname || !Number.isInteger(port) || port <= 0 || port > 65535) {
    throw new Error('Invalid proxy host or port.');
  }
  return {
    kind,
    host: parsed.hostname,
    port,
    rawUrl: `${kind}://${parsed.hostname}:${port}`,
  };
}

function readHttpHeaderBlock(
  socket: net.Socket,
  signal: AbortSignal,
  timeoutMs = 15_000,
): Promise<{ statusCode: number; statusMessage: string; headers: Map<string, string>; leftover: Buffer }> {
  return new Promise((resolve, reject) => {
    let buffer = Buffer.alloc(0);
    let settled = false;

    const cleanup = () => {
      socket.off('data', onData);
      socket.off('error', onError);
      socket.off('close', onClose);
      signal.removeEventListener('abort', onAbort);
      clearTimeout(timer);
    };

    const fail = (err: Error) => {
      if (!settled) {
        settled = true;
        cleanup();
        reject(err);
      }
    };

    const onAbort = () => fail(new Error('Dial aborted.'));
    const onError = (err: Error) => fail(err);
    const onClose = () => fail(new Error('Socket closed during HTTP handshake.'));

    const timer = setTimeout(() => {
      fail(new Error('HTTP handshake timed out.'));
    }, timeoutMs);

    const onData = (chunk: Buffer) => {
      buffer = Buffer.concat([buffer, chunk]);
      if (buffer.length > 64 * 1024) {
        fail(new Error('HTTP response headers exceeded 64 KiB.'));
        return;
      }
      const markerIdx = buffer.indexOf('\r\n\r\n');
      if (markerIdx < 0) {
        return;
      }
      const headText = buffer.subarray(0, markerIdx).toString('latin1');
      const leftover = buffer.subarray(markerIdx + 4);
      const lines = headText.split('\r\n');
      const statusLine = lines[0] ?? '';
      const statusMatch = /^HTTP\/1\.[01]\s+(\d{3})(?:\s+(.*))?$/i.exec(statusLine);
      if (!statusMatch) {
        fail(new Error(`Invalid HTTP status line: ${statusLine}`));
        return;
      }
      const statusCode = Number.parseInt(statusMatch[1]!, 10);
      const statusMessage = statusMatch[2] ?? '';
      const headers = new Map<string, string>();
      for (let i = 1; i < lines.length; i += 1) {
        const line = lines[i]!;
        const colon = line.indexOf(':');
        if (colon <= 0) {
          continue;
        }
        const key = line.slice(0, colon).trim().toLowerCase();
        const val = line.slice(colon + 1).trim();
        headers.set(key, val);
      }
      if (!settled) {
        settled = true;
        cleanup();
        resolve({ statusCode, statusMessage, headers, leftover });
      }
    };

    signal.addEventListener('abort', onAbort, { once: true });
    socket.on('data', onData);
    socket.on('error', onError);
    socket.on('close', onClose);
  });
}

function encodeWsClientFrame(opcode: number, payload: Uint8Array): Buffer {
  const mask = randomBytes(4);
  const len = payload.byteLength;
  let headerLen = 2 + 4;
  if (len >= 126 && len <= 0xffff) {
    headerLen += 2;
  } else if (len > 0xffff) {
    headerLen += 8;
  }

  const out = Buffer.allocUnsafe(headerLen + len);
  out[0] = 0x80 | (opcode & 0x0f); // FIN=1
  let offset = 2;

  if (len < 126) {
    out[1] = 0x80 | len;
  } else if (len <= 0xffff) {
    out[1] = 0x80 | 126;
    out.writeUInt16BE(len, offset);
    offset += 2;
  } else {
    out[1] = 0x80 | 127;
    out.writeBigUInt64BE(BigInt(len), offset);
    offset += 8;
  }

  mask.copy(out, offset);
  offset += 4;
  for (let i = 0; i < len; i += 1) {
    out[offset + i] = payload[i]! ^ mask[i % 4]!;
  }
  return out;
}

class NativeRfc6455Transport implements WsTransportSession {
  private readonly socket: net.Socket;
  private recvBuffer: Buffer = Buffer.alloc(0);
  private fragmentedOpcode: number | null = null;
  private fragmentedChunks: Buffer[] = [];
  private closed = false;

  private binaryCb: ((data: Uint8Array) => void) | null = null;
  private pingCb: ((data: Uint8Array) => void) | null = null;
  private pongCb: ((data: Uint8Array) => void) | null = null;
  private closeCb: ((code: number, reason: string) => void) | null = null;
  private errorCb: ((err: Error) => void) | null = null;

  constructor(socket: net.Socket, initialLeftover: Buffer) {
    this.socket = socket;
    this.socket.on('data', (chunk: Buffer) => this.handleData(chunk));
    this.socket.on('error', (err: Error) => {
      if (!this.closed) {
        this.errorCb?.(err);
      }
    });
    this.socket.on('close', () => {
      if (!this.closed) {
        this.closed = true;
        this.closeCb?.(1006, 'connection_closed');
      }
    });
    if (initialLeftover.length > 0) {
      queueMicrotask(() => this.handleData(initialLeftover));
    }
  }

  public sendBinary(payload: Uint8Array): void {
    if (this.closed || this.socket.destroyed) {
      return;
    }
    this.socket.write(encodeWsClientFrame(0x02, payload));
  }

  public sendPing(payload: Uint8Array = new Uint8Array(0)): void {
    if (this.closed || this.socket.destroyed) {
      return;
    }
    this.socket.write(encodeWsClientFrame(0x09, payload));
  }

  public sendPong(payload: Uint8Array = new Uint8Array(0)): void {
    if (this.closed || this.socket.destroyed) {
      return;
    }
    this.socket.write(encodeWsClientFrame(0x0a, payload));
  }

  public close(code = 1000, reason = ''): void {
    if (this.closed) {
      return;
    }
    this.closed = true;
    const reasonBuf = reason ? Buffer.from(reason, 'utf8').subarray(0, 120) : Buffer.alloc(0);
    const body = Buffer.allocUnsafe(2 + reasonBuf.length);
    body.writeUInt16BE(code, 0);
    reasonBuf.copy(body, 2);
    try {
      if (!this.socket.destroyed) {
        this.socket.write(encodeWsClientFrame(0x08, body));
      }
    } catch {
      // ignore write error during close
    }
    this.socket.end();
    this.socket.destroy();
    this.closeCb?.(code, reason);
  }

  public onBinaryMessage(cb: (data: Uint8Array) => void): void {
    this.binaryCb = cb;
  }
  public onPing(cb: (data: Uint8Array) => void): void {
    this.pingCb = cb;
  }
  public onPong(cb: (data: Uint8Array) => void): void {
    this.pongCb = cb;
  }
  public onClose(cb: (code: number, reason: string) => void): void {
    this.closeCb = cb;
  }
  public onError(cb: (err: Error) => void): void {
    this.errorCb = cb;
  }

  private handleData(chunk: Buffer): void {
    if (this.closed) {
      return;
    }
    this.recvBuffer =
      this.recvBuffer.length === 0 ? chunk : Buffer.concat([this.recvBuffer, chunk]);

    while (this.recvBuffer.length >= 2) {
      const b0 = this.recvBuffer[0]!;
      const b1 = this.recvBuffer[1]!;
      const fin = (b0 & 0x80) !== 0;
      const opcode = b0 & 0x0f;
      const masked = (b1 & 0x80) !== 0;
      let payloadLen = b1 & 0x7f;
      let offset = 2;

      if (payloadLen === 126) {
        if (this.recvBuffer.length < offset + 2) {
          return;
        }
        payloadLen = this.recvBuffer.readUInt16BE(offset);
        offset += 2;
      } else if (payloadLen === 127) {
        if (this.recvBuffer.length < offset + 8) {
          return;
        }
        const bigLen = this.recvBuffer.readBigUInt64BE(offset);
        if (bigLen > 16n * 1024n * 1024n) {
          this.errorCb?.(new Error('WebSocket frame exceeds 16 MiB bound.'));
          this.close(1009, 'frame_too_large');
          return;
        }
        payloadLen = Number(bigLen);
        offset += 8;
      }

      let maskKey: Buffer | null = null;
      if (masked) {
        if (this.recvBuffer.length < offset + 4) {
          return;
        }
        maskKey = this.recvBuffer.subarray(offset, offset + 4);
        offset += 4;
      }

      if (this.recvBuffer.length < offset + payloadLen) {
        return;
      }

      const rawPayload = Buffer.from(this.recvBuffer.subarray(offset, offset + payloadLen));
      if (maskKey) {
        for (let i = 0; i < rawPayload.length; i += 1) {
          rawPayload[i] = rawPayload[i]! ^ maskKey[i % 4]!;
        }
      }
      this.recvBuffer = this.recvBuffer.subarray(offset + payloadLen);

      if (opcode === 0x08) {
        const closeCode = rawPayload.length >= 2 ? rawPayload.readUInt16BE(0) : 1005;
        const closeReason = rawPayload.length > 2 ? rawPayload.subarray(2).toString('utf8') : '';
        if (!this.closed) {
          this.closed = true;
          this.socket.destroy();
          this.closeCb?.(closeCode, closeReason);
        }
        return;
      }
      if (opcode === 0x09) {
        this.sendPong(rawPayload);
        this.pingCb?.(rawPayload);
        continue;
      }
      if (opcode === 0x0a) {
        this.pongCb?.(rawPayload);
        continue;
      }

      if (opcode === 0x00) {
        if (this.fragmentedOpcode === null) {
          this.close(1002, 'unexpected_continuation');
          return;
        }
        this.fragmentedChunks.push(rawPayload);
        if (fin) {
          const full = Buffer.concat(this.fragmentedChunks);
          const origOp = this.fragmentedOpcode;
          this.fragmentedOpcode = null;
          this.fragmentedChunks = [];
          if (origOp === 0x02) {
            this.binaryCb?.(full);
          }
        }
        continue;
      }

      if (!fin) {
        this.fragmentedOpcode = opcode;
        this.fragmentedChunks = [rawPayload];
        continue;
      }

      if (opcode === 0x02) {
        this.binaryCb?.(rawPayload);
      }
    }
  }
}

export async function defaultDialWebSocketWithProxy(params: {
  wsUrl: string;
  deviceToken: string;
  proxy: ResolvedProxyTarget | null;
  proxyUsername: string;
  proxyPassword: string | null;
  signal: AbortSignal;
}): Promise<DialAttemptResult> {
  const target = new URL(params.wsUrl);
  const isTlsTarget = target.protocol === 'wss:';
  const targetPort = target.port
    ? Number.parseInt(target.port, 10)
    : isTlsTarget
      ? 443
      : 80;
  const hostHeader =
    (isTlsTarget && targetPort === 443) || (!isTlsTarget && targetPort === 80)
      ? target.hostname
      : `${target.hostname}:${targetPort}`;

  let socket: net.Socket | null = null;
  try {
    if (params.proxy) {
      if (params.proxy.kind === 'socks5') {
        return {
          ok: false,
          terminal: true,
          state: 'upgrade_failed',
          message: 'SOCKS5 proxy tunneling requires direct HTTP/HTTPS CONNECT proxy.',
        };
      }

      socket = await new Promise<net.Socket>((resolve, reject) => {
        const s =
          params.proxy!.kind === 'https'
            ? tls.connect({ host: params.proxy!.host, port: params.proxy!.port, servername: params.proxy!.host })
            : net.connect({ host: params.proxy!.host, port: params.proxy!.port });
        const onAbort = () => {
          s.destroy();
          reject(new Error('Proxy dial aborted.'));
        };
        params.signal.addEventListener('abort', onAbort, { once: true });
        const eventName = params.proxy!.kind === 'https' ? 'secureConnect' : 'connect';
        s.once(eventName, () => {
          params.signal.removeEventListener('abort', onAbort);
          resolve(s);
        });
        s.once('error', (err) => {
          params.signal.removeEventListener('abort', onAbort);
          reject(err);
        });
      });

      const connectLines = [
        `CONNECT ${target.hostname}:${targetPort} HTTP/1.1`,
        `Host: ${target.hostname}:${targetPort}`,
      ];
      if (params.proxyUsername || params.proxyPassword) {
        const creds = Buffer.from(
          `${params.proxyUsername}:${params.proxyPassword ?? ''}`,
          'utf8',
        ).toString('base64');
        connectLines.push(`Proxy-Authorization: Basic ${creds}`);
      }
      connectLines.push('Proxy-Connection: Keep-Alive', '', '');
      socket.write(connectLines.join('\r\n'));

      const connectResp = await readHttpHeaderBlock(socket, params.signal);
      if (connectResp.statusCode === 407) {
        socket.destroy();
        return {
          ok: false,
          terminal: true,
          state: 'auth_required_407',
          statusCode: 407,
          message: 'Enterprise HTTP proxy requires authentication (407 Proxy Authentication Required).',
        };
      }
      if (connectResp.statusCode === 403) {
        socket.destroy();
        return {
          ok: false,
          terminal: true,
          state: 'forbidden_403',
          statusCode: 403,
          message: 'Proxy blocked tunnel destination (403 Forbidden).',
        };
      }
      if (connectResp.statusCode !== 200) {
        socket.destroy();
        return {
          ok: false,
          terminal: false,
          state: 'upgrade_failed',
          statusCode: connectResp.statusCode,
          message: `Proxy CONNECT failed with HTTP ${connectResp.statusCode}.`,
        };
      }

      if (isTlsTarget) {
        const baseSocket = socket;
        socket = await new Promise<tls.TLSSocket>((resolve, reject) => {
          const ts = tls.connect({
            socket: baseSocket,
            servername: target.hostname,
          });
          ts.once('secureConnect', () => resolve(ts));
          ts.once('error', reject);
        });
      }
    } else {
      socket = await new Promise<net.Socket>((resolve, reject) => {
        const s = isTlsTarget
          ? tls.connect({ host: target.hostname, port: targetPort, servername: target.hostname })
          : net.connect({ host: target.hostname, port: targetPort });
        const onAbort = () => {
          s.destroy();
          reject(new Error('Dial aborted.'));
        };
        params.signal.addEventListener('abort', onAbort, { once: true });
        const eventName = isTlsTarget ? 'secureConnect' : 'connect';
        s.once(eventName, () => {
          params.signal.removeEventListener('abort', onAbort);
          resolve(s);
        });
        s.once('error', (err) => {
          params.signal.removeEventListener('abort', onAbort);
          reject(err);
        });
      });
    }

    const wsKey = randomBytes(16).toString('base64');
    const pathAndQuery = `${target.pathname || '/'}${target.search}`;
    const upgradeLines = [
      `GET ${pathAndQuery} HTTP/1.1`,
      `Host: ${hostHeader}`,
      'Upgrade: websocket',
      'Connection: Upgrade',
      `Sec-WebSocket-Key: ${wsKey}`,
      'Sec-WebSocket-Version: 13',
      `Authorization: Bearer ${params.deviceToken}`,
      '',
      '',
    ];
    socket.write(upgradeLines.join('\r\n'));

    const upgradeResp = await readHttpHeaderBlock(socket, params.signal);
    if (upgradeResp.statusCode === 407) {
      socket.destroy();
      return {
        ok: false,
        terminal: true,
        state: 'auth_required_407',
        statusCode: 407,
        message: 'Proxy authentication required (407).',
      };
    }
    if (upgradeResp.statusCode === 401) {
      socket.destroy();
      return {
        ok: false,
        terminal: true,
        state: 'unauthorized_401',
        statusCode: 401,
        message: 'Portal rejected desktop deviceToken (401 Unauthorized).',
      };
    }
    if (upgradeResp.statusCode === 403) {
      socket.destroy();
      return {
        ok: false,
        terminal: true,
        state: 'forbidden_403',
        statusCode: 403,
        message: 'Portal denied remote tunnel access (403 Forbidden).',
      };
    }
    if (upgradeResp.statusCode !== 101) {
      socket.destroy();
      return {
        ok: false,
        terminal: true,
        state: 'upgrade_failed',
        statusCode: upgradeResp.statusCode,
        message: `WebSocket upgrade rejected with HTTP ${upgradeResp.statusCode}.`,
      };
    }

    const expectedAccept = createHash('sha1')
      .update(`${wsKey}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`, 'ascii')
      .digest('base64');
    if (upgradeResp.headers.get('sec-websocket-accept') !== expectedAccept) {
      socket.destroy();
      return {
        ok: false,
        terminal: true,
        state: 'upgrade_failed',
        message: 'Invalid Sec-WebSocket-Accept digest from server.',
      };
    }

    return {
      ok: true,
      transport: new NativeRfc6455Transport(socket, upgradeResp.leftover),
    };
  } catch (err) {
    socket?.destroy();
    return {
      ok: false,
      terminal: false,
      state: 'network_error',
      message: err instanceof Error ? err.message : String(err),
    };
  }
}

export class RemoteDialController {
  private readonly options: RemoteDialControllerOptions;
  private readonly baseBackoffMs: number;
  private readonly maxBackoffMs: number;

  private dialGeneration = 0;
  private state: DialConnectionState = 'stopped';
  private retryAttempt = 0;
  private nextRetryAtUtc: string | null = null;
  private lastError: string | null = null;
  private resolvedProxyDescription = 'direct';

  private backoffTimer: NodeJS.Timeout | null = null;
  private activeAbort: AbortController | null = null;
  private activeTransport: WsTransportSession | null = null;
  private suspended = false;
  private stopped = true;

  constructor(options: RemoteDialControllerOptions) {
    this.options = options;
    this.baseBackoffMs = options.baseBackoffMs ?? 1500;
    this.maxBackoffMs = options.maxBackoffMs ?? 60_000;
  }

  public getStatus(): RemoteDialStatusSnapshot {
    return {
      state: this.state,
      dialGeneration: this.dialGeneration,
      retryAttempt: this.retryAttempt,
      nextRetryAtUtc: this.nextRetryAtUtc,
      lastError: this.lastError,
      resolvedProxyDescription: this.resolvedProxyDescription,
    };
  }

  public startOrReconnect(): void {
    this.stopped = false;
    const params = this.options.getDialParams();
    if (params.manualPaused) {
      this.transitionToPaused('Paused by user.');
      return;
    }
    if (!params.portalUrl || !params.deviceToken) {
      this.stop('Remote desktop device is not bound.');
      return;
    }
    if (this.suspended) {
      return;
    }
    this.retryAttempt = 0;
    void this.beginDialAttempt();
  }

  public stop(reason = 'Stopped.'): void {
    this.stopped = true;
    this.clearBackoffTimer();
    this.dialGeneration += 1;
    this.abortActiveAttempt();
    this.state = 'stopped';
    this.nextRetryAtUtc = null;
    this.lastError = reason;
    this.emitStatus();
  }

  public setManualPaused(paused: boolean): void {
    if (paused) {
      this.transitionToPaused('Paused by user.');
      return;
    }
    this.startOrReconnect();
  }

  public handlePowerSuspend(): void {
    this.suspended = true;
    this.clearBackoffTimer();
    this.dialGeneration += 1;
    this.abortActiveAttempt();
    if (this.state !== 'stopped' && this.state !== 'paused') {
      this.state = 'paused';
      this.lastError = 'System suspended.';
      this.emitStatus();
    }
  }

  public handlePowerResume(): void {
    this.suspended = false;
    if (this.stopped) {
      return;
    }
    const params = this.options.getDialParams();
    if (params.manualPaused || !params.portalUrl || !params.deviceToken) {
      return;
    }
    this.retryAttempt = 0;
    void this.beginDialAttempt();
  }

  private transitionToPaused(reason: string): void {
    this.clearBackoffTimer();
    this.dialGeneration += 1;
    this.abortActiveAttempt();
    this.state = 'paused';
    this.nextRetryAtUtc = null;
    this.lastError = reason;
    this.emitStatus();
  }

  private clearBackoffTimer(): void {
    if (this.backoffTimer) {
      clearTimeout(this.backoffTimer);
      this.backoffTimer = null;
    }
  }

  private abortActiveAttempt(): void {
    if (this.activeAbort) {
      this.activeAbort.abort();
      this.activeAbort = null;
    }
    if (this.activeTransport) {
      try {
        this.activeTransport.close(1000, 'controller_reset');
      } catch {
        // ignore close errors
      }
      this.activeTransport = null;
    }
  }

  private async resolveProxyTarget(params: ReturnType<RemoteDialControllerOptions['getDialParams']>): Promise<ResolvedProxyTarget | null> {
    if (params.proxyMode === 'direct') {
      return null;
    }
    if (params.proxyMode === 'custom') {
      return parseCustomProxyUrl(params.customProxyUrl);
    }
    if (this.options.resolveSystemProxy) {
      const pacStr = await this.options.resolveSystemProxy(params.portalUrl);
      return parsePacProxyDirective(pacStr);
    }
    return null;
  }

  private async beginDialAttempt(): Promise<void> {
    if (this.stopped || this.suspended) {
      return;
    }
    const params = this.options.getDialParams();
    if (params.manualPaused || !params.portalUrl || !params.deviceToken) {
      return;
    }

    this.clearBackoffTimer();
    this.abortActiveAttempt();

    const myGen = ++this.dialGeneration;
    const ac = new AbortController();
    this.activeAbort = ac;
    this.state = 'dialing';
    this.nextRetryAtUtc = null;
    this.lastError = null;
    this.emitStatus();

    let proxy: ResolvedProxyTarget | null = null;
    try {
      proxy = await this.resolveProxyTarget(params);
      this.resolvedProxyDescription = proxy ? proxy.rawUrl : 'direct';
    } catch (err) {
      if (myGen !== this.dialGeneration) {
        return;
      }
      this.scheduleBackoff(err instanceof Error ? err.message : 'Proxy resolution failed.');
      return;
    }

    if (myGen !== this.dialGeneration || this.stopped || this.suspended || ac.signal.aborted) {
      return;
    }

    const portalUrlObj = new URL(params.portalUrl);
    const wsScheme = portalUrlObj.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${wsScheme}//${portalUrlObj.host}/ws/agent`;

    const dialFn = this.options.dialWebSocket ?? defaultDialWebSocketWithProxy;
    const res = await dialFn({
      wsUrl,
      deviceToken: params.deviceToken,
      proxy,
      proxyUsername: params.proxyUsername,
      proxyPassword: params.proxyPassword,
      signal: ac.signal,
    });

    if (myGen !== this.dialGeneration || this.stopped || this.suspended || ac.signal.aborted) {
      if (res.ok) {
        res.transport.close(1000, 'stale_dial_generation');
      }
      return;
    }

    if (!res.ok) {
      if (res.terminal) {
        this.state = res.state;
        this.lastError = res.message;
        this.nextRetryAtUtc = null;
        this.emitStatus();
        return;
      }
      this.scheduleBackoff(res.message);
      return;
    }

    this.activeTransport = res.transport;
    this.state = 'connected';
    this.retryAttempt = 0;
    this.lastError = null;
    this.nextRetryAtUtc = null;
    this.emitStatus();

    let disconnectOverride: { terminalCloseState?: DialConnectionState; message?: string } | void;
    try {
      disconnectOverride = await this.options.onSessionConnected(res.transport, myGen);
    } catch (err) {
      disconnectOverride = {
        message: err instanceof Error ? err.message : 'Tunnel session failed.',
      };
    }

    if (myGen !== this.dialGeneration) {
      return;
    }

    this.activeTransport = null;
    if (this.stopped || this.suspended || this.options.getDialParams().manualPaused) {
      return;
    }

    if (disconnectOverride?.terminalCloseState) {
      this.state = disconnectOverride.terminalCloseState;
      this.lastError = disconnectOverride.message ?? 'Remote binding terminated.';
      this.nextRetryAtUtc = null;
      this.emitStatus();
      return;
    }

    this.scheduleBackoff(disconnectOverride?.message ?? 'Tunnel disconnected.');
  }

  private scheduleBackoff(reason: string): void {
    if (this.stopped || this.suspended) {
      return;
    }
    this.retryAttempt += 1;
    const exp = Math.min(
      this.maxBackoffMs,
      this.baseBackoffMs * 2 ** Math.min(6, this.retryAttempt - 1),
    );
    const jitter = Math.floor(Math.random() * Math.min(1000, exp * 0.25));
    const delayMs = exp + jitter;

    this.state = 'backoff';
    this.lastError = reason;
    this.nextRetryAtUtc = new Date(Date.now() + delayMs).toISOString();
    this.emitStatus();

    const genAtSchedule = this.dialGeneration;
    this.backoffTimer = setTimeout(() => {
      this.backoffTimer = null;
      if (genAtSchedule === this.dialGeneration && !this.stopped && !this.suspended) {
        void this.beginDialAttempt();
      }
    }, delayMs);
    this.backoffTimer.unref();
  }

  private emitStatus(): void {
    this.options.onStateChanged?.(this.getStatus());
  }
}
