import { CheckSquare, Layers, Settings } from 'lucide-react';
import type { MobileTab } from '@/store/useMobileStore';

export function BottomTabBar({
  activeTab,
  pendingCount,
  onSelectTab,
}: {
  readonly activeTab: MobileTab;
  readonly pendingCount: number;
  readonly onSelectTab: (tab: MobileTab) => void;
}) {
  const items: Array<{
    id: MobileTab;
    label: string;
    Icon: typeof Layers;
    badge?: number;
  }> = [
    { id: 'sessions', label: '会话', Icon: Layers },
    { id: 'approvals', label: '审批', Icon: CheckSquare, badge: pendingCount },
    { id: 'settings', label: '设置', Icon: Settings },
  ];

  return (
    <nav
      aria-label="主导航"
      className="pb-safe shrink-0 border-t border-slate-800/90 bg-slate-950/95 backdrop-blur"
    >
      <div className="grid h-14 grid-cols-3">
        {items.map(({ id, label, Icon, badge }) => {
          const active = activeTab === id;
          return (
            <button
              key={id}
              type="button"
              onClick={() => onSelectTab(id)}
              className={`relative flex min-h-[44px] flex-col items-center justify-center gap-0.5 text-xs font-medium transition-colors ${
                active ? 'text-sky-400' : 'text-slate-400 hover:text-slate-200'
              }`}
            >
              <div className="relative">
                <Icon className="h-5 w-5" />
                {badge !== undefined && badge > 0 ? (
                  <span className="absolute -right-2.5 -top-1.5 min-w-[16px] rounded-full bg-amber-500 px-1 text-center text-[10px] font-bold leading-4 text-slate-950">
                    {badge > 99 ? '99+' : badge}
                  </span>
                ) : null}
              </div>
              <span>{label}</span>
            </button>
          );
        })}
      </div>
    </nav>
  );
}
