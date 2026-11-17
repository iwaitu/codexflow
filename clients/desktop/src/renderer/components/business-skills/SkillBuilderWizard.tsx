import React, { useCallback, useEffect, useState } from 'react';
import {
  AlertTriangle,
  ArrowLeft,
  ArrowRight,
  CheckCircle2,
  FlaskConical,
  Play,
  Rocket,
  Search,
  Sparkles,
} from 'lucide-react';
import type {
  BusinessPluginDto,
  CapabilitySummaryDto,
  PublishedSkillDto,
  SkillDraftDto,
  SkillRunDto,
  SkillStepDefinition,
} from '@shared/ipc';
import { apiClient, type ApiAuthContext } from '../../lib/apiClient';
import {
  buildInitialInputDrafts,
  extractControlPlaneError,
  formatBusinessSkillError,
  getRiskBadgeInfo,
  newClientRequestId,
  parseDynamicInputs,
} from './businessSkillHelpers';
import { SkillRunCard } from './SkillRunCard';

interface SkillBuilderWizardProps {
  auth: ApiAuthContext | null;
  plugins: BusinessPluginDto[];
  initialPluginId?: string | null;
  activeSessionId: string | null;
  controlPlaneAvailable: boolean;
  modelConfigured: boolean;
  onRefreshPlugins: () => Promise<void>;
  onPublished: (skill: PublishedSkillDto) => Promise<void>;
}

const STEP_LABELS = [
  '1. 连接系统',
  '2. 选择能力',
  '3. 描述与生成',
  '4. 校验与试运行',
  '5. 发布版本',
];

function summarizeStep(step: SkillStepDefinition): string {
  switch (step.type) {
    case 'tool':
      return `调用能力: ${step.capabilityId || '-'}`;
    case 'transform':
      return `数据变换: ${step.operation || 'Transform'}`;
    case 'branch':
      return `条件分支 (${step.thenStepIds?.length ?? 0} / ${step.elseStepIds?.length ?? 0})`;
    case 'foreach':
      return `循环遍历 (上限 ${step.maxItems ?? '-'})`;
    case 'generateText':
      return '受控文本摘要生成（仅允许指定只读字段）';
    case 'approval':
      return `写入批准确认: ${step.plannedMutation || step.targetCapabilityId || '-'}`;
    case 'output':
      return '结构化结果输出';
    default:
      return '流程步骤';
  }
}

export const SkillBuilderWizard: React.FC<SkillBuilderWizardProps> = ({
  auth,
  plugins,
  initialPluginId = null,
  activeSessionId,
  controlPlaneAvailable,
  modelConfigured,
  onRefreshPlugins,
  onPublished,
}) => {
  const [stepIndex, setStepIndex] = useState(0);
  const [selectedPluginId, setSelectedPluginId] = useState<string>(initialPluginId ?? '');
  const [snapshotId, setSnapshotId] = useState<string>('');
  const [draft, setDraft] = useState<SkillDraftDto | null>(null);

  // 第 2 步：能力跨页选择
  const [capabilities, setCapabilities] = useState<CapabilitySummaryDto[]>([]);
  const [capCounts, setCapCounts] = useState({ total: 0, executable: 0, filtered: 0 });
  const [capSearch, setCapSearch] = useState('');
  const [capGroupTag, setCapGroupTag] = useState('');
  const [capExecutableOnly, setCapExecutableOnly] = useState(true);
  const [capPage, setCapPage] = useState(1);
  const [selectedCapIds, setSelectedCapIds] = useState<string[]>([]);

  // 第 3 步：业务目标与受控生成（保持回退不丢失）
  const [skillName, setSkillName] = useState('');
  const [businessGoal, setBusinessGoal] = useState('');
  const [confirmModelScope, setConfirmModelScope] = useState(false);
  const [includeSanitizedSamples, setIncludeSanitizedSamples] = useState(false);
  const [disclosedCategories, setDisclosedCategories] = useState<string[]>([]);

  // 第 4/5 步：输入、试运行与发布
  const [inputDrafts, setInputDrafts] = useState<Record<string, string>>({});
  const [testRun, setTestRun] = useState<SkillRunDto | null>(null);
  const [releaseNote, setReleaseNote] = useState('');

  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    if (initialPluginId) {
      setSelectedPluginId(initialPluginId);
    } else if (!selectedPluginId && plugins[0]?.id) {
      setSelectedPluginId(plugins[0].id);
    }
  }, [initialPluginId, plugins, selectedPluginId]);

  const currentPlugin = plugins.find((p) => p.id === selectedPluginId) ?? null;

  useEffect(() => {
    if (currentPlugin?.activeSnapshotId && !snapshotId) {
      setSnapshotId(currentPlugin.activeSnapshotId);
    }
  }, [currentPlugin?.activeSnapshotId, snapshotId]);

  // 每次草稿的候选定义输入 Schema 变化时同步默认表单草稿（保留已填字段）
  useEffect(() => {
    const schema = draft?.candidateDefinition?.inputs;
    if (!schema) return;
    setInputDrafts((prev) => ({
      ...buildInitialInputDrafts(schema),
      ...prev,
    }));
  }, [draft?.candidateDefinition?.inputs]);

  const loadCapabilitiesPage = useCallback(async () => {
    if (!auth || !selectedPluginId) return;
    try {
      const pageRes = await apiClient.queryPluginCapabilities(auth, selectedPluginId, {
        snapshotId: snapshotId || currentPlugin?.activeSnapshotId || undefined,
        search: capSearch.trim() || undefined,
        groupTag: capGroupTag.trim() || undefined,
        executableOnly: capExecutableOnly,
        page: capPage,
        pageSize: 20,
      });
      setCapabilities(pageRes.items ?? []);
      if (pageRes.snapshotId && !snapshotId) {
        setSnapshotId(pageRes.snapshotId);
      }
      setCapCounts({
        total: Number(pageRes.totalCount ?? 0),
        executable: Number(pageRes.executableCount ?? 0),
        filtered: Number(pageRes.filteredCount ?? 0),
      });
    } catch (err) {
      setError(formatBusinessSkillError(err, '加载插件能力目录失败'));
    }
  }, [
    auth,
    selectedPluginId,
    snapshotId,
    currentPlugin?.activeSnapshotId,
    capSearch,
    capGroupTag,
    capExecutableOnly,
    capPage,
  ]);

  useEffect(() => {
    if (stepIndex === 1) {
      void loadCapabilitiesPage();
    }
  }, [stepIndex, loadCapabilitiesPage]);

  const handleDiscoverPluginInWizard = async () => {
    if (!selectedPluginId || !window.codexflow) return;
    setBusy('discover');
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.discoverBusinessPlugin({
        pluginId: selectedPluginId,
        clientRequestId: newClientRequestId('wiz-discover'),
      });
      if (!res.ok) {
        setError(extractControlPlaneError(res, '刷新插件目录失败'));
        return;
      }
      if (res.data?.snapshot?.id) {
        setSnapshotId(res.data.snapshot.id);
      }
      await onRefreshPlugins();
      setNotice('目录快照已刷新，可进入下一步选择能力。');
    } catch (err) {
      setError(formatBusinessSkillError(err, '刷新插件目录失败'));
    } finally {
      setBusy(null);
    }
  };

  const handleStep1Next = () => {
    if (!currentPlugin) {
      setError('请先选择一个业务系统插件');
      return;
    }
    const targetSnap = snapshotId || currentPlugin.activeSnapshotId || '';
    if (!targetSnap) {
      setError('该插件尚无已发现的目录快照，请先点击“执行/刷新能力发现”');
      return;
    }
    setSnapshotId(targetSnap);
    setError(null);
    setStepIndex(1);
  };

  const toggleCapId = (id: string) => {
    setSelectedCapIds((prev) =>
      prev.includes(id) ? prev.filter((item) => item !== id) : [...prev, id],
    );
  };

  const handleSelectCurrentPageExecutable = () => {
    const pageExecIds = capabilities
      .filter((c) => c.executability === 'Executable' && Boolean(c.id))
      .map((c) => c.id as string);
    setSelectedCapIds((prev) => Array.from(new Set([...prev, ...pageExecIds])));
  };

  const collectAllExecutableAcrossPages = async (withCurrentFilter: boolean) => {
    if (!auth || !selectedPluginId) return;
    setBusy('bulk-select');
    setError(null);
    try {
      const collected = new Set<string>(selectedCapIds);
      let currentPage = 1;
      const fixedSnap = snapshotId || currentPlugin?.activeSnapshotId || undefined;
      while (true) {
        const res = await apiClient.queryPluginCapabilities(auth, selectedPluginId, {
          snapshotId: fixedSnap,
          search: withCurrentFilter ? capSearch.trim() || undefined : undefined,
          groupTag: withCurrentFilter ? capGroupTag.trim() || undefined : undefined,
          executableOnly: true,
          page: currentPage,
          pageSize: 200,
        });
        const items = res.items ?? [];
        for (const item of items) {
          if (item.id && item.executability === 'Executable') {
            collected.add(item.id);
          }
        }
        const filteredTotal = Number(res.filteredCount ?? items.length);
        if (items.length === 0 || currentPage * 200 >= filteredTotal) {
          break;
        }
        currentPage += 1;
      }
      setSelectedCapIds(Array.from(collected));
      setNotice(`已冻结加入共 ${collected.size} 项可用能力（跨页绑定快照 ${fixedSnap ?? '-'}）。`);
    } catch (err) {
      setError(formatBusinessSkillError(err, '跨页拉取能力列表失败'));
    } finally {
      setBusy(null);
    }
  };

  const handleSaveSelectionAndNext = async () => {
    if (!auth || !selectedPluginId || !window.codexflow) return;
    if (selectedCapIds.length === 0) {
      setError('请至少选择一个可用能力');
      return;
    }
    setBusy('save-selection');
    setError(null);
    setNotice(null);
    try {
      const targetName = skillName.trim() || `${currentPlugin?.name || '业务系统'}自动化 Skill`;
      const targetGoal = businessGoal.trim() || '按选择能力自动化处理业务流程';
      let currentDraft = draft;
      if (!currentDraft?.id) {
        currentDraft = await apiClient.createSkillDraft(auth, {
          pluginId: selectedPluginId,
          catalogSnapshotId: snapshotId || null,
          name: targetName,
          businessGoal: targetGoal,
          selectedCapabilityIds: selectedCapIds,
        });
        setDraft(currentDraft);
        if (!skillName.trim()) setSkillName(currentDraft.name ?? targetName);
      } else {
        const res = await window.codexflow.updateSkillDraftSelection({
          draftId: currentDraft.id,
          expectedRevision: currentDraft.revision ?? 1,
          catalogSnapshotId: snapshotId || currentDraft.catalogSnapshotId || '',
          selectedCapabilityIds: selectedCapIds,
        });
        if (!res.ok || !res.data) {
          setError(extractControlPlaneError(res, '保存能力选择失败'));
          return;
        }
        setDraft(res.data);
      }
      setStepIndex(2);
    } catch (err) {
      setError(formatBusinessSkillError(err, '保存草稿能力选择失败'));
    } finally {
      setBusy(null);
    }
  };

  const handleGenerateCandidate = async () => {
    if (!auth || !draft?.id || !window.codexflow) return;
    if (!skillName.trim() || !businessGoal.trim()) {
      setError('请填写 Skill 名称和清晰的业务目标描述');
      return;
    }
    if (!confirmModelScope) {
      setError('请先阅读并勾选“确认模型仅接收接口元信息与业务目标，不含本地密钥或业务数据”');
      return;
    }
    setBusy('generate');
    setError(null);
    setNotice(null);
    try {
      const updatedDraft = await apiClient.updateSkillDraft(auth, draft.id, {
        expectedRevision: draft.revision ?? 1,
        name: skillName.trim(),
        businessGoal: businessGoal.trim(),
        candidateDefinition: draft.candidateDefinition ?? null,
      });
      setDraft(updatedDraft);

      const genRes = await window.codexflow.generateSkillDraftCandidate({
        draftId: updatedDraft.id!,
        clientRequestId: newClientRequestId('gen'),
        confirmModelDataScope: true,
        includeSanitizedSamples,
      });
      if (!genRes.ok || !genRes.data) {
        setError(extractControlPlaneError(genRes, 'AI 生成候选方案失败'));
        return;
      }
      setDisclosedCategories(genRes.data.disclosedDataCategories ?? []);
      setDraft(genRes.data.draft);
      setNotice('已完成结构化 Skill 方案生成与服务端规范化。');
      if (genRes.data.draft.candidateDefinition) {
        setStepIndex(3);
      }
    } catch (err) {
      setError(formatBusinessSkillError(err, '生成候选方案失败'));
    } finally {
      setBusy(null);
    }
  };

  const handleValidateDraft = async () => {
    if (!auth || !draft?.id) return;
    setBusy('validate');
    setError(null);
    setNotice(null);
    try {
      const validated = await apiClient.validateSkillDraft(auth, draft.id);
      setDraft(validated);
      if ((validated.validationErrors?.length ?? 0) === 0) {
        setNotice('静态校验通过，未发现结构、绑定或风险规则问题。');
      } else {
        setError(`校验发现 ${validated.validationErrors?.length} 项问题，请先处理。`);
      }
    } catch (err) {
      setError(formatBusinessSkillError(err, '静态校验失败'));
    } finally {
      setBusy(null);
    }
  };

  const handleStartTestRun = async (allowWriteInTestRun: boolean) => {
    if (!draft?.id || !window.codexflow) return;
    const { inputs, error: inputErr } = parseDynamicInputs(
      draft.candidateDefinition?.inputs,
      inputDrafts,
    );
    if (inputErr || !inputs) {
      setError(inputErr || '请检查输入参数');
      return;
    }
    setBusy(allowWriteInTestRun ? 'test-write' : 'test-read');
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.startSkillDraftTestRun({
        draftId: draft.id,
        version: null,
        clientRequestId: newClientRequestId('test-run'),
        inputs,
        isTestRun: true,
        sessionId: activeSessionId,
        allowWriteInTestRun,
      });
      if (!res.ok || !res.data) {
        setError(extractControlPlaneError(res, '启动试运行失败'));
        return;
      }
      setTestRun(res.data);
      if (
        auth &&
        (res.data.status === 'Succeeded' ||
          res.data.status === 'PartialSucceeded' ||
          res.data.status === 'Failed')
      ) {
        try {
          const latestDraft = await apiClient.getSkillDraft(auth, draft.id);
          setDraft(latestDraft);
        } catch {
          // 忽略即时刷新失败，用户可手动重新校验
        }
      }
      setNotice(
        allowWriteInTestRun
          ? '已启动写入试运行（写入步骤将强制等待单次人工确认）。'
          : '已启动只读试运行。',
      );
    } catch (err) {
      setError(formatBusinessSkillError(err, '启动试运行失败'));
    } finally {
      setBusy(null);
    }
  };

  const handleTestRunUpdated = useCallback(
    async (updatedRun: SkillRunDto) => {
      setTestRun(updatedRun);
      if (
        auth &&
        draft?.id &&
        (updatedRun.status === 'Succeeded' ||
          updatedRun.status === 'PartialSucceeded' ||
          updatedRun.status === 'Failed')
      ) {
        try {
          const latestDraft = await apiClient.getSkillDraft(auth, draft.id);
          setDraft(latestDraft);
        } catch {
          // 忽略后台刷新细节错误，用户可手动重新校验
        }
      }
    },
    [auth, draft?.id],
  );

  const handlePublish = async () => {
    if (!draft?.id || !window.codexflow) return;
    setBusy('publish');
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.publishSkillDraft({
        draftId: draft.id,
        expectedRevision: draft.revision ?? 1,
        releaseNote: releaseNote.trim() || null,
      });
      if (!res.ok || !res.data) {
        setError(extractControlPlaneError(res, '发布 Skill 失败'));
        return;
      }
      setNotice(`已发布不可变版本 v${res.data.activeVersion}！`);
      await onPublished(res.data);
    } catch (err) {
      setError(formatBusinessSkillError(err, '发布 Skill 失败'));
    } finally {
      setBusy(null);
    }
  };

  const validationErrors = draft?.validationErrors ?? [];
  const candidate = draft?.candidateDefinition ?? null;
  const evidence = candidate?.validationEvidence;
  const canPublish =
    controlPlaneAvailable &&
    Boolean(candidate) &&
    validationErrors.length === 0 &&
    !draft?.needsRevalidation &&
    Boolean(evidence?.readOnlyTestRunId);

  return (
    <div className="space-y-4">
      {/* 5 步导航条 */}
      <div className="grid grid-cols-2 gap-2 md:grid-cols-5">
        {STEP_LABELS.map((label, idx) => {
          const active = idx === stepIndex;
          const completed = idx < stepIndex;
          return (
            <button
              key={label}
              type="button"
              onClick={() => {
                if (idx <= stepIndex || (idx === 3 && candidate) || (idx === 4 && candidate)) {
                  setStepIndex(idx);
                }
              }}
              className={`rounded-lg border px-3 py-2 text-left text-xs font-medium transition ${
                active
                  ? 'border-indigo-500 bg-indigo-600/20 text-indigo-200'
                  : completed
                    ? 'border-emerald-500/30 bg-emerald-950/20 text-emerald-200'
                    : 'border-slate-800 bg-slate-900/60 text-slate-400'
              }`}
            >
              {label}
            </button>
          );
        })}
      </div>

      {notice && (
        <div className="rounded-lg border border-emerald-500/40 bg-emerald-950/30 px-3 py-2 text-xs text-emerald-200">
          {notice}
        </div>
      )}
      {error && (
        <div className="rounded-lg border border-rose-500/40 bg-rose-950/30 px-3 py-2 text-xs text-rose-200">
          {error}
        </div>
      )}

      {/* 第 1 步：连接系统 */}
      {stepIndex === 0 && (
        <div className="space-y-4 rounded-xl border border-slate-800 bg-slate-900/70 p-4">
          <div className="text-sm font-semibold text-slate-100">第 1 步：选择已接入业务插件与快照</div>
          <label className="block space-y-1 text-xs">
            <span className="text-slate-300">目标业务系统插件</span>
            <select
              aria-label="选择业务插件"
              value={selectedPluginId}
              onChange={(e) => {
                setSelectedPluginId(e.target.value);
                setSnapshotId('');
                setDraft(null);
                setSelectedCapIds([]);
              }}
              className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
            >
              <option value="">-- 请选择插件（需先在「我的插件」页创建） --</option>
              {plugins
                .filter((p) => p.enabled !== false)
                .map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name} ({p.sourceType} · {p.connection?.environment ?? '-'} · 能力 {p.capabilityCount ?? 0} 项)
                  </option>
                ))}
            </select>
          </label>

          {currentPlugin && (
            <div className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-slate-800 bg-slate-950/60 p-3 text-xs">
              <div>
                <div className="text-slate-200">源地址：{currentPlugin.connection?.sourceUrl}</div>
                <div className="text-slate-400">
                  当前快照：{snapshotId || currentPlugin.activeSnapshotId || '尚未发现'}
                </div>
              </div>
              <button
                type="button"
                onClick={() => void handleDiscoverPluginInWizard()}
                disabled={!controlPlaneAvailable || busy !== null}
                className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-1.5 text-slate-100 hover:bg-slate-700 disabled:opacity-50"
              >
                {busy === 'discover' ? '发现中...' : '执行 / 刷新能力发现'}
              </button>
            </div>
          )}

          <div className="flex justify-end">
            <button
              type="button"
              onClick={handleStep1Next}
              disabled={!currentPlugin}
              className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-xs font-medium text-white hover:bg-indigo-500 disabled:opacity-50"
            >
              下一步：选择能力
              <ArrowRight className="h-3.5 w-3.5" />
            </button>
          </div>
        </div>
      )}

      {/* 第 2 步：跨页/筛选选择能力 */}
      {stepIndex === 1 && (
        <div className="space-y-4 rounded-xl border border-slate-800 bg-slate-900/70 p-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              <div className="text-sm font-semibold text-slate-100">第 2 步：挑选参与编排的能力</div>
              <div className="text-xs text-slate-400">
                共 {capCounts.total} 项，可选 {capCounts.executable} 项，已选 {selectedCapIds.length} 项
                （锁定快照 {snapshotId || '-'}）
              </div>
            </div>
            <div className="flex flex-wrap gap-2 text-xs">
              <button
                type="button"
                onClick={handleSelectCurrentPageExecutable}
                className="rounded-lg border border-slate-700 bg-slate-800 px-2.5 py-1 text-slate-200 hover:bg-slate-700"
              >
                选择本页可选
              </button>
              <button
                type="button"
                onClick={() => void collectAllExecutableAcrossPages(true)}
                disabled={busy !== null}
                className="rounded-lg border border-slate-700 bg-slate-800 px-2.5 py-1 text-slate-200 hover:bg-slate-700 disabled:opacity-50"
              >
                选择全部筛选结果
              </button>
              <button
                type="button"
                onClick={() => void collectAllExecutableAcrossPages(false)}
                disabled={busy !== null}
                className="rounded-lg border border-indigo-500/40 bg-indigo-500/15 px-2.5 py-1 text-indigo-200 hover:bg-indigo-500/25 disabled:opacity-50"
              >
                选择全部可用能力
              </button>
              <button
                type="button"
                onClick={() => setSelectedCapIds([])}
                className="rounded-lg border border-slate-700 px-2.5 py-1 text-slate-400 hover:bg-slate-800"
              >
                清空已选
              </button>
            </div>
          </div>

          <div className="flex flex-wrap items-center gap-2 text-xs">
            <div className="relative flex-1 min-w-[180px]">
              <Search className="pointer-events-none absolute left-2.5 top-2 h-3.5 w-3.5 text-slate-500" />
              <input
                value={capSearch}
                onChange={(e) => {
                  setCapSearch(e.target.value);
                  setCapPage(1);
                }}
                placeholder="按名称、路径或说明筛选..."
                className="w-full rounded-lg border border-slate-700 bg-slate-950 py-1.5 pl-8 pr-3 text-slate-100"
              />
            </div>
            <input
              value={capGroupTag}
              onChange={(e) => {
                setCapGroupTag(e.target.value);
                setCapPage(1);
              }}
              placeholder="分组 Tag"
              className="w-28 rounded-lg border border-slate-700 bg-slate-950 px-2.5 py-1.5 text-slate-100"
            />
            <label className="flex items-center gap-1.5 text-slate-300">
              <input
                type="checkbox"
                checked={capExecutableOnly}
                onChange={(e) => {
                  setCapExecutableOnly(e.target.checked);
                  setCapPage(1);
                }}
              />
              仅显示可执行
            </label>
          </div>

          <div className="max-h-72 space-y-1.5 overflow-y-auto">
            {capabilities.map((cap) => {
              const id = cap.id ?? '';
              const checked = selectedCapIds.includes(id);
              const disabled = cap.executability !== 'Executable';
              const riskInfo = getRiskBadgeInfo(cap.risk);
              return (
                <label
                  key={id}
                  className={`flex cursor-pointer items-center justify-between gap-3 rounded-lg border px-3 py-2 text-xs ${
                    checked
                      ? 'border-indigo-500/60 bg-indigo-950/25'
                      : 'border-slate-800 bg-slate-950/60'
                  } ${disabled ? 'opacity-50' : ''}`}
                >
                  <div className="flex items-center gap-2.5 min-w-0">
                    <input
                      type="checkbox"
                      checked={checked}
                      disabled={disabled}
                      onChange={() => id && toggleCapId(id)}
                    />
                    <div className="min-w-0">
                      <div className="truncate font-medium text-slate-100">
                        {cap.title || cap.nativeIdentity}
                      </div>
                      <div className="truncate font-mono text-[11px] text-slate-400">
                        {cap.nativeIdentity}
                      </div>
                    </div>
                  </div>
                  <span className={`shrink-0 rounded border px-1.5 py-0.5 text-[10px] ${riskInfo.className}`}>
                    {riskInfo.label}
                  </span>
                </label>
              );
            })}
          </div>

          <div className="flex items-center justify-between text-xs text-slate-400">
            <button
              type="button"
              disabled={capPage <= 1}
              onClick={() => setCapPage((p) => Math.max(1, p - 1))}
              className="rounded border border-slate-700 px-2.5 py-1 disabled:opacity-40"
            >
              上一页
            </button>
            <span>
              第 {capPage} 页（筛选匹配 {capCounts.filtered} 项）
            </span>
            <button
              type="button"
              disabled={capPage * 20 >= capCounts.filtered}
              onClick={() => setCapPage((p) => p + 1)}
              className="rounded border border-slate-700 px-2.5 py-1 disabled:opacity-40"
            >
              下一页
            </button>
          </div>

          <div className="flex justify-between pt-2">
            <button
              type="button"
              onClick={() => setStepIndex(0)}
              className="inline-flex items-center gap-1 rounded-lg border border-slate-700 px-3 py-1.5 text-xs text-slate-200 hover:bg-slate-800"
            >
              <ArrowLeft className="h-3.5 w-3.5" />
              上一步
            </button>
            <button
              type="button"
              onClick={() => void handleSaveSelectionAndNext()}
              disabled={!controlPlaneAvailable || busy !== null || selectedCapIds.length === 0}
              className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-xs font-medium text-white hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {busy === 'save-selection' ? '保存中...' : '保存选择并进入生成'}
              <ArrowRight className="h-3.5 w-3.5" />
            </button>
          </div>
        </div>
      )}

      {/* 第 3 步：描述业务目标与 AI 受控生成 */}
      {stepIndex === 2 && (
        <div className="space-y-4 rounded-xl border border-slate-800 bg-slate-900/70 p-4">
          <div className="text-sm font-semibold text-slate-100">第 3 步：描述业务目标并生成结构化 Skill</div>

          {!modelConfigured && (
            <div className="flex items-start gap-2 rounded-lg border border-amber-500/40 bg-amber-950/30 p-3 text-xs text-amber-200">
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
              <span>当前尚未配置外部对话模型；如服务端未设置本地模型，请先在右上角「设置」中配置模型参数。</span>
            </div>
          )}

          <label className="block space-y-1 text-xs">
            <span className="text-slate-300">Skill 名称</span>
            <input
              value={skillName}
              onChange={(e) => setSkillName(e.target.value)}
              placeholder="例如：超时未发货订单自动排查与补发"
              className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
            />
          </label>

          <label className="block space-y-1 text-xs">
            <span className="text-slate-300">业务目标描述（说明触发输入、关联字段、筛选条件与期望输出）</span>
            <textarea
              rows={4}
              value={businessGoal}
              onChange={(e) => setBusinessGoal(e.target.value)}
              placeholder="例如：按订单日期查询待处理订单，与物流记录按 orderId 关联过滤未揽收记录，生成摘要报告并在确认后批量更新状态。"
              className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
            />
          </label>

          <div className="space-y-2 rounded-lg border border-slate-800 bg-slate-950/70 p-3 text-xs">
            <label className="flex items-start gap-2 text-slate-200">
              <input
                type="checkbox"
                checked={confirmModelScope}
                onChange={(e) => setConfirmModelScope(e.target.checked)}
                className="mt-0.5"
              />
              <span>
                我已确认本次发送给模型的范围仅限：所选接口标题、摘要、输入输出 Schema 与以上业务目标（绝不包含本地凭证、Token、Cookie 或未脱敏业务数据）。
              </span>
            </label>
            <label className="flex items-center gap-2 text-slate-400">
              <input
                type="checkbox"
                checked={includeSanitizedSamples}
                onChange={(e) => setIncludeSanitizedSamples(e.target.checked)}
              />
              允许附带脱敏字段结构示例（可选）
            </label>
          </div>

          {disclosedCategories.length > 0 && (
            <div className="text-[11px] text-slate-400">
              最近一次模型外发类别：{disclosedCategories.join('、')}
            </div>
          )}

          {(draft?.clarificationQuestions?.length ?? 0) > 0 && (
            <div className="rounded-lg border border-amber-500/40 bg-amber-950/20 p-3 text-xs text-amber-200">
              <div className="font-medium">需要进一步澄清以下问题：</div>
              <ul className="mt-1 list-disc space-y-0.5 pl-4">
                {draft?.clarificationQuestions?.map((q, idx) => (
                  <li key={idx}>{q}</li>
                ))}
              </ul>
            </div>
          )}

          <div className="flex justify-between pt-2">
            <button
              type="button"
              onClick={() => setStepIndex(1)}
              className="inline-flex items-center gap-1 rounded-lg border border-slate-700 px-3 py-1.5 text-xs text-slate-200 hover:bg-slate-800"
            >
              <ArrowLeft className="h-3.5 w-3.5" />
              返回能力选择
            </button>
            <div className="flex gap-2">
              {candidate && (
                <button
                  type="button"
                  onClick={() => setStepIndex(3)}
                  className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-1.5 text-xs text-slate-200 hover:bg-slate-700"
                >
                  直接查看已有方案
                </button>
              )}
              <button
                type="button"
                onClick={() => void handleGenerateCandidate()}
                disabled={!controlPlaneAvailable || busy !== null}
                className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-xs font-medium text-white hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <Sparkles className="h-3.5 w-3.5" />
                {busy === 'generate' ? '生成中...' : '生成候选方案'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* 第 4 步：结构化方案预览、校验与只读/写入试运行 */}
      {stepIndex === 3 && (
        <div className="space-y-4 rounded-xl border border-slate-800 bg-slate-900/70 p-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              <div className="text-sm font-semibold text-slate-100">
                第 4 步：方案预览、静态校验与受控试运行
              </div>
              <div className="text-xs text-slate-400">
                最大外部请求 {candidate?.budgets?.maxExternalRequests ?? '-'} 次 · 最大读取{' '}
                {candidate?.budgets?.maxReadRecords ?? '-'} 条 · 最大写入批次{' '}
                {candidate?.budgets?.maxWriteBatchItems ?? '-'} 条
              </div>
            </div>
            <button
              type="button"
              onClick={() => void handleValidateDraft()}
              disabled={busy !== null}
              className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-1.5 text-xs text-slate-100 hover:bg-slate-700 disabled:opacity-50"
            >
              {busy === 'validate' ? '校验中...' : '校验方案'}
            </button>
          </div>

          {validationErrors.length > 0 && (
            <div className="rounded-lg border border-rose-500/40 bg-rose-950/30 p-3 text-xs text-rose-200">
              <div className="font-medium">静态校验问题：</div>
              <ul className="mt-1 list-disc space-y-0.5 pl-4">
                {validationErrors.map((errItem, idx) => (
                  <li key={`${errItem.field}-${idx}`}>
                    [{errItem.field}] {errItem.message}
                  </li>
                ))}
              </ul>
            </div>
          )}

          {candidate?.steps && candidate.steps.length > 0 && (
            <div className="space-y-1.5">
              <div className="text-xs font-medium text-slate-300">可读步骤链路</div>
              <div className="grid grid-cols-1 gap-2 md:grid-cols-2">
                {candidate.steps.map((step, idx) => (
                  <div
                    key={`${step.id ?? idx}`}
                    className="rounded-lg border border-slate-800 bg-slate-950/70 px-3 py-2 text-xs"
                  >
                    <div className="flex items-center justify-between">
                      <span className="font-medium text-slate-100">
                        {idx + 1}. {step.title || step.id}
                      </span>
                      <span className="rounded bg-slate-800 px-1.5 py-0.5 text-[10px] text-slate-300">
                        {step.type}
                      </span>
                    </div>
                    <div className="mt-1 text-[11px] text-slate-400">{summarizeStep(step)}</div>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* 动态试运行输入参数 */}
          <div className="space-y-2 rounded-lg border border-slate-800 bg-slate-950/60 p-3">
            <div className="text-xs font-medium text-slate-200">试运行输入参数</div>
            {Object.keys(candidate?.inputs?.properties ?? {}).length === 0 ? (
              <div className="text-xs text-slate-400">当前 Skill 无需额外输入参数，可直接启动试运行。</div>
            ) : (
              <div className="grid grid-cols-1 gap-2 md:grid-cols-2">
                {Object.entries(candidate?.inputs?.properties ?? {}).map(([key, prop]) => (
                  <label key={key} className="space-y-1 text-xs">
                    <span className="text-slate-300">
                      {key} ({prop.type || 'string'})
                      {(candidate?.inputs?.required ?? []).includes(key) ? ' *' : ''}
                    </span>
                    <input
                      value={inputDrafts[key] ?? ''}
                      onChange={(e) =>
                        setInputDrafts((prev) => ({ ...prev, [key]: e.target.value }))
                      }
                      placeholder={prop.description || key}
                      className="w-full rounded border border-slate-700 bg-slate-900 px-2.5 py-1.5 text-slate-100"
                    />
                  </label>
                ))}
              </div>
            )}

            <div className="flex flex-wrap justify-end gap-2 pt-2">
              <button
                type="button"
                onClick={() => void handleStartTestRun(false)}
                disabled={!controlPlaneAvailable || busy !== null}
                className="inline-flex items-center gap-1.5 rounded-lg bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-500 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <FlaskConical className="h-3.5 w-3.5" />
                只读试运行
              </button>
              <button
                type="button"
                onClick={() => void handleStartTestRun(true)}
                disabled={!controlPlaneAvailable || busy !== null}
                className="inline-flex items-center gap-1.5 rounded-lg border border-amber-500/50 bg-amber-600/20 px-3 py-1.5 text-xs font-medium text-amber-200 hover:bg-amber-600/30 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <Play className="h-3.5 w-3.5" />
                执行写入试运行
              </button>
            </div>
          </div>

          {testRun && (
            <SkillRunCard
              auth={auth}
              runId={testRun.runId}
              initialRun={testRun}
              controlPlaneAvailable={controlPlaneAvailable}
              onRunUpdated={(updated) => void handleTestRunUpdated(updated)}
            />
          )}

          <div className="flex justify-between pt-2">
            <button
              type="button"
              onClick={() => setStepIndex(2)}
              className="inline-flex items-center gap-1 rounded-lg border border-slate-700 px-3 py-1.5 text-xs text-slate-200 hover:bg-slate-800"
            >
              <ArrowLeft className="h-3.5 w-3.5" />
              返回描述与生成
            </button>
            <button
              type="button"
              onClick={() => setStepIndex(4)}
              disabled={!candidate}
              className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-xs font-medium text-white hover:bg-indigo-500 disabled:opacity-50"
            >
              下一步：发布不可变版本
              <ArrowRight className="h-3.5 w-3.5" />
            </button>
          </div>
        </div>
      )}

      {/* 第 5 步：发布门禁检查与正式发布 */}
      {stepIndex === 4 && (
        <div className="space-y-4 rounded-xl border border-slate-800 bg-slate-900/70 p-4">
          <div className="text-sm font-semibold text-slate-100">第 5 步：发布为不可变业务 Skill 版本</div>

          <div className="space-y-2 rounded-lg border border-slate-800 bg-slate-950/70 p-3 text-xs">
            <div className="flex items-center gap-2">
              <CheckCircle2
                className={`h-4 w-4 ${
                  validationErrors.length === 0 && !draft?.needsRevalidation
                    ? 'text-emerald-400'
                    : 'text-slate-500'
                }`}
              />
              <span className="text-slate-200">
                静态结构与绑定校验：{validationErrors.length === 0 ? '通过' : '存在错误'}
                {draft?.needsRevalidation ? '（草稿需要重新校验）' : ''}
              </span>
            </div>
            <div className="flex items-center gap-2">
              <CheckCircle2
                className={`h-4 w-4 ${evidence?.readOnlyTestRunId ? 'text-emerald-400' : 'text-slate-500'}`}
              />
              <span className="text-slate-200">
                只读试运行证据：
                {evidence?.readOnlyTestRunId ? `已记录 (${evidence.readOnlyTestRunId})` : '尚未完成只读试运行'}
              </span>
            </div>
            <div className="flex items-center gap-2">
              <CheckCircle2
                className={`h-4 w-4 ${evidence?.writeStepVerified ? 'text-emerald-400' : 'text-amber-400'}`}
              />
              <span className="text-slate-200">
                写入步骤试运行验证：
                {evidence?.writeStepVerified
                  ? '已验证 (writeStepVerified=true)'
                  : '未做真实写入试运行（允许带 WriteStepUnverified 标记发布，但正式运行时写确认卡将给出醒目黄色警告）'}
              </span>
            </div>
          </div>

          <label className="block space-y-1 text-xs">
            <span className="text-slate-300">发布说明（可选）</span>
            <input
              value={releaseNote}
              onChange={(e) => setReleaseNote(e.target.value)}
              placeholder="例如：v1 首版上线，已完成测试环境只读验证"
              className="w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100"
            />
          </label>

          <div className="flex justify-between pt-2">
            <button
              type="button"
              onClick={() => setStepIndex(3)}
              className="inline-flex items-center gap-1 rounded-lg border border-slate-700 px-3 py-1.5 text-xs text-slate-200 hover:bg-slate-800"
            >
              <ArrowLeft className="h-3.5 w-3.5" />
              返回测试
            </button>
            <button
              type="button"
              onClick={() => void handlePublish()}
              disabled={!canPublish || busy !== null}
              className="inline-flex items-center gap-1.5 rounded-lg bg-emerald-600 px-4 py-2 text-xs font-medium text-white hover:bg-emerald-500 disabled:cursor-not-allowed disabled:opacity-50"
            >
              <Rocket className="h-3.5 w-3.5" />
              {busy === 'publish' ? '发布中...' : '发布不可变版本'}
            </button>
          </div>
        </div>
      )}
    </div>
  );
};
