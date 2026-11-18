import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  AlertTriangle,
  KeyRound,
  Plus,
  PowerOff,
  RefreshCw,
  Search,
  ShieldCheck,
  Trash2,
} from 'lucide-react';
import type {
  BusinessAuthKind,
  BusinessPluginDto,
  BusinessPluginSourceType,
  CapabilityDetailDto,
  CapabilityPageResponseDto,
  CapabilityRiskLevel,
  CredentialSlotKind,
  DiscoverPluginResultDto,
} from '@shared/ipc';
import { apiClient, type ApiAuthContext } from '../../lib/apiClient';
import { MarkdownRenderer } from '../MarkdownRenderer';
import {
  buildSecretPayloadJson,
  extractControlPlaneError,
  formatBusinessSkillError,
  getRiskBadgeInfo,
  newClientRequestId,
} from './businessSkillHelpers';

interface PluginCatalogPanelProps {
  auth: ApiAuthContext | null;
  plugins: BusinessPluginDto[];
  controlPlaneAvailable: boolean;
  onRefreshPlugins: () => Promise<void>;
  onStartWizardForPlugin?: (pluginId: string) => void;
}

export const PluginCatalogPanel: React.FC<PluginCatalogPanelProps> = ({
  auth,
  plugins,
  controlPlaneAvailable,
  onRefreshPlugins,
  onStartWizardForPlugin,
}) => {
  const [selectedPluginId, setSelectedPluginId] = useState<string | null>(null);
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [confirmDanger, setConfirmDanger] = useState<
    | { kind: 'prune' }
    | { kind: 'disable'; pluginId: string; pluginName: string }
    | { kind: 'delete-cred'; pluginId: string; slot: CredentialSlotKind }
    | null
  >(null);
  const capReqSeqRef = useRef(0);

  // 新建插件表单状态
  const [newName, setNewName] = useState('');
  const [newSourceType, setNewSourceType] = useState<BusinessPluginSourceType>('OpenApi');
  const [newSourceUrl, setNewSourceUrl] = useState('');
  const [newEnvironment, setNewEnvironment] = useState('Sandbox');
  const [newAllowedOrigin, setNewAllowedOrigin] = useState('');
  const [newAllowedPrefix, setNewAllowedPrefix] = useState('');
  const [newAllowPlainHttp, setNewAllowPlainHttp] = useState(false);

  // 编辑当前插件连接态
  const [editSourceUrl, setEditSourceUrl] = useState('');
  const [editEnvironment, setEditEnvironment] = useState('Sandbox');
  const [editAllowedOrigin, setEditAllowedOrigin] = useState('');
  const [editAllowedPrefix, setEditAllowedPrefix] = useState('');
  const [editAllowPlainHttp, setEditAllowPlainHttp] = useState(false);

  // 凭证配置（严格仅存组件瞬时本地态，提交或关闭即清空）
  const [credentialSlot, setCredentialSlot] = useState<CredentialSlotKind>('execution');
  const [credentialAuthKind, setCredentialAuthKind] = useState<BusinessAuthKind>('Bearer');
  const [credToken, setCredToken] = useState('');
  const [credHeaderName, setCredHeaderName] = useState('X-API-Key');
  const [credApiKey, setCredApiKey] = useState('');
  const [credUsername, setCredUsername] = useState('');
  const [credPassword, setCredPassword] = useState('');

  // 目录查询与能力详情
  const [search, setSearch] = useState('');
  const [groupTag, setGroupTag] = useState('');
  const [executableOnly, setExecutableOnly] = useState(false);
  const [page, setPage] = useState(1);
  const [catalogPage, setCatalogPage] = useState<CapabilityPageResponseDto | null>(null);
  const [selectedCapId, setSelectedCapId] = useState<string | null>(null);
  const [capDetail, setCapDetail] = useState<CapabilityDetailDto | null>(null);
  const [pendingRisk, setPendingRisk] = useState<CapabilityRiskLevel>('Read');
  const [riskNote, setRiskNote] = useState('');
  const [lastDiscoverResult, setLastDiscoverResult] = useState<DiscoverPluginResultDto | null>(null);

  const wipeSecretInputs = useCallback(() => {
    setCredToken('');
    setCredApiKey('');
    setCredUsername('');
    setCredPassword('');
  }, []);

  useEffect(() => {
    if (plugins.length > 0 && (!selectedPluginId || !plugins.some((p) => p.id === selectedPluginId))) {
      setSelectedPluginId(plugins[0]?.id ?? null);
    }
    if (plugins.length === 0) {
      setSelectedPluginId(null);
    }
  }, [plugins, selectedPluginId]);

  const selectedPlugin = plugins.find((p) => p.id === selectedPluginId) ?? null;

  useEffect(() => {
    wipeSecretInputs();
    setLastDiscoverResult(null);
    setSelectedCapId(null);
    setCapDetail(null);
    setPage(1);
    setConfirmDanger(null);
  }, [selectedPluginId, wipeSecretInputs]);

  useEffect(() => {
    if (selectedPlugin?.connection) {
      const conn = selectedPlugin.connection;
      setEditSourceUrl(conn.sourceUrl ?? '');
      setEditEnvironment(conn.environment ?? 'Sandbox');
      setEditAllowedOrigin(conn.endpointPolicy?.allowedOrigin ?? '');
      setEditAllowedPrefix(conn.endpointPolicy?.allowedPathPrefix ?? '');
      setEditAllowPlainHttp(Boolean(conn.endpointPolicy?.allowPlainHttpForIntranet));
    } else {
      setEditSourceUrl('');
      setEditEnvironment('Sandbox');
      setEditAllowedOrigin('');
      setEditAllowedPrefix('');
      setEditAllowPlainHttp(false);
    }
  }, [selectedPluginId, selectedPlugin?.connection?.revision, selectedPlugin?.connection]);

  const loadCapabilities = useCallback(async () => {
    if (!auth || !selectedPluginId) {
      setCatalogPage(null);
      return;
    }
    const seq = ++capReqSeqRef.current;
    try {
      const result = await apiClient.queryPluginCapabilities(auth, selectedPluginId, {
        snapshotId: selectedPlugin?.activeSnapshotId ?? undefined,
        search: search.trim() || undefined,
        groupTag: groupTag.trim() || undefined,
        executableOnly,
        page,
        pageSize: 20,
      });
      if (seq !== capReqSeqRef.current) return;
      setCatalogPage(result);
    } catch (err) {
      if (seq !== capReqSeqRef.current) return;
      setError(formatBusinessSkillError(err, '加载能力目录失败'));
    }
  }, [auth, selectedPluginId, selectedPlugin?.activeSnapshotId, search, groupTag, executableOnly, page]);

  useEffect(() => {
    void loadCapabilities();
  }, [loadCapabilities]);

  useEffect(() => {
    if (!auth || !selectedPluginId || !selectedCapId) {
      setCapDetail(null);
      return;
    }
    let cancelled = false;
    void apiClient
      .getPluginCapabilityDetail(
        auth,
        selectedPluginId,
        selectedCapId,
        selectedPlugin?.activeSnapshotId ?? undefined,
      )
      .then((detail) => {
        if (cancelled) return;
        setCapDetail(detail);
        setPendingRisk(detail.summary?.risk ?? 'Unknown');
      })
      .catch((err) => {
        if (!cancelled) {
          setError(formatBusinessSkillError(err, '获取能力详情失败'));
        }
      });
    return () => {
      cancelled = true;
    };
  }, [auth, selectedPluginId, selectedCapId, selectedPlugin?.activeSnapshotId]);

  const handleCreatePlugin = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!window.codexflow) return;
    setBusyAction('create');
    setError(null);
    setNotice(null);
    try {
      let inferredOrigin = newAllowedOrigin.trim();
      if (!inferredOrigin && newSourceUrl.trim()) {
        try {
          inferredOrigin = new URL(newSourceUrl.trim()).origin;
        } catch {
          inferredOrigin = '';
        }
      }
      const res = await window.codexflow.createBusinessPlugin({
        name: newName.trim(),
        sourceType: newSourceType,
        sourceUrl: newSourceUrl.trim(),
        environment: newEnvironment,
        endpointPolicy: {
          allowedOrigin: inferredOrigin,
          allowedPathPrefix: newAllowedPrefix.trim() || undefined,
          allowPlainHttpForIntranet: newAllowPlainHttp,
          allowExternalOpenApiRefs: false,
          allowedExternalRefOrigins: [],
        },
      });
      if (!res.ok) {
        setError(extractControlPlaneError(res, '注册插件失败'));
        return;
      }
      setNewName('');
      setNewSourceUrl('');
      setNewAllowedOrigin('');
      setNewAllowedPrefix('');
      setShowCreateForm(false);
      setNotice('插件已创建，请先完善凭证或点击“刷新发现”。');
      await onRefreshPlugins();
      if (res.data?.id) {
        setSelectedPluginId(res.data.id);
      }
    } catch (err) {
      setError(formatBusinessSkillError(err, '注册插件失败'));
    } finally {
      setBusyAction(null);
    }
  };

  const handleUpdateConnection = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!selectedPlugin?.id || !window.codexflow) return;
    setBusyAction('update-conn');
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.updateBusinessPluginConnection({
        pluginId: selectedPlugin.id,
        expectedRevision: selectedPlugin.connection?.revision ?? 1,
        sourceUrl: editSourceUrl.trim(),
        environment: editEnvironment,
        endpointPolicy: {
          allowedOrigin: editAllowedOrigin.trim(),
          allowedPathPrefix: editAllowedPrefix.trim() || undefined,
          allowPlainHttpForIntranet: editAllowPlainHttp,
          allowExternalOpenApiRefs: selectedPlugin.connection?.endpointPolicy?.allowExternalOpenApiRefs ?? false,
          allowedExternalRefOrigins: selectedPlugin.connection?.endpointPolicy?.allowedExternalRefOrigins ?? [],
          customCaThumbprint: selectedPlugin.connection?.endpointPolicy?.customCaThumbprint ?? null,
        },
      });
      if (!res.ok) {
        setError(extractControlPlaneError(res, '更新连接配置失败'));
        return;
      }
      setNotice('连接配置已更新。');
      await onRefreshPlugins();
    } catch (err) {
      setError(formatBusinessSkillError(err, '更新连接配置失败'));
    } finally {
      setBusyAction(null);
    }
  };

  const handleSaveCredential = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!selectedPlugin?.id || !window.codexflow) return;
    setBusyAction('save-cred');
    setError(null);
    setNotice(null);
    try {
      const payload = buildSecretPayloadJson(credentialAuthKind, {
        token: credToken,
        headerName: credHeaderName,
        apiKey: credApiKey,
        username: credUsername,
        password: credPassword,
      });
      const res = await window.codexflow.setBusinessPluginCredential({
        pluginId: selectedPlugin.id,
        slot: credentialSlot,
        authKind: credentialAuthKind,
        secretPayload: payload,
      });
      if (!res.ok) {
        setError(extractControlPlaneError(res, '保存凭证失败'));
        return;
      }
      setNotice(`${credentialSlot === 'document' ? '文档拉取' : '接口执行'}凭证已加密保存（输入已清空）。`);
      await onRefreshPlugins();
    } catch (err) {
      setError(formatBusinessSkillError(err, '保存凭证失败'));
    } finally {
      // 安全边界：无论成功或失败，均立即清除组件中的明文密钥/口令状态
      wipeSecretInputs();
      setBusyAction(null);
    }
  };

  const handleDeleteCredential = async (slot: CredentialSlotKind) => {
    if (!selectedPlugin?.id || !window.codexflow) return;
    setBusyAction(`del-cred-${slot}`);
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.deleteBusinessPluginCredential({
        pluginId: selectedPlugin.id,
        slot,
        unbindIfReferenced: true,
      });
      if (!res.ok) {
        setError(extractControlPlaneError(res, '删除凭证失败'));
        return;
      }
      setNotice(`已解绑并删除「${slot === 'document' ? '文档拉取' : '接口执行'}」凭证。`);
      await onRefreshPlugins().catch(() => undefined);
    } catch (err) {
      setError(formatBusinessSkillError(err, '删除凭证失败'));
    } finally {
      setBusyAction(null);
    }
  };

  const handleDiscover = async () => {
    if (!selectedPlugin?.id || !window.codexflow) return;
    setBusyAction('discover');
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.discoverBusinessPlugin({
        pluginId: selectedPlugin.id,
        clientRequestId: newClientRequestId('discover'),
      });
      if (!res.ok) {
        setError(extractControlPlaneError(res, '能力发现失败'));
        return;
      }
      setLastDiscoverResult(res.data ?? null);
      setNotice('能力目录发现已完成。');
      await onRefreshPlugins();
      await loadCapabilities();
    } catch (err) {
      setError(formatBusinessSkillError(err, '能力发现失败'));
    } finally {
      setBusyAction(null);
    }
  };

  const handleDisablePlugin = async () => {
    if (!selectedPlugin?.id || !window.codexflow) return;
    setBusyAction('disable');
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.disableBusinessPlugin(selectedPlugin.id);
      if (!res.ok) {
        setError(extractControlPlaneError(res, '停用插件失败'));
        return;
      }
      setNotice('插件已停用。');
      await onRefreshPlugins();
    } catch (err) {
      setError(formatBusinessSkillError(err, '停用插件失败'));
    } finally {
      setBusyAction(null);
    }
  };

  const handleUpdateRisk = async () => {
    if (!selectedPlugin?.id || !selectedCapId || !window.codexflow) return;
    setBusyAction('update-risk');
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.updateBusinessCapabilityRisk({
        pluginId: selectedPlugin.id,
        capabilityId: selectedCapId,
        risk: pendingRisk,
        note: riskNote.trim() || undefined,
      });
      if (!res.ok) {
        setError(extractControlPlaneError(res, '更新能力风险分级失败'));
        return;
      }
      setRiskNote('');
      setNotice(`能力风险等级已通过控制面确认为 ${pendingRisk}。`);
      await loadCapabilities();
      if (auth) {
        const refreshed = await apiClient.getPluginCapabilityDetail(
          auth,
          selectedPlugin.id,
          selectedCapId,
          selectedPlugin.activeSnapshotId ?? undefined,
        );
        setCapDetail(refreshed);
      }
    } catch (err) {
      setError(formatBusinessSkillError(err, '更新能力风险分级失败'));
    } finally {
      setBusyAction(null);
    }
  };

  const handlePruneRetention = async () => {
    if (!window.codexflow) return;
    setBusyAction('prune');
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.pruneBusinessSkillRetention();
      if (!res.ok || !res.data) {
        setError(extractControlPlaneError(res, '清理过期运行与诊断记录失败'));
        return;
      }
      const d = res.data;
      setNotice(
        `已完成保留期清理：裁剪步骤诊断 ${d.trimmedStepDiagnosticsCount} 条、清理终态运行 ${d.deletedTerminalRunsCount} 条、清理孤立快照 ${d.deletedOrphanSnapshotsCount} 个（保留 OutcomeUnknown ${d.retainedOutcomeUnknownRunsCount} 条、受保护快照 ${d.retainedProtectedSnapshotsCount} 个）。`,
      );
      await onRefreshPlugins();
    } catch (err) {
      setError(formatBusinessSkillError(err, '清理过期运行与诊断记录失败'));
    } finally {
      setBusyAction(null);
    }
  };

  const totalCount = Number(catalogPage?.totalCount ?? 0);
  const totalPages = Math.max(1, Math.ceil(totalCount / 20));

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h3 className="text-sm font-semibold text-slate-100">已连接业务系统与能力目录</h3>
          <p className="text-xs text-slate-400">
            管理 OpenAPI 3.x / MCP 插件连接、分离式凭证槽位、目录快照发现与控制面风险确认。
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={() => setConfirmDanger({ kind: 'prune' })}
            disabled={!controlPlaneAvailable || busyAction !== null}
            title={
              controlPlaneAvailable
                ? '清理超期终态运行（30 天）与步骤诊断（7 天），保留受引用快照与 OutcomeUnknown'
                : '控制面不可用'
            }
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-700 bg-slate-800 px-3 py-1.5 text-xs font-medium text-slate-200 hover:bg-slate-700 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <Trash2 className="h-3.5 w-3.5" />
            {busyAction === 'prune' ? '清理中...' : '清理过期记录'}
          </button>
          <button
            type="button"
            onClick={() => setShowCreateForm((v) => !v)}
            disabled={!controlPlaneAvailable}
            title={controlPlaneAvailable ? '注册新的企业 OpenAPI / MCP 插件' : '控制面不可用'}
            className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <Plus className="h-3.5 w-3.5" />
            接入新插件
          </button>
        </div>
      </div>

      {confirmDanger && (
        <div
          role="alertdialog"
          aria-label="高风险操作确认"
          className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-rose-500/40 bg-rose-950/30 px-3 py-2 text-xs text-rose-200"
        >
          <span>
            {confirmDanger.kind === 'prune' &&
              '确认清理过期记录吗？将删除超过 30 天的终态运行与 7 天的步骤诊断（受保护快照与 OutcomeUnknown 会保留），不可恢复。'}
            {confirmDanger.kind === 'disable' &&
              `确认停用插件「${confirmDanger.pluginName}」吗？停用后依赖它的 Skill 在执行时会受阻。`}
            {confirmDanger.kind === 'delete-cred' &&
              `确认删除「${confirmDanger.slot === 'document' ? '文档拉取' : '接口执行'}」凭证并解除引用绑定吗？之后对应请求将改为无认证调用。`}
          </span>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => {
                const target = confirmDanger;
                setConfirmDanger(null);
                if (target.kind === 'prune') {
                  void handlePruneRetention();
                } else if (target.kind === 'disable') {
                  void handleDisablePlugin();
                } else {
                  void handleDeleteCredential(target.slot);
                }
              }}
              className="rounded bg-rose-600 px-2.5 py-1 text-[11px] font-medium text-white hover:bg-rose-500"
            >
              确认执行
            </button>
            <button
              type="button"
              onClick={() => setConfirmDanger(null)}
              className="rounded border border-slate-700 px-2.5 py-1 text-[11px] text-slate-300 hover:bg-slate-800"
            >
              取消
            </button>
          </div>
        </div>
      )}

      {notice && (
        <div role="status" className="rounded-lg border border-emerald-500/40 bg-emerald-950/30 px-3 py-2 text-xs text-emerald-200">
          {notice}
        </div>
      )}
      {error && (
        <div role="alert" className="rounded-lg border border-rose-500/40 bg-rose-950/30 px-3 py-2 text-xs text-rose-200">
          {error}
        </div>
      )}

      {showCreateForm && (
        <form
          onSubmit={(e) => void handleCreatePlugin(e)}
          className="space-y-3 rounded-xl border border-indigo-500/40 bg-slate-900/90 p-4"
        >
          <div className="text-xs font-semibold text-indigo-200">接入新插件（控制面创建）</div>
          <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
            <label className="space-y-1 text-xs">
              <span className="text-slate-300">插件名称</span>
              <input
                required
                value={newName}
                onChange={(e) => setNewName(e.target.value)}
                placeholder="例如：内网订单系统"
                className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-1.5 text-slate-100"
              />
            </label>
            <div className="grid grid-cols-2 gap-2">
              <label className="space-y-1 text-xs">
                <span className="text-slate-300">协议类型</span>
                <select
                  value={newSourceType}
                  onChange={(e) => setNewSourceType(e.target.value as BusinessPluginSourceType)}
                  className="w-full rounded-lg border border-slate-700 bg-slate-950 px-2.5 py-1.5 text-slate-100"
                >
                  <option value="OpenApi">OpenAPI 3.x</option>
                  <option value="Mcp">MCP (Streamable HTTP)</option>
                </select>
              </label>
              <label className="space-y-1 text-xs">
                <span className="text-slate-300">环境标签</span>
                <select
                  value={newEnvironment}
                  onChange={(e) => setNewEnvironment(e.target.value)}
                  className="w-full rounded-lg border border-slate-700 bg-slate-950 px-2.5 py-1.5 text-slate-100"
                >
                  <option value="Sandbox">Sandbox（测试）</option>
                  <option value="Staging">Staging（预发）</option>
                  <option value="Production">Production（生产）</option>
                </select>
              </label>
            </div>
            <label className="space-y-1 text-xs md:col-span-2">
              <span className="text-slate-300">源地址 (OpenAPI JSON/YAML URL 或 MCP Endpoint URL)</span>
              <input
                required
                type="url"
                value={newSourceUrl}
                onChange={(e) => setNewSourceUrl(e.target.value)}
                placeholder="https://api.internal.example.com/openapi.json"
                className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-1.5 text-slate-100"
              />
            </label>
            <label className="space-y-1 text-xs">
              <span className="text-slate-300">允许源站 Origin（默认取自 URL）</span>
              <input
                value={newAllowedOrigin}
                onChange={(e) => setNewAllowedOrigin(e.target.value)}
                placeholder="https://api.internal.example.com"
                className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-1.5 text-slate-100"
              />
            </label>
            <label className="space-y-1 text-xs">
              <span className="text-slate-300">允许路径前缀（可选）</span>
              <input
                value={newAllowedPrefix}
                onChange={(e) => setNewAllowedPrefix(e.target.value)}
                placeholder="/api/v1"
                className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-1.5 text-slate-100"
              />
            </label>
          </div>
          <label className="flex items-center gap-2 text-xs text-amber-200">
            <input
              type="checkbox"
              checked={newAllowPlainHttp}
              onChange={(e) => setNewAllowPlainHttp(e.target.checked)}
              className="rounded border-slate-600 bg-slate-950"
            />
            允许纯内网私网明文 HTTP（仅限内网环境显式放行，明文传输存在风险提示）
          </label>
          <div className="flex justify-end gap-2">
            <button
              type="button"
              onClick={() => setShowCreateForm(false)}
              className="rounded-lg border border-slate-700 px-3 py-1.5 text-xs text-slate-300 hover:bg-slate-800"
            >
              取消
            </button>
            <button
              type="submit"
              disabled={!controlPlaneAvailable || busyAction !== null}
              className="rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-indigo-500 disabled:opacity-50"
            >
              {busyAction === 'create' ? '创建中...' : '确认创建'}
            </button>
          </div>
        </form>
      )}

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-12">
        {/* 左侧插件列表 */}
        <div className="space-y-2 lg:col-span-4">
          {plugins.length === 0 ? (
            <div className="rounded-xl border border-dashed border-slate-800 bg-slate-900/40 p-6 text-center text-xs text-slate-400">
              暂未接入任何业务插件。点击上方「接入新插件」或使用「五步向导」开始。
            </div>
          ) : (
            plugins.map((plugin) => {
              const active = plugin.id === selectedPluginId;
              const insecure = plugin.connection?.isEncryptedTransport === false;
              return (
                <button
                  key={plugin.id}
                  type="button"
                  onClick={() => setSelectedPluginId(plugin.id ?? null)}
                  className={`w-full rounded-xl border p-3 text-left transition ${
                    active
                      ? 'border-indigo-500/60 bg-indigo-950/25'
                      : 'border-slate-800 bg-slate-900/60 hover:border-slate-700'
                  }`}
                >
                  <div className="flex items-center justify-between gap-2">
                    <span className="truncate text-sm font-medium text-slate-100">{plugin.name}</span>
                    <span className="rounded bg-slate-800 px-1.5 py-0.5 text-[10px] text-slate-300">
                      {plugin.sourceType} · {plugin.connection?.environment ?? '-'}
                    </span>
                  </div>
                  <div className="mt-1 truncate text-[11px] text-slate-400">
                    {plugin.connection?.sourceUrl}
                  </div>
                  <div className="mt-2 flex flex-wrap items-center gap-2 text-[11px]">
                    <span className="text-slate-300">能力 {plugin.capabilityCount ?? 0} 项</span>
                    {!plugin.enabled && (
                      <span className="rounded bg-rose-500/20 px-1.5 py-0.5 text-rose-300">已停用</span>
                    )}
                    {insecure && (
                      <span className="inline-flex items-center gap-1 rounded bg-amber-500/20 px-1.5 py-0.5 text-amber-200">
                        <AlertTriangle className="h-3 w-3" /> 非加密 HTTP
                      </span>
                    )}
                  </div>
                </button>
              );
            })
          )}
        </div>

        {/* 右侧插件详情、凭证管理与能力目录 */}
        <div className="space-y-4 lg:col-span-8">
          {selectedPlugin ? (
            <>
              <div className="space-y-3 rounded-xl border border-slate-800 bg-slate-900/70 p-4">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div>
                    <div className="text-sm font-semibold text-slate-100">{selectedPlugin.name}</div>
                    <div className="text-xs text-slate-400">
                      {selectedPlugin.sourceType} · 快照 {selectedPlugin.activeSnapshotId || '尚未发现'}
                    </div>
                  </div>
                  <div className="flex flex-wrap items-center gap-2">
                    <button
                      type="button"
                      onClick={() => void handleDiscover()}
                      disabled={!controlPlaneAvailable || busyAction !== null}
                      className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      <RefreshCw className={`h-3.5 w-3.5 ${busyAction === 'discover' ? 'animate-spin' : ''}`} />
                      刷新发现目录
                    </button>
                    {onStartWizardForPlugin && selectedPlugin.id && (
                      <button
                        type="button"
                        onClick={() => onStartWizardForPlugin(selectedPlugin.id!)}
                        className="rounded-lg border border-indigo-500/50 bg-indigo-500/15 px-3 py-1.5 text-xs font-medium text-indigo-200 hover:bg-indigo-500/25"
                      >
                        用此插件生成 Skill
                      </button>
                    )}
                    {selectedPlugin.enabled && (
                      <button
                        type="button"
                        onClick={() =>
                          setConfirmDanger({
                            kind: 'disable',
                            pluginId: selectedPlugin.id!,
                            pluginName: selectedPlugin.name ?? selectedPlugin.id!,
                          })
                        }
                        disabled={!controlPlaneAvailable || busyAction !== null}
                        className="inline-flex items-center gap-1 rounded-lg border border-rose-500/40 bg-rose-500/10 px-2.5 py-1.5 text-xs text-rose-200 hover:bg-rose-500/20 disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        <PowerOff className="h-3.5 w-3.5" />
                        停用
                      </button>
                    )}
                  </div>
                </div>

                {selectedPlugin.connection?.isEncryptedTransport === false && (
                  <div className="flex items-start gap-2 rounded-lg border border-amber-500/40 bg-amber-950/30 p-2.5 text-xs text-amber-200">
                    <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
                    <span>当前配置使用明文 HTTP 传输（仅限内网调试），请避免携带高敏生产口令。</span>
                  </div>
                )}

                {lastDiscoverResult?.diff && lastDiscoverResult.diff.entries.length > 0 && (
                  <div className="rounded-lg border border-sky-500/30 bg-sky-950/20 p-3 text-xs text-sky-200">
                    <div className="font-medium">
                      目录变更摘要（{lastDiscoverResult.diff.entries.length} 项变化
                      {lastDiscoverResult.diff.triggersNeedsRevalidation ? ' · 触发关联草稿/Skill 重验证' : ''}）：
                    </div>
                    <ul className="mt-1 list-disc space-y-0.5 pl-4 text-[11px]">
                      {lastDiscoverResult.diff.entries.slice(0, 6).map((entry) => (
                        <li key={`${entry.capabilityId}-${entry.changeKind}`}>
                          [{entry.changeKind}] {entry.title || entry.capabilityId}: {entry.summary}
                        </li>
                      ))}
                    </ul>
                  </div>
                )}

                {/* 连接编辑 */}
                <form
                  onSubmit={(e) => void handleUpdateConnection(e)}
                  className="grid grid-cols-1 gap-2 border-t border-slate-800 pt-3 md:grid-cols-2"
                >
                  <label className="space-y-1 text-xs md:col-span-2">
                    <span className="text-slate-400">源地址</span>
                    <input
                      value={editSourceUrl}
                      onChange={(e) => setEditSourceUrl(e.target.value)}
                      className="w-full rounded-lg border border-slate-700 bg-slate-950 px-2.5 py-1.5 text-slate-100"
                    />
                  </label>
                  <label className="space-y-1 text-xs">
                    <span className="text-slate-400">环境</span>
                    <select
                      value={editEnvironment}
                      onChange={(e) => setEditEnvironment(e.target.value)}
                      className="w-full rounded-lg border border-slate-700 bg-slate-950 px-2.5 py-1.5 text-slate-100"
                    >
                      <option value="Sandbox">Sandbox</option>
                      <option value="Staging">Staging</option>
                      <option value="Production">Production</option>
                    </select>
                  </label>
                  <label className="space-y-1 text-xs">
                    <span className="text-slate-400">允许源站 Origin</span>
                    <input
                      value={editAllowedOrigin}
                      onChange={(e) => setEditAllowedOrigin(e.target.value)}
                      className="w-full rounded-lg border border-slate-700 bg-slate-950 px-2.5 py-1.5 text-slate-100"
                    />
                  </label>
                  <div className="flex flex-wrap items-center justify-between gap-2 md:col-span-2">
                    <label className="flex items-center gap-2 text-xs text-slate-300">
                      <input
                        type="checkbox"
                        checked={editAllowPlainHttp}
                        onChange={(e) => setEditAllowPlainHttp(e.target.checked)}
                      />
                      内网明文 HTTP 放行
                    </label>
                    <button
                      type="submit"
                      disabled={!controlPlaneAvailable || busyAction !== null}
                      className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-1 text-xs text-slate-100 hover:bg-slate-700 disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      保存连接策略
                    </button>
                  </div>
                </form>

                {/* 分离式文档与执行凭证管理 */}
                <div className="space-y-3 border-t border-slate-800 pt-3">
                  <div className="flex items-center gap-2 text-xs font-semibold text-slate-200">
                    <KeyRound className="h-3.5 w-3.5 text-indigo-400" />
                    凭证槽位管理（本地 DPAPI 加密存储，明文提交后立即清除）
                  </div>
                  <div className="grid grid-cols-1 gap-2 md:grid-cols-2">
                    {(['document', 'execution'] as CredentialSlotKind[]).map((slot) => {
                      const summary =
                        slot === 'document'
                          ? selectedPlugin.connection?.documentCredential
                          : selectedPlugin.connection?.executionCredential;
                      return (
                        <div
                          key={slot}
                          className="flex items-center justify-between rounded-lg border border-slate-800 bg-slate-950/70 px-3 py-2 text-xs"
                        >
                          <div>
                            <div className="font-medium text-slate-200">
                              {slot === 'document' ? '文档拉取凭证 (document)' : '接口执行凭证 (execution)'}
                            </div>
                            <div className="text-[11px] text-slate-400">
                              {summary ? `${summary.authKind} · ${summary.maskedHint}` : '未配置（默认无认证）'}
                            </div>
                          </div>
                          {summary && (
                            <button
                              type="button"
                              onClick={() =>
                                setConfirmDanger({
                                  kind: 'delete-cred',
                                  pluginId: selectedPlugin.id!,
                                  slot,
                                })
                              }
                              disabled={!controlPlaneAvailable || busyAction !== null}
                              title="删除凭证"
                              aria-label={`删除${slot === 'document' ? '文档拉取' : '接口执行'}凭证`}
                              className="rounded p-1 text-slate-400 hover:bg-rose-500/20 hover:text-rose-300 disabled:opacity-40"
                            >
                              <Trash2 className="h-3.5 w-3.5" />
                            </button>
                          )}
                        </div>
                      );
                    })}
                  </div>

                  <form
                    onSubmit={(e) => void handleSaveCredential(e)}
                    className="space-y-2 rounded-lg border border-slate-800 bg-slate-950/60 p-3 text-xs"
                  >
                    <div className="grid grid-cols-2 gap-2">
                      <label className="space-y-1">
                        <span className="text-slate-400">目标槽位</span>
                        <select
                          aria-label="凭证槽位"
                          value={credentialSlot}
                          onChange={(e) => setCredentialSlot(e.target.value as CredentialSlotKind)}
                          className="w-full rounded border border-slate-700 bg-slate-900 px-2 py-1 text-slate-100"
                        >
                          <option value="execution">execution（执行调用）</option>
                          <option value="document">document（发现拉取）</option>
                        </select>
                      </label>
                      <label className="space-y-1">
                        <span className="text-slate-400">认证类型</span>
                        <select
                          aria-label="认证类型"
                          value={credentialAuthKind}
                          onChange={(e) => setCredentialAuthKind(e.target.value as BusinessAuthKind)}
                          className="w-full rounded border border-slate-700 bg-slate-900 px-2 py-1 text-slate-100"
                        >
                          <option value="Bearer">Bearer Token</option>
                          <option value="ApiKey">API Key Header</option>
                          <option value="BasicTlsOnly">Basic Auth (仅 TLS)</option>
                          <option value="OAuth2AuthCodePkce">OAuth2 Token</option>
                          <option value="None">无认证 (None)</option>
                        </select>
                      </label>
                    </div>

                    {(credentialAuthKind === 'Bearer' || credentialAuthKind === 'OAuth2AuthCodePkce') && (
                      <label className="block space-y-1">
                        <span className="text-slate-400">Access Token / Bearer 密钥</span>
                        <input
                          type="password"
                          autoComplete="new-password"
                          value={credToken}
                          onChange={(e) => setCredToken(e.target.value)}
                          placeholder="输入后仅通过控制面 IPC 落盘，提交后立即清空"
                          className="w-full rounded border border-slate-700 bg-slate-900 px-2.5 py-1.5 text-slate-100"
                        />
                      </label>
                    )}

                    {credentialAuthKind === 'ApiKey' && (
                      <div className="grid grid-cols-2 gap-2">
                        <label className="space-y-1">
                          <span className="text-slate-400">请求头名称</span>
                          <input
                            value={credHeaderName}
                            onChange={(e) => setCredHeaderName(e.target.value)}
                            className="w-full rounded border border-slate-700 bg-slate-900 px-2.5 py-1.5 text-slate-100"
                          />
                        </label>
                        <label className="space-y-1">
                          <span className="text-slate-400">API Key 值</span>
                          <input
                            type="password"
                            autoComplete="new-password"
                            value={credApiKey}
                            onChange={(e) => setCredApiKey(e.target.value)}
                            className="w-full rounded border border-slate-700 bg-slate-900 px-2.5 py-1.5 text-slate-100"
                          />
                        </label>
                      </div>
                    )}

                    {credentialAuthKind === 'BasicTlsOnly' && (
                      <div className="grid grid-cols-2 gap-2">
                        <label className="space-y-1">
                          <span className="text-slate-400">用户名</span>
                          <input
                            value={credUsername}
                            onChange={(e) => setCredUsername(e.target.value)}
                            className="w-full rounded border border-slate-700 bg-slate-900 px-2.5 py-1.5 text-slate-100"
                          />
                        </label>
                        <label className="space-y-1">
                          <span className="text-slate-400">口令密码</span>
                          <input
                            type="password"
                            autoComplete="new-password"
                            value={credPassword}
                            onChange={(e) => setCredPassword(e.target.value)}
                            className="w-full rounded border border-slate-700 bg-slate-900 px-2.5 py-1.5 text-slate-100"
                          />
                        </label>
                      </div>
                    )}

                    <div className="flex justify-end">
                      <button
                        type="submit"
                        disabled={!controlPlaneAvailable || busyAction !== null}
                        className="rounded-lg bg-slate-800 px-3 py-1.5 text-xs font-medium text-slate-100 hover:bg-slate-700 disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        {busyAction === 'save-cred' ? '加密保存中...' : '保存凭证到控制面'}
                      </button>
                    </div>
                  </form>
                </div>
              </div>

              {/* 能力目录搜索与控制面风险确认 */}
              <div className="space-y-3 rounded-xl border border-slate-800 bg-slate-900/70 p-4">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div className="text-xs font-semibold text-slate-200">
                    能力目录（总计 {catalogPage?.totalCount ?? 0} · 可执行 {catalogPage?.executableCount ?? 0} · 需配置{' '}
                    {catalogPage?.needsConfigurationCount ?? 0} · 不支持 {catalogPage?.unsupportedCount ?? 0}）
                  </div>
                  <div className="flex flex-wrap items-center gap-2 text-xs">
                    <div className="relative">
                      <Search className="pointer-events-none absolute left-2 top-1.5 h-3.5 w-3.5 text-slate-500" />
                      <input
                        value={search}
                        onChange={(e) => {
                          setSearch(e.target.value);
                          setPage(1);
                        }}
                        placeholder="搜索接口/工具..."
                        className="rounded-lg border border-slate-700 bg-slate-950 py-1 pl-7 pr-2.5 text-xs text-slate-100"
                      />
                    </div>
                    <input
                      value={groupTag}
                      onChange={(e) => {
                        setGroupTag(e.target.value);
                        setPage(1);
                      }}
                      placeholder="分组 Tag"
                      className="w-24 rounded-lg border border-slate-700 bg-slate-950 px-2 py-1 text-xs text-slate-100"
                    />
                    <label className="flex items-center gap-1.5 text-slate-300">
                      <input
                        type="checkbox"
                        checked={executableOnly}
                        onChange={(e) => {
                          setExecutableOnly(e.target.checked);
                          setPage(1);
                        }}
                      />
                      仅看可执行
                    </label>
                  </div>
                </div>

                <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
                  <div className="max-h-72 space-y-1.5 overflow-y-auto pr-1">
                    {(catalogPage?.items ?? []).map((item) => {
                      const riskInfo = getRiskBadgeInfo(item.risk);
                      const active = item.id === selectedCapId;
                      return (
                        <button
                          key={item.id}
                          type="button"
                          onClick={() => setSelectedCapId(item.id ?? null)}
                          className={`w-full rounded-lg border p-2.5 text-left text-xs ${
                            active
                              ? 'border-indigo-500/60 bg-indigo-950/30'
                              : 'border-slate-800 bg-slate-950/60 hover:border-slate-700'
                          }`}
                        >
                          <div className="flex items-center justify-between gap-2">
                            <span className="truncate font-medium text-slate-100">
                              {item.title || item.nativeIdentity}
                            </span>
                            <span className={`shrink-0 rounded border px-1.5 py-0.5 text-[10px] ${riskInfo.className}`}>
                              {riskInfo.label}
                            </span>
                          </div>
                          <div className="mt-1 truncate font-mono text-[11px] text-slate-400">
                            {item.nativeIdentity}
                          </div>
                        </button>
                      );
                    })}
                    {(catalogPage?.items ?? []).length === 0 && (
                      <div className="rounded-lg border border-dashed border-slate-800 p-4 text-center text-xs text-slate-400">
                        当前条件下没有匹配的能力条目。
                      </div>
                    )}
                    {totalPages > 1 && (
                      <div className="flex items-center justify-between pt-2 text-xs text-slate-400">
                        <button
                          type="button"
                          disabled={page <= 1}
                          onClick={() => setPage((p) => Math.max(1, p - 1))}
                          className="rounded border border-slate-700 px-2 py-0.5 disabled:opacity-40"
                        >
                          上一页
                        </button>
                        <span>
                          第 {page} / {totalPages} 页
                        </span>
                        <button
                          type="button"
                          disabled={page >= totalPages}
                          onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                          className="rounded border border-slate-700 px-2 py-0.5 disabled:opacity-40"
                        >
                          下一页
                        </button>
                      </div>
                    )}
                  </div>

                  {/* 能力详情与风险确认 */}
                  <div className="rounded-lg border border-slate-800 bg-slate-950/70 p-3 text-xs">
                    {capDetail?.summary ? (
                      <div className="space-y-3">
                        <div>
                          <div className="font-semibold text-slate-100">
                            {capDetail.summary.title || capDetail.summary.nativeIdentity}
                          </div>
                          <div className="mt-0.5 font-mono text-[11px] text-slate-400">
                            {capDetail.invocation?.httpMethod
                              ? `${capDetail.invocation.httpMethod} ${capDetail.invocation.normalizedPath}`
                              : capDetail.invocation?.mcpToolName || capDetail.summary.nativeIdentity}
                          </div>
                        </div>

                        {capDetail.summary.sourceDescriptionMissing ? (
                          <div className="rounded border border-amber-500/30 bg-amber-950/20 px-2.5 py-2 text-amber-200">
                            源接口未提供说明（请在确认参数结构后再用于 Skill 生成）。
                          </div>
                        ) : (
                          <div className="rounded border border-slate-800 bg-slate-900/60 p-2.5">
                            <MarkdownRenderer
                              content={capDetail.summary.description || ''}
                              allowImages={false}
                            />
                          </div>
                        )}

                        <div className="space-y-2 border-t border-slate-800 pt-2">
                          <div className="flex items-center gap-1.5 font-medium text-slate-200">
                            <ShieldCheck className="h-3.5 w-3.5 text-indigo-400" />
                            控制面风险分级确认
                          </div>
                          {capDetail.summary.suggestedSourceRiskHint && (
                            <div className="text-[11px] text-slate-400">
                              源端非权威建议提示：{capDetail.summary.suggestedSourceRiskHint}（不可直接作为只读依据）
                            </div>
                          )}
                          <div className="flex flex-wrap items-center gap-2">
                            <select
                              aria-label="能力风险分级"
                              value={pendingRisk}
                              onChange={(e) => setPendingRisk(e.target.value as CapabilityRiskLevel)}
                              className="rounded border border-slate-700 bg-slate-900 px-2 py-1 text-slate-100"
                            >
                              <option value="Unknown">Unknown（默认待确认）</option>
                              <option value="Read">Read（只读）</option>
                              <option value="Write">Write（写入）</option>
                              <option value="Destructive">Destructive（破坏性写入）</option>
                            </select>
                            <input
                              value={riskNote}
                              onChange={(e) => setRiskNote(e.target.value)}
                              placeholder="风险确认说明"
                              className="min-w-[120px] flex-1 rounded border border-slate-700 bg-slate-900 px-2 py-1 text-slate-100"
                            />
                            <button
                              type="button"
                              onClick={() => void handleUpdateRisk()}
                              disabled={!controlPlaneAvailable || busyAction !== null}
                              className="rounded bg-indigo-600 px-2.5 py-1 font-medium text-white hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
                            >
                              确认风险
                            </button>
                          </div>
                        </div>
                      </div>
                    ) : (
                      <div className="py-10 text-center text-slate-500">
                        在左侧选择一个能力以查看安全说明与控制面风险分级。
                      </div>
                    )}
                  </div>
                </div>
              </div>
            </>
          ) : (
            <div className="rounded-xl border border-dashed border-slate-800 p-10 text-center text-xs text-slate-400">
              请在左侧选择或创建一个业务系统插件。
            </div>
          )}
        </div>
      </div>
    </div>
  );
};
