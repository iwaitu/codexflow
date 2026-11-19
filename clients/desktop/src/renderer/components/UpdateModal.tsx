import React from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { ExternalLink, RefreshCw, X } from 'lucide-react';
import { APP_METADATA } from '@shared/ipc';
import { useI18n } from '../lib/i18n';
import { AppUpdatePanel } from './AppUpdatePanel';

interface UpdateModalProps {
  open: boolean;
  clientVersion?: string | null;
  hostVersion?: string | null;
  onOpenChange: (open: boolean) => void;
}

/**
 * Dedicated desktop auto-update modal triggered from the Help menu and command palette
 * so checking for updates does not jump into the full Settings modal.
 */
export const UpdateModal: React.FC<UpdateModalProps> = ({
  open,
  clientVersion,
  hostVersion,
  onOpenChange,
}) => {
  const { t } = useI18n();

  const handleOpenReleases = () => {
    void window.codexflow?.openExternal?.(APP_METADATA.releasesUrl);
  };

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/70 backdrop-blur-sm" />
        <Dialog.Content
          data-testid="update-modal"
          className="fixed left-1/2 top-1/2 z-50 w-full max-w-lg -translate-x-1/2 -translate-y-1/2 rounded-2xl border border-slate-800 bg-slate-900 p-6 shadow-2xl"
        >
          <div className="flex items-start justify-between gap-3">
            <div className="flex items-center gap-3">
              <div className="flex h-10 w-10 items-center justify-center rounded-xl border border-indigo-500/30 bg-indigo-500/10 text-indigo-400">
                <RefreshCw className="h-5 w-5" />
              </div>
              <div>
                <Dialog.Title className="text-base font-semibold text-slate-100">
                  {t('updatesModal.title')}
                </Dialog.Title>
                <Dialog.Description className="mt-0.5 text-xs text-slate-400">
                  {t('updatesModal.subtitle')}
                </Dialog.Description>
              </div>
            </div>
            <button
              type="button"
              onClick={() => onOpenChange(false)}
              aria-label={t('updatesModal.close')}
              className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
            >
              <X className="h-4 w-4" />
            </button>
          </div>

          <div className="mt-5">
            <AppUpdatePanel
              active={open}
              clientVersion={clientVersion}
              hostVersion={hostVersion}
              autoCheckOnOpen
            />
          </div>

          <div className="mt-5 flex items-center justify-between gap-3">
            <button
              type="button"
              data-testid="update-releases-button"
              onClick={handleOpenReleases}
              className="inline-flex items-center gap-1.5 rounded-lg border border-slate-700 bg-slate-950 px-3 py-1.5 text-xs font-medium text-slate-200 hover:bg-slate-800"
            >
              <ExternalLink className="h-3.5 w-3.5 text-indigo-400" />
              {t('updatesModal.openReleases')}
            </button>
            <button
              type="button"
              onClick={() => onOpenChange(false)}
              className="rounded-lg bg-indigo-600 px-3.5 py-1.5 text-xs font-medium text-white hover:bg-indigo-500"
            >
              {t('updatesModal.close')}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
};
