import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { APP_METADATA, type DesktopBridge } from '@shared/ipc';
import { AboutModal } from '@renderer/components/AboutModal';
import { usePreferencesStore } from '@renderer/stores/usePreferencesStore';

function installMockBridge(overrides: Partial<Pick<DesktopBridge, 'openExternal' | 'getUpdateState'>> = {}) {
  const bridge = {
    openExternal: vi.fn().mockResolvedValue(true),
    getUpdateState: vi.fn().mockResolvedValue({
      status: 'idle' as const,
      currentVersion: '0.1.11',
      availableVersion: null,
      downloadProgress: null,
      message: null,
    }),
    ...overrides,
  };

  Object.defineProperty(window, 'codexflow', {
    value: bridge as unknown as DesktopBridge,
    configurable: true,
    writable: true,
  });

  return bridge;
}

describe('AboutModal', () => {
  beforeEach(() => {
    usePreferencesStore.getState().resetForTests();
    usePreferencesStore.setState({ language: 'zh-CN', theme: 'dark' });
    Object.defineProperty(navigator, 'clipboard', {
      value: {
        writeText: vi.fn().mockResolvedValue(undefined),
      },
      configurable: true,
    });
  });

  it('renders software version, host version, author, license, project URL, and triggers external open & copy', async () => {
    const bridge = installMockBridge();

    render(
      <AboutModal
        open={true}
        clientVersion="0.1.11"
        hostVersion="0.1.11-host"
        onOpenChange={vi.fn()}
      />,
    );

    expect(screen.getByTestId('about-modal')).toBeInTheDocument();
    expect(screen.getByTestId('about-version')).toHaveTextContent('0.1.11');
    expect(screen.getByTestId('about-host-version')).toHaveTextContent('0.1.11-host');
    expect(screen.getByTestId('about-author')).toHaveTextContent(APP_METADATA.author);
    expect(screen.getByText('版权所有 @codexflow.ai')).toBeVisible();
    expect(screen.getByTestId('about-modal')).toHaveTextContent(APP_METADATA.license);
    expect(screen.getByTestId('about-project-url')).toHaveTextContent(APP_METADATA.projectUrl);

    fireEvent.click(screen.getByTestId('about-project-url'));
    expect(bridge.openExternal).toHaveBeenCalledWith(APP_METADATA.projectUrl);

    fireEvent.click(screen.getByTestId('about-copy-button'));
    await waitFor(() => {
      expect(navigator.clipboard.writeText).toHaveBeenCalledTimes(1);
    });
    const copiedText = vi.mocked(navigator.clipboard.writeText).mock.calls[0]?.[0] ?? '';
    expect(copiedText).toContain(APP_METADATA.name);
    expect(copiedText).toContain('0.1.11');
    expect(copiedText).toContain(APP_METADATA.author);
    expect(copiedText).toContain(APP_METADATA.projectUrl);
  });

  it('falls back to getUpdateState currentVersion when clientVersion prop is omitted', async () => {
    installMockBridge({
      getUpdateState: vi.fn().mockResolvedValue({
        status: 'idle',
        currentVersion: '0.2.0',
        availableVersion: null,
        downloadProgress: null,
        message: null,
      }),
    });

    render(<AboutModal open={true} hostVersion={null} onOpenChange={vi.fn()} />);

    await waitFor(() => {
      expect(screen.getByTestId('about-version')).toHaveTextContent('0.2.0');
    });
  });
});
