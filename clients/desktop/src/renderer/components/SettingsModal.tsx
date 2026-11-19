import React, { useEffect, useRef, useState } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { AlertTriangle, Trash2, X } from 'lucide-react';
import type {
  ExecutionBackendKind,
  ExecutionPermissionModeKind,
  PersistedPermissionRule,
} from '@shared/ipc';
import type { ExecutionSettings, ModelSettings, SystemStatusSummary } from '../lib/apiClient';
import { isExecutionRiskIncreasing, isRelaxingPermission } from '../lib/executionPolicy';
import { useI18n } from '../lib/i18n';
import { parseModelSettingsInput } from '../lib/modelSettingsForm';
import { usePreferencesStore } from '../stores/usePreferencesStore';
import { AppUpdatePanel } from './AppUpdatePanel';
import { EnvironmentCheckPanel } from './EnvironmentCheckPanel';
import { MaxContextField } from './MaxContextField';

export { isRelaxingPermission };

export type SettingsTabKind = 'execution' | 'model' | 'rules' | 'environment' | 'preferences';

interface SettingsModalProps {
  open: boolean;
  initialTab?: SettingsTabKind;
  clientVersion?: string | null;
  systemStatus: SystemStatusSummary | null;
  executionSettings: ExecutionSettings | null;
  modelSettings: ModelSettings | null;
  controlPlaneAvailable: boolean;
  onClose: () => void;
  onUpdated: () => void;
}

export const SettingsModal: React.FC<SettingsModalProps> = ({
  open,
  initialTab = 'execution',
  clientVersion,
  systemStatus,
  executionSettings,
  modelSettings,
  controlPlaneAvailable,
  onClose,
  onUpdated,
}) => {
  const { t } = useI18n();
  const {
    theme,
    language,
    notificationsEnabled,
    notifyOnlyWhenUnfocused,
    minimizeToTrayOnClose,
    setTheme,
    setLanguage,
    setNotificationsEnabled,
    setNotifyOnlyWhenUnfocused,
    setMinimizeToTrayOnClose,
  } = usePreferencesStore();
  const [tab, setTab] = useState<SettingsTabKind>(initialTab);
  const prevInitialTabRef = useRef<SettingsTabKind>(initialTab);

  const currentBackend = (executionSettings?.backend as ExecutionBackendKind) || 'Local';
  const currentMode = (executionSettings?.permissionMode as ExecutionPermissionModeKind) || 'Ask';
  const [backend, setBackend] = useState<ExecutionBackendKind>(currentBackend);
  const [permissionMode, setPermissionMode] = useState<ExecutionPermissionModeKind>(currentMode);
  const [confirmRelaxOpen, setConfirmRelaxOpen] = useState(false);

  const [apiUrl, setApiUrl] = useState(modelSettings?.apiUrl ?? '');
  const [apiMode, setApiMode] = useState(modelSettings?.apiMode ?? 'ChatCompletions');
  const [model, setModel] = useState(modelSettings?.model ?? '');
  const [maxTokensLength, setMaxTokensLength] = useState(
    modelSettings?.maxTokensLength != null ? String(modelSettings.maxTokensLength) : '',
  );
  const [temperature, setTemperature] = useState(
    modelSettings?.temperature != null ? String(modelSettings.temperature) : '',
  );
  const [topP, setTopP] = useState(
    modelSettings?.topP != null ? String(modelSettings.topP) : '',
  );
  const [apiKey, setApiKey] = useState('');
  const [supportsMultimodal, setSupportsMultimodal] = useState(Boolean(modelSettings?.supportsMultimodal));

  const [rules, setRules] = useState<PersistedPermissionRule[]>([]);
  const [riskReasons, setRiskReasons] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const wasOpenRef = useRef(false);

  useEffect(() => {
    const justOpened = open && !wasOpenRef.current;
    const initialTabChanged = prevInitialTabRef.current !== initialTab;
    wasOpenRef.current = open;
    prevInitialTabRef.current = initialTab;
    if (open && initialTabChanged && !justOpened) {
      setTab(initialTab);
    }
    if (!justOpened) return;
    setTab(initialTab);
    setBackend(currentBackend);
    setPermissionMode(currentMode);
    setApiUrl(modelSettings?.apiUrl ?? '');
    setApiMode(modelSettings?.apiMode ?? 'ChatCompletions');
    setModel(modelSettings?.model ?? '');
    setMaxTokensLength(modelSettings?.maxTokensLength != null ? String(modelSettings.maxTokensLength) : '');
    setTemperature(modelSettings?.temperature != null ? String(modelSettings.temperature) : '');
    setTopP(modelSettings?.topP != null ? String(modelSettings.topP) : '');
    setApiKey('');
    setSupportsMultimodal(Boolean(modelSettings?.supportsMultimodal));
    setConfirmRelaxOpen(false);
    setNotice(null);
    setError(null);
  }, [open, initialTab, currentBackend, currentMode, modelSettings]);

  const loadRules = async () => {
    if (!controlPlaneAvailable) return;
    setError(null);
    try {
      const res = await window.codexflow.listPermissionRules();
      if (res.ok && res.data) {
        setRules(res.data.rules);
      } else if (!res.ok) {
        setError(res.errorMessage || `加载规则失败 (${res.status})`);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  };

  useEffect(() => {
    if (open && tab === 'rules') {
      void loadRules();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, tab]);

  const applyExecutionUpdate = async () => {
    setSaving(true);
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.updateExecutionSettings({ backend, permissionMode });
      setConfirmRelaxOpen(false);
      if (!res.ok) {
        setError(res.errorMessage || `保存失败 (${res.status})`);
        return;
      }
      setNotice('执行与权限设置已生效。');
      onUpdated();
    } catch (err) {
      setConfirmRelaxOpen(false);
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  };

  const handleExecutionSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    const { risky, reasons } = isExecutionRiskIncreasing(currentBackend, currentMode, backend, permissionMode);
    if (risky) {
      setRiskReasons(reasons);
      setConfirmRelaxOpen(true);
      return;
    }
    void applyExecutionUpdate();
  };

  const handleModelSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    const parsed = parseModelSettingsInput({
      apiUrl,
      apiMode,
      model,
      maxTokensLength,
      temperature,
      topP,
      apiKey,
      ...(supportsMultimodal !== Boolean(modelSettings?.supportsMultimodal)
        ? { supportsMultimodal }
        : {}),
    });
    if (!parsed.ok) {
      setError(parsed.error);
      setNotice(null);
      return;
    }

    setSaving(true);
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.updateModelSettings(parsed.payload);
      if (!res.ok) {
        setError(res.errorMessage || `模型配置保存失败 (${res.status})`);
        return;
      }
      setApiKey('');
      if (typeof res.data?.supportsMultimodal === 'boolean') {
        setSupportsMultimodal(res.data.supportsMultimodal);
      }
      const probeNote = res.data?.multimodalProbeMessage ? ` ${res.data.multimodalProbeMessage}` : '';
      setNotice(`模型配置已更新，上下文与模型参数将在后续请求立即生效。${probeNote}`);
      onUpdated();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  };

  const handleDeleteRule = async (id: string) => {
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.deletePermissionRule(id);
      if (res.ok) {
        setRules((prev) => prev.filter((r) => r.id !== id));
        setNotice(t('settings.ruleRevoked'));
      } else {
        setError(res.errorMessage || `撤销规则失败 (${res.status})`);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  };

  return (
    <Dialog.Root open={open} onOpenChange={(v) => (!v ? onClose() : undefined)}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/70 backdrop-blur-sm" />
        <Dialog.Content className="fixed left-1/2 top-1/2 z-50 max-h-[90vh] w-full max-w-2xl -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-2xl border border-slate-800 bg-slate-900 p-6 shadow-2xl">
          <div className="flex items-center justify-between">
            <Dialog.Title className="text-base font-semibold text-slate-100">{t('settings.title')}</Dialog.Title>
            <button
              type="button"
              onClick={onClose}
              aria-label={t('settings.close')}
              className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
            >
              <X className="h-4 w-4" />
            </button>
          </div>

          <div className="mt-4 flex flex-wrap gap-2 border-b border-slate-800 pb-3 text-xs">
            {(
              [
                ['execution', t('settings.tab.execution')],
                ['model', t('settings.tab.model')],
                ['rules', t('settings.tab.rules')],
                ['environment', t('settings.tab.environment')],
                ['preferences', t('settings.tab.preferences')],
              ] as const
            ).map(([key, label]) => (
              <button
                key={key}
                type="button"
                data-testid={`settings-tab-${key}`}
                onClick={() => setTab(key)}
                className={`rounded-lg px-3 py-1.5 font-medium ${
                  tab === key ? 'bg-indigo-600 text-white' : 'bg-slate-800 text-slate-300 hover:bg-slate-700'
                }`}
              >
                {label}
              </button>
            ))}
          </div>

          {tab === 'execution' && (
            <form onSubmit={handleExecutionSubmit} className="mt-4 space-y-4 text-xs">
              <div className="grid grid-cols-2 gap-3">
                <label className="block">
                  <span className="mb-1 block text-slate-300">{t('settings.executionBackend')}</span>
                  <select
                    aria-label={t('settings.executionBackend')}
                    value={backend}
                    onChange={(e) => setBackend(e.target.value as ExecutionBackendKind)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  >
                    <option value="Local">Local（本地执行）</option>
                    <option value="Docker" disabled={!systemStatus?.dockerAvailable}>
                      Docker（容器隔离{systemStatus?.dockerAvailable ? '' : ' - 不可用'}）
                    </option>
                  </select>
                </label>

                <label className="block">
                  <span className="mb-1 block text-slate-300">{t('settings.permissionMode')}</span>
                  <select
                    aria-label={t('settings.permissionMode')}
                    value={permissionMode}
                    onChange={(e) => setPermissionMode(e.target.value as ExecutionPermissionModeKind)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  >
                    <option value="Deny">Deny（禁止执行）</option>
                    <option value="Ask">Ask（每次询问）</option>
                    <option value="AutoInWorkspace">AutoInWorkspace（只读 git/rg）</option>
                    <option value="FullAuto">FullAuto（全部自动放行）</option>
                  </select>
                </label>
              </div>

              {permissionMode === 'FullAuto' && (
                <div className="flex items-start gap-2 rounded-lg border border-rose-500/40 bg-rose-950/30 p-3 text-rose-200">
                  <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-rose-400" />
                  <div>
                    <strong>高风险提示：</strong>
                    `FullAuto` 等于信任模型自动放行研发白名单内命令及日常模式 `run_shell` 脚本；在 Local 后端下将以你的用户权限无隔离运行。
                  </div>
                </div>
              )}

              {confirmRelaxOpen && (
                <div
                  data-testid="relax-permission-confirm"
                  className="rounded-xl border border-amber-500/50 bg-amber-950/30 p-3 text-amber-100"
                >
                  <div className="font-semibold">确认放宽执行或隔离策略？</div>
                  <ul className="mt-1 list-disc space-y-0.5 pl-4 text-amber-200/90">
                    {riskReasons.map((reason) => (
                      <li key={reason}>{reason}</li>
                    ))}
                  </ul>
                  <div className="mt-2.5 flex gap-2">
                    <button
                      type="button"
                      disabled={saving}
                      onClick={() => void applyExecutionUpdate()}
                      className="rounded-lg bg-amber-600 px-3 py-1 font-medium text-white hover:bg-amber-500"
                    >
                      {t('settings.confirmRelaxButton')}
                    </button>
                    <button
                      type="button"
                      onClick={() => setConfirmRelaxOpen(false)}
                      className="rounded-lg border border-slate-700 px-3 py-1 text-slate-300 hover:bg-slate-800"
                    >
                      {t('settings.cancel')}
                    </button>
                  </div>
                </div>
              )}

              {notice && <div className="text-emerald-300">{notice}</div>}
              {error && <div className="text-rose-300">{error}</div>}

              <div className="flex justify-end">
                <button
                  type="submit"
                  disabled={saving || !controlPlaneAvailable}
                  className="rounded-lg bg-indigo-600 px-4 py-1.5 font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
                >
                  {t('settings.saveExecution')}
                </button>
              </div>
            </form>
          )}

          {tab === 'model' && (
            <form noValidate onSubmit={(e) => void handleModelSubmit(e)} className="mt-4 space-y-3 text-xs">
              <label className="block">
                <span className="mb-1 block text-slate-300">ApiUrl</span>
                <input
                  value={apiUrl}
                  onChange={(e) => setApiUrl(e.target.value)}
                  className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                />
              </label>

              <div className="grid grid-cols-2 gap-3">
                <label className="block">
                  <span className="mb-1 block text-slate-300">Model</span>
                  <input
                    value={model}
                    onChange={(e) => setModel(e.target.value)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  />
                </label>

                <label className="block">
                  <span className="mb-1 block text-slate-300">ApiMode</span>
                  <select
                    value={apiMode}
                    onChange={(e) => setApiMode(e.target.value)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  >
                    <option value="ChatCompletions">ChatCompletions</option>
                    <option value="Responses">Responses</option>
                  </select>
                </label>
              </div>

              <MaxContextField
                value={maxTokensLength}
                onChange={setMaxTokensLength}
                helperText="留空则保持当前设置不变；用于计算上下文占用并作为 85% 自动压缩阈值依据（单位：tokens）。"
              />

              <div className="grid grid-cols-2 gap-3">
                <label className="block">
                  <span className="mb-1 block text-slate-300">Temperature（0 ~ 2，可选）</span>
                  <input
                    type="number"
                    min={0}
                    max={2}
                    step="0.05"
                    aria-label="Temperature"
                    value={temperature}
                    onChange={(e) => setTemperature(e.target.value)}
                    placeholder="留空则保持当前配置"
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  />
                </label>

                <label className="block">
                  <span className="mb-1 block text-slate-300">TopP（0 ~ 1，可选）</span>
                  <input
                    type="number"
                    min={0}
                    max={1}
                    step="0.05"
                    aria-label="TopP"
                    value={topP}
                    onChange={(e) => setTopP(e.target.value)}
                    placeholder="留空则保持当前配置"
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  />
                </label>
              </div>

              <label className="block">
                <span className="mb-1 block text-slate-300">
                  API Key（当前：{modelSettings?.apiKeySet ? `已设置 (****${modelSettings.apiKeyLast4 ?? ''})` : '未设置'}）
                </span>
                <input
                  type="password"
                  value={apiKey}
                  onChange={(e) => setApiKey(e.target.value)}
                  placeholder="留空则保持当前 Key 不变"
                  className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                />
              </label>

              <label className="flex items-start gap-2 rounded-lg border border-slate-800 bg-slate-950/60 p-3">
                <input
                  type="checkbox"
                  checked={supportsMultimodal}
                  onChange={(e) => setSupportsMultimodal(e.target.checked)}
                  className="mt-0.5 h-3.5 w-3.5 rounded border-slate-600 bg-slate-900 text-indigo-500"
                />
                <span className="space-y-0.5 text-slate-300">
                  <span className="block font-medium text-slate-200">支持多模态（图片与扫描版 PDF 识别）</span>
                  <span className="block text-[11px] text-slate-400">
                    保存模型配置时会自动使用内置测试图片检测多模态能力；若未勾选但测试通过，将自动启用；若探测超时或未明确拒绝，将保留你的手动勾选。
                  </span>
                  {modelSettings?.multimodalProbeMessage && (
                    <span
                      className={`block text-[11px] ${
                        modelSettings.multimodalProbeStatus === 'Supported'
                          ? 'text-emerald-300'
                          : modelSettings.multimodalProbeStatus === 'Unsupported'
                            ? 'text-rose-300'
                            : 'text-amber-300'
                      }`}
                    >
                      最近探测：{modelSettings.multimodalProbeMessage}
                    </span>
                  )}
                </span>
              </label>

              {notice && <div className="text-emerald-300">{notice}</div>}
              {error && <div className="text-rose-300">{error}</div>}

              <div className="flex justify-end">
                <button
                  type="submit"
                  disabled={saving || !controlPlaneAvailable}
                  className="rounded-lg bg-indigo-600 px-4 py-1.5 font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
                >
                  {t('settings.saveModel')}
                </button>
              </div>
            </form>
          )}

          {tab === 'rules' && (
            <div className="mt-4 space-y-2 text-xs">
              {notice && <div className="text-emerald-300">{notice}</div>}
              {error && <div className="text-rose-300">{error}</div>}
              {rules.length === 0 ? (
                <div className="rounded-lg border border-slate-800 bg-slate-950 p-4 text-center text-slate-400">
                  {t('settings.noRules')}
                </div>
              ) : (
                <div className="max-h-64 space-y-2 overflow-y-auto">
                  {rules.map((r) => (
                    <div
                      key={r.id}
                      className="flex items-center justify-between gap-2 rounded-lg border border-slate-800 bg-slate-950 p-3"
                    >
                      <div className="min-w-0">
                        <div className="font-mono text-slate-100">
                          {r.executable} {r.argv.join(' ')}
                        </div>
                        <div className="mt-0.5 text-[11px] text-slate-400">
                          {r.toolName} · {new Date(r.createdAtUtc).toLocaleString()}
                          {r.note ? ` · ${r.note}` : ''}
                        </div>
                      </div>
                      <button
                        type="button"
                        onClick={() => void handleDeleteRule(r.id)}
                        className="rounded p-1.5 text-slate-400 hover:bg-rose-950 hover:text-rose-300"
                        title={t('settings.revokeRule')}
                      >
                        <Trash2 className="h-3.5 w-3.5" />
                      </button>
                    </div>
                  ))}
                </div>
              )}
            </div>
          )}

          {tab === 'preferences' && (
            <div data-testid="settings-preferences-panel" className="mt-4 space-y-4 text-xs">
              <div className="grid grid-cols-2 gap-3">
                <div className="rounded-xl border border-slate-800 bg-slate-950 p-3">
                  <div className="mb-2 font-medium text-slate-200">{t('settings.themeLabel')}</div>
                  <div className="grid grid-cols-2 gap-2">
                    <button
                      type="button"
                      data-testid="theme-select-dark"
                      onClick={() => setTheme('dark')}
                      className={`rounded-lg border px-2.5 py-1.5 font-medium transition ${
                        theme === 'dark'
                          ? 'border-indigo-500 bg-indigo-600 text-white'
                          : 'border-slate-700 bg-slate-900 text-slate-300 hover:bg-slate-800'
                      }`}
                    >
                      {t('settings.themeDark')}
                    </button>
                    <button
                      type="button"
                      data-testid="theme-select-light"
                      onClick={() => setTheme('light')}
                      className={`rounded-lg border px-2.5 py-1.5 font-medium transition ${
                        theme === 'light'
                          ? 'border-indigo-500 bg-indigo-600 text-white'
                          : 'border-slate-700 bg-slate-900 text-slate-300 hover:bg-slate-800'
                      }`}
                    >
                      {t('settings.themeLight')}
                    </button>
                  </div>
                </div>

                <div className="rounded-xl border border-slate-800 bg-slate-950 p-3">
                  <div className="mb-2 font-medium text-slate-200">{t('settings.languageLabel')}</div>
                  <div className="grid grid-cols-2 gap-2">
                    <button
                      type="button"
                      data-testid="lang-select-zh-CN"
                      onClick={() => setLanguage('zh-CN')}
                      className={`rounded-lg border px-2.5 py-1.5 font-medium transition ${
                        language === 'zh-CN'
                          ? 'border-indigo-500 bg-indigo-600 text-white'
                          : 'border-slate-700 bg-slate-900 text-slate-300 hover:bg-slate-800'
                      }`}
                    >
                      {t('settings.languageZh')}
                    </button>
                    <button
                      type="button"
                      data-testid="lang-select-en-US"
                      onClick={() => setLanguage('en-US')}
                      className={`rounded-lg border px-2.5 py-1.5 font-medium transition ${
                        language === 'en-US'
                          ? 'border-indigo-500 bg-indigo-600 text-white'
                          : 'border-slate-700 bg-slate-900 text-slate-300 hover:bg-slate-800'
                      }`}
                    >
                      {t('settings.languageEn')}
                    </button>
                  </div>
                  <div className="mt-1.5 text-[11px] text-slate-400">
                    {language === 'en-US'
                      ? 'English currently covers navigation, status bar, and core settings; chat cards and Business Skills remain in Chinese.'
                      : '英文模式目前优先覆盖导航、状态栏与核心设置面板，业务技能与审批详单持续完善中。'}
                  </div>
                </div>
              </div>

              <div className="space-y-2.5 rounded-xl border border-slate-800 bg-slate-950 p-3.5">
                <label className="flex cursor-pointer items-start justify-between gap-3">
                  <div>
                    <div className="font-medium text-slate-200">{t('settings.notificationsLabel')}</div>
                    <div className="mt-0.5 text-[11px] text-slate-400">{t('settings.notificationsDesc')}</div>
                  </div>
                  <input
                    type="checkbox"
                    data-testid="pref-notifications-toggle"
                    checked={notificationsEnabled}
                    onChange={(e) => setNotificationsEnabled(e.target.checked)}
                    className="mt-1 h-4 w-4 rounded border-slate-700 accent-indigo-600"
                  />
                </label>

                <label className="flex cursor-pointer items-start justify-between gap-3 border-t border-slate-800/80 pt-2.5">
                  <div>
                    <div className="font-medium text-slate-200">{t('settings.notifyUnfocusedLabel')}</div>
                    <div className="mt-0.5 text-[11px] text-slate-400">{t('settings.notifyUnfocusedDesc')}</div>
                  </div>
                  <input
                    type="checkbox"
                    data-testid="pref-notify-unfocused-toggle"
                    disabled={!notificationsEnabled}
                    checked={notifyOnlyWhenUnfocused}
                    onChange={(e) => setNotifyOnlyWhenUnfocused(e.target.checked)}
                    className="mt-1 h-4 w-4 rounded border-slate-700 accent-indigo-600 disabled:opacity-40"
                  />
                </label>

                <label className="flex cursor-pointer items-start justify-between gap-3 border-t border-slate-800/80 pt-2.5">
                  <div>
                    <div className="font-medium text-slate-200">{t('settings.minimizeToTrayLabel')}</div>
                    <div className="mt-0.5 text-[11px] text-slate-400">{t('settings.minimizeToTrayDesc')}</div>
                  </div>
                  <input
                    type="checkbox"
                    data-testid="pref-minimize-tray-toggle"
                    checked={minimizeToTrayOnClose}
                    onChange={(e) => setMinimizeToTrayOnClose(e.target.checked)}
                    className="mt-1 h-4 w-4 rounded border-slate-700 accent-indigo-600"
                  />
                </label>
              </div>

              <AppUpdatePanel
                active={open && tab === 'preferences'}
                clientVersion={clientVersion}
                hostVersion={systemStatus?.version ?? null}
              />
            </div>
          )}

          {tab === 'environment' && (
            <EnvironmentCheckPanel active={open && tab === 'environment'} />
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
};
