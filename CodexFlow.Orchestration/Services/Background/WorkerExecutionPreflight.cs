// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Orchestration.Jobs;
using CodexFlow.Core.Services;
using CodexFlow.Core.Workers;
using CodexFlow.Domain.Jobs;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Services.Background;

internal enum WorkerPreflightKind
{
    /// <summary>The worker may start (possibly with a sanitized payload).</summary>
    Allowed,

    /// <summary>Refused: the stored payload is not authorized. Terminal.</summary>
    Denied,

    /// <summary>An explicit path does not exist (and no verified retained worktree replaces it). Nothing is created or started.</summary>
    WorkspaceUnavailable
}

/// <summary>
/// The result of <see cref="WorkerExecutionPreflight.EvaluateAsync"/>. <see cref="Payload"/> is non-null only when the payload was
/// sanitized (an unverified retained shadow worktree was dropped) and must replace the job's stored payload.
/// </summary>
internal sealed record WorkerPreflightOutcome(WorkerPreflightKind Kind, string Code, string Message, JObject? Payload, string? Path = null)
{
    public bool Allowed => Kind == WorkerPreflightKind.Allowed;

    public static WorkerPreflightOutcome Allow(JObject? sanitized = null) => new(WorkerPreflightKind.Allowed, "allowed", string.Empty, sanitized);

    public static WorkerPreflightOutcome Deny(string code, string message) => new(WorkerPreflightKind.Denied, code, message, null);

    public static WorkerPreflightOutcome Unavailable(string path) =>
        new(WorkerPreflightKind.WorkspaceUnavailable, "workspace_unavailable", $"The workspace path does not exist: {path}", null, path);
}

/// <summary>
/// Authorization checked immediately before ANY worker job starts, whatever created it: a fresh dispatch, a queued job that predates
/// the Gateway repository policy, a resumed or retried job. A stored payload is untrusted until proven:
/// <list type="bullet">
/// <item>the job must have a SQL session, and the payload's <c>sessionId</c> and <c>userId</c> must be present and match the job's
/// session and that session's owner;</item>
/// <item>every explicit repository path (<c>mainWorkspacePath</c>, <c>workspacePath</c>) must exist and be allowed by
/// <see cref="LocalRepositoryAuthorization"/> against the SQL persisted session root and
/// <c>Workspace:AllowedLocalRepositoryRoots</c>. A path that does not exist is NOT waved through (a worker or a race could create or
/// populate it): the job reports workspace_unavailable and nothing is created;</item>
/// <item>a retained <c>shadowWorktreePath</c> is adopted only when it is link-free, lies in the application's shadow root, belongs to
/// the Git store of an authorized repository AND is a worktree registered in that repository's own
/// <c>git worktree list --porcelain -z</c> (bound to the job's task branch). A hand-written <c>.git</c> file or a directory alias is
/// not enough; an unverified shadow is dropped and never used.</item>
/// </list>
/// Non-worker jobs (e.g. MockSimpleTask) are not affected.
/// </summary>
internal static class WorkerExecutionPreflight
{
    public static async Task<WorkerPreflightOutcome> EvaluateAsync(
        ISessionDirectory sessions, BackgroundJob job, IConfiguration? configuration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(job);

        if (!WorkerJobTypes.IsWorkerJobType(job.JobType))
        {
            return WorkerPreflightOutcome.Allow();
        }

        if (string.IsNullOrWhiteSpace(job.SessionId))
        {
            return WorkerPreflightOutcome.Deny("session_missing", "A worker job must belong to a session.");
        }

        JObject payload;
        try
        {
            payload = string.IsNullOrWhiteSpace(job.PayloadJson) ? new JObject() : JObject.Parse(job.PayloadJson);
        }
        catch (JsonException)
        {
            return WorkerPreflightOutcome.Deny("payload_invalid", "The worker job payload is not valid JSON.");
        }

        var session = await sessions.FindAsync(job.SessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return WorkerPreflightOutcome.Deny("session_not_found", "The job's session does not exist.");
        }

        var payloadSession = payload["sessionId"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(payloadSession) || !string.Equals(payloadSession, job.SessionId, StringComparison.Ordinal))
        {
            return WorkerPreflightOutcome.Deny("session_mismatch", "The job payload does not name the job's own session.");
        }

        var claimedUser = payload["userId"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(claimedUser) || !string.Equals(session.UserId, claimedUser, StringComparison.Ordinal))
        {
            return WorkerPreflightOutcome.Deny("user_mismatch", "The job payload's user is missing or does not own the job's session.");
        }

        var allowed = LocalRepositoryAuthorization.ReadAllowedRoots(configuration);
        var sessionRoot = session.WorkspacePath ?? string.Empty;
        var inspector = new GitArtifactInspector();
        var worktreeKey = payload["worktreeKey"]?.Value<string>() ?? job.TaskId ?? job.JobId;

        // Verified retained shadow (if any) first: it may legitimately stand in for a main repository that is currently missing.
        var shadow = payload["shadowWorktreePath"]?.Value<string>();
        var shadowVerified = false;
        var sanitized = false;
        if (!string.IsNullOrWhiteSpace(shadow))
        {
            shadowVerified = Directory.Exists(shadow) &&
                             await IsVerifiedShadowAsync(shadow, sessionRoot, allowed, inspector, worktreeKey, ct).ConfigureAwait(false);
            if (!shadowVerified)
            {
                payload.Remove("shadowWorktreePath");
                var mirroredWorkspace = payload["workspacePath"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(mirroredWorkspace) && SamePath(mirroredWorkspace, shadow))
                {
                    // workspacePath only mirrored the shadow: the worker must start from the (authorized) main repository instead.
                    var mirrorMain = payload["mainWorkspacePath"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(mirrorMain)) payload.Remove("workspacePath"); else payload["workspacePath"] = mirrorMain;
                }

                payload["worktreeRetained"] = false;
                payload["shadowWorktreeDropped"] = "not_a_registered_worktree_of_an_authorized_repository";
                sanitized = true;
            }
        }

        foreach (var key in new[] { "mainWorkspacePath", "workspacePath" })
        {
            var path = payload[key]?.Value<string>();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var isRetainedShadow = shadowVerified && SamePath(path, shadow!);
            if (isRetainedShadow)
            {
                continue; // already verified as a registered worktree of an authorized repository
            }

            if (!Directory.Exists(path))
            {
                if (shadowVerified)
                {
                    continue; // the job continues in the verified retained worktree; the main path is not used
                }

                return WorkerPreflightOutcome.Unavailable(path);
            }

            var decision = LocalRepositoryAuthorization.Evaluate(path, sessionRoot, allowed);
            if (!decision.Allowed)
            {
                return WorkerPreflightOutcome.Deny(
                    decision.Reason == LocalRepositoryDenial.InvalidConfiguration ? "repository_authorization_misconfigured" : "repository_not_authorized",
                    $"{key} is not an authorized repository location: {decision.Message}");
            }
        }

        // Every worker needs a real execution root, and the validated field must be the one the worker actually consumes:
        // Forge uses mainWorkspacePath ?? workspacePath; Explore/Plan/Verify read workspacePath only. A missing root is never waved
        // through (an empty path would fall back to the process working directory).
        var main = payload["mainWorkspacePath"]?.Value<string>();
        var work = payload["workspacePath"]?.Value<string>();
        var isForge = WorkerJobTypes.TryResolveWorkerType(job.JobType, out var wt) && wt == CodexFlow.Core.Protocols.WorkerType.Forge;
        if (isForge)
        {
            if (string.IsNullOrWhiteSpace(main) && string.IsNullOrWhiteSpace(work) && !shadowVerified)
            {
                return WorkerPreflightOutcome.Unavailable(string.Empty);
            }
        }
        else if (string.IsNullOrWhiteSpace(work))
        {
            // Normalize from an already validated main path, else from the trusted SQL session root - only if it really exists
            // and the policy accepts it. Written back so the worker executes exactly what was verified.
            string? root = null;
            if (!string.IsNullOrWhiteSpace(main) && Directory.Exists(main) && LocalRepositoryAuthorization.Evaluate(main, sessionRoot, allowed).Allowed)
            {
                root = main;
            }
            else if (string.IsNullOrWhiteSpace(main) && !string.IsNullOrWhiteSpace(sessionRoot) && Directory.Exists(sessionRoot) &&
                     LocalRepositoryAuthorization.Evaluate(sessionRoot, sessionRoot, allowed).Allowed)
            {
                root = sessionRoot;
            }

            if (root is null)
            {
                return WorkerPreflightOutcome.Unavailable(main ?? string.Empty);
            }

            payload["workspacePath"] = root;
            sanitized = true;
        }

        return WorkerPreflightOutcome.Allow(sanitized ? payload : null);
    }

    private static bool SamePath(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// A retained shadow is verified only if ALL hold: no link anywhere in its own path, inside the application's shadow root, git's
    /// common dir is "&lt;repository&gt;/.git" of a repository the policy authorizes, the path is a worktree REGISTERED in that
    /// repository's own registry (not a directory with a hand-written .git pointing at it) and, when the job names a task, it is that
    /// task's branch.
    /// </summary>
    private static async Task<bool> IsVerifiedShadowAsync(
        string path, string sessionRoot, AllowedLocalRepositoryRoots allowed, GitArtifactInspector inspector,
        string? worktreeKey, CancellationToken ct)
    {
        try
        {
            if (LocalRepositoryAuthorization.ContainsLink(path) || !IsInsideShadowRoot(path))
            {
                return false;
            }

            var commonDir = await inspector.GetRepositoryIdentityAsync(path, ct).ConfigureAwait(false);
            if (!string.Equals(Path.GetFileName(commonDir), ".git", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var repositoryRoot = Directory.GetParent(commonDir)?.FullName;
            if (string.IsNullOrWhiteSpace(repositoryRoot) ||
                !LocalRepositoryAuthorization.Evaluate(repositoryRoot, sessionRoot, allowed).Allowed)
            {
                return false;
            }

            var registered = await inspector.FindRegisteredWorktreeAsync(repositoryRoot, path, ct).ConfigureAwait(false);
            if (registered is null || registered.Prunable)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(worktreeKey) ||
                   string.Equals(registered.Branch, $"refs/heads/task-{worktreeKey}", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Mirrors <c>GitService.ResolveShadowRoot</c>: shadows are only ever created below &lt;root&gt;/shadows.</summary>
    private static bool IsInsideShadowRoot(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrWhiteSpace(root))
            {
                candidates.Add(Path.Combine(root, "cfw", "shadows"));
            }
        }

        candidates.Add(Path.Combine(Path.GetTempPath(), "codexflow-shadow-root", "shadows"));
        return candidates
            .Select(candidate => Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)))
            .Any(candidate => full.StartsWith(candidate + Path.DirectorySeparatorChar, comparison));
    }
}
