import { Coffee, FolderGit2, MonitorCheck, MonitorOff, Plus, RefreshCw } from 'lucide-react';
import { useMobileStore } from '@/store/useMobileStore';

export function SessionsView() {
  const pairingState = useMobileStore((s) => s.pairingState);
  const desktopOnline = useMobileStore((s) => s.desktopOnline);
  const sessions = useMobileStore((s) => s.sessions);
  const loadingSessions = useMobileStore((s) => s.loadingSessions);
  const refreshSessions = useMobileStore((s) => s.refreshSessions);
  const createDefaultSession = useMobileStore((s) => s.createDefaultSession);
  const createDailyAssistantSession = useMobileStore((s) => s.createDailyAssistantSession);
  const openSession = useMobileStore((s) => s.openSession);

  return (
    <div className="flex flex-1 flex-col overflow-y-auto px-4 py-4">
      <div className="mx-auto w-full max-w-2xl space-y-4">
        <div className="flex items-center justify-between rounded-2xl border border-slate-800 bg-slate-900/80 p-3.5">
          <div className="flex items-center gap-3">
            <div
              className={`flex h-10 w-10 items-center justify-center rounded-xl ${
                desktopOnline === false
                  ? 'bg-rose-500/15 text-rose-400'
                  : 'bg-emerald-500/15 text-emerald-400'
              }`}
            >
              {desktopOnline === false ? (
                <MonitorOff className="h-5 w-5" />
              ) : (
                <MonitorCheck className="h-5 w-5" />
              )}
            </div>
            <div>
              <div className="text-sm font-semibold text-slate-100">
                {pairingState?.desktopName || '桌面工作站'}
              </div>
              <div className="text-xs text-slate-400">
                {desktopOnline === false ? '离线或待唤醒' : '安全隧道在线'} · bv={pairingState?.bindingVersion ?? '-'}
              </div>
            </div>
          </div>
          <button
            type="button"
            onClick={() => void refreshSessions()}
            disabled={loadingSessions}
            aria-label="刷新工作区"
            className="flex h-11 w-11 items-center justify-center rounded-xl border border-slate-700 bg-slate-800 text-slate-200 hover:bg-slate-700 disabled:opacity-50"
          >
            <RefreshCw className={`h-4 w-4 ${loadingSessions ? 'animate-spin' : ''}`} />
          </button>
        </div>

        <div className="grid grid-cols-2 gap-2.5">
          <button
            type="button"
            onClick={() => void createDefaultSession('移动新建工作区')}
            className="flex min-h-[44px] items-center justify-center gap-1.5 rounded-xl bg-sky-500 px-3 text-xs font-semibold text-slate-950 hover:bg-sky-400"
          >
            <Plus className="h-4 w-4" />
            <span>新建工作区</span>
          </button>
          <button
            type="button"
            onClick={() => void createDailyAssistantSession()}
            className="flex min-h-[44px] items-center justify-center gap-1.5 rounded-xl border border-slate-700 bg-slate-800 px-3 text-xs font-medium text-slate-100 hover:bg-slate-700"
          >
            <Coffee className="h-4 w-4 text-amber-400" />
            <span>日常助手</span>
          </button>
        </div>

        <div className="space-y-2.5">
          <div className="flex items-center justify-between text-xs font-medium text-slate-400">
            <span>可远程访问的会话（{sessions.length}）</span>
          </div>

          {sessions.length === 0 ? (
            <div className="rounded-2xl border border-dashed border-slate-800 p-8 text-center text-sm text-slate-400">
              {loadingSessions
                ? '正在从桌面同步受权工作区...'
                : '暂无可远程访问的会话。可点击上方新建，或在桌面端授权外部目录。'}
            </div>
          ) : (
            sessions.map((item) => (
              <button
                key={item.sessionId}
                type="button"
                onClick={() => void openSession(item.sessionId)}
                className="flex w-full items-center justify-between rounded-2xl border border-slate-800 bg-slate-900/70 p-3.5 text-left transition hover:border-sky-500/40 hover:bg-slate-900"
              >
                <div className="flex min-w-0 items-center gap-3">
                  <FolderGit2 className="h-5 w-5 shrink-0 text-sky-400" />
                  <div className="min-w-0">
                    <div className="truncate text-sm font-semibold text-slate-100">
                      {item.name}
                    </div>
                    <div className="mt-1 flex flex-wrap items-center gap-1.5 text-[11px] text-slate-400">
                      <span className="rounded bg-slate-800 px-1.5 py-0.5 text-slate-300">
                        {item.workMode}
                      </span>
                      {item.planModeActive ? (
                        <span className="rounded bg-indigo-500/20 px-1.5 py-0.5 text-indigo-300">
                          Plan Mode
                        </span>
                      ) : null}
                      {item.isExternalWorkspace ? (
                        <span className="rounded bg-emerald-500/20 px-1.5 py-0.5 text-emerald-300">
                          已授权外部目录
                        </span>
                      ) : null}
                    </div>
                  </div>
                </div>
                <span className="ml-2 shrink-0 text-xs text-sky-400">进入</span>
              </button>
            ))
          )}
        </div>
      </div>
    </div>
  );
}
