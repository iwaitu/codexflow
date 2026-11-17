import React, { useMemo, useState } from 'react';
import { FileDiff, GitBranch, RefreshCw } from 'lucide-react';
import type { WorkspaceChanges } from '../lib/apiClient';
import { parseUnifiedDiff, type DiffLineKind } from '../lib/diffParser';

interface WorkspaceChangesPanelProps {
  sessionId: string | null;
  changes: WorkspaceChanges | null | undefined;
  isLoading: boolean;
  isFetching: boolean;
  error: string | null;
  onRefresh: () => void;
}

const LINE_STYLE_MAP: Record<DiffLineKind, string> = {
  add: 'bg-emerald-500/12 text-emerald-200',
  del: 'bg-rose-500/12 text-rose-200',
  hunk: 'bg-indigo-500/10 text-indigo-300',
  meta: 'text-slate-500',
  context: 'text-slate-300',
};

export const WorkspaceChangesPanel: React.FC<WorkspaceChangesPanelProps> = ({
  sessionId,
  changes,
  isLoading,
  isFetching,
  error,
  onRefresh,
}) => {
  const [selectedFile, setSelectedFile] = useState<string | null>(null);

  const sections = useMemo(
    () => parseUnifiedDiff(changes?.unifiedDiff ?? ''),
    [changes?.unifiedDiff],
  );

  const files = changes?.files ?? [];
  const activeFilter = selectedFile && files.some((f) => f.filePath === selectedFile) ? selectedFile : null;

  const visibleSections = useMemo(() => {
    if (!activeFilter) return sections;
    return sections.filter(
      (sec) => sec.filePath === activeFilter || sec.newPath === activeFilter || sec.oldPath === activeFilter,
    );
  }, [sections, activeFilter]);

  if (!sessionId) {
    return <div className="p-4 text-xs text-slate-500">请先选择一个工作区。</div>;
  }

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex items-center justify-between gap-2 border-b border-slate-800/80 px-3 py-2 text-xs">
        <div className="flex min-w-0 items-center gap-1.5 text-slate-300">
          <GitBranch className="h-3.5 w-3.5 shrink-0 text-indigo-400" />
          <span className="truncate font-mono text-[11px]">
            {changes?.baseRef ? `基线: ${changes.baseRef}` : '工作区改动'}
          </span>
        </div>
        <button
          type="button"
          onClick={onRefresh}
          title="刷新改动"
          aria-label="刷新改动"
          className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
        >
          <RefreshCw className={`h-3.5 w-3.5 ${isFetching ? 'animate-spin' : ''}`} />
        </button>
      </div>

      {error && (
        <div className="mx-3 mt-2 rounded-lg border border-rose-500/40 bg-rose-950/30 px-2.5 py-2 text-[11px] text-rose-200">
          {error}
        </div>
      )}

      {isLoading ? (
        <div className="p-4 text-xs text-slate-500">正在检查工作区 Git 改动...</div>
      ) : changes && !changes.hasGitRepository ? (
        <div className="p-4 text-xs leading-relaxed text-slate-400">
          当前工作区不是 Git 仓库，无法计算结构化 Diff。可通过“打开文件夹”选择 Git 工程查看代码变更。
        </div>
      ) : files.length === 0 && sections.length === 0 ? (
        <div className="p-4 text-xs text-slate-500">当前工作区干净，暂无未提交改动。</div>
      ) : (
        <div className="flex min-h-0 flex-1 flex-col">
          <div className="max-h-[38%] space-y-1 overflow-y-auto border-b border-slate-800 px-2 py-2">
            {activeFilter && (
              <button
                type="button"
                onClick={() => setSelectedFile(null)}
                className="mb-1 rounded bg-slate-800 px-2 py-0.5 text-[11px] text-indigo-300 hover:bg-slate-700"
              >
                显示全部文件 ({files.length})
              </button>
            )}
            {files.map((file) => {
              const filePath = file.filePath ?? '';
              const isSelected = activeFilter === filePath;
              return (
                <button
                  key={`${file.status}-${filePath}`}
                  type="button"
                  onClick={() => setSelectedFile(isSelected ? null : filePath)}
                  className={`flex w-full items-center justify-between gap-2 rounded-md px-2 py-1 text-left text-xs ${
                    isSelected
                      ? 'bg-indigo-500/20 text-indigo-100'
                      : 'text-slate-300 hover:bg-slate-900 hover:text-slate-100'
                  }`}
                >
                  <span className="flex min-w-0 items-center gap-1.5">
                    <FileDiff className="h-3.5 w-3.5 shrink-0 text-slate-400" />
                    <span className="truncate font-mono text-[11px]">{filePath}</span>
                  </span>
                  <span className="flex shrink-0 items-center gap-1.5 font-mono text-[10px]">
                    <span className="rounded bg-slate-800 px-1 py-0.5 text-slate-300">{file.status}</span>
                    {Number(file.additions ?? 0) > 0 && (
                      <span className="text-emerald-400">+{file.additions}</span>
                    )}
                    {Number(file.deletions ?? 0) > 0 && (
                      <span className="text-rose-400">-{file.deletions}</span>
                    )}
                  </span>
                </button>
              );
            })}
          </div>

          {changes?.truncated && (
            <div className="border-b border-amber-500/30 bg-amber-500/10 px-3 py-1 text-[11px] text-amber-200">
              Diff 内容较大，已截断展示。
            </div>
          )}

          <div className="min-h-0 flex-1 space-y-3 overflow-auto p-2 font-mono text-[11px] leading-5">
            {visibleSections.length === 0 ? (
              <div className="px-2 py-3 font-sans text-xs text-slate-500">
                选中文件暂无文本 Diff（可能是新增二进制或未追踪空文件）。
              </div>
            ) : (
              visibleSections.map((sec, sIdx) => (
                <div key={`${sec.filePath}-${sIdx}`} className="overflow-hidden rounded-lg border border-slate-800">
                  <div className="border-b border-slate-800 bg-slate-900/90 px-2.5 py-1 text-[11px] font-semibold text-slate-200">
                    {sec.filePath}
                  </div>
                  <div className="overflow-x-auto bg-slate-950">
                    {sec.lines.map((line, lIdx) => (
                      <div key={lIdx} className={`flex min-w-max px-2 ${LINE_STYLE_MAP[line.kind]}`}>
                        <span className="w-8 shrink-0 select-none pr-2 text-right text-slate-600">
                          {line.oldLineNumber ?? ''}
                        </span>
                        <span className="w-8 shrink-0 select-none pr-2 text-right text-slate-600">
                          {line.newLineNumber ?? ''}
                        </span>
                        <span className="whitespace-pre">{line.text || ' '}</span>
                      </div>
                    ))}
                  </div>
                </div>
              ))
            )}
          </div>
        </div>
      )}
    </div>
  );
};
