import type { ControlPlaneCallResult } from '../../src/shared/ipc';

export interface ControlPlaneCredentials {
  baseUrl: string | null;
  accessToken: string | null;
  controlToken: string | null;
  controlPlaneAvailable: boolean;
}

/**
 * Proxies control-plane HTTP calls strictly inside the Electron main process so
 * `controlToken` never enters the renderer or preload contexts.
 */
export async function callControlPlane<T>(
  creds: ControlPlaneCredentials,
  method: 'GET' | 'POST' | 'PUT' | 'DELETE',
  path: string,
  body?: unknown,
): Promise<ControlPlaneCallResult<T>> {
  if (!creds.baseUrl) {
    return {
      ok: false,
      status: 503,
      errorCode: 'backend_not_ready',
      errorMessage: '后端尚未就绪。',
    };
  }

  const token = creds.controlToken ?? creds.accessToken;
  if (!token) {
    return {
      ok: false,
      status: 401,
      errorCode: 'missing_credentials',
      errorMessage: '缺少访问凭据。',
    };
  }

  if (!creds.controlPlaneAvailable && !creds.controlToken) {
    return {
      ok: false,
      status: 403,
      errorCode: 'control_plane_unavailable',
      errorMessage: '当前处于独立运行本地模式且未启用控制面；请改用托管模式后再执行控制面操作。',
    };
  }

  try {
    const response = await fetch(`${creds.baseUrl}${path}`, {
      method,
      headers: {
        Authorization: `Bearer ${token}`,
        ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
      },
      body: body !== undefined ? JSON.stringify(body) : undefined,
      signal: AbortSignal.timeout(15_000),
    });

    const rawText = await response.text();
    let parsed: Record<string, unknown> | undefined;
    if (rawText) {
      try {
        parsed = JSON.parse(rawText) as Record<string, unknown>;
      } catch {
        parsed = undefined;
      }
    }

    if (response.ok) {
      return {
        ok: true,
        status: response.status,
        data: (parsed as T) ?? (undefined as T),
      };
    }

    const errorCode =
      (typeof parsed?.code === 'string' && parsed.code) ||
      (typeof parsed?.reason === 'string' && parsed.reason) ||
      `http_${response.status}`;
    const errorMessage =
      (typeof parsed?.message === 'string' && parsed.message) ||
      rawText ||
      response.statusText ||
      `请求失败 (${response.status})`;
    const expectedRequestDigest =
      typeof parsed?.expectedRequestDigest === 'string' ? parsed.expectedRequestDigest : undefined;

    return {
      ok: false,
      status: response.status,
      errorCode,
      errorMessage,
      expectedRequestDigest,
    };
  } catch (error) {
    return {
      ok: false,
      status: 503,
      errorCode: 'network_error',
      errorMessage: error instanceof Error ? error.message : String(error),
    };
  }
}
