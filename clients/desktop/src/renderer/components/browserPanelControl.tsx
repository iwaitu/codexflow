import { useCallback, useEffect, useRef, useState } from 'react';
import type { BrowserPanelFrame, BrowserPanelRequest, BrowserPanelResponse, ControlPlaneCallResult } from '@shared/ipc';

interface PanelLease {
  sessionId: string;
  id: string;
}

const PANEL_KEYS = new Set(['Tab', 'Enter', 'Escape', 'Backspace', 'Delete', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight']);
const FRAME_INTERVAL_MS = 365;
const MIN_OPERATION_INTERVAL_MS = 91;
const MAX_VIEWPORT_WIDTH = 1920;
const MAX_VIEWPORT_HEIGHT = 1440;

type ViewportSize = Pick<BrowserPanelRequest, 'viewportWidth' | 'viewportHeight'>;
type PanelCommand = Pick<BrowserPanelRequest, 'direction' | 'url' | 'tabId'> & {
  action: 'navigate' | 'tab_new' | 'tab_switch' | 'tab_close';
};

export function useBrowserPanelControl(sessionId: string | null, connectionKey: string | null, enabled: boolean) {
  const [active, setActive] = useState(false);
  const [busy, setBusy] = useState(false);
  const [frame, setFrame] = useState<BrowserPanelFrame | null>(null);
  const [responsive, setResponsive] = useState<boolean | null>(null);
  const [frameMatches, setFrameMatches] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const leaseRef = useRef<PanelLease | null>(null);
  const generation = useRef(0);
  const frameEpoch = useRef(0);
  const viewportElement = useRef<HTMLDivElement>(null);
  const viewportSize = useRef<ViewportSize>({});
  const frameReady = useRef(false);
  const responsiveLease = useRef(false);
  const inputEpoch = useRef(0);
  const queue = useRef<Promise<void>>(Promise.resolve());
  const queued = useRef(0);
  const starting = useRef(false);
  const lastSent = useRef(0);

  const send = useCallback((sid: string, input: BrowserPanelRequest): Promise<ControlPlaneCallResult<BrowserPanelResponse>> =>
    window.codexflow?.controlBrowserPanel?.(sid, input) ??
    Promise.resolve({ ok: false, status: 503, errorMessage: '当前客户端没有连接到侧栏浏览器。' }), []);

  const cancelQueuedInput = useCallback(() => { inputEpoch.current += 1; }, []);
  const invalidateFrame = useCallback(() => {
    frameEpoch.current += 1;
    frameReady.current = false;
    setFrameMatches(false);
    cancelQueuedInput();
  }, [cancelQueuedInput]);

  useEffect(() => {
    const element = viewportElement.current;
    if (!element || typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(([entry]) => {
      if (!entry) return;
      const { width, height } = entry.contentRect;
      if (width < 1 || height < 1) return;
      const scale = Math.min(1, MAX_VIEWPORT_WIDTH / width, MAX_VIEWPORT_HEIGHT / height);
      const viewportWidth = Math.max(1, Math.floor(width * scale));
      const viewportHeight = Math.max(1, Math.floor(height * scale));
      if (viewportSize.current.viewportWidth === viewportWidth && viewportSize.current.viewportHeight === viewportHeight) return;
      viewportSize.current = { viewportWidth, viewportHeight };
      if (responsiveLease.current) invalidateFrame();
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [invalidateFrame]);

  const release = useCallback(() => {
    generation.current += 1;
    cancelQueuedInput();
    frameReady.current = false;
    responsiveLease.current = false;
    starting.current = false;
    const lease = leaseRef.current;
    leaseRef.current = null;
    if (lease) void send(lease.sessionId, { action: 'end', leaseId: lease.id }).catch(() => undefined);
  }, [cancelQueuedInput, send]);

  const stop = useCallback(() => {
    release();
    setActive(false);
    setBusy(false);
    setFrame(null);
    setFrameMatches(false);
    setError(null);
  }, [release]);

  useEffect(() => {
    setActive(false);
    setBusy(false);
    setFrame(null);
    setFrameMatches(false);
    setResponsive(null);
    setError(null);
    return release;
  }, [sessionId, connectionKey, enabled, release]);

  useEffect(() => {
    if (!active) return;
    let disposed = false;
    let timer: number | undefined;
    const lease = leaseRef.current;
    if (!lease) return;

    const refresh = async () => {
      if (disposed || leaseRef.current !== lease) return;
      if (!document.hidden && !starting.current) {
        const epoch = frameEpoch.current;
        try {
          const size = responsiveLease.current ? viewportSize.current : {};
          const res = await send(lease.sessionId, { action: 'frame', leaseId: lease.id, ...size });
          if (disposed || leaseRef.current !== lease) return;
          if (res.ok && res.data?.frame && epoch === frameEpoch.current && !starting.current) {
            frameReady.current = true;
            setFrameMatches(Boolean(size.viewportWidth));
            setFrame(res.data.frame);
          } else if (!res.ok && res.errorCode === 'browser_panel_lease_invalid') {
            stop();
            setError('侧栏操作已结束。可重新进入侧栏操作。');
            return;
          }
        } catch {
          if (!disposed) setError('暂时无法刷新浏览器画面。');
        }
      }
      if (!disposed) timer = window.setTimeout(() => void refresh(), FRAME_INTERVAL_MS);
    };
    void refresh();
    return () => {
      disposed = true;
      window.clearTimeout(timer);
    };
  }, [active, send, stop]);

  const run = async (command?: PanelCommand) => {
    if (!enabled || !sessionId || starting.current) return false;
    const currentGeneration = generation.current;
    starting.current = true;
    setBusy(true);
    setError(null);
    invalidateFrame();
    try {
      let lease = leaseRef.current;
      if (!lease) {
        const res = await send(sessionId, { action: 'start' });
        const leaseId = res.ok ? res.data?.leaseId : null;
        if (currentGeneration !== generation.current) {
          if (leaseId) void send(sessionId, { action: 'end', leaseId }).catch(() => undefined);
          return false;
        }
        if (!leaseId) {
          setError(res.status === 404 && res.errorCode === 'http_404'
            ? '当前后端版本不支持侧栏浏览器，请使用与客户端配套的后端重新启动。'
            : res.errorMessage ?? '无法进入侧栏操作。');
          return false;
        }
        lease = { sessionId, id: leaseId };
        leaseRef.current = lease;
        responsiveLease.current = res.data?.responsiveViewport === true;
        setResponsive(responsiveLease.current);
        setActive(true);
      }

      if (command) {
        if (command.action !== 'navigate' && !responsiveLease.current) {
          setError('当前后端版本不支持在侧栏管理标签页。');
          return false;
        }
        setFrame(null);
        await queue.current;
        const wait = MIN_OPERATION_INTERVAL_MS - (performance.now() - lastSent.current);
        if (wait > 0) await new Promise<void>((resolve) => window.setTimeout(resolve, wait));
        if (currentGeneration !== generation.current) return false;
        lastSent.current = performance.now();
        const res = await send(lease.sessionId, { leaseId: lease.id, ...command });
        if (currentGeneration !== generation.current) return false;
        if (!res.ok) {
          setError(res.errorMessage ?? (command.action === 'navigate' ? '网页导航失败。' : '标签页操作失败。'));
          return false;
        }
      }
      return true;
    } catch {
      if (currentGeneration === generation.current) setError('无法连接受管浏览器。');
      return false;
    } finally {
      if (currentGeneration === generation.current) {
        starting.current = false;
        setBusy(false);
      }
    }
  };

  const input = (request: BrowserPanelRequest) => {
    const lease = leaseRef.current;
    if (!lease || !frameReady.current || starting.current || document.hidden || queued.current >= 24) return;
    const epoch = inputEpoch.current;
    queued.current += 1;
    queue.current = queue.current.then(async () => {
      if (inputEpoch.current !== epoch || leaseRef.current !== lease) return;
      const wait = MIN_OPERATION_INTERVAL_MS - (performance.now() - lastSent.current);
      if (wait > 0) await new Promise<void>((resolve) => window.setTimeout(resolve, wait));
      if (inputEpoch.current !== epoch || leaseRef.current !== lease) return;
      lastSent.current = performance.now();
      const res = await send(lease.sessionId, { ...request, leaseId: lease.id });
      if (leaseRef.current === lease && !res.ok && res.errorCode !== 'browser_panel_stale_view') {
        setError(res.errorMessage ?? '浏览器暂时无法接收输入。');
      }
    }).catch(() => {
      if (leaseRef.current === lease) setError('暂时无法向浏览器发送输入。');
    }).finally(() => { queued.current -= 1; });
  };

  const showing = enabled && active && leaseRef.current?.sessionId === sessionId;
  return { active: showing, busy, frame: showing ? frame : null, frameMatches, responsive, viewportElement,
    error, run, stop, input, cancelQueuedInput };
}

interface BrowserViewportProps {
  frame: BrowserPanelFrame;
  fill: boolean;
  onInput: (request: BrowserPanelRequest) => void;
  onBlur: () => void;
}

export function BrowserPanelViewport({ frame, fill, onInput, onBlur }: BrowserViewportProps) {
  const image = useRef<HTMLImageElement>(null);
  const keyboard = useRef<HTMLTextAreaElement>(null);
  const composing = useRef(false);
  const deferredComposition = useRef<string | null>(null);
  const wheel = useRef<(BrowserPanelRequest & { deltaX: number; deltaY: number }) | null>(null);
  const latest = useRef({ frame, onInput, onBlur });
  latest.current = { frame, onInput, onBlur };
  const viewUrl = `data:image/jpeg;base64,${frame.jpegBase64}`;

  const locate = (clientX: number, clientY: number, renderedFrame: BrowserPanelFrame) => {
    const rect = image.current?.getBoundingClientRect();
    if (!rect?.width || !rect.height) return null;
    const x = (clientX - rect.left) / rect.width;
    const y = (clientY - rect.top) / rect.height;
    if (x < 0 || x >= 1 || y < 0 || y >= 1) return null;
    return { viewId: renderedFrame.viewId, x: x * Number(renderedFrame.cssWidth), y: y * Number(renderedFrame.cssHeight) };
  };

  useEffect(() => {
    const target = image.current;
    if (!target) return;
    let pending: number | undefined;
    const cap = (value: number) => Math.max(-1200, Math.min(1200, value));
    const scroll = (event: WheelEvent) => {
      const position = locate(event.clientX, event.clientY, latest.current.frame);
      if (!position || event.ctrlKey) return;
      event.preventDefault();
      const factor = event.deltaMode === WheelEvent.DOM_DELTA_LINE ? 24 : event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? 600 : 1;
      const prior = wheel.current?.viewId === position.viewId ? wheel.current : null;
      wheel.current = { action: 'wheel', ...position,
        deltaX: cap((prior?.deltaX ?? 0) + event.deltaX * factor),
        deltaY: cap((prior?.deltaY ?? 0) + event.deltaY * factor) };
      if (pending !== undefined) return;
      pending = window.setTimeout(() => {
        pending = undefined;
        if (wheel.current) latest.current.onInput(wheel.current);
        wheel.current = null;
      }, 120);
    };
    target.addEventListener('wheel', scroll, { passive: false });
    return () => {
      target.removeEventListener('wheel', scroll);
      window.clearTimeout(pending);
      wheel.current = null;
    };
  }, []);

  const sendText = (text: string) => {
    if (text) onInput({ action: 'text', viewId: frame.viewId, text: text.slice(0, 1024) });
  };

  return (
    <div className="absolute inset-0 flex items-center justify-center overflow-hidden"
      onBlur={() => {
        composing.current = false;
        deferredComposition.current = null;
        wheel.current = null;
        if (keyboard.current) keyboard.current.value = '';
        onBlur();
      }}>
      <img
        data-testid="browser-live-viewport"
        ref={image}
        src={viewUrl}
        alt={frame.title || '受管网页'}
        draggable={false}
        className={`block select-none ${fill ? 'h-full w-full' : 'h-auto max-h-full w-auto max-w-full'}`}
        onClick={(event) => {
          const position = locate(event.clientX, event.clientY, frame);
          if (!position) return;
          keyboard.current?.focus();
          onInput({ action: 'click', ...position });
        }}
      />
      <textarea
        ref={keyboard}
        aria-label="受管网页键盘输入"
        autoComplete="off"
        className="absolute top-0 left-0 h-px w-px opacity-0"
        onKeyDown={(event) => {
          if (composing.current || event.nativeEvent.isComposing || event.ctrlKey || event.metaKey || event.altKey) return;
          if (!PANEL_KEYS.has(event.key)) return;
          event.preventDefault();
          event.stopPropagation();
          onInput({ action: 'key', viewId: frame.viewId, key: event.key });
        }}
        onCompositionStart={() => { composing.current = true; deferredComposition.current = null; }}
        onCompositionEnd={(event) => {
          composing.current = false;
          deferredComposition.current = event.data;
          sendText(event.data);
          event.currentTarget.value = '';
        }}
        onChange={(event) => {
          if (composing.current) return;
          const value = event.currentTarget.value;
          const duplicate = (event.nativeEvent as InputEvent).inputType === 'insertCompositionText' &&
            value === deferredComposition.current;
          deferredComposition.current = null;
          if (!duplicate) sendText(value);
          event.currentTarget.value = '';
        }}
      />
    </div>
  );
}
