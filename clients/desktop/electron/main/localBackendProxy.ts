import {
  createServer,
  request as httpRequest,
  type ClientRequest,
  type IncomingMessage,
  type Server,
  type ServerResponse,
} from 'node:http';
import { request as httpsRequest } from 'node:https';
import { timingSafeEqual } from 'node:crypto';
import { APP_ORIGIN } from './protocol';

export interface LocalProxyUpstreamState {
  targetBaseUrl: string | null;
  expectedAccessToken: string | null;
  controlToken: string | null;
}

interface ActiveExchange {
  req: IncomingMessage;
  res: ServerResponse;
  upstreamReq?: ClientRequest;
}

function tokensEqual(a: string, b: string): boolean {
  const bufA = Buffer.from(a, 'utf-8');
  const bufB = Buffer.from(b, 'utf-8');
  return bufA.length === bufB.length && timingSafeEqual(bufA, bufB);
}

/**
 * Single pinned loopback gateway hosted inside the Electron main process.
 * Renderer CSP strictly connects ONLY to this origin; backend restarts or standalone
 * endpoint switches update the upstream target transparently without reloading the page
 * or exposing arbitrary `127.0.0.1:*` loopback ports to renderer code.
 */
export class LocalBackendProxy {
  private server: Server | null = null;
  private proxyOrigin: string | null = null;
  private closing = false;
  private readonly activeExchanges = new Set<ActiveExchange>();

  constructor(
    private readonly getUpstream: () => LocalProxyUpstreamState,
    private readonly allowedOrigins: ReadonlySet<string>,
  ) {}

  public async start(): Promise<string> {
    if (this.proxyOrigin) {
      return this.proxyOrigin;
    }

    this.closing = false;
    this.server = createServer((req, res) => {
      this.handleRequest(req, res);
    });

    await new Promise<void>((resolveListen, rejectListen) => {
      this.server!.once('error', rejectListen);
      this.server!.listen(0, '127.0.0.1', () => {
        const addr = this.server!.address();
        if (addr && typeof addr === 'object') {
          this.proxyOrigin = `http://127.0.0.1:${addr.port}`;
          resolveListen();
        } else {
          rejectListen(new Error('Failed to bind local backend proxy port'));
        }
      });
    });

    return this.proxyOrigin!;
  }

  public getOrigin(): string | null {
    return this.proxyOrigin;
  }

  public async close(): Promise<void> {
    this.closing = true;
    const srv = this.server;
    this.server = null;
    this.proxyOrigin = null;

    // Actively tear down every open SSE / HTTP exchange & upstream socket so app shutdown
    // is never blocked by long-lived standalone SSE streams (F2).
    for (const ex of this.activeExchanges) {
      try {
        ex.upstreamReq?.destroy();
      } catch {
        // ignore
      }
      try {
        ex.req.destroy();
      } catch {
        // ignore
      }
      try {
        ex.res.destroy();
      } catch {
        // ignore
      }
    }
    this.activeExchanges.clear();

    if (!srv) {
      return;
    }

    try {
      srv.closeIdleConnections?.();
      srv.closeAllConnections?.();
    } catch {
      // ignore if unsupported
    }

    await Promise.race([
      new Promise<void>((resolveClose) => {
        srv.close(() => resolveClose());
      }),
      new Promise<void>((resolveTimeout) => {
        setTimeout(resolveTimeout, 1000).unref();
      }),
    ]);
  }

  private setCorsHeaders(req: IncomingMessage, res: ServerResponse): string | null {
    const origin = req.headers.origin;
    if (!origin) {
      return APP_ORIGIN;
    }
    if (!this.allowedOrigins.has(origin)) {
      return null;
    }
    res.setHeader('Access-Control-Allow-Origin', origin);
    res.setHeader('Vary', 'Origin');
    res.setHeader('Access-Control-Allow-Headers', 'Authorization, Content-Type, Last-Event-ID');
    res.setHeader('Access-Control-Allow-Methods', 'GET, POST, PUT, PATCH, DELETE, OPTIONS');
    return origin;
  }

  private handleRequest(req: IncomingMessage, res: ServerResponse): void {
    if (this.closing) {
      res.writeHead(503, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ code: 'proxy_closing', message: 'Proxy is shutting down' }));
      return;
    }

    const allowedOrigin = this.setCorsHeaders(req, res);
    if (!allowedOrigin) {
      res.writeHead(403, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ code: 'forbidden_origin', message: 'Untrusted origin' }));
      return;
    }

    if (req.method === 'OPTIONS') {
      res.writeHead(204);
      res.end();
      return;
    }

    const reqUrl = req.url || '/';
    if (!reqUrl.startsWith('/api/')) {
      res.writeHead(404, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ code: 'not_found', message: 'Only /api/ routes are proxied' }));
      return;
    }

    const upstream = this.getUpstream();
    if (!upstream.targetBaseUrl) {
      res.writeHead(503, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ code: 'backend_unavailable', message: 'Backend is starting or disconnected' }));
      return;
    }

    const authHeader = req.headers.authorization;
    const bearer =
      typeof authHeader === 'string' && authHeader.startsWith('Bearer ')
        ? authHeader.slice(7).trim()
        : '';

    if (upstream.controlToken && bearer && tokensEqual(bearer, upstream.controlToken)) {
      res.writeHead(403, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ code: 'control_token_forbidden', message: 'Renderer must not use control token directly' }));
      return;
    }

    if (upstream.expectedAccessToken && (!bearer || !tokensEqual(bearer, upstream.expectedAccessToken))) {
      res.writeHead(401, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ code: 'unauthorized', message: 'Invalid access token' }));
      return;
    }

    let target: URL;
    try {
      target = new URL(reqUrl, upstream.targetBaseUrl.endsWith('/') ? upstream.targetBaseUrl : `${upstream.targetBaseUrl}/`);
    } catch {
      res.writeHead(502, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ code: 'bad_upstream', message: 'Invalid backend base URL' }));
      return;
    }

    const exchange: ActiveExchange = { req, res };
    this.activeExchanges.add(exchange);
    const cleanupExchange = () => {
      this.activeExchanges.delete(exchange);
    };
    res.once('close', cleanupExchange);
    res.once('finish', cleanupExchange);

    const headers: Record<string, string | string[]> = {};
    for (const key of ['authorization', 'content-type', 'content-length', 'accept', 'last-event-id', 'cache-control']) {
      const val = req.headers[key];
      if (val !== undefined) {
        headers[key] = val;
      }
    }

    const requestFn = target.protocol === 'https:' ? httpsRequest : httpRequest;
    const upstreamReq = requestFn(
      target,
      {
        method: req.method,
        headers,
      },
      (upstreamRes) => {
        const outHeaders: Record<string, string | string[]> = {};
        for (const [k, v] of Object.entries(upstreamRes.headers)) {
          if (v !== undefined && !k.toLowerCase().startsWith('access-control-')) {
            outHeaders[k] = v;
          }
        }
        res.writeHead(upstreamRes.statusCode ?? 502, outHeaders);
        res.flushHeaders?.();
        upstreamRes.pipe(res);

        // Terminate downstream response immediately if upstream drops unexpectedly (F2).
        const terminateDownstreamOnAbort = () => {
          if (!res.writableFinished && !res.destroyed) {
            res.destroy();
          }
        };
        upstreamRes.on('error', terminateDownstreamOnAbort);
        upstreamRes.on('aborted', terminateDownstreamOnAbort);
        upstreamRes.on('close', () => {
          if (!upstreamRes.complete) {
            terminateDownstreamOnAbort();
          }
        });
      },
    );
    exchange.upstreamReq = upstreamReq;

    upstreamReq.on('error', (err) => {
      if (!res.headersSent) {
        res.writeHead(502, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ code: 'upstream_error', message: err.message }));
      } else if (!res.destroyed) {
        res.destroy();
      }
    });

    // In Node 22, `req` emits 'close' as soon as the request stream finishes reading.
    // Abort upstream only when the client closes the response stream before it finishes.
    res.on('close', () => {
      if (!res.writableFinished && !upstreamReq.destroyed) {
        upstreamReq.destroy();
      }
    });

    req.pipe(upstreamReq);
  }
}
