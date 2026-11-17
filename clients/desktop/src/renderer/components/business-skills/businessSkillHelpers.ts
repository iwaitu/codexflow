import type {
  BusinessAuthKind,
  CapabilityRiskLevel,
  ControlPlaneCallResult,
  SkillInputPropertyDto,
  SkillInputSchemaDto,
  SkillRunStatus,
  SkillRunStepState,
  StartSkillRunRequest,
} from '@shared/ipc';
import { ApiError } from '../../lib/apiClient';

/**
 * 判断异常是否表示服务端 BusinessSkills:Enabled 开关未开启。
 */
export function isBusinessSkillsDisabledError(error: unknown): boolean {
  if (error instanceof ApiError) {
    return (
      error.status === 503 &&
      (error.message.includes('BusinessSkills:Enabled') ||
        error.code.toLowerCase().includes('feature') ||
        error.code === 'ConnectionUnavailable' ||
        error.message.includes('未开启'))
    );
  }
  if (error instanceof Error) {
    return error.message.includes('BusinessSkills:Enabled');
  }
  return false;
}

/**
 * 统一格式化渲染层 API 与控制面 IPC 调用错误。
 */
export function formatBusinessSkillError(error: unknown, fallback = '操作失败'): string {
  if (error instanceof ApiError) {
    return error.message || `${fallback} (${error.status})`;
  }
  if (error instanceof Error) {
    return error.message || fallback;
  }
  return fallback;
}

/**
 * 从控制面 IPC 返回包提取业务错误信息（包含字段级校验提示）。
 */
export function extractControlPlaneError<T>(
  result: ControlPlaneCallResult<T>,
  fallback = '控制面操作失败',
): string {
  if (result.errorMessage) {
    return result.errorMessage;
  }
  if (result.data && typeof result.data === 'object') {
    const record = result.data as Record<string, unknown>;
    const msg = typeof record.message === 'string' ? record.message : null;
    const fieldErrors = Array.isArray(record.fieldErrors) ? record.fieldErrors : [];
    const details = fieldErrors
      .map((item) => {
        if (!item || typeof item !== 'object') return null;
        const fe = item as Record<string, unknown>;
        return typeof fe.field === 'string' && typeof fe.message === 'string'
          ? `${fe.field}: ${fe.message}`
          : null;
      })
      .filter((item): item is string => Boolean(item));
    if (msg && details.length > 0) {
      return `${msg}（${details.join('；')}）`;
    }
    if (msg) return msg;
  }
  return `${fallback} (HTTP ${result.status})`;
}

/**
 * 生成客户端幂等请求 ID。
 */
export function newClientRequestId(prefix = 'req'): string {
  if (typeof globalThis.crypto?.randomUUID === 'function') {
    return `${prefix}-${globalThis.crypto.randomUUID()}`;
  }
  return `${prefix}-${Date.now()}-${Math.random().toString(16).slice(2, 10)}`;
}

/**
 * 根据认证方式构造传给后端 DPAPI 控制面的 secretPayload JSON（调用后调用方需立即清空密码输入态）。
 */
export function buildSecretPayloadJson(
  authKind: BusinessAuthKind,
  fields: {
    token: string;
    headerName: string;
    apiKey: string;
    username: string;
    password: string;
  },
): string {
  switch (authKind) {
    case 'None':
      return '{}';
    case 'Bearer':
    case 'OAuth2AuthCodePkce':
      return JSON.stringify({ token: fields.token.trim() });
    case 'ApiKey':
      return JSON.stringify({
        headerName: fields.headerName.trim() || 'X-API-Key',
        apiKey: fields.apiKey.trim(),
      });
    case 'BasicTlsOnly':
      return JSON.stringify({
        username: fields.username.trim(),
        password: fields.password,
      });
  }
}

export function isNonTerminalRunStatus(status?: SkillRunStatus | null): boolean {
  return status === 'Queued' || status === 'Running' || status === 'WaitingForApproval';
}

export function getRunStatusMeta(status?: SkillRunStatus | null): {
  label: string;
  badgeClass: string;
} {
  switch (status) {
    case 'Queued':
      return { label: '排队中', badgeClass: 'border-slate-600 bg-slate-800 text-slate-200' };
    case 'Running':
      return { label: '执行中', badgeClass: 'border-indigo-500/40 bg-indigo-500/15 text-indigo-300' };
    case 'WaitingForApproval':
      return { label: '等待人工批准确认', badgeClass: 'border-amber-500/50 bg-amber-500/15 text-amber-300' };
    case 'Succeeded':
      return { label: '已成功', badgeClass: 'border-emerald-500/40 bg-emerald-500/15 text-emerald-300' };
    case 'PartialSucceeded':
      return { label: '部分成功', badgeClass: 'border-amber-500/40 bg-amber-500/15 text-amber-200' };
    case 'Failed':
      return { label: '执行失败', badgeClass: 'border-rose-500/40 bg-rose-500/15 text-rose-300' };
    case 'Cancelled':
      return { label: '已停止', badgeClass: 'border-slate-500/40 bg-slate-700/40 text-slate-300' };
    case 'OutcomeUnknown':
      return {
        label: '结果未知（需核对目标系统）',
        badgeClass: 'border-orange-500/60 bg-orange-500/20 text-orange-200',
      };
    default:
      return { label: '未知状态', badgeClass: 'border-slate-700 bg-slate-800 text-slate-300' };
  }
}

export function getStepStateBadgeClass(state?: SkillRunStepState | null): string {
  switch (state) {
    case 'Succeeded':
      return 'text-emerald-300';
    case 'Dispatching':
      return 'text-indigo-300';
    case 'Prepared':
      return 'text-slate-300';
    case 'OutcomeUnknown':
      return 'text-orange-300';
    case 'Failed':
      return 'text-rose-300';
    case 'Cancelled':
      return 'text-slate-400';
    default:
      return 'text-slate-400';
  }
}

export function getRiskBadgeInfo(risk?: CapabilityRiskLevel | null): {
  label: string;
  className: string;
} {
  switch (risk) {
    case 'Read':
      return { label: '只读 Read', className: 'border-emerald-500/30 bg-emerald-500/10 text-emerald-300' };
    case 'Write':
      return { label: '写入 Write', className: 'border-amber-500/40 bg-amber-500/15 text-amber-300' };
    case 'Destructive':
      return { label: '高危 Destructive', className: 'border-rose-500/40 bg-rose-500/15 text-rose-300' };
    default:
      return { label: '未分级 Unknown', className: 'border-slate-600 bg-slate-800 text-slate-300' };
  }
}

/**
 * 为动态 Skill 输入表单生成初始文本值映射。
 */
export function buildInitialInputDrafts(schema?: SkillInputSchemaDto | null): Record<string, string> {
  const result: Record<string, string> = {};
  const properties = schema?.properties ?? {};
  for (const [key, prop] of Object.entries(properties)) {
    if (prop.defaultValue !== undefined && prop.defaultValue !== null) {
      result[key] =
        typeof prop.defaultValue === 'string' ? prop.defaultValue : JSON.stringify(prop.defaultValue);
    } else if (prop.type === 'boolean') {
      result[key] = 'false';
    } else {
      result[key] = '';
    }
  }
  return result;
}

/**
 * 根据 SkillInputSchemaDto 将表单文本值解析并校验为 StartSkillRunRequest.inputs 负荷。
 */
export function parseDynamicInputs(
  schema: SkillInputSchemaDto | null | undefined,
  draftValues: Record<string, string>,
): { inputs?: StartSkillRunRequest['inputs']; error?: string } {
  const properties: Record<string, SkillInputPropertyDto> = schema?.properties ?? {};
  const required = new Set(schema?.required ?? []);
  const parsed: Record<string, unknown> = {};

  for (const [key, prop] of Object.entries(properties)) {
    const raw = (draftValues[key] ?? '').trim();
    if (!raw) {
      if (required.has(key)) {
        return { error: `请填写必填参数「${key}」` };
      }
      continue;
    }

    const kind = prop.type ?? 'string';
    if (kind === 'number' || kind === 'integer') {
      const num = Number(raw);
      if (Number.isNaN(num) || (kind === 'integer' && !Number.isInteger(num))) {
        return { error: `参数「${key}」需要有效的${kind === 'integer' ? '整数' : '数字'}` };
      }
      parsed[key] = num;
    } else if (kind === 'boolean') {
      if (raw !== 'true' && raw !== 'false') {
        return { error: `参数「${key}」必须为 true 或 false` };
      }
      parsed[key] = raw === 'true';
    } else if (kind === 'array' || kind === 'object') {
      try {
        parsed[key] = JSON.parse(raw);
      } catch {
        return { error: `参数「${key}」需要合法的 JSON 格式` };
      }
    } else {
      parsed[key] = draftValues[key] ?? '';
    }
  }

  return { inputs: parsed as unknown as StartSkillRunRequest['inputs'] };
}
