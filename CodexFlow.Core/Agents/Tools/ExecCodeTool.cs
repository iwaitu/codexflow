// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// Multi-language snippet runner (Python / Node.js / Java). The code runs only inside the isolation backend
/// (no network, private workspace copy, bounded resources). Snippets never export file changes. Without a
/// host-bound trusted context or backend the call is refused; there is no host interpreter fallback.
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

        var (languageKey, argv) = ResolveRuntime(language);
        if (!_gate.Options.LanguageImages.TryGetValue(languageKey, out var image))
            return ToolIsolationGate.Refuse(Name, ExecutionDecision.Unavailable(ExecutionReasonCodes.UnsupportedIsolation, $"No isolation image is configured for language '{languageKey}'."));

        var open = await backend.OpenSessionAsync(context, new IsolatedSessionOptions(image), ct).ConfigureAwait(false);
        if (!open.Decision.IsAllowed || open.Session is null)
            return ToolIsolationGate.Refuse(Name, open.Decision);

        await using var session = open.Session;
        var timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        var run = await session.RunAsync(new IsolatedCommandRequest(argv, StdIn: code, Timeout: timeout), ct).ConfigureAwait(false);
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

    private static (string Language, string[] Argv) ResolveRuntime(string language)
        => language switch
        {
            "NODE" or "NODEJS" or "JAVASCRIPT" or "TYPESCRIPT" => ("node", ["node", "--input-type=module"]),
            "JAVA" => ("java", ["sh", "-c", "cat > /tmp/Main.java && java --source 21 /tmp/Main.java"]),
            _ => ("python", ["python3", "-"])
        };
}
