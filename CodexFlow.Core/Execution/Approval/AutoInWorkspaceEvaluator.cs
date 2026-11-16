// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexFlow.Core.Execution.Approval;

public sealed record AutoInWorkspaceEvaluation(
    bool AutoAllowed,
    string ResolvedExecutable,
    IReadOnlyList<string> RewrittenArgs,
    IReadOnlyDictionary<string, string?>? ForcedEnvironment,
    string Reason,
    bool RequiresGuestGit = false);

/// <summary>
/// Strict allow-list evaluator for <see cref="ExecutionPermissionMode.AutoInWorkspace"/>:
/// - Only <c>git</c> (<c>status</c>, <c>diff</c>, <c>log</c>, <c>show</c>) and <c>rg</c> may be auto-approved.
/// - Executable must resolve via a sanitized PATH into a verified trusted installation directory after resolving
///   every symlink / junction / reparse point along the entire path (including all parent directories).
/// - Arguments must match subcommand-specific read-only syntax allow-lists without ambiguous next-arg skipping.
/// - Runs under a minimal fixed environment so repository or user configs cannot spawn external helpers.
/// </summary>
public static class AutoInWorkspaceEvaluator
{
    private static readonly HashSet<string> AllowedGitSubcommands = new(StringComparer.Ordinal)
    {
        "status", "diff", "log", "show"
    };

    private static readonly string[] ForbiddenGitArgPrefixes =
    [
        "-c", "--config-env", "--exec-path", "--git-dir", "--work-tree",
        "--namespace", "--super-prefix", "--output", "-o", "--upload-pack",
        "--receive-pack", "--ext-diff", "--textconv", "--paginate", "-P"
    ];

    private static readonly HashSet<string> SafeGitStatusBooleanFlags = new(StringComparer.Ordinal)
    {
        "--short", "-s", "--branch", "-b", "--porcelain", "--long", "-v", "--verbose",
        "-u", "--untracked-files", "--ignored", "--renames", "--no-renames", "-z"
    };

    private static readonly HashSet<string> SafeGitStatusEqualsFlags = new(StringComparer.Ordinal)
    {
        "--porcelain", "--untracked-files", "--ignored", "--column"
    };

    private static readonly HashSet<string> SafeGitDiffLogShowBooleanFlags = new(StringComparer.Ordinal)
    {
        "--stat", "--numstat", "--shortstat", "--name-only", "--name-status", "--summary",
        "--patch", "-p", "-u", "--raw", "--cached", "--staged", "--no-color", "--color",
        "--oneline", "--graph", "--decorate", "--no-decorate", "--all", "--branches",
        "--tags", "--remotes", "--renames", "--no-renames", "-M", "-C", "-B",
        "-w", "--ignore-all-space", "--ignore-space-change", "--ignore-blank-lines",
        "--minimal", "--patience", "--histogram", "--check", "--abbrev-commit",
        "--no-abbrev-commit", "--full-index", "--binary", "-z", "--reverse",
        "--first-parent", "--no-merges", "--merges", "-q", "--quiet", "-v",
        "--no-ext-diff", "--no-textconv", "--no-patch", "-s"
    };

    /// <summary>
    /// Long flags for diff/log/show that are ONLY accepted in explicit <c>--flag=value</c> form so optional-value
    /// flags (such as <c>--pretty</c>, <c>--color</c>, <c>--stat</c>) can never swallow or skip checking the next argv token.
    /// </summary>
    private static readonly HashSet<string> SafeGitDiffLogShowEqualsFlags = new(StringComparer.Ordinal)
    {
        "--unified", "--max-count", "--skip", "--since", "--after", "--until", "--before",
        "--author", "--committer", "--grep", "--pretty", "--format", "--date", "--stat",
        "--color", "--diff-filter", "--abbrev", "--find-renames", "--find-copies",
        "--relative", "--src-prefix", "--dst-prefix", "--decorate"
    };

    private static readonly HashSet<string> SafeRgValueFlags = new(StringComparer.Ordinal)
    {
        "-e", "--regexp", "-g", "--glob", "-t", "--type", "-T", "--type-not",
        "-C", "--context", "-A", "--after-context", "-B", "--before-context",
        "-m", "--max-count", "--max-depth", "--max-filesize", "--color", "--iglob"
    };

    private static readonly HashSet<string> SafeRgBooleanFlags = new(StringComparer.Ordinal)
    {
        "-n", "--line-number", "-N", "--no-line-number", "-i", "--ignore-case",
        "-S", "--smart-case", "-s", "--case-sensitive", "-F", "--fixed-strings",
        "-w", "--word-regexp", "-x", "--line-regexp", "-l", "--files-with-matches",
        "--files-without-match", "--files", "-c", "--count", "-v", "--invert-match",
        "-H", "--with-filename", "-I", "--no-filename", "-o", "--only-matching",
        "-q", "--quiet", "--hidden", "--no-ignore", "--no-ignore-vcs", "--vimgrep", "--json", "--stats"
    };

    public static bool IsTrustedExecutableOrigin(
        string commandOrPath,
        string workspaceRoot,
        string stagingRoot,
        IReadOnlyList<string> configuredTrustedPaths,
        out string resolvedExecutable)
    {
        ArgumentNullException.ThrowIfNull(configuredTrustedPaths);
        resolvedExecutable = string.Empty;
        if (string.IsNullOrWhiteSpace(commandOrPath))
        {
            return false;
        }

        var trustedRoots = BuildTrustedRoots(workspaceRoot, stagingRoot, configuredTrustedPaths);
        if (trustedRoots.Count == 0)
        {
            return false;
        }

        var candidate = ResolveWithSanitizedPath(commandOrPath, workspaceRoot, stagingRoot, trustedRoots);
        if (candidate is null || !File.Exists(candidate))
        {
            return false;
        }

        var finalPath = ResolveFinalTargetPath(candidate);
        if (finalPath is null)
        {
            return false;
        }

        if (IsUnderOrEqual(finalPath, workspaceRoot) || IsUnderOrEqual(finalPath, stagingRoot))
        {
            return false;
        }

        if (!trustedRoots.Any(root => IsTrustedMatch(finalPath, root)))
        {
            return false;
        }

        resolvedExecutable = finalPath;
        return true;
    }

    public static AutoInWorkspaceEvaluation Evaluate(
        string toolName,
        string command,
        IReadOnlyList<string> args,
        string workspaceRoot,
        string stagingRoot,
        string stagingWorkspaceDir,
        string? guestGitDir,
        string sessionTempDir,
        IReadOnlyList<string> configuredTrustedPaths,
        ExecutionBackend backend = ExecutionBackend.Local)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuredTrustedPaths);

        if (!toolName.Equals("run_command", StringComparison.OrdinalIgnoreCase) &&
            !toolName.Equals("exec_cmd", StringComparison.OrdinalIgnoreCase))
        {
            return AskRequired(command, args, $"Tool '{toolName}' requires interactive approval in AutoInWorkspace mode.");
        }

        var stem = Path.GetFileNameWithoutExtension(command);
        if (!stem.Equals("git", StringComparison.OrdinalIgnoreCase) &&
            !stem.Equals("rg", StringComparison.OrdinalIgnoreCase))
        {
            return AskRequired(command, args, $"Command '{stem}' is not in the AutoInWorkspace read-only allow-list.");
        }

        string trustedExe;
        if (backend == ExecutionBackend.Docker)
        {
            var normalized = command.Replace('\\', '/');
            var allowedContainerCmd = normalized is "git" or "/usr/bin/git" or "/bin/git" or "rg" or "/usr/bin/rg" or "/bin/rg";
            if (!allowedContainerCmd)
            {
                return AskRequired(command, args, $"Container command '{command}' is not a system /usr/bin binary.");
            }

            trustedExe = "/usr/bin/" + stem.ToLowerInvariant();
        }
        else
        {
            if (!IsTrustedExecutableOrigin(command, workspaceRoot, stagingRoot, configuredTrustedPaths, out trustedExe))
            {
                return AskRequired(command, args, $"Executable '{command}' did not resolve to a verified trusted installation location.");
            }
        }

        return stem.Equals("git", StringComparison.OrdinalIgnoreCase)
            ? EvaluateGit(trustedExe, args, workspaceRoot, stagingRoot, stagingWorkspaceDir, guestGitDir, sessionTempDir, backend)
            : EvaluateRipgrep(trustedExe, args, workspaceRoot, stagingRoot, stagingWorkspaceDir, sessionTempDir, backend);
    }

    internal static bool IsStrictlyReadOnlyGitSyntax(IReadOnlyList<string> args, out string rejectionReason)
    {
        rejectionReason = string.Empty;
        if (args.Count == 0)
        {
            rejectionReason = "git requires an explicit read-only subcommand.";
            return false;
        }

        var subcommand = args[0];
        if (!AllowedGitSubcommands.Contains(subcommand))
        {
            rejectionReason = $"git subcommand '{subcommand}' is not auto-approved.";
            return false;
        }

        // Pass 1 (unconditional, never skips any token): reject any forbidden flag prefix before `--`.
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                break;
            }

            if (arg.StartsWith('-') && IsForbiddenGitFlag(arg))
            {
                rejectionReason = $"git argument '{arg}' is not permitted for auto-approval.";
                return false;
            }
        }

        // Pass 2: subcommand-specific positive syntax allow-list (never consumes next argv for optional-value flags).
        var isStatus = subcommand.Equals("status", StringComparison.Ordinal);
        var boolFlags = isStatus ? SafeGitStatusBooleanFlags : SafeGitDiffLogShowBooleanFlags;
        var equalsFlags = isStatus ? SafeGitStatusEqualsFlags : SafeGitDiffLogShowEqualsFlags;

        var afterDoubleDash = false;
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (!afterDoubleDash && arg == "--")
            {
                afterDoubleDash = true;
                continue;
            }

            if (!afterDoubleDash && arg.StartsWith('-'))
            {
                if (!isStatus && arg.Length > 1 && arg[1..].All(char.IsAsciiDigit))
                {
                    continue;
                }

                var eqIdx = arg.IndexOf('=', StringComparison.Ordinal);
                if (eqIdx > 0)
                {
                    var flagName = arg[..eqIdx];
                    if (equalsFlags.Contains(flagName))
                    {
                        continue;
                    }

                    rejectionReason = $"git option '{arg}' is outside the read-only allow-list.";
                    return false;
                }

                if (boolFlags.Contains(arg))
                {
                    continue;
                }

                if (!isStatus && (arg == "-U" || arg == "-n"))
                {
                    if (i + 1 < args.Count && args[i + 1].Length > 0 && args[i + 1].All(char.IsAsciiDigit))
                    {
                        i++;
                        continue;
                    }

                    rejectionReason = $"git option '{arg}' requires an explicit numeric count.";
                    return false;
                }

                if (!isStatus && arg.Length > 2 &&
                    (arg.StartsWith("-U", StringComparison.Ordinal) || arg.StartsWith("-n", StringComparison.Ordinal)) &&
                    arg[2..].All(char.IsAsciiDigit))
                {
                    continue;
                }

                rejectionReason = $"git option '{arg}' is outside the read-only syntax allow-list.";
                return false;
            }

            // B4 (Round 3): Unconditionally reject ALL git pathspec magic (both long `:(top)` / `:(glob)` and short `:/`, `:!`, `:^`, `:*`)
            // regardless of whether `--` appeared, with ZERO revision-range exemption!
            if (arg.StartsWith(':'))
            {
                rejectionReason = $"git positional argument '{arg}' uses disallowed pathspec magic.";
                return false;
            }

            if (arg.Contains("..", StringComparison.Ordinal))
            {
                if (afterDoubleDash || !IsValidGitRevisionRangeToken(arg))
                {
                    rejectionReason = $"git positional argument '{arg}' uses disallowed parent traversal.";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsValidGitRevisionRangeToken(string token)
    {
        if (token.Contains('/', StringComparison.Ordinal) ||
            token.Contains('\\', StringComparison.Ordinal) ||
            token.Contains(':', StringComparison.Ordinal) ||
            token.Contains("....", StringComparison.Ordinal))
        {
            return false;
        }

        var separator = token.Contains("...", StringComparison.Ordinal) ? "..." : "..";
        var parts = token.Split(separator, StringSplitOptions.None);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return false;
        }

        static bool IsSafeRevSide(string s)
            => !s.StartsWith('.') &&
               !s.StartsWith('-') &&
               !s.EndsWith('.') &&
               !s.Contains("..", StringComparison.Ordinal) &&
               s.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or '~' or '^');

        return IsSafeRevSide(parts[0]) && IsSafeRevSide(parts[1]);
    }

    private static AutoInWorkspaceEvaluation EvaluateGit(
        string trustedGitExe,
        IReadOnlyList<string> args,
        string workspaceRoot,
        string stagingRoot,
        string stagingWorkspaceDir,
        string? guestGitDir,
        string sessionTempDir,
        ExecutionBackend backend)
    {
        if (!IsStrictlyReadOnlyGitSyntax(args, out var syntaxRejection))
        {
            return AskRequired(trustedGitExe, args, syntaxRejection);
        }

        var subcommand = args[0];
        var hasDedicatedStaging = HasDedicatedStagingWorkspace(stagingWorkspaceDir, workspaceRoot, stagingRoot);
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--" || arg.StartsWith('-'))
            {
                continue;
            }

            if (backend == ExecutionBackend.Local && IsRootedOrDriveQualified(arg))
            {
                if (!hasDedicatedStaging || !IsPathStrictlyInsideStagingWorkspace(arg, stagingWorkspaceDir))
                {
                    return AskRequired(trustedGitExe, args, $"git path argument '{arg}' targets a host absolute path outside isolated staging.");
                }
            }
            else if (backend == ExecutionBackend.Docker && arg.StartsWith('/') &&
                     !arg.Equals("/workspace", StringComparison.Ordinal) &&
                     !arg.StartsWith("/workspace/", StringComparison.Ordinal))
            {
                return AskRequired(trustedGitExe, args, $"git path argument '{arg}' escapes /workspace.");
            }
        }

        string emptyHooksDir;
        string emptyConfigFile;
        Dictionary<string, string?> env;
        if (backend == ExecutionBackend.Docker)
        {
            emptyHooksDir = "/tmp";
            emptyConfigFile = "/dev/null";
            env = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["PATH"] = "/usr/bin:/bin",
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = "/dev/null",
                ["GIT_TERMINAL_PROMPT"] = "0"
            };
        }
        else
        {
            Directory.CreateDirectory(sessionTempDir);
            emptyHooksDir = Path.Combine(sessionTempDir, "cf-empty-git-hooks");
            emptyConfigFile = Path.Combine(sessionTempDir, "cf-empty-gitconfig");
            Directory.CreateDirectory(emptyHooksDir);
            if (!File.Exists(emptyConfigFile))
            {
                File.WriteAllText(emptyConfigFile, string.Empty);
            }

            env = BuildMinimalBaseEnvironment(sessionTempDir);
            if (!string.IsNullOrWhiteSpace(guestGitDir))
            {
                env["GIT_DIR"] = guestGitDir;
                env["GIT_WORK_TREE"] = stagingWorkspaceDir;
            }

            env["GIT_CONFIG_NOSYSTEM"] = "1";
            env["GIT_CONFIG_GLOBAL"] = emptyConfigFile;
            env["GIT_TERMINAL_PROMPT"] = "0";
        }

        var rewritten = new List<string>
        {
            "--no-pager",
            "-c", "core.fsmonitor=",
            "-c", $"core.hooksPath={emptyHooksDir}",
            "-c", "diff.external=",
            "-c", "core.pager=cat",
            subcommand
        };

        if (subcommand is "diff" or "show" or "log")
        {
            rewritten.Add("--no-ext-diff");
            rewritten.Add("--no-textconv");
        }

        for (var i = 1; i < args.Count; i++)
        {
            rewritten.Add(args[i]);
        }

        return new AutoInWorkspaceEvaluation(
            true,
            trustedGitExe,
            rewritten,
            env,
            "Auto-approved read-only GuestGit command.",
            RequiresGuestGit: true);
    }

    internal static bool IsForbiddenGitFlag(string arg)
        => ForbiddenGitArgPrefixes.Any(prefix =>
            arg.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase) ||
            (prefix.Length == 2 && prefix[0] == '-' && arg.StartsWith(prefix, StringComparison.Ordinal)));

    private static AutoInWorkspaceEvaluation EvaluateRipgrep(
        string trustedRgExe,
        IReadOnlyList<string> args,
        string workspaceRoot,
        string stagingRoot,
        string stagingWorkspaceDir,
        string sessionTempDir,
        ExecutionBackend backend)
    {
        var positional = new List<string>();
        var endOfOptions = false;
        var hasExplicitPatternFlag = false;
        var isFilesMode = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!endOfOptions && arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && arg.StartsWith('-'))
            {
                if (arg.StartsWith("--pre", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-z", StringComparison.Ordinal) ||
                    arg.StartsWith("--search-zip", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("--hostname-bin", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("--dfa-size-limit", StringComparison.OrdinalIgnoreCase))
                {
                    return AskRequired(trustedRgExe, args, $"rg option '{arg}' may invoke external preprocessors or is disallowed.");
                }

                var eqIdx = arg.IndexOf('=', StringComparison.Ordinal);
                var flagName = eqIdx > 0 ? arg[..eqIdx] : arg;

                if (flagName.Equals("--files", StringComparison.Ordinal))
                {
                    isFilesMode = true;
                }

                if (flagName is "-e" or "--regexp")
                {
                    hasExplicitPatternFlag = true;
                }

                if (SafeRgBooleanFlags.Contains(flagName) && eqIdx < 0)
                {
                    continue;
                }

                if (SafeRgValueFlags.Contains(flagName))
                {
                    if (eqIdx < 0)
                    {
                        if (i + 1 >= args.Count)
                        {
                            return AskRequired(trustedRgExe, args, $"rg option '{arg}' is missing its value.");
                        }

                        // Ensure next token is not a dangerous preprocessor flag disguised as value when empty
                        var nextVal = args[i + 1];
                        if (nextVal.StartsWith("--pre", StringComparison.OrdinalIgnoreCase) ||
                            nextVal.StartsWith("--search-zip", StringComparison.OrdinalIgnoreCase))
                        {
                            return AskRequired(trustedRgExe, args, $"rg option '{arg}' followed by ambiguous flag '{nextVal}'.");
                        }

                        i++;
                    }

                    continue;
                }

                return AskRequired(trustedRgExe, args, $"rg option '{arg}' is outside the read-only allow-list.");
            }

            positional.Add(arg);
        }

        var hasDedicatedStaging = HasDedicatedStagingWorkspace(stagingWorkspaceDir, workspaceRoot, stagingRoot);
        var pathStartIndex = isFilesMode || hasExplicitPatternFlag ? 0 : 1;
        for (var i = pathStartIndex; i < positional.Count; i++)
        {
            var pathArg = positional[i];
            if (backend == ExecutionBackend.Docker)
            {
                if (pathArg.StartsWith('/'))
                {
                    if (!pathArg.Equals("/workspace", StringComparison.Ordinal) &&
                        !pathArg.StartsWith("/workspace/", StringComparison.Ordinal))
                    {
                        return AskRequired(trustedRgExe, args, $"rg target path '{pathArg}' escapes /workspace.");
                    }

                    var sub = pathArg.Equals("/workspace", StringComparison.Ordinal) ? "." : pathArg["/workspace/".Length..];
                    if (sub is not "." && (!WorkspacePath.TryNormalizeStrict(sub, out var normSub, out _) || WorkspacePath.IsProtected(normSub)))
                    {
                        return AskRequired(trustedRgExe, args, $"rg target path '{pathArg}' targets a protected or invalid workspace path.");
                    }
                }
                else if (pathArg is not "." and not "./" &&
                         (!WorkspacePath.TryNormalizeStrict(pathArg, out var normRel, out _) || WorkspacePath.IsProtected(normRel)))
                {
                    return AskRequired(trustedRgExe, args, $"rg target path '{pathArg}' escapes /workspace or targets a protected path.");
                }
            }
            else
            {
                if (IsRootedOrDriveQualified(pathArg))
                {
                    // NEVER auto-allow host workspace absolute paths (R6): rooted paths are accepted ONLY when an already-isolated
                    // dedicated stagingWorkspaceDir under stagingRoot (strictly disjoint from workspaceRoot) was explicitly supplied.
                    if (!hasDedicatedStaging || !IsPathStrictlyInsideStagingWorkspace(pathArg, stagingWorkspaceDir))
                    {
                        return AskRequired(trustedRgExe, args, $"rg target path '{pathArg}' is a rooted host path and cannot bypass staging workspace filtering.");
                    }
                }
                else if (pathArg is not "." and not "./")
                {
                    if (!WorkspacePath.TryNormalizeStrict(pathArg, out var normRel, out _) || WorkspacePath.IsProtected(normRel))
                    {
                        return AskRequired(trustedRgExe, args, $"rg target path '{pathArg}' escapes the staging workspace or targets a protected path.");
                    }

                    if (hasDedicatedStaging && !IsPathStrictlyInsideStagingWorkspace(pathArg, stagingWorkspaceDir))
                    {
                        return AskRequired(trustedRgExe, args, $"rg target path '{pathArg}' resolves via link outside the staging workspace.");
                    }
                }
            }
        }

        var rewritten = new List<string>(1 + args.Count) { "--no-config" };
        rewritten.AddRange(args);

        Dictionary<string, string?> env;
        if (backend == ExecutionBackend.Docker)
        {
            env = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["PATH"] = "/usr/bin:/bin",
                ["RIPGREP_CONFIG_PATH"] = string.Empty
            };
        }
        else
        {
            env = BuildMinimalBaseEnvironment(sessionTempDir);
            env["RIPGREP_CONFIG_PATH"] = null;
        }

        return new AutoInWorkspaceEvaluation(true, trustedRgExe, rewritten, env, "Auto-approved read-only ripgrep search.");
    }

    private static bool IsRootedOrDriveQualified(string rawPath)
        => !string.IsNullOrEmpty(rawPath) &&
           (Path.IsPathRooted(rawPath) ||
            rawPath.StartsWith('/') ||
            rawPath.StartsWith('\\') ||
            (rawPath.Length >= 2 && rawPath[1] == ':'));

    private static bool HasDedicatedStagingWorkspace(string stagingWorkspaceDir, string workspaceRoot, string stagingRoot)
    {
        if (string.IsNullOrWhiteSpace(stagingWorkspaceDir) || string.IsNullOrWhiteSpace(stagingRoot))
        {
            return false;
        }

        var stgWsFull = (ResolveFinalTargetPath(stagingWorkspaceDir) ?? Path.GetFullPath(stagingWorkspaceDir))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var stgRootFull = (ResolveFinalTargetPath(stagingRoot) ?? Path.GetFullPath(stagingRoot))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!IsUnderOrEqual(stgWsFull, stgRootFull))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var wsFull = (ResolveFinalTargetPath(workspaceRoot) ?? Path.GetFullPath(workspaceRoot))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (IsUnderOrEqual(stgWsFull, wsFull) || IsUnderOrEqual(wsFull, stgWsFull))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPathStrictlyInsideStagingWorkspace(string rawPath, string stagingWorkspaceDir)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        var resolvedRoot = ResolveFinalTargetPath(stagingWorkspaceDir) ?? Path.GetFullPath(stagingWorkspaceDir);
        var wsFull = resolvedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.IsPathRooted(rawPath)
            ? Path.GetFullPath(rawPath)
            : Path.GetFullPath(Path.Combine(wsFull, rawPath));

        if (!IsUnderOrEqual(candidate, wsFull))
        {
            return false;
        }

        // Walk existing ancestors of candidate to ensure no intermediate directory symlink/junction escapes wsFull.
        var probe = candidate;
        while (!string.IsNullOrEmpty(probe) && !File.Exists(probe) && !Directory.Exists(probe))
        {
            probe = Path.GetDirectoryName(probe);
        }

        if (!string.IsNullOrEmpty(probe))
        {
            var resolved = ResolveFinalTargetPath(probe);
            if (resolved is null || !IsUnderOrEqual(resolved, wsFull))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string?> BuildMinimalBaseEnvironment(string sessionTempDir)
    {
        Directory.CreateDirectory(sessionTempDir);
        var homeDir = Path.Combine(sessionTempDir, "cf-auto-home");
        Directory.CreateDirectory(homeDir);

        var env = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            ["HOME"] = homeDir,
            ["USERPROFILE"] = homeDir,
            ["TEMP"] = sessionTempDir,
            ["TMP"] = sessionTempDir
        };

        foreach (var sysVar in new[] { "SystemRoot", "WINDIR", "OS" })
        {
            var val = Environment.GetEnvironmentVariable(sysVar);
            if (!string.IsNullOrEmpty(val))
            {
                env[sysVar] = val;
            }
        }

        return env;
    }

    private static string? ResolveWithSanitizedPath(
        string commandOrPath,
        string workspaceRoot,
        string stagingRoot,
        IReadOnlyList<string> trustedRoots)
    {
        var hasSeparator = commandOrPath.Contains(Path.DirectorySeparatorChar) ||
                           commandOrPath.Contains(Path.AltDirectorySeparatorChar);
        if (hasSeparator)
        {
            if (!Path.IsPathRooted(commandOrPath))
            {
                return null;
            }

            var full = Path.GetFullPath(commandOrPath);
            return File.Exists(full) ? full : null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Path.HasExtension(commandOrPath) ? new[] { "" } : new[] { ".exe" })
            : new[] { "" };

        var rawPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var candidateDirs = new List<string>();
        foreach (var entry in rawPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var clean = entry.Trim('"');
            if (string.IsNullOrWhiteSpace(clean) || !Path.IsPathRooted(clean))
            {
                continue;
            }

            string fullDir;
            try
            {
                fullDir = Path.GetFullPath(clean);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (IsUnderOrEqual(fullDir, workspaceRoot) || IsUnderOrEqual(fullDir, stagingRoot))
            {
                continue;
            }

            if (trustedRoots.Any(root => IsTrustedMatch(fullDir, root) || IsUnderOrEqual(root, fullDir)))
            {
                candidateDirs.Add(fullDir);
            }
        }

        foreach (var root in trustedRoots)
        {
            if (File.Exists(root))
            {
                var fileName = Path.GetFileName(root);
                if (extensions.Any(ext => string.Equals(fileName, commandOrPath + ext, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                {
                    return root;
                }
            }
            else if (Directory.Exists(root))
            {
                candidateDirs.Add(root);
                var cmdSub = Path.Combine(root, "cmd");
                var binSub = Path.Combine(root, "bin");
                if (Directory.Exists(cmdSub)) candidateDirs.Add(cmdSub);
                if (Directory.Exists(binSub)) candidateDirs.Add(binSub);
            }
        }

        foreach (var dir in candidateDirs)
        {
            foreach (var ext in extensions)
            {
                var probe = Path.Combine(dir, commandOrPath + ext);
                if (File.Exists(probe))
                {
                    return Path.GetFullPath(probe);
                }
            }
        }

        return null;
    }

    private static List<string> BuildTrustedRoots(
        string workspaceRoot,
        string stagingRoot,
        IReadOnlyList<string> configuredTrustedPaths)
    {
        var roots = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            foreach (var regInstall in QueryWindowsGitInstallLocations())
            {
                TryAddTrustedPath(roots, regInstall, workspaceRoot, stagingRoot);
            }
        }
        else
        {
            TryAddTrustedPath(roots, "/usr/bin", workspaceRoot, stagingRoot);
            TryAddTrustedPath(roots, "/bin", workspaceRoot, stagingRoot);
        }

        foreach (var custom in configuredTrustedPaths)
        {
            TryAddTrustedPath(roots, custom, workspaceRoot, stagingRoot);
        }

        return roots;
    }

    private static void TryAddTrustedPath(List<string> roots, string? rawPath, string workspaceRoot, string stagingRoot)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || !Path.IsPathRooted(rawPath))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(rawPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Only accept a configured trusted root if its own real target (resolving parent junctions/symlinks)
            // does not point inside the workspace or staging directory.
            if (File.Exists(full) || Directory.Exists(full))
            {
                var resolved = ResolveFinalTargetPath(full);
                if (resolved is null || IsUnderOrEqual(resolved, workspaceRoot) || IsUnderOrEqual(resolved, stagingRoot))
                {
                    return;
                }

                full = resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            if (!IsUnderOrEqual(full, workspaceRoot) && !IsUnderOrEqual(full, stagingRoot))
            {
                roots.Add(full);
            }
        }
        catch (ArgumentException)
        {
        }
    }

    /// <summary>
    /// Resolves the canonical final target path across EVERY component of <paramref name="path"/>
    /// (catching parent directory junctions, mount points, and symlinks even when the leaf file is a plain regular file).
    /// </summary>
    internal static string? ResolveFinalTargetPath(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var byHandle = TryGetWindowsFinalPathByHandle(path);
                if (byHandle is not null)
                {
                    return byHandle;
                }
            }

            var candidatePath = Path.GetFullPath(path);
            for (var resolveRound = 0; resolveRound < 16; resolveRound++)
            {
                var current = candidatePath;
                var tailSegments = new Stack<string>();
                var expandedLinkInRound = false;
                for (var depth = 0; depth < 64 && !string.IsNullOrEmpty(current); depth++)
                {
                    FileSystemInfo? info = Directory.Exists(current)
                        ? new DirectoryInfo(current)
                        : (File.Exists(current) ? new FileInfo(current) : null);

                    if (info is not null && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                    {
                        var target = info.ResolveLinkTarget(returnFinalTarget: true);
                        if (target is null)
                        {
                            return null;
                        }

                        var rebuilt = Path.GetFullPath(target.FullName);
                        while (tailSegments.Count > 0)
                        {
                            rebuilt = Path.Combine(rebuilt, tailSegments.Pop());
                        }

                        candidatePath = Path.GetFullPath(rebuilt);
                        expandedLinkInRound = true;
                        break;
                    }

                    var parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                    {
                        break;
                    }

                    tailSegments.Push(Path.GetFileName(current));
                    current = parent;
                }

                if (!expandedLinkInRound)
                {
                    return File.Exists(candidatePath) || Directory.Exists(candidatePath) ? candidatePath : null;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? TryGetWindowsFinalPathByHandle(string path)
    {
        const uint FileShareAll = 0x00000001 | 0x00000002 | 0x00000004;
        const uint OpenExisting = 3;
        const uint FileFlagBackupSemantics = 0x02000000;

        using var handle = CreateFileW(
            path,
            0, // zero desired access queries metadata/final path without requiring read permission on locked files
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[1024];
        var len = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (len == 0 || len >= buffer.Length)
        {
            return null;
        }

        var raw = new string(buffer, 0, (int)len);
        if (raw.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            raw = @"\\" + raw[8..];
        }
        else if (raw.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            raw = raw[4..];
        }

        return Path.GetFullPath(raw);
    }

    private static bool IsTrustedMatch(string candidatePath, string trustedEntry)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (File.Exists(trustedEntry))
        {
            return string.Equals(candidatePath, trustedEntry, comparison);
        }

        return IsUnderOrEqual(candidatePath, trustedEntry);
    }

    internal static bool IsUnderOrEqual(string candidatePath, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normCandidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normBase = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normCandidate, normBase, comparison))
        {
            return true;
        }

        return normCandidate.StartsWith(normBase + Path.DirectorySeparatorChar, comparison);
    }

    private static List<string> QueryWindowsGitInstallLocations()
    {
        var results = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return results;
        }

        var hklm = new IntPtr(unchecked((int)0x80000002));
        var hkcu = new IntPtr(unchecked((int)0x80000001));
        foreach (var root in new[] { hklm, hkcu })
        {
            if (TryReadRegString(root, @"SOFTWARE\GitForWindows", "InstallPath", out var gitInstall))
            {
                results.Add(gitInstall);
            }

            if (TryReadRegString(root, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Git_is1", "InstallLocation", out var uninstallLoc))
            {
                results.Add(uninstallLoc);
            }
        }

        return results;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport("advapi32.dll", EntryPoint = "RegGetValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegGetValueW(
        IntPtr hkey,
        string lpSubKey,
        string lpValue,
        uint dwFlags,
        out uint pdwType,
        [Out] char[]? pvData,
        ref uint pcbData);

    private static bool TryReadRegString(IntPtr rootKey, string subKey, string valueName, out string result)
    {
        result = string.Empty;
        const uint RrfRtRegSz = 0x00000002;
        uint byteCount = 1024;
        var buffer = new char[byteCount / sizeof(char)];
        var rc = RegGetValueW(rootKey, subKey, valueName, RrfRtRegSz, out _, buffer, ref byteCount);
        if (rc != 0 || byteCount < sizeof(char))
        {
            return false;
        }

        var charLen = (int)(byteCount / sizeof(char)) - 1;
        if (charLen <= 0)
        {
            return false;
        }

        result = new string(buffer, 0, charLen).TrimEnd('\0');
        return !string.IsNullOrWhiteSpace(result);
    }

    private static AutoInWorkspaceEvaluation AskRequired(string executable, IReadOnlyList<string> args, string reason)
        => new(false, executable, args, null, reason);
}
