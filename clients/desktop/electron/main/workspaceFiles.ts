import { lstatSync, realpathSync } from 'node:fs';
import { isAbsolute, posix, resolve } from 'node:path';
import type { DesktopLanguage, WorkspaceFileActionResult } from '../../src/shared/ipc';
import { isPathStrictlyInsideRoot } from './securityUtils';

export interface ResolvedWorkspaceTarget {
  realWorkspaceRoot: string;
  realPath: string;
  relativePath: string;
  isDirectory: boolean;
  isFile: boolean;
}

export type ResolveWorkspaceTargetResult =
  | { ok: true; target: ResolvedWorkspaceTarget }
  | { ok: false; error: string };

export interface WorkspaceShellApi {
  openPath: (fullPath: string) => Promise<string>;
  showItemInFolder: (fullPath: string) => void;
}

function localizedError(language: DesktopLanguage, zh: string, en: string): string {
  return language === 'en-US' ? en : zh;
}

/**
 * Resolves a workspace-relative path against `workspacePath`, blocking drive-letter prefixes,
 * UNC paths, backslashes, null bytes, dot-dot traversal segments, and symlink escapes outside
 * the canonical workspace root.
 */
export function resolveSafeWorkspaceTarget(
  workspacePath: string,
  relativePath: string,
  language: DesktopLanguage = 'zh-CN',
): ResolveWorkspaceTargetResult {
  if (typeof workspacePath !== 'string' || !workspacePath.trim()) {
    return {
      ok: false,
      error: localizedError(language, '当前工作区本地路径不可用。', 'Workspace local path is unavailable.'),
    };
  }

  let realWorkspaceRoot: string;
  try {
    realWorkspaceRoot = realpathSync(resolve(workspacePath));
    if (!lstatSync(realWorkspaceRoot).isDirectory()) {
      return {
        ok: false,
        error: localizedError(language, '当前工作区路径不是有效目录。', 'Workspace path is not a valid directory.'),
      };
    }
  } catch {
    return {
      ok: false,
      error: localizedError(
        language,
        '本地工作区路径不存在或无法访问（可能为远程工作区或目录已被移动）。',
        'Local workspace path does not exist or is inaccessible.',
      ),
    };
  }

  if (typeof relativePath !== 'string') {
    return {
      ok: false,
      error: localizedError(language, '无效的文件路径。', 'Invalid file path.'),
    };
  }

  if (relativePath === '') {
    return {
      ok: true,
      target: {
        realWorkspaceRoot,
        realPath: realWorkspaceRoot,
        relativePath: '',
        isDirectory: true,
        isFile: false,
      },
    };
  }

  if (
    relativePath.includes('\0') ||
    relativePath.includes('\\') ||
    relativePath.startsWith('/') ||
    relativePath.startsWith('//') ||
    /^[a-zA-Z]:/.test(relativePath) ||
    isAbsolute(relativePath) ||
    relativePath.split('/').some((seg) => seg === '..')
  ) {
    return {
      ok: false,
      error: localizedError(language, '目标路径不合法，已拒绝访问。', 'Invalid target path rejected.'),
    };
  }

  const normalizedPosix = posix.normalize(relativePath);
  if (
    normalizedPosix === '..' ||
    normalizedPosix.startsWith('../') ||
    normalizedPosix.includes('/../') ||
    normalizedPosix.endsWith('/..') ||
    normalizedPosix.startsWith('/') ||
    /^[a-zA-Z]:/.test(normalizedPosix) ||
    isAbsolute(normalizedPosix)
  ) {
    return {
      ok: false,
      error: localizedError(language, '目标路径超出工作区范围，已拒绝访问。', 'Target path escapes workspace root.'),
    };
  }

  const cleanRelative = normalizedPosix === '.' ? '' : normalizedPosix.replace(/\/+$/, '');
  const candidatePath = cleanRelative ? resolve(realWorkspaceRoot, cleanRelative) : realWorkspaceRoot;
  if (!isPathStrictlyInsideRoot(realWorkspaceRoot, candidatePath)) {
    return {
      ok: false,
      error: localizedError(language, '目标路径超出工作区范围，已拒绝访问。', 'Target path escapes workspace root.'),
    };
  }

  let realPath: string;
  try {
    realPath = realpathSync(candidatePath);
  } catch {
    return {
      ok: false,
      error: localizedError(
        language,
        '目标文件或目录不存在，可能已被移动或删除。',
        'Target file or folder no longer exists.',
      ),
    };
  }

  if (!isPathStrictlyInsideRoot(realWorkspaceRoot, realPath)) {
    return {
      ok: false,
      error: localizedError(
        language,
        '目标符号链接指向工作区外部，已拒绝打开。',
        'Target symlink points outside the workspace root.',
      ),
    };
  }

  let stat;
  try {
    stat = lstatSync(realPath);
  } catch {
    return {
      ok: false,
      error: localizedError(language, '无法读取目标文件状态。', 'Unable to inspect target path.'),
    };
  }

  return {
    ok: true,
    target: {
      realWorkspaceRoot,
      realPath,
      relativePath: cleanRelative,
      isDirectory: stat.isDirectory(),
      isFile: stat.isFile(),
    },
  };
}

/**
 * Opens a folder directly in the OS file manager, or reveals a file inside its parent folder.
 */
export async function openWorkspaceTargetInFileManager(
  target: ResolvedWorkspaceTarget,
  shellApi: WorkspaceShellApi,
  language: DesktopLanguage = 'zh-CN',
): Promise<WorkspaceFileActionResult> {
  try {
    if (target.isDirectory) {
      const errorMessage = await shellApi.openPath(target.realPath);
      if (errorMessage) {
        return {
          ok: false,
          error: localizedError(
            language,
            `无法在文件管理器中打开目录：${errorMessage}`,
            `Failed to open folder in file manager: ${errorMessage}`,
          ),
        };
      }
      return { ok: true };
    }

    shellApi.showItemInFolder(target.realPath);
    return { ok: true };
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    return {
      ok: false,
      error: localizedError(
        language,
        `无法在文件管理器中打开：${detail}`,
        `Failed to open in file manager: ${detail}`,
      ),
    };
  }
}

/**
 * Opens a regular workspace file with the operating system's default application.
 */
export async function openWorkspaceFileWithDefaultApp(
  target: ResolvedWorkspaceTarget,
  shellApi: WorkspaceShellApi,
  language: DesktopLanguage = 'zh-CN',
): Promise<WorkspaceFileActionResult> {
  if (!target.isFile) {
    return {
      ok: false,
      error: localizedError(
        language,
        '仅支持使用默认应用打开文件。',
        'Only regular files can be opened with the default application.',
      ),
    };
  }

  try {
    const errorMessage = await shellApi.openPath(target.realPath);
    if (errorMessage) {
      return {
        ok: false,
        error: localizedError(
          language,
          `无法使用默认应用打开文件：${errorMessage}`,
          `Failed to open file with default app: ${errorMessage}`,
        ),
      };
    }
    return { ok: true };
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    return {
      ok: false,
      error: localizedError(
        language,
        `无法使用默认应用打开文件：${detail}`,
        `Failed to open file with default app: ${detail}`,
      ),
    };
  }
}
