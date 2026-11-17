import React, { useId } from 'react';
import { DEFAULT_MAX_CONTEXT_TOKENS, MAX_CONTEXT_PRESETS } from '../lib/modelSettingsForm';

interface MaxContextFieldProps {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  helperText?: string;
}

/**
 * Shared form field for editing `VllmAgent:MaxTokensLength` with quick context-window presets.
 */
export const MaxContextField: React.FC<MaxContextFieldProps> = ({
  value,
  onChange,
  placeholder = String(DEFAULT_MAX_CONTEXT_TOKENS),
  helperText = '用于计算上下文占用并作为 85% 自动压缩阈值依据（单位：tokens）。',
}) => {
  const inputId = useId();
  const numericValue = /^\d+$/.test(value.trim()) ? Number(value.trim()) : null;

  return (
    <div className="block">
      <div className="mb-1 flex flex-wrap items-center justify-between gap-2">
        <label htmlFor={inputId} className="text-slate-300">
          最大上下文长度 (MaxTokensLength, tokens)
        </label>
        <div className="flex items-center gap-1">
          {MAX_CONTEXT_PRESETS.map((preset) => {
            const active = numericValue === preset.value;
            return (
              <button
                key={preset.value}
                type="button"
                onClick={() => onChange(String(preset.value))}
                className={`rounded px-1.5 py-0.5 text-[11px] transition-colors ${
                  active
                    ? 'bg-indigo-600 text-white'
                    : 'bg-slate-800 text-slate-300 hover:bg-slate-700'
                }`}
              >
                {preset.label}
              </button>
            );
          })}
        </div>
      </div>
      <input
        id={inputId}
        type="number"
        min={1}
        step={1}
        inputMode="numeric"
        aria-label="最大上下文长度"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 focus:border-indigo-500 focus:outline-none"
      />
      <div className="mt-1 text-[11px] text-slate-400">{helperText}</div>
    </div>
  );
};
