// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.Execution;

/// <summary>
/// Shared gate for tools that execute untrusted code. The trusted context comes only from the host-bound accessor;
/// model arguments (workspace_path, project_root, session_id, ...) are never consulted. Missing context, missing
/// backend or an Unknown capability all refuse, and there is no host fallback.
/// </summary>
public sealed class ToolIsolationGate(
    IIsolatedExecutionService? service,
    ITrustedExecutionContextAccessor? accessor,
    IsolationOptions? options,
    IExecutionApprovalBroker? approvalBroker = null,
    IOptionsMonitor<ExecutionOptions>? executionOptions = null,
    IServiceProvider? serviceProvider = null)
{
    private readonly ITrustedExecutionContextAccessor _accessor = accessor ?? new TrustedExecutionContextAccessor();

    public IsolationOptions Options { get; } = options ?? new IsolationOptions();

    public IIsolatedExecutionService? Service => service;

    public IExecutionApprovalBroker? ApprovalBroker { get; } = approvalBroker;

    public IOptionsMonitor<ExecutionOptions>? ExecutionOptions { get; } = executionOptions;

    public ICodexGateway? Gateway => serviceProvider?.GetService<ICodexGateway>();

    public string StagingRoot => Options.StagingRoot ?? Path.Combine(Path.GetTempPath(), "codexflow-isolation");

    public static ToolIsolationGate Unconfigured { get; } = new(null, null, null);

    /// <summary>Returns null when execution may proceed, otherwise a failed tool result carrying the reason code.</summary>
    public CodexToolResult? TryAcquire(string toolName, ToolCapability capability, out TrustedExecutionContext context, out IIsolatedExecutionService backend)
    {
        context = null!;
        backend = null!;

        var capabilityDecision = ToolCapabilityCatalog.Evaluate(toolName, capability);
        if (!capabilityDecision.IsAllowed)
        {
            return Refuse(toolName, capabilityDecision);
        }

        if (capability != ToolCapability.ExecuteUntrustedCode)
        {
            return Refuse(toolName, ExecutionDecision.Deny(ExecutionReasonCodes.UnknownToolCapability, $"Tool '{toolName}' is not declared as an isolated-execution tool."));
        }

        var current = _accessor.Current;
        if (current is null)
        {
            return Refuse(toolName, ExecutionDecision.Deny(
                ExecutionReasonCodes.MissingTrustedContext,
                "No host-provided execution context is bound to this call; untrusted code is never run without it."));
        }

        var valid = current.Validate();
        if (!valid.IsAllowed)
        {
            return Refuse(toolName, valid);
        }

        if (service is null)
        {
            return Refuse(toolName, ExecutionDecision.Unavailable(
                ExecutionReasonCodes.IsolationNotConfigured,
                "No isolated execution backend is configured; refusing to run on the host."));
        }

        if (!service.SupportsNetworkMode(current.Network.Mode))
        {
            return Refuse(toolName, ExecutionDecision.Unavailable(
                ExecutionReasonCodes.NetworkNotPermitted,
                $"Execution backend '{service.GetType().Name}' does not support network mode '{current.Network.Mode}'."));
        }

        context = current;
        backend = service;
        return null;
    }

    public async Task<(CodexToolResult? Refusal, ExecutionAuthorizationResult Authorization)> AuthorizeBeforeExecutionAsync(
        string toolName,
        TrustedExecutionContext context,
        IIsolatedExecutionService backend,
        IReadOnlyList<string> fullArgv,
        string? cwd = null,
        string? stdIn = null,
        string? codeSha256 = null,
        string? scriptSha256 = null,
        string? inputsManifestSha256 = null,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        Func<bool>? verifyStagedArtifactsUnchanged = null,
        Func<(string? ScriptSha256, string? InputsManifestSha256)>? refreshArtifactHashes = null,
        Func<ExecutionBackend, (IReadOnlyList<string> FullArgv, string? StdIn, IReadOnlyDictionary<string, string>? EnvOverrides)?>? resolveArgvForBackend = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(fullArgv);

        if (fullArgv.Count == 0 || string.IsNullOrWhiteSpace(fullArgv[0]))
        {
            var deny = ExecutionDecision.Deny(ExecutionReasonCodes.InvalidRequest, "Command argv must not be empty.");
            return (Refuse(toolName, deny), new ExecutionAuthorizationResult(deny, [], string.Empty));
        }

        var activeBackend = backend.ActiveBackend;
        if (ApprovalBroker is null)
        {
            return (null, new ExecutionAuthorizationResult(
                ExecutionDecision.Allow(),
                fullArgv.Skip(1).ToArray(),
                fullArgv[0],
                ApprovedBackend: activeBackend,
                IsPreResolvedExecutable: false));
        }

        ExecutionAuthorizationContext? BuildAuthContextForBackend(ExecutionBackend targetBackend, bool refreshHashes, out string? errorReason)
        {
            errorReason = null;
            var effectiveFullArgv = fullArgv;
            var effectiveStdIn = stdIn;
            var effectiveEnv = environmentOverrides;
            if (resolveArgvForBackend is not null)
            {
                var perBackend = resolveArgvForBackend(targetBackend);
                if (perBackend is null || perBackend.Value.FullArgv.Count == 0)
                {
                    errorReason = $"Command could not be prepared for backend '{targetBackend}'.";
                    return null;
                }

                effectiveFullArgv = perBackend.Value.FullArgv;
                effectiveStdIn = perBackend.Value.StdIn;
                effectiveEnv = perBackend.Value.EnvOverrides;
            }

            var currentScriptSha = scriptSha256;
            var currentInputsSha = inputsManifestSha256;
            if (refreshHashes && refreshArtifactHashes is not null)
            {
                var refreshed = refreshArtifactHashes();
                currentScriptSha = refreshed.ScriptSha256;
                currentInputsSha = refreshed.InputsManifestSha256;
            }

            var cmd = effectiveFullArgv[0];
            var args = effectiveFullArgv.Skip(1).ToArray();
            var resolvedExecutable = cmd;
            IReadOnlyList<string> resolvedArgs = args;
            IReadOnlyList<string> interpreterChain = [cmd];
            var trustedOrigin = false;

            if (targetBackend == ExecutionBackend.Local)
            {
                var trustedPaths = ExecutionOptions?.CurrentValue.TrustedExecutablePaths?.ToArray() ?? [];
                var rawPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                var trustedDirs = trustedPaths
                    .Where(static s => !string.IsNullOrWhiteSpace(s))
                    .Select(static s => File.Exists(s) ? Path.GetDirectoryName(s) : s)
                    .Where(static s => !string.IsNullOrWhiteSpace(s));
                var combinedPath = string.Join(Path.PathSeparator, new[] { rawPath }.Concat(trustedDirs!).Where(static s => !string.IsNullOrWhiteSpace(s)));
                var hostEnv = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                {
                    ["PATH"] = combinedPath
                };
                var hostResolved = WindowsCommandResolver.Resolve(cmd, args, hostEnv);
                if (!hostResolved.Allowed)
                {
                    errorReason = hostResolved.RejectionReason ?? "Command rejected by host launch policy.";
                    return null;
                }

                resolvedExecutable = hostResolved.Executable;
                resolvedArgs = hostResolved.Args;
                interpreterChain = hostResolved.InterpreterChain;
                trustedOrigin = AutoInWorkspaceEvaluator.IsTrustedExecutableOrigin(
                    resolvedExecutable,
                    context.WorkspaceRoot,
                    StagingRoot,
                    trustedPaths,
                    out _);
            }
            else
            {
                var norm = cmd.Replace('\\', '/');
                trustedOrigin = !norm.Contains('/', StringComparison.Ordinal) ||
                                norm.StartsWith("/usr/bin/", StringComparison.Ordinal) ||
                                norm.StartsWith("/bin/", StringComparison.Ordinal);
            }

            var envDelta = effectiveEnv is null
                ? Array.Empty<KeyValuePair<string, string>>()
                : effectiveEnv.OrderBy(static kv => kv.Key, StringComparer.Ordinal).ToArray();

            var req = new ExecutionRequest
            {
                SessionId = context.SessionId,
                JobId = context.JobId,
                TurnId = $"{context.JobId}:{context.TaskId}:{context.AttemptId}",
                WorkspaceRoot = Path.GetFullPath(context.WorkspaceRoot),
                ToolName = toolName,
                Executable = resolvedExecutable,
                InterpreterChain = interpreterChain,
                Argv = resolvedArgs,
                Cwd = string.IsNullOrWhiteSpace(cwd) ? "." : cwd,
                StdinSha256 = effectiveStdIn is null ? null : ExecutionRequest.Sha256Text(effectiveStdIn),
                CodeSha256 = codeSha256,
                ScriptSha256 = currentScriptSha,
                InputsManifestSha256 = currentInputsSha,
                EnvironmentDelta = envDelta,
                Backend = targetBackend,
                Network = targetBackend == ExecutionBackend.Local ? ExecutionNetworkMode.Host.ToString() : context.Network.Mode.ToString(),
                Filesystem = targetBackend == ExecutionBackend.Local ? "unrestricted" : "container",
                TrustedOriginVerified = trustedOrigin
            };

            return new ExecutionAuthorizationContext(
                Request: req,
                WorkspaceRoot: context.WorkspaceRoot,
                StagingRoot: StagingRoot,
                StagingWorkspaceDir: string.Empty,
                GuestGitDir: null,
                VerifyStagedArtifactsUnchanged: verifyStagedArtifactsUnchanged,
                RebuildForBackend: nextBackend => BuildAuthContextForBackend(nextBackend, refreshHashes: true, out _));
        }

        var initialCtx = BuildAuthContextForBackend(activeBackend, refreshHashes: false, out var initialErr);
        if (initialCtx is null)
        {
            var deny = ExecutionDecision.Deny(ExecutionReasonCodes.InvalidRequest, initialErr ?? "Command rejected.");
            return (Refuse(toolName, deny), new ExecutionAuthorizationResult(deny, [], string.Empty, ApprovedBackend: activeBackend));
        }

        var authResult = await ApprovalBroker.AuthorizeAsync(initialCtx, ct).ConfigureAwait(false);
        if (!authResult.Decision.IsAllowed)
        {
            return (Refuse(toolName, authResult.Decision), authResult);
        }

        return (null, authResult);
    }

    public static CodexToolResult Refuse(string toolName, ExecutionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var verb = decision.Kind == ExecutionDecisionKind.Unavailable ? "unavailable" : "denied";
        return CodexToolResult.Error(
            $"{toolName} {verb}: [{decision.ReasonCode}] {decision.Message}",
            new { decision.Kind, decision.ReasonCode, decision.Message });
    }
}
