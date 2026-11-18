import React, { useEffect, useRef, useState } from 'react';
import { Bot, ChevronDown, Code2, Plus, ShieldAlert, Sparkles } from 'lucide-react';
import type { SessionWorkMode } from '../lib/apiClient';

interface WorkModeMenuProps {
  workMode: SessionWorkMode;
  planModeActive?: boolean;
  controlPlaneAvailable?: boolean;
  placement?: 'top' | 'bottom';
  disabled?: boolean;
  testId?: string;
  onOpenDailyAssistant?: () => Promise<void> | void;
  onCreateDailySession?: () => Promise<void> | void;
  onCreateDevSession?: () => Promise<void> | void;
  onTogglePlanMode?: () => Promise<void> | void;
}

export const WorkModeMenu: React.FC<WorkModeMenuProps> = ({
  workMode,
  planModeActive = false,
  controlPlaneAvailable = true,
  placement = 'bottom',
  disabled = false,
  testId,
  onOpenDailyAssistant,
  onCreateDailySession,
  onCreateDevSession,
  onTogglePlanMode,
}) => {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const isDaily = workMode === 'Daily';

  useEffect(() => {
    if (!open) return undefined;
    const handlePointerDown = (event: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(event.target as Node)) {
        setOpen(false);
      }
    };
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', handlePointerDown);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('mousedown', handlePointerDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [open]);

  const runAction = (fn?: () => Promise<void> | void) => {
    setOpen(false);
    if (fn) {
      void fn();
    }
  };

  return (
    <div ref={rootRef} className="relative inline-flex shrink-0">
      <button
        type="button"
        data-testid={testId}
        disabled={disabled}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((prev) => !prev)}
        title="点击查看当前会话模式或切换 / 新建日常模式、开发模式会话"
        className={`inline-flex items-center gap-1 rounded px-2 py-0.5 text-[11px] font-medium transition disabled:opacity-40 ${
          isDaily
            ? 'border border-emerald-500/40 bg-emerald-500/15 text-emerald-200 hover:bg-emerald-500/25'
            : 'border border-indigo-500/40 bg-indigo-500/15 text-indigo-200 hover:bg-indigo-500/25'
        }`}
      >
        <span>{isDaily ? '日常模式' : '开发模式'}</span>
        <ChevronDown className="h-3 w-3 opacity-75" />
      </button>

      {open && (
        <div
          role="menu"
          aria-label="工作模式菜单"
          className={`absolute left-0 z-50 w-64 rounded-xl border border-slate-700/90 bg-slate-950/95 p-1.5 text-xs text-slate-200 shadow-2xl backdrop-blur ${
            placement === 'top' ? 'bottom-full mb-1.5' : 'top-full mt-1.5'
          }`}
        >
          <div className="mb-1 rounded-lg bg-slate-900/80 px-2.5 py-1.5 text-[11px] leading-relaxed text-slate-400">
            当前会话已锁定为{' '}
            <span className={isDaily ? 'font-semibold text-emerald-300' : 'font-semibold text-indigo-300'}>
              {isDaily ? '日常模式 (Daily)' : '开发模式 (Development)'}
            </span>
            。切换模式将跳转到日常助手或新建独立会话。
          </div>

          {onOpenDailyAssistant && (
            <button
              type="button"
              role="menuitem"
              onClick={() => runAction(onOpenDailyAssistant)}
              className="flex w-full items-center gap-2 rounded-lg px-2.5 py-2 text-left text-slate-200 hover:bg-emerald-500/15 hover:text-emerald-200"
            >
              <Bot className="h-3.5 w-3.5 shrink-0 text-emerald-400" />
              <div className="min-w-0">
                <div className="font-medium">切换到「日常助手」</div>
                <div className="text-[10px] text-slate-400">打开专属 Daily 日常办公空间</div>
              </div>
            </button>
          )}

          {onCreateDailySession && (
            <button
              type="button"
              role="menuitem"
              onClick={() => runAction(onCreateDailySession)}
              className="flex w-full items-center gap-2 rounded-lg px-2.5 py-2 text-left text-slate-200 hover:bg-slate-800"
            >
              <Plus className="h-3.5 w-3.5 shrink-0 text-emerald-400" />
              <div className="min-w-0">
                <div className="font-medium">新建日常模式对话 (/daily)</div>
                <div className="text-[10px] text-slate-400">支持目录查看与受控 Shell 审批</div>
              </div>
            </button>
          )}

          {onCreateDevSession && (
            <button
              type="button"
              role="menuitem"
              onClick={() => runAction(onCreateDevSession)}
              className="flex w-full items-center gap-2 rounded-lg px-2.5 py-2 text-left text-slate-200 hover:bg-slate-800"
            >
              <Code2 className="h-3.5 w-3.5 shrink-0 text-indigo-400" />
              <div className="min-w-0">
                <div className="font-medium">新建开发模式对话 (/dev)</div>
                <div className="text-[10px] text-slate-400">代码工程、规划与多代理验证工作流</div>
              </div>
            </button>
          )}

          {onTogglePlanMode && (
            <button
              type="button"
              role="menuitem"
              disabled={planModeActive && !controlPlaneAvailable}
              onClick={() => runAction(onTogglePlanMode)}
              className="mt-1 flex w-full items-center gap-2 rounded-lg border-t border-slate-800/80 px-2.5 py-2 text-left text-slate-200 hover:bg-amber-500/15 hover:text-amber-200 disabled:opacity-40"
            >
              {planModeActive ? (
                <ShieldAlert className="h-3.5 w-3.5 shrink-0 text-amber-400" />
              ) : (
                <Sparkles className="h-3.5 w-3.5 shrink-0 text-amber-400" />
              )}
              <div className="min-w-0">
                <div className="font-medium">
                  {planModeActive ? '退出只读 Plan Mode (/plan off)' : '开启只读 Plan Mode (/plan)'}
                </div>
                <div className="text-[10px] text-slate-400">
                  {planModeActive ? '经控制面确认恢复当前模式工具' : '正交叠加只读规划限制，不改工作模式'}
                </div>
              </div>
            </button>
          )}
        </div>
      )}
    </div>
  );
};
