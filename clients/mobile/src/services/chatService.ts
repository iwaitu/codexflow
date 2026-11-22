import {
  buildCanonicalStructuredActionV1,
  buildChatOpV1Payload,
  type KeyPurpose,
} from '@codexflow-shared/remoteProtocol';
import type {
  GatewayChatAcceptedResponse,
  RemoteChatOpStatusResponse,
  UploadedAttachmentDto,
} from '@/api/hostTypes';
import { sha256HexLower } from '@/crypto/incrementalSha256';
import {
  verifyPlanEnvelopeAsync,
  type SignedPlanEnvelopeInput,
} from '@/crypto/p256Verify';
import { classifyRemoteError, RemoteApiException } from './errorClassifier';
import type { RelayClient } from './relayClient';

export interface MobileChatAttachmentRef {
  readonly uploadId: string;
  readonly dto: UploadedAttachmentDto;
}

export interface MobileStructuredPlanAction {
  readonly action: 'approve' | 'execute' | 'reject' | 'stop' | 'planOnly';
  readonly planArtifactId?: string | null;
  readonly planVersion?: string | null;
  readonly planHash?: string | null;
  readonly idempotencyKey?: string | null;
  readonly startExecution?: boolean | null;
  readonly markdown?: string | null;
  readonly signedEnvelope?: SignedPlanEnvelopeInput | null;
}

function randomClientOpId(): string {
  if (typeof globalThis.crypto.randomUUID === 'function') {
    return globalThis.crypto.randomUUID();
  }
  return `op_${Date.now()}_${Math.random().toString(16).slice(2, 10)}`;
}

export function computeMobileChatOpDigest(params: {
  sessionId: string;
  conversationId?: string | null;
  conversationMode?: string | null;
  uploadIds: readonly string[];
  structuredAction?: MobileStructuredPlanAction | null;
  message: string;
}): string {
  const mode = params.conversationMode?.trim().toLowerCase() || 'execution';
  const canonicalSa = params.structuredAction
    ? buildCanonicalStructuredActionV1({
        action: params.structuredAction.action,
        planArtifactId: params.structuredAction.planArtifactId ?? null,
        planVersion: params.structuredAction.planVersion ?? null,
        planHash: params.structuredAction.planHash ?? null,
        idempotencyKey: params.structuredAction.idempotencyKey ?? null,
        startExecution: params.structuredAction.startExecution ?? null,
      })
    : '';
  const payload = buildChatOpV1Payload(
    params.sessionId,
    params.conversationId ?? '',
    mode,
    params.uploadIds,
    canonicalSa,
    params.message,
  );
  return sha256HexLower(payload);
}

export async function sendChatWithIdempotency(params: {
  relayClient: RelayClient;
  sessionId: string;
  conversationId?: string | null;
  conversationMode?: string | null;
  message: string;
  attachments?: readonly MobileChatAttachmentRef[];
  structuredAction?: MobileStructuredPlanAction | null;
  clientOpId?: string;
  retryDelayMs?: number;
  signal?: AbortSignal;
}): Promise<GatewayChatAcceptedResponse> {
  const { relayClient, sessionId } = params;
  const state = relayClient.getState();
  if (!state) {
    throw new RemoteApiException(classifyRemoteError('not_paired', 401, '设备尚未配对。'));
  }

  let keyPurpose: KeyPurpose = 'device_session';
  const sa = params.structuredAction ?? null;

  if (sa && (sa.action === 'approve' || sa.action === 'execute')) {
    if (!state.biometricEnrolled) {
      throw new RemoteApiException(
        classifyRemoteError('biometric_key_required', 403, '批准计划需要使用生物识别密钥签名。'),
      );
    }
    if (
      !sa.signedEnvelope ||
      !sa.planArtifactId ||
      !sa.planVersion ||
      !sa.planHash ||
      typeof sa.markdown !== 'string' ||
      typeof sa.startExecution !== 'boolean'
    ) {
      throw new RemoteApiException(
        classifyRemoteError(
          'envelope_verification_failed',
          403,
          '计划缺少完整的桌面端签名信封或显式执行标识。',
        ),
      );
    }

    const ok = await verifyPlanEnvelopeAsync({
      desktopSpkiB64: state.desktopSpkiB64,
      envelope: sa.signedEnvelope,
      expectedDeviceId: state.deviceId,
      expectedBindingVersion: state.bindingVersion,
      sessionId,
      planArtifactId: sa.planArtifactId,
      planVersion: sa.planVersion,
      planHash: sa.planHash,
      markdown: sa.markdown,
      action: sa.action,
      startExecution: sa.startExecution,
      nowSec: Math.floor(Date.now() / 1000),
    });
    if (!ok) {
      throw new RemoteApiException(
        classifyRemoteError(
          'envelope_verification_failed',
          403,
          '计划内容摘要与桌面签名信封不匹配或已过期（可能为 session-plan 临时计划）。',
        ),
      );
    }
    keyPurpose = 'biometric_approval';
  }

  const clientOpId = params.clientOpId ?? randomClientOpId();
  const uploadIds = (params.attachments ?? []).map((a) => a.uploadId);
  const requestDigest = computeMobileChatOpDigest({
    sessionId,
    conversationId: params.conversationId,
    conversationMode: params.conversationMode,
    uploadIds,
    structuredAction: sa,
    message: params.message,
  });

  const body: Record<string, unknown> = {
    sessionId,
    message: params.message,
  };
  if (params.conversationId) {
    body.conversationId = params.conversationId;
  }
  if (params.conversationMode) {
    body.conversationMode = params.conversationMode;
  }
  if (params.attachments && params.attachments.length > 0) {
    body.attachments = params.attachments.map((a) => ({
      name: a.dto.name,
      relativePath: a.dto.relativePath,
      sizeBytes: a.dto.sizeBytes,
      contentType: a.dto.contentType,
      isWorkspaceReference: false,
    }));
  }
  if (sa) {
    body.structuredAction = {
      action: sa.action,
      ...(sa.planArtifactId ? { planArtifactId: sa.planArtifactId } : {}),
      ...(sa.planVersion ? { planVersion: sa.planVersion } : {}),
      ...(sa.planHash ? { planHash: sa.planHash } : {}),
      ...(sa.idempotencyKey ? { idempotencyKey: sa.idempotencyKey } : {}),
      ...(typeof sa.startExecution === 'boolean' ? { startExecution: sa.startExecution } : {}),
      acceptExistingDependencyDebt: false,
      approveDependencyUpgrades: [],
    };
    if (sa.signedEnvelope) {
      body.signedEnvelope = sa.signedEnvelope;
    }
  }

  const opStatusPath = `/api/gateway/session/${encodeURIComponent(sessionId)}/ops/${encodeURIComponent(clientOpId)}`;
  const retryDelay = params.retryDelayMs ?? 450;

  for (let attempt = 0; attempt < 2; attempt += 1) {
    try {
      return await relayClient.requestJson<GatewayChatAcceptedResponse>({
        method: 'POST',
        path: '/api/gateway/chat',
        keyPurpose,
        clientOpId,
        jsonBody: body,
        promptReason: sa?.action ? `批准执行会话计划 (${sa.action})` : undefined,
        signal: params.signal,
      });
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') {
        throw err;
      }
      if (err instanceof RemoteApiException && !err.appError.recoverable && err.appError.httpStatus < 500) {
        throw err;
      }

      // 网络或 5xx 中断后查询 op 账本，防止重复投递用户消息
      try {
        const op = await relayClient.requestJson<RemoteChatOpStatusResponse>({
          method: 'GET',
          path: opStatusPath,
          queryPairs: [['requestDigest', requestDigest]],
          keyPurpose: 'device_session',
          signal: params.signal,
        });
        if (op.acceptedResponse) {
          return op.acceptedResponse;
        }
        if (op.status === 'queued' || op.status === 'running' || op.status === 'completed') {
          return {
            messageId: op.messageId ?? clientOpId,
            sessionId,
          };
        }
      } catch (statusErr) {
        if (statusErr instanceof RemoteApiException && statusErr.appError.httpStatus === 409) {
          throw statusErr;
        }
      }

      if (attempt >= 1) {
        throw err;
      }
      await new Promise((r) => setTimeout(r, retryDelay));
    }
  }

  throw new RemoteApiException(
    classifyRemoteError('chat_send_failed', 503, '消息发送失败，请重试。'),
  );
}
