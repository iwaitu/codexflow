import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  Activity,
  Hand,
  Monitor,
  OctagonX,
  Play,
  Power,
  RefreshCw,
} from 'lucide-react';
import type { ComputerSessionStateDto } from '@shared/ipc';
import { apiClient, type ApiAuthContext } from '../lib/apiClient';

interface ComputerPanelProps {
  auth: ApiAuthContext | null;
  sessionId: string | null;
  computerState: ComputerSessionStateDto | null;
  controlPlaneAvailable: boolean;
  onStateUpdated: (next: ComputerSessionStateDto) => void;
  onEmergencyStopped?: () => void;
}

export const ComputerPanel: React.FC<ComputerPanelProps> = ({
  auth,
  sessionId,
  computerState,
  controlPlaneAvailable,
  onStateUpdated,
  onEmergencyStopped,
}) => {
  const [screenshotUrl, setScreenshotUrl] = useState<string | null>(null);
  const [loadingShot, setLoadingShot] = useState(false);
  const [shotError, setShotError] = useState<string | null>(null);
  const [closing, setClosing] = useState(false);
  const [stopping, setStopping] = useState(false);
  const [takeoverBusy, setTakeoverBusy] = useState(false);
  const lastFetchedSeqRef = useRef<number>(-1);
  const lastFetchedSessionRef = useRef<string | null>(null);
  const lastEtagRef = useRef<string | null>(null);
  const activeObjectUrlRef = useRef<string | null>(null);
  const activeFetchAbortRef = useRef<AbortController | null>(null);
  const fetchGenerationRef = useRef<number>(0);
  const mountedRef = useRef<boolean>(true);

  const status = computerState?.status ?? 'idle';
  const emergencyStopped = Boolean(computerState?.emergencyStopped || status === 'emergency_stopped');
  const takeoverPending = Boolean(computerState?.takeoverPending || status === 'takeover');
  const isActive = Boolean(computerState?.active);
  const screenshotSeq = Number(computerState?.screenshotSeq ?? 0);
  const actions = computerState?.recentActions ?? [];
  const pinnedProcess = computerState?.pinnedProcessName ?? null;
  const pinnedTitle = computerState?.pinnedWindowTitle ?? null;

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
      if (!auth || !sessionId || screenshotSeq <= 0) {
        fetchGenerationRef.current += 1;
        activeFetchAbortRef.current?.abort();
        activeFetchAbortRef.current = null;
        revokeCurrentObjectUrl();
        setScreenshotUrl(null);
        lastFetchedSeqRef.current = -1;
        lastFetchedSessionRef.current = sessionId ?? null;
        lastEtagRef.current = null;
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
        const res = await apiClient.getComputerScreenshotBlob(
          auth,
          sessionId,
          controller.signal,
          force ? null : lastEtagRef.current,
        );
        if (
          !mountedRef.current ||
          controller.signal.aborted ||
          fetchGenerationRef.current !== generation
        ) {
          return;
        }
        if (res === null) {
          revokeCurrentObjectUrl();
          setScreenshotUrl(null);
          lastFetchedSeqRef.current = 0;
          lastFetchedSessionRef.current = sessionId;
          lastEtagRef.current = null;
          return;
        }
        if ('notModified' in res && res.notModified) {
          lastFetchedSeqRef.current = screenshotSeq;
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
        lastEtagRef.current = res.etag;
        setScreenshotUrl(nextUrl);
      } catch (err) {
        if (
          !mountedRef.current ||
          controller.signal.aborted ||
          fetchGenerationRef.current !== generation
        ) {
          return;
        }
        setShotError(err instanceof Error ? err.message : '加载桌面回显截图失败');
      } finally {
        if (mountedRef.current && fetchGenerationRef.current === generation) {
          setLoadingShot(false);
        }
        if (activeFetchAbortRef.current === controller) {
          activeFetchAbortRef.current = null;
        }
      }
    },
    [auth, sessionId, screenshotSeq, revokeCurrentObjectUrl],
  );

  useEffect(() => {
    void fetchScreenshot(false);
  }, [fetchScreenshot]);

  const handleRefreshAll = async () => {
    if (!auth || !sessionId) return;
    try {
      const fresh = await apiClient.getComputerState(auth, sessionId);
      onStateUpdated(fresh);
      await fetchScreenshot(true);
    } catch (err) {
      setShotError(err instanceof Error ? err.message : '刷新桌面操控状态失败');
    }
  };

  const handleEmergencyStop = async () => {
    if (!sessionId || !window.codexflow || !controlPlaneAvailable) return;
    setStopping(true);
    setShotError(null);
    try {
      const res = await window.codexflow.emergencyStopComputer({
        sessionId,
        reason: 'panel_button',
      });
      if (!res.ok) {
        setShotError(res.errorMessage || '触发紧急停止失败');
        return;
      }
      if (auth) {
        const fresh = await apiClient.getComputerState(auth, sessionId);
        onStateUpdated(fresh);
      }
      onEmergencyStopped?.();
    } catch (err) {
      setShotError(err instanceof Error ? err.message : '触发紧急停止失败');
    } finally {
      setStopping(false);
    }
  };

  const handleCloseSession = async () => {
    if (!sessionId || !window.codexflow || !controlPlaneAvailable) return;
    setClosing(true);
    setShotError(null);
    try {
      const res = await window.codexflow.closeComputerSession(sessionId);
      if (!res.ok) {
        setShotError(res.errorMessage || '关闭桌面操控会话失败');
        return;
      }
      revokeCurrentObjectUrl();
      setScreenshotUrl(null);
      lastFetchedSeqRef.current = -1;
      lastEtagRef.current = null;
      if (auth) {
        const fresh = await apiClient.getComputerState(auth, sessionId);
        onStateUpdated(fresh);
      }
    } catch (err) {
      setShotError(err instanceof Error ? err.message : '关闭桌面操控会话失败');
    } finally {
      setClosing(false);
    }
  };

  const handleStartTakeover = async () => {
    if (!sessionId || !window.codexflow || !controlPlaneAvailable) return;
    setTakeoverBusy(true);
    setShotError(null);
    try {
      const res = await window.codexflow.startComputerTakeover(
        sessionId,
        '用户在桌面操控面板主动发起人工接管',
      );
      if (!res.ok) {
        setShotError(res.errorMessage || '发起人工接管失败');
        return;
      }
      if (auth) {
        const fresh = await apiClient.getComputerState(auth, sessionId);
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
      const res = await window.codexflow.completeComputerTakeover(sessionId);
      if (!res.ok) {
        setShotError(res.errorMessage || '交回控制权失败');
        return;
      }
      if (auth) {
        const fresh = await apiClient.getComputerState(auth, sessionId);
        onStateUpdated(fresh);
        await fetchScreenshot(true);
      }
    } catch (err) {
      setShotError(err instanceof Error ? err.message : '交回控制权失败');
    } finally {
      setTakeoverBusy(false);
    }
  };

  return (
    <div
      data-testid="computer-panel"
      className="flex h-full flex-col bg-slate-950 text-xs text-slate-200"
    >
      {/* Top toolbar */}
      <div className="flex flex-col gap-2 border-b border-slate-800 px-3 py-2.5">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="flex items-center gap-2">
            <span
              className={`h-2 w-2 rounded-full ${
                emergencyStopped
                  ? 'bg-rose-500'
                  : takeoverPending
                    ? 'bg-amber-400 animate-pulse'
                    : isActive
                      ? 'bg-emerald-400 animate-pulse'
                      : 'bg-slate-600'
              }`}
            />
            <span className="font-medium text-slate-100">
              {emergencyStopped
                ? '已紧急停止'
                : takeoverPending
                  ? '等待人工接管'
                  : isActive
                    ? '桌面操控运行中'
                    : '桌面操控空闲'}
            </span>
          </div>

          <div className="flex flex-wrap items-center gap-1">
            <button
              type="button"
              onClick={() => void handleRefreshAll()}
              disabled={!auth || !sessionId || loadingShot}
              title="刷新桌面状态与截图"
              className="rounded-md p-1.5 text-slate-400 transition-colors hover:bg-slate-800 hover:text-slate-100 disabled:opacity-40"
            >
              <RefreshCw className={`h-3.5 w-3.5 ${loadingShot ? 'animate-spin' : ''}`} />
            </button>

            <button
              type="button"
              data-testid="computer-emergency-stop-btn"
              onClick={() => void handleEmergencyStop()}
              disabled={!sessionId || !controlPlaneAvailable || stopping}
              title={controlPlaneAvailable ? '立即熔断全部桌面键鼠注入并收回控制权' : '独立模式未启用控制面'}
              className="inline-flex items-center gap-1 rounded-md bg-rose-600 px-2.5 py-1 text-[11px] font-semibold text-white transition-colors hover:bg-rose-500 disabled:opacity-40"
            >
              <OctagonX className="h-3.5 w-3.5" />
              <span>{stopping ? '停止中…' : '紧急停止'}</span>
            </button>

            {isActive && !takeoverPending && (
              <button
                type="button"
                data-testid="computer-start-takeover-btn"
                onClick={() => void handleStartTakeover()}
                disabled={takeoverBusy || !controlPlaneAvailable}
                title="暂停 AI 操控并交由人工处理"
                className="inline-flex items-center gap-1 rounded-md border border-amber-500/50 bg-amber-950/40 px-2 py-1 text-[11px] font-medium text-amber-200 transition-colors hover:bg-amber-900/50 disabled:opacity-40"
              >
                <Hand className="h-3 w-3" />
                <span>{takeoverBusy ? '切换中…' : '人工接管'}</span>
              </button>
            )}

            {(isActive || takeoverPending || emergencyStopped) && (
              <button
                type="button"
                onClick={() => void handleCloseSession()}
                disabled={closing || !controlPlaneAvailable}
                title="结束并释放当前会话的桌面操控租约锁"
                className="inline-flex items-center gap-1 rounded-md border border-slate-700 bg-slate-900 px-2 py-1 text-[11px] font-medium text-slate-200 transition-colors hover:bg-slate-800 disabled:opacity-40"
              >
                <Power className="h-3 w-3" />
                <span>{closing ? '释放中…' : '结束'}</span>
              </button>
            )}
          </div>
        </div>

        {takeoverPending && (
          <div
            data-testid="computer-takeover-banner"
            className="flex flex-col gap-2 rounded-lg border border-amber-500/50 bg-amber-950/40 px-2.5 py-2 text-[11px] text-amber-100"
          >
            <div className="flex items-start gap-1.5">
              <Hand className="mt-0.5 h-3.5 w-3.5 shrink-0 text-amber-400" />
              <div className="min-w-0 flex-1">
                <div className="font-medium text-amber-200">人工接管进行中（例如登录、验证码或人工确认）</div>
                {computerState?.takeoverReason && (
                  <div className="mt-0.5 text-amber-100/90 break-words">
                    接管原因：{computerState.takeoverReason}
                  </div>
                )}
              </div>
            </div>
            <div className="flex justify-end">
              <button
                type="button"
                data-testid="computer-complete-takeover-btn"
                onClick={() => void handleCompleteTakeover()}
                disabled={takeoverBusy || !controlPlaneAvailable}
                className="inline-flex items-center gap-1 rounded-md bg-amber-500 px-2.5 py-1 text-[11px] font-semibold text-slate-950 transition-colors hover:bg-amber-400 disabled:opacity-40"
              >
                <Play className="h-3 w-3" />
                <span>{takeoverBusy ? '交回中…' : '我已完成操作，继续'}</span>
              </button>
            </div>
          </div>
        )}

        {/* Target lock summary bar */}
        <div className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-slate-800 bg-slate-900/80 px-2.5 py-1.5 text-[11px]">
          <div className="flex min-w-0 flex-1 items-center gap-1.5">
            <Monitor className="h-3.5 w-3.5 shrink-0 text-slate-400" />
            <span className="truncate font-mono text-slate-200" title={pinnedTitle || pinnedProcess || '未锁定目标窗口'}>
              {pinnedProcess ? `${pinnedProcess}${pinnedTitle ? ` · ${pinnedTitle}` : ''}` : '未锁定目标窗口（支持全局屏幕观测或 pin 目标窗口）'}
            </span>
          </div>
          {computerState && (
            <span className="shrink-0 rounded bg-slate-800 px-1.5 py-0.5 font-mono text-[10px] text-slate-300">
              {computerState.coordinateMode}
              {computerState.imageWidth && computerState.imageHeight
                ? ` · ${computerState.imageWidth}×${computerState.imageHeight}`
                : ''}
            </span>
          )}
        </div>
      </div>

      {/* Main viewport */}
      <div className="relative flex min-h-0 flex-1 flex-col items-center justify-center overflow-auto bg-slate-950/90 p-3">
        {shotError && (
          <div className="mb-2 w-full rounded-lg border border-rose-500/40 bg-rose-950/40 px-2.5 py-1.5 text-[11px] text-rose-200">
            {shotError}
          </div>
        )}

        {screenshotUrl ? (
          <div className="flex w-full flex-col items-center gap-1.5">
            {computerState?.lastActionSummary && (
              <div
                className="w-full truncate text-center text-[11px] text-slate-400"
                title={computerState.lastActionSummary}
              >
                {computerState.lastActionSummary}
              </div>
            )}
            <img
              src={screenshotUrl}
              alt={pinnedTitle || '桌面回显画面'}
              data-testid="computer-preview-image"
              className="max-h-full w-full rounded-lg border border-slate-800 object-contain shadow-lg"
            />
          </div>
        ) : (
          <div className="flex max-w-[300px] flex-col items-center gap-2 text-center text-slate-400">
            <Monitor className="h-9 w-9 text-slate-600" />
            <div className="font-medium text-slate-300">本机桌面视觉操控实时回显区</div>
            <p className="text-[11px] leading-relaxed text-slate-400">
              在日常模式或开发模式下要求助手操作桌面应用或验收桌面 GUI 产物时，此处将展示带坐标标尺与红圈落点的最新回显截图、锁定窗口及操作审计轨迹。
            </p>
          </div>
        )}
      </div>

      {/* Bottom drawer: Recent Actions */}
      <div className="flex h-[200px] shrink-0 flex-col border-t border-slate-800 bg-slate-950">
        <div className="flex items-center justify-between border-b border-slate-800 px-2.5 py-1.5">
          <div className="inline-flex items-center gap-1 text-[11px] font-medium text-slate-200">
            <Activity className="h-3.5 w-3.5 text-rose-400" />
            <span>桌面动作审计轨迹{actions.length > 0 ? ` (${actions.length})` : ''}</span>
          </div>
          <span className="text-[10px] text-slate-500">支持物理 Esc×2 或全局热键急停</span>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto p-2 font-mono text-[11px]">
          {actions.length === 0 ? (
            <div className="py-4 text-center text-slate-500">暂无桌面操控记录</div>
          ) : (
            <div className="space-y-1.5">
              {[...actions].reverse().map((act, idx) => (
                <div
                  key={`${act.seq ?? idx}`}
                  className="rounded border border-slate-800/80 bg-slate-900/50 px-2 py-1"
                >
                  <div className="flex items-center justify-between gap-2">
                    <span className={act.succeeded ? 'text-emerald-300' : 'text-rose-300'}>
                      {act.tool} · {act.action}
                      {act.targetX != null && act.targetY != null ? ` (${act.targetX}, ${act.targetY})` : ''}
                    </span>
                    <span className="text-[10px] text-slate-500">
                      {act.durationMs != null ? `${act.durationMs}ms` : ''}
                    </span>
                  </div>
                  {(act.targetProcessName || act.targetWindowTitle) && (
                    <div className="mt-0.5 truncate text-[10px] text-slate-400">
                      {[act.targetProcessName, act.targetWindowTitle].filter(Boolean).join(' · ')}
                    </div>
                  )}
                  {act.summary && (
                    <div className="mt-0.5 truncate text-slate-300" title={act.summary}>
                      {act.summary}
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
};
