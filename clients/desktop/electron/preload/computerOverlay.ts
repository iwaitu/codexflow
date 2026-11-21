import { ipcRenderer } from 'electron';
import { COMPUTER_OVERLAY_IPC_CHANNELS } from '../../src/shared/computerOverlayIpc';
import type { ComputerControlOverlayState } from '../../src/shared/ipc';

interface OverlayElementLike {
  textContent: string | null;
  disabled?: boolean;
  addEventListener: (event: string, listener: () => void) => void;
}

interface OverlayGlobalLike {
  document?: {
    getElementById: (id: string) => OverlayElementLike | null;
  };
  addEventListener?: (event: string, listener: () => void) => void;
}

const overlayGlobal = globalThis as unknown as OverlayGlobalLike;
let latestState: ComputerControlOverlayState | null = null;

function applyOverlayDomState(state: ComputerControlOverlayState): void {
  const doc = overlayGlobal.document;
  if (!doc) {
    return;
  }
  const statusEl = doc.getElementById('cf-overlay-status');
  const shortcutEl = doc.getElementById('cf-overlay-shortcut');
  const stopBtn = doc.getElementById('cf-overlay-stop-btn');

  if (statusEl) {
    statusEl.textContent = state.summary?.trim() || 'AI 正在操控本机桌面';
  }
  if (shortcutEl) {
    const sc = state.shortcut?.trim() || 'CommandOrControl+.';
    const prettyShortcut =
      process.platform === 'darwin'
        ? sc.replace(/CommandOrControl/gi, '⌘')
        : sc.replace(/CommandOrControl/gi, 'Ctrl');
    shortcutEl.textContent = `${prettyShortcut} 或连按物理 Esc×2 急停`;
  }
  if (stopBtn) {
    stopBtn.disabled = false;
  }
}

ipcRenderer.on(COMPUTER_OVERLAY_IPC_CHANNELS.stateUpdated, (_event, state: ComputerControlOverlayState) => {
  latestState = state;
  applyOverlayDomState(state);
});

overlayGlobal.addEventListener?.('DOMContentLoaded', () => {
  const stopBtn = overlayGlobal.document?.getElementById('cf-overlay-stop-btn');
  if (stopBtn) {
    stopBtn.addEventListener('click', () => {
      stopBtn.disabled = true;
      void ipcRenderer.invoke(COMPUTER_OVERLAY_IPC_CHANNELS.triggerStop).finally(() => {
        stopBtn.disabled = false;
      });
    });
  }
  if (latestState) {
    applyOverlayDomState(latestState);
  }
});
