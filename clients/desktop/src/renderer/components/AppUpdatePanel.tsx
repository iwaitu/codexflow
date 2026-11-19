import React, { useEffect, useState } from 'react';
import { Download, RefreshCw } from 'lucide-react';
import type { AppUpdateState } from '@shared/ipc';
import { useI18n } from '../lib/i18n';

export interface AppUpdatePanelProps {
  active: boolean;
  clientVersion?: string | null;
  hostVersion?: string | null;
  autoCheckOnOpen?: boolean;
  className?: string;
}

const SKIP_AUTO_CHECK_STATUSES = new Set<AppUpdateState['status']>([
  'checking',
  'downloading',
  'downloaded',
  'unavailable',
]);

/**
 * Shared auto-update status and action panel used by both the dedicated UpdateModal
 * and the Preferences tab inside SettingsModal.
 */
export const AppUpdatePanel: React.FC<AppUpdatePanelProps> = ({
  active,
  clientVersion,
  hostVersion,
  autoCheckOnOpen = false,
  className = '',
}) => {
  const { t } = useI18n();
  const [updateState, setUpdateState] = useState<AppUpdateState | null>(null);

  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    const bridge = window.codexflow;

    const unsubscribe = bridge?.onUpdateStateChanged?.((state) => {
      if (!cancelled) {
        setUpdateState(state);
      }
    });

    const loadAndMaybeCheck = async () => {
      const initialState = await bridge?.getUpdateState?.().catch(() => null);
      if (cancelled) return;
      if (initialState) {
        setUpdateState(initialState);
      }
      if (autoCheckOnOpen && (!initialState || !SKIP_AUTO_CHECK_STATUSES.has(initialState.status))) {
        const checkedState = await bridge?.checkForUpdates?.().catch(() => null);
        if (!cancelled && checkedState) {
          setUpdateState(checkedState);
        }
      }
    };

    void loadAndMaybeCheck();

    return () => {
      cancelled = true;
      unsubscribe?.();
    };
  }, [active, autoCheckOnOpen]);

  const handleCheckForUpdates = () => {
    void window.codexflow?.checkForUpdates?.().then((state) => {
      if (state) setUpdateState(state);
    });
  };

  const displayVersion = updateState?.currentVersion ?? clientVersion ?? hostVersion ?? '0.1.14';
  const statusMessage =
    updateState?.status === 'available' && updateState.availableVersion
      ? t('settings.updateAvailable', { version: updateState.availableVersion })
      : updateState?.status === 'not-available'
        ? t('settings.updateNotAvailable')
        : updateState?.status === 'downloading'
          ? t('settings.downloadingUpdate', { progress: updateState.downloadProgress ?? 0 })
          : updateState?.status === 'downloaded' && updateState.availableVersion
            ? t('settings.updateDownloaded', { version: updateState.availableVersion })
            : updateState?.status === 'unavailable'
              ? t('settings.updateUnavailable')
              : updateState?.status === 'error'
                ? t('settings.updateError', { message: updateState.message ?? '' })
                : t('settings.updateIdle');

  return (
    <div className={`rounded-xl border border-slate-800 bg-slate-950 p-3.5 text-xs ${className}`.trim()}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <div className="font-medium text-slate-200">{t('settings.updatesSection')}</div>
          <div data-testid="update-current-version" className="mt-0.5 text-[11px] text-slate-400">
            {t('settings.currentVersion', { version: displayVersion })}
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {updateState?.status === 'available' && updateState.availableVersion && (
            <button
              type="button"
              data-testid="download-update-button"
              onClick={() => void window.codexflow?.downloadUpdate?.()}
              className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 font-medium text-white hover:bg-indigo-500"
            >
              <Download className="h-3.5 w-3.5" />
              {t('settings.downloadUpdate', { version: updateState.availableVersion })}
            </button>
          )}
          {updateState?.status === 'downloaded' && updateState.availableVersion && (
            <button
              type="button"
              data-testid="install-update-button"
              onClick={() => void window.codexflow?.quitAndInstall?.()}
              className="inline-flex items-center gap-1.5 rounded-lg bg-emerald-600 px-3 py-1.5 font-medium text-white hover:bg-emerald-500"
            >
              {t('settings.restartAndInstall', { version: updateState.availableVersion })}
            </button>
          )}
          <button
            type="button"
            data-testid="check-updates-button"
            disabled={updateState?.status === 'checking' || updateState?.status === 'downloading'}
            onClick={handleCheckForUpdates}
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-700 bg-slate-900 px-3 py-1.5 font-medium text-slate-200 hover:bg-slate-800 disabled:opacity-40"
          >
            <RefreshCw className={`h-3.5 w-3.5 ${updateState?.status === 'checking' ? 'animate-spin' : ''}`} />
            {updateState?.status === 'checking' ? t('settings.checkingUpdates') : t('settings.checkUpdates')}
          </button>
        </div>
      </div>

      {updateState?.status === 'downloading' && (
        <div className="mt-3 h-1.5 w-full overflow-hidden rounded-full bg-slate-800">
          <div
            data-testid="update-download-progress"
            className="h-full rounded-full bg-indigo-500 transition-all"
            style={{ width: `${Math.max(0, Math.min(100, updateState.downloadProgress ?? 0))}%` }}
          />
        </div>
      )}

      <div data-testid="update-status-message" className="mt-2 text-[11px] text-slate-400">
        {statusMessage}
      </div>
    </div>
  );
};
