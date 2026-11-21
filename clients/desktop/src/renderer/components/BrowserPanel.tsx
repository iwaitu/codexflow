import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  Activity,
  Check,
  Copy,
  Globe,
  Hand,
  Play,
  Power,
  RefreshCw,
  Terminal,
} from 'lucide-react';
import type { BrowserSessionStateDto } from '@shared/ipc';
import { apiClient, type ApiAuthContext } from '../lib/apiClient';

interface BrowserPanelProps {
  auth: ApiAuthContext | null;
  sessionId: string | null;
  browserState: BrowserSessionStateDto | null;
  controlPlaneAvailable: boolean;
  onStateUpdated: (next: BrowserSessionStateDto) => void;
}

type DrawerTab = 'actions' | 'console';

export const BrowserPanel: React.FC<BrowserPanelProps> = ({
  auth,
  sessionId,
  browserState,
  controlPlaneAvailable,
  onStateUpdated,
}) => {
  const [screenshotUrl, setScreenshotUrl] = useState<string | null>(null);
  const [loadingShot, setLoadingShot] = useState(false);
  const [shotError, setShotError] = useState<string | null>(null);
  const [closing, setClosing] = useState(false);
  const [takeoverBusy, setTakeoverBusy] = useState(false);
  const [copiedUrl, setCopiedUrl] = useState(false);
  const [drawerTab, setDrawerTab] = useState<DrawerTab>('actions');
  const lastFetchedSeqRef = useRef<number>(-1);
  const lastFetchedSessionRef = useRef<string | null>(null);
  const activeObjectUrlRef = useRef<string | null>(null);
  const activeFetchAbortRef = useRef<AbortController | null>(null);
  const fetchGenerationRef = useRef<number>(0);
  const mountedRef = useRef<boolean>(true);

  const status = browserState?.status ?? 'idle';
  const takeoverActive = status === 'takeover' || Boolean(browserState?.takeoverActive);
  const isRunning = status === 'running' || takeoverActive;
  const screenshotSeq = Number(browserState?.screenshotSeq ?? 0);
  const currentUrl = browserState?.url ?? '';
  let currentOrigin = '';
  if (currentUrl) {
    try {
      currentOrigin = new URL(currentUrl).origin;
    } catch {
      currentOrigin = '';
    }
  }
  const currentTitle = browserState?.title ?? '';
  const tabs = browserState?.tabs ?? [];
  const actions = browserState?.recentActions ?? [];
  const consoleLogs = browserState?.recentConsole ?? [];
  const consoleErrorCount = Number(browserState?.consoleErrorCount ?? 0);
  const modeLabel = browserState?.isRemote
    ? '远端 CDP·隔离'
    : `${browserState?.headless === false ? '有头窗口' : '无头'}${
        browserState?.profileMode === 'Persistent' ? '·持久化' : '·隔离'
      }`;

  const revokeCurrentObjectUrl = useCallback(() => {
    if (activeObjectUrlRef.current) {
      URL.revokeObjectURL(activeObjectUrlRef.current);
      activeObjectUrlRef.current = null;
    }
  }, []);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      fetchGenerationRef.current += 1;
      activeFetchAbortRef.current?.abort();
      activeFetchAbortRef.current = null;
      revokeCurrentObjectUrl();
    };
  }, [revokeCurrentObjectUrl]);

  const fetchScreenshot = useCallback(
    async (force = false) => {
      if (!auth || !sessionId || !isRunning || screenshotSeq <= 0) {
        fetchGenerationRef.current += 1;
        activeFetchAbortRef.current?.abort();
        activeFetchAbortRef.current = null;
        revokeCurrentObjectUrl();
        setScreenshotUrl(null);
        lastFetchedSeqRef.current = -1;
        lastFetchedSessionRef.current = sessionId ?? null;
        return;
      }
      if (
        !force &&
        lastFetchedSessionRef.current === sessionId &&
        lastFetchedSeqRef.current === screenshotSeq
      ) {
        return;
      }

      activeFetchAbortRef.current?.abort();
      const controller = new AbortController();
      activeFetchAbortRef.current = controller;
      const generation = ++fetchGenerationRef.current;

      setLoadingShot(true);
      setShotError(null);
      try {
        const res = await apiClient.getBrowserScreenshotBlob(auth, sessionId, controller.signal);
        if (
          !mountedRef.current ||
          controller.signal.aborted ||
          fetchGenerationRef.current !== generation
        ) {
          return;
        }
        if (!res) {
          revokeCurrentObjectUrl();
          setScreenshotUrl(null);
          lastFetchedSeqRef.current = 0;
          lastFetchedSessionRef.current = sessionId;
          return;
        }
        const nextUrl = URL.createObjectURL(res.blob);
        if (
          !mountedRef.current ||
          controller.signal.aborted ||
          fetchGenerationRef.current !== generation
        ) {
          URL.revokeObjectURL(nextUrl);
          return;
        }
        revokeCurrentObjectUrl();
        activeObjectUrlRef.current = nextUrl;
        lastFetchedSeqRef.current = res.seq || screenshotSeq;
        lastFetchedSessionRef.current = sessionId;
        setScreenshotUrl(nextUrl);
      } catch (err) {
        if (
          !mountedRef.current ||
          controller.signal.aborted ||
          fetchGenerationRef.current !== generation
        ) {
          return;
        }
        setShotError(err instanceof Error ? err.message : '加载截图失败');
      } finally {
        if (mountedRef.current && fetchGenerationRef.current === generation) {
          setLoadingShot(false);
        }
        if (activeFetchAbortRef.current === controller) {
          activeFetchAbortRef.current = null;
        }
      }
    },
    [auth, sessionId, isRunning, screenshotSeq, revokeCurrentObjectUrl],
  );

  useEffect(() => {
    void fetchScreenshot(false);
  }, [fetchScreenshot]);

  const handleRefreshAll = async () => {
    if (!auth || !sessionId) return;
    try {
      const fresh = await apiClient.getBrowserState(auth, sessionId);
      onStateUpdated(fresh);
      await fetchScreenshot(true);
    } catch (err) {
      setShotError(err instanceof Error ? err.message : '刷新浏览器状态失败');
    }
  };

  const handleCloseBrowser = async () => {
    if (!sessionId || !window.codexflow || !controlPlaneAvailable) return;
    setClosing(true);
    setShotError(null);
    try {
      const res = await window.codexflow.closeBrowserSession(sessionId);
      if (!res.ok) {
        setShotError(res.errorMessage || '关闭浏览器会话失败');
        return;
      }
      revokeCurrentObjectUrl();
      setScreenshotUrl(null);
      lastFetchedSeqRef.current = -1;
      if (auth) {
        const fresh = await apiClient.getBrowserState(auth, sessionId);
        onStateUpdated(fresh);
      }
    } catch (err) {
      setShotError(err instanceof Error ? err.message : '关闭浏览器会话失败');
    } finally {
      setClosing(false);
    }
  };

  const handleStartTakeover = async () => {
    if (!sessionId || !window.codexflow || !controlPlaneAvailable) return;
    setTakeoverBusy(true);
    setShotError(null);
    try {
      const res = await window.codexflow.startBrowserTakeover(sessionId, '用户在浏览器面板主动发起人工接管');
      if (!res.ok) {
        setShotError(res.errorMessage || '发起人工接管失败');
        return;
      }
      if (auth) {
        const fresh = await apiClient.getBrowserState(auth, sessionId);
        onStateUpdated(fresh);
      }
    } catch (err) {
      setShotError(err instanceof Error ? err.message : '发起人工接管失败');
    } finally {
      setTakeoverBusy(false);
    }
  };

  const handleCompleteTakeover = async () => {
    if (!sessionId || !window.codexflow || !controlPlaneAvailable) return;
    setTakeoverBusy(true);
    setShotError(null);
    try {
      const res = await window.codexflow.completeBrowserTakeover(sessionId);
      if (!res.ok) {
        setShotError(res.errorMessage || '交回控制权失败');
        return;
      }
      if (auth) {
        const fresh = await apiClient.getBrowserState(auth, sessionId);
        onStateUpdated(fresh);
        await fetchScreenshot(true);
      }
    } catch (err) {
      setShotError(err instanceof Error ? err.message : '交回控制权失败');
    } finally {
      setTakeoverBusy(false);
    }
  };

  const handleCopyUrl = async () => {
    if (!currentUrl) return;
    try {
      await navigator.clipboard.writeText(currentUrl);
      setCopiedUrl(true);
      window.setTimeout(() => setCopiedUrl(false), 1500);
    } catch {
      // ignore clipboard errors
    }
  };

  return (
    <div
      data-testid="browser-panel"
      className="flex h-full flex-col bg-slate-950 text-xs text-slate-200"
    >
      {/* Top toolbar */}
      <div className="flex flex-col gap-2 border-b border-slate-800 px-3 py-2.5">
        <div className="flex items-center justify-between gap-2">
          <div className="flex items-center gap-2">
            <span
              className={`h-2 w-2 rounded-full ${
                takeoverActive
                  ? 'bg-amber-400 animate-pulse'
                  : isRunning
                    ? 'bg-emerald-400 animate-pulse'
                    : 'bg-slate-600'
              }`}
            />
            <span className="font-medium text-slate-100">
              {takeoverActive
                ? `人工接管中（${modeLabel}）`
                : isRunning
                  ? `受管浏览器运行中（${modeLabel}）`
                  : '浏览器空闲'}
            </span>
          </div>

          <div className="flex items-center gap-1">
            <button
              type="button"
              onClick={() => void handleRefreshAll()}
              disabled={!auth || !sessionId || loadingShot}
              title="刷新浏览器状态与截图"
              className="rounded-md p-1.5 text-slate-400 transition-colors hover:bg-slate-800 hover:text-slate-100 disabled:opacity-40"
            >
              <RefreshCw className={`h-3.5 w-3.5 ${loadingShot ? 'animate-spin' : ''}`} />
            </button>
            {isRunning && !takeoverActive && !browserState?.isRemote && browserState?.headless === false && (
              <button
                type="button"
                data-testid="browser-start-takeover-btn"
                onClick={() => void handleStartTakeover()}
                disabled={takeoverBusy || !controlPlaneAvailable}
                title={controlPlaneAvailable ? '唤出桌面浏览器窗口人工操作' : '独立模式未启用控制面'}
                className="inline-flex items-center gap-1 rounded-md border border-amber-500/50 bg-amber-950/40 px-2 py-1 text-[11px] font-medium text-amber-200 transition-colors hover:bg-amber-900/50 disabled:opacity-40"
              >
                <Hand className="h-3 w-3" />
                <span>{takeoverBusy ? '唤出中…' : '人工接管'}</span>
              </button>
            )}
            {isRunning && (
              <button
                type="button"
                onClick={() => void handleCloseBrowser()}
                disabled={closing || !controlPlaneAvailable}
                title={controlPlaneAvailable ? '关闭当前会话浏览器' : '独立模式未启用控制面，无法关闭'}
                className="inline-flex items-center gap-1 rounded-md border border-rose-500/40 bg-rose-950/40 px-2 py-1 text-[11px] font-medium text-rose-200 transition-colors hover:bg-rose-900/50 disabled:opacity-40"
              >
                <Power className="h-3 w-3" />
                <span>{closing ? '关闭中…' : '关闭'}</span>
              </button>
            )}
          </div>
        </div>

        {takeoverActive && (
          <div
            data-testid="browser-takeover-banner"
            className="flex flex-col gap-2 rounded-lg border border-amber-500/50 bg-amber-950/40 px-2.5 py-2 text-[11px] text-amber-100"
          >
            <div className="flex items-start gap-1.5">
              <Hand className="mt-0.5 h-3.5 w-3.5 shrink-0 text-amber-400" />
              <div className="min-w-0 flex-1">
                <div className="font-medium text-amber-200">人工接管进行中（操作完成后请交回控制权）</div>
                {currentOrigin && (
                  <div data-testid="browser-takeover-origin" className="mt-0.5 text-amber-200/90 break-all">
                    当前站点：{currentOrigin}
                  </div>
                )}
                {browserState?.takeoverReason && (
                  <div className="mt-0.5 text-amber-100/90 break-words">
                    接管说明：{browserState.takeoverReason}
                  </div>
                )}
              </div>
            </div>
            <div className="flex justify-end">
              <button
                type="button"
                data-testid="browser-complete-takeover-btn"
                onClick={() => void handleCompleteTakeover()}
                disabled={takeoverBusy || !controlPlaneAvailable}
                className="inline-flex items-center gap-1 rounded-md bg-amber-500 px-2.5 py-1 text-[11px] font-semibold text-slate-950 transition-colors hover:bg-amber-400 disabled:opacity-40"
              >
                <Play className="h-3 w-3" />
                <span>{takeoverBusy ? '交回中…' : '完成并交回 Agent'}</span>
              </button>
            </div>
          </div>
        )}

        {/* Address bar */}
        <div className="flex items-center gap-1.5 rounded-lg border border-slate-800 bg-slate-900/80 px-2.5 py-1.5">
          <Globe className="h-3.5 w-3.5 shrink-0 text-slate-400" />
          <div className="min-w-0 flex-1 truncate font-mono text-[11px] text-slate-200" title={currentUrl || '未打开页面'}>
            {currentUrl || '未打开页面（Agent 调用 browser_navigate 后自动预览）'}
          </div>
          {currentUrl && (
            <button
              type="button"
              onClick={() => void handleCopyUrl()}
              title="复制当前 URL"
              className="shrink-0 rounded p-0.5 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
            >
              {copiedUrl ? <Check className="h-3.5 w-3.5 text-emerald-400" /> : <Copy className="h-3.5 w-3.5" />}
            </button>
          )}
        </div>

        {/* Tabs strip */}
        {tabs.length > 0 && (
          <div className="flex items-center gap-1 overflow-x-auto pb-0.5">
            {tabs.map((tab, idx) => (
              <div
                key={tab.tabId ?? `tab-${idx}`}
                title={`${tab.title || '无标题'}\n${tab.url || ''}`}
                className={`max-w-[160px] shrink-0 truncate rounded-md border px-2 py-0.5 text-[11px] ${
                  tab.active
                    ? 'border-sky-500/50 bg-sky-950/50 text-sky-100'
                    : 'border-slate-800 bg-slate-900/60 text-slate-400'
                }`}
              >
                {tab.title || tab.url || `Tab ${idx + 1}`}
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Main viewport canvas */}
      <div className="relative flex min-h-0 flex-1 flex-col items-center justify-center overflow-auto bg-slate-950/90 p-3">
        {shotError && (
          <div className="mb-2 w-full rounded-lg border border-rose-500/40 bg-rose-950/40 px-2.5 py-1.5 text-[11px] text-rose-200">
            {shotError}
          </div>
        )}

        {screenshotUrl ? (
          <div className="flex w-full flex-col items-center gap-1.5">
            {currentTitle && (
              <div className="w-full truncate text-center text-[11px] text-slate-400" title={currentTitle}>
                {currentTitle}
              </div>
            )}
            <img
              src={screenshotUrl}
              alt={currentTitle || 'Browser preview'}
              data-testid="browser-preview-image"
              className="max-h-full w-full rounded-lg border border-slate-800 object-contain shadow-lg"
            />
          </div>
        ) : isRunning ? (
          <div className="flex flex-col items-center gap-2 text-center text-slate-400">
            <Globe className="h-8 w-8 text-sky-400/70" />
            <p className="text-xs text-slate-300">浏览器会话已就绪，等待页面截图…</p>
          </div>
        ) : (
          <div className="flex max-w-[280px] flex-col items-center gap-2 text-center text-slate-400">
            <Globe className="h-9 w-9 text-slate-600" />
            <div className="font-medium text-slate-300">内置 Web Browser Use 预览区</div>
            <p className="text-[11px] leading-relaxed text-slate-400">
              在对话中要求助手打开网页、测试本地 <code className="rounded bg-slate-800 px-1">localhost</code> 前端页面或执行页面交互时，此处将实时展示浏览器画面、操作轨迹与控制台日志。
            </p>
          </div>
        )}
      </div>

      {/* Bottom drawer: Actions & Console */}
      <div className="flex h-[210px] shrink-0 flex-col border-t border-slate-800 bg-slate-950">
        <div className="flex items-center gap-1 border-b border-slate-800 px-2.5 py-1.5">
          <button
            type="button"
            onClick={() => setDrawerTab('actions')}
            className={`inline-flex items-center gap-1 rounded px-2 py-0.5 text-[11px] font-medium ${
              drawerTab === 'actions'
                ? 'bg-slate-800 text-slate-100'
                : 'text-slate-400 hover:bg-slate-900 hover:text-slate-200'
            }`}
          >
            <Activity className="h-3 w-3" />
            <span>操作轨迹{actions.length > 0 ? ` (${actions.length})` : ''}</span>
          </button>
          <button
            type="button"
            onClick={() => setDrawerTab('console')}
            className={`inline-flex items-center gap-1 rounded px-2 py-0.5 text-[11px] font-medium ${
              drawerTab === 'console'
                ? 'bg-slate-800 text-slate-100'
                : 'text-slate-400 hover:bg-slate-900 hover:text-slate-200'
            }`}
          >
            <Terminal className="h-3 w-3" />
            <span>
              控制台{consoleLogs.length > 0 ? ` (${consoleLogs.length})` : ''}
              {consoleErrorCount > 0 ? ` · ${consoleErrorCount} 错误` : ''}
            </span>
          </button>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto p-2 font-mono text-[11px]">
          {drawerTab === 'actions' ? (
            actions.length === 0 ? (
              <div className="py-4 text-center text-slate-500">暂无浏览器操作记录</div>
            ) : (
              <div className="space-y-1.5">
                {[...actions].reverse().map((act, idx) => (
                  <div
                    key={act.seq ?? idx}
                    className="rounded border border-slate-800/80 bg-slate-900/50 px-2 py-1"
                  >
                    <div className="flex items-center justify-between gap-2">
                      <span className={act.succeeded ? 'text-emerald-300' : 'text-rose-300'}>
                        {act.tool} · {act.action}
                      </span>
                      <span className="text-[10px] text-slate-500">
                        {act.durationMs != null ? `${act.durationMs}ms` : ''}
                      </span>
                    </div>
                    {act.summary && (
                      <div className="mt-0.5 truncate text-slate-300" title={act.summary}>
                        {act.summary}
                      </div>
                    )}
                  </div>
                ))}
              </div>
            )
          ) : consoleLogs.length === 0 ? (
            <div className="py-4 text-center text-slate-500">暂无页面 Console 日志</div>
          ) : (
            <div className="space-y-1">
              {[...consoleLogs].reverse().map((entry, idx) => {
                const isErr = entry.level === 'error' || entry.level === 'exception';
                const isWarn = entry.level === 'warn' || entry.level === 'warning';
                return (
                  <div
                    key={idx}
                    className={`rounded px-2 py-1 ${
                      isErr
                        ? 'bg-rose-950/50 text-rose-200'
                        : isWarn
                          ? 'bg-amber-950/40 text-amber-200'
                          : 'bg-slate-900/50 text-slate-300'
                    }`}
                  >
                    <span className="mr-1.5 uppercase opacity-75">[{entry.level}]</span>
                    <span className="break-all">{entry.text}</span>
                  </div>
                );
              })}
            </div>
          )}
        </div>
      </div>
    </div>
  );
};
