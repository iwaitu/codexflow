import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { posix, win32 } from 'node:path';

function joinForPlatform(platform: NodeJS.Platform, ...parts: string[]): string {
  return platform === 'win32' ? win32.join(...parts) : posix.join(...parts);
}

const PATH_START_MARKER = '__CODEXFLOW_PATH_START__';
const PATH_END_MARKER = '__CODEXFLOW_PATH_END__';

export interface ShellEnvOptions {
  platform?: NodeJS.Platform;
  homeDir?: string;
  pathExists?: (candidate: string) => boolean;
  probeLoginShellPath?: (shellBin: string) => string | null;
  forceRefresh?: boolean;
}

export interface EnhancedEnvResult {
  env: NodeJS.ProcessEnv;
  enhanced: boolean;
  addedPaths: string[];
}

let cachedLoginShellPath: string | null | undefined;

export function clearCachedShellEnv(): void {
  cachedLoginShellPath = undefined;
}

export function getStandardToolDirs(
  platform: NodeJS.Platform = process.platform,
  homeDir: string = homedir(),
): string[] {
  if (platform === 'darwin') {
    return [
      joinForPlatform(platform, homeDir, '.dotnet'),
      '/opt/homebrew/bin',
      '/opt/homebrew/sbin',
      '/usr/local/bin',
      '/usr/local/sbin',
      '/usr/local/share/dotnet',
      '/Applications/Docker.app/Contents/Resources/bin',
      joinForPlatform(platform, homeDir, '.local', 'bin'),
      joinForPlatform(platform, homeDir, '.nvm', 'current', 'bin'),
      joinForPlatform(platform, homeDir, '.volta', 'bin'),
      joinForPlatform(platform, homeDir, '.asdf', 'shims'),
      joinForPlatform(platform, homeDir, '.cargo', 'bin'),
      '/usr/bin',
      '/bin',
      '/usr/sbin',
      '/sbin',
    ];
  }

  if (platform === 'win32') {
    const programFiles = process.env.ProgramFiles || 'C:\\Program Files';
    const localAppData = process.env.LOCALAPPDATA || joinForPlatform(platform, homeDir, 'AppData', 'Local');
    return [
      joinForPlatform(platform, homeDir, '.dotnet'),
      joinForPlatform(platform, programFiles, 'dotnet'),
      joinForPlatform(platform, programFiles, 'Git', 'cmd'),
      joinForPlatform(platform, programFiles, 'nodejs'),
      joinForPlatform(platform, programFiles, 'Docker', 'Docker', 'resources', 'bin'),
      joinForPlatform(platform, localAppData, 'Microsoft', 'WindowsApps'),
    ];
  }

  return [
    joinForPlatform(platform, homeDir, '.dotnet'),
    '/usr/local/bin',
    '/usr/local/sbin',
    '/usr/share/dotnet',
    joinForPlatform(platform, homeDir, '.local', 'bin'),
    joinForPlatform(platform, homeDir, '.nvm', 'current', 'bin'),
    joinForPlatform(platform, homeDir, '.volta', 'bin'),
    joinForPlatform(platform, homeDir, '.cargo', 'bin'),
    '/usr/bin',
    '/bin',
    '/usr/sbin',
    '/sbin',
  ];
}

export function splitPathEntries(rawPath: string | undefined, platform: NodeJS.Platform = process.platform): string[] {
  if (!rawPath) {
    return [];
  }
  const delimiter = platform === 'win32' ? ';' : ':';
  return rawPath
    .split(delimiter)
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0 && !entry.includes('\0') && !entry.includes('\n') && !entry.includes('\r'));
}

export function mergeSearchPaths(
  existingPath: string | undefined,
  candidateGroups: string[][],
  platform: NodeJS.Platform = process.platform,
): { mergedPath: string; addedPaths: string[] } {
  const delimiter = platform === 'win32' ? ';' : ':';
  const normalizeKey = (value: string) => (platform === 'win32' ? value.toLowerCase() : value);

  const existingEntries = splitPathEntries(existingPath, platform);
  const existingSet = new Set(existingEntries.map(normalizeKey));
  const seen = new Set<string>();
  const finalList: string[] = [];
  const addedPaths: string[] = [];

  for (const group of candidateGroups) {
    for (const rawEntry of group) {
      const entry = rawEntry.trim();
      if (!entry) {
        continue;
      }
      const key = normalizeKey(entry);
      if (seen.has(key)) {
        continue;
      }
      seen.add(key);
      finalList.push(entry);
      if (!existingSet.has(key)) {
        addedPaths.push(entry);
      }
    }
  }

  return {
    mergedPath: finalList.join(delimiter),
    addedPaths,
  };
}

function defaultProbeLoginShellPath(shellBin: string): string | null {
  try {
    const res = spawnSync(
      shellBin,
      ['-lc', `printf '${PATH_START_MARKER}%s${PATH_END_MARKER}' "$PATH"`],
      {
        encoding: 'utf-8',
        timeout: 2_000,
        windowsHide: true,
        stdio: ['ignore', 'pipe', 'ignore'],
      },
    );
    if (res.status !== 0 || !res.stdout) {
      return null;
    }
    const startIdx = res.stdout.indexOf(PATH_START_MARKER);
    const endIdx = res.stdout.indexOf(PATH_END_MARKER, startIdx + PATH_START_MARKER.length);
    if (startIdx === -1 || endIdx === -1) {
      return null;
    }
    const extracted = res.stdout.slice(startIdx + PATH_START_MARKER.length, endIdx).trim();
    return extracted || null;
  } catch {
    return null;
  }
}

function getPathEnvKey(env: NodeJS.ProcessEnv, platform: NodeJS.Platform): string {
  if (platform !== 'win32') {
    return 'PATH';
  }
  const found = Object.keys(env).find((key) => key.toLowerCase() === 'path');
  return found ?? 'PATH';
}

export function resolveEnhancedProcessEnv(
  baseEnv: NodeJS.ProcessEnv = process.env,
  options: ShellEnvOptions = {},
): EnhancedEnvResult {
  const platform = options.platform ?? process.platform;
  const homeDir = options.homeDir ?? homedir();
  const pathExists = options.pathExists ?? existsSync;
  const probeLoginShellPath = options.probeLoginShellPath ?? defaultProbeLoginShellPath;

  const pathKey = getPathEnvKey(baseEnv, platform);
  const currentPath = baseEnv[pathKey];

  let loginShellPath: string | null = null;
  if (platform !== 'win32') {
    if (options.forceRefresh || options.probeLoginShellPath !== undefined || cachedLoginShellPath === undefined) {
      const shellBin = baseEnv.SHELL && pathExists(baseEnv.SHELL) ? baseEnv.SHELL : platform === 'darwin' ? '/bin/zsh' : '/bin/sh';
      const probed = probeLoginShellPath(shellBin);
      if (options.probeLoginShellPath === undefined) {
        cachedLoginShellPath = probed;
      }
      loginShellPath = probed;
    } else {
      loginShellPath = cachedLoginShellPath;
    }
  }

  const standardDirs = getStandardToolDirs(platform, homeDir).filter((dir) => pathExists(dir));
  const currentEntries = splitPathEntries(currentPath, platform);
  const loginEntries = splitPathEntries(loginShellPath ?? undefined, platform);

  // Preserve current PATH priority (so explicit DOTNET/venv/dev overrides still win),
  // then augment with user's login shell PATH and verified standard tool directories.
  const { mergedPath, addedPaths } = mergeSearchPaths(
    currentPath,
    [currentEntries, loginEntries, standardDirs],
    platform,
  );

  const nextEnv: NodeJS.ProcessEnv = {
    ...baseEnv,
    [pathKey]: mergedPath,
  };

  // Auto-detect DOTNET_ROOT when user installed .NET via dotnet-install.sh into ~/.dotnet
  // or standard macOS/Linux locations and DOTNET_ROOT is not explicitly configured.
  if (!nextEnv.DOTNET_ROOT) {
    const dotnetBin = platform === 'win32' ? 'dotnet.exe' : 'dotnet';
    const userDotnetRoot = joinForPlatform(platform, homeDir, '.dotnet');
    const systemDotnetRoot = platform === 'darwin' ? '/usr/local/share/dotnet' : '/usr/share/dotnet';
    if (pathExists(joinForPlatform(platform, userDotnetRoot, dotnetBin))) {
      nextEnv.DOTNET_ROOT = userDotnetRoot;
    } else if (platform !== 'win32' && pathExists(joinForPlatform(platform, systemDotnetRoot, dotnetBin))) {
      nextEnv.DOTNET_ROOT = systemDotnetRoot;
    }
  }

  return {
    env: nextEnv,
    enhanced: addedPaths.length > 0 || Boolean(!baseEnv.DOTNET_ROOT && nextEnv.DOTNET_ROOT),
    addedPaths,
  };
}
