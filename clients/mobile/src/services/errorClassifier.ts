export interface MobileAppError {
  readonly code: string;
  readonly httpStatus: number;
  readonly title: string;
  readonly message: string;
  readonly recoverable: boolean;
  readonly requiresRepair: boolean;
}

const REVOCATION_CODES = new Set([
  'refresh_token_reuse_detected',
  'token_reuse_detected',
  'pairing_revoked',
  'pairing_or_key_revoked',
  'revoked_token',
  'revocation_seq_mismatch',
  'desktop_revoked',
  'mobile_revoked',
  'account_revoked',
  'pairing_inactive',
  'binding_version_mismatch',
  'account_or_device_revoked',
  'device_or_account_revoked',
]);

export function isTerminalRevocationCode(code: string): boolean {
  return REVOCATION_CODES.has(code.trim().toLowerCase());
}

export function classifyRemoteError(
  code: string,
  httpStatus: number,
  rawMessage?: string,
): MobileAppError {
  const norm = code.trim().toLowerCase();
  const detail = rawMessage?.trim() || '';

  if (isTerminalRevocationCode(norm)) {
    return {
      code: norm,
      httpStatus,
      title: '配对凭据已吊销',
      message: detail || '当前设备配对已在官网或桌面端撤销，本地密钥已清理，请重新扫码配对。',
      recoverable: false,
      requiresRepair: true,
    };
  }

  switch (norm) {
    case 'desktop_offline':
    case 'desktop_offline_or_no_cert':
    case 'desktop_not_ready':
    case 'local_backend_unreachable':
    case 'host_backend_unavailable':
      return {
        code: norm,
        httpStatus,
        title: '桌面端暂不在线',
        message: detail || '请确认电脑上的 CodexFlow 桌面端已启动且远程控制开关处于开启状态。',
        recoverable: true,
        requiresRepair: false,
      };

    case 'biometric_key_required':
    case 'biometric_key_unavailable':
      return {
        code: norm,
        httpStatus,
        title: '需要生物识别高危签名密钥',
        message: '此操作必须使用已启用硬件生物识别的高权限密钥签名，请在开启 Face ID / 指纹锁后重新配对。',
        recoverable: false,
        requiresRepair: false,
      };

    case 'remote_workspace_not_authorized':
    case 'remote_workspace_forbidden':
      return {
        code: norm,
        httpStatus,
        title: '外部工作区未授权远程访问',
        message: detail || '出于安全边界限制，请先在桌面端「设置 -> 远程控制」中显式授权该外部项目目录。',
        recoverable: false,
        requiresRepair: false,
      };

    case 'unsupported_ephemeral_plan':
      return {
        code: norm,
        httpStatus,
        title: '临时计划暂不支持远程批准',
        message: '该会话计划尚未持久化为正式 PlanArtifact，请在桌面端确认或等待计划持久化后再试。',
        recoverable: false,
        requiresRepair: false,
      };

    case 'upload_id_conflict':
    case 'chat_op_conflict':
      return {
        code: norm,
        httpStatus,
        title: '幂等摘要冲突（已阻止重放）',
        message: detail || '检测到同一操作 ID 被复用于不同内容，已中止该请求。',
        recoverable: false,
        requiresRepair: false,
      };

    case 'desktop_spki_fingerprint_mismatch':
    case 'sas_mismatch':
    case 'pair_ack_signature_invalid':
    case 'envelope_verification_failed':
      return {
        code: norm,
        httpStatus,
        title: '安全校验未通过',
        message: detail || '桌面端加密指纹或签名为伪造/失效状态，已拒绝继续执行。',
        recoverable: false,
        requiresRepair: false,
      };

    case 'native_crypto_unavailable':
      return {
        code: norm,
        httpStatus,
        title: '原生安全硬件不可用',
        message: detail || '当前环境无法访问硬件安全密钥库，生产构建已阻断降级运行。',
        recoverable: false,
        requiresRepair: false,
      };

    case 'timestamp_skew_exceeded':
      return {
        code: norm,
        httpStatus,
        title: '设备时钟偏差过大',
        message: detail || '手机与桌面端时间偏差超过 60 秒，请检查系统自动校准时间。',
        recoverable: true,
        requiresRepair: false,
      };

    default:
      return {
        code: norm || `http_${httpStatus}`,
        httpStatus,
        title: httpStatus >= 500 ? '服务暂不可用' : '操作失败',
        message: detail || `请求返回状态 ${httpStatus}（${norm || 'unknown_error'}）。`,
        recoverable: httpStatus >= 500 || httpStatus === 429 || httpStatus === 408,
        requiresRepair: false,
      };
  }
}

export class RemoteApiException extends Error {
  public readonly appError: MobileAppError;

  public constructor(appError: MobileAppError) {
    super(appError.message);
    this.name = 'RemoteApiException';
    this.appError = appError;
  }
}
