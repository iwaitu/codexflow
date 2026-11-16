import type { ExecutionBackendKind, ExecutionPermissionModeKind } from '@shared/ipc';

export const STRICTNESS_RANK: Record<ExecutionPermissionModeKind, number> = {
  Deny: 3,
  Ask: 2,
  AutoInWorkspace: 1,
  FullAuto: 0,
};

export function isRelaxingPermission(
  current: ExecutionPermissionModeKind,
  next: ExecutionPermissionModeKind,
): boolean {
  return STRICTNESS_RANK[next] < STRICTNESS_RANK[current];
}

export function isExecutionRiskIncreasing(
  currentBackend: ExecutionBackendKind,
  currentMode: ExecutionPermissionModeKind,
  nextBackend: ExecutionBackendKind,
  nextMode: ExecutionPermissionModeKind,
): { risky: boolean; reasons: string[] } {
  const reasons: string[] = [];
  if (isRelaxingPermission(currentMode, nextMode)) {
    reasons.push(`权限模式从 ${currentMode} 放宽为 ${nextMode}`);
  }
  if (currentBackend === 'Docker' && nextBackend === 'Local') {
    reasons.push('执行后端从 Docker 隔离降级为 Local（无容器与网络隔离）');
  }
  return { risky: reasons.length > 0, reasons };
}
