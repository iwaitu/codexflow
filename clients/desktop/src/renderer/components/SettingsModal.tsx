import React, { useEffect, useRef, useState } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { AlertTriangle, Trash2, X } from 'lucide-react';
import type {
  BrowserSettingsDto,
  ComputerSettingsDto,
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

export type SettingsTabKind = 'execution' | 'model' | 'browser' | 'computer' | 'rules' | 'environment' | 'preferences';

interface SettingsModalProps {
  open: boolean;
  initialTab?: SettingsTabKind;
  clientVersion?: string | null;
  systemStatus: SystemStatusSummary | null;
  executionSettings: ExecutionSettings | null;
  modelSettings: ModelSettings | null;
  browserSettings?: BrowserSettingsDto | null;
  computerSettings?: ComputerSettingsDto | null;
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
  browserSettings = null,
  computerSettings = null,
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
  const [browserEnabled, setBrowserEnabled] = useState(Boolean(browserSettings?.enabled ?? true));
  const [browserExecutablePath, setBrowserExecutablePath] = useState(browserSettings?.executablePath ?? '');
  const [browserRemoteCdpWsUrl, setBrowserRemoteCdpWsUrl] = useState(browserSettings?.remoteCdpWsUrl ?? '');
  const [browserRemoteCdpTouched, setBrowserRemoteCdpTouched] = useState(false);
  const [browserHeadless, setBrowserHeadless] = useState(Boolean(browserSettings?.headless ?? true));
  const [browserProfileMode, setBrowserProfileMode] = useState(browserSettings?.profileMode ?? 'Ephemeral');
  const [browserApprovalPolicy, setBrowserApprovalPolicy] = useState(
    browserSettings?.interactionApprovalPolicy ?? 'HighRiskOnly',
  );
  const [browserAllowLocalhostDaily, setBrowserAllowLocalhostDaily] = useState(
    Boolean(browserSettings?.allowLocalhostInDailyMode),
  );
  const [browserAllowDownloads, setBrowserAllowDownloads] = useState(
    Boolean(browserSettings?.allowControlledDownloads),
  );
  const [browserAllowedDomainsText, setBrowserAllowedDomainsText] = useState(
    (browserSettings?.allowedDomains ?? []).join('\n'),
  );
  const [browserBlockedDomainsText, setBrowserBlockedDomainsText] = useState(
    (browserSettings?.blockedDomains ?? []).join('\n'),
  );
  const [browserIdleMinutes, setBrowserIdleMinutes] = useState(
    String(browserSettings?.idleTimeoutMinutes ?? 15),
  );
  const [browserMaxSessions, setBrowserMaxSessions] = useState(
    String(browserSettings?.maxConcurrentSessions ?? 3),
  );
  const [computerEnabled, setComputerEnabled] = useState(Boolean(computerSettings?.enabled ?? false));
  const [computerApprovalPolicy, setComputerApprovalPolicy] = useState(
    computerSettings?.interactionApprovalPolicy ?? 'AlwaysAsk',
  );
  const [computerCoordinateMode, setComputerCoordinateMode] = useState(
    computerSettings?.coordinateMode ?? 'Auto',
  );
  const [computerMaxLongEdge, setComputerMaxLongEdge] = useState(
    String(computerSettings?.maxImageLongEdge ?? 1280),
  );
  const [computerMaxScreenshots, setComputerMaxScreenshots] = useState(
    String(computerSettings?.maxScreenshotsPerTurn ?? 10),
  );
  const [computerEnableGrid, setComputerEnableGrid] = useState(
    Boolean(computerSettings?.enableGridOverlay ?? true),
  );
  const [computerAutoMinimizeHost, setComputerAutoMinimizeHost] = useState(
    Boolean(computerSettings?.autoMinimizeHostWindow ?? true),
  );
  const [computerStopShortcut, setComputerStopShortcut] = useState(
    computerSettings?.emergencyStopShortcut ?? 'CommandOrControl+.',
  );
  const [computerIdleMinutes, setComputerIdleMinutes] = useState(
    String(computerSettings?.idleTimeoutMinutes ?? 15),
  );
  const [computerBlockedProcessesText, setComputerBlockedProcessesText] = useState(
    (computerSettings?.blockedProcessNames ?? []).join('\n'),
  );

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
    setBrowserEnabled(Boolean(browserSettings?.enabled ?? true));
    setBrowserExecutablePath(browserSettings?.executablePath ?? '');
    setBrowserRemoteCdpWsUrl(browserSettings?.remoteCdpWsUrl ?? '');
    setBrowserRemoteCdpTouched(false);
    setBrowserHeadless(Boolean(browserSettings?.headless ?? true));
    setBrowserProfileMode(browserSettings?.profileMode ?? 'Ephemeral');
    setBrowserApprovalPolicy(browserSettings?.interactionApprovalPolicy ?? 'HighRiskOnly');
    setBrowserAllowLocalhostDaily(Boolean(browserSettings?.allowLocalhostInDailyMode));
    setBrowserAllowDownloads(Boolean(browserSettings?.allowControlledDownloads));
    setBrowserAllowedDomainsText((browserSettings?.allowedDomains ?? []).join('\n'));
    setBrowserBlockedDomainsText((browserSettings?.blockedDomains ?? []).join('\n'));
    setBrowserIdleMinutes(String(browserSettings?.idleTimeoutMinutes ?? 15));
    setBrowserMaxSessions(String(browserSettings?.maxConcurrentSessions ?? 3));
    setComputerEnabled(Boolean(computerSettings?.enabled ?? false));
    setComputerApprovalPolicy(computerSettings?.interactionApprovalPolicy ?? 'AlwaysAsk');
    setComputerCoordinateMode(computerSettings?.coordinateMode ?? 'Auto');
    setComputerMaxLongEdge(String(computerSettings?.maxImageLongEdge ?? 1280));
    setComputerMaxScreenshots(String(computerSettings?.maxScreenshotsPerTurn ?? 10));
    setComputerEnableGrid(Boolean(computerSettings?.enableGridOverlay ?? true));
    setComputerAutoMinimizeHost(Boolean(computerSettings?.autoMinimizeHostWindow ?? true));
    setComputerStopShortcut(computerSettings?.emergencyStopShortcut ?? 'CommandOrControl+.');
    setComputerIdleMinutes(String(computerSettings?.idleTimeoutMinutes ?? 15));
    setComputerBlockedProcessesText((computerSettings?.blockedProcessNames ?? []).join('\n'));
    setConfirmRelaxOpen(false);
    setNotice(null);
    setError(null);
  }, [open, initialTab, currentBackend, currentMode, modelSettings, browserSettings, computerSettings]);

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
                ['browser', language === 'en-US' ? 'Browser' : '浏览器'],
                ['computer', t('settings.tab.computer')],
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
            <EnvironmentCheckPanel
              active={open && tab === 'environment'}
              computerDriverStatus={computerSettings?.driverStatus ?? null}
            />
          )}

          {tab === 'computer' && (
            <form
              data-testid="settings-computer-form"
              onSubmit={async (e) => {
                e.preventDefault();
                if (!controlPlaneAvailable || !window.codexflow) return;
                setSaving(true);
                setError(null);
                setNotice(null);
                try {
                  const parsedLongEdge = Number.parseInt(computerMaxLongEdge, 10);
                  const parsedMaxShots = Number.parseInt(computerMaxScreenshots, 10);
                  const parsedIdle = Number.parseInt(computerIdleMinutes, 10);
                  const blockedList = computerBlockedProcessesText
                    .split(/[\r\n,，;；]+/)
                    .map((entry) => entry.trim())
                    .filter(Boolean);
                  const shortcutTrimmed = computerStopShortcut.trim() || 'CommandOrControl+.';
                  const res = await window.codexflow.updateComputerSettings({
                    enabled: computerEnabled,
                    interactionApprovalPolicy: computerApprovalPolicy,
                    coordinateMode: computerCoordinateMode,
                    maxImageLongEdge: Number.isFinite(parsedLongEdge)
                      ? Math.min(2560, Math.max(640, parsedLongEdge))
                      : 1280,
                    maxScreenshotsPerTurn: Number.isFinite(parsedMaxShots)
                      ? Math.min(30, Math.max(1, parsedMaxShots))
                      : 10,
                    enableGridOverlay: computerEnableGrid,
                    autoMinimizeHostWindow: computerAutoMinimizeHost,
                    blockedProcessNames: blockedList,
                    emergencyStopShortcut: shortcutTrimmed,
                    idleTimeoutMinutes: Number.isFinite(parsedIdle)
                      ? Math.min(120, Math.max(1, parsedIdle))
                      : 15,
                  });
                  if (!res.ok) {
                    setError(res.errorMessage || `保存桌面操控设置失败 (${res.status})`);
                    return;
                  }
                  setNotice('桌面操控（Computer Use）设置已保存并立即生效。');
                  onUpdated();
                } catch (err) {
                  setError(err instanceof Error ? err.message : '保存桌面操控设置失败');
                } finally {
                  setSaving(false);
                }
              }}
              className="mt-4 space-y-4 text-xs"
            >
              <div className="rounded-xl border border-slate-800 bg-slate-950 p-3.5 space-y-2">
                <div className="flex items-center justify-between gap-2">
                  <div className="font-medium text-slate-200">本机原生驱动与系统权限状态</div>
                  {computerSettings?.driverStatus && (
                    <span className="rounded bg-slate-900 px-2 py-0.5 font-mono text-[10px] text-slate-300">
                      {computerSettings.driverStatus.platform} · {computerSettings.driverStatus.driverName}
                    </span>
                  )}
                </div>
                {computerSettings?.driverStatus ? (
                  <div className="space-y-1.5 text-slate-300">
                    <div className="flex flex-wrap items-center gap-3">
                      <div className="flex items-center gap-1.5">
                        <span
                          className={`h-2 w-2 rounded-full ${
                            computerSettings.driverStatus.available ? 'bg-emerald-400' : 'bg-amber-400'
                          }`}
                        />
                        <span
                          className={`font-medium ${
                            computerSettings.driverStatus.available ? 'text-emerald-300' : 'text-amber-300'
                          }`}
                        >
                          {computerSettings.driverStatus.available
                            ? `驱动就绪（检测到 ${computerSettings.driverStatus.displayCount} 个显示器）`
                            : '驱动不可用或缺少系统授权'}
                        </span>
                      </div>
                      <span className="text-[11px] text-slate-400">
                        屏幕录制：{computerSettings.driverStatus.screenCaptureGranted ? '已授权' : '未授权'} · 辅助功能：
                        {computerSettings.driverStatus.accessibilityGranted ? '已授权' : '未授权'}
                      </span>
                    </div>
                    {computerSettings.driverStatus.diagnosticMessage && (
                      <div className="text-[11px] text-slate-400">
                        {computerSettings.driverStatus.diagnosticMessage}
                      </div>
                    )}
                    {computerSettings.driverStatus.permissionGuide && (
                      <div className="rounded-lg border border-amber-500/40 bg-amber-950/30 px-3 py-2 text-[11px] text-amber-200">
                        {computerSettings.driverStatus.permissionGuide}
                      </div>
                    )}
                  </div>
                ) : (
                  <div className="text-slate-400">正在等待宿主返回桌面驱动状态…</div>
                )}
              </div>

              <label className="flex cursor-pointer items-center justify-between gap-3 rounded-xl border border-slate-800 bg-slate-950 p-3.5">
                <div>
                  <div className="font-medium text-slate-200">启用本机桌面视觉操控（Computer Use）</div>
                  <div className="mt-0.5 text-[11px] text-slate-400">
                    开启后，助手可在日常模式和开发模式下（需多模态模型）观测桌面并执行受控键鼠交互，支持物理 Esc×2 与全局热键急停。
                  </div>
                </div>
                <input
                  type="checkbox"
                  data-testid="computer-enabled-toggle"
                  checked={computerEnabled}
                  onChange={(e) => setComputerEnabled(e.target.checked)}
                  className="h-4 w-4 rounded border-slate-700 accent-indigo-600"
                />
              </label>

              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                <label className="block">
                  <span className="mb-1 block text-slate-300">桌面交互审批策略</span>
                  <select
                    data-testid="computer-approval-policy-select"
                    value={computerApprovalPolicy}
                    onChange={(e) => setComputerApprovalPolicy(e.target.value)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  >
                    <option value="HighRiskOnly">HighRiskOnly（文本输入、组合键和拖拽需审批）</option>
                    <option value="AlwaysAsk">AlwaysAsk（每次键鼠动作均弹窗询问）</option>
                    <option value="AppScopedInSession">AppScopedInSession（本会话按应用进程授信）</option>
                  </select>
                  <span className="mt-1 block text-xs text-slate-400">
                    受管桌面使用 FullAuto 时自动放行常规键鼠动作。Deny 禁止键鼠输入。
                  </span>
                </label>

                <label className="block">
                  <span className="mb-1 block text-slate-300">默认坐标系模式</span>
                  <select
                    data-testid="computer-coordinate-mode-select"
                    value={computerCoordinateMode}
                    onChange={(e) => setComputerCoordinateMode(e.target.value)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  >
                    <option value="Auto">Auto（按屏幕缩放图自动适配像素）</option>
                    <option value="ScreenshotPixels">ScreenshotPixels（截图像素坐标）</option>
                    <option value="Normalized1000">Normalized1000（0-1000 归一化坐标）</option>
                  </select>
                </label>
              </div>

              <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
                <label className="block">
                  <span className="mb-1 block text-slate-300">图像长边上限 (px)</span>
                  <input
                    type="number"
                    min={640}
                    max={2560}
                    value={computerMaxLongEdge}
                    onChange={(e) => setComputerMaxLongEdge(e.target.value)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  />
                </label>
                <label className="block">
                  <span className="mb-1 block text-slate-300">单轮截屏上限</span>
                  <input
                    type="number"
                    min={1}
                    max={30}
                    value={computerMaxScreenshots}
                    onChange={(e) => setComputerMaxScreenshots(e.target.value)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  />
                </label>
                <label className="block">
                  <span className="mb-1 block text-slate-300">空闲超时 (分钟)</span>
                  <input
                    type="number"
                    min={1}
                    max={120}
                    value={computerIdleMinutes}
                    onChange={(e) => setComputerIdleMinutes(e.target.value)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  />
                </label>
                <label className="block">
                  <span className="mb-1 block text-slate-300">操控期急停热键</span>
                  <input
                    type="text"
                    value={computerStopShortcut}
                    onChange={(e) => setComputerStopShortcut(e.target.value)}
                    placeholder="CommandOrControl+."
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 font-mono text-slate-100"
                  />
                </label>
              </div>

              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                <label className="flex cursor-pointer items-center justify-between gap-3 rounded-xl border border-slate-800 bg-slate-950 p-3">
                  <div>
                    <div className="font-medium text-slate-200">坐标网格标尺与十字准星辅助</div>
                    <div className="mt-0.5 text-[11px] text-slate-400">
                      在截图上绘制像素/归一化标尺与当前指针双色准星，提高视觉点击精度。
                    </div>
                  </div>
                  <input
                    type="checkbox"
                    checked={computerEnableGrid}
                    onChange={(e) => setComputerEnableGrid(e.target.checked)}
                    className="h-4 w-4 rounded border-slate-700 accent-indigo-600"
                  />
                </label>

                <label className="flex cursor-pointer items-center justify-between gap-3 rounded-xl border border-slate-800 bg-slate-950 p-3">
                  <div>
                    <div className="font-medium text-slate-200">操控期自动避让最小化主窗口</div>
                    <div className="mt-0.5 text-[11px] text-slate-400">
                      开始执行全屏或桌面操控时自动最小化宿主窗口，审批或结束时自动恢复。
                    </div>
                  </div>
                  <input
                    type="checkbox"
                    checked={computerAutoMinimizeHost}
                    onChange={(e) => setComputerAutoMinimizeHost(e.target.checked)}
                    className="h-4 w-4 rounded border-slate-700 accent-indigo-600"
                  />
                </label>
              </div>

              <label className="block">
                <div className="mb-1 flex items-center justify-between text-slate-300">
                  <span>屏蔽高敏进程黑名单（Blocked Processes）</span>
                  <span className="text-[10px] text-slate-500">
                    {computerBlockedProcessesText.split(/[\r\n,，;；]+/).filter((s) => s.trim()).length} 项
                  </span>
                </div>
                <textarea
                  rows={3}
                  data-testid="computer-blocked-processes-input"
                  value={computerBlockedProcessesText}
                  onChange={(e) => setComputerBlockedProcessesText(e.target.value)}
                  placeholder={"每行或逗号分隔，命中进程严禁视觉点击/输入\n例如 1password\nkeepass\nbitwarden"}
                  className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 font-mono text-[11px] text-slate-100 placeholder:text-slate-600"
                />
              </label>

              {error && <div className="rounded-lg border border-rose-500/40 bg-rose-950/40 p-2.5 text-rose-200">{error}</div>}
              {notice && <div className="rounded-lg border border-emerald-500/40 bg-emerald-950/40 p-2.5 text-emerald-200">{notice}</div>}

              <div className="flex justify-end">
                <button
                  type="submit"
                  data-testid="computer-save-settings-btn"
                  disabled={!controlPlaneAvailable || saving}
                  className="rounded-lg bg-indigo-600 px-4 py-2 font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
                >
                  {saving ? '保存中…' : '保存桌面操控设置'}
                </button>
              </div>
            </form>
          )}

          {tab === 'browser' && (
            <form
              onSubmit={async (e) => {
                e.preventDefault();
                if (!controlPlaneAvailable || !window.codexflow) return;
                setSaving(true);
                setError(null);
                setNotice(null);
                try {
                  const parsedIdle = Number.parseInt(browserIdleMinutes, 10);
                  const parsedMax = Number.parseInt(browserMaxSessions, 10);
                  const parseDomains = (raw: string): string[] =>
                    raw
                      .split(/[\r\n,，;；]+/)
                      .map((entry) => entry.trim())
                      .filter(Boolean);
                  const res = await window.codexflow.updateBrowserSettings({
                    enabled: browserEnabled,
                    executablePath: browserExecutablePath.trim(),
                    remoteCdpWsUrl: browserRemoteCdpTouched ? browserRemoteCdpWsUrl.trim() : undefined,
                    headless: browserHeadless,
                    profileMode: browserProfileMode,
                    interactionApprovalPolicy: browserApprovalPolicy,
                    allowLocalhostInDailyMode: browserAllowLocalhostDaily,
                    allowControlledDownloads: browserAllowDownloads,
                    allowedDomains: parseDomains(browserAllowedDomainsText),
                    blockedDomains: parseDomains(browserBlockedDomainsText),
                    idleTimeoutMinutes: Number.isFinite(parsedIdle) ? Math.min(120, Math.max(1, parsedIdle)) : 15,
                    maxConcurrentSessions: Number.isFinite(parsedMax) ? Math.min(8, Math.max(1, parsedMax)) : 3,
                  });
                  if (!res.ok) {
                    setError(res.errorMessage || `保存浏览器设置失败 (${res.status})`);
                    return;
                  }
                  setNotice('浏览器设置已保存（审批与下载策略立即生效；若当前已有运行中的浏览器会话，窗口模式与 Profile 模式将在关闭该浏览器会话后生效）。');
                  onUpdated();
                } catch (err) {
                  setError(err instanceof Error ? err.message : '保存浏览器设置失败');
                } finally {
                  setSaving(false);
                }
              }}
              className="mt-4 space-y-4 text-xs"
            >
              <div className="rounded-xl border border-slate-800 bg-slate-950 p-3.5 space-y-2">
                <div className="font-medium text-slate-200">本机浏览器自动探测</div>
                {browserSettings?.detectedBrowser?.available ? (
                  <div className="space-y-1 text-slate-300">
                    <div className="flex items-center gap-2">
                      <span className="h-2 w-2 rounded-full bg-emerald-400" />
                      <span className="font-medium text-emerald-300">
                        已就绪：{browserSettings.detectedBrowser.browserName}
                        {browserSettings.detectedBrowser.version ? ` (${browserSettings.detectedBrowser.version})` : ''}
                      </span>
                    </div>
                    <div className="truncate font-mono text-[11px] text-slate-400">
                      {browserSettings.detectedBrowser.executablePath}
                    </div>
                  </div>
                ) : (
                  <div className="text-amber-300">
                    {browserSettings?.detectedBrowser?.diagnosticMessage ||
                      '未在可信系统目录检测到 Chrome / Edge / Chromium，可安装任一浏览器或手动填写可执行路径。'}
                  </div>
                )}
              </div>

              <label className="flex cursor-pointer items-center justify-between gap-3 rounded-xl border border-slate-800 bg-slate-950 p-3.5">
                <div>
                  <div className="font-medium text-slate-200">启用内置 Web Browser Use</div>
                  <div className="mt-0.5 text-[11px] text-slate-400">
                    开启后，助手可在开发模式与日常模式下通过隔离浏览器渲染页面、采集控制台日志并执行受控交互。
                  </div>
                </div>
                <input
                  type="checkbox"
                  data-testid="browser-enabled-toggle"
                  checked={browserEnabled}
                  onChange={(e) => setBrowserEnabled(e.target.checked)}
                  className="h-4 w-4 rounded border-slate-700 accent-indigo-600"
                />
              </label>

              <label className="block">
                <span className="mb-1 block text-slate-300">自定义浏览器路径（留空则自动探测可信 Chrome / Edge）</span>
                <input
                  type="text"
                  value={browserExecutablePath}
                  onChange={(e) => setBrowserExecutablePath(e.target.value)}
                  placeholder="例如 C:\Program Files\Google\Chrome\Application\chrome.exe"
                  className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 font-mono text-slate-100 placeholder:text-slate-600"
                />
              </label>

              <div className="rounded-xl border border-slate-800 bg-slate-950 p-3.5 space-y-2">
                <div className="flex items-center justify-between gap-2">
                  <span className="font-medium text-slate-200">
                    远端 CDP Grid 端点（可选，留空使用本机受管浏览器）
                  </span>
                  {browserSettings?.isRemoteCdpConfigured && !browserRemoteCdpTouched && (
                    <span className="rounded-full bg-indigo-500/20 px-2 py-0.5 text-[10px] text-indigo-300">
                      已配置（凭据已掩码保护）
                    </span>
                  )}
                </div>
                <div className="flex items-center gap-2">
                  <input
                    type="text"
                    data-testid="browser-remote-cdp-input"
                    value={browserRemoteCdpWsUrl}
                    onChange={(e) => {
                      setBrowserRemoteCdpWsUrl(e.target.value);
                      setBrowserRemoteCdpTouched(true);
                    }}
                    placeholder="wss://grid.example.com/devtools/browser/..."
                    className="flex-1 rounded-lg border border-slate-700 bg-slate-900 px-3 py-2 font-mono text-slate-100 placeholder:text-slate-600"
                  />
                  {(browserRemoteCdpWsUrl || browserSettings?.isRemoteCdpConfigured) && (
                    <button
                      type="button"
                      data-testid="browser-clear-remote-cdp-btn"
                      onClick={() => {
                        setBrowserRemoteCdpWsUrl('');
                        setBrowserRemoteCdpTouched(true);
                      }}
                      className="rounded-lg border border-slate-700 bg-slate-900 px-2.5 py-2 text-[11px] text-slate-300 hover:bg-slate-800"
                    >
                      清空切回本机
                    </button>
                  )}
                </div>
                <div className="text-[11px] text-slate-400">
                  仅支持直连 <code className="rounded bg-slate-900 px-1">ws://</code> 或 <code className="rounded bg-slate-900 px-1">wss://</code>；远端模式下禁用本机人工接管、文件上传及工作区下载。
                </div>
              </div>

              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                <label className="block">
                  <div className="mb-1 flex items-center justify-between text-slate-300">
                    <span>允许域名白名单（Allowed Domains）</span>
                    <span className="text-[10px] text-slate-500">
                      {browserAllowedDomainsText.split(/[\r\n,，;；]+/).filter((s) => s.trim()).length} 项
                    </span>
                  </div>
                  <textarea
                    rows={3}
                    data-testid="browser-allowed-domains-input"
                    value={browserAllowedDomainsText}
                    onChange={(e) => setBrowserAllowedDomainsText(e.target.value)}
                    placeholder={"留空不限公网；每行或逗号分隔\n例如 example.com\n*.corp.internal"}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 font-mono text-[11px] text-slate-100 placeholder:text-slate-600"
                  />
                </label>

                <label className="block">
                  <div className="mb-1 flex items-center justify-between text-slate-300">
                    <span>禁止域名黑名单（Blocked Domains）</span>
                    <span className="text-[10px] text-slate-500">
                      {browserBlockedDomainsText.split(/[\r\n,，;；]+/).filter((s) => s.trim()).length} 项
                    </span>
                  </div>
                  <textarea
                    rows={3}
                    data-testid="browser-blocked-domains-input"
                    value={browserBlockedDomainsText}
                    onChange={(e) => setBrowserBlockedDomainsText(e.target.value)}
                    placeholder={"命中优先拦截；每行或逗号分隔\n例如 ads.example.com\n*.untrusted.test"}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 font-mono text-[11px] text-slate-100 placeholder:text-slate-600"
                  />
                </label>
              </div>

              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                <label className="block">
                  <span className="mb-1 block text-slate-300">运行窗口模式</span>
                  <select
                    data-testid="browser-headless-select"
                    value={browserHeadless ? 'headless' : 'headed'}
                    onChange={(e) => setBrowserHeadless(e.target.value === 'headless')}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  >
                    <option value="headless">无头后台（默认，零打扰）</option>
                    <option value="headed">桌面离屏有头（支持无损人工接管登录/验证码）</option>
                  </select>
                </label>

                <label className="block">
                  <span className="mb-1 block text-slate-300">登录态与 Profile 策略</span>
                  <select
                    data-testid="browser-profile-select"
                    value={browserProfileMode}
                    onChange={(e) => setBrowserProfileMode(e.target.value)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  >
                    <option value="Ephemeral">会话隔离临时 Profile（默认，关闭即销毁）</option>
                    <option value="Persistent">专用目录持久化登录态（单活互斥，非本地写操作强审批）</option>
                  </select>
                </label>
              </div>

              {browserProfileMode === 'Persistent' && (
                <div className="rounded-lg border border-amber-500/40 bg-amber-950/30 px-3 py-2 text-[11px] text-amber-200">
                  持久化 Profile 存储于专用的 <code className="rounded bg-slate-900 px-1">~/.codexflow/browser-profiles/default</code>（不读取您日常浏览器数据）。同一时间仅允许 1 个活跃会话占用，且对所有非 localhost 网站的点击/输入/上传均强制要求单次人工审批。
                </div>
              )}

              <label className="block">
                <span className="mb-1 block text-slate-300">页面交互审批策略</span>
                <select
                  value={browserApprovalPolicy}
                  onChange={(e) => setBrowserApprovalPolicy(e.target.value)}
                  className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                >
                  <option value="HighRiskOnly">HighRiskOnly（默认：仅高风险提交/非白名单本地端口审批）</option>
                  <option value="AlwaysAsk">AlwaysAsk（每次点击与输入均询问审批）</option>
                  <option value="AutoInSession">AutoInSession（常规交互自动放行，敏感提交仍强制审批）</option>
                </select>
              </label>

              <label className="flex cursor-pointer items-center justify-between gap-3 rounded-xl border border-slate-800 bg-slate-950 p-3.5">
                <div>
                  <div className="font-medium text-slate-200">日常模式允许访问 localhost / 127.0.0.1</div>
                  <div className="mt-0.5 text-[11px] text-slate-400">
                    默认关闭（CodexFlow 受保护控制端口始终禁止访问）。开发模式已自动支持白名单前端开发端口。
                  </div>
                </div>
                <input
                  type="checkbox"
                  checked={browserAllowLocalhostDaily}
                  onChange={(e) => setBrowserAllowLocalhostDaily(e.target.checked)}
                  className="h-4 w-4 rounded border-slate-700 accent-indigo-600"
                />
              </label>

              <label className="flex cursor-pointer items-center justify-between gap-3 rounded-xl border border-slate-800 bg-slate-950 p-3.5">
                <div>
                  <div className="font-medium text-slate-200">允许受控文件下载至工作区</div>
                  <div className="mt-0.5 text-[11px] text-slate-400">
                    默认拒绝所有浏览器下载；开启后仅重定向到当前会话工作区的 <code className="rounded bg-slate-900 px-1">outputs/browser-downloads/</code> 目录。
                  </div>
                </div>
                <input
                  type="checkbox"
                  data-testid="browser-downloads-toggle"
                  checked={browserAllowDownloads}
                  onChange={(e) => setBrowserAllowDownloads(e.target.checked)}
                  className="h-4 w-4 rounded border-slate-700 accent-indigo-600"
                />
              </label>

              <div className="grid grid-cols-2 gap-3">
                <label className="block">
                  <span className="mb-1 block text-slate-300">空闲自动回收（分钟，1-120）</span>
                  <input
                    type="number"
                    min={1}
                    max={120}
                    value={browserIdleMinutes}
                    onChange={(e) => setBrowserIdleMinutes(e.target.value)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  />
                </label>
                <label className="block">
                  <span className="mb-1 block text-slate-300">最大并发会话数（1-8）</span>
                  <input
                    type="number"
                    min={1}
                    max={8}
                    value={browserMaxSessions}
                    onChange={(e) => setBrowserMaxSessions(e.target.value)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  />
                </label>
              </div>

              {error && <div className="rounded-lg border border-rose-500/40 bg-rose-950/40 p-2.5 text-rose-200">{error}</div>}
              {notice && <div className="rounded-lg border border-emerald-500/40 bg-emerald-950/40 p-2.5 text-emerald-200">{notice}</div>}

              <div className="flex justify-end">
                <button
                  type="submit"
                  disabled={!controlPlaneAvailable || saving}
                  className="rounded-lg bg-indigo-600 px-4 py-2 font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
                >
                  {saving ? '保存中…' : '保存浏览器设置'}
                </button>
              </div>
            </form>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
};
