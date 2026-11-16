// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// Multi-language snippet runner (Python / Node.js / Java). The code runs only inside the configured execution backend.
/// Snippets never export file changes. Without a host-bound trusted context or backend the call is refused.
/// </summary>
public class ExecCodeTool(ILogger<ExecCodeTool> logger, ToolIsolationGate? isolationGate = null) : ICodexTool
{
    private readonly ToolIsolationGate _gate = isolationGate ?? ToolIsolationGate.Unconfigured;

    public string Name => "exec_code";
    public string Description => "在隔离容器沙箱（无网络、私有工作区副本）中执行代码片段。参数：code (要执行的代码), language (可选：python|node|java, 默认 python)。Few-shot: exec_code({\"code\":\"print(1+1)\",\"language\":\"python\"})。代码对工作区的写入不会导出。";
    public ToolCategory Category => ToolCategory.Forge;
    public IReadOnlyList<int> AllowedStages => [3, 4];
    public ToolCapability Capability => ToolCapability.ExecuteUntrustedCode;

    private const int DefaultTimeoutSeconds = 30;

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var code = arguments.GetValueOrDefault("code")?.ToString();
        var language = (arguments.GetValueOrDefault("language")?.ToString() ?? "python").ToUpperInvariant();

        if (string.IsNullOrEmpty(code))
            return CodexToolResult.Error("Missing code parameter.");

        var refusal = _gate.TryAcquire(Name, Capability, out var context, out var backend);
        if (refusal is not null)
            return refusal;

        var (languageKey, dockerArgv) = ResolveRuntime(language);
        if (!_gate.Options.LanguageImages.TryGetValue(languageKey, out var image))
            return ToolIsolationGate.Refuse(Name, ExecutionDecision.Unavailable(ExecutionReasonCodes.UnsupportedIsolation, $"No isolation image is configured for language '{languageKey}'."));

        var codeSha256 = ExecutionRequest.Sha256Text(code);
        string? prestageDir = null;
        string? prestageMainJava = null;

        try
        {
            if (languageKey == "java")
            {
                prestageDir = Path.Combine(_gate.StagingRoot, "prestage", $"exec-code-{Guid.NewGuid():N}");
                Directory.CreateDirectory(prestageDir);
                prestageMainJava = Path.Combine(prestageDir, "Main.java");
                await File.WriteAllTextAsync(prestageMainJava, code, ct).ConfigureAwait(false);
            }

            (IReadOnlyList<string> FullArgv, string? StdIn, IReadOnlyDictionary<string, string>? EnvOverrides)? PrepareForBackend(ExecutionBackend targetBackend)
            {
                if (targetBackend == ExecutionBackend.Local)
                {
                    if (languageKey == "java")
                    {
                        return (new[] { "java", "--source", "21", "Main.java" }, null, null);
                    }

                    if (languageKey == "python")
                    {
                        var pyExe = (OperatingSystem.IsWindows()
                            ? FindFirstHostExecutable("python", "python3")
                            : FindFirstHostExecutable("python3", "python")) ?? "python";
                        return (new[] { pyExe, "-" }, code, null);
                    }

                    return (dockerArgv, code, null);
                }

                return (dockerArgv, code, null);
            }

            var initialPrepared = PrepareForBackend(backend.ActiveBackend)!.Value;

            Func<bool>? verifyCopy = prestageMainJava is null
                ? null
                : () => File.Exists(prestageMainJava) &&
                        string.Equals(ExecutionRequest.Sha256File(prestageMainJava), codeSha256, StringComparison.Ordinal);

            var (authRefusal, authorization) = await _gate.AuthorizeBeforeExecutionAsync(
                Name,
                context,
                backend,
                initialPrepared.FullArgv,
                stdIn: initialPrepared.StdIn,
                codeSha256: codeSha256,
                verifyStagedArtifactsUnchanged: verifyCopy,
                resolveArgvForBackend: PrepareForBackend,
                ct: ct).ConfigureAwait(false);
            if (authRefusal is not null)
                return authRefusal;

            var open = await backend.OpenPinnedBackendSessionAsync(authorization.ApprovedBackend, context, new IsolatedSessionOptions(image), ct).ConfigureAwait(false);
            if (!open.Decision.IsAllowed || open.Session is null)
                return ToolIsolationGate.Refuse(Name, open.Decision);

            await using var session = open.Session;
            if (session.Layout.Backend != authorization.ApprovedBackend)
            {
                return ToolIsolationGate.Refuse(Name, ExecutionDecision.Deny(
                    ExecutionReasonCodes.ApprovalInvalidated,
                    "Opened session backend did not match approved backend."));
            }

            // Consume frozen EffectiveExecutable + EffectiveArgv from authorization (B6)
            var launchArgs = authorization.EffectiveArgv.ToArray();
            string? stdIn = code;
            if (session.Layout.Backend == ExecutionBackend.Local && languageKey == "java")
            {
                Directory.CreateDirectory(session.Layout.TempDir);
                var mainJava = Path.Combine(session.Layout.TempDir, "Main.java");
                File.Copy(prestageMainJava!, mainJava, overwrite: true);
                if (!string.Equals(ExecutionRequest.Sha256File(mainJava), codeSha256, StringComparison.Ordinal))
                {
                    return ToolIsolationGate.Refuse(Name, ExecutionDecision.Deny(
                        ExecutionReasonCodes.ApprovalInvalidated,
                        "Staged Main.java hash differed from approved codeSha256."));
                }

                for (var i = 0; i < launchArgs.Length; i++)
                {
                    if (string.Equals(launchArgs[i], "Main.java", StringComparison.Ordinal))
                    {
                        launchArgs[i] = mainJava;
                    }
                }

                stdIn = null;
            }

            var frozenArgv = new string[1 + launchArgs.Length];
            frozenArgv[0] = authorization.EffectiveExecutable;
            Array.Copy(launchArgs, 0, frozenArgv, 1, launchArgs.Length);

            var timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
            var run = await session.RunAsync(
                new IsolatedCommandRequest(
                    frozenArgv,
                    StdIn: stdIn,
                    Timeout: timeout,
                    PreResolvedExecutable: authorization.IsPreResolvedExecutable),
                ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            switch (run.Status)
            {
                case IsolatedCommandStatus.TimedOut:
                    return CodexToolResult.Error($"代码执行超时（{DefaultTimeoutSeconds}s）。");
                case IsolatedCommandStatus.Canceled:
                    throw new OperationCanceledException(ct);
                case IsolatedCommandStatus.Completed:
                case IsolatedCommandStatus.Failed:
                    break;
                default:
                    StructuredLog.Warning(logger, "exec_code refused/failed: {Reason}", run.Decision.ReasonCode);
                    return ToolIsolationGate.Refuse(Name, run.Decision);
            }

            var result = new { Stdout = run.Stdout, Stderr = run.Stderr, ExitCode = run.ExitCode };
            if (run.ExitCode != 0)
            {
                return CodexToolResult.Error(
                    $"⚠️ 代码执行失败（exit code {run.ExitCode}）\n[stdout]\n{result.Stdout}\n[stderr]\n{result.Stderr}",
                    result);
            }

            return CodexToolResult.Succeeded(
                string.IsNullOrWhiteSpace(result.Stdout) ? "✅ 代码执行成功（无输出）" : $"✅ 执行结果:\n{result.Stdout}",
                result);
        }
        finally
        {
            if (prestageDir is not null)
            {
                LocalExecutionService.TryDeleteDirectory(prestageDir);
            }
        }
    }

    private static (string Language, string[] Argv) ResolveRuntime(string language)
        => language switch
        {
            "NODE" or "NODEJS" or "JAVASCRIPT" or "TYPESCRIPT" => ("node", ["node", "--input-type=module"]),
            "JAVA" => ("java", ["sh", "-c", "cat > /tmp/Main.java && java --source 21 /tmp/Main.java"]),
            _ => ("python", ["python3", "-"])
        };

    private static string? FindFirstHostExecutable(params string[] candidates)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = pathEnv };
        foreach (var candidate in candidates)
        {
            if (OperatingSystem.IsWindows())
            {
                if (WindowsCommandResolver.FindExecutableOnPath(candidate, env) is { } found)
                {
                    return found;
                }
            }
            else if (WindowsCommandResolver.FindUnixExecutableOnPath(candidate, env) is { } unixFound)
            {
                return unixFound;
            }
        }

        return null;
    }
}
