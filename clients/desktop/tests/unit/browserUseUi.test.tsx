import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { BrowserApprovalCard } from '../../src/renderer/components/BrowserApprovalCard';
import { BrowserPanel } from '../../src/renderer/components/BrowserPanel';
import { SettingsModal } from '../../src/renderer/components/SettingsModal';
import { useAppStore } from '../../src/renderer/stores/useAppStore';
import type { DesktopBridge } from '../../src/shared/ipc';

describe('Web Browser Use desktop UI & store', () => {
  beforeEach(() => {
    useAppStore.getState().setActiveSession('s-browser-1');
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it('reduces browser SSE events in useAppStore and clears approvals on resolution', () => {
    useAppStore.getState().applySseEvent({
      id: 1,
      event: 'browser_state_updated',
      data: {
        sessionId: 's-browser-1',
        status: 'running',
        headless: true,
        profileMode: 'Ephemeral',
        url: 'http://localhost:5173/',
        title: 'Dev App',
        screenshotSeq: 2,
        consoleErrorCount: 1,
        tabs: [{ tabId: 't1', url: 'http://localhost:5173/', title: 'Dev App', active: true }],
        recentActions: [],
        recentConsole: [],
      },
    } as never);

    expect(useAppStore.getState().browserState?.url).toBe('http://localhost:5173/');
    expect(useAppStore.getState().browserState?.status).toBe('running');

    useAppStore.getState().applySseEvent({
      id: 2,
      event: 'browser_approval_requested',
      data: {
        approvalId: 'brapr-123',
        requestDigest: 'abc123',
        sessionId: 's-browser-1',
        tool: 'browser_act',
        action: 'click',
        targetUrl: 'https://shop.example/checkout',
        targetOrigin: 'https://shop.example',
        targetDescription: 'click [e5] button "Pay Now"',
        riskReason: 'sensitive submit',
        expressionOrPath: null,
        expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
        allowedDecisions: ['allow_once', 'deny'],
        controlPlaneAvailable: true,
        selfApprovalProtection: true,
      },
    } as never);

    expect(useAppStore.getState().pendingBrowserApprovals).toHaveLength(1);

    useAppStore.getState().applySseEvent({
      id: 3,
      event: 'browser_approval_resolved',
      data: {
        approvalId: 'brapr-123',
        sessionId: 's-browser-1',
        decision: 'allow_once',
        resolvedAtUtc: new Date().toISOString(),
      },
    } as never);

    expect(useAppStore.getState().pendingBrowserApprovals).toHaveLength(0);
  });

  it('fetches authenticated browser screenshot via Bearer fetch and revokes object URL on unmount', async () => {
    const createUrlSpy = vi.fn().mockReturnValue('blob:mock-screenshot-1');
    const revokeUrlSpy = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, writable: true, value: createUrlSpy });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, writable: true, value: revokeUrlSpy });
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(new Uint8Array([0xff, 0xd8, 0xff]), {
        status: 200,
        headers: { 'Content-Type': 'image/jpeg', 'X-Screenshot-Seq': '3' },
      }),
    );

    const { unmount } = render(
      <BrowserPanel
        auth={{ baseUrl: 'http://127.0.0.1:32586', accessToken: 'tok-secret' }}
        sessionId="s-browser-1"
        controlPlaneAvailable
        onStateUpdated={vi.fn()}
        browserState={{
          sessionId: 's-browser-1',
          status: 'running',
          headless: true,
          profileMode: 'Ephemeral',
          isRemote: false,
          url: 'http://localhost:5173/dashboard',
          title: 'Dashboard',
          screenshotSeq: 3,
          consoleErrorCount: 0,
          tabs: [{ tabId: 't1', url: 'http://localhost:5173/dashboard', title: 'Dashboard', active: true }],
          recentActions: [],
          recentConsole: [],
          takeoverActive: false,
        }}
      />,
    );

    await waitFor(() => {
      const img = screen.getByTestId('browser-preview-image') as HTMLImageElement;
      expect(img.src).toBe('blob:mock-screenshot-1');
    });

    expect(fetchSpy).toHaveBeenCalledWith(
      'http://127.0.0.1:32586/api/gateway/session/s-browser-1/browser/screenshot',
      expect.objectContaining({
        headers: { Authorization: 'Bearer tok-secret' },
      }),
    );
    expect(createUrlSpy).toHaveBeenCalledTimes(1);

    unmount();
    expect(revokeUrlSpy).toHaveBeenCalledWith('blob:mock-screenshot-1');
  });

  it('submits browser approval decision through existing approveToolExecution bridge', async () => {
    const approveSpy = vi.fn().mockResolvedValue({ ok: true, status: 200 });
    Object.defineProperty(window, 'codexflow', {
      configurable: true,
      writable: true,
      value: { approveToolExecution: approveSpy } as unknown as DesktopBridge,
    });
    const onResolved = vi.fn();

    render(
      <BrowserApprovalCard
        controlPlaneAvailable
        onResolved={onResolved}
        approval={{
          approvalId: 'brapr-999',
          requestDigest: 'digest-xyz',
          sessionId: 's-browser-1',
          tool: 'browser_navigate',
          action: 'loopback_port_access',
          targetUrl: 'http://127.0.0.1:9229/',
          targetOrigin: 'http://127.0.0.1:9229',
          targetDescription: '允许访问开发端口 9229',
          riskReason: '非默认开发白名单端口',
          expressionOrPath: 'http://127.0.0.1:9229/',
          expiresAtUtc: new Date(Date.now() + 120_000).toISOString(),
          allowedDecisions: ['allow_once', 'allow_session', 'deny'],
          controlPlaneAvailable: true,
          selfApprovalProtection: true,
        }}
      />,
    );

    fireEvent.click(screen.getByText('本会话允许'));

    await waitFor(() => {
      expect(approveSpy).toHaveBeenCalledWith({
        approvalId: 'brapr-999',
        requestDigest: 'digest-xyz',
        decision: 'allow_session',
        note: undefined,
      });
      expect(onResolved).toHaveBeenCalledWith('brapr-999');
    });
  });

  it('disables buttons when approval is expired, renders full targetUrl/expression, and auto-clears on 409 conflict', async () => {
    const approveSpy = vi.fn().mockResolvedValue({
      ok: false,
      status: 409,
      errorMessage: 'Browser approval request has expired.',
    });
    Object.defineProperty(window, 'codexflow', {
      configurable: true,
      writable: true,
      value: { approveToolExecution: approveSpy } as unknown as DesktopBridge,
    });
    const onResolved = vi.fn();

    const { rerender } = render(
      <BrowserApprovalCard
        controlPlaneAvailable
        onResolved={onResolved}
        approval={{
          approvalId: 'brapr-exp',
          requestDigest: 'digest-exp',
          sessionId: 's-browser-1',
          tool: 'browser_act',
          action: 'click',
          targetUrl: 'https://pay.example/checkout?amount=99',
          targetOrigin: 'https://pay.example',
          targetDescription: 'click Pay',
          riskReason: 'sensitive payment submit',
          expressionOrPath: 'action=click ref=e7',
          expiresAtUtc: new Date(Date.now() - 5_000).toISOString(),
          allowedDecisions: ['allow_once', 'deny'],
          controlPlaneAvailable: true,
          selfApprovalProtection: true,
        }}
      />,
    );

    expect(screen.getByTestId('browser-approval-target-url').textContent).toBe(
      'https://pay.example/checkout?amount=99',
    );
    expect(screen.getByTestId('browser-approval-expression').textContent).toBe('action=click ref=e7');
    expect((screen.getByText('允许一次').closest('button') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByText('拒绝').closest('button') as HTMLButtonElement).disabled).toBe(true);

    // Re-render with active expiry and verify 409 auto-dismisses the card.
    rerender(
      <BrowserApprovalCard
        controlPlaneAvailable
        onResolved={onResolved}
        approval={{
          approvalId: 'brapr-409',
          requestDigest: 'digest-409',
          sessionId: 's-browser-1',
          tool: 'browser_act',
          action: 'click',
          targetUrl: 'https://pay.example/checkout?amount=99',
          targetOrigin: 'https://pay.example',
          targetDescription: 'click Pay',
          riskReason: 'sensitive payment submit',
          expressionOrPath: 'action=click ref=e7',
          expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
          allowedDecisions: ['allow_once', 'deny'],
          controlPlaneAvailable: true,
          selfApprovalProtection: true,
        }}
      />,
    );

    fireEvent.click(screen.getByText('允许一次'));
    await waitFor(() => {
      expect(onResolved).toHaveBeenCalledWith('brapr-409');
    });
  });

  it('aborts in-flight screenshot fetch on unmount and never creates leaked object URLs', async () => {
    const createUrlSpy = vi.fn().mockReturnValue('blob:should-not-be-created');
    const revokeUrlSpy = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, writable: true, value: createUrlSpy });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, writable: true, value: revokeUrlSpy });

    let capturedSignal: AbortSignal | undefined;
    let resolveFetch: ((res: Response) => void) | undefined;
    vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => {
      capturedSignal = init?.signal ?? undefined;
      return new Promise<Response>((resolve) => {
        resolveFetch = resolve;
      });
    });

    const { unmount } = render(
      <BrowserPanel
        auth={{ baseUrl: 'http://127.0.0.1:32586', accessToken: 'tok-secret' }}
        sessionId="s-browser-1"
        controlPlaneAvailable
        onStateUpdated={vi.fn()}
        browserState={{
          sessionId: 's-browser-1',
          status: 'running',
          headless: true,
          profileMode: 'Ephemeral',
          isRemote: false,
          url: 'http://localhost:5173/',
          title: 'Slow Shot',
          screenshotSeq: 5,
          consoleErrorCount: 0,
          tabs: [],
          recentActions: [],
          recentConsole: [],
          takeoverActive: false,
        }}
      />,
    );

    await waitFor(() => {
      expect(capturedSignal).toBeDefined();
    });

    unmount();
    expect(capturedSignal?.aborted).toBe(true);

    resolveFetch?.(
      new Response(new Uint8Array([0xff, 0xd8, 0xff]), {
        status: 200,
        headers: { 'Content-Type': 'image/jpeg', 'X-Screenshot-Seq': '5' },
      }),
    );
    await Promise.resolve();
    await Promise.resolve();

    expect(createUrlSpy).not.toHaveBeenCalled();
  });

  it('ignores older browser_state_updated events and deduplicates repeated browser_approval_requested events', () => {
    useAppStore.getState().applySseEvent({
      id: 10,
      event: 'browser_state_updated',
      data: {
        sessionId: 's-browser-1',
        status: 'running',
        url: 'https://newer.example/',
        screenshotSeq: 4,
        updatedAtUtc: '2026-11-20T12:00:10.000Z',
        tabs: [],
        recentActions: [],
        recentConsole: [],
      },
    } as never);

    useAppStore.getState().applySseEvent({
      id: 11,
      event: 'browser_state_updated',
      data: {
        sessionId: 's-browser-1',
        status: 'running',
        url: 'https://older.example/',
        screenshotSeq: 2,
        updatedAtUtc: '2026-11-20T12:00:05.000Z',
        tabs: [],
        recentActions: [],
        recentConsole: [],
      },
    } as never);

    expect(useAppStore.getState().browserState?.url).toBe('https://newer.example/');

    const approvalPayload = {
      approvalId: 'brapr-dup',
      requestDigest: 'dig-dup',
      sessionId: 's-browser-1',
      tool: 'browser_act',
      action: 'click',
      targetUrl: 'https://shop.example/pay',
      targetOrigin: 'https://shop.example',
      targetDescription: 'click Pay',
      expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
      allowedDecisions: ['allow_once', 'deny'],
    };

    useAppStore.getState().applySseEvent({ id: 12, event: 'browser_approval_requested', data: approvalPayload } as never);
    useAppStore.getState().applySseEvent({ id: 13, event: 'browser_approval_requested', data: approvalPayload } as never);
    expect(useAppStore.getState().pendingBrowserApprovals).toHaveLength(1);
  });

  it('supports starting and completing human takeover from BrowserPanel in headed mode', async () => {
    const startTakeover = vi.fn().mockResolvedValue({ ok: true, status: 200, data: { active: true, sessionId: 's-browser-1' } });
    const completeTakeover = vi.fn().mockResolvedValue({ ok: true, status: 200, data: { active: false, sessionId: 's-browser-1' } });
    Object.defineProperty(window, 'codexflow', {
      configurable: true,
      writable: true,
      value: {
        startBrowserTakeover: startTakeover,
        completeBrowserTakeover: completeTakeover,
      } as unknown as DesktopBridge,
    });
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(
        JSON.stringify({
          sessionId: 's-browser-1',
          status: 'takeover',
          headless: false,
          profileMode: 'Persistent',
          isRemote: false,
          url: 'https://login.example/',
          title: 'Sign In',
          screenshotSeq: 0,
          consoleErrorCount: 0,
          tabs: [],
          recentActions: [],
          recentConsole: [],
          takeoverActive: true,
          takeoverReason: '请完成登录验证',
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    );

    const onStateUpdated = vi.fn();
    const { rerender } = render(
      <BrowserPanel
        auth={{ baseUrl: 'http://127.0.0.1:32586', accessToken: 'tok' }}
        sessionId="s-browser-1"
        controlPlaneAvailable
        onStateUpdated={onStateUpdated}
        browserState={{
          sessionId: 's-browser-1',
          status: 'running',
          headless: false,
          profileMode: 'Persistent',
          isRemote: false,
          url: 'https://login.example/',
          title: 'Sign In',
          screenshotSeq: 0,
          consoleErrorCount: 0,
          tabs: [],
          recentActions: [],
          recentConsole: [],
          takeoverActive: false,
        }}
      />,
    );

    fireEvent.click(screen.getByTestId('browser-start-takeover-btn'));
    await waitFor(() => {
      expect(startTakeover).toHaveBeenCalledWith('s-browser-1', expect.any(String));
    });

    rerender(
      <BrowserPanel
        auth={{ baseUrl: 'http://127.0.0.1:32586', accessToken: 'tok' }}
        sessionId="s-browser-1"
        controlPlaneAvailable
        onStateUpdated={onStateUpdated}
        browserState={{
          sessionId: 's-browser-1',
          status: 'takeover',
          headless: false,
          profileMode: 'Persistent',
          isRemote: false,
          url: 'https://login.example/',
          title: 'Sign In',
          screenshotSeq: 0,
          consoleErrorCount: 0,
          tabs: [],
          recentActions: [],
          recentConsole: [],
          takeoverActive: true,
          takeoverReason: '请完成登录验证',
        }}
      />,
    );

    expect(screen.getByTestId('browser-takeover-banner')).toBeTruthy();
    expect(screen.getByTestId('browser-takeover-origin').textContent).toContain('https://login.example');
    expect(screen.getByText('接管说明：请完成登录验证')).toBeTruthy();

    fireEvent.click(screen.getByTestId('browser-complete-takeover-btn'));
    await waitFor(() => {
      expect(completeTakeover).toHaveBeenCalledWith('s-browser-1');
    });
  });

  it('renders preformatted script preview without allow_session for browser_evaluate approvals', () => {
    render(
      <BrowserApprovalCard
        controlPlaneAvailable
        onResolved={vi.fn()}
        approval={{
          approvalId: 'brapr-eval-1',
          requestDigest: 'dig-eval-1',
          sessionId: 's-browser-1',
          tool: 'browser_evaluate',
          action: 'evaluate',
          targetUrl: 'https://app.example/dashboard',
          targetOrigin: 'https://app.example',
          targetDescription: '在页面主上下文中执行 JavaScript 表达式',
          riskReason: '必须单次人工确认（allow_once）',
          expressionOrPath: '(() => ({ tokenPresent: Boolean(window.__STATE__) }))()',
          expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
          allowedDecisions: ['allow_once', 'deny'],
          controlPlaneAvailable: true,
          selfApprovalProtection: true,
        }}
      />,
    );

    const codePre = screen.getByTestId('browser-approval-expression');
    expect(codePre.tagName.toLowerCase()).toBe('pre');
    expect(codePre.textContent).toContain('window.__STATE__');
    expect(screen.getByText('允许一次')).toBeTruthy();
    expect(screen.queryByText('本会话允许')).toBeNull();
  });

  it('hides human takeover button and shows remote CDP label when isRemote is true', () => {
    render(
      <BrowserPanel
        auth={{ baseUrl: 'http://127.0.0.1:32586', accessToken: 'tok' }}
        sessionId="s-browser-1"
        controlPlaneAvailable
        onStateUpdated={vi.fn()}
        browserState={{
          sessionId: 's-browser-1',
          status: 'running',
          headless: false,
          profileMode: 'Ephemeral',
          isRemote: true,
          url: 'https://grid.example/',
          title: 'Remote Session',
          screenshotSeq: 0,
          consoleErrorCount: 0,
          tabs: [],
          recentActions: [],
          recentConsole: [],
          takeoverActive: false,
        }}
      />,
    );

    expect(screen.getByText(/远端 CDP/)).toBeTruthy();
    expect(screen.queryByTestId('browser-start-takeover-btn')).toBeNull();
  });

  it('preserves masked remoteCdpWsUrl until edited and saves domain allow/block lists in SettingsModal', async () => {
    const updateBrowserSettings = vi.fn().mockResolvedValue({ ok: true, status: 200 });
    Object.defineProperty(window, 'codexflow', {
      configurable: true,
      writable: true,
      value: {
        updateBrowserSettings,
      } as unknown as DesktopBridge,
    });

    const onUpdated = vi.fn();
    render(
      <SettingsModal
        open
        initialTab="browser"
        systemStatus={null}
        executionSettings={null}
        modelSettings={null}
        controlPlaneAvailable
        onClose={vi.fn()}
        onUpdated={onUpdated}
        browserSettings={{
          enabled: true,
          executablePath: null,
          headless: true,
          profileMode: 'Ephemeral',
          interactionApprovalPolicy: 'HighRiskOnly',
          allowLocalhostInDailyMode: false,
          allowControlledDownloads: false,
          developmentAllowedLoopbackPorts: [5173],
          allowedDomains: ['example.com'],
          blockedDomains: ['ads.example.com'],
          idleTimeoutMinutes: 15,
          maxConcurrentSessions: 3,
          remoteCdpWsUrl: 'wss://grid.example.com/devtools/browser/abc?***',
          isRemoteCdpConfigured: true,
          detectedBrowser: {
            available: true,
            browserName: 'Chrome',
            executablePath: '/usr/bin/google-chrome',
            version: '131.0.0.0',
            source: 'auto',
          },
        }}
      />,
    );

    fireEvent.change(screen.getByTestId('browser-allowed-domains-input'), {
      target: { value: 'example.com\n*.docs.example.com' },
    });
    fireEvent.change(screen.getByTestId('browser-blocked-domains-input'), {
      target: { value: 'tracker.example.com' },
    });

    // Save without touching remoteCdpWsUrl -> undefined preserves masked secret on backend.
    fireEvent.click(screen.getByText('保存浏览器设置'));
    await waitFor(() => {
      expect(updateBrowserSettings).toHaveBeenCalledTimes(1);
    });
    expect(updateBrowserSettings.mock.calls[0]?.[0]).toMatchObject({
      remoteCdpWsUrl: undefined,
      allowedDomains: ['example.com', '*.docs.example.com'],
      blockedDomains: ['tracker.example.com'],
    });

    // Click clear button -> explicitly sends empty string to clear remote endpoint.
    fireEvent.click(screen.getByTestId('browser-clear-remote-cdp-btn'));
    fireEvent.click(screen.getByText('保存浏览器设置'));
    await waitFor(() => {
      expect(updateBrowserSettings).toHaveBeenCalledTimes(2);
    });
    expect(updateBrowserSettings.mock.calls[1]?.[0]).toMatchObject({
      remoteCdpWsUrl: '',
    });
  });
});
