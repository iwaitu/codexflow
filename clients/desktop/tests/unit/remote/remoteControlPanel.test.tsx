import React from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { DesktopBridge, RemoteControlBridge, RemoteStatusSnapshot } from '@shared/ipc';
import { RemoteControlPanel } from '../../../src/renderer/components/RemoteControlPanel';
import { encodeQrMatrix, qrMatrixToSvgPath } from '../../../src/renderer/lib/qrSvg';

function makeSnapshot(overrides: Partial<RemoteStatusSnapshot> = {}): RemoteStatusSnapshot {
  return {
    config: {
      portalUrl: 'https://portal.codexflow.example',
      account: null,
      device: null,
      bound: false,
      hasDeviceToken: false,
      manualRemotePaused: false,
      watermarks: { accountRevocationSeq: 0, deviceRevocationSeq: 0 },
      pairings: [],
      proxy: { mode: 'system', customUrl: '', username: '', hasPassword: false },
    },
    dial: {
      state: 'stopped',
      dialGeneration: 0,
      retryAttempt: 0,
      nextRetryAtUtc: null,
      lastError: null,
      resolvedProxyDescription: 'direct',
    },
    connEpoch: null,
    activeEpochExpiresAtUnix: null,
    lastPongAtUtc: null,
    pendingPairRequests: [],
    activeStreamCount: 0,
    safeStorageAvailable: true,
    lastHostEventId: 0,
    ...overrides,
  };
}

function installMockRemoteWindow(remote: RemoteControlBridge): void {
  const bridge = {
    pickFolder: vi.fn().mockResolvedValue(null),
    remote,
  } as unknown as DesktopBridge;

  Object.defineProperty(window, 'codexflow', {
    configurable: true,
    writable: true,
    value: bridge,
  });
}

describe('QR SVG Encoder & RemoteControlPanel UI', () => {
  it('encodes short and realistic pairing URIs into valid QR matrices with 7x7 finder patterns', () => {
    const small = encodeQrMatrix('HELLO WORLD');
    expect(small.version).toBe(1);
    expect(small.size).toBe(21);
    expect(qrMatrixToSvgPath(small)).toContain('M0,0');

    // Top-left 7x7 finder corners & center must be dark
    expect(small.modules[0]).toBe(1);
    expect(small.modules[6]).toBe(1);
    expect(small.modules[3 * 21 + 3]).toBe(1);
    expect(small.modules[1 * 21 + 1]).toBe(0);

    const uri =
      'codexflow-pair://v1?portal=https%3A%2F%2Fportal.codexflow.example&deviceId=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee&ticketId=11111111-2222-3333-4444-555555555555&token=sampleBootstrapTokenValue1234567890&bv=1&fp=' +
      'a'.repeat(64);
    const large = encodeQrMatrix(uri);
    expect(large.version).toBeGreaterThanOrEqual(7);
    expect(large.size).toBe(17 + 4 * large.version);
    expect(qrMatrixToSvgPath(large).length).toBeGreaterThan(200);
  });

  it('supports OTP bind, cross-account switch confirmation, QR ticket SVG, 6-digit SAS approval, pause toggle, and zero billing copy', async () => {
    const listenerHolder: { current: ((snap: RemoteStatusSnapshot) => void) | null } = {
      current: null,
    };
    const unboundSnap = makeSnapshot();
    const boundSnap = makeSnapshot({
      config: {
        ...unboundSnap.config,
        bound: true,
        hasDeviceToken: true,
        account: {
          userId: 'u-2',
          userIdHash: 'a'.repeat(32),
          accountEmail: 'alice@example.com',
        },
        device: {
          deviceId: 'dev-1',
          deviceName: 'Alice Workstation',
          hostInstanceId: 'inst-1',
          bindingVersion: 2,
          dataDirHash: 'd'.repeat(64),
          desktopKeyId: 'k'.repeat(32),
          desktopSpkiSha256: 'e'.repeat(64),
        },
        pairings: [],
      },
      dial: {
        state: 'connected',
        dialGeneration: 1,
        retryAttempt: 0,
        nextRetryAtUtc: null,
        lastError: null,
        resolvedProxyDescription: 'direct',
      },
      connEpoch: 3,
      pendingPairRequests: [
        {
          pairingId: 'pair-99',
          ticketId: 't-99',
          mobileId: 'mob-99',
          platform: 'ios',
          deviceName: 'Alice iPhone',
          sas6: '784231',
          expiresAtUnix: Math.floor(Date.now() / 1000) + 120,
          receivedAtUtc: new Date().toISOString(),
        },
      ],
    });

    const remoteMock: RemoteControlBridge = {
      getStatus: vi.fn().mockResolvedValue({ ok: true, data: unboundSnap }),
      onStatusChanged: vi.fn((cb: (snap: RemoteStatusSnapshot) => void) => {
        listenerHolder.current = cb;
        return () => {
          listenerHolder.current = null;
        };
      }),
      setPortalUrl: vi.fn().mockResolvedValue({ ok: true, data: unboundSnap }),
      sendBindOtp: vi.fn().mockResolvedValue({ ok: true, data: { message: 'OTP sent' } }),
      verifyAndBind: vi
        .fn()
        .mockResolvedValueOnce({
          ok: true,
          data: {
            status: 'requires_account_switch_confirmation',
            previousAccountEmail: 'old@example.com',
            nextUserId: 'u-2',
          },
        })
        .mockResolvedValueOnce({
          ok: true,
          data: {
            status: 'bound',
            snapshot: boundSnap,
          },
        }),
      unbind: vi.fn().mockResolvedValue({ ok: true, data: unboundSnap }),
      setManualPaused: vi.fn().mockResolvedValue({
        ok: true,
        data: {
          ...boundSnap,
          config: { ...boundSnap.config, manualRemotePaused: true },
          dial: { ...boundSnap.dial, state: 'paused' },
        },
      }),
      setProxySettings: vi.fn().mockResolvedValue({ ok: true, data: boundSnap }),
      issueQrTicket: vi.fn().mockResolvedValue({
        ok: true,
        data: {
          ticketId: 'ticket-abc',
          qrBootstrapToken: 'qr-tok-xyz',
          bindingVersion: 2,
          expiresAt: new Date(Date.now() + 120_000).toISOString(),
          qrUri:
            'codexflow-pair://v1?portal=https%3A%2F%2Fportal.codexflow.example&deviceId=dev-1&ticketId=ticket-abc&token=qr-tok-xyz&bv=2&fp=' +
            'e'.repeat(64),
        },
      }),
      respondPairRequest: vi.fn().mockResolvedValue({
        ok: true,
        data: { ...boundSnap, pendingPairRequests: [] },
      }),
      revokePairing: vi.fn().mockResolvedValue({ ok: true, data: boundSnap }),
      listWorkspaceAuthorizations: vi.fn().mockResolvedValue({ ok: true, data: [] }),
      setWorkspaceAuthorization: vi.fn().mockResolvedValue({
        ok: true,
        data: { workspaceRealPath: 'D:/ext', authorized: true },
      }),
    };

    installMockRemoteWindow(remoteMock);

    const { container } = render(<RemoteControlPanel active={true} controlPlaneAvailable={true} />);

    await waitFor(() => {
      expect(screen.getByTestId('remote-bind-section')).toBeInTheDocument();
    });

    fireEvent.change(screen.getByPlaceholderText('you@example.com'), {
      target: { value: 'alice@example.com' },
    });
    fireEvent.click(screen.getByTestId('remote-send-otp-btn'));
    await waitFor(() => {
      expect(remoteMock.sendBindOtp).toHaveBeenCalledWith({
        email: 'alice@example.com',
        deviceName: undefined,
      });
    });

    fireEvent.change(screen.getByPlaceholderText('输入邮件中的 6 位数字验证码'), {
      target: { value: '654321' },
    });
    fireEvent.click(screen.getByTestId('remote-verify-bind-btn'));

    // Expect account-switch isolation warning card
    await waitFor(() => {
      expect(screen.getByTestId('remote-account-switch-confirm')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('remote-confirm-account-switch-btn'));

    // Now bound + 6-digit SAS card rendered
    await waitFor(() => {
      expect(screen.getByTestId('remote-pair-sas-784231')).toHaveTextContent('784231');
    });

    fireEvent.click(screen.getByTestId('remote-issue-qr-btn'));
    await waitFor(() => {
      expect(screen.getByRole('img', { name: 'Remote Pairing QR Code' })).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('remote-pair-approve-pair-99'));
    await waitFor(() => {
      expect(remoteMock.respondPairRequest).toHaveBeenCalledWith({
        pairingId: 'pair-99',
        approved: true,
      });
    });

    act(() => {
      listenerHolder.current?.(boundSnap);
    });
    fireEvent.click(screen.getByTestId('remote-pause-toggle'));
    await waitFor(() => {
      expect(remoteMock.setManualPaused).toHaveBeenCalledWith(true);
    });

    // Enforce zero payment / subscription copy
    const renderedText = container.textContent?.toLowerCase() ?? '';
    expect(renderedText).not.toMatch(/stripe|subscription|billing|pricing|checkout|付费|支付|订阅|套餐|会员/);
  });
});
