import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  AlertTriangle,
  CheckCircle2,
  FileText,
  History,
  Play,
  RefreshCw,
} from 'lucide-react';
import type {
  PublishedSkillDto,
  SkillMarkdownPreviewDto,
  SkillRunDto,
  StartSkillRunRequest,
} from '@shared/ipc';
import { apiClient, type ApiAuthContext } from '../../lib/apiClient';
import { MarkdownRenderer } from '../MarkdownRenderer';
import {
  extractControlPlaneError,
  formatBusinessSkillError,
  getRunStatusMeta,
  newClientRequestId,
} from './businessSkillHelpers';
import { SkillRunCard } from './SkillRunCard';

interface PublishedSkillsPanelProps {
  auth: ApiAuthContext | null;
  skills: PublishedSkillDto[];
  initialSelectedSkillId?: string | null;
  activeSessionId: string | null;
  controlPlaneAvailable: boolean;
  onRefreshSkills: () => Promise<void>;
}

export const PublishedSkillsPanel: React.FC<PublishedSkillsPanelProps> = ({
  auth,
  skills,
  initialSelectedSkillId = null,
  activeSessionId,
  controlPlaneAvailable,
  onRefreshSkills,
}) => {
  const [selectedSkillId, setSelectedSkillId] = useState<string | null>(initialSelectedSkillId);
  const [selectedVersion, setSelectedVersion] = useState<number | undefined>(undefined);
  const [markdownPreview, setMarkdownPreview] = useState<SkillMarkdownPreviewDto | null>(null);
  const [runs, setRuns] = useState<SkillRunDto[]>([]);
  const [activeRun, setActiveRun] = useState<SkillRunDto | null>(null);
  const [inputsJson, setInputsJson] = useState('{}');
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const appliedInitialSkillIdRef = useRef<string | null>(null);
  const detailReqSeqRef = useRef(0);

  useEffect(() => {
    if (initialSelectedSkillId && initialSelectedSkillId !== appliedInitialSkillIdRef.current) {
      appliedInitialSkillIdRef.current = initialSelectedSkillId;
      setSelectedSkillId(initialSelectedSkillId);
    }
  }, [initialSelectedSkillId]);

  useEffect(() => {
    if (skills.length === 0) {
      setSelectedSkillId(null);
    } else if (!selectedSkillId || !skills.some((s) => s.id === selectedSkillId)) {
      setSelectedSkillId(skills[0]?.id ?? null);
    }
  }, [skills, selectedSkillId]);

  const selectedSkill = skills.find((s) => s.id === selectedSkillId) ?? null;

  useEffect(() => {
    if (selectedSkill?.activeVersion !== undefined) {
      setSelectedVersion(Number(selectedSkill.activeVersion));
    } else {
      setSelectedVersion(undefined);
    }
    setActiveRun(null);
    setInputsJson('{}');
    setNotice(null);
    setError(null);
  }, [selectedSkillId, selectedSkill?.activeVersion]);

  const loadSkillDetail = useCallback(async () => {
    if (!auth || !selectedSkillId) {
      setMarkdownPreview(null);
      setRuns([]);
      return;
    }
    const seq = ++detailReqSeqRef.current;
    try {
      const [md, runList] = await Promise.all([
        apiClient.getPublishedSkillMarkdown(auth, selectedSkillId, selectedVersion),
        apiClient.listSkillRuns(auth, selectedSkillId),
      ]);
      if (seq !== detailReqSeqRef.current) return;
      setMarkdownPreview(md);
      setRuns(runList);
      setActiveRun((prev) =>
        prev && runList.some((r) => r.runId === prev.runId) ? prev : (runList[0] ?? null),
      );
    } catch (err) {
      if (seq !== detailReqSeqRef.current) return;
      setError(formatBusinessSkillError(err, '加载 Skill 详情失败'));
    }
  }, [auth, selectedSkillId, selectedVersion]);

  useEffect(() => {
    void loadSkillDetail();
  }, [loadSkillDetail]);

  const handleActivateVersion = async (version: number) => {
    if (!selectedSkillId || !window.codexflow) return;
    setBusy('activate');
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.activateSkillVersion({
        skillId: selectedSkillId,
        version,
      });
      if (!res.ok) {
        setError(extractControlPlaneError(res, '切换版本失败'));
        return;
      }
      setNotice(`已切换激活版本至 v${version}。`);
      await onRefreshSkills();
      await loadSkillDetail();
    } catch (err) {
      setError(formatBusinessSkillError(err, '切换版本失败'));
    } finally {
      setBusy(null);
    }
  };

  const handleStartRun = async () => {
    if (!selectedSkillId || !window.codexflow) return;
    let parsedInputs: unknown;
    try {
      parsedInputs = JSON.parse(inputsJson.trim() || '{}');
      if (!parsedInputs || typeof parsedInputs !== 'object' || Array.isArray(parsedInputs)) {
        setError('运行参数必须是 JSON 对象（例如 {} 或 {"orderId": "SO-1001"}）');
        return;
      }
    } catch {
      setError('运行参数 JSON 格式无效，请检查语法');
      return;
    }

    setBusy('run');
    setError(null);
    setNotice(null);
    try {
      const res = await window.codexflow.startPublishedSkillRun({
        skillId: selectedSkillId,
        version: selectedVersion ?? null,
        clientRequestId: newClientRequestId('run'),
        inputs: parsedInputs as StartSkillRunRequest['inputs'],
        isTestRun: false,
        sessionId: activeSessionId,
        allowWriteInTestRun: false,
      });
      if (!res.ok || !res.data) {
        setError(extractControlPlaneError(res, '启动 Skill 运行失败'));
        return;
      }
      setActiveRun(res.data);
      setNotice(`已启动运行 ${res.data.runId}。`);
      await loadSkillDetail();
    } catch (err) {
      setError(formatBusinessSkillError(err, '启动 Skill 运行失败'));
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="space-y-4">
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

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-12">
        {/* 左侧已发布 Skill 列表 */}
        <div className="space-y-2 lg:col-span-4">
          {skills.length === 0 ? (
            <div className="rounded-xl border border-dashed border-slate-800 bg-slate-900/40 p-6 text-center text-xs text-slate-400">
              暂无已发布的业务 Skill。请切换至「五步向导」创建并发布首个 Skill。
            </div>
          ) : (
            skills.map((skill) => {
              const active = skill.id === selectedSkillId;
              return (
                <button
                  key={skill.id}
                  type="button"
                  onClick={() => setSelectedSkillId(skill.id ?? null)}
                  className={`w-full rounded-xl border p-3 text-left transition ${
                    active
                      ? 'border-indigo-500/60 bg-indigo-950/25'
                      : 'border-slate-800 bg-slate-900/60 hover:border-slate-700'
                  }`}
                >
                  <div className="flex items-center justify-between gap-2">
                    <span className="truncate text-sm font-medium text-slate-100">{skill.name}</span>
                    <span className="rounded bg-indigo-500/20 px-1.5 py-0.5 text-[10px] text-indigo-200">
                      v{skill.activeVersion ?? 1}
                    </span>
                  </div>
                  <div className="mt-1 line-clamp-2 text-[11px] text-slate-400">
                    {skill.description || '无描述'}
                  </div>
                  <div className="mt-2 text-[10px] text-slate-500">
                    环境：{skill.environment || '-'} · 共 {skill.versions?.length ?? 0} 个不可变版本
                  </div>
                </button>
              );
            })
          )}
        </div>

        {/* 右侧 Skill 版本管理、只读 SKILL.md 预览与运行控制台 */}
        <div className="space-y-4 lg:col-span-8">
          {selectedSkill ? (
            <>
              <div className="space-y-3 rounded-xl border border-slate-800 bg-slate-900/70 p-4">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div>
                    <h3 className="text-sm font-semibold text-slate-100">{selectedSkill.name}</h3>
                    <p className="text-xs text-slate-400">{selectedSkill.description}</p>
                  </div>
                  <button
                    type="button"
                    onClick={() => void loadSkillDetail()}
                    className="inline-flex items-center gap-1 rounded-lg border border-slate-700 bg-slate-800 px-2.5 py-1 text-xs text-slate-200 hover:bg-slate-700"
                  >
                    <RefreshCw className="h-3.5 w-3.5" />
                    刷新记录
                  </button>
                </div>

                {/* 版本列表与不可变回滚切换 */}
                <div className="space-y-1.5 border-t border-slate-800 pt-3">
                  <div className="text-xs font-medium text-slate-300">不可变版本与状态</div>
                  <div className="flex flex-wrap gap-2">
                    {(selectedSkill.versions ?? []).map((ver) => {
                      const verNum = Number(ver.version);
                      const isCurrentActive = verNum === Number(selectedSkill.activeVersion);
                      const isViewing = verNum === selectedVersion;
                      return (
                        <div
                          key={verNum}
                          className={`flex items-center gap-2 rounded-lg border px-2.5 py-1.5 text-xs ${
                            isViewing
                              ? 'border-indigo-500/60 bg-indigo-950/30'
                              : 'border-slate-800 bg-slate-950/60'
                          }`}
                        >
                          <button
                            type="button"
                            onClick={() => setSelectedVersion(verNum)}
                            className="font-medium text-slate-100 hover:text-indigo-300"
                          >
                            v{verNum}
                          </button>
                          {isCurrentActive && (
                            <span className="inline-flex items-center gap-0.5 text-[10px] text-emerald-300">
                              <CheckCircle2 className="h-3 w-3" /> 激活中
                            </span>
                          )}
                          {!ver.writeStepVerified && (
                            <span
                              title="写步骤尚未通过试运行验证"
                              className="inline-flex items-center gap-0.5 text-[10px] text-amber-300"
                            >
                              <AlertTriangle className="h-3 w-3" /> 写未实测
                            </span>
                          )}
                          {ver.needsRevalidation && (
                            <span className="text-[10px] text-rose-300">需重验</span>
                          )}
                          {!isCurrentActive && (
                            <button
                              type="button"
                              onClick={() => void handleActivateVersion(verNum)}
                              disabled={!controlPlaneAvailable || busy !== null}
                              className="rounded border border-slate-700 px-1.5 py-0.5 text-[10px] text-slate-200 hover:bg-slate-800 disabled:opacity-40"
                            >
                              切换/回滚
                            </button>
                          )}
                        </div>
                      );
                    })}
                  </div>
                </div>

                {/* 运行触发面板 */}
                <div className="space-y-2 border-t border-slate-800 pt-3">
                  <label className="block space-y-1 text-xs">
                    <span className="text-slate-300">运行输入参数 (JSON 对象)</span>
                    <textarea
                      rows={3}
                      value={inputsJson}
                      onChange={(e) => setInputsJson(e.target.value)}
                      className="w-full rounded-lg border border-slate-700 bg-slate-950 p-2.5 font-mono text-xs text-slate-100"
                    />
                  </label>
                  <div className="flex justify-end">
                    <button
                      type="button"
                      onClick={() => void handleStartRun()}
                      disabled={!controlPlaneAvailable || busy !== null}
                      className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-xs font-medium text-white hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      <Play className="h-3.5 w-3.5" />
                      {busy === 'run' ? '启动中...' : `执行 Skill (v${selectedVersion ?? selectedSkill.activeVersion})`}
                    </button>
                  </div>
                </div>
              </div>

              {/* 当前运行卡片 */}
              {activeRun && (
                <SkillRunCard
                  auth={auth}
                  runId={activeRun.runId}
                  initialRun={activeRun}
                  controlPlaneAvailable={controlPlaneAvailable}
                  onRunUpdated={(updated) => {
                    setActiveRun(updated);
                    setRuns((prev) =>
                      prev.map((item) => (item.runId === updated.runId ? updated : item)),
                    );
                  }}
                />
              )}

              {/* 历史运行列表 */}
              {runs.length > 0 && (
                <div className="space-y-2 rounded-xl border border-slate-800 bg-slate-900/70 p-4">
                  <div className="flex items-center gap-1.5 text-xs font-semibold text-slate-200">
                    <History className="h-3.5 w-3.5 text-slate-400" />
                    最近运行记录 ({runs.length})
                  </div>
                  <div className="space-y-1">
                    {runs.slice(0, 8).map((item) => {
                      const meta = getRunStatusMeta(item.status);
                      return (
                        <button
                          key={item.runId}
                          type="button"
                          onClick={() => setActiveRun(item)}
                          className={`flex w-full items-center justify-between rounded-lg border px-3 py-1.5 text-xs ${
                            activeRun?.runId === item.runId
                              ? 'border-indigo-500/60 bg-indigo-950/25'
                              : 'border-slate-800 bg-slate-950/50 hover:border-slate-700'
                          }`}
                        >
                          <span className="font-mono text-slate-200">{item.runId}</span>
                          <span className={`rounded-full border px-2 py-0.5 text-[10px] ${meta.badgeClass}`}>
                            {meta.label} · v{item.skillVersion}
                          </span>
                        </button>
                      );
                    })}
                  </div>
                </div>
              )}

              {/* 导出 SKILL.md 只读预览 */}
              {markdownPreview && (
                <div className="space-y-2 rounded-xl border border-slate-800 bg-slate-900/70 p-4">
                  <div className="flex items-center justify-between text-xs">
                    <span className="inline-flex items-center gap-1.5 font-semibold text-slate-200">
                      <FileText className="h-3.5 w-3.5 text-indigo-400" />
                      导出 SKILL.md 只读预览 (v{markdownPreview.version})
                    </span>
                    <span className="font-mono text-[10px] text-slate-500">
                      Hash: {markdownPreview.contentHash.slice(0, 12)}
                    </span>
                  </div>
                  <div className="max-h-72 overflow-y-auto rounded-lg border border-slate-800 bg-slate-950/70 p-3">
                    <MarkdownRenderer content={markdownPreview.markdown} allowImages={false} />
                  </div>
                </div>
              )}
            </>
          ) : (
            <div className="rounded-xl border border-dashed border-slate-800 p-10 text-center text-xs text-slate-400">
              请在左侧选择一个已发布 Skill 查看详情与执行记录。
            </div>
          )}
        </div>
      </div>
    </div>
  );
};
