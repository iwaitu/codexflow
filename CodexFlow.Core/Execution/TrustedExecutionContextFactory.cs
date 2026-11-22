// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using System.Text;
using CodexFlow.Core.Agents;

namespace CodexFlow.Core.Execution;

/// <summary>Host-side helpers that build trusted contexts from host-known facts (never from model output).</summary>
public static class TrustedExecutionContextFactory
{
    public static ExportScope ToExportScope(TaskFileScopeDescriptor? scope)
        => scope is null
            ? ExportScope.Empty
            : ExportScope.From(scope.AllowedFiles.Concat(scope.DeclaredOutputFiles), scope.AllowedDirectories, scope.AllowedFileNames, scope.DisallowedDirectories);

    /// <summary>
    /// Stable, order-independent but CASE-PRESERVING hash over every authorization list, with length-prefixed entries so
    /// different authorizations can never collapse into one hash (e.g. "Foo.cs" vs "foo.cs", or moving an entry between lists).
    /// </summary>
    public static string ComputeScopeHash(ExportScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var sb = new StringBuilder("cf-scope-v2").Append((char)10);
        void Append(string tag, IEnumerable<string> items)
        {
            foreach (var item in items.OrderBy(x => x, StringComparer.Ordinal))
            {
                sb.Append(tag).Append(':').Append(item.Length).Append(':').Append(item).Append((char)10);
            }
        }

        Append("F", scope.AllowedFiles);
        Append("D", scope.AllowedDirectories);
        Append("N", scope.AllowedFileNames);
        Append("X", scope.DisallowedDirectories);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static TrustedExecutionContext ForJob(
        string userId,
        string sessionId,
        string jobId,
        string taskId,
        int attempt,
        string workspaceRoot,
        string? baseCommit,
        TaskFileScopeDescriptor? taskScope,
        ExecutionResourceLimits? limits = null,
        DateTimeOffset? deadline = null)
    {
        var scope = ToExportScope(taskScope);
        var remoteScope = CodexFlow.Core.Remote.RemoteExecutionAmbientContext.Current;
        return new TrustedExecutionContext
        {
            UserId = userId,
            SessionId = sessionId,
            JobId = jobId,
            TaskId = taskId,
            AttemptId = $"{jobId}:{attempt}",
            WorkspaceRoot = workspaceRoot,
            BaseCommit = baseCommit,
            Scope = scope,
            ScopeHash = ComputeScopeHash(scope),
            Limits = limits ?? new ExecutionResourceLimits(),
            Deadline = deadline,
            Origin = ExecutionOrigin.DevWorker,
            TrustedOriginVerified = true,
            ControlChannel = remoteScope?.Channel ?? CodexFlow.Core.Remote.ControlChannel.Local,
            RemoteStamp = remoteScope?.Stamp
        };
    }

    /// <summary>Creates a single-invocation DailyChat execution context bound strictly to the granted tools.</summary>
    public static TrustedExecutionContext ForDailyInvocation(
        string userId,
        string sessionId,
        string turnId,
        string invocationId,
        string workspaceRoot,
        ExportScope scope,
        IReadOnlySet<string> grantedTools,
        int policyVersion,
        ExecutionResourceLimits? limits = null,
        DateTimeOffset? notAfter = null,
        DateTimeOffset? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(grantedTools);
        var remoteScope = CodexFlow.Core.Remote.RemoteExecutionAmbientContext.Current;
        return new TrustedExecutionContext
        {
            UserId = userId,
            SessionId = sessionId,
            JobId = string.Empty,
            TaskId = string.Empty,
            AttemptId = string.Empty,
            TurnId = turnId,
            InvocationId = invocationId,
            WorkspaceRoot = workspaceRoot,
            Scope = scope,
            ScopeHash = ComputeScopeHash(scope),
            Limits = limits ?? new ExecutionResourceLimits(),
            Origin = ExecutionOrigin.DailyChat,
            GrantedTools = new HashSet<string>(grantedTools, StringComparer.OrdinalIgnoreCase),
            PolicyVersion = policyVersion,
            TrustedOriginVerified = true,
            NotAfter = notAfter ?? DateTimeOffset.UtcNow.AddMinutes(30),
            Deadline = deadline,
            ControlChannel = remoteScope?.Channel ?? CodexFlow.Core.Remote.ControlChannel.Local,
            RemoteStamp = remoteScope?.Stamp
        };
    }
}
