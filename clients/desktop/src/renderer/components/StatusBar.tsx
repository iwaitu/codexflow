import React from 'react';
import { AlertTriangle, RefreshCw, Shield, ShieldAlert } from 'lucide-react';
import type { BackendConnectionPublicInfo } from '@shared/ipc';
import type { SessionContextWindow, SystemStatusSummary } from '../lib/apiClient';
import { ContextUsageRing } from './ContextUsageRing';

interface StatusBarProps {
  connection: BackendConnectionPublicInfo | null;
  sseState: 'connecting' | 'connected' | 'reconnecting' | 'closed';
  systemStatus: SystemStatusSummary | null;
  contextWindow: SessionContextWindow | null;
  onOpenSettings: () => void;
  onRestartBackend: () => void;
}

export const StatusBar: React.FC<StatusBarProps> = ({
  connection,
  sseState,
  systemStatus,
  contextWindow,
  onOpenSettings,
  onRestartBackend,
}) => {
  const statusLabel = {
    starting: '启动中',
    ready: '已连接',
    reconnecting: '重连中',
    error: '连接错误',
    disconnected: '已断开',
  }[connection?.status ?? 'starting'];

  const backend = systemStatus?.executionBackend ?? 'Local';
  const perm = systemStatus?.permissionMode ?? 'Ask';
  const selfProtection = connection?.selfApprovalProtection ?? systemStatus?.selfApprovalProtection ?? true;
  const controlAvailable = connection?.controlPlaneAvailable ?? systemStatus?.controlPlaneAvailable ?? true;

  return (
    <footer className="flex flex-wrap items-center justify-between gap-2 border-t border-slate-800 bg-slate-950 px-4 py-1.5 text-[11px] text-slate-400">
      <div className="flex flex-wrap items-center gap-3">
        <span className="inline-flex items-center gap-1.5">
          <span
            className={`h-2 w-2 rounded-full ${
              connection?.status === 'ready' && sseState === 'connected'
                ? 'bg-emerald-400'
                : connection?.status === 'error'
                  ? 'bg-rose-400'
                  : 'bg-amber-400'
            }`}
          />
          <span>
            {connection?.mode === 'standalone' ? '独立实例' : '托管宿主'}: {statusLabel} (SSE: {sseState})
          </span>
        </span>

        <button
          type="button"
          onClick={onOpenSettings}
          className="rounded border border-slate-800 bg-slate-900 px-2 py-0.5 text-slate-200 hover:bg-slate-800"
        >
          后端: <strong>{backend}</strong> · 权限: <strong>{perm}</strong>
        </button>

        {!selfProtection && (
          <span
            data-testid="self-approval-warning"
            className="inline-flex items-center gap-1 rounded bg-rose-500/20 px-2 py-0.5 font-medium text-rose-300"
          >
            <ShieldAlert className="h-3.5 w-3.5" />
            防自批保证不成立
          </span>
        )}

        {!controlAvailable && (
          <span className="inline-flex items-center gap-1 rounded bg-amber-500/15 px-2 py-0.5 text-amber-300">
            <AlertTriangle className="h-3.5 w-3.5" />
            只读连接（无控制面）
          </span>
        )}
      </div>

      <div className="flex items-center gap-3">
        {connection?.clientVersion && (
          <span
            data-testid="client-version-badge"
            className="rounded border border-slate-800 bg-slate-900 px-2 py-0.5 font-mono text-slate-300"
            title={systemStatus?.version ? `宿主版本: ${systemStatus.version}` : '客户端版本'}
          >
            v{connection.clientVersion}
          </span>
        )}
        {contextWindow && <ContextUsageRing contextWindow={contextWindow} />}
        {systemStatus && !systemStatus.modelConfigured && (
          <button
            type="button"
            onClick={onOpenSettings}
            className="inline-flex items-center gap-1 text-amber-300 hover:underline"
          >
            <Shield className="h-3 w-3" />
            未配置模型
          </button>
        )}
        {connection?.mode === 'managed' && connection.status === 'error' && (
          <button
            type="button"
            onClick={onRestartBackend}
            className="inline-flex items-center gap-1 rounded bg-slate-800 px-2 py-0.5 text-slate-200 hover:bg-slate-700"
          >
            <RefreshCw className="h-3 w-3" />
            重启后端
          </button>
        )}
      </div>
    </footer>
  );
};
