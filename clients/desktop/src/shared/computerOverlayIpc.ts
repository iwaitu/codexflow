// Keep the overlay's runtime channels separate so each sandboxed preload bundles independently.
export const COMPUTER_OVERLAY_IPC_CHANNELS = {
  triggerStop: 'codexflow:computer-overlay-trigger-stop',
  stateUpdated: 'codexflow:computer-overlay-state-updated',
} as const;
