export const DEFAULT_PORTAL_URL =
  import.meta.env.VITE_PORTAL_URL || 'https://portal.codexflow.example';

export function normalizePortalOrigin(rawUrl: string): string {
  const trimmed = rawUrl.trim();
  if (!trimmed) {
    throw new Error('请输入有效的 Portal 服务地址。');
  }

  let parsed: URL;
  try {
    parsed = new URL(trimmed);
  } catch {
    throw new Error('Portal 服务地址格式无效。');
  }

  if (parsed.username || parsed.password) {
    throw new Error('Portal 地址严禁内嵌用户名或密码。');
  }

  if (parsed.protocol === 'http:') {
    const allowHttp = import.meta.env.DEV || import.meta.env.MODE === 'test';
    const host = parsed.hostname.toLowerCase();
    const isLoopback = host === '127.0.0.1' || host === 'localhost' || host === '::1' || host === '[::1]';
    if (!allowHttp || !isLoopback) {
      throw new Error('生产环境仅支持 HTTPS 协议连接 Portal。');
    }
  } else if (parsed.protocol !== 'https:') {
    throw new Error('Portal 地址必须使用 https:// 协议。');
  }

  return parsed.origin;
}
