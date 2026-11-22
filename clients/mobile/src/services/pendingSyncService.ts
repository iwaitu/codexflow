import type {
  PendingApprovalScriptSnapshot,
  RemotePendingSummarySnapshotResponse,
  RemotePendingTodoItem,
} from '@/api/hostTypes';
import { verifyApprovalEnvelopeAsync } from '@/crypto/p256Verify';
import { classifyRemoteError, RemoteApiException } from './errorClassifier';
import type { RelayClient } from './relayClient';

export interface PendingSyncCursor {
  readonly summaryEpoch: number;
  readonly lastEventId: number;
}

export interface PendingSyncResult {
  readonly mode: 'snapshot' | 'delta';
  readonly cursor: PendingSyncCursor;
  readonly items: RemotePendingTodoItem[];
}

export function mapSourceKindToApprovalKind(sourceKind: string): string {
  const norm = sourceKind.trim().toLowerCase();
  if (norm === 'execution' || norm === 'command' || norm === 'execution_approval') {
    return 'command';
  }
  if (norm === 'business' || norm === 'business_approval') {
    return 'business';
  }
  if (norm === 'browser' || norm === 'browser_approval') {
    return 'browser';
  }
  if (norm === 'computer' || norm === 'computer_approval') {
    return 'computer';
  }
  return norm;
}

function itemKey(item: RemotePendingTodoItem): string {
  return `${item.sourceKind}\n${item.opaqueRefId}`;
}

export function mergeDeltaItems(
  existing: readonly RemotePendingTodoItem[],
  delta: readonly RemotePendingTodoItem[],
): RemotePendingTodoItem[] {
  const map = new Map<string, RemotePendingTodoItem>();
  for (const item of existing) {
    if (!item.isTerminal) {
      map.set(itemKey(item), item);
    }
  }
  for (const ev of delta) {
    const key = itemKey(ev);
    if (ev.isTerminal) {
      map.delete(key);
    } else {
      map.set(key, ev);
    }
  }
  return Array.from(map.values()).sort((a, b) => a.eventId - b.eventId);
}

async function fetchFullSnapshot(
  relayClient: RelayClient,
  limit: number,
  signal?: AbortSignal,
): Promise<PendingSyncResult> {
  const expectedBindingVersion = relayClient.getState()?.bindingVersion;

  for (let restart = 0; restart < 2; restart += 1) {
    let snapshotId: string | undefined;
    let pageToken: string | undefined;
    let summaryEpoch = 0;
    let resumeAfterEventId = 0;
    const collected: RemotePendingTodoItem[] = [];
    let restarted = false;

    while (true) {
      const queryPairs: Array<[string, string]> = [
        ['mode', 'snapshot'],
        ['limit', String(limit)],
      ];
      if (snapshotId && pageToken !== undefined) {
        queryPairs.push(['snapshotId', snapshotId], ['pageToken', pageToken]);
      }

      const page = await relayClient.requestJson<RemotePendingSummarySnapshotResponse>({
        method: 'GET',
        path: '/api/gateway/events/summary',
        queryPairs,
        keyPurpose: 'device_session',
        signal,
      });

      if (
        typeof page.bindingVersion === 'number' &&
        expectedBindingVersion !== undefined &&
        page.bindingVersion !== expectedBindingVersion
      ) {
        throw new RemoteApiException(
          classifyRemoteError('binding_version_mismatch', 401, '桌面端绑定代次已变更，需重新配对。'),
        );
      }

      if (page.resetRequired && snapshotId) {
        restarted = true;
        break;
      }

      snapshotId = page.snapshotId ?? snapshotId;
      summaryEpoch = page.summaryEpoch;
      resumeAfterEventId = page.resumeAfterEventId;
      collected.push(...page.items);

      if (page.snapshotComplete || !page.nextPageToken || !snapshotId) {
        return {
          mode: 'snapshot',
          cursor: {
            summaryEpoch,
            lastEventId: resumeAfterEventId,
          },
          items: collected.filter((i) => !i.isTerminal),
        };
      }

      pageToken = page.nextPageToken;
    }

    if (!restarted) {
      break;
    }
  }

  throw new RemoteApiException(
    classifyRemoteError('summary_snapshot_unstable', 503, '待办投影快照重建超时，请重试。'),
  );
}

export async function syncPendingSummary(params: {
  relayClient: RelayClient;
  currentItems: readonly RemotePendingTodoItem[];
  cursor: PendingSyncCursor | null;
  limit?: number;
  signal?: AbortSignal;
}): Promise<PendingSyncResult> {
  const limit = params.limit ?? 50;
  if (!params.cursor) {
    return fetchFullSnapshot(params.relayClient, limit, params.signal);
  }

  let afterEventId = params.cursor.lastEventId;
  const deltaCollected: RemotePendingTodoItem[] = [];
  const expectedBindingVersion = params.relayClient.getState()?.bindingVersion;

  while (true) {
    const page = await params.relayClient.requestJson<RemotePendingSummarySnapshotResponse>({
      method: 'GET',
      path: '/api/gateway/events/summary',
      queryPairs: [
        ['mode', 'delta'],
        ['afterEventId', String(afterEventId)],
        ['limit', String(limit)],
      ],
      keyPurpose: 'device_session',
      signal: params.signal,
    });

    if (
      typeof page.bindingVersion === 'number' &&
      expectedBindingVersion !== undefined &&
      page.bindingVersion !== expectedBindingVersion
    ) {
      throw new RemoteApiException(
        classifyRemoteError('binding_version_mismatch', 401, '桌面端绑定代次已变更，需重新配对。'),
      );
    }

    if (page.resetRequired || page.summaryEpoch !== params.cursor.summaryEpoch) {
      return fetchFullSnapshot(params.relayClient, limit, params.signal);
    }

    deltaCollected.push(...page.items);
    if (page.resumeAfterEventId > afterEventId) {
      afterEventId = page.resumeAfterEventId;
    }

    if (!page.nextPageToken || page.items.length < limit) {
      return {
        mode: 'delta',
        cursor: {
          summaryEpoch: page.summaryEpoch,
          lastEventId: afterEventId,
        },
        items: mergeDeltaItems(params.currentItems, deltaCollected),
      };
    }
  }
}

export async function fetchVerifiedApprovalScript(params: {
  relayClient: RelayClient;
  approvalId: string;
}): Promise<PendingApprovalScriptSnapshot> {
  const state = params.relayClient.getState();
  if (!state) {
    throw new RemoteApiException(classifyRemoteError('not_paired', 401, '设备尚未配对。'));
  }

  const snap = await params.relayClient.requestJson<PendingApprovalScriptSnapshot>({
    method: 'GET',
    path: `/api/gateway/approvals/${encodeURIComponent(params.approvalId)}/script`,
    keyPurpose: 'device_session',
  });

  if (!snap.signedEnvelope) {
    throw new RemoteApiException(
      classifyRemoteError('envelope_verification_failed', 403, '脚本快照缺少桌面签名信封。'),
    );
  }

  const valid = await verifyApprovalEnvelopeAsync({
    desktopSpkiB64: state.desktopSpkiB64,
    envelope: snap.signedEnvelope,
    expectedDeviceId: state.deviceId,
    expectedBindingVersion: state.bindingVersion,
    sessionId: snap.sessionId,
    approvalKind: 'command',
    approvalId: snap.approvalId,
    requestDigest: snap.requestDigest,
    title: `script:${snap.shell || 'shell'}`,
    summary: `scriptSha256:${snap.scriptSha256}`,
    payloadText: snap.scriptText,
    nowSec: Math.floor(Date.now() / 1000),
  });
  if (!valid) {
    throw new RemoteApiException(
      classifyRemoteError('envelope_verification_failed', 403, '完整脚本内容与桌面端 WYSIWYS 签名不一致。'),
    );
  }
  return snap;
}

export async function submitVerifiedTodoDecision(params: {
  relayClient: RelayClient;
  item: RemotePendingTodoItem;
  decision: 'allow_once' | 'deny';
  note?: string;
}): Promise<void> {
  const { relayClient, item, decision } = params;
  const state = relayClient.getState();
  if (!state) {
    throw new RemoteApiException(classifyRemoteError('not_paired', 401, '设备尚未配对。'));
  }
  if (item.readOnlyInRemote || item.isTerminal) {
    throw new RemoteApiException(
      classifyRemoteError('approval_read_only', 403, '该审批项当前在远程通道只读，请在桌面端处理。'),
    );
  }
  if (!state.biometricEnrolled) {
    throw new RemoteApiException(
      classifyRemoteError('biometric_key_required', 403, '高危审批必须启用硬件生物识别密钥。'),
    );
  }
  if (!item.approvalId || !item.requestDigest || !item.signedEnvelope) {
    throw new RemoteApiException(
      classifyRemoteError('envelope_verification_failed', 403, '待审批项缺少桌面签发的 CF-APPROVAL-ENV-V1 信封。'),
    );
  }

  const approvalKind = mapSourceKindToApprovalKind(item.sourceKind);
  const payloadText = JSON.stringify({
    sourceKind: item.sourceKind ?? null,
    approvalId: item.approvalId,
    sessionId: item.sessionId,
    title: item.title ?? null,
    summary: item.summary ?? null,
  });

  const valid = await verifyApprovalEnvelopeAsync({
    desktopSpkiB64: state.desktopSpkiB64,
    envelope: item.signedEnvelope,
    expectedDeviceId: state.deviceId,
    expectedBindingVersion: state.bindingVersion,
    sessionId: item.sessionId,
    approvalKind,
    approvalId: item.approvalId,
    requestDigest: item.requestDigest,
    title: item.title || approvalKind,
    summary: item.summary || item.approvalId,
    payloadText,
    nowSec: Math.floor(Date.now() / 1000),
  });
  if (!valid) {
    throw new RemoteApiException(
      classifyRemoteError(
        'envelope_verification_failed',
        403,
        '审批摘要本地 WYSIWYS 复算与桌面签名不匹配，或信封已过期，已拒绝签批。',
      ),
    );
  }

  await relayClient.requestJson({
    method: 'POST',
    path: `/api/gateway/approvals/${encodeURIComponent(item.approvalId)}`,
    keyPurpose: 'biometric_approval',
    promptReason: `${decision === 'allow_once' ? '允许一次' : '拒绝'}: ${item.title || item.approvalId}`,
    jsonBody: {
      requestDigest: item.requestDigest,
      decision,
      ...(params.note?.trim() ? { note: params.note.trim() } : {}),
      signedEnvelope: item.signedEnvelope,
    },
  });
}
