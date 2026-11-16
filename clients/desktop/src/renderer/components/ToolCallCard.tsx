import React, { useState } from 'react';
import { CheckCircle2, ChevronDown, ChevronRight, Loader2, Terminal, XCircle } from 'lucide-react';
import type { UiToolCall } from '../lib/sessionRecovery';

interface ToolCallCardProps {
  tool: UiToolCall;
}

export const ToolCallCard: React.FC<ToolCallCardProps> = ({ tool }) => {
  const [expanded, setExpanded] = useState(tool.status === 'running' || tool.status === 'failed');

  const statusBadge = {
    called: <span className="rounded bg-slate-800 px-1.5 py-0.5 text-[11px] text-slate-300">已请求</span>,
    running: (
      <span className="inline-flex items-center gap-1 rounded bg-indigo-500/20 px-1.5 py-0.5 text-[11px] text-indigo-300">
        <Loader2 className="h-3 w-3 animate-spin" />
        运行中
      </span>
    ),
    completed: (
      <span className="inline-flex items-center gap-1 rounded bg-emerald-500/20 px-1.5 py-0.5 text-[11px] text-emerald-300">
        <CheckCircle2 className="h-3 w-3" />
        完成
      </span>
    ),
    failed: (
      <span className="inline-flex items-center gap-1 rounded bg-rose-500/20 px-1.5 py-0.5 text-[11px] text-rose-300">
        <XCircle className="h-3 w-3" />
        失败
      </span>
    ),
  }[tool.status];

  return (
    <div className="rounded-lg border border-slate-800 bg-slate-900/70 text-xs">
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        className="flex w-full items-center justify-between px-3 py-2 text-left hover:bg-slate-800/50"
      >
        <div className="flex items-center gap-2">
          {expanded ? <ChevronDown className="h-3.5 w-3.5 text-slate-400" /> : <ChevronRight className="h-3.5 w-3.5 text-slate-400" />}
          <Terminal className="h-3.5 w-3.5 text-indigo-400" />
          <span className="font-mono font-medium text-slate-200">{tool.name}</span>
          {statusBadge}
          {tool.outputTruncated && (
            <span className="rounded bg-amber-500/15 px-1.5 py-0.5 text-[11px] text-amber-300">
              输出已截断{tool.suppressedLines > 0 ? ` (${tool.suppressedLines} 行已合并)` : ''}
            </span>
          )}
        </div>
      </button>

      {expanded && (
        <div className="space-y-2 border-t border-slate-800 p-3">
          {tool.arguments && Object.keys(tool.arguments).length > 0 && (
            <div>
              <div className="mb-1 flex items-center gap-2 text-[11px] text-slate-400">
                <span>参数</span>
                {tool.argumentsTruncated && <span className="text-amber-300">(参数已截断)</span>}
              </div>
              <pre className="max-h-36 overflow-auto rounded bg-slate-950 p-2 font-mono text-[11px] text-slate-300">
                {JSON.stringify(tool.arguments, null, 2)}
              </pre>
            </div>
          )}

          {(tool.outputStdout || tool.outputStderr) && (
            <div>
              <div className="mb-1 text-[11px] text-slate-400">实时输出</div>
              <pre className="max-h-48 overflow-auto rounded bg-slate-950 p-2 font-mono text-[11px] text-slate-200">
                {tool.outputStdout}
                {tool.outputStderr ? `\n[stderr]\n${tool.outputStderr}` : ''}
              </pre>
            </div>
          )}

          {tool.result && (
            <div>
              <div className="mb-1 text-[11px] text-slate-400">结果</div>
              <pre className="max-h-48 overflow-auto rounded bg-slate-950 p-2 font-mono text-[11px] text-slate-300 whitespace-pre-wrap">
                {tool.result}
              </pre>
            </div>
          )}
        </div>
      )}
    </div>
  );
};
