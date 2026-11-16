import React, { useEffect, useState } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { AlertTriangle, Trash2, X } from 'lucide-react';
import type {
  ExecutionBackendKind,
  ExecutionPermissionModeKind,
  PersistedPermissionRule,
} from '@shared/ipc';
import type { ExecutionSettings, ModelSettings, SystemStatusSummary } from '../lib/apiClient';
import { isExecutionRiskIncreasing, isRelaxingPermission } from '../lib/executionPolicy';

export { isRelaxingPermission };

interface SettingsModalProps {
  open: boolean;
  systemStatus: SystemStatusSummary | null;
  executionSettings: ExecutionSettings | null;
  modelSettings: ModelSettings | null;
  controlPlaneAvailable: boolean;
  onClose: () => void;
  onUpdated: () => void;
}

export const SettingsModal: React.FC<SettingsModalProps> = ({
  open,
  systemStatus,
  executionSettings,
  modelSettings,
  controlPlaneAvailable,
  onClose,
  onUpdated,
}) => {
  const [tab, setTab] = useState<'execution' | 'model' | 'rules'>('execution');

  const currentBackend = (executionSettings?.backend as ExecutionBackendKind) || 'Local';
  const currentMode = (executionSettings?.permissionMode as ExecutionPermissionModeKind) || 'Ask';
  const [backend, setBackend] = useState<ExecutionBackendKind>(currentBackend);
  const [permissionMode, setPermissionMode] = useState<ExecutionPermissionModeKind>(currentMode);
  const [confirmRelaxOpen, setConfirmRelaxOpen] = useState(false);

  const [apiUrl, setApiUrl] = useState(modelSettings?.apiUrl ?? '');
  const [apiMode, setApiMode] = useState(modelSettings?.apiMode ?? 'ChatCompletions');
  const [model, setModel] = useState(modelSettings?.model ?? '');
  const [apiKey, setApiKey] = useState('');

  const [rules, setRules] = useState<PersistedPermissionRule[]>([]);
  const [riskReasons, setRiskReasons] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setBackend(currentBackend);
    setPermissionMode(currentMode);
    setApiUrl(modelSettings?.apiUrl ?? '');
    setApiMode(modelSettings?.apiMode ?? 'ChatCompletions');
    setModel(modelSettings?.model ?? '');
    setApiKey('');
    setConfirmRelaxOpen(false);
    setNotice(null);
    setError(null);
  }, [open, currentBackend, currentMode, modelSettings]);

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
    setSaving(true);
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.updateModelSettings({
        apiUrl: apiUrl.trim() || undefined,
        apiMode,
        model: model.trim() || undefined,
        apiKey: apiKey.trim() || undefined,
      });
      if (!res.ok) {
        setError(res.errorMessage || `模型配置保存失败 (${res.status})`);
        return;
      }
      setApiKey('');
      setNotice('模型配置已更新。');
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
        setNotice('规则已撤销。');
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
        <Dialog.Content className="fixed left-1/2 top-1/2 z-50 w-full max-w-2xl -translate-x-1/2 -translate-y-1/2 rounded-2xl border border-slate-800 bg-slate-900 p-6 shadow-2xl">
          <div className="flex items-center justify-between">
            <Dialog.Title className="text-base font-semibold text-slate-100">设置</Dialog.Title>
            <button
              type="button"
              onClick={onClose}
              className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
            >
              <X className="h-4 w-4" />
            </button>
          </div>

          <div className="mt-4 flex gap-2 border-b border-slate-800 pb-3 text-xs">
            {[
              ['execution', '执行与权限'],
              ['model', '模型设置'],
              ['rules', '始终允许规则'],
            ].map(([key, label]) => (
              <button
                key={key}
                type="button"
                onClick={() => setTab(key as 'execution' | 'model' | 'rules')}
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
                  <span className="mb-1 block text-slate-300">执行后端</span>
                  <select
                    aria-label="执行后端"
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
                  <span className="mb-1 block text-slate-300">权限模式</span>
                  <select
                    aria-label="权限模式"
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
                    `FullAuto` 等于信任模型执行白名单内任意命令；在 Local 后端下将以你的用户权限无隔离运行。
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
                      确认放宽
                    </button>
                    <button
                      type="button"
                      onClick={() => setConfirmRelaxOpen(false)}
                      className="rounded-lg border border-slate-700 px-3 py-1 text-slate-300 hover:bg-slate-800"
                    >
                      取消
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
                  保存执行设置
                </button>
              </div>
            </form>
          )}

          {tab === 'model' && (
            <form onSubmit={(e) => void handleModelSubmit(e)} className="mt-4 space-y-3 text-xs">
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

              {notice && <div className="text-emerald-300">{notice}</div>}
              {error && <div className="text-rose-300">{error}</div>}

              <div className="flex justify-end">
                <button
                  type="submit"
                  disabled={saving || !controlPlaneAvailable}
                  className="rounded-lg bg-indigo-600 px-4 py-1.5 font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
                >
                  保存模型设置
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
                  暂无持久化的“始终允许”规则。
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
                        title="撤销规则"
                      >
                        <Trash2 className="h-3.5 w-3.5" />
                      </button>
                    </div>
                  ))}
                </div>
              )}
            </div>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
};
