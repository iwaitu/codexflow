import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  AlertTriangle,
  Blocks,
  Puzzle,
  RefreshCw,
  ShieldOff,
  Wand2,
} from 'lucide-react';
import type { BusinessPluginDto, PublishedSkillDto } from '@shared/ipc';
import { apiClient, type ApiAuthContext } from '../../lib/apiClient';
import {
  formatBusinessSkillError,
  isBusinessSkillsDisabledError,
} from './businessSkillHelpers';
import { PluginCatalogPanel } from './PluginCatalogPanel';
import { BuiltInSkillCatalog } from './BuiltInSkillCatalog';
import { PublishedSkillsPanel } from './PublishedSkillsPanel';
import { SkillBuilderWizard } from './SkillBuilderWizard';

type HubTab = 'plugins' | 'skills' | 'wizard';

interface BusinessSkillsHubProps {
  auth: ApiAuthContext | null;
  instanceKey: string | null;
  activeSessionId: string | null;
  controlPlaneAvailable: boolean;
  modelConfigured: boolean;
}

export const BusinessSkillsHub: React.FC<BusinessSkillsHubProps> = ({
  auth,
  instanceKey,
  activeSessionId,
  controlPlaneAvailable,
  modelConfigured,
}) => {
  const [activeTab, setActiveTab] = useState<HubTab>('plugins');
  const [visitedTabs, setVisitedTabs] = useState<Record<HubTab, boolean>>({
    plugins: true,
    skills: false,
    wizard: false,
  });
  const [plugins, setPlugins] = useState<BusinessPluginDto[]>([]);
  const [skills, setSkills] = useState<PublishedSkillDto[]>([]);
  const [featureDisabled, setFeatureDisabled] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [wizardPluginId, setWizardPluginId] = useState<string | null>(null);
  const [selectedSkillId, setSelectedSkillId] = useState<string | null>(null);
  const loadRequestSeqRef = useRef(0);

  const switchTab = useCallback((nextTab: HubTab) => {
    setActiveTab(nextTab);
    setVisitedTabs((prev) => (prev[nextTab] ? prev : { ...prev, [nextTab]: true }));
  }, []);

  const loadAll = useCallback(async () => {
    if (!auth) {
      setPlugins([]);
      setSkills([]);
      return;
    }
    const reqSeq = ++loadRequestSeqRef.current;
    setLoading(true);
    setError(null);
    try {
      const [pluginList, skillList] = await Promise.all([
        apiClient.listBusinessPlugins(auth),
        apiClient.listPublishedSkills(auth),
      ]);
      if (reqSeq !== loadRequestSeqRef.current) return;
      setPlugins(pluginList);
      setSkills(skillList);
      setFeatureDisabled(false);
    } catch (err) {
      if (reqSeq !== loadRequestSeqRef.current) return;
      if (isBusinessSkillsDisabledError(err)) {
        setFeatureDisabled(true);
        setPlugins([]);
        setSkills([]);
      } else {
        setError(formatBusinessSkillError(err, '加载企业插件与 Skill 数据失败'));
      }
    } finally {
      if (reqSeq === loadRequestSeqRef.current) {
        setLoading(false);
      }
    }
  }, [auth]);

  useEffect(() => {
    setPlugins([]);
    setSkills([]);
    void loadAll();
  }, [loadAll, instanceKey]);

  const refreshPlugins = useCallback(async () => {
    if (!auth) return;
    try {
      const list = await apiClient.listBusinessPlugins(auth);
      setPlugins(list);
    } catch (err) {
      setError(`插件列表刷新失败：${formatBusinessSkillError(err, '请点击刷新重试')}`);
    }
  }, [auth]);

  const refreshSkills = useCallback(async () => {
    if (!auth) return;
    try {
      const list = await apiClient.listPublishedSkills(auth);
      setSkills(list);
    } catch (err) {
      setError(`Skill 列表刷新失败：${formatBusinessSkillError(err, '请点击刷新重试')}`);
    }
  }, [auth]);

  return (
    <div
      data-testid="business-skills-hub"
      className="flex min-h-0 flex-1 flex-col overflow-y-auto bg-slate-950 p-5"
    >
      <div className="mx-auto w-full max-w-6xl space-y-4">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-800 pb-4">
          <div>
            <h2 className="text-base font-semibold text-slate-100">
              内置企业插件与业务 Skill 构建器
            </h2>
            <p className="text-xs text-slate-400">
              安全接入企业 OpenAPI / MCP 系统，由人工把控风险分级与单次写入批准确认。
            </p>
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => void loadAll()}
              disabled={loading || !auth}
              className="inline-flex items-center gap-1.5 rounded-lg border border-slate-700 bg-slate-900 px-3 py-1.5 text-xs text-slate-200 hover:bg-slate-800 disabled:opacity-50"
            >
              <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
              刷新全部
            </button>
          </div>
        </div>

        <BuiltInSkillCatalog auth={auth} instanceKey={instanceKey} />

        {!controlPlaneAvailable && (
          <div
            data-testid="control-plane-readonly-banner"
            className="flex items-start gap-2 rounded-xl border border-amber-500/40 bg-amber-950/30 p-3 text-xs text-amber-200"
          >
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-300" />
            <div>
              当前处于只读模式（独立后端未导入 controlToken）。您仍可浏览已接入插件、能力目录、已发布 Skill 与历史运行记录；创建/修改插件、配置凭证、风险确认、试运行及写操作审批等控制面动作已禁用。
            </div>
          </div>
        )}

        {featureDisabled ? (
          <div
            data-testid="business-skills-disabled-banner"
            className="space-y-2 rounded-xl border border-slate-800 bg-slate-900/70 p-6 text-xs text-slate-300"
          >
            <div className="flex items-center gap-2 text-sm font-semibold text-slate-100">
              <ShieldOff className="h-4 w-4 text-amber-400" />
              企业插件与业务 Skill 构建器功能开关未开启 (BusinessSkills:Enabled=false)
            </div>
            <p className="leading-relaxed text-slate-400">
              当前宿主配置已关闭该能力。如需重新启用，请在启动桌面端或服务端配置中将{' '}
              <code className="rounded bg-slate-800 px-1.5 py-0.5 text-slate-200">
                BusinessSkills:Enabled
              </code>{' '}
              设为 <code className="rounded bg-slate-800 px-1.5 py-0.5 text-slate-200">true</code>{' '}
              后重启连接。
            </p>
          </div>
        ) : (
          <>
            {!auth && (
              <div className="rounded-lg border border-amber-500/40 bg-amber-950/30 px-3 py-2 text-xs text-amber-200">
                后端服务尚未就绪，连接恢复后会自动加载插件与 Skill 列表。
              </div>
            )}

            {error && (
              <div role="alert" className="rounded-lg border border-rose-500/40 bg-rose-950/30 px-3 py-2 text-xs text-rose-200">
                {error}
              </div>
            )}

            <div role="tablist" aria-label="业务技能功能导航" className="flex flex-wrap gap-2 border-b border-slate-800 pb-3">
              <button
                type="button"
                role="tab"
                aria-selected={activeTab === 'plugins'}
                onClick={() => switchTab('plugins')}
                className={`inline-flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-medium ${
                  activeTab === 'plugins'
                    ? 'bg-indigo-600 text-white'
                    : 'bg-slate-900 text-slate-300 hover:bg-slate-800'
                }`}
              >
                <Puzzle className="h-3.5 w-3.5" />
                我的插件 ({plugins.length})
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={activeTab === 'skills'}
                onClick={() => switchTab('skills')}
                className={`inline-flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-medium ${
                  activeTab === 'skills'
                    ? 'bg-indigo-600 text-white'
                    : 'bg-slate-900 text-slate-300 hover:bg-slate-800'
                }`}
              >
                <Blocks className="h-3.5 w-3.5" />
                我的 Skill ({skills.length})
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={activeTab === 'wizard'}
                onClick={() => switchTab('wizard')}
                className={`inline-flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-medium ${
                  activeTab === 'wizard'
                    ? 'bg-indigo-600 text-white'
                    : 'bg-slate-900 text-slate-300 hover:bg-slate-800'
                }`}
              >
                <Wand2 className="h-3.5 w-3.5" />
                五步构建向导
              </button>
            </div>

            {visitedTabs.plugins && (
              <div className={activeTab === 'plugins' ? '' : 'hidden'}>
                <PluginCatalogPanel
                  auth={auth}
                  plugins={plugins}
                  controlPlaneAvailable={controlPlaneAvailable}
                  onRefreshPlugins={refreshPlugins}
                  onStartWizardForPlugin={(pluginId) => {
                    setWizardPluginId(pluginId);
                    switchTab('wizard');
                  }}
                />
              </div>
            )}

            {visitedTabs.skills && (
              <div className={activeTab === 'skills' ? '' : 'hidden'}>
                <PublishedSkillsPanel
                  auth={auth}
                  skills={skills}
                  initialSelectedSkillId={selectedSkillId}
                  activeSessionId={activeSessionId}
                  controlPlaneAvailable={controlPlaneAvailable}
                  onRefreshSkills={refreshSkills}
                />
              </div>
            )}

            {visitedTabs.wizard && (
              <div className={activeTab === 'wizard' ? '' : 'hidden'}>
                <SkillBuilderWizard
                  auth={auth}
                  plugins={plugins}
                  initialPluginId={wizardPluginId}
                  activeSessionId={activeSessionId}
                  controlPlaneAvailable={controlPlaneAvailable}
                  modelConfigured={modelConfigured}
                  onRefreshPlugins={refreshPlugins}
                  onPublished={async (published) => {
                    await refreshSkills();
                    setSelectedSkillId(published.id ?? null);
                    switchTab('skills');
                  }}
                />
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
};
