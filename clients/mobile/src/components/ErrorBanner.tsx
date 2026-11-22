import { AlertTriangle, X } from 'lucide-react';
import type { MobileAppError } from '@/services/errorClassifier';

export function ErrorBanner({
  error,
  onDismiss,
}: {
  readonly error: MobileAppError | null;
  readonly onDismiss: () => void;
}) {
  if (!error) {
    return null;
  }

  return (
    <div
      role="alert"
      className="mx-3 mt-2 flex items-start gap-2.5 rounded-xl border border-rose-500/40 bg-rose-950/80 px-3.5 py-2.5 text-sm text-rose-100 shadow-lg"
    >
      <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-rose-400" />
      <div className="min-w-0 flex-1">
        <div className="font-semibold text-rose-200">{error.title}</div>
        <p className="mt-0.5 text-xs leading-relaxed text-rose-200/90">{error.message}</p>
        <div className="mt-1 text-[11px] font-mono text-rose-300/75">
          code: {error.code} · HTTP {error.httpStatus}
        </div>
      </div>
      <button
        type="button"
        onClick={onDismiss}
        aria-label="关闭错误提示"
        className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg text-rose-300 hover:bg-rose-900/50"
      >
        <X className="h-4 w-4" />
      </button>
    </div>
  );
}
