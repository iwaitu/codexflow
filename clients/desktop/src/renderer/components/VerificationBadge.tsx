import React from 'react';
import { AlertCircle, Clock, ShieldAlert, ShieldCheck } from 'lucide-react';
import type { BackgroundJob, VerificationReceipt } from '../lib/apiClient';

export type JobVerificationBadgeKind =
  | 'verified_isolated'
  | 'verified_unisolated'
  | 'failed'
  | 'unverified';

/**
 * Resolves the verification badge kind strictly from verifiable receipt/job evidence.
 * - Explicit failure ALWAYS takes precedence over unisolated/skip notices.
 * - Green `verified_isolated` is ONLY returned when a full positive container receipt exists
 *   (`identityVerified && buildPassed && testsPassed && !verifiedWithoutIsolation && verificationImageId/Image`).
 * - Summary text containing words like "Docker" without a receipt NEVER triggers green `verified_isolated`.
 */
export function resolveJobVerificationKind(
  job?: BackgroundJob | null,
  receipt?: VerificationReceipt | null,
): JobVerificationBadgeKind {
  // Global failure precedence: any explicit job or verification failure ALWAYS wins
  // over receipt flags (e.g. skipped/unisolated or even a partial/stale positive receipt).
  if (
    job?.status === 'Failed' ||
    job?.verification?.passed === false ||
    receipt?.failureCode ||
    receipt?.verificationStatus === 'failed' ||
    receipt?.buildPassed === false ||
    receipt?.testsPassed === false ||
    receipt?.identityVerified === false ||
    receipt?.audit?.passed === false
  ) {
    return 'failed';
  }

  if (receipt) {
    if (
      receipt.verifiedWithoutIsolation ||
      receipt.verificationStatus === 'skipped(requires_docker_backend)'
    ) {
      return 'verified_unisolated';
    }

    const hasExplicitReceiptSuccess =
      receipt.identityVerified === true &&
      receipt.buildPassed === true &&
      receipt.testsPassed === true &&
      receipt.verifiedWithoutIsolation === false &&
      Boolean(receipt.verificationImageId || receipt.verificationImage);

    if (hasExplicitReceiptSuccess) {
      return 'verified_isolated';
    }

    return 'unverified';
  }

  const summary = `${job?.summary ?? ''} ${job?.latestMessage ?? ''} ${job?.verification?.summary ?? ''}`;
  if (
    summary.includes('未在隔离环境中验证') ||
    summary.includes('skipped(requires_docker_backend)')
  ) {
    return 'verified_unisolated';
  }

  return 'unverified';
}

export function isUnverifiedWithoutIsolation(
  job?: BackgroundJob | null,
  receipt?: VerificationReceipt | null,
  activeBackend?: string | null,
): boolean {
  const kind = resolveJobVerificationKind(job, receipt);
  if (kind === 'verified_unisolated') {
    return true;
  }
  if (kind === 'unverified' && job?.status === 'Completed' && activeBackend === 'Local') {
    return true;
  }
  return false;
}

interface VerificationBadgeProps {
  kind?: JobVerificationBadgeKind;
  unverified?: boolean;
  notice?: string | null;
}

export const VerificationBadge: React.FC<VerificationBadgeProps> = ({ kind, unverified, notice }) => {
  // Never default to green `verified_isolated` when kind is omitted.
  const resolvedKind: JobVerificationBadgeKind =
    kind ?? (unverified ? 'verified_unisolated' : 'unverified');

  if (resolvedKind === 'verified_unisolated') {
    return (
      <span
        data-testid="unverified-isolation-badge"
        className="inline-flex items-center gap-1 rounded-full border border-amber-500/40 bg-amber-500/15 px-2.5 py-0.5 text-xs font-medium text-amber-300"
        title={notice || '本地执行模式下未经过容器隔离验证'}
      >
        <ShieldAlert className="h-3.5 w-3.5 shrink-0" />
        <span>未经隔离验证</span>
      </span>
    );
  }

  if (resolvedKind === 'failed') {
    return (
      <span
        data-testid="failed-verification-badge"
        className="inline-flex items-center gap-1 rounded-full border border-rose-500/40 bg-rose-500/15 px-2.5 py-0.5 text-xs font-medium text-rose-300"
        title={notice || '隔离验证失败'}
      >
        <AlertCircle className="h-3.5 w-3.5 shrink-0" />
        <span>验证失败</span>
      </span>
    );
  }

  if (resolvedKind === 'verified_isolated') {
    return (
      <span
        data-testid="verified-isolation-badge"
        className="inline-flex items-center gap-1 rounded-full border border-emerald-500/30 bg-emerald-500/10 px-2.5 py-0.5 text-xs font-medium text-emerald-300"
        title={notice || '已通过独立隔离容器构建与测试验证'}
      >
        <ShieldCheck className="h-3.5 w-3.5 shrink-0" />
        <span>隔离已验证</span>
      </span>
    );
  }

  return (
    <span
      data-testid="pending-verification-badge"
      className="inline-flex items-center gap-1 rounded-full border border-slate-600/50 bg-slate-800/70 px-2.5 py-0.5 text-xs font-medium text-slate-300"
      title={notice || '尚未完成或缺少容器隔离验证凭据'}
    >
      <Clock className="h-3.5 w-3.5 shrink-0" />
      <span>待隔离验证</span>
    </span>
  );
};
