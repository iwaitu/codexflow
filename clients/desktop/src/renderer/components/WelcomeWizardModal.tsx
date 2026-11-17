import React, { useEffect, useRef, useState } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { CheckCircle2, Link2, ShieldAlert, X } from 'lucide-react';
import type { ExecutionBackendKind, ExecutionPermissionModeKind } from '@shared/ipc';
import type { ModelSettings, SystemStatusSummary } from '../lib/apiClient';
import { isExecutionRiskIncreasing } from '../lib/executionPolicy';
import { DEFAULT_MAX_CONTEXT_TOKENS, parseModelSettingsInput } from '../lib/modelSettingsForm';
import { CodexFlowBrand } from './CodexFlowBrand';
import { MaxContextField } from './MaxContextField';

interface WelcomeWizardModalProps {
  open: boolean;
  systemStatus: SystemStatusSummary | null;
  modelSettings?: ModelSettings | null;
  controlPlaneAvailable: boolean;
  onClose: () => void;
  onSaved: () => void;
}

export const WelcomeWizardModal: React.FC<WelcomeWizardModalProps> = ({
  open,
  systemStatus,
  modelSettings = null,
  controlPlaneAvailable,
  onClose,
  onSaved,
}) => {
  const [tab, setTab] = useState<'managed' | 'standalone'>('managed');
  const [apiUrl, setApiUrl] = useState(modelSettings?.apiUrl ?? '');
  const [apiMode, setApiMode] = useState(modelSettings?.apiMode ?? 'ChatCompletions');
  const [model, setModel] = useState(modelSettings?.model ?? '');
  const [maxTokensLength, setMaxTokensLength] = useState(
    String(modelSettings?.maxTokensLength ?? DEFAULT_MAX_CONTEXT_TOKENS),
  );
  const [apiKey, setApiKey] = useState('');
  const [supportsMultimodal, setSupportsMultimodal] = useState(Boolean(modelSettings?.supportsMultimodal));
  const currentBackend = (systemStatus?.executionBackend as ExecutionBackendKind) || 'Local';
  const currentPermissionMode = (systemStatus?.permissionMode as ExecutionPermissionModeKind) || 'Ask';
  const [backend, setBackend] = useState<ExecutionBackendKind>(currentBackend);
  const [permissionMode, setPermissionMode] = useState<ExecutionPermissionModeKind>(currentPermissionMode);
  const [confirmRiskOpen, setConfirmRiskOpen] = useState(false);
  const [riskReasons, setRiskReasons] = useState<string[]>([]);

  const [standaloneUrl, setStandaloneUrl] = useState('http://127.0.0.1:5206');
  const [discoveryHandle, setDiscoveryHandle] = useState<string | undefined>(undefined);
  const [accessTokenHandle, setAccessTokenHandle] = useState<string | undefined>(undefined);
  const [accessTokenDisplay, setAccessTokenDisplay] = useState('');
  const [accessTokenManual, setAccessTokenManual] = useState('');
  const [controlTokenHandle, setControlTokenHandle] = useState<string | undefined>(undefined);
  const [controlTokenDisplay, setControlTokenDisplay] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const wasOpenRef = useRef(false);
  useEffect(() => {
    const justOpened = open && !wasOpenRef.current;
    wasOpenRef.current = open;
    if (!justOpened) return;
    setApiUrl(modelSettings?.apiUrl ?? '');
    setApiMode(modelSettings?.apiMode ?? 'ChatCompletions');
    setModel(modelSettings?.model ?? '');
    setMaxTokensLength(String(modelSettings?.maxTokensLength ?? DEFAULT_MAX_CONTEXT_TOKENS));
    setApiKey('');
    setSupportsMultimodal(Boolean(modelSettings?.supportsMultimodal));
    setBackend(currentBackend);
    setPermissionMode(currentPermissionMode);
    setConfirmRiskOpen(false);
    setError(null);
  }, [open, modelSettings, currentBackend, currentPermissionMode]);

  const executeSaveManaged = async () => {
    const parsed = parseModelSettingsInput({
      apiUrl,
      apiMode,
      model,
      maxTokensLength,
      apiKey,
      ...(supportsMultimodal !== Boolean(modelSettings?.supportsMultimodal)
        ? { supportsMultimodal }
        : {}),
    });
    if (!parsed.ok) {
      setError(parsed.error);
      setConfirmRiskOpen(false);
      return;
    }

    setSaving(true);
    setError(null);
    setConfirmRiskOpen(false);
    try {
      if (parsed.hasAnyChange) {
        const modelRes = await window.codexflow.updateModelSettings(parsed.payload);
        if (!modelRes.ok) {
          setError(modelRes.errorMessage || '保存模型设置失败。');
          setSaving(false);
          return;
        }
        if (typeof modelRes.data?.supportsMultimodal === 'boolean') {
          setSupportsMultimodal(modelRes.data.supportsMultimodal);
        }
      }

      const execRes = await window.codexflow.updateExecutionSettings({ backend, permissionMode });
      if (!execRes.ok) {
        setError(execRes.errorMessage || '保存执行后端设置失败。');
        setSaving(false);
        return;
      }

      onSaved();
      onClose();
    } finally {
      setSaving(false);
    }
  };

  const handleSaveManaged = (e: React.FormEvent) => {
    e.preventDefault();
    const parsed = parseModelSettingsInput({
      apiUrl,
      apiMode,
      model,
      maxTokensLength,
      apiKey,
      ...(supportsMultimodal !== Boolean(modelSettings?.supportsMultimodal)
        ? { supportsMultimodal }
        : {}),
    });
    if (!parsed.ok) {
      setError(parsed.error);
      return;
    }
    setError(null);
    const { risky, reasons } = isExecutionRiskIncreasing(
      currentBackend,
      currentPermissionMode,
      backend,
      permissionMode,
    );
    if (risky) {
      setRiskReasons(reasons);
      setConfirmRiskOpen(true);
      return;
    }
    void executeSaveManaged();
  };

  const handleDiscover = async () => {
    setError(null);
    const found = await window.codexflow.discoverStandaloneInstance();
    if (!found) {
      setError('未在默认目录发现正在运行的 CodexFlow 实例，请选择 token 文件或输入访问令牌。');
      return;
    }
    setStandaloneUrl(found.baseUrl);
    setDiscoveryHandle(found.discoveryHandle);
    setAccessTokenHandle(undefined);
    setAccessTokenManual('');
    setAccessTokenDisplay(found.hasAccessToken ? `${found.instanceId} (已发现 host.token)` : '');
    setControlTokenHandle(undefined);
    setControlTokenDisplay(found.hasControlToken ? `${found.instanceId} (已发现 control.token)` : '');
  };

  const handlePickToken = async (kind: 'access' | 'control') => {
    setError(null);
    try {
      const picked = await window.codexflow.pickStandaloneTokenFile(kind);
      if (!picked) return;
      setDiscoveryHandle(undefined);
      if (kind === 'access') {
        setAccessTokenHandle(picked.handle);
        setAccessTokenDisplay(`已选择文件: ${picked.displayName}`);
        setAccessTokenManual('');
      } else {
        setControlTokenHandle(picked.handle);
        setControlTokenDisplay(`已选择文件: ${picked.displayName}`);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  };

  const handleConnectStandalone = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      const info = await window.codexflow.connectStandalone({
        baseUrl: standaloneUrl.trim(),
        discoveryHandle,
        accessTokenHandle,
        accessToken: accessTokenManual.trim() || undefined,
        controlTokenHandle,
      });
      if (info.status === 'error') {
        setError(info.lastError || '连接失败');
        return;
      }
      onSaved();
      onClose();
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog.Root open={open} onOpenChange={(v) => (!v ? onClose() : undefined)}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/70 backdrop-blur-sm" />
        <Dialog.Content className="fixed left-1/2 top-1/2 z-50 max-h-[90vh] w-full max-w-xl -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-2xl border border-slate-800 bg-slate-900 p-6 shadow-2xl">
          <div className="flex items-center justify-between">
            <Dialog.Title className="flex items-center gap-2 text-base font-semibold text-slate-100">
              <CodexFlowBrand showText={false} iconClassName="h-6 w-6" />
              首次运行向导
            </Dialog.Title>
            <button
              type="button"
              onClick={onClose}
              className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
            >
              <X className="h-4 w-4" />
            </button>
          </div>

          <div className="mt-4 flex gap-2 border-b border-slate-800 pb-3">
            <button
              type="button"
              onClick={() => setTab('managed')}
              className={`rounded-lg px-3 py-1.5 text-xs font-medium ${
                tab === 'managed' ? 'bg-indigo-600 text-white' : 'bg-slate-800 text-slate-300'
              }`}
            >
              托管模式配置
            </button>
            <button
              type="button"
              onClick={() => setTab('standalone')}
              className={`inline-flex items-center gap-1 rounded-lg px-3 py-1.5 text-xs font-medium ${
                tab === 'standalone' ? 'bg-indigo-600 text-white' : 'bg-slate-800 text-slate-300'
              }`}
            >
              <Link2 className="h-3.5 w-3.5" />
              连接已运行实例
            </button>
          </div>

          {tab === 'managed' ? (
            <form noValidate onSubmit={(e) => void handleSaveManaged(e)} className="mt-4 space-y-4 text-xs">
              <div className="rounded-lg border border-slate-800 bg-slate-950/70 p-3">
                <div className="flex items-center justify-between">
                  <span className="text-slate-300">
                    模型状态：{systemStatus?.modelConfigured ? '已配置' : '未配置'}{' '}
                    {systemStatus?.modelReachable ? '（接口可达）' : ''}
                  </span>
                  <span className="inline-flex items-center gap-1 text-slate-300">
                    <CheckCircle2
                      className={`h-3.5 w-3.5 ${systemStatus?.dockerAvailable ? 'text-emerald-400' : 'text-slate-500'}`}
                    />
                    Docker: {systemStatus?.dockerAvailable ? '可用（可选）' : '不可用（已启用本地模式）'}
                  </span>
                </div>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <label className="col-span-2 block">
                  <span className="mb-1 block text-slate-300">模型接口地址 (ApiUrl)</span>
                  <input
                    value={apiUrl}
                    onChange={(e) => setApiUrl(e.target.value)}
                    placeholder="https://your-model-host/v1/{1}"
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 focus:border-indigo-500 focus:outline-none"
                  />
                </label>

                <label className="block">
                  <span className="mb-1 block text-slate-300">模型名称 (Model)</span>
                  <input
                    value={model}
                    onChange={(e) => setModel(e.target.value)}
                    placeholder="qwen3.8-27b-fp8"
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 focus:border-indigo-500 focus:outline-none"
                  />
                </label>

                <label className="block">
                  <span className="mb-1 block text-slate-300">接口模式 (ApiMode)</span>
                  <select
                    value={apiMode}
                    onChange={(e) => setApiMode(e.target.value)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 focus:outline-none"
                  >
                    <option value="ChatCompletions">ChatCompletions</option>
                    <option value="Responses">Responses</option>
                  </select>
                </label>

                <div className="col-span-2">
                  <MaxContextField
                    value={maxTokensLength}
                    onChange={setMaxTokensLength}
                  />
                </div>

                <label className="col-span-2 block">
                  <span className="mb-1 block text-slate-300">API Key（仅写入本地配置，不回显完整密钥）</span>
                  <input
                    type="password"
                    value={apiKey}
                    onChange={(e) => setApiKey(e.target.value)}
                    placeholder="sk-..."
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 focus:border-indigo-500 focus:outline-none"
                  />
                </label>

                <label className="col-span-2 flex items-start gap-2 rounded-lg border border-slate-800 bg-slate-950/60 p-3">
                  <input
                    type="checkbox"
                    checked={supportsMultimodal}
                    onChange={(e) => setSupportsMultimodal(e.target.checked)}
                    className="mt-0.5 h-3.5 w-3.5 rounded border-slate-600 bg-slate-900 text-indigo-500"
                  />
                  <span className="space-y-0.5 text-slate-300">
                    <span className="block font-medium text-slate-200">支持多模态（图片与扫描版 PDF 识别）</span>
                    <span className="block text-[11px] text-slate-400">
                      保存时会自动用内置测试图片检测当前模型是否支持视觉输入，检测通过将自动开启。
                    </span>
                  </span>
                </label>

                <label className="block">
                  <span className="mb-1 block text-slate-300">执行后端</span>
                  <select
                    value={backend}
                    onChange={(e) => setBackend(e.target.value as ExecutionBackendKind)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 focus:outline-none"
                  >
                    <option value="Local">Local（默认，无需 Docker）</option>
                    <option value="Docker" disabled={!systemStatus?.dockerAvailable}>
                      Docker（容器隔离{systemStatus?.dockerAvailable ? '' : ' - 不可用'}）
                    </option>
                  </select>
                </label>

                <label className="block">
                  <span className="mb-1 block text-slate-300">权限模式</span>
                  <select
                    value={permissionMode}
                    onChange={(e) => setPermissionMode(e.target.value as ExecutionPermissionModeKind)}
                    className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 focus:outline-none"
                  >
                    <option value="Ask">Ask（每次询问 - 推荐）</option>
                    <option value="AutoInWorkspace">AutoInWorkspace（仅放行只读 git/rg）</option>
                    <option value="FullAuto">FullAuto（全部自动放行）</option>
                    <option value="Deny">Deny（只读分析，禁止执行）</option>
                  </select>
                </label>
              </div>

              {backend === 'Local' && (
                <div className="flex items-start gap-2 rounded-lg border border-amber-500/30 bg-amber-500/10 p-3 text-amber-200">
                  <ShieldAlert className="mt-0.5 h-4 w-4 shrink-0 text-amber-400" />
                  <div>
                    <strong>本地执行边界说明：</strong>
                    本地执行 = 以你的用户权限直接运行，没有文件系统隔离和网络隔离；安全边界建立在“未经批准不启动（Ask）”与“模型无法自批”之上，容器验证也将默认跳过并标注“未经隔离验证”。
                  </div>
                </div>
              )}

              {confirmRiskOpen && (
                <div
                  data-testid="wizard-risk-confirm"
                  className="rounded-xl border border-amber-500/50 bg-amber-950/30 p-3 text-amber-100"
                >
                  <div className="font-semibold">确认放宽执行或隔离策略？</div>
                  <ul className="mt-1 list-disc space-y-0.5 pl-4 text-amber-200/90">
                    {riskReasons.map((reason) => (
                      <li key={reason}>{reason}</li>
                    ))}
                  </ul>
                  <div className="mt-2 flex gap-2">
                    <button
                      type="button"
                      disabled={saving}
                      onClick={() => void executeSaveManaged()}
                      className="rounded-lg bg-amber-600 px-3 py-1 font-medium text-white hover:bg-amber-500"
                    >
                      确认放宽并保存
                    </button>
                    <button
                      type="button"
                      onClick={() => setConfirmRiskOpen(false)}
                      className="rounded-lg border border-slate-700 px-3 py-1 text-slate-300 hover:bg-slate-800"
                    >
                      取消
                    </button>
                  </div>
                </div>
              )}

              {error && <div className="text-rose-300">{error}</div>}

              <div className="flex justify-end gap-2 pt-2">
                <button
                  type="button"
                  onClick={onClose}
                  className="rounded-lg border border-slate-700 px-3 py-1.5 text-slate-300 hover:bg-slate-800"
                >
                  稍后配置
                </button>
                <button
                  type="submit"
                  disabled={saving || !controlPlaneAvailable}
                  className="rounded-lg bg-indigo-600 px-4 py-1.5 font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
                >
                  {saving ? '保存中...' : '保存并开始'}
                </button>
              </div>
            </form>
          ) : (
            <form onSubmit={(e) => void handleConnectStandalone(e)} className="mt-4 space-y-3 text-xs">
              <div className="flex items-center justify-between">
                <span className="text-slate-400">通过发现文件或手动指定正在运行的 CodexFlow.Local 地址：</span>
                <button
                  type="button"
                  onClick={() => void handleDiscover()}
                  className="rounded bg-slate-800 px-2.5 py-1 text-slate-200 hover:bg-slate-700"
                >
                  自动探测本机实例
                </button>
              </div>

              <label className="block">
                <span className="mb-1 block text-slate-300">后端地址</span>
                <input
                  value={standaloneUrl}
                  onChange={(e) => {
                    setStandaloneUrl(e.target.value);
                    setDiscoveryHandle(undefined);
                  }}
                  className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                />
              </label>

              <div className="block">
                <span className="mb-1 block text-slate-300">普通访问令牌（输入或选择 `host.token`）</span>
                <div className="flex gap-2">
                  <input
                    type="password"
                    value={accessTokenManual}
                    onChange={(e) => {
                      setAccessTokenManual(e.target.value);
                      setAccessTokenHandle(undefined);
                      setDiscoveryHandle(undefined);
                      setAccessTokenDisplay('');
                    }}
                    placeholder={accessTokenDisplay || '粘贴访问令牌或点击右侧选择 host.token'}
                    className="flex-1 rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
                  />
                  <button
                    type="button"
                    onClick={() => void handlePickToken('access')}
                    className="rounded-lg border border-slate-700 px-3 text-slate-200 hover:bg-slate-800"
                  >
                    选择 host.token
                  </button>
                </div>
                {accessTokenDisplay && (
                  <div className="mt-1 text-[11px] text-emerald-300">{accessTokenDisplay}</div>
                )}
              </div>

              <div className="block">
                <span className="mb-1 block text-slate-300">控制面令牌文件（可选，仅主进程持有）</span>
                <div className="flex items-center justify-between gap-2 rounded-lg border border-slate-700 bg-slate-950 px-3 py-2">
                  <span className="truncate text-slate-400">
                    {controlTokenDisplay || '未选择 control.token（Local 独立实例默认无控制面）'}
                  </span>
                  <button
                    type="button"
                    onClick={() => void handlePickToken('control')}
                    className="shrink-0 rounded border border-slate-700 px-2.5 py-1 text-slate-200 hover:bg-slate-800"
                  >
                    选择 control.token
                  </button>
                </div>
              </div>

              {confirmRiskOpen && (
                <div
                  data-testid="wizard-risk-confirm"
                  className="rounded-xl border border-amber-500/50 bg-amber-950/30 p-3 text-amber-100"
                >
                  <div className="font-semibold">确认放宽执行或隔离策略？</div>
                  <ul className="mt-1 list-disc space-y-0.5 pl-4 text-amber-200/90">
                    {riskReasons.map((reason) => (
                      <li key={reason}>{reason}</li>
                    ))}
                  </ul>
                  <div className="mt-2 flex gap-2">
                    <button
                      type="button"
                      disabled={saving}
                      onClick={() => void executeSaveManaged()}
                      className="rounded-lg bg-amber-600 px-3 py-1 font-medium text-white hover:bg-amber-500"
                    >
                      确认放宽并保存
                    </button>
                    <button
                      type="button"
                      onClick={() => setConfirmRiskOpen(false)}
                      className="rounded-lg border border-slate-700 px-3 py-1 text-slate-300 hover:bg-slate-800"
                    >
                      取消
                    </button>
                  </div>
                </div>
              )}

              {error && <div className="text-rose-300">{error}</div>}

              <div className="flex justify-end gap-2 pt-2">
                <button
                  type="button"
                  onClick={onClose}
                  className="rounded-lg border border-slate-700 px-3 py-1.5 text-slate-300 hover:bg-slate-800"
                >
                  取消
                </button>
                <button
                  type="submit"
                  disabled={saving}
                  className="rounded-lg bg-indigo-600 px-4 py-1.5 font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
                >
                  {saving ? '连接中...' : '连接实例'}
                </button>
              </div>
            </form>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
};
