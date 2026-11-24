// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// Daily-mode general shell execution tool (PowerShell on Windows, Bash/sh on Unix and containers).
/// Enforces DailyChat origin, single-use Ask approval under Ask/AutoInWorkspace (or auto-allow under FullAuto),
/// fixed system interpreter resolution (no PATH fallback), versioned UTF-8 wrapper verification, and controlled
/// output export under <c>outputs/</c>.
/// </summary>
public sealed class RunShellTool(
    ILogger<RunShellTool> logger,
    ToolIsolationGate? isolationGate = null) : ICodexTool
{
    private const int DefaultTimeoutSeconds = 60;
    private const int MaxTimeoutSeconds = 600;
    private const int MaxScriptBytes = 256 * 1024;
    private const int PreviewMaxChars = 512;
    private const string WrapperVersion = "cf-ps-wrapper-v1";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly HashSet<string> SupportedShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "pwsh", "powershell", "bash", "sh"
    };

    private readonly ToolIsolationGate _gate = isolationGate ?? ToolIsolationGate.Unconfigured;

    public string Name => "run_shell";

    public string Description =>
        "在当前日常工作区中执行 Shell 脚本（Windows 支持 PowerShell 管道与系统工具，Linux/macOS/容器支持 Bash/sh）。默认逐次展示完整脚本并请求单次审批，当用户显式开启 FullAuto 时将自动放行。\n" +
        "重要边界说明：\n" +
        "  1. Local 模式下 HOME/USERPROFILE 会重定向到临时目录；只有写入工作区 outputs/ 目录内的产物会被保留并导出，写入 ~ 或其他非工作区路径的文件会随会话清理被丢弃或不受管理。\n" +
        "  2. Docker 模式下运行在离线容器（--network none），无法访问宿主机文件或桌面办公软件。\n" +
        "参数（JSON object）：\n" +
        "  - script (string, 必填): 要执行的完整脚本内容\n" +
        "  - shell (string, 可选): auto | pwsh | powershell | bash | sh，默认 auto\n" +
        "  - cwd (string, 可选): 相对于工作区的工作目录，默认 \".\"\n" +
        "  - timeout_seconds (int, 可选): 超时秒数，默认 60，范围 1-600";

    public ToolCategory Category => ToolCategory.Forge;

    public ToolCapability Capability => ToolCapability.ExecuteUntrustedCode;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: true,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 16_384);

    public IReadOnlyList<int> AllowedStages => [1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        var script = arguments.GetValueOrDefault("script")?.ToString();
        if (string.IsNullOrWhiteSpace(script))
        {
            return CodexToolResult.Error("Missing or empty 'script' parameter.");
        }

        var scriptBytes = Utf8NoBom.GetBytes(script);
        if (scriptBytes.Length > MaxScriptBytes)
        {
            return CodexToolResult.Error($"Script size ({scriptBytes.Length} bytes) exceeds maximum allowed limit ({MaxScriptBytes} bytes).");
        }

        var shellRequested = (arguments.GetValueOrDefault("shell")?.ToString() ?? "auto").Trim().ToLowerInvariant();
        if (!SupportedShells.Contains(shellRequested))
        {
            return CodexToolResult.Error($"Unsupported shell '{shellRequested}'. Allowed: auto, pwsh, powershell, bash, sh.");
        }

        var cwd = arguments.GetValueOrDefault("cwd")?.ToString();
        string? guestCwd = null;
        if (!string.IsNullOrWhiteSpace(cwd) && cwd is not "." and not "./")
        {
            if (!WorkspacePath.TryNormalizeStrict(cwd, out var normalizedCwd, out var cwdReason))
            {
                return ToolIsolationGate.Refuse(Name, ExecutionDecision.Deny(ExecutionReasonCodes.InvalidRequest, $"cwd rejected: {cwdReason}"));
            }

            guestCwd = normalizedCwd;
        }

        var timeoutSeconds = DefaultTimeoutSeconds;
        if (arguments.GetValueOrDefault("timeout_seconds") is { } timeoutRaw &&
            int.TryParse(timeoutRaw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTimeout))
        {
            timeoutSeconds = Math.Clamp(parsedTimeout, 1, MaxTimeoutSeconds);
        }

        var refusal = _gate.TryAcquire(Name, Capability, out var context, out var backend);
        if (refusal is not null)
        {
            return refusal;
        }

        var scriptSha256 = Convert.ToHexString(SHA256.HashData(scriptBytes)).ToLowerInvariant();
        var preview = BuildPreview(script);
        var activeBackend = backend.ActiveBackend;
        var resolvedShell = ResolveInterpreter(activeBackend, shellRequested);
        if (!resolvedShell.Success)
        {
            return ToolIsolationGate.Refuse(Name, ExecutionDecision.Unavailable(ExecutionReasonCodes.UnsupportedIsolation, resolvedShell.Error!));
        }

        string? stagedScriptPath = null;
        try
        {
            IReadOnlyList<string> fullArgv;
            string? stdIn = null;
            IReadOnlyDictionary<string, string>? envOverrides = null;
            string? wrapperSha256 = null;
            Func<bool>? verifyArtifacts = null;

            if (resolvedShell.IsPowerShell)
            {
                var scriptDir = Path.Combine(_gate.StagingRoot, "shell-scripts", context.SessionId);
                Directory.CreateDirectory(scriptDir);
                stagedScriptPath = Path.Combine(scriptDir, $"{Guid.NewGuid():N}.ps1");
                await File.WriteAllBytesAsync(stagedScriptPath, scriptBytes, ct).ConfigureAwait(false);

                var (wrapperText, encodedWrapper) = BuildPowerShellWrapper(stagedScriptPath, scriptSha256);
                wrapperSha256 = ExecutionRequest.Sha256Text($"{WrapperVersion}:{wrapperText}");
                fullArgv = [resolvedShell.Executable, "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedWrapper];
                verifyArtifacts = () => File.Exists(stagedScriptPath) &&
                                        string.Equals(ExecutionRequest.Sha256File(stagedScriptPath), scriptSha256, StringComparison.Ordinal);
            }
            else
            {
                wrapperSha256 = ExecutionRequest.Sha256Text($"cf-posix-stdin-v1:{resolvedShell.ShellKind}");
                fullArgv = string.Equals(resolvedShell.ShellKind, "bash", StringComparison.Ordinal)
                    ? [resolvedShell.Executable, "--noprofile", "--norc", "-s"]
                    : [resolvedShell.Executable, "-s"];
                stdIn = script;
                envOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BASH_ENV"] = string.Empty,
                    ["ENV"] = string.Empty
                };
            }

            var options = _gate.Options;
            var image = options.CommandImage;
            var backendNotice = activeBackend == ExecutionBackend.Local
                ? "本地日常 Shell：以当前系统用户权限直接执行脚本（无容器隔离；只有工作区 outputs/ 下的产物会被保留导出，写入 ~ 或其他外部路径的文件不受管理）。"
                : $"容器隔离 Shell（{image}）：运行在离线 Docker 容器（--network none），无法访问宿主机文件或桌面软件。";

            var (authRefusal, authorization) = await _gate.AuthorizeBeforeExecutionAsync(
                Name,
                context,
                backend,
                fullArgv,
                cwd: guestCwd,
                stdIn: stdIn,
                scriptSha256: scriptSha256,
                environmentOverrides: envOverrides,
                verifyStagedArtifactsUnchanged: verifyArtifacts,
                ct: ct,
                preResolvedTrustedExecutable: true,
                wrapperSha256: wrapperSha256,
                timeoutSeconds: timeoutSeconds,
                imageDigest: activeBackend == ExecutionBackend.Docker ? image : null,
                shellKind: resolvedShell.ShellKind,
                scriptText: script,
                scriptPreview: preview,
                scriptLength: script.Length,
                backendNotice: backendNotice,
                allowBackendSwitchRebuild: false).ConfigureAwait(false);

            if (authRefusal is not null)
            {
                return authRefusal;
            }

            var open = await backend.OpenPinnedBackendSessionAsync(
                authorization.ApprovedBackend,
                context,
                new IsolatedSessionOptions(image, GuestGit: GuestGitMode.None),
                ct).ConfigureAwait(false);
            if (!open.Decision.IsAllowed || open.Session is null)
            {
                return ToolIsolationGate.Refuse(Name, open.Decision);
            }

            await using var session = open.Session;
            if (session.Layout.Backend != authorization.ApprovedBackend)
            {
                return ToolIsolationGate.Refuse(Name, ExecutionDecision.Deny(
                    ExecutionReasonCodes.ApprovalInvalidated,
                    "Opened session backend did not match approved backend."));
            }

            var launchArgv = new string[1 + authorization.EffectiveArgv.Count];
            launchArgv[0] = authorization.EffectiveExecutable;
            for (var i = 0; i < authorization.EffectiveArgv.Count; i++)
            {
                launchArgv[i + 1] = authorization.EffectiveArgv[i];
            }

            var run = await session.RunAsync(
                new IsolatedCommandRequest(
                    launchArgv,
                    guestCwd,
                    StdIn: stdIn,
                    Timeout: TimeSpan.FromSeconds(timeoutSeconds),
                    Env: envOverrides,
                    PreResolvedExecutable: activeBackend == ExecutionBackend.Local,
                    ForceUtf8Encoding: true,
                    IsolateProcessGroup: true),
                ct).ConfigureAwait(false);

            if (run.Status == IsolatedCommandStatus.TimedOut)
            {
                return CodexToolResult.Error($"run_shell timed out after {timeoutSeconds}s.\n{FormatOutput(run)}");
            }

            if (run.Status == IsolatedCommandStatus.Canceled)
            {
                return CodexToolResult.Error($"run_shell was cancelled.\n{FormatOutput(run)}");
            }

            if (run.Status is IsolatedCommandStatus.Denied or IsolatedCommandStatus.Unavailable)
            {
                return ToolIsolationGate.Refuse(Name, run.Decision);
            }

            var export = await session.ExportScopedChangesAsync(ct).ConfigureAwait(false);
            if (!export.Decision.IsAllowed)
            {
                var rejText = string.Join("\n", export.Rejected.Take(20).Select(r => $"  rejected {r.Path}: [{r.ReasonCode}] {r.Message}"));
                return CodexToolResult.Error(
                    $"run_shell export rejected ([{export.Decision.ReasonCode}] {export.Decision.Message}).\n{rejText}\n{FormatOutput(run)}");
            }

            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"shell: {resolvedShell.ShellKind} ({authorization.ApprovedBackend}) | exit_code: {run.ExitCode ?? -1}");
            if (run.OrphanedProcessesCleaned > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"[process_cleanup] 检测到并清理了 {run.OrphanedProcessesCleaned} 个退出后残留进程。");
            }

            if (export.Applied.Count > 0)
            {
                sb.AppendLine("[exported_outputs]");
                foreach (var change in export.Applied)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  - {change.Kind} {change.Path} ({change.Size} bytes)");
                }
            }

            if (activeBackend == ExecutionBackend.Local)
            {
                sb.AppendLine("[note] Local 模式下 HOME/USERPROFILE 已重定向；工作区 outputs/ 之外的非受管写入不会被导出或保留。");
            }

            sb.AppendLine(FormatOutput(run));

            var text = sb.ToString().TrimEnd();
            var metadata = new Dictionary<string, object?>
            {
                ["shell"] = resolvedShell.ShellKind,
                ["exitCode"] = run.ExitCode,
                ["exportedCount"] = export.Applied.Count
            };
            return run.ExitCode == 0
                ? CodexToolResult.Succeeded(text, metadata)
                : CodexToolResult.Error(text, metadata);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(stagedScriptPath) && File.Exists(stagedScriptPath))
            {
                try
                {
                    File.Delete(stagedScriptPath);
                }
                catch (IOException ex)
                {
                    logger.LogDebug(ex, "Failed to remove temporary staged script file.");
                }
                catch (UnauthorizedAccessException ex)
                {
                    logger.LogDebug(ex, "Failed to remove temporary staged script file.");
                }
            }
        }
    }

    internal static (bool Success, string ShellKind, string Executable, bool IsPowerShell, string? Error) ResolveInterpreter(
        ExecutionBackend backend,
        string requestedShell)
    {
        if (backend == ExecutionBackend.Docker)
        {
            return requestedShell switch
            {
                "auto" or "bash" => (true, "bash", "/bin/bash", false, null),
                "sh" => (true, "sh", "/bin/sh", false, null),
                _ => (false, requestedShell, string.Empty, false, $"Shell '{requestedShell}' is not available in the Linux Docker verification container.")
            };
        }

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            string? FindPwsh()
            {
                var candidates = new[]
                {
                    Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"),
                    Path.Combine(programFiles, "PowerShell", "7-preview", "pwsh.exe"),
                    Path.Combine(programFilesX86, "PowerShell", "7", "pwsh.exe")
                };
                return candidates.FirstOrDefault(File.Exists);
            }

            string? FindWindowsPowerShell()
            {
                var p = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
                return File.Exists(p) ? p : null;
            }

            string? FindWindowsBash()
            {
                var candidates = new[]
                {
                    Path.Combine(programFiles, "Git", "bin", "bash.exe"),
                    Path.Combine(programFilesX86, "Git", "bin", "bash.exe")
                };
                return candidates.FirstOrDefault(File.Exists);
            }

            if (requestedShell == "auto")
            {
                if (FindPwsh() is { } pwsh) return (true, "pwsh", pwsh, true, null);
                if (FindWindowsPowerShell() is { } ps5) return (true, "powershell", ps5, true, null);
                return (false, "auto", string.Empty, false, "Neither pwsh.exe nor powershell.exe was found in trusted system directories.");
            }

            if (requestedShell == "pwsh")
            {
                return FindPwsh() is { } pwsh
                    ? (true, "pwsh", pwsh, true, null)
                    : (false, "pwsh", string.Empty, false, "pwsh.exe was not found in Program Files trusted paths.");
            }

            if (requestedShell == "powershell")
            {
                return FindWindowsPowerShell() is { } ps5
                    ? (true, "powershell", ps5, true, null)
                    : (false, "powershell", string.Empty, false, "powershell.exe was not found in System32 trusted path.");
            }

            if (requestedShell is "bash" or "sh")
            {
                return FindWindowsBash() is { } bash
                    ? (true, requestedShell, bash, false, null)
                    : (false, requestedShell, string.Empty, false, "bash.exe was not found in trusted Git install directories.");
            }

            return (false, requestedShell, string.Empty, false, $"Unsupported shell '{requestedShell}' on Windows.");
        }

        // POSIX (Linux / macOS): resolve from fixed system directories only (no PATH fallback).
        static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

        if (requestedShell == "auto")
        {
            if (FirstExisting("/bin/bash", "/usr/bin/bash", "/opt/homebrew/bin/bash", "/usr/local/bin/bash") is { } bash)
                return (true, "bash", bash, false, null);
            if (FirstExisting("/bin/sh", "/usr/bin/sh") is { } sh)
                return (true, "sh", sh, false, null);
            return (false, "auto", string.Empty, false, "Neither bash nor sh was found in fixed system directories.");
        }

        return requestedShell switch
        {
            "bash" => FirstExisting("/bin/bash", "/usr/bin/bash", "/opt/homebrew/bin/bash", "/usr/local/bin/bash") is { } b
                ? (true, "bash", b, false, null)
                : (false, "bash", string.Empty, false, "bash was not found in fixed system directories."),
            "sh" => FirstExisting("/bin/sh", "/usr/bin/sh") is { } s
                ? (true, "sh", s, false, null)
                : (false, "sh", string.Empty, false, "sh was not found in fixed system directories."),
            "pwsh" => FirstExisting("/usr/bin/pwsh", "/usr/local/bin/pwsh", "/opt/homebrew/bin/pwsh") is { } p
                ? (true, "pwsh", p, true, null)
                : (false, "pwsh", string.Empty, false, "pwsh was not found in fixed system directories."),
            _ => (false, requestedShell, string.Empty, false, $"Shell '{requestedShell}' is not available on this OS.")
        };
    }

    internal static (string WrapperText, string EncodedCommand) BuildPowerShellWrapper(string stagedScriptPath, string expectedSha256)
    {
        var escapedPath = stagedScriptPath.Replace("'", "''", StringComparison.Ordinal);
        var wrapperText =
            "$ErrorActionPreference = 'Stop'\n" +
            "$utf8 = [System.Text.UTF8Encoding]::new($false)\n" +
            "[Console]::InputEncoding = $utf8\n" +
            "[Console]::OutputEncoding = $utf8\n" +
            "$OutputEncoding = $utf8\n" +
            $"$p = '{escapedPath}'\n" +
            $"$expected = '{expectedSha256}'\n" +
            "$bytes = [System.IO.File]::ReadAllBytes($p)\n" +
            "$sha = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()\n" +
            "if ($sha -ne $expected) { throw 'Approved script hash mismatch.' }\n" +
            "$text = $utf8.GetString($bytes)\n" +
            "& ([scriptblock]::Create($text))\n";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapperText));
        return (wrapperText, encoded);
    }

    private static string BuildPreview(string script)
        => script.Length <= PreviewMaxChars
            ? script
            : script[..PreviewMaxChars] + "…";

    private static string FormatOutput(IsolatedCommandResult result)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(result.Stdout))
        {
            sb.AppendLine("--- stdout ---");
            sb.AppendLine(result.Stdout.TrimEnd());
        }
        if (!string.IsNullOrEmpty(result.Stderr))
        {
            sb.AppendLine("--- stderr ---");
            sb.AppendLine(result.Stderr.TrimEnd());
        }
        if (result.Truncated)
        {
            sb.AppendLine("[output_truncated] 输出超过上限已截断。");
        }
        return sb.ToString().TrimEnd();
    }
}
