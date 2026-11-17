import React, { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  AlertTriangle,
  ArrowLeft,
  Check,
  Copy,
  FileCode2,
  Folder,
  Paperclip,
  RefreshCw,
  X,
} from 'lucide-react';
import { apiClient, type ApiAuthContext, type WorkspaceFileContent, type WorkspaceFileEntry } from '../lib/apiClient';
import { formatFileSize } from '../lib/attachments';

interface WorkspaceFilesPanelProps {
  auth: ApiAuthContext | null;
  sessionId: string | null;
  workspaceRevision: number;
  onAttachWorkspaceFile?: (file: { name: string; path: string; sizeBytes: number }) => void;
}

export const WorkspaceFilesPanel: React.FC<WorkspaceFilesPanelProps> = ({
  auth,
  sessionId,
  workspaceRevision,
  onAttachWorkspaceFile,
}) => {
  const [currentPath, setCurrentPath] = useState('');
  const [selectedFile, setSelectedFile] = useState<WorkspaceFileContent | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const [loadingFilePath, setLoadingFilePath] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    setCurrentPath('');
    setSelectedFile(null);
    setFileError(null);
  }, [sessionId]);

  const listingQuery = useQuery({
    queryKey: ['workspace-files', auth?.baseUrl, sessionId, currentPath, workspaceRevision],
    queryFn: () => apiClient.listFiles(auth!, sessionId!, currentPath),
    enabled: Boolean(auth && sessionId),
  });

  const handleOpenEntry = async (entry: WorkspaceFileEntry) => {
    if (!auth || !sessionId) return;
    const entryPath = entry.path ?? entry.name ?? '';
    if (entry.isDirectory) {
      setSelectedFile(null);
      setFileError(null);
      setCurrentPath(entryPath);
      return;
    }

    setLoadingFilePath(entryPath);
    setFileError(null);
    try {
      const content = await apiClient.readWorkspaceFileContent(auth, sessionId, entryPath);
      setSelectedFile(content);
      setCopied(false);
    } catch (error) {
      setFileError(error instanceof Error ? error.message : String(error));
    } finally {
      setLoadingFilePath(null);
    }
  };

  const goParent = () => {
    if (!currentPath) return;
    const parts = currentPath.split('/').filter(Boolean);
    parts.pop();
    setCurrentPath(parts.join('/'));
    setSelectedFile(null);
    setFileError(null);
  };

  const breadcrumbs = currentPath.split('/').filter(Boolean);
  const entries = listingQuery.data?.entries ?? [];

  if (!sessionId) {
    return <div className="p-4 text-xs text-slate-500">请先选择一个工作区。</div>;
  }

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex items-center justify-between gap-2 border-b border-slate-800/80 px-3 py-2 text-xs">
        <div className="flex min-w-0 flex-1 items-center gap-1 overflow-x-auto">
          {currentPath && (
            <button
              type="button"
              onClick={goParent}
              title="返回上级目录"
              aria-label="返回上级目录"
              className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
            >
              <ArrowLeft className="h-3.5 w-3.5" />
            </button>
          )}
          <button
            type="button"
            onClick={() => {
              setCurrentPath('');
              setSelectedFile(null);
            }}
            className={`rounded px-1.5 py-0.5 font-mono text-[11px] ${
              !currentPath ? 'bg-slate-800 text-slate-100' : 'text-slate-400 hover:bg-slate-800/70 hover:text-slate-200'
            }`}
          >
            /
          </button>
          {breadcrumbs.map((seg, idx) => {
            const target = breadcrumbs.slice(0, idx + 1).join('/');
            const isLast = idx === breadcrumbs.length - 1;
            return (
              <React.Fragment key={target}>
                <span className="text-slate-600">/</span>
                <button
                  type="button"
                  onClick={() => {
                    setCurrentPath(target);
                    setSelectedFile(null);
                  }}
                  className={`truncate rounded px-1.5 py-0.5 font-mono text-[11px] ${
                    isLast ? 'bg-slate-800 text-slate-100' : 'text-slate-400 hover:bg-slate-800/70 hover:text-slate-200'
                  }`}
                >
                  {seg}
                </button>
              </React.Fragment>
            );
          })}
        </div>

        <button
          type="button"
          onClick={() => void listingQuery.refetch()}
          title="刷新文件列表"
          aria-label="刷新文件列表"
          className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
        >
          <RefreshCw className={`h-3.5 w-3.5 ${listingQuery.isFetching ? 'animate-spin' : ''}`} />
        </button>
      </div>

      {listingQuery.data?.truncated && (
        <div className="border-b border-amber-500/30 bg-amber-500/10 px-3 py-1.5 text-[11px] text-amber-200">
          当前目录条目较多，仅展示前 {entries.length} 项。
        </div>
      )}

      {(listingQuery.error || fileError) && (
        <div className="mx-3 mt-2 rounded-lg border border-rose-500/40 bg-rose-950/30 px-2.5 py-2 text-[11px] text-rose-200">
          {fileError ||
            (listingQuery.error instanceof Error ? listingQuery.error.message : String(listingQuery.error))}
        </div>
      )}

      <div className={`min-h-0 overflow-y-auto px-2 py-2 ${selectedFile ? 'max-h-[45%] border-b border-slate-800' : 'flex-1'}`}>
        {listingQuery.isLoading ? (
          <div className="px-2 py-4 text-xs text-slate-500">正在加载工作区文件...</div>
        ) : entries.length === 0 ? (
          <div className="px-2 py-4 text-xs text-slate-500">该目录为空。</div>
        ) : (
          <div className="space-y-0.5">
            {entries.map((entry) => {
              const entryPath = entry.path ?? entry.name ?? '';
              const isSelected = selectedFile?.path === entryPath;
              const isLoading = loadingFilePath === entryPath;
              return (
                <button
                  key={entryPath}
                  type="button"
                  onClick={() => void handleOpenEntry(entry)}
                  className={`flex w-full items-center justify-between gap-2 rounded-md px-2 py-1.5 text-left text-xs ${
                    isSelected
                      ? 'bg-indigo-500/20 text-indigo-100'
                      : 'text-slate-300 hover:bg-slate-900 hover:text-slate-100'
                  }`}
                >
                  <span className="flex min-w-0 items-center gap-2">
                    {entry.isDirectory ? (
                      <Folder className="h-3.5 w-3.5 shrink-0 text-amber-400/90" />
                    ) : (
                      <FileCode2 className="h-3.5 w-3.5 shrink-0 text-slate-400" />
                    )}
                    <span className="truncate font-mono text-[11px]">{entry.name}</span>
                  </span>
                  <span className="shrink-0 text-[10px] text-slate-500">
                    {isLoading
                      ? '读取中...'
                      : entry.isDirectory
                        ? ''
                        : formatFileSize(Number(entry.sizeBytes ?? 0))}
                  </span>
                </button>
              );
            })}
          </div>
        )}
      </div>

      {selectedFile && (
        <div data-testid="workspace-file-preview" className="flex min-h-0 flex-1 flex-col bg-slate-950">
          <div className="flex items-center justify-between gap-2 border-b border-slate-800 bg-slate-900/70 px-3 py-1.5">
            <div className="min-w-0 flex-1">
              <div className="truncate font-mono text-[11px] text-slate-200" title={selectedFile.path}>
                {selectedFile.path}
              </div>
              <div className="text-[10px] text-slate-500">
                {formatFileSize(Number(selectedFile.sizeBytes ?? 0))} · {selectedFile.encoding}
              </div>
            </div>
            <div className="flex shrink-0 items-center gap-1">
              {onAttachWorkspaceFile && Number(selectedFile.sizeBytes ?? 0) > 0 && selectedFile.path && (
                <button
                  type="button"
                  onClick={() => {
                    const filePath = selectedFile.path!;
                    const parts = filePath.split('/');
                    onAttachWorkspaceFile({
                      name: parts[parts.length - 1] || filePath,
                      path: filePath,
                      sizeBytes: Number(selectedFile.sizeBytes ?? 0),
                    });
                  }}
                  title="作为附件引用到当前对话"
                  className="inline-flex items-center gap-1 rounded border border-slate-700 bg-slate-800 px-2 py-0.5 text-[11px] text-slate-200 hover:bg-slate-700"
                >
                  <Paperclip className="h-3 w-3" />
                  <span>引用</span>
                </button>
              )}
              {!selectedFile.isBinary && (
                <button
                  type="button"
                  onClick={() => {
                    void navigator.clipboard.writeText(selectedFile.content ?? '').then(() => {
                      setCopied(true);
                      setTimeout(() => setCopied(false), 1500);
                    });
                  }}
                  title="复制文件内容"
                  aria-label="复制文件内容"
                  className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
                >
                  {copied ? <Check className="h-3.5 w-3.5 text-emerald-400" /> : <Copy className="h-3.5 w-3.5" />}
                </button>
              )}
              <button
                type="button"
                onClick={() => setSelectedFile(null)}
                title="关闭预览"
                aria-label="关闭预览"
                className="rounded p-1 text-slate-400 hover:bg-slate-800 hover:text-slate-100"
              >
                <X className="h-3.5 w-3.5" />
              </button>
            </div>
          </div>

          {selectedFile.truncated && (
            <div className="flex items-center gap-1.5 border-b border-amber-500/30 bg-amber-500/10 px-3 py-1 text-[11px] text-amber-200">
              <AlertTriangle className="h-3.5 w-3.5 shrink-0" />
              <span>文件超出只读预览上限，仅展示部分内容。</span>
            </div>
          )}

          {selectedFile.isBinary ? (
            <div className="p-4 text-xs text-slate-400">
              该文件为二进制文件（{formatFileSize(Number(selectedFile.sizeBytes ?? 0))}），暂不支持文本预览。
            </div>
          ) : (
            <div className="min-h-0 flex-1 overflow-auto p-2 font-mono text-[11px] leading-5 text-slate-200">
              {(selectedFile.content ?? '').split(/\r?\n/).map((line, idx) => (
                <div key={idx} className="flex gap-3 hover:bg-slate-900/70">
                  <span className="w-8 shrink-0 select-none text-right text-slate-600">{idx + 1}</span>
                  <span className="whitespace-pre-wrap break-all">{line || ' '}</span>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
};
