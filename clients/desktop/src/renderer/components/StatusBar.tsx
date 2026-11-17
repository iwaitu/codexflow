import React from 'react';
import { AlertTriangle, Command, Languages, Moon, RefreshCw, Shield, ShieldAlert, Sun } from 'lucide-react';
import type { BackendConnectionPublicInfo } from '@shared/ipc';
import type { SessionContextWindow, SystemStatusSummary } from '../lib/apiClient';
import { useI18n } from '../lib/i18n';
import { usePreferencesStore } from '../stores/usePreferencesStore';
import { ContextUsageRing } from './ContextUsageRing';

interface StatusBarProps {
  connection: BackendConnectionPublicInfo | null;
  sseState: 'connecting' | 'connected' | 'reconnecting' | 'closed';
  systemStatus: SystemStatusSummary | null;
  contextWindow: SessionContextWindow | null;
  onOpenSettings: () => void;
  onOpenCommandPalette?: () => void;
  onRestartBackend: () => void;
}

export const StatusBar: React.FC<StatusBarProps> = ({
  connection,
  sseState,
  systemStatus,
  contextWindow,
  onOpenSettings,
  onOpenCommandPalette,
  onRestartBackend,
}) => {
  const { language, t } = useI18n();
  const { theme, toggleTheme, toggleLanguage } = usePreferencesStore();
  const statusLabel = {
    starting: t('status.starting'),
    ready: t('status.ready'),
    reconnecting: t('status.reconnecting'),
    error: t('status.error'),
    disconnected: t('status.disconnected'),
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
            {connection?.mode === 'standalone' ? t('status.standalone') : t('status.managed')}: {statusLabel} (SSE: {sseState})
          </span>
        </span>

        <button
          type="button"
          onClick={onOpenSettings}
          className="rounded border border-slate-800 bg-slate-900 px-2 py-0.5 text-slate-200 hover:bg-slate-800"
        >
          {t('status.backendAndPerm', { backend, perm })}
        </button>

        {!selfProtection && (
          <span
            data-testid="self-approval-warning"
            className="inline-flex items-center gap-1 rounded bg-rose-500/20 px-2 py-0.5 font-medium text-rose-300"
          >
            <ShieldAlert className="h-3.5 w-3.5" />
            {t('status.selfApprovalBroken')}
          </span>
        )}

        {!controlAvailable && (
          <span className="inline-flex items-center gap-1 rounded bg-amber-500/15 px-2 py-0.5 text-amber-300">
            <AlertTriangle className="h-3.5 w-3.5" />
            {t('status.readonlyNoControl')}
          </span>
        )}
      </div>

      <div className="flex items-center gap-2">
        {connection?.clientVersion && (
          <span
            data-testid="client-version-badge"
            className="rounded border border-slate-800 bg-slate-900 px-2 py-0.5 font-mono text-slate-300"
            title={
              systemStatus?.version
                ? t('status.hostVersion', { version: systemStatus.version })
                : t('status.clientVersion')
            }
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
            {t('status.modelUnconfigured')}
          </button>
        )}
        {connection?.mode === 'managed' && connection.status === 'error' && (
          <button
            type="button"
            onClick={onRestartBackend}
            className="inline-flex items-center gap-1 rounded bg-slate-800 px-2 py-0.5 text-slate-200 hover:bg-slate-700"
          >
            <RefreshCw className="h-3 w-3" />
            {t('status.restartBackend')}
          </button>
        )}

        {onOpenCommandPalette && (
          <button
            type="button"
            data-testid="status-command-palette-button"
            onClick={onOpenCommandPalette}
            title={`${t('nav.commandPalette')} (Ctrl/Cmd+K)`}
            aria-label={t('nav.commandPalette')}
            className="inline-flex items-center gap-1 rounded border border-slate-800 bg-slate-900 px-2 py-0.5 text-slate-300 hover:bg-slate-800 hover:text-slate-100"
          >
            <Command className="h-3 w-3" />
            <span className="font-mono text-[10px]">Ctrl+K</span>
          </button>
        )}

        <button
          type="button"
          data-testid="status-theme-toggle"
          onClick={toggleTheme}
          title={t('status.toggleTheme')}
          aria-label={t('status.toggleTheme')}
          className="inline-flex items-center rounded border border-slate-800 bg-slate-900 p-1 text-slate-300 hover:bg-slate-800 hover:text-slate-100"
        >
          {theme === 'dark' ? <Sun className="h-3.5 w-3.5" /> : <Moon className="h-3.5 w-3.5" />}
        </button>

        <button
          type="button"
          data-testid="status-language-toggle"
          onClick={toggleLanguage}
          title={t('status.toggleLanguage')}
          aria-label={t('status.toggleLanguage')}
          className="inline-flex items-center gap-1 rounded border border-slate-800 bg-slate-900 px-1.5 py-0.5 font-mono text-[10px] text-slate-300 hover:bg-slate-800 hover:text-slate-100"
        >
          <Languages className="h-3 w-3" />
          <span>{language === 'zh-CN' ? '中' : 'EN'}</span>
        </button>
      </div>
    </footer>
  );
};
