import { isAbsolute, posix, relative, resolve } from 'node:path';

export const APP_SCHEME = 'app';
export const APP_HOST = 'codexflow';
export const APP_ORIGIN = `${APP_SCHEME}://${APP_HOST}`;

export function buildContentSecurityPolicy(gatewayProxyOrigin: string | null, isDev: boolean): string {
  const connectOrigins = ["'self'"];
  if (gatewayProxyOrigin) {
    try {
      connectOrigins.push(new URL(gatewayProxyOrigin).origin);
    } catch {
      // ignore malformed proxy URL
    }
  }
  if (isDev && process.env.ELECTRON_RENDERER_URL) {
    try {
      const devOrigin = new URL(process.env.ELECTRON_RENDERER_URL).origin;
      connectOrigins.push(devOrigin, devOrigin.replace(/^http/, 'ws'));
    } catch {
      // ignore malformed dev URL
    }
  }

  const scriptSrc = isDev ? `'self' 'unsafe-inline' ${APP_ORIGIN}` : `'self' ${APP_ORIGIN}`;
  return [
    `default-src 'self' ${APP_ORIGIN}`,
    `script-src ${scriptSrc}`,
    `style-src 'self' 'unsafe-inline' ${APP_ORIGIN}`,
    `img-src 'self' data: blob: ${APP_ORIGIN}`,
    `font-src 'self' data: ${APP_ORIGIN}`,
    `connect-src ${connectOrigins.join(' ')}`,
    `object-src 'none'`,
    `base-uri 'self'`,
    `frame-ancestors 'none'`,
  ].join('; ');
}

export function isTrustedRendererOrigin(rawUrl: string, allowedOrigins: ReadonlySet<string>): boolean {
  try {
    const parsed = new URL(rawUrl);
    // Node's WHATWG URL returns `'null'` for non-special schemes like `app:`,
    // whereas Electron treats `app://codexflow` as a standard origin.
    const effectiveOrigin = parsed.origin !== 'null' ? parsed.origin : `${parsed.protocol}//${parsed.host}`;
    return allowedOrigins.has(effectiveOrigin);
  } catch {
    return false;
  }
}

export function validateIpcCaller(params: {
  hasWindow: boolean;
  windowDestroyed: boolean;
  senderMatchesWindow: boolean;
  hasSenderFrame: boolean;
  senderFrameMatchesMainFrame: boolean;
  frameUrl: string;
  allowedOrigins: ReadonlySet<string>;
}): void {
  if (!params.hasWindow || params.windowDestroyed) {
    throw new Error('Unauthorized IPC caller: no active main window');
  }
  if (!params.senderMatchesWindow) {
    throw new Error('Unauthorized IPC caller: untrusted WebContents');
  }
  if (!params.hasSenderFrame || !params.senderFrameMatchesMainFrame) {
    throw new Error('Unauthorized IPC caller: non-main frame caller rejected');
  }
  if (!isTrustedRendererOrigin(params.frameUrl, params.allowedOrigins)) {
    throw new Error('Unauthorized IPC caller: untrusted frame origin');
  }
}

export function isPathStrictlyInsideRoot(realRootDir: string, candidateRealPath: string): boolean {
  const rel = relative(realRootDir, candidateRealPath);
  if (!rel) {
    return true;
  }
  return rel !== '..' && !rel.startsWith(`..${posix.sep}`) && !rel.startsWith('..\\') && !isAbsolute(rel);
}

/**
 * Resolves a request pathname against `rootDir`, blocking drive-letter paths, UNC prefixes,
 * backslashes, dot-dot segments, null bytes, and symlink escapes.
 */
export function resolveSafeAppPath(
  realRootDir: string,
  rawPathname: string,
): { ok: true; relativeSegments: string; targetPath: string } | { ok: false; status: 400 | 403 } {
  let decoded: string;
  try {
    decoded = decodeURIComponent(rawPathname || '/');
  } catch {
    return { ok: false, status: 400 };
  }

  if (
    decoded.includes('\0') ||
    decoded.includes('\\') ||
    decoded.includes(':') ||
    decoded.startsWith('//') ||
    decoded.split('/').some((seg) => seg === '..')
  ) {
    return { ok: false, status: 403 };
  }

  const normalizedPosix = posix.normalize(decoded === '/' ? '/index.html' : decoded);
  if (normalizedPosix.startsWith('..') || normalizedPosix.includes('/../') || normalizedPosix.endsWith('/..')) {
    return { ok: false, status: 403 };
  }

  const stripped = normalizedPosix.replace(/^\/+/, '');
  if (!stripped || /^[a-zA-Z]:/.test(stripped) || isAbsolute(stripped)) {
    return { ok: false, status: 403 };
  }

  const candidate = resolve(realRootDir, stripped);
  if (!isPathStrictlyInsideRoot(realRootDir, candidate)) {
    return { ok: false, status: 403 };
  }

  return { ok: true, relativeSegments: stripped, targetPath: candidate };
}
