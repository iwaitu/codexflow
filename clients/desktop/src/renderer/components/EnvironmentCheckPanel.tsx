import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  AlertTriangle,
  CheckCircle2,
  Copy,
  Download,
  ExternalLink,
  Loader2,
  RefreshCw,
  Square,
  Terminal,
  Wrench,
  XCircle,
} from 'lucide-react';
import type {
  EnvironmentDependencyId,
  EnvironmentDependencyItem,
  EnvironmentInstallEvent,
  EnvironmentReport,
} from '@shared/ipc';
import { useI18n } from '../lib/i18n';

export interface EnvironmentCheckPanelProps {
  active: boolean;
  compact?: boolean;
  onReportUpdated?: (report: EnvironmentReport) => void;
}

const MAX_LOG_CHARS = 16_000;

export const EnvironmentCheckPanel: React.FC<EnvironmentCheckPanelProps> = ({
  active,
  compact = false,
  onReportUpdated,
}) => {
  const { t } = useI18n();
  const [report, setReport] = useState<EnvironmentReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [installingId, setInstallingId] = useState<EnvironmentDependencyId | null>(null);
  const [installPhase, setInstallPhase] = useState<EnvironmentInstallEvent['phase'] | null>(null);
  const [installMessage, setInstallMessage] = useState<string | null>(null);
  const [installLog, setInstallLog] = useState('');
  const [copiedId, setCopiedId] = useState<string | null>(null);
  const logRef = useRef<HTMLPreElement | null>(null);
  const onReportUpdatedRef = useRef(onReportUpdated);
  onReportUpdatedRef.current = onReportUpdated;

  const loadEnvironment = useCallback(async (forceRefresh = false) => {
    const bridge = window.codexflow;
    if (!bridge?.inspectEnvironment) {
      return;
    }
    setLoading(true);
    setError(null);
    try {
      const next = await bridge.inspectEnvironment(forceRefresh);
      setReport(next);
      onReportUpdatedRef.current?.(next);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    if (!active) {
      return;
    }
    void loadEnvironment(false);
  }, [active, loadEnvironment]);

  useEffect(() => {
    const bridge = window.codexflow;
    if (!bridge?.onEnvironmentInstallEvent) {
      return;
    }
    const unsubscribe = bridge.onEnvironmentInstallEvent((event) => {
      setInstallPhase(event.phase);
      setInstallMessage(event.message);
      if (event.logChunk) {
        setInstallLog((prev) => {
          const combined = prev + event.logChunk;
          return combined.length > MAX_LOG_CHARS ? combined.slice(-MAX_LOG_CHARS) : combined;
        });
      }
      if (event.report) {
        setReport(event.report);
        onReportUpdatedRef.current?.(event.report);
      }
      if (event.phase === 'starting' || event.phase === 'running' || event.phase === 'verifying') {
        setInstallingId(event.dependencyId);
      } else {
        setInstallingId(null);
      }
    });
    return () => {
      unsubscribe?.();
    };
  }, []);

  useEffect(() => {
    if (logRef.current) {
      logRef.current.scrollTop = logRef.current.scrollHeight;
    }
  }, [installLog]);

  const handleInstall = async (dependencyId: EnvironmentDependencyId) => {
    const bridge = window.codexflow;
    if (!bridge?.installEnvironmentDependency) {
      return;
    }
    setError(null);
    setInstallLog('');
    setInstallMessage(null);
    setInstallingId(dependencyId);
    setInstallPhase('starting');
    const res = await bridge.installEnvironmentDependency(dependencyId);
    if (!res.accepted) {
      setInstallingId(null);
      setInstallPhase('failed');
      setError(res.error || '无法启动安装任务。');
    }
  };

  const handleCancelInstall = async () => {
    await window.codexflow?.cancelEnvironmentInstall?.();
  };

  const handleCopyCommand = async (id: string, command: string) => {
    try {
      await navigator.clipboard?.writeText(command);
      setCopiedId(id);
      window.setTimeout(() => {
        setCopiedId((cur) => (cur === id ? null : cur));
      }, 1800);
    } catch {
      // ignore clipboard errors
    }
  };

  const handleOpenDownload = (url: string) => {
    void window.codexflow?.openExternal?.(url);
  };

  const renderStatusBadge = (item: EnvironmentDependencyItem) => {
    switch (item.status) {
      case 'ready':
        return (
          <span className="inline-flex items-center gap-1 rounded-full bg-emerald-500/15 px-2 py-0.5 text-[11px] font-medium text-emerald-300">
            <CheckCircle2 className="h-3 w-3" />
            {t('env.status.ready')}
          </span>
        );
      case 'stopped':
      case 'outdated':
        return (
          <span className="inline-flex items-center gap-1 rounded-full bg-amber-500/15 px-2 py-0.5 text-[11px] font-medium text-amber-300">
            <AlertTriangle className="h-3 w-3" />
            {item.status === 'stopped' ? t('env.status.stopped') : t('env.status.outdated')}
          </span>
        );
      case 'error':
        return (
          <span className="inline-flex items-center gap-1 rounded-full bg-rose-500/15 px-2 py-0.5 text-[11px] font-medium text-rose-300">
            <XCircle className="h-3 w-3" />
            {t('env.status.error')}
          </span>
        );
      default:
        return (
          <span className="inline-flex items-center gap-1 rounded-full bg-slate-800 px-2 py-0.5 text-[11px] font-medium text-slate-300">
            <AlertTriangle className="h-3 w-3 text-amber-400" />
            {t('env.status.missing')}
          </span>
        );
    }
  };

  const readyCount = report?.items.filter((item) => item.status === 'ready').length ?? 0;
  const totalCount = report?.items.length ?? 0;
  const isBusy = installingId !== null;

  return (
    <div data-testid="environment-check-panel" className={`space-y-3 text-xs ${compact ? '' : 'mt-4'}`}>
      <div className="flex flex-wrap items-center justify-between gap-2 rounded-xl border border-slate-800 bg-slate-950/70 p-3">
        <div>
          <div className="flex items-center gap-2 font-medium text-slate-100">
            <Wrench className="h-4 w-4 text-indigo-400" />
            <span>{t('env.title')}</span>
            {report && (
              <span
                data-testid="environment-ready-summary"
                className="rounded-full bg-slate-800 px-2 py-0.5 text-[11px] text-slate-300"
              >
                {t('env.summaryReady', { ready: readyCount, total: totalCount })}
              </span>
            )}
          </div>
          <p className="mt-1 text-[11px] text-slate-400">{t('env.subtitle')}</p>
          {report && (
            <div className="mt-1.5 flex flex-wrap items-center gap-2 text-[11px] text-slate-400">
              <span className="rounded bg-slate-900 px-1.5 py-0.5 font-mono text-slate-300">
                {report.platform} / {report.arch}
              </span>
              <span>
                {report.packageManager
                  ? t('env.packageManager', { manager: report.packageManager })
                  : t('env.packageManagerNone')}
              </span>
              {report.shellPathEnhanced && (
                <span className="rounded bg-indigo-500/15 px-1.5 py-0.5 text-indigo-300">
                  {t('env.pathEnhanced')}
                </span>
              )}
            </div>
          )}
        </div>

        <button
          type="button"
          data-testid="environment-recheck-button"
          disabled={loading || isBusy}
          onClick={() => void loadEnvironment(true)}
          className="inline-flex items-center gap-1.5 rounded-lg border border-slate-700 bg-slate-900 px-3 py-1.5 font-medium text-slate-200 hover:bg-slate-800 disabled:opacity-40"
        >
          <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          {loading ? t('env.checking') : t('env.recheck')}
        </button>
      </div>

      {error && (
        <div
          data-testid="environment-error-banner"
          className="rounded-lg border border-rose-500/40 bg-rose-950/40 p-2.5 text-rose-200"
        >
          {error}
        </div>
      )}

      {(installMessage || installLog) && (
        <div
          data-testid="environment-install-console"
          className="rounded-xl border border-slate-800 bg-slate-950 p-3"
        >
          <div className="flex items-center justify-between gap-2">
            <div className="flex items-center gap-2 font-medium text-slate-200">
              {isBusy ? (
                <Loader2 className="h-3.5 w-3.5 animate-spin text-indigo-400" />
              ) : (
                <Terminal className="h-3.5 w-3.5 text-slate-400" />
              )}
              <span>{installMessage ?? t('env.logTitle')}</span>
            </div>
            {isBusy && (
              <button
                type="button"
                data-testid="environment-cancel-install-button"
                onClick={() => void handleCancelInstall()}
                className="inline-flex items-center gap-1 rounded-md border border-rose-500/40 bg-rose-500/15 px-2 py-1 text-[11px] font-medium text-rose-200 hover:bg-rose-500/25"
              >
                <Square className="h-3 w-3" />
                {t('env.action.cancel')}
              </button>
            )}
          </div>
          {installLog && (
            <pre
              ref={logRef}
              className={`mt-2 max-h-36 overflow-auto rounded-lg border border-slate-800 bg-black/50 p-2 font-mono text-[11px] leading-relaxed ${
                installPhase === 'failed' ? 'text-rose-200' : 'text-slate-300'
              }`}
            >
              {installLog}
            </pre>
          )}
        </div>
      )}

      {report?.platform === 'darwin' && !report.packageManager && (
        <div className="rounded-lg border border-amber-500/30 bg-amber-500/10 p-2.5 text-[11px] text-amber-200">
          <div>{t('env.homebrewHint')}</div>
          <div className="mt-1.5 flex flex-wrap items-center gap-2">
            <code className="rounded bg-slate-950/80 px-2 py-1 font-mono text-[10px] text-amber-100">
              /bin/bash -c &quot;$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)&quot;
            </code>
            <button
              type="button"
              onClick={() =>
                void handleCopyCommand(
                  'homebrew',
                  '/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"',
                )
              }
              className="inline-flex items-center gap-1 rounded border border-amber-500/40 bg-amber-500/15 px-2 py-1 text-[10px] text-amber-100 hover:bg-amber-500/25"
            >
              <Copy className="h-3 w-3" />
              {copiedId === 'homebrew' ? t('env.action.copied') : t('env.action.copyCommand')}
            </button>
          </div>
        </div>
      )}

      <div className="space-y-2">
        {(report?.items ?? []).map((item) => {
          const isItemInstalling = installingId === item.id;
          const severityLabel =
            item.severity === 'required'
              ? t('env.severity.required')
              : item.severity === 'recommended'
                ? t('env.severity.recommended')
                : t('env.severity.optional');

          return (
            <div
              key={item.id}
              data-testid={`env-item-${item.id}`}
              className="rounded-xl border border-slate-800 bg-slate-950/60 p-3"
            >
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium text-slate-100">{item.name}</span>
                    <span
                      className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${
                        item.severity === 'required'
                          ? 'bg-rose-500/15 text-rose-300'
                          : item.severity === 'recommended'
                            ? 'bg-indigo-500/15 text-indigo-300'
                            : 'bg-slate-800 text-slate-400'
                      }`}
                    >
                      {severityLabel}
                    </span>
                    {item.bundled && (
                      <span className="rounded bg-emerald-500/15 px-1.5 py-0.5 text-[10px] text-emerald-300">
                        {t('env.bundledBadge')}
                      </span>
                    )}
                    {renderStatusBadge(item)}
                    {item.version && (
                      <span className="font-mono text-[11px] text-slate-400">{item.version}</span>
                    )}
                  </div>
                  <p className="mt-1 text-[11px] text-slate-400">{item.summary}</p>
                  {item.resolvedPath && (
                    <div className="mt-1 truncate font-mono text-[10px] text-slate-500" title={item.resolvedPath}>
                      {item.resolvedPath}
                    </div>
                  )}
                  {item.detail && item.status !== 'ready' && (
                    <div className="mt-1 text-[11px] text-amber-300/90">{item.detail}</div>
                  )}
                </div>

                {item.status !== 'ready' && (
                  <div className="flex flex-wrap items-center gap-1.5">
                    {item.installStrategy === 'auto' && (
                      <button
                        type="button"
                        data-testid={`env-install-${item.id}`}
                        disabled={isBusy}
                        onClick={() => void handleInstall(item.id)}
                        className="inline-flex items-center gap-1 rounded-lg bg-indigo-600 px-2.5 py-1.5 text-[11px] font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
                      >
                        {isItemInstalling ? (
                          <Loader2 className="h-3.5 w-3.5 animate-spin" />
                        ) : (
                          <Download className="h-3.5 w-3.5" />
                        )}
                        {isItemInstalling ? t('env.action.installing') : t('env.action.install')}
                      </button>
                    )}

                    {item.installCommandPreview && (
                      <button
                        type="button"
                        data-testid={`env-copy-${item.id}`}
                        onClick={() => void handleCopyCommand(item.id, item.installCommandPreview!)}
                        className="inline-flex items-center gap-1 rounded-lg border border-slate-700 bg-slate-900 px-2.5 py-1.5 text-[11px] text-slate-200 hover:bg-slate-800"
                      >
                        <Copy className="h-3 w-3" />
                        {copiedId === item.id ? t('env.action.copied') : t('env.action.copyCommand')}
                      </button>
                    )}

                    {item.downloadUrl && (
                      <button
                        type="button"
                        data-testid={`env-download-${item.id}`}
                        onClick={() => handleOpenDownload(item.downloadUrl!)}
                        className="inline-flex items-center gap-1 rounded-lg border border-slate-700 bg-slate-900 px-2.5 py-1.5 text-[11px] text-slate-200 hover:bg-slate-800"
                      >
                        <ExternalLink className="h-3 w-3" />
                        {t('env.action.download')}
                      </button>
                    )}
                  </div>
                )}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
};
