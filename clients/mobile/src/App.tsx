import { useEffect } from 'react';
import { Capacitor } from '@capacitor/core';
import { Keyboard, KeyboardResize } from '@capacitor/keyboard';
import { StatusBar, Style } from '@capacitor/status-bar';
import { BottomTabBar } from '@/components/BottomTabBar';
import { ErrorBanner } from '@/components/ErrorBanner';
import { useMobileStore } from '@/store/useMobileStore';
import { ApprovalsView } from '@/views/ApprovalsView';
import { ChatView } from '@/views/ChatView';
import { PairView } from '@/views/PairView';
import { SessionsView } from '@/views/SessionsView';
import { SettingsView } from '@/views/SettingsView';

export function App() {
  const bootstrapped = useMobileStore((s) => s.bootstrapped);
  const pairingState = useMobileStore((s) => s.pairingState);
  const selectedSessionId = useMobileStore((s) => s.selectedSessionId);
  const activeTab = useMobileStore((s) => s.activeTab);
  const pendingTodos = useMobileStore((s) => s.pendingTodos);
  const lastError = useMobileStore((s) => s.lastError);

  const bootstrap = useMobileStore((s) => s.bootstrap);
  const disposeLifecycleListeners = useMobileStore((s) => s.disposeLifecycleListeners);
  const setActiveTab = useMobileStore((s) => s.setActiveTab);
  const clearError = useMobileStore((s) => s.clearError);

  useEffect(() => {
    void bootstrap();

    if (Capacitor.isNativePlatform()) {
      void StatusBar.setStyle({ style: Style.Dark }).catch(() => undefined);
      void Keyboard.setResizeMode({ mode: KeyboardResize.Body }).catch(() => undefined);
    }

    return () => {
      void disposeLifecycleListeners();
    };
  }, [bootstrap, disposeLifecycleListeners]);

  if (!bootstrapped) {
    return (
      <div className="flex h-full items-center justify-center bg-slate-950 text-sm text-slate-400">
        正在初始化安全密钥环境...
      </div>
    );
  }

  return (
    <div className="flex h-full flex-col bg-slate-950 text-slate-100">
      <ErrorBanner error={lastError} onDismiss={clearError} />

      {!pairingState ? (
        <PairView />
      ) : selectedSessionId ? (
        <ChatView />
      ) : (
        <>
          <header className="pt-safe shrink-0 border-b border-slate-800/80 bg-slate-950/95 px-4 py-3">
            <div className="mx-auto flex max-w-2xl items-center justify-between">
              <div className="text-base font-bold tracking-tight text-slate-100">
                CodexFlow Mobile
              </div>
              <div className="text-xs text-slate-400">
                {pairingState.desktopName || '桌面工作站'}
              </div>
            </div>
          </header>

          <main className="flex min-h-0 flex-1 flex-col">
            {activeTab === 'sessions' ? (
              <SessionsView />
            ) : activeTab === 'approvals' ? (
              <ApprovalsView />
            ) : (
              <SettingsView />
            )}
          </main>

          <BottomTabBar
            activeTab={activeTab}
            pendingCount={pendingTodos.filter((t) => !t.isTerminal).length}
            onSelectTab={setActiveTab}
          />
        </>
      )}
    </div>
  );
}
