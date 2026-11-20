import { describe, expect, it, vi } from 'vitest';
import type { ContextMenuInput } from '../../electron/main/appMenu';
import {
  buildApplicationMenuTemplate,
  buildContextMenuTemplate,
  buildWorkspaceFileContextMenuTemplate,
} from '../../electron/main/appMenu';
import { APP_METADATA } from '../../src/shared/ipc';

function createBaseEditFlags(
  overrides: Partial<ContextMenuInput['editFlags']> = {},
): ContextMenuInput['editFlags'] {
  return {
    canUndo: false,
    canRedo: false,
    canCut: false,
    canCopy: false,
    canPaste: false,
    canDelete: false,
    canSelectAll: true,
    canEditRichly: false,
    ...overrides,
  };
}

describe('buildApplicationMenuTemplate', () => {
  it('builds localized zh-CN menus on win32 without Electron default roles and wires Help -> About', () => {
    const onDispatchCommand = vi.fn();
    const onOpenExternal = vi.fn();
    const onQuit = vi.fn();

    const template = buildApplicationMenuTemplate({
      language: 'zh-CN',
      platform: 'win32',
      isDev: false,
      onDispatchCommand,
      onOpenExternal,
      onQuit,
    });

    expect(template.map((item) => item.label)).toEqual([
      '文件(&F)',
      '编辑(&E)',
      '视图(&V)',
      '窗口(&W)',
      '帮助(&H)',
    ]);

    const helpMenu = template.find((item) => item.label === '帮助(&H)');
    expect(helpMenu).toBeDefined();
    const helpSubmenu = Array.isArray(helpMenu?.submenu) ? helpMenu.submenu : [];
    const aboutItem = helpSubmenu.find((item) => item.id === 'help-about');
    expect(aboutItem).toBeDefined();

    aboutItem?.click?.({} as never, undefined, {} as never);
    expect(onDispatchCommand).toHaveBeenCalledWith('open-about');

    const clickableHelpItems = helpSubmenu.filter(
      (item) => typeof item.click === 'function' && item.id !== 'help-about' && item.role !== 'toggleDevTools',
    );
    expect(clickableHelpItems.length).toBeGreaterThanOrEqual(3);
    for (const item of clickableHelpItems) {
      item.click?.({} as never, undefined, {} as never);
    }

    expect(onOpenExternal).toHaveBeenCalledWith(APP_METADATA.projectUrl);
    expect(onOpenExternal).toHaveBeenCalledWith(APP_METADATA.releasesUrl);
    expect(onDispatchCommand).toHaveBeenCalledWith('open-updates');
  });

  it('builds en-US menus with macOS app menu when platform is darwin', () => {
    const onDispatchCommand = vi.fn();
    const template = buildApplicationMenuTemplate({
      language: 'en-US',
      platform: 'darwin',
      isDev: true,
      onDispatchCommand,
      onOpenExternal: vi.fn(),
      onQuit: vi.fn(),
    });

    expect(template.map((item) => item.label)).toEqual([
      APP_METADATA.name,
      '&File',
      '&Edit',
      '&View',
      '&Window',
      '&Help',
    ]);

    const macAppSubmenu = Array.isArray(template[0]?.submenu) ? template[0].submenu : [];
    const macAboutItem = macAppSubmenu.find((item) => typeof item.label === 'string' && item.label.includes('About'));
    expect(macAboutItem).toBeDefined();
    macAboutItem?.click?.({} as never, undefined, {} as never);
    expect(onDispatchCommand).toHaveBeenCalledWith('open-about');

    const helpMenu = template.find((item) => item.label === '&Help');
    const helpSubmenu = Array.isArray(helpMenu?.submenu) ? helpMenu.submenu : [];
    const helpAboutItem = helpSubmenu.find((item) => item.id === 'help-about');
    expect(helpAboutItem).toBeDefined();
  });
});

describe('buildContextMenuTemplate', () => {
  it('builds editable input context menu with undo/redo/cut/copy/paste/delete/selectAll matching editFlags', () => {
    const template = buildContextMenuTemplate(
      {
        isEditable: true,
        selectionText: 'hello',
        linkURL: '',
        editFlags: createBaseEditFlags({
          canUndo: true,
          canRedo: false,
          canCut: true,
          canCopy: true,
          canPaste: true,
          canDelete: true,
          canSelectAll: true,
        }),
      },
      'zh-CN',
      {
        onOpenLink: vi.fn(),
        onCopyText: vi.fn(),
      },
    );

    const roles = template.filter((item) => item.type !== 'separator').map((item) => item.role);
    expect(roles).toEqual(['undo', 'redo', 'cut', 'copy', 'paste', 'delete', 'selectAll']);

    const redoItem = template.find((item) => item.role === 'redo');
    const pasteItem = template.find((item) => item.role === 'paste');
    expect(redoItem?.enabled).toBe(false);
    expect(pasteItem?.enabled).toBe(true);
  });

  it('builds read-only chat context menu enabling copy when selectionText is non-empty and rejects unsafe URLs', () => {
    const onOpenLink = vi.fn();
    const onCopyText = vi.fn();

    const withSelectionAndHttpLink = buildContextMenuTemplate(
      {
        isEditable: false,
        selectionText: 'selected answer',
        linkURL: 'https://github.com/iwaitu/codexflow',
        editFlags: createBaseEditFlags({ canCopy: false, canSelectAll: true }),
      },
      'en-US',
      { onOpenLink, onCopyText },
    );

    const copyItem = withSelectionAndHttpLink.find((item) => item.role === 'copy');
    const selectAllItem = withSelectionAndHttpLink.find((item) => item.role === 'selectAll');
    expect(copyItem?.enabled).toBe(true);
    expect(selectAllItem?.enabled).toBe(true);

    const linkActionItems = withSelectionAndHttpLink.filter(
      (item) => item.type !== 'separator' && item.role !== 'copy' && item.role !== 'selectAll',
    );
    expect(linkActionItems).toHaveLength(2);
    for (const item of linkActionItems) {
      item.click?.({} as never, undefined, {} as never);
    }
    expect(onOpenLink).toHaveBeenCalledWith('https://github.com/iwaitu/codexflow');
    expect(onCopyText).toHaveBeenCalledWith('https://github.com/iwaitu/codexflow');

    const withUnsafeLink = buildContextMenuTemplate(
      {
        isEditable: false,
        selectionText: '',
        linkURL: 'javascript:alert(1)',
        editFlags: createBaseEditFlags({ canCopy: false, canSelectAll: true }),
      },
      'en-US',
      { onOpenLink: vi.fn(), onCopyText: vi.fn() },
    );
    expect(withUnsafeLink.find((item) => item.role === 'copy')?.enabled).toBe(false);
    expect(
      withUnsafeLink.filter((item) => item.type !== 'separator' && item.role !== 'copy' && item.role !== 'selectAll'),
    ).toHaveLength(0);
  });
});

describe('buildWorkspaceFileContextMenuTemplate', () => {
  it('includes default-app open and file manager open actions for files and wires callbacks', () => {
    const onOpenWithDefaultApp = vi.fn();
    const onOpenInFileManager = vi.fn();
    const onCopyRelativePath = vi.fn();
    const onCopyFullPath = vi.fn();

    const template = buildWorkspaceFileContextMenuTemplate({
      isDirectory: false,
      hasRelativePath: true,
      language: 'zh-CN',
      onOpenWithDefaultApp,
      onOpenInFileManager,
      onCopyRelativePath,
      onCopyFullPath,
    });

    const labels = template.filter((item) => item.type !== 'separator').map((item) => item.label);
    expect(labels).toEqual(['使用默认应用打开', '文件管理器打开', '复制相对路径', '复制完整路径']);

    for (const item of template) {
      item.click?.({} as never, undefined, {} as never);
    }
    expect(onOpenWithDefaultApp).toHaveBeenCalledTimes(1);
    expect(onOpenInFileManager).toHaveBeenCalledTimes(1);
    expect(onCopyRelativePath).toHaveBeenCalledTimes(1);
    expect(onCopyFullPath).toHaveBeenCalledTimes(1);
  });

  it('omits default-app open for directories and omits relative path copy for workspace root', () => {
    const dirTemplate = buildWorkspaceFileContextMenuTemplate({
      isDirectory: true,
      hasRelativePath: true,
      language: 'zh-CN',
      onOpenWithDefaultApp: vi.fn(),
      onOpenInFileManager: vi.fn(),
      onCopyRelativePath: vi.fn(),
      onCopyFullPath: vi.fn(),
    });
    expect(dirTemplate.filter((item) => item.type !== 'separator').map((item) => item.label)).toEqual([
      '文件管理器打开',
      '复制相对路径',
      '复制完整路径',
    ]);

    const rootTemplate = buildWorkspaceFileContextMenuTemplate({
      isDirectory: true,
      hasRelativePath: false,
      language: 'en-US',
      onOpenWithDefaultApp: vi.fn(),
      onOpenInFileManager: vi.fn(),
      onCopyRelativePath: vi.fn(),
      onCopyFullPath: vi.fn(),
    });
    expect(rootTemplate.filter((item) => item.type !== 'separator').map((item) => item.label)).toEqual([
      'Open in File Manager',
      'Copy Full Path',
    ]);
  });
});
