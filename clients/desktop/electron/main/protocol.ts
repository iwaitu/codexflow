import { protocol, session } from 'electron';
import { realpathSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { extname, resolve } from 'node:path';
import {
  APP_HOST,
  APP_ORIGIN,
  APP_SCHEME,
  buildContentSecurityPolicy,
  isPathStrictlyInsideRoot,
  resolveSafeAppPath,
} from './securityUtils';

export {
  APP_HOST,
  APP_ORIGIN,
  APP_SCHEME,
  buildContentSecurityPolicy,
  isPathStrictlyInsideRoot,
  resolveSafeAppPath,
};

const MIME_TYPES: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
};

export function registerAppSchemePrivileges(): void {
  protocol.registerSchemesAsPrivileged([
    {
      scheme: APP_SCHEME,
      privileges: {
        standard: true,
        secure: true,
        supportFetchAPI: true,
        corsEnabled: true,
        stream: true,
      },
    },
  ]);
}

export function registerAppProtocolHandler(rendererDistDir: string): void {
  const realRootDir = realpathSync(resolve(rendererDistDir));

  protocol.handle(APP_SCHEME, async (request) => {
    const url = new URL(request.url);
    if (url.hostname !== APP_HOST) {
      return new Response('Forbidden', { status: 403 });
    }

    const resolved = resolveSafeAppPath(realRootDir, url.pathname);
    if (!resolved.ok) {
      return new Response(resolved.status === 400 ? 'Bad Request' : 'Forbidden', {
        status: resolved.status,
      });
    }

    try {
      const realTarget = realpathSync(resolved.targetPath);
      if (!isPathStrictlyInsideRoot(realRootDir, realTarget)) {
        return new Response('Forbidden', { status: 403 });
      }
      const body = await readFile(realTarget);
      const mime = MIME_TYPES[extname(realTarget).toLowerCase()] ?? 'application/octet-stream';
      return new Response(body, {
        status: 200,
        headers: {
          'Content-Type': mime,
          'X-Content-Type-Options': 'nosniff',
        },
      });
    } catch {
      if (extname(resolved.relativeSegments)) {
        return new Response('Not Found', { status: 404 });
      }
      const indexBytes = await readFile(resolve(realRootDir, 'index.html'));
      return new Response(indexBytes, {
        status: 200,
        headers: {
          'Content-Type': 'text/html; charset=utf-8',
          'X-Content-Type-Options': 'nosniff',
        },
      });
    }
  });
}

export function attachCspHeaderHook(getGatewayProxyOrigin: () => string | null, isDev: boolean): void {
  session.defaultSession.webRequest.onHeadersReceived((details, callback) => {
    const isAppUrl =
      details.url.startsWith(`${APP_ORIGIN}/`) ||
      (isDev && process.env.ELECTRON_RENDERER_URL && details.url.startsWith(process.env.ELECTRON_RENDERER_URL));
    if (!isAppUrl) {
      callback({ responseHeaders: details.responseHeaders });
      return;
    }

    const csp = buildContentSecurityPolicy(getGatewayProxyOrigin(), isDev);
    callback({
      responseHeaders: {
        ...details.responseHeaders,
        'Content-Security-Policy': [csp],
      },
    });
  });
}
