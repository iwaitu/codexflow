import React, { useEffect, useState } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { Check, Copy, ExternalLink, X } from 'lucide-react';
import { APP_METADATA } from '@shared/ipc';
import { useI18n } from '../lib/i18n';
import { CodexFlowBrand } from './CodexFlowBrand';

interface AboutModalProps {
  open: boolean;
  clientVersion?: string | null;
  hostVersion?: string | null;
  onOpenChange: (open: boolean) => void;
}

export const AboutModal: React.FC<AboutModalProps> = ({
  open,
  clientVersion,
  hostVersion,
  onOpenChange,
}) => {
  const { t } = useI18n();
  const [resolvedVersion, setResolvedVersion] = useState<string | null>(clientVersion ?? null);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (clientVersion) {
      setResolvedVersion(clientVersion);
    }
  }, [clientVersion]);

  useEffect(() => {
    if (!open) {
      setCopied(false);
      return;
    }
    if (clientVersion) {
      return;
    }
    let cancelled = false;
    void window.codexflow
      ?.getUpdateState?.()
      .then((state) => {
        if (!cancelled && state?.currentVersion) {
          setResolvedVersion(state.currentVersion);
        }
      })
      .catch(() => {
        // Ignore version lookup failures in non-Electron environments.
      });
    return () => {
      cancelled = true;
    };
  }, [open, clientVersion]);

  const displayVersion = resolvedVersion || hostVersion || '0.1.12';

  const handleOpenProjectUrl = () => {
    void window.codexflow?.openExternal?.(APP_METADATA.projectUrl);
  };

  const handleCopyInfo = async () => {
    const lines = [
      `${APP_METADATA.name} v${displayVersion}`,
      ...(hostVersion ? [`${t('about.hostVersionLabel')}: v${hostVersion}`] : []),
      `${t('about.authorLabel')}: ${APP_METADATA.author} (${APP_METADATA.copyright})`,
      `${t('about.licenseLabel')}: ${APP_METADATA.license}`,
      `${t('about.projectUrlLabel')}: ${APP_METADATA.projectUrl}`,
    ];
    try {
      await navigator.clipboard?.writeText(lines.join('\n'));
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1800);
    } catch {
      // Ignore clipboard permission errors in restricted contexts.
    }
  };

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/65 backdrop-blur-xs" />
        <Dialog.Content
          data-testid="about-modal"
          className="fixed left-1/2 top-1/2 z-50 w-[min(92vw,480px)] -translate-x-1/2 -translate-y-1/2 rounded-2xl border border-slate-800 bg-slate-900 p-5 text-slate-100 shadow-2xl"
        >
          <div className="flex items-start justify-between gap-3">
            <div className="flex items-center gap-3">
              <CodexFlowBrand iconClassName="h-10 w-10" showText={false} />
              <div>
                <Dialog.Title className="text-base font-semibold text-slate-100">
                  {t('about.title', { name: APP_METADATA.name })}
                </Dialog.Title>
                <Dialog.Description className="mt-0.5 text-xs text-slate-400">
                  {t('about.subtitle')}
                </Dialog.Description>
              </div>
            </div>
            <Dialog.Close
              aria-label={t('about.close')}
              className="rounded-lg p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
            >
              <X className="h-4 w-4" />
            </Dialog.Close>
          </div>

          <div className="mt-4 space-y-2.5 rounded-xl border border-slate-800 bg-slate-950 p-3.5 text-xs">
            <div className="flex items-center justify-between gap-3">
              <span className="text-slate-400">{t('about.versionLabel')}</span>
              <span data-testid="about-version" className="font-mono font-medium text-slate-100">
                v{displayVersion}
              </span>
            </div>

            {hostVersion && (
              <div className="flex items-center justify-between gap-3 border-t border-slate-800/80 pt-2.5">
                <span className="text-slate-400">{t('about.hostVersionLabel')}</span>
                <span data-testid="about-host-version" className="font-mono text-slate-200">
                  v{hostVersion}
                </span>
              </div>
            )}

            <div className="flex items-center justify-between gap-3 border-t border-slate-800/80 pt-2.5">
              <span className="text-slate-400">{t('about.authorLabel')}</span>
              <span data-testid="about-author" className="text-slate-200">
                {APP_METADATA.author} ({APP_METADATA.copyright})
              </span>
            </div>

            <div className="flex items-center justify-between gap-3 border-t border-slate-800/80 pt-2.5">
              <span className="text-slate-400">{t('about.licenseLabel')}</span>
              <span className="font-mono text-slate-200">{APP_METADATA.license}</span>
            </div>

            <div className="flex items-center justify-between gap-3 border-t border-slate-800/80 pt-2.5">
              <span className="shrink-0 text-slate-400">{t('about.projectUrlLabel')}</span>
              <button
                type="button"
                data-testid="about-project-url"
                onClick={handleOpenProjectUrl}
                title={t('about.openProjectUrl')}
                className="inline-flex items-center gap-1 truncate text-indigo-400 hover:text-indigo-300 hover:underline"
              >
                <span className="truncate">{APP_METADATA.projectUrl}</span>
                <ExternalLink className="h-3.5 w-3.5 shrink-0" />
              </button>
            </div>
          </div>

          <div className="mt-4 flex items-center justify-end gap-2">
            <button
              type="button"
              data-testid="about-copy-button"
              onClick={() => void handleCopyInfo()}
              className="inline-flex items-center gap-1.5 rounded-lg border border-slate-700 bg-slate-900 px-3 py-1.5 text-xs font-medium text-slate-200 hover:bg-slate-800"
            >
              {copied ? <Check className="h-3.5 w-3.5 text-emerald-400" /> : <Copy className="h-3.5 w-3.5" />}
              {copied ? t('about.copied') : t('about.copyInfo')}
            </button>
            <Dialog.Close className="rounded-lg bg-indigo-600 px-3.5 py-1.5 text-xs font-medium text-white hover:bg-indigo-500">
              {t('about.close')}
            </Dialog.Close>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
};
