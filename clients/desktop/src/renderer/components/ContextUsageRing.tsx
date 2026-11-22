import React from 'react';
import type { SessionContextWindow } from '../lib/apiClient';

interface ContextUsageRingProps {
  contextWindow: SessionContextWindow;
}

const RADIUS = 7;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

export function formatContextUsagePercent(usageRatio: number): string {
  const pct = Math.max(0, Math.min(100, usageRatio * 100));
  if (pct === 0) {
    return '0%';
  }
  if (pct < 1) {
    return `${pct.toFixed(1)}%`;
  }
  return `${Math.round(pct)}%`;
}

/**
 * Compact circular SVG progress ring displaying active context-window occupancy and cumulative session tokens.
 */
export const ContextUsageRing: React.FC<ContextUsageRingProps> = ({ contextWindow }) => {
  const pctRaw = Math.max(0, Math.min(100, contextWindow.usageRatio * 100));
  const pctLabel = formatContextUsagePercent(contextWindow.usageRatio);
  const visualPct = contextWindow.estimatedTokens > 0 ? Math.max(pctRaw, 2) : 0;
  const dashOffset = CIRCUMFERENCE * (1 - visualPct / 100);

  const isCritical = contextWindow.triggerRatio >= 1 || pctRaw >= 85;
  const isWarning = !isCritical && pctRaw >= 65;

  const strokeClass = isCritical
    ? 'stroke-rose-400'
    : isWarning
      ? 'stroke-amber-400'
      : 'stroke-emerald-400';

  const labelClass = isCritical
    ? 'text-rose-300'
    : isWarning
      ? 'text-amber-300'
      : 'text-slate-200';

  const tooltip = [
    `上下文占用: ${contextWindow.estimatedTokens.toLocaleString()} / ${contextWindow.modelMaxTokens.toLocaleString()} tokens (${pctLabel})`,
    `自动压缩阈值: ${contextWindow.compressionTriggerTokens.toLocaleString()} tokens`,
    `工作区累计消耗: ${contextWindow.totalSessionTokens.toLocaleString()} tokens (Prompt ${contextWindow.totalPromptTokens.toLocaleString()}，缓存 ${contextWindow.totalCachedPromptTokens.toLocaleString()}，Completion ${contextWindow.totalCompletionTokens.toLocaleString()})`,
  ].join('\n');

  return (
    <span
      data-testid="context-usage-ring"
      title={tooltip}
      className="inline-flex items-center gap-1.5 text-slate-300"
    >
      <svg
        width="18"
        height="18"
        viewBox="0 0 20 20"
        role="progressbar"
        aria-label="上下文窗口使用率"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Number(pctRaw.toFixed(1))}
        className="shrink-0"
      >
        <circle
          cx="10"
          cy="10"
          r={RADIUS}
          fill="none"
          strokeWidth="2.5"
          className="stroke-slate-800"
        />
        <circle
          cx="10"
          cy="10"
          r={RADIUS}
          fill="none"
          strokeWidth="2.5"
          strokeLinecap="round"
          strokeDasharray={CIRCUMFERENCE}
          strokeDashoffset={dashOffset}
          transform="rotate(-90 10 10)"
          className={`${strokeClass} transition-[stroke-dashoffset] duration-300`}
        />
      </svg>
      <span className={`font-medium ${labelClass}`}>上下文 {pctLabel}</span>
      <span className="text-slate-400">
        ({contextWindow.estimatedTokens.toLocaleString()} / {contextWindow.modelMaxTokens.toLocaleString()})
      </span>
      {contextWindow.totalSessionTokens > 0 && (
        <span className="text-slate-400">· 工作区累计 {contextWindow.totalSessionTokens.toLocaleString()} tok</span>
      )}
    </span>
  );
};
