import { create } from 'zustand';
import type { DesktopLanguage, DesktopTheme } from '@shared/ipc';

const STORAGE_KEY = 'codexflow.desktop.preferences.v1';

export interface DesktopPreferencesSnapshot {
  theme: DesktopTheme;
  language: DesktopLanguage;
  notificationsEnabled: boolean;
  notifyOnlyWhenUnfocused: boolean;
  minimizeToTrayOnClose: boolean;
}

const DEFAULT_PREFERENCES: DesktopPreferencesSnapshot = {
  theme: 'dark',
  language: 'zh-CN',
  notificationsEnabled: true,
  notifyOnlyWhenUnfocused: true,
  minimizeToTrayOnClose: false,
};

function readPersistedPreferences(): DesktopPreferencesSnapshot {
  try {
    if (typeof window === 'undefined' || !window.localStorage) {
      return DEFAULT_PREFERENCES;
    }
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return DEFAULT_PREFERENCES;
    }
    const parsed = JSON.parse(raw) as Partial<DesktopPreferencesSnapshot>;
    return {
      theme: parsed.theme === 'light' ? 'light' : 'dark',
      language: parsed.language === 'en-US' ? 'en-US' : 'zh-CN',
      notificationsEnabled:
        typeof parsed.notificationsEnabled === 'boolean'
          ? parsed.notificationsEnabled
          : DEFAULT_PREFERENCES.notificationsEnabled,
      notifyOnlyWhenUnfocused:
        typeof parsed.notifyOnlyWhenUnfocused === 'boolean'
          ? parsed.notifyOnlyWhenUnfocused
          : DEFAULT_PREFERENCES.notifyOnlyWhenUnfocused,
      minimizeToTrayOnClose:
        typeof parsed.minimizeToTrayOnClose === 'boolean'
          ? parsed.minimizeToTrayOnClose
          : DEFAULT_PREFERENCES.minimizeToTrayOnClose,
    };
  } catch {
    return DEFAULT_PREFERENCES;
  }
}

function persistPreferences(snapshot: DesktopPreferencesSnapshot): void {
  try {
    if (typeof window !== 'undefined' && window.localStorage) {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(snapshot));
    }
  } catch {
    // ignore storage failures in restricted contexts
  }
}

export function applyPreferencesToHost(snapshot: DesktopPreferencesSnapshot): void {
  if (typeof document !== 'undefined') {
    const root = document.documentElement;
    root.lang = snapshot.language;
    root.dataset.theme = snapshot.theme;
    root.classList.toggle('dark', snapshot.theme === 'dark');
    root.classList.toggle('light', snapshot.theme === 'light');
  }
  if (typeof window !== 'undefined') {
    void window.codexflow?.setDesktopPreferences?.({
      theme: snapshot.theme,
      language: snapshot.language,
      minimizeToTrayOnClose: snapshot.minimizeToTrayOnClose,
    });
  }
}

export interface PreferencesState extends DesktopPreferencesSnapshot {
  setTheme: (theme: DesktopTheme) => void;
  toggleTheme: () => void;
  setLanguage: (language: DesktopLanguage) => void;
  toggleLanguage: () => void;
  setNotificationsEnabled: (enabled: boolean) => void;
  setNotifyOnlyWhenUnfocused: (onlyWhenUnfocused: boolean) => void;
  setMinimizeToTrayOnClose: (minimize: boolean) => void;
  syncHostPreferences: () => void;
  resetForTests: () => void;
}

const initial = readPersistedPreferences();

export const usePreferencesStore = create<PreferencesState>((set, get) => {
  const update = (patch: Partial<DesktopPreferencesSnapshot>) => {
    const current = get();
    const next: DesktopPreferencesSnapshot = {
      theme: patch.theme ?? current.theme,
      language: patch.language ?? current.language,
      notificationsEnabled: patch.notificationsEnabled ?? current.notificationsEnabled,
      notifyOnlyWhenUnfocused: patch.notifyOnlyWhenUnfocused ?? current.notifyOnlyWhenUnfocused,
      minimizeToTrayOnClose: patch.minimizeToTrayOnClose ?? current.minimizeToTrayOnClose,
    };
    set(next);
    persistPreferences(next);
    applyPreferencesToHost(next);
  };

  return {
    ...initial,
    setTheme: (theme) => update({ theme }),
    toggleTheme: () => update({ theme: get().theme === 'dark' ? 'light' : 'dark' }),
    setLanguage: (language) => update({ language }),
    toggleLanguage: () => update({ language: get().language === 'zh-CN' ? 'en-US' : 'zh-CN' }),
    setNotificationsEnabled: (notificationsEnabled) => update({ notificationsEnabled }),
    setNotifyOnlyWhenUnfocused: (notifyOnlyWhenUnfocused) => update({ notifyOnlyWhenUnfocused }),
    setMinimizeToTrayOnClose: (minimizeToTrayOnClose) => update({ minimizeToTrayOnClose }),
    syncHostPreferences: () => applyPreferencesToHost(get()),
    resetForTests: () => {
      set(DEFAULT_PREFERENCES);
      persistPreferences(DEFAULT_PREFERENCES);
      applyPreferencesToHost(DEFAULT_PREFERENCES);
    },
  };
});
