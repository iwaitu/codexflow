// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

internal sealed record RunCommandCompletionMetadata(
    string CommandTaskId,
    int? ExitCode,
    string Status,
    string Stdout,
    string Stderr);

/// <summary>
/// 在工作区执行白名单命令（替代 MCP run_command）。
/// 仅允许预定义的安全命令。
/// </summary>
public sealed record CommandExecutionPolicy(
    string Name,
    IReadOnlySet<string>? AllowedCommands = null,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? DeniedSubcommands = null,
    int MaxTimeoutSeconds = 3600,
    bool AllowBackground = true)
{
    public static CommandExecutionPolicy Default { get; } = new("default");

    public static CommandExecutionPolicy VerifyWorker { get; } = new(
        "verify-worker",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dotnet", "npm", "npx", "node", "python", "python3",
            "cargo", "go", "make", "cmake", "gradle", "mvn",
            "git", "ls", "dir", "cat", "head", "tail", "find", "grep", "rg", "wc",
            "echo", "pwd", "which", "env"
        },
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["git"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "add", "apply", "bisect", "branch", "checkout", "cherry-pick", "clean",
                "commit", "merge", "mv", "pull", "push", "rebase", "reset", "restore",
                "revert", "rm", "stash", "switch", "tag"
            },
            ["npm"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "adduser", "audit", "ci", "dedupe", "install", "link", "login", "logout",
                "publish", "rebuild", "star", "unpublish", "update", "version"
            },
            ["npx"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "npm", "yarn", "pnpm"
            },
            ["dotnet"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "add", "new", "nuget", "pack", "publish", "remove", "restore", "tool", "workload"
            }
        },
        MaxTimeoutSeconds: 300,
        AllowBackground: false);
}

public class RunCommandTool(
    ILogger<RunCommandTool> logger,
    CommandExecutionPolicy? policy = null,
    ToolIsolationGate? isolationGate = null) : ICodexTool
{
    private readonly CommandExecutionPolicy _policy = policy ?? CommandExecutionPolicy.Default;
    private readonly ToolIsolationGate _gate = isolationGate ?? ToolIsolationGate.Unconfigured;

    public string Name => "exec_cmd";
    public string Description => "在工作区中执行白名单命令。支持 dotnet/npm/git/python/grep/ls 等常用命令。\n" +
        "参数（JSON object）：\n" +
        "  - command (string[] 或 string, 必填): 命令及参数，推荐使用数组格式如 [\"dotnet\",\"build\"]，也可用字符串 \"dotnet build\"\n" +
        "  - cwd (string, 可选): 相对于工作区的工作目录，默认为工作区根目录\n" +
        "  - background (bool, 可选): true 时后台运行并立即返回 command_task_id，可用 task_output 读取增量输出；受 worker 策略限制\n" +
        "  - timeout_seconds (int, 可选): 超时秒数，默认 120，范围 1-3600；受 worker 策略上限限制\n" +
        "返回：同步模式返回 stdout/stderr/exit code 并包含 command_task_id；后台模式返回 command_task_id。\n" +
        "白名单：dotnet, npm, npx, node, python, python3, git, make, ls, cat, find, grep, rg, mkdir, cp, mv, echo, pwd, which\n" +
        "容器镜像由命令的第一个词决定：python/python3、node/npm/npx 在各自的镜像里运行，里面没有 dotnet。不要在 python 或 node 里启动 dotnet，要运行 dotnet 就把它直接作为命令。\n" +
        "调用示例：\n" +
        "  exec_cmd({\"command\":[\"dotnet\",\"build\",\"CodexFlow.Core/CodexFlow.Core.csproj\"]})\n" +
        "  exec_cmd({\"command\":[\"dotnet\",\"test\"],\"background\":true})\n" +
        "  exec_cmd({\"command\":[\"dotnet\",\"test\",\"--filter\",\"FullyQualifiedName~MyTests\"]})\n" +
        "  exec_cmd({\"command\":[\"grep\",\"-rn\",\"IFileRepository\",\"src/\"],\"cwd\":\".\"})" +
        (_policy.Name.Equals(CommandExecutionPolicy.Default.Name, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $"\n当前命令策略：{_policy.Name}；最大超时 {_policy.MaxTimeoutSeconds}s；后台执行：{(_policy.AllowBackground ? "允许" : "禁止")}。");
    public ToolCategory Category => ToolCategory.Forge;
    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: true,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 16_384);
    public IReadOnlyList<int> AllowedStages => [3, 4];

    private const int DefaultTimeoutSeconds = 120;

    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "npm", "npx", "node", "python", "python3", "pip", "pip3",
        "cargo", "go", "git", "make", "cmake", "gradle", "mvn",
        "ls", "dir", "cat", "head", "tail", "find", "grep", "rg", "wc",
        "mkdir", "cp", "mv", "echo", "pwd", "which", "env"
    };

    private const string EnvironmentStream = "environment";

    public ToolCapability Capability => ToolCapability.ExecuteUntrustedCode;

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        // workspace_path / project_root / session_id in the arguments are model-controllable and deliberately ignored:
        // the workspace, scope and identity come only from the host-bound trusted context.
        var cwd = arguments.GetValueOrDefault("cwd")?.ToString();
        var background = arguments.GetValueOrDefault("background")?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
        var timeoutSeconds = DefaultTimeoutSeconds;
        if (arguments.GetValueOrDefault("timeout_seconds") is { } timeoutRaw &&
            int.TryParse(timeoutRaw.ToString(), out var parsedTimeout))
        {
            timeoutSeconds = Math.Clamp(parsedTimeout, 1, 3600);
        }
        timeoutSeconds = Math.Min(timeoutSeconds, Math.Clamp(_policy.MaxTimeoutSeconds, 1, 3600));

        var commandParts = ParseCommandArray(arguments.GetValueOrDefault("command"));
        if (commandParts is null || commandParts.Count == 0)
            return CodexToolResult.Error("Missing or invalid command parameter. Expected a string array like [\"dotnet\", \"build\"].");

        var executable = commandParts[0];
        if (!AllowedCommands.Contains(executable))
            return CodexToolResult.Error($"Command \"{executable}\" is not in the whitelist. Allowed: {string.Join(", ", AllowedCommands)}");
        if (!ValidatePolicy(commandParts, background, out var policyError))
            return CodexToolResult.Error(policyError);

        var refusal = _gate.TryAcquire(Name, Capability, out var context, out var backend);
        if (refusal is not null)
            return refusal;

        string? guestCwd = null;
        if (!string.IsNullOrWhiteSpace(cwd) && cwd is not "." and not "./")
        {
            if (!WorkspacePath.TryNormalizeStrict(cwd, out var normalizedCwd, out var cwdReason))
                return ToolIsolationGate.Refuse(Name, ExecutionDecision.Deny(ExecutionReasonCodes.InvalidRequest, $"cwd rejected: {cwdReason}"));
            guestCwd = normalizedCwd;
        }

        var options = _gate.Options;
        var image = options.ExecutableImages.TryGetValue(executable, out var mapped) ? mapped : options.CommandImage;
        var argv = commandParts.ToArray();

        var (authRefusal, authorization) = await _gate.AuthorizeBeforeExecutionAsync(
            Name,
            context,
            backend,
            argv,
            cwd: guestCwd,
            environmentOverrides: backend.ActiveBackend == ExecutionBackend.Docker ? OfflineToolchainPreparation.EnvFor(executable) : null,
            resolveArgvForBackend: targetBackend => (
                argv,
                null,
                targetBackend == ExecutionBackend.Docker ? OfflineToolchainPreparation.EnvFor(executable) : null),
            ct: ct).ConfigureAwait(false);
        if (authRefusal is not null)
            return authRefusal;

        var effectiveArgv = new string[1 + authorization.EffectiveArgv.Count];
        effectiveArgv[0] = authorization.EffectiveExecutable;
        for (var i = 0; i < authorization.EffectiveArgv.Count; i++)
        {
            effectiveArgv[i + 1] = authorization.EffectiveArgv[i];
        }

        var displayCommand = string.Join(' ', argv.Select(QuoteIfNeeded));
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var gateway = _gate.Gateway;

        string commandTaskId = string.Empty;
        commandTaskId = CommandTaskRegistry.StartIsolated(
            displayCommand,
            guestCwd ?? ".",
            timeout,
            (sink, token) => RunInIsolationAsync(
                backend,
                authorization.ApprovedBackend,
                authorization.IsPreResolvedExecutable,
                context,
                image,
                executable,
                effectiveArgv,
                guestCwd,
                timeout,
                authorization.ForcedEnvironment,
                authorization.RequiresGuestGit,
                gateway,
                Name,
                commandTaskId,
                sink,
                token));

        if (background)
        {
            return CodexToolResult.Succeeded(
                $"COMMAND_TASK_STARTED\ncommand_task_id: {commandTaskId}\n$ {displayCommand}\nUse task_output({{\"job_id\":\"{commandTaskId}\"}}) to read stdout/stderr, and task_stop({{\"job_id\":\"{commandTaskId}\"}}) to stop it.",
                new
                {
                    CommandTaskId = commandTaskId,
                    Command = displayCommand,
                    WorkingDirectory = guestCwd ?? ".",
                    Background = true,
                    TimeoutSeconds = timeoutSeconds,
                    Isolated = true
                },
                summary: $"command task started: {commandTaskId}");
        }

        CommandTaskSnapshot? snapshot;
        try
        {
            snapshot = await CommandTaskRegistry.WaitForCompletionAsync(commandTaskId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation propagates, and the container is killed through the task runner.
            CommandTaskRegistry.Stop(commandTaskId, out _);
            throw;
        }

        if (snapshot == null)
        {
            return CodexToolResult.Error($"command task not found after start: {commandTaskId}");
        }

        var backendRefusal = snapshot.Events.FirstOrDefault(e => e.Stream.Equals("refusal", StringComparison.OrdinalIgnoreCase));
        if (backendRefusal is not null)
        {
            return CodexToolResult.Error(
                $"{Name} refused by the isolation backend: {backendRefusal.Text}\ncommand_task_id: {commandTaskId}",
                new { CommandTaskId = commandTaskId, ExitCode = (int?)null, Status = snapshot.Status.ToString(), Refusal = backendRefusal.Text });
        }

        var output = BuildStreamText(snapshot, "stdout");
        var errors = BuildStreamText(snapshot, "stderr");
        var system = BuildStreamText(snapshot, "system");

        const int maxChars = 8000;
        var originalOutputLength = output.Length;
        var originalErrorLength = errors.Length;
        if (output.Length > maxChars) output = output[..maxChars] + $"\n... (truncated, total {originalOutputLength} chars)";
        if (errors.Length > maxChars) errors = errors[..maxChars] + $"\n... (truncated, total {originalErrorLength} chars)";

        var result = new RunCommandCompletionMetadata(commandTaskId, snapshot.ExitCode, snapshot.Status.ToString(), output, errors);

        var environmentNote = BuildEnvironmentNote(executable, image, options.CommandImage) is { } note ? "\n" + note : string.Empty;
        if (BuildStreamText(snapshot, EnvironmentStream) is { Length: > 0 } prepared) environmentNote += "\n" + prepared;
        if (snapshot.Status != CommandTaskStatus.Completed || snapshot.ExitCode != 0)
        {
            return CodexToolResult.Succeeded(
                $"⚠️ 命令执行完成（status {snapshot.Status}, exit code {snapshot.ExitCode?.ToString() ?? "n/a"}）\ncommand_task_id: {commandTaskId}\n$ {displayCommand}\n[stdout]\n{output}\n[stderr]\n{errors}{environmentNote}",
                result,
                summary: $"command {commandTaskId}: status={snapshot.Status}, exit={snapshot.ExitCode?.ToString() ?? "n/a"}");
        }

        return CodexToolResult.Succeeded(
            $"✅ command_task_id: {commandTaskId}\n$ {displayCommand}\n{output}" + (string.IsNullOrWhiteSpace(errors) ? "" : $"\n[stderr]\n{errors}") +
            (system.Contains("exported:", StringComparison.Ordinal) ? $"\n[export]\n{system}" : "") + environmentNote,
            result,
            summary: $"command {commandTaskId}: exit=0");
    }

    /// <summary>
    /// The container image follows the command's first word: <c>python3</c>, <c>node</c>, <c>npm</c> run in their own images, which do not
    /// contain the default toolchain. Seen live: a worker started <c>dotnet</c> from inside <c>python3 -c</c>, got "No such file", concluded the
    /// environment had no dotnet and reported a finished upgrade as blocked. The result of such a command says where it ran, at the point the
    /// conclusion is drawn. Null for commands that ran in the default image.
    /// </summary>
    internal static string? BuildEnvironmentNote(string executable, string image, string commandImage)
        => BuildEnvironmentNote(executable, image, commandImage, ExecutionBackend.Docker);

    internal static string? BuildEnvironmentNote(string executable, string image, string commandImage, ExecutionBackend backend)
    {
        if (backend == ExecutionBackend.Local)
        {
            return "[environment] 本地执行模式：命令直接在本机运行（无网络隔离、无文件系统隔离），使用主机工具链与用户包缓存。";
        }

        return string.Equals(image, commandImage, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"[environment] 这条命令在 `{executable}` 专用的容器（{image}）里运行，里面只有 `{executable}` 自己的工具链，没有 dotnet 等其他工具；" +
              "在这里找不到某个工具，不代表环境里没有它。要运行 dotnet，请把它直接作为一条命令执行，例如 exec_cmd({\"command\":[\"dotnet\",\"restore\"]})，" +
              $"不要从 `{executable}` 里启动它。";
    }

    private static async Task<CommandTaskOutcome> RunInIsolationAsync(
        IIsolatedExecutionService backend,
        ExecutionBackend approvedBackend,
        bool isPreResolvedExecutable,
        TrustedExecutionContext context,
        string image,
        string originalCommand,
        string[] argv,
        string? cwd,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? forcedMinimalEnv,
        bool requiresGuestGit,
        CodexFlow.Core.Gateway.ICodexGateway? gateway,
        string toolName,
        string commandTaskId,
        CommandTaskSink sink,
        CancellationToken token)
    {
        var guestGitMode = requiresGuestGit ? GuestGitMode.Required : GuestGitMode.IfAvailable;
        var open = await backend.OpenPinnedBackendSessionAsync(approvedBackend, context, new IsolatedSessionOptions(image, GuestGit: guestGitMode), token).ConfigureAwait(false);
        if (!open.Decision.IsAllowed || open.Session is null)
        {
            // Nothing ran: the "refusal" event lets the tool report a FAILED result with the reason code instead of a
            // success-shaped "command finished" message.
            sink.Add("refusal", $"[{open.Decision.ReasonCode}] {open.Decision.Message}");
            return new CommandTaskOutcome(CommandTaskStatus.Failed, null);
        }

        await using var session = open.Session;
        if (session.Layout.Backend != approvedBackend)
        {
            sink.Add("refusal", $"[{ExecutionReasonCodes.ApprovalInvalidated}] Opened session backend '{session.Layout.Backend}' did not match approved backend '{approvedBackend}'.");
            return new CommandTaskOutcome(CommandTaskStatus.Failed, null);
        }

        IReadOnlyDictionary<string, string>? prepEnv = null;
        if (session.Layout.Backend == ExecutionBackend.Docker)
        {
            if (forcedMinimalEnv is null)
            {
                if (await OfflineToolchainPreparation.PrepareAsync(session, originalCommand, token).ConfigureAwait(false) is { } prepared)
                {
                    sink.Add(EnvironmentStream, prepared);
                }

                prepEnv = OfflineToolchainPreparation.EnvFor(originalCommand);
            }
        }
        else
        {
            sink.Add(EnvironmentStream, "local host execution (network=Host, filesystem=unrestricted)");
        }

        await using var outputThrottler = new CodexFlow.Core.Execution.Approval.ToolOutputEventThrottler(
            gateway,
            context.SessionId,
            toolName,
            commandTaskId);

        var run = await session.RunAsync(
            new IsolatedCommandRequest(
                argv,
                cwd,
                Timeout: timeout,
                Env: prepEnv,
                OnOutput: line =>
                {
                    sink.Add(line.Stream, line.Text);
                    outputThrottler.Append(line.Stream, line.Text);
                },
                TrustedMinimalEnv: forcedMinimalEnv,
                PreResolvedExecutable: isPreResolvedExecutable),
            token).ConfigureAwait(false);

        await outputThrottler.FlushAsync(token).ConfigureAwait(false);

        switch (run.Status)
        {
            case IsolatedCommandStatus.Completed:
            case IsolatedCommandStatus.Failed:
                break;
            case IsolatedCommandStatus.TimedOut:
                sink.Add("system", $"timed out after {(int)timeout.TotalSeconds}s");
                return new CommandTaskOutcome(CommandTaskStatus.TimedOut, null);
            case IsolatedCommandStatus.Canceled:
                return new CommandTaskOutcome(CommandTaskStatus.Stopped, null);
            default:
                sink.Add("refusal", $"[{run.Decision.ReasonCode}] {run.Decision.Message}");
                return new CommandTaskOutcome(CommandTaskStatus.Failed, run.ExitCode);
        }

        var export = await session.ExportScopedChangesAsync(token).ConfigureAwait(false);
        if (!export.Decision.IsAllowed)
        {
            sink.Add("stderr", $"[{export.Decision.ReasonCode}] {export.Decision.Message}");
            foreach (var r in export.Rejected.Take(20))
            {
                sink.Add("stderr", $"  rejected {r.Path}: [{r.ReasonCode}] {r.Message}");
            }

            sink.Add("stderr", "No source changes were applied to the workspace.");
            return new CommandTaskOutcome(CommandTaskStatus.Failed, run.ExitCode is 0 or null ? 1 : run.ExitCode);
        }

        foreach (var c in export.Applied)
        {
            sink.Add("system", $"exported: {c.Kind} {c.Path} {c.Sha256}");
        }

        return new CommandTaskOutcome(run.ExitCode == 0 ? CommandTaskStatus.Completed : CommandTaskStatus.Failed, run.ExitCode);
    }

    public RunCommandTool WithPolicy(CommandExecutionPolicy policy)
        => new(logger, policy, _gate);

    private bool ValidatePolicy(List<string> commandParts, bool background, out string error)
    {
        error = string.Empty;
        if (background && !_policy.AllowBackground)
        {
            error = $"Command policy \"{_policy.Name}\" does not allow background command tasks.";
            return false;
        }

        var executable = commandParts[0];
        if (_policy.AllowedCommands is not null && !_policy.AllowedCommands.Contains(executable))
        {
            error = $"Command \"{executable}\" is not allowed by command policy \"{_policy.Name}\".";
            return false;
        }

        if (commandParts.Count > 1 &&
            _policy.DeniedSubcommands is not null &&
            _policy.DeniedSubcommands.TryGetValue(executable, out var deniedSubcommands) &&
            deniedSubcommands.Contains(commandParts[1]))
        {
            error = $"Command \"{executable} {commandParts[1]}\" is denied by command policy \"{_policy.Name}\".";
            return false;
        }

        return true;
    }

    private static string BuildStreamText(CommandTaskSnapshot snapshot, string stream)
        => string.Join(
            Environment.NewLine,
            snapshot.Events
                .Where(evt => evt.Stream.Equals(stream, StringComparison.OrdinalIgnoreCase))
                .Select(evt => evt.Text));

    private static List<string>? ParseCommandArray(object? value)
    {
        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                return jsonElement.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToList();
            }

            if (jsonElement.ValueKind == JsonValueKind.String)
            {
                return ParseCommandArray(jsonElement.GetString());
            }
        }

        if (value is JArray jArray)
            return jArray.Select(token => token.ToString()).Where(token => !string.IsNullOrWhiteSpace(token)).ToList();

        if (value is IEnumerable<object> arr)
            return arr.Select(o => o.ToString()!).ToList();

        if (value is string s)
        {
            // Try parse as JSON array
            s = StripMarkdownCodeFences(s).Trim();
            if (s.StartsWith('['))
            {
                try
                {
                    var parts = System.Text.Json.JsonSerializer.Deserialize<List<string>>(s);
                    return parts;
                }
                catch (JsonException) { }
                catch (NotSupportedException) { }
            }
            // Fallback: split by space
            return s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        return null;
    }

    private static string StripMarkdownCodeFences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal) || !trimmed.EndsWith("```", StringComparison.Ordinal))
            return text;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        var body = trimmed[(firstNewline + 1)..];
        var closingFenceIndex = body.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFenceIndex < 0)
            return text;

        return body[..closingFenceIndex].TrimEnd('\r', '\n');
    }

    private static string QuoteIfNeeded(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return "\"\"";

        if (arg.Contains(' ', StringComparison.Ordinal) && !(arg.StartsWith('"') && arg.EndsWith('"')))
            return $"\"{arg}\"";

        return arg;
    }
}
