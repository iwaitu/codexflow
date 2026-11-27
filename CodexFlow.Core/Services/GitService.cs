// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CodexFlow.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Services;

public class GitService : IGitService, IShadowWorktreeCreationDiagnostics
{
    private readonly ILogger<GitService> _logger;
    private const int MaxShadowSegmentLength = 24;

    public GitService(ILogger<GitService> logger)
    {
        _logger = logger;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunGitCommandAsync(
        string path, string args, bool invariantMessages = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "-c core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null") +
                " -c core.quotepath=false -c core.fsmonitor=false -c commit.gpgSign=false -c tag.gpgSign=false -c protocol.ext.allow=never " + args,
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        ApplySanitizedGitEnvironment(startInfo);
        if (invariantMessages)
        {
            startInfo.Environment["LC_ALL"] = "C";
        }

        return await ExecuteGitProcessAsync(startInfo, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a read-only git inspection command while neutralizing ALL repository-local clean/smudge/process
    /// filters and external diff/textconv drivers discovered in git configuration.
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error)> RunSafeReadOnlyGitCommandAsync(
        string path,
        params string[] commandArgs)
    {
        var (filterPrefixes, diffPrefixes) = await DiscoverConfiguredDriversAsync(path).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        ApplySanitizedGitEnvironment(startInfo);
        AddSafeBaseGitConfigArgs(startInfo);

        foreach (var prefix in filterPrefixes)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"{prefix}.clean=");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"{prefix}.smudge=");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"{prefix}.process=");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"{prefix}.required=false");
        }

        foreach (var prefix in diffPrefixes)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"{prefix}.command=");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"{prefix}.textconv=");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"{prefix}.cachetextconv=false");
        }

        foreach (var arg in commandArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return await ExecuteGitProcessAsync(startInfo, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    private static async Task<(string[] FilterPrefixes, string[] DiffPrefixes)> DiscoverConfiguredDriversAsync(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        ApplySanitizedGitEnvironment(startInfo);
        startInfo.ArgumentList.Add("--no-pager");
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add("--list");
        startInfo.ArgumentList.Add("--name-only");
        startInfo.ArgumentList.Add("--null");

        var res = await ExecuteGitProcessAsync(startInfo, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to inspect repository git configuration before read-only diff (exit {res.ExitCode}): {res.Error}");
        }

        if (string.IsNullOrEmpty(res.Output))
        {
            return ([], []);
        }

        // Git subsection names (e.g. filter.evil vs filter.Evil) are case-sensitive.
        var filterPrefixes = new HashSet<string>(StringComparer.Ordinal);
        var diffPrefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in res.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (key.Any(char.IsControl))
            {
                continue;
            }

            var lastDot = key.LastIndexOf('.');
            if (lastDot <= 0)
            {
                continue;
            }

            if (key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) && lastDot > "filter.".Length)
            {
                filterPrefixes.Add("filter." + key["filter.".Length..lastDot]);
            }
            else if (key.StartsWith("diff.", StringComparison.OrdinalIgnoreCase) && lastDot > "diff.".Length)
            {
                diffPrefixes.Add("diff." + key["diff.".Length..lastDot]);
            }
        }

        return (filterPrefixes.ToArray(), diffPrefixes.ToArray());
    }

    private static void AddSafeBaseGitConfigArgs(ProcessStartInfo startInfo)
    {
        var nullDev = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.ArgumentList.Add("--no-pager");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"core.hooksPath={nullDev}");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.fsmonitor=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("commit.gpgSign=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("tag.gpgSign=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("protocol.ext.allow=never");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.pager=");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("credential.helper=");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("diff.external=");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("diff.noprefix=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("diff.mnemonicPrefix=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("diff.srcPrefix=a/");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("diff.dstPrefix=b/");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.alternateRefsCommand=");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"core.attributesFile={nullDev}");
    }

    private static void ApplySanitizedGitEnvironment(ProcessStartInfo startInfo)
    {
        // Repository code must not be invoked through inherited Git configuration or environment overrides.
        foreach (var key in startInfo.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            startInfo.Environment.Remove(key);
        }

        var nullDev = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = nullDev;
        startInfo.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        startInfo.Environment["GIT_ASKPASS"] = string.Empty;
        startInfo.Environment["GIT_SSH_COMMAND"] = "false";
        startInfo.Environment["GIT_PAGER"] = string.Empty;
    }

    private static async Task<(int ExitCode, string Output, string Error)> ExecuteGitProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout)
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        using var cts = new CancellationTokenSource(timeout);
        var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var errorTask = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return (process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, string.Empty, $"git command timed out after {timeout.TotalSeconds:0}s.");
        }
    }

    private static string QuoteGitArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>
    /// Diff entry points take caller-supplied refs. A ref that starts with '-' (e.g. --output=FILE), carries whitespace, control
    /// characters/NUL, or contains a quote could be read by git as an option or break out of the quoting, so such a ref
    /// is refused (fail closed: throwing is safer than returning an empty "no changes" list for a scope check).
    /// </summary>
    private static void ValidateSafeRefArgument(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] == '-' || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '"'))
            throw new ArgumentException("Git ref is empty or not a safe revision argument.", parameterName);
    }

    private static string QuoteRefArgument(string value, string parameterName)
    {
        ValidateSafeRefArgument(value, parameterName);
        return QuoteGitArgument(value);
    }

    private static bool IsDefaultBranchPlaceholder(string value)
        => string.Equals(value, "main", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "master", StringComparison.OrdinalIgnoreCase);

    private static async Task EnableLongPathsIfSupportedAsync(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await RunGitCommandAsync(path, "config core.longpaths true").ConfigureAwait(false);
    }

    public async Task<bool> InitRepositoryAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (Directory.Exists(Path.Combine(path, ".git"))) return true;

        Directory.CreateDirectory(path); // 确保目录物理存在

        var result = await RunGitCommandAsync(path, "init").ConfigureAwait(false);
        if (result.ExitCode != 0) return false;

        await RunGitCommandAsync(path, "config user.name \"IvilsonAgent\"").ConfigureAwait(false);
        await RunGitCommandAsync(path, "config user.email \"agent@ivilson.com\"").ConfigureAwait(false);
        await EnableLongPathsIfSupportedAsync(path).ConfigureAwait(false);

        // Initial commit
        await File.WriteAllTextAsync(Path.Combine(path, ".gitignore"), ".venv/\nnode_modules/\nbin/\nobj/\n").ConfigureAwait(false);
        await RunGitCommandAsync(path, "add .").ConfigureAwait(false);
        await RunGitCommandAsync(path, "commit -m \"Initial workspace setup\"").ConfigureAwait(false);

        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex ScpLikeSource = new(
        @"^[A-Za-z0-9._-]+@[A-Za-z0-9][A-Za-z0-9.-]*:(?![/\\])[^\s]+$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static bool IsRemoteCloneSource(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('-') || url.Contains((char)92) || url.Contains((char)0))
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return !uri.IsFile && uri.Scheme is "https" or "ssh" && !string.IsNullOrEmpty(uri.Host);
        }

        return ScpLikeSource.IsMatch(url);
    }

    public async Task<bool> CloneRepositoryAsync(string url, string path, string? branch = null, string? githubToken = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(path);

        // Only remote https/ssh sources (including scp-like user@host:path). file URIs, local paths, ext::/fd:: transports,
        // option-looking strings and every other scheme are refused before git is ever started.
        if (!IsRemoteCloneSource(url))
        {
            StructuredLog.Warning(_logger, "Refusing to clone a non-remote source (scheme/shape not allowed) into {Path}", path);
            return false;
        }

        StructuredLog.Information(_logger, "Cloning repository: {Url} to {Path} (branch: {Branch})", url, path, branch ?? "default");

        // 如果目标路径已存在 .git，检查是否为有效的克隆仓库
        if (Directory.Exists(Path.Combine(path, ".git")))
        {
            // 检查是否有 remote origin（区分 git init 和 git clone）
            var remoteResult = await RunGitCommandAsync(path, "remote get-url origin").ConfigureAwait(false);
            if (remoteResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(remoteResult.Output))
            {
                var existingRemote = remoteResult.Output.Trim();
                // 匹配已有 remote 与请求 URL（兼容末尾斜杠差异）
                if (string.Equals(existingRemote, url, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existingRemote, url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    // 同一仓库，检查是否有实际的项目文件（不只是 .git + .gitignore）
                    var projectFiles = Directory.GetFiles(path)
                        .Where(f => !Path.GetFileName(f).Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    if (projectFiles.Length > 0)
                    {
                        StructuredLog.Information(_logger, "Repository already cloned at {Path} (remote: {Remote}, {FileCount} project files)", path, existingRemote, projectFiles.Length);
                        return true;
                    }

                    StructuredLog.Warning(_logger, "Repository at {Path} has matching remote but no project files. Will re-clone.", path);
                }
                else
                {
                    StructuredLog.Warning(_logger, "Repository at {Path} has different remote ({Existing} vs {Requested}). Will re-clone.", path, existingRemote, url);
                }
            }
            else
            {
                StructuredLog.Warning(_logger, "Directory at {Path} has .git but no remote origin (likely from 'git init'). Will re-clone.", path);
            }

            // 删除旧目录以进行全新克隆
            StructuredLog.Information(_logger, "Removing stale git directory at {Path} for fresh clone", path);
            try
            {
                SetAttributesNormal(new DirectoryInfo(path));
                Directory.Delete(path, true);
            }
            catch (IOException ex)
            {
                StructuredLog.Error(_logger, ex, "Failed to remove stale directory at {Path}", path);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                StructuredLog.Error(_logger, ex, "Failed to remove stale directory at {Path}", path);
                return false;
            }
        }

        var parent = Directory.GetParent(path)!.FullName;
        Directory.CreateDirectory(parent); // 确保父目录存在

        StructuredLog.Debug(_logger, "Parent directory: {Parent}", parent);
        StructuredLog.Debug(_logger, "Target directory name: {DirName}", Path.GetFileName(path));

        var branchArg = !string.IsNullOrEmpty(branch) ? $"--branch {QuoteGitArgument(branch)}" : "";
        var cloneCommand = $"clone --depth 1 {branchArg} -- {QuoteGitArgument(url)} {QuoteGitArgument(Path.GetFileName(path))}";
        var isGithubHttps = Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl)
            && string.Equals(parsedUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(parsedUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase);

        // 策略：先匿名克隆，仅当出现鉴权失败且提供了 token 时才带凭据重试
        StructuredLog.Debug(_logger, "Executing anonymous git clone: git {Command}", cloneCommand);
        var result = await RunGitCommandAsync(parent, cloneCommand).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            var isAuthFailure = IsAuthenticationFailure(result.Error, result.Output);

            if (isAuthFailure && isGithubHttps && !string.IsNullOrWhiteSpace(githubToken))
            {
                StructuredLog.Warning(_logger, "Anonymous clone failed with auth error — retrying with user's GitHub token.");

                // 清理匿名克隆可能留下的残留目录
                var staleTarget = Path.Combine(parent, Path.GetFileName(path));
                if (Directory.Exists(staleTarget))
                {
                    try { SetAttributesNormal(new DirectoryInfo(staleTarget)); Directory.Delete(staleTarget, true); }
                    catch (IOException) { /* best effort */ }
                    catch (UnauthorizedAccessException) { /* best effort */ }
                }

                var escapedToken = githubToken!.Replace("\"", "\\\"", StringComparison.Ordinal);
                var authenticatedCloneCommand = $"-c http.extraHeader=\"Authorization: Bearer {escapedToken}\" {cloneCommand}";
                result = await RunGitCommandAsync(parent, authenticatedCloneCommand).ConfigureAwait(false);
            }
            else if (isAuthFailure)
            {
                StructuredLog.Warning(_logger, "Anonymous clone failed with auth error and no GitHub token provided. Likely a private repository.");
            }
            else
            {
                StructuredLog.Warning(_logger, "Anonymous clone failed for non-auth reason. Error: {Error}", result.Error);
            }
        }

        if (result.ExitCode != 0)
        {
            StructuredLog.Error(_logger, "Git clone failed with exit code {ExitCode}. Error: {Error}. Output: {Output}",
                result.ExitCode, result.Error, result.Output);
            return false;
        }

        StructuredLog.Information(_logger, "Git clone succeeded. Output: {Output}", result.Output);

        // 验证克隆结果
        if (!Directory.Exists(path))
        {
            StructuredLog.Error(_logger, "Clone reported success but target directory does not exist: {Path}", path);
            return false;
        }

        var gitDir = Path.Combine(path, ".git");
        if (!Directory.Exists(gitDir))
        {
            StructuredLog.Error(_logger, "Clone reported success but .git directory does not exist: {GitDir}", gitDir);
            return false;
        }

        // 配置 git 用户信息
        await RunGitCommandAsync(path, "config user.name \"IvilsonAgent\"").ConfigureAwait(false);
        await RunGitCommandAsync(path, "config user.email \"agent@ivilson.com\"").ConfigureAwait(false);
        await EnableLongPathsIfSupportedAsync(path).ConfigureAwait(false);

        StructuredLog.Information(_logger, "Repository cloned and configured successfully at {Path}", path);

        return true;
    }

    public Task<bool> CloneRepositoryAsync(Uri url, string path, string? branch = null, string? githubToken = null)
    {
        ArgumentNullException.ThrowIfNull(url);

        return CloneRepositoryAsync(url.ToString(), path, branch, githubToken);
    }

    /// <summary>
    /// 检测 git 输出是否包含鉴权失败的特征关键字。
    /// </summary>
    private static bool IsAuthenticationFailure(string? error, string? output)
    {
        var text = string.Concat(error ?? string.Empty, "\n", output ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var authMarkers = new[]
        {
            "authentication failed",
            "could not read username",
            "repository not found",
            "permission denied",
            "access denied",
            "http basic: access denied",
            "403",
            "401"
        };

        return authMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> CreateBranchAsync(string path, string branchName, string? startPoint = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(branchName);

        var startArg = !string.IsNullOrEmpty(startPoint) ? startPoint : "";
        var result = await RunGitCommandAsync(path, $"checkout -b {branchName} {startArg}").ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<string?> GetHeadHashAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var result = await RunGitCommandAsync(path, "rev-parse HEAD").ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return null;
        return result.Output.Trim();
    }

    public async Task<string?> GetCurrentBranchAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var result = await RunGitCommandAsync(path, "rev-parse --abbrev-ref HEAD").ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return null;

        var branch = result.Output.Trim();
        return string.Equals(branch, "HEAD", StringComparison.OrdinalIgnoreCase) ? null : branch;
    }

    public async Task<bool?> HasUncommittedChangesAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var result = await RunGitCommandAsync(path, "status --porcelain").ConfigureAwait(false);
        if (result.ExitCode != 0)
            return null;

        return !string.IsNullOrWhiteSpace(result.Output);
    }

    public async Task<string?> CreateShadowWorktreeAsync(string mainPath, string taskId, string baseBranch)
        => (await CreateShadowWorktreeWithDiagnosticsAsync(mainPath, taskId, baseBranch).ConfigureAwait(false)).Path;

    Task<ShadowWorktreeCreationResult> IShadowWorktreeCreationDiagnostics.CreateShadowWorktreeWithDiagnosticsAsync(
        string mainPath, string taskId, string baseBranch)
        => CreateShadowWorktreeWithDiagnosticsAsync(mainPath, taskId, baseBranch);

    private async Task<ShadowWorktreeCreationResult> CreateShadowWorktreeWithDiagnosticsAsync(
        string mainPath, string taskId, string baseBranch)
    {
        ArgumentNullException.ThrowIfNull(mainPath);
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(baseBranch);

        // Check Git ownership before touching a previous attempt's worktree or branch.
        var headResult = await ReadShadowRepositoryHeadAsync(mainPath).ConfigureAwait(false);
        if (IsRepositoryOwnershipError(headResult.ExitCode, headResult.Error))
        {
            StructuredLog.Warning(_logger, "Shadow worktree ownership check failed (exit {Code}): {Error}", headResult.ExitCode, headResult.Error);
            return new(null, ShadowWorktreeCreationFailure.RepositoryOwnership);
        }

        var shadowPath = BuildShadowPath(mainPath, taskId);
        var branchName = $"task-{taskId}";

        // 检测实际的基础分支名（git init 可能用 master 而不是 main）。
        //    If the caller passed an explicit ref/commit, keep it as the worktree baseline.
        var actualBase = baseBranch;
        if (IsDefaultBranchPlaceholder(baseBranch) &&
            headResult.ExitCode == 0 &&
            !string.IsNullOrWhiteSpace(headResult.Output))
        {
            var currentBranch = headResult.Output.Trim();
            if (!string.Equals(currentBranch, "HEAD", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(currentBranch, baseBranch, StringComparison.Ordinal))
            {
                actualBase = currentBranch;
                StructuredLog.Information(_logger, "Adjusted baseBranch from '{Requested}' to '{Actual}'", baseBranch, actualBase);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(shadowPath)!);

        // 检查工作树是否已存在
        if (Directory.Exists(shadowPath))
        {
            StructuredLog.Information(_logger, "Shadow path {Path} already exists. Attempting to prune or reuse.", shadowPath);
            await RunGitCommandAsync(mainPath, $"worktree prune").ConfigureAwait(false);
            // 如果目录还在，尝试物理删除
            if (Directory.Exists(shadowPath))
            {
                try { Directory.Delete(shadowPath, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        await RemoveAssociatedShadowWorktreesAsync(mainPath, shadowPath, branchName).ConfigureAwait(false);

        // 每次任务都从最新基线重建 task 分支，避免复用旧分支导致历史漂移
        var branchDelete = await DeleteShadowBranchAsync(mainPath, branchName).ConfigureAwait(false);
        if (branchDelete.ExitCode != 0)
        {
            var err = (branchDelete.Error ?? string.Empty) + (branchDelete.Output ?? string.Empty);
            if (!err.Contains("not found", StringComparison.OrdinalIgnoreCase)
                && !err.Contains("not a valid branch", StringComparison.OrdinalIgnoreCase)
                && !err.Contains("cannot delete branch", StringComparison.OrdinalIgnoreCase))
            {
                StructuredLog.Warning(_logger, "Failed to delete stale task branch {Branch}: {Error}", branchName, err.Trim());
            }
        }

        await EnableLongPathsIfSupportedAsync(mainPath).ConfigureAwait(false);

        var worktreeCmd = $"worktree add -b {QuoteGitArgument(branchName)} {QuoteGitArgument(shadowPath)} {QuoteGitArgument(actualBase)}";

        var result = await RunGitCommandAsync(mainPath, worktreeCmd, invariantMessages: true).ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            return new(shadowPath, ShadowWorktreeCreationFailure.None);
        }

        StructuredLog.Warning(_logger, "git worktree add failed (exit {Code}): {Err}", result.ExitCode, result.Output + result.Error);
        return new(null, IsRepositoryOwnershipError(result.ExitCode, result.Error)
            ? ShadowWorktreeCreationFailure.RepositoryOwnership
            : ShadowWorktreeCreationFailure.GitError);
    }

    internal static bool IsRepositoryOwnershipError(int exitCode, string error)
        => exitCode == 128 &&
           error.Split('\n').Any(line =>
               line.StartsWith("fatal: detected dubious ownership in repository at ", StringComparison.Ordinal) ||
               line.StartsWith("fatal: unsafe repository (", StringComparison.Ordinal));

    internal static async Task<bool> CanReadShadowRepositoryAsync(string path)
        => (await ReadShadowRepositoryHeadAsync(path).ConfigureAwait(false)).ExitCode == 0;

    private static Task<(int ExitCode, string Output, string Error)> ReadShadowRepositoryHeadAsync(string path)
        => RunGitCommandAsync(path, "rev-parse --abbrev-ref HEAD", invariantMessages: true);

    public async Task<bool> RemoveShadowWorktreeAsync(string mainPath, string taskId)
    {
        ArgumentNullException.ThrowIfNull(mainPath);
        ArgumentNullException.ThrowIfNull(taskId);

        var shadowPath = BuildShadowPath(mainPath, taskId);
        var branchName = $"task-{taskId}";

        // 1. 先從 git 中移除 worktree 記錄
        var result = await RunGitCommandAsync(mainPath, $"worktree remove {QuoteGitArgument(shadowPath)} --force").ConfigureAwait(false);

        // 2. 物理刪除殘留目錄 (如果 force remove 沒刪乾淨，處理只讀屬性和進程衝突)
        if (Directory.Exists(shadowPath))
        {
            try
            {
                SetAttributesNormal(new DirectoryInfo(shadowPath));
                Directory.Delete(shadowPath, true);
            }
            catch (IOException ex)
            {
                StructuredLog.Warning(_logger, "物理刪除影子路徑失敗（可能文件被鎖定）: {Path}, {Msg}", shadowPath, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                StructuredLog.Warning(_logger, "物理刪除影子路徑失敗（可能文件被鎖定）: {Path}, {Msg}", shadowPath, ex.Message);
            }
        }

        var branchDelete = await DeleteShadowBranchAsync(mainPath, branchName).ConfigureAwait(false);
        if (branchDelete.ExitCode != 0)
        {
            var err = (branchDelete.Error ?? string.Empty) + (branchDelete.Output ?? string.Empty);
            if (!err.Contains("not found", StringComparison.OrdinalIgnoreCase)
                && !err.Contains("not a valid branch", StringComparison.OrdinalIgnoreCase)
                && !err.Contains("branch", StringComparison.OrdinalIgnoreCase))
            {
                StructuredLog.Warning(_logger, "清理影子分支失败: {Branch}, {Error}", branchName, err.Trim());
            }
        }

        return result.ExitCode == 0;
    }

    private async Task RemoveAssociatedShadowWorktreesAsync(string mainPath, string shadowPath, string branchName)
    {
        var entries = await ListWorktreesAsync(mainPath).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return;
        }

        var mainPathFull = Path.GetFullPath(mainPath);
        var shadowPathFull = Path.GetFullPath(shadowPath);

        foreach (var entry in entries)
        {
            var entryPathFull = Path.GetFullPath(entry.Path);
            var matchesPath = string.Equals(entryPathFull, shadowPathFull, StringComparison.OrdinalIgnoreCase);
            var matchesBranch = string.Equals(entry.Branch, branchName, StringComparison.OrdinalIgnoreCase);
            if ((!matchesPath && !matchesBranch) || string.Equals(entryPathFull, mainPathFull, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            StructuredLog.Information(_logger, "Removing stale shadow worktree {Path} for branch {Branch}", entry.Path, entry.Branch);
            await RunGitCommandAsync(mainPath, $"worktree remove {QuoteGitArgument(entry.Path)} --force").ConfigureAwait(false);

            if (Directory.Exists(entry.Path))
            {
                try
                {
                    SetAttributesNormal(new DirectoryInfo(entry.Path));
                    Directory.Delete(entry.Path, true);
                }
                catch (IOException ex)
                {
                    StructuredLog.Warning(_logger, "Failed to delete stale shadow directory {Path}: {Message}", entry.Path, ex.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    StructuredLog.Warning(_logger, "Failed to delete stale shadow directory {Path}: {Message}", entry.Path, ex.Message);
                }
            }
        }

        await RunGitCommandAsync(mainPath, "worktree prune").ConfigureAwait(false);
    }

    private async Task<(int ExitCode, string Output, string Error)> DeleteShadowBranchAsync(string mainPath, string branchName)
    {
        await RunGitCommandAsync(mainPath, "worktree prune").ConfigureAwait(false);
        var branchDelete = await RunGitCommandAsync(mainPath, $"branch -D {branchName}").ConfigureAwait(false);
        if (branchDelete.ExitCode == 0)
        {
            return branchDelete;
        }

        await RemoveAssociatedShadowWorktreesAsync(mainPath, BuildShadowPath(mainPath, branchName["task-".Length..]), branchName).ConfigureAwait(false);
        return await RunGitCommandAsync(mainPath, $"branch -D {branchName}").ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<GitWorktreeEntry>> ListWorktreesAsync(string mainPath)
    {
        var result = await RunGitCommandAsync(mainPath, "worktree list --porcelain").ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            return [];
        }

        var entries = new List<GitWorktreeEntry>();
        string? currentPath = null;
        string? currentBranch = null;
        using var reader = new StringReader(result.Output);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (!string.IsNullOrWhiteSpace(currentPath))
                {
                    entries.Add(new GitWorktreeEntry(currentPath, currentBranch));
                }

                currentPath = null;
                currentBranch = null;
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                currentPath = line["worktree ".Length..].Trim();
                continue;
            }

            if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var branchRef = line["branch ".Length..].Trim();
                currentBranch = branchRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? branchRef["refs/heads/".Length..]
                    : branchRef;
            }
        }

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            entries.Add(new GitWorktreeEntry(currentPath, currentBranch));
        }

        return entries;
    }

    private sealed record GitWorktreeEntry(string Path, string? Branch);

    internal static string BuildShadowPath(string mainPath, string taskId)
    {
        var repoName = Path.GetFileName(mainPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(repoName)) repoName = "workspace";

        var shadowRoot = ResolveShadowRoot(mainPath);
        var repoSegment = SanitizeShadowSegment(repoName, "workspace");
        var taskSegment = SanitizeShadowSegment(taskId, "task");

        return Path.Combine(shadowRoot, "shadows", repoSegment, taskSegment);
    }

    private static string ResolveShadowRoot(string mainPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var workspaceRoot = Path.GetPathRoot(Path.GetFullPath(mainPath));
            if (!string.IsNullOrWhiteSpace(workspaceRoot))
            {
                return Path.Combine(workspaceRoot, "cfw");
            }
        }

        return Path.Combine(Path.GetTempPath(), "codexflow-shadow-root");
    }

    private static string SanitizeShadowSegment(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch);
        }

        var sanitized = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = fallback;
        }

        if (sanitized.Length <= MaxShadowSegmentLength)
        {
            return sanitized;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sanitized)))[..8];
        var prefixLength = Math.Max(8, MaxShadowSegmentLength - hash.Length - 1);
        return $"{sanitized[..prefixLength]}-{hash}";
    }

    public async Task<bool> CommitAsync(string path, string message)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(message);

        var staged = await RunGitCommandAsync(path, "add .").ConfigureAwait(false);
        if (staged.ExitCode != 0)
        {
            StructuredLog.Warning(_logger, "Git staging failed (exit {Code}).", staged.ExitCode);
            return false;
        }

        var command = $"commit -m {QuoteGitArgument(message)}";
        var result = await RunGitCommandAsync(path, command, invariantMessages: true).ConfigureAwait(false);
        if (result.ExitCode != 0 &&
            (result.Error.Contains("identity unknown", StringComparison.OrdinalIgnoreCase) ||
             result.Error.Contains("unable to auto-detect email address", StringComparison.OrdinalIgnoreCase)))
        {
            // Use the identity assigned to CodexFlow-managed repositories without editing a user's repository config.
            result = await RunGitCommandAsync(path,
                "-c user.name=IvilsonAgent -c user.email=agent@ivilson.com " + command, invariantMessages: true).ConfigureAwait(false);
        }

        if (result.ExitCode != 0)
        {
            StructuredLog.Warning(_logger, "Git commit failed (exit {Code}).", result.ExitCode);
        }
        return result.ExitCode == 0;
    }

    public async Task<bool> MergeAsync(string path, string sourceBranch, string targetBranch = "main")
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sourceBranch);
        ArgumentNullException.ThrowIfNull(targetBranch);

        // 策略升级：以影子分支内容直接覆盖目标分支，避免 merge 冲突阻塞流水线
        var checkout = await RunGitCommandAsync(path, $"checkout {targetBranch}").ConfigureAwait(false);
        if (checkout.ExitCode != 0)
        {
            StructuredLog.Warning(_logger, "Failed to checkout target branch {Target}: {Error}", targetBranch, checkout.Error);
            return false;
        }

        // 清理可能残留的未完成合并状态
        await RunGitCommandAsync(path, "merge --abort").ConfigureAwait(false);

        var reset = await RunGitCommandAsync(path, $"reset --hard {sourceBranch}").ConfigureAwait(false);
        if (reset.ExitCode != 0)
        {
            StructuredLog.Warning(_logger, "Failed to replace branch {Target} with {Source}: {Error}", targetBranch, sourceBranch, reset.Error);
            return false;
        }

        await RunGitCommandAsync(path, "clean -fd").ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RevertToLastCommitAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var result = await RunGitCommandAsync(path, "reset --hard HEAD").ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<bool> DiscardUncommittedChangesAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var reset = await RunGitCommandAsync(path, "reset --hard HEAD").ConfigureAwait(false);
        if (reset.ExitCode != 0)
        {
            return false;
        }

        var clean = await RunGitCommandAsync(path, "clean -fd").ConfigureAwait(false);
        return clean.ExitCode == 0;
    }

    public async Task<bool> ResetToCommitAsync(string path, string commitHash)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitHash);

        var result = await RunGitCommandAsync(path, $"reset --hard {commitHash}").ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<bool> RestoreFilesAsync(string path, string gitRef, IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitRef);
        ArgumentNullException.ThrowIfNull(relativePaths);

        var files = relativePaths
            .Where(static file => !string.IsNullOrWhiteSpace(file))
            .Select(static file => file.Replace('\\', '/').Trim().TrimStart('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            return true;
        }

        foreach (var file in files)
        {
            var result = await RunGitCommandAsync(path, $"restore --source {QuoteGitArgument(gitRef)} -- {QuoteGitArgument(file)}").ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                RemoveEmptyParentDirectories(path, file);
                continue;
            }

            var absolutePath = Path.Combine(path, file.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(absolutePath))
            {
                try
                {
                    File.Delete(absolutePath);
                    RemoveEmptyParentDirectories(path, file);
                    continue;
                }
                catch (IOException ex)
                {
                    StructuredLog.Warning(_logger, ex, "Failed to delete out-of-scope untracked file {FilePath}", absolutePath);
                }
                catch (UnauthorizedAccessException ex)
                {
                    StructuredLog.Warning(_logger, ex, "Failed to delete out-of-scope untracked file {FilePath}", absolutePath);
                }
            }

            StructuredLog.Warning(
                _logger,
                "Git restore failed for {File} from {Ref}. Error: {Error}. Output: {Output}",
                file,
                gitRef,
                result.Error,
                result.Output);
            return false;
        }

        return true;
    }

    private static void RemoveEmptyParentDirectories(string workspacePath, string relativePath)
    {
        var root = Path.GetFullPath(workspacePath);
        var current = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))));
        while (!string.IsNullOrWhiteSpace(current) &&
               current.StartsWith(root, StringComparison.Ordinal) &&
               !string.Equals(current, root, StringComparison.Ordinal))
        {
            if (!Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current);
                continue;
            }

            try
            {
                if (Directory.EnumerateFileSystemEntries(current).Any())
                {
                    break;
                }

                Directory.Delete(current);
                current = Path.GetDirectoryName(current);
            }
            catch (IOException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
        }
    }

    public async Task<string?> GetRemoteUrlAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!Directory.Exists(Path.Combine(path, ".git")))
            return null;

        var result = await RunGitCommandAsync(path, "remote get-url origin").ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return null;

        return result.Output.Trim();
    }

    public async Task<string> GetDiffSummaryAsync(string path, string fromBranch, string toBranch)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(fromBranch);
        ArgumentNullException.ThrowIfNull(toBranch);

        var result = await RunGitCommandAsync(path, $"diff --stat {fromBranch}..{toBranch}").ConfigureAwait(false);
        return result.Output;
    }

    public async Task<IReadOnlyList<GitFileChange>> GetChangedFilesAsync(string path, string fromBranch, string toBranch)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(fromBranch);
        ArgumentNullException.ThrowIfNull(toBranch);

        // Raw NUL-delimited, rename-free output: a rename is reported as delete(old)+add(new) so neither path can hide,
        // and non-ASCII/space paths are never quoted or re-encoded.
        const string safe = "diff --no-ext-diff --no-textconv --no-renames -z";
        var range = $"{QuoteRefArgument(fromBranch, nameof(fromBranch))} {QuoteRefArgument(toBranch, nameof(toBranch))}";
        var nameStatusResult = await RunGitCommandAsync(path, $"{safe} --name-status {range} --").ConfigureAwait(false);
        if (nameStatusResult.ExitCode != 0 || string.IsNullOrWhiteSpace(nameStatusResult.Output))
            return Array.Empty<GitFileChange>();

        var statsByPath = new Dictionary<string, (int Additions, int Deletions)>(StringComparer.Ordinal);
        var numstatResult = await RunGitCommandAsync(path, $"{safe} --numstat {range} --").ConfigureAwait(false);
        if (numstatResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(numstatResult.Output))
        {
            foreach (var statRaw in numstatResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var statParts = statRaw.TrimStart('\n').Split('\t', 3);
                if (statParts.Length < 3 || string.IsNullOrEmpty(statParts[2])) continue;
                var additions = int.TryParse(statParts[0], out var add) ? add : 0;
                var deletions = int.TryParse(statParts[1], out var del) ? del : 0;
                statsByPath[statParts[2]] = (additions, deletions);
            }
        }

        // -z name-status: "<status> NUL <path> NUL" pairs.
        var changes = new List<GitFileChange>();
        var tokens = nameStatusResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < tokens.Length; i += 2)
        {
            var statusCode = tokens[i].Trim();
            var filePath = tokens[i + 1];
            if (statusCode.Length == 0 || string.IsNullOrEmpty(filePath)) continue;
            var stat = statsByPath.TryGetValue(filePath, out var s) ? s : (0, 0);
            changes.Add(new GitFileChange(filePath, statusCode[0].ToString(), stat.Item1, stat.Item2));
        }
        return changes.AsReadOnly();
    }

    public async Task<IReadOnlyList<GitFileChange>> GetWorkingTreeChangedFilesAsync(string path, string baseRef)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(baseRef);

        var changes = new List<GitFileChange>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        ValidateSafeRefArgument(baseRef, nameof(baseRef));

        // 1. Tracked changes (staged + unstaged) against the base ref. Raw NUL-delimited and rename-free: a rename is
        //    reported as delete(old)+add(new), so an out-of-scope source cannot hide behind an in-scope destination.
        var nameStatusResult = await RunSafeReadOnlyGitCommandAsync(
            path,
            "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "-z", "--name-status", baseRef, "--").ConfigureAwait(false);
        if (nameStatusResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(nameStatusResult.Output))
        {
            var statsByPath = new Dictionary<string, (int Additions, int Deletions)>(StringComparer.Ordinal);
            var numstatResult = await RunSafeReadOnlyGitCommandAsync(
                path,
                "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "-z", "--numstat", baseRef, "--").ConfigureAwait(false);
            if (numstatResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(numstatResult.Output))
            {
                foreach (var statRaw in numstatResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    var statParts = statRaw.TrimStart('\n').Split('\t', 3);
                    if (statParts.Length < 3 || string.IsNullOrEmpty(statParts[2])) continue;
                    var additions = int.TryParse(statParts[0], out var add) ? add : 0;
                    var deletions = int.TryParse(statParts[1], out var del) ? del : 0;
                    statsByPath[statParts[2]] = (additions, deletions);
                }
            }

            var tokens = nameStatusResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i + 1 < tokens.Length; i += 2)
            {
                var statusCode = tokens[i].Trim();
                var filePath = tokens[i + 1];
                if (statusCode.Length == 0 || string.IsNullOrEmpty(filePath)) continue;
                var stat = statsByPath.TryGetValue(filePath, out var s) ? s : (0, 0);
                changes.Add(new GitFileChange(filePath, statusCode[0].ToString(), stat.Item1, stat.Item2));
                seenPaths.Add(filePath);
            }
        }

        // 2. Untracked new files (invisible to `git diff`, so they would otherwise escape the security audit).
        var untrackedResult = await RunSafeReadOnlyGitCommandAsync(path, "ls-files", "-z", "--others", "--exclude-standard").ConfigureAwait(false);
        if (untrackedResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(untrackedResult.Output))
        {
            foreach (var filePath in untrackedResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrEmpty(filePath) || seenPaths.Contains(filePath)) continue;
                changes.Add(new GitFileChange(filePath, "A", 0, 0));
                seenPaths.Add(filePath);
            }
        }

        return changes.AsReadOnly();
    }

    public async Task<string> GetWorkingTreeDiffAsync(string path, string baseRef)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(baseRef);

        ValidateSafeRefArgument(baseRef, nameof(baseRef));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var builder = new StringBuilder();
        var diffResult = await RunSafeReadOnlyGitCommandAsync(
            path,
            "diff", "--no-ext-diff", "--no-textconv", "--find-renames", "--binary", baseRef, "--").ConfigureAwait(false);
        if (diffResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(diffResult.Output))
        {
            builder.AppendLine(diffResult.Output.TrimEnd());
        }

        var untrackedResult = await RunSafeReadOnlyGitCommandAsync(path, "ls-files", "-z", "--others", "--exclude-standard").ConfigureAwait(false);
        if (untrackedResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(untrackedResult.Output))
        {
            foreach (var rawPath in untrackedResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = rawPath.Replace('\\', '/');
                if (IsInternalMetadataRelativePath(normalized))
                {
                    continue;
                }

                builder.AppendLine();
                builder.AppendLine($"diff --git a/{normalized} b/{normalized}");
                builder.AppendLine("new file mode 100644");
                builder.AppendLine("--- /dev/null");
                builder.AppendLine($"+++ b/{normalized}");

                if (!TryResolveSafeUntrackedFilePath(fullRoot, normalized, out var fullPath, out var skipReason))
                {
                    builder.AppendLine("@@");
                    builder.AppendLine($"+[{skipReason}]");
                    continue;
                }

                try
                {
                    var info = new FileInfo(fullPath);
                    if (!info.Exists)
                    {
                        builder.AppendLine("@@");
                        builder.AppendLine("+[untracked file disappeared before diff capture]");
                        continue;
                    }

                    if (info.Length > 64 * 1024)
                    {
                        builder.AppendLine("@@");
                        builder.AppendLine($"+[untracked file omitted: {info.Length} bytes exceeds inline capture limit]");
                        continue;
                    }

                    var bytes = await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false);
                    if (bytes.Contains((byte)0))
                    {
                        builder.AppendLine("@@");
                        builder.AppendLine($"+[binary untracked file omitted: {info.Length} bytes]");
                        continue;
                    }

                    var text = Encoding.UTF8.GetString(bytes);
                    builder.AppendLine("@@");
                    foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
                    {
                        builder.Append('+');
                        builder.AppendLine(line);
                    }
                }
                catch (IOException ex)
                {
                    builder.AppendLine("@@");
                    builder.AppendLine($"+[untracked file could not be read: {ex.Message}]");
                }
                catch (UnauthorizedAccessException ex)
                {
                    builder.AppendLine("@@");
                    builder.AppendLine($"+[untracked file could not be read: {ex.Message}]");
                }
            }
        }

        return builder.ToString();
    }

    private static bool IsInternalMetadataRelativePath(string normalizedRelativePath)
    {
        foreach (var seg in normalizedRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(seg, ".git", StringComparison.OrdinalIgnoreCase)
                || string.Equals(seg, ".codexflow", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveSafeUntrackedFilePath(
        string fullRoot,
        string normalizedRelativePath,
        out string fullPath,
        out string skipReason)
    {
        fullPath = string.Empty;
        skipReason = string.Empty;

        var segments = normalizedRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static s => s is "." or ".."))
        {
            skipReason = "untracked file path skipped: unsafe relative segments";
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(segments)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, comparison))
        {
            skipReason = "untracked file path skipped: escapes repository root";
            return false;
        }

        var current = fullRoot;
        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            if (i < segments.Length - 1)
            {
                var di = new DirectoryInfo(current);
                if (di.Exists && ((di.Attributes & FileAttributes.ReparsePoint) != 0 || di.LinkTarget is not null))
                {
                    skipReason = "untracked symlink directory omitted";
                    return false;
                }
            }
            else
            {
                var fi = new FileInfo(current);
                if (fi.Exists && ((fi.Attributes & FileAttributes.ReparsePoint) != 0 || fi.LinkTarget is not null))
                {
                    skipReason = "symlink untracked file omitted";
                    return false;
                }

                var di = new DirectoryInfo(current);
                if (di.Exists && ((di.Attributes & FileAttributes.ReparsePoint) != 0 || di.LinkTarget is not null))
                {
                    skipReason = "symlink untracked path omitted";
                    return false;
                }
            }
        }

        fullPath = candidate;
        return true;
    }

    public async Task<string?> GetFileAtRefAsync(string path, string relativePath, string gitRef = "HEAD~1")
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(gitRef);

        // normalize path separators for git
        var gitPath = relativePath.Replace('\\', '/');
        var result = await RunGitCommandAsync(path, $"show {gitRef}:{gitPath}").ConfigureAwait(false);
        if (result.ExitCode != 0) return null;
        return result.Output;
    }

    public async Task<bool> ApplyPatchAsync(string path, string patchContent)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(patchContent);

        var tempPatchFile = Path.Combine(Path.GetTempPath(), $"patch_{Guid.NewGuid()}.diff");
        try
        {
            var normalizedPatch = patchContent.EndsWith('\n')
                ? patchContent
                : patchContent + "\n";
            await File.WriteAllTextAsync(tempPatchFile, normalizedPatch).ConfigureAwait(false);
            var quotedPatchFile = $"\"{tempPatchFile}\"";
            var checkResult = await RunGitCommandAsync(path, $"apply --check --recount --whitespace=nowarn {quotedPatchFile}").ConfigureAwait(false);
            if (checkResult.ExitCode != 0)
            {
                StructuredLog.Warning(_logger, "Git apply --check failed: {Error}", checkResult.Error);
                return false;
            }

            // 使用 --3way 可以在有微小偏移时自动合并。
            var result = await RunGitCommandAsync(path, $"apply --3way --recount --whitespace=nowarn {quotedPatchFile}").ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                StructuredLog.Warning(_logger, "Git apply failed: {Error}", result.Error);

                if ((result.Error ?? string.Empty).Contains("repository lacks the necessary blob to perform 3-way merge", StringComparison.OrdinalIgnoreCase))
                {
                    StructuredLog.Warning(_logger, "Git apply --3way lacked blob metadata. Falling back to direct application.");
                    var directResult = await RunGitCommandAsync(path, $"apply --recount --whitespace=nowarn {quotedPatchFile}").ConfigureAwait(false);
                    if (directResult.ExitCode != 0)
                    {
                        StructuredLog.Warning(_logger, "Git apply direct fallback failed: {Error}", directResult.Error);
                    }

                    return directResult.ExitCode == 0;
                }
            }

            return result.ExitCode == 0;
        }
        finally
        {
            if (File.Exists(tempPatchFile)) File.Delete(tempPatchFile);
        }
    }

    public async Task<bool> PushAsync(string path, string branchName)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(branchName);

        var remoteCheck = await RunGitCommandAsync(path, "remote get-url origin").ConfigureAwait(false);
        if (remoteCheck.ExitCode != 0 || string.IsNullOrWhiteSpace(remoteCheck.Output))
        {
            StructuredLog.Information(_logger, "Skip push: repository has no remote 'origin'.");
            return true;
        }

        var targetBranch = (branchName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(targetBranch))
        {
            StructuredLog.Warning(_logger, "Skip push: target branch name is empty.");
            return true;
        }

        // 将当前 HEAD 显式保存到目标分支（创建或更新），确保“先保存为分支，再推送”
        var saveBranchResult = await RunGitCommandAsync(path, $"branch -f {targetBranch} HEAD").ConfigureAwait(false);
        if (saveBranchResult.ExitCode != 0)
        {
            StructuredLog.Warning(_logger, "Failed to save HEAD to branch {Branch}: {Error}", targetBranch, saveBranchResult.Error);
            return false;
        }

        StructuredLog.Information(_logger, "Pushing branch {Branch} to origin...", targetBranch);
        var result = await RunGitCommandAsync(path, $"push origin {targetBranch}").ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            StructuredLog.Warning(_logger, "Git push failed: {Error}", result.Error);
        }
        return result.ExitCode == 0;
    }

    /// <summary>
    /// 递归将目录下所有文件属性设为 Normal，以便删除 .git 中的只读文件。
    /// </summary>
    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories())
        {
            SetAttributesNormal(subDir);
        }
        foreach (var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }
    }
}
