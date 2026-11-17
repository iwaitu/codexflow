import type { UpdateModelSettingsPayload } from '@shared/ipc';

export const DEFAULT_MAX_CONTEXT_TOKENS = 128000;

export const MAX_CONTEXT_PRESETS: ReadonlyArray<{ value: number; label: string }> = [
  { value: 32768, label: '32K' },
  { value: 65536, label: '64K' },
  { value: 128000, label: '128K' },
  { value: 200000, label: '200K' },
];

export interface ModelSettingsFormInput {
  apiUrl: string;
  apiMode: string;
  model: string;
  maxTokensLength: string;
  temperature?: string;
  topP?: string;
  apiKey: string;
}

export type ParseModelSettingsResult =
  | {
      ok: true;
      payload: UpdateModelSettingsPayload;
      hasAnyChange: boolean;
    }
  | {
      ok: false;
      error: string;
    };

function parsePositiveInteger(raw: string): number | null {
  if (!/^\d+$/.test(raw)) {
    return null;
  }
  const value = Number(raw);
  if (!Number.isSafeInteger(value) || value <= 0) {
    return null;
  }
  return value;
}

function parseOptionalBoundedFloat(raw: string | undefined, min: number, max: number, label: string): {
  ok: true;
  value: number | undefined;
} | {
  ok: false;
  error: string;
} {
  const trimmed = raw?.trim() ?? '';
  if (!trimmed) {
    return { ok: true, value: undefined };
  }
  if (!/^-?\d+(?:\.\d+)?$/.test(trimmed)) {
    return { ok: false, error: `${label} 必须是 ${min} 到 ${max} 之间的数字。` };
  }
  const value = Number(trimmed);
  if (!Number.isFinite(value) || value < min || value > max) {
    return { ok: false, error: `${label} 必须在 ${min} 到 ${max} 之间。` };
  }
  return { ok: true, value };
}

/**
 * Validates and normalizes model settings fields shared between the onboarding wizard and settings modal.
 */
export function parseModelSettingsInput(input: ModelSettingsFormInput): ParseModelSettingsResult {
  const maxTokensRaw = input.maxTokensLength.trim();
  let maxTokensLength: number | undefined;
  if (maxTokensRaw) {
    const parsedMax = parsePositiveInteger(maxTokensRaw);
    if (parsedMax === null) {
      return {
        ok: false,
        error: '最大上下文长度必须为大于 0 的整数（例如 128000）。',
      };
    }
    maxTokensLength = parsedMax;
  }

  const parsedTemp = parseOptionalBoundedFloat(input.temperature, 0, 2, 'Temperature');
  if (!parsedTemp.ok) {
    return parsedTemp;
  }

  const parsedTopP = parseOptionalBoundedFloat(input.topP, 0, 1, 'TopP');
  if (!parsedTopP.ok) {
    return parsedTopP;
  }

  const apiUrl = input.apiUrl.trim() || undefined;
  const model = input.model.trim() || undefined;
  const apiKey = input.apiKey.trim() || undefined;
  const payload: UpdateModelSettingsPayload = {
    apiUrl,
    apiMode: input.apiMode,
    model,
    maxTokensLength,
    temperature: parsedTemp.value,
    topP: parsedTopP.value,
    apiKey,
  };

  const hasAnyChange = Boolean(
    apiUrl ||
      model ||
      apiKey ||
      maxTokensLength !== undefined ||
      parsedTemp.value !== undefined ||
      parsedTopP.value !== undefined,
  );

  return { ok: true, payload, hasAnyChange };
}
