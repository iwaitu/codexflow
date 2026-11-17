import React, { useId } from 'react';

interface CodexFlowBrandProps {
  className?: string;
  iconClassName?: string;
  textClassName?: string;
  subtitle?: string;
  showText?: boolean;
}

/**
 * Animated CodexFlow brand mark aligned with CodexFlow.WebUI.
 */
export const CodexFlowBrand: React.FC<CodexFlowBrandProps> = ({
  className = '',
  iconClassName = 'h-9 w-9',
  textClassName = '',
  subtitle,
  showText = true,
}) => {
  const gradientId = useId();
  const haloGradientId = useId();

  return (
    <div className={`flex items-center gap-2.5 ${className}`.trim()}>
      <svg
        viewBox="0 0 64 64"
        aria-hidden="true"
        data-testid="codexflow-brand-svg"
        className={`shrink-0 ${iconClassName}`.trim()}
      >
        <defs>
          <linearGradient id={gradientId} x1="14" y1="14" x2="52" y2="50" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#9dd6ff" />
            <stop offset="52%" stopColor="#4aa8ff" />
            <stop offset="100%" stopColor="#0a63ff" />
          </linearGradient>
          <linearGradient id={haloGradientId} x1="12" y1="52" x2="56" y2="12" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#dff1ff" stopOpacity="0.28" />
            <stop offset="100%" stopColor="#4aa8ff" stopOpacity="0.9" />
          </linearGradient>
        </defs>

        <circle
          cx="32"
          cy="32"
          r="21"
          fill="none"
          stroke={`url(#${haloGradientId})`}
          strokeWidth="4.5"
          strokeDasharray="86 42"
          strokeLinecap="round"
        >
          <animateTransform
            attributeName="transform"
            type="rotate"
            from="0 32 32"
            to="360 32 32"
            dur="8s"
            repeatCount="indefinite"
          />
        </circle>

        <path
          d="M27 22 18.5 32 27 42"
          fill="none"
          stroke={`url(#${gradientId})`}
          strokeWidth="4.25"
          strokeLinecap="round"
          strokeLinejoin="round"
        >
          <animate attributeName="opacity" values="0.72;1;0.72" dur="1.9s" repeatCount="indefinite" />
        </path>

        <path
          d="M37 22 45.5 32 37 42"
          fill="none"
          stroke={`url(#${gradientId})`}
          strokeWidth="4.25"
          strokeLinecap="round"
          strokeLinejoin="round"
        >
          <animate attributeName="opacity" values="1;0.72;1" dur="1.9s" repeatCount="indefinite" />
        </path>

        <circle cx="32" cy="32" r="3.5" fill={`url(#${gradientId})`}>
          <animate attributeName="r" values="3.2;4.3;3.2" dur="1.9s" repeatCount="indefinite" />
          <animate attributeName="opacity" values="0.85;1;0.85" dur="1.9s" repeatCount="indefinite" />
        </circle>
      </svg>

      {showText && (
        <div className={`min-w-0 ${textClassName}`.trim()}>
          <div className="truncate text-[0.75rem] font-semibold uppercase tracking-[0.28em] text-sky-100">
            codexflow
          </div>
          {subtitle ? (
            <div className="truncate text-[11px] tracking-[0.16em] text-slate-400">{subtitle}</div>
          ) : null}
        </div>
      )}
    </div>
  );
};
