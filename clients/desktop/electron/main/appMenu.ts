import type { ContextMenuParams, MenuItemConstructorOptions } from 'electron';
import { APP_METADATA, type DesktopCommandAction, type DesktopLanguage } from '../../src/shared/ipc';

export interface ApplicationMenuOptions {
  language: DesktopLanguage;
  platform?: NodeJS.Platform;
  isDev?: boolean;
  onDispatchCommand: (action: DesktopCommandAction) => void;
  onOpenExternal: (url: string) => void;
  onQuit: () => void;
}

export interface ContextMenuCallbacks {
  onOpenLink: (url: string) => void;
  onCopyText: (text: string) => void;
}

export interface WorkspaceFileContextMenuOptions {
  isDirectory: boolean;
  hasRelativePath: boolean;
  language: DesktopLanguage;
  onOpenWithDefaultApp: () => void;
  onOpenInFileManager: () => void;
  onCopyRelativePath: () => void;
  onCopyFullPath: () => void;
}

export type ContextMenuInput = Pick<ContextMenuParams, 'isEditable' | 'selectionText' | 'linkURL' | 'editFlags'>;

interface MenuLabels {
  appAbout: string;
  appSettings: string;
  appServices: string;
  appHide: string;
  appHideOthers: string;
  appShowAll: string;
  appQuit: string;
  fileMenu: string;
  newConversation: string;
  newWorkspace: string;
  openFolder: string;
  settings: string;
  quit: string;
  editMenu: string;
  undo: string;
  redo: string;
  cut: string;
  copy: string;
  paste: string;
  delete: string;
  selectAll: string;
  viewMenu: string;
  viewChat: string;
  viewSkills: string;
  panelFiles: string;
  panelChanges: string;
  panelJobs: string;
  commandPalette: string;
  toggleTheme: string;
  reload: string;
  forceReload: string;
  resetZoom: string;
  zoomIn: string;
  zoomOut: string;
  toggleFullscreen: string;
  windowMenu: string;
  minimize: string;
  closeWindow: string;
  bringAllToFront: string;
  helpMenu: string;
  projectHome: string;
  releases: string;
  checkUpdates: string;
  toggleDevTools: string;
  about: string;
  openLink: string;
  copyLink: string;
  openWithDefaultApp: string;
  openInFileManager: string;
  copyRelativePath: string;
  copyFullPath: string;
}

const MENU_LABELS: Record<DesktopLanguage, MenuLabels> = {
  'zh-CN': {
    appAbout: `关于 ${APP_METADATA.name}`,
    appSettings: '设置…',
    appServices: '服务',
    appHide: `隐藏 ${APP_METADATA.name}`,
    appHideOthers: '隐藏其他',
    appShowAll: '全部显示',
    appQuit: `退出 ${APP_METADATA.name}`,
    fileMenu: '文件(&F)',
    newConversation: '新建对话',
    newWorkspace: '新建日常对话',
    openFolder: '打开本地目录…',
    settings: '设置…',
    quit: '退出',
    editMenu: '编辑(&E)',
    undo: '撤销',
    redo: '重做',
    cut: '剪切',
    copy: '复制',
    paste: '粘贴',
    delete: '删除',
    selectAll: '全选',
    viewMenu: '视图(&V)',
    viewChat: '工作区对话',
    viewSkills: '插件与 Skill',
    panelFiles: '工作区文件面板',
    panelChanges: '代码变更面板',
    panelJobs: '后台作业面板',
    commandPalette: '命令面板…',
    toggleTheme: '切换深浅主题',
    reload: '重新加载',
    forceReload: '强制重新加载',
    resetZoom: '实际大小',
    zoomIn: '放大',
    zoomOut: '缩小',
    toggleFullscreen: '切换全屏',
    windowMenu: '窗口(&W)',
    minimize: '最小化',
    closeWindow: '关闭窗口',
    bringAllToFront: '前置全部窗口',
    helpMenu: '帮助(&H)',
    projectHome: '项目主页 (GitHub)',
    releases: '发行版与更新日志',
    checkUpdates: '检查更新…',
    toggleDevTools: '开发者工具',
    about: `关于 ${APP_METADATA.name}`,
    openLink: '在浏览器中打开链接',
    copyLink: '复制链接地址',
    openWithDefaultApp: '使用默认应用打开',
    openInFileManager: '文件管理器打开',
    copyRelativePath: '复制相对路径',
    copyFullPath: '复制完整路径',
  },
  'en-US': {
    appAbout: `About ${APP_METADATA.name}`,
    appSettings: 'Settings…',
    appServices: 'Services',
    appHide: `Hide ${APP_METADATA.name}`,
    appHideOthers: 'Hide Others',
    appShowAll: 'Show All',
    appQuit: `Quit ${APP_METADATA.name}`,
    fileMenu: '&File',
    newConversation: 'New Conversation',
    newWorkspace: 'New Daily Chat',
    openFolder: 'Open Local Folder…',
    settings: 'Settings…',
    quit: 'Exit',
    editMenu: '&Edit',
    undo: 'Undo',
    redo: 'Redo',
    cut: 'Cut',
    copy: 'Copy',
    paste: 'Paste',
    delete: 'Delete',
    selectAll: 'Select All',
    viewMenu: '&View',
    viewChat: 'Workspace Chat',
    viewSkills: 'Plugins & Skills',
    panelFiles: 'Workspace Files Panel',
    panelChanges: 'Code Changes Panel',
    panelJobs: 'Background Jobs Panel',
    commandPalette: 'Command Palette…',
    toggleTheme: 'Toggle Light / Dark Theme',
    reload: 'Reload',
    forceReload: 'Force Reload',
    resetZoom: 'Actual Size',
    zoomIn: 'Zoom In',
    zoomOut: 'Zoom Out',
    toggleFullscreen: 'Toggle Full Screen',
    windowMenu: '&Window',
    minimize: 'Minimize',
    closeWindow: 'Close Window',
    bringAllToFront: 'Bring All to Front',
    helpMenu: '&Help',
    projectHome: 'Project Homepage (GitHub)',
    releases: 'Releases & Changelog',
    checkUpdates: 'Check for Updates…',
    toggleDevTools: 'Toggle Developer Tools',
    about: `About ${APP_METADATA.name}`,
    openLink: 'Open Link in Browser',
    copyLink: 'Copy Link Address',
    openWithDefaultApp: 'Open with Default App',
    openInFileManager: 'Open in File Manager',
    copyRelativePath: 'Copy Relative Path',
    copyFullPath: 'Copy Full Path',
  },
};

function getSafeHttpUrl(rawUrl: string | undefined): string | null {
  if (!rawUrl || !rawUrl.trim()) {
    return null;
  }
  try {
    const parsed = new URL(rawUrl);
    if (parsed.protocol === 'http:' || parsed.protocol === 'https:') {
      return parsed.toString();
    }
    return null;
  } catch {
    return null;
  }
}

/**
 * Builds the CodexFlow Desktop application menu template tailored to the app's
 * workspace, chat, inspector, settings, and help/about workflows.
 */
export function buildApplicationMenuTemplate(options: ApplicationMenuOptions): MenuItemConstructorOptions[] {
  const labels = MENU_LABELS[options.language] ?? MENU_LABELS['zh-CN'];
  const platform = options.platform ?? process.platform;
  const isMac = platform === 'darwin';

  const cmd = (action: DesktopCommandAction): (() => void) => () => options.onDispatchCommand(action);

  const template: MenuItemConstructorOptions[] = [];

  if (isMac) {
    template.push({
      label: APP_METADATA.name,
      submenu: [
        { label: labels.appAbout, click: cmd('open-about') },
        { label: labels.checkUpdates, click: cmd('open-updates') },
        { type: 'separator' },
        { label: labels.appSettings, accelerator: 'CmdOrCtrl+,', click: cmd('open-settings') },
        { type: 'separator' },
        { label: labels.appServices, role: 'services' },
        { type: 'separator' },
        { label: labels.appHide, role: 'hide' },
        { label: labels.appHideOthers, role: 'hideOthers' },
        { label: labels.appShowAll, role: 'unhide' },
        { type: 'separator' },
        { label: labels.appQuit, accelerator: 'CmdOrCtrl+Q', click: () => options.onQuit() },
      ],
    });
  }

  template.push({
    label: labels.fileMenu,
    submenu: [
      { label: labels.newConversation, accelerator: 'CmdOrCtrl+N', click: cmd('new-conversation') },
      { label: labels.newWorkspace, click: cmd('new-workspace') },
      { label: labels.openFolder, accelerator: 'CmdOrCtrl+O', click: cmd('open-folder') },
      ...(isMac
        ? []
        : [
            { type: 'separator' as const },
            { label: labels.settings, accelerator: 'CmdOrCtrl+,', click: cmd('open-settings') },
            { type: 'separator' as const },
            { label: labels.quit, accelerator: 'Alt+F4', click: () => options.onQuit() },
          ]),
    ],
  });

  template.push({
    label: labels.editMenu,
    submenu: [
      { label: labels.undo, role: 'undo' },
      { label: labels.redo, role: 'redo' },
      { type: 'separator' },
      { label: labels.cut, role: 'cut' },
      { label: labels.copy, role: 'copy' },
      { label: labels.paste, role: 'paste' },
      { label: labels.delete, role: 'delete' },
      { type: 'separator' },
      { label: labels.selectAll, role: 'selectAll' },
    ],
  });

  template.push({
    label: labels.viewMenu,
    submenu: [
      { label: labels.viewChat, click: cmd('switch-view-chat') },
      { label: labels.viewSkills, click: cmd('switch-view-skills') },
      { type: 'separator' },
      { label: labels.panelFiles, accelerator: 'CmdOrCtrl+Shift+F', click: cmd('toggle-inspector-files') },
      { label: labels.panelChanges, accelerator: 'CmdOrCtrl+Shift+G', click: cmd('toggle-inspector-changes') },
      { label: labels.panelJobs, accelerator: 'CmdOrCtrl+Shift+J', click: cmd('toggle-inspector-jobs') },
      { type: 'separator' },
      { label: labels.commandPalette, accelerator: 'CmdOrCtrl+K', click: cmd('open-command-palette') },
      { label: labels.toggleTheme, click: cmd('toggle-theme') },
      { type: 'separator' },
      ...(options.isDev
        ? [
            { label: labels.reload, role: 'reload' as const },
            { label: labels.forceReload, role: 'forceReload' as const },
            { type: 'separator' as const },
          ]
        : []),
      { label: labels.resetZoom, role: 'resetZoom' },
      { label: labels.zoomIn, role: 'zoomIn' },
      { label: labels.zoomOut, role: 'zoomOut' },
      { type: 'separator' },
      { label: labels.toggleFullscreen, role: 'togglefullscreen' },
    ],
  });

  template.push({
    label: labels.windowMenu,
    submenu: [
      { label: labels.minimize, role: 'minimize' },
      { label: labels.closeWindow, role: 'close' },
      ...(isMac
        ? [{ type: 'separator' as const }, { label: labels.bringAllToFront, role: 'front' as const }]
        : []),
    ],
  });

  template.push({
    label: labels.helpMenu,
    role: 'help',
    submenu: [
      {
        label: labels.projectHome,
        click: () => options.onOpenExternal(APP_METADATA.projectUrl),
      },
      {
        label: labels.releases,
        click: () => options.onOpenExternal(APP_METADATA.releasesUrl),
      },
      {
        label: labels.checkUpdates,
        click: cmd('open-updates'),
      },
      {
        label: labels.toggleDevTools,
        role: 'toggleDevTools',
      },
      { type: 'separator' },
      {
        id: 'help-about',
        label: labels.about,
        click: cmd('open-about'),
      },
    ],
  });

  return template;
}

/**
 * Builds the right-click context menu for chat messages, markdown links, and text inputs.
 */
export function buildContextMenuTemplate(
  params: ContextMenuInput,
  language: DesktopLanguage,
  callbacks: ContextMenuCallbacks,
): MenuItemConstructorOptions[] {
  const labels = MENU_LABELS[language] ?? MENU_LABELS['zh-CN'];
  const safeLinkUrl = getSafeHttpUrl(params.linkURL);

  if (params.isEditable) {
    const items: MenuItemConstructorOptions[] = [
      { label: labels.undo, role: 'undo', enabled: params.editFlags.canUndo },
      { label: labels.redo, role: 'redo', enabled: params.editFlags.canRedo },
      { type: 'separator' },
      { label: labels.cut, role: 'cut', enabled: params.editFlags.canCut },
      { label: labels.copy, role: 'copy', enabled: params.editFlags.canCopy },
      { label: labels.paste, role: 'paste', enabled: params.editFlags.canPaste },
      { label: labels.delete, role: 'delete', enabled: params.editFlags.canDelete },
      { type: 'separator' },
      { label: labels.selectAll, role: 'selectAll', enabled: params.editFlags.canSelectAll },
    ];
    if (safeLinkUrl) {
      items.push(
        { type: 'separator' },
        { label: labels.openLink, click: () => callbacks.onOpenLink(safeLinkUrl) },
        { label: labels.copyLink, click: () => callbacks.onCopyText(safeLinkUrl) },
      );
    }
    return items;
  }

  const hasSelection = Boolean(params.selectionText && params.selectionText.length > 0);
  const items: MenuItemConstructorOptions[] = [
    { label: labels.copy, role: 'copy', enabled: hasSelection || params.editFlags.canCopy },
    { label: labels.selectAll, role: 'selectAll', enabled: params.editFlags.canSelectAll },
  ];

  if (safeLinkUrl) {
    items.push(
      { type: 'separator' },
      { label: labels.openLink, click: () => callbacks.onOpenLink(safeLinkUrl) },
      { label: labels.copyLink, click: () => callbacks.onCopyText(safeLinkUrl) },
    );
  }

  return items;
}

/**
 * Builds the native right-click context menu for workspace files, folders, and empty directory area.
 */
export function buildWorkspaceFileContextMenuTemplate(
  options: WorkspaceFileContextMenuOptions,
): MenuItemConstructorOptions[] {
  const labels = MENU_LABELS[options.language] ?? MENU_LABELS['zh-CN'];
  const items: MenuItemConstructorOptions[] = [];

  if (!options.isDirectory) {
    items.push({
      id: 'workspace-file-open-default',
      label: labels.openWithDefaultApp,
      click: () => options.onOpenWithDefaultApp(),
    });
  }

  items.push({
    id: 'workspace-file-open-manager',
    label: labels.openInFileManager,
    click: () => options.onOpenInFileManager(),
  });

  items.push({ type: 'separator' });

  if (options.hasRelativePath) {
    items.push({
      id: 'workspace-file-copy-relative',
      label: labels.copyRelativePath,
      click: () => options.onCopyRelativePath(),
    });
  }

  items.push({
    id: 'workspace-file-copy-full',
    label: labels.copyFullPath,
    click: () => options.onCopyFullPath(),
  });

  return items;
}
