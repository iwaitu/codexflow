// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;

namespace CodexFlow.Core.Execution;

/// <summary>Trusted capability of a tool. Declared by the host, never by model input. Unknown denies.</summary>
public enum ToolCapability
{
    Unknown = 0,
    ReadOnly,
    WorkspaceWrite,
    ExecuteUntrustedCode,
    BrowserRead,
    BrowserInteract,
    ComputerRead,
    ComputerInteract
}

public enum ExecutionDecisionKind
{
    Denied = 0,
    Allowed,
    Unavailable
}

public static class ExecutionReasonCodes
{
    public const string Allowed = "Allowed";
    public const string MissingTrustedContext = "MissingTrustedContext";
    public const string InvalidTrustedContext = "InvalidTrustedContext";
    public const string UnknownToolCapability = "UnknownToolCapability";
    public const string IsolationUnavailable = "IsolationUnavailable";
    public const string IsolationNotConfigured = "IsolationNotConfigured";
    public const string ImageUnavailable = "ImageUnavailable";
    public const string UnsupportedIsolation = "UnsupportedIsolation";
    public const string NetworkNotPermitted = "NetworkNotPermitted";
    public const string WorkspaceTooLarge = "WorkspaceTooLarge";
    public const string WorkspaceNotFound = "WorkspaceNotFound";
    public const string Timeout = "Timeout";
    public const string Canceled = "Canceled";
    public const string ResourceLimitExceeded = "ResourceLimitExceeded";
    public const string SessionTerminated = "SessionTerminated";
    public const string InvalidRequest = "InvalidRequest";
    public const string ExportRejectedOutOfScope = "ExportRejectedOutOfScope";
    public const string ExportRejectedLink = "ExportRejectedLink";
    public const string ExportRejectedProtectedPath = "ExportRejectedProtectedPath";
    public const string ExportRejectedUnsafePath = "ExportRejectedUnsafePath";
    public const string ExportRejectedQuota = "ExportRejectedQuota";
    public const string ExportRejectedSpecialFile = "ExportRejectedSpecialFile";
    public const string ExportRejectedConcurrentModification = "ExportRejectedConcurrentModification";
    public const string ExportFailed = "ExportFailed";
    public const string InvalidOperation = "InvalidOperation";
    public const string MissingExpectedHash = "MissingExpectedHash";
    public const string PermissionDenied = "PermissionDenied";
    public const string ApprovalUnavailable = "approval_unavailable";
    public const string ApprovalExpired = "ApprovalExpired";
    public const string ApprovalInvalidated = "ApprovalInvalidated";
    public const string PlanModeActive = "plan_mode_active";
}

public sealed record ExecutionDecision(ExecutionDecisionKind Kind, string ReasonCode, string Message)
{
    public bool IsAllowed => Kind == ExecutionDecisionKind.Allowed;

    public static ExecutionDecision Allow() => new(ExecutionDecisionKind.Allowed, ExecutionReasonCodes.Allowed, "allowed");

    public static ExecutionDecision Deny(string reasonCode, string message) => new(ExecutionDecisionKind.Denied, reasonCode, message);

    public static ExecutionDecision Unavailable(string reasonCode, string message) => new(ExecutionDecisionKind.Unavailable, reasonCode, message);
}

public sealed record ExecutionResourceLimits
{
    public double Cpus { get; init; } = 2;
    /// <summary>Container memory cap. tmpfs workspace/tmp pages are charged to it, so keep it above the volume sizes you rely on.</summary>
    public long MemoryBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Hard byte cap of the guest /workspace volume (tmpfs). The effective size is never smaller than the staged snapshot plus headroom.</summary>
    public long GuestWorkspaceBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Hard byte cap of the guest /tmp volume (tmpfs).</summary>
    public long GuestTmpBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>Hard inode cap per guest volume, so unlimited tiny files cannot exhaust anything.</summary>
    public long GuestMaxInodes { get; init; } = 400_000;
    public int Pids { get; init; } = 512;
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public long MaxOutputBytes { get; init; } = 4L * 1024 * 1024;
    public long MaxWorkspaceBytes { get; init; } = 1024L * 1024 * 1024;
    public int MaxFiles { get; init; } = 100_000;
    public long MaxExportBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxExportFiles { get; init; } = 2000;
}

public enum ExecutionNetworkMode
{
    /// <summary>No network at all (required by Docker isolation).</summary>
    None = 0,
    /// <summary>Host network access (only supported by Local execution backend).</summary>
    Host,
    /// <summary>Reserved. Always refused: unrestricted egress is never silently enabled.</summary>
    Unrestricted
}

public sealed record ExecutionNetworkPolicy(ExecutionNetworkMode Mode = ExecutionNetworkMode.None)
{
    public static ExecutionNetworkPolicy Denied { get; } = new(ExecutionNetworkMode.None);
}

/// <summary>
/// Workspace-relative scope (forward slashes) that guest changes may be exported into. Matching is CASE-SENSITIVE:
/// on a case-sensitive host "Foo.cs" and "foo.cs" are different files and must not share an authorization. (Case
/// collisions on case-insensitive hosts are rejected separately by the export analyzer.) DisallowedDirectories always win.
/// </summary>
public sealed record ExportScope
{
    public static ExportScope Empty { get; } = new();

    public IReadOnlyList<string> AllowedFiles { get; init; } = Array.Empty<string>();

    /// <summary>Any file below these directories may be created/modified/deleted.</summary>
    public IReadOnlyList<string> AllowedDirectories { get; init; } = Array.Empty<string>();

    /// <summary>Bare file names that may be touched in any directory (mirrors TaskFileScopeDescriptor.AllowedFileNames).</summary>
    public IReadOnlyList<string> AllowedFileNames { get; init; } = Array.Empty<string>();

    /// <summary>Directories that are never exportable, even if an allow rule matches.</summary>
    public IReadOnlyList<string> DisallowedDirectories { get; init; } = Array.Empty<string>();

    public bool IsEmpty => AllowedFiles.Count == 0 && AllowedDirectories.Count == 0 && AllowedFileNames.Count == 0;

    public static ExportScope From(
        IEnumerable<string>? files,
        IEnumerable<string>? directories = null,
        IEnumerable<string>? fileNames = null,
        IEnumerable<string>? disallowedDirectories = null)
        => new()
        {
            AllowedFiles = Normalize(files),
            AllowedDirectories = Normalize(directories),
            AllowedFileNames = Normalize(fileNames),
            DisallowedDirectories = Normalize(disallowedDirectories)
        };

    private static ReadOnlyCollection<string> Normalize(IEnumerable<string>? items)
        => new((items ?? []).Select(WorkspacePath.NormalizeLoose).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList());

    public bool Allows(string normalizedRelativePath)
    {
        ArgumentNullException.ThrowIfNull(normalizedRelativePath);
        foreach (var d in DisallowedDirectories)
        {
            if (normalizedRelativePath.StartsWith(d + "/", StringComparison.Ordinal) || normalizedRelativePath == d)
            {
                return false;
            }
        }

        if (AllowedFiles.Contains(normalizedRelativePath, StringComparer.Ordinal))
        {
            return true;
        }

        var name = normalizedRelativePath[(normalizedRelativePath.LastIndexOf('/') + 1)..];
        if (AllowedFileNames.Contains(name, StringComparer.Ordinal))
        {
            return true;
        }

        foreach (var d in AllowedDirectories)
        {
            if (normalizedRelativePath.StartsWith(d + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

public enum ExecutionOrigin
{
    Unspecified = 0,
    DevWorker = 1,
    DailyChat = 2,
    BusinessSkill = 3
}

/// <summary>
/// Host-created execution context. It is never populated from model arguments.
/// </summary>
public sealed record TrustedExecutionContext
{
    public required string UserId { get; init; }
    public required string SessionId { get; init; }
    public required string JobId { get; init; }
    public required string TaskId { get; init; }
    public required string AttemptId { get; init; }
    public required string WorkspaceRoot { get; init; }
    public string? BaseCommit { get; init; }
    public string? ScopeHash { get; init; }
    public ExportScope Scope { get; init; } = ExportScope.Empty;
    public DateTimeOffset? Deadline { get; init; }
    public ExecutionResourceLimits Limits { get; init; } = new();
    public ExecutionNetworkPolicy Network { get; init; } = ExecutionNetworkPolicy.Denied;
    public ExecutionOrigin Origin { get; init; } = ExecutionOrigin.Unspecified;
    public string? TurnId { get; init; }
    public string? InvocationId { get; init; }
    public IReadOnlySet<string>? GrantedTools { get; init; }
    public DateTimeOffset? NotAfter { get; init; }
    public bool TrustedOriginVerified { get; init; }
    public int PolicyVersion { get; init; }
    public CodexFlow.Core.Remote.ControlChannel ControlChannel { get; init; } = CodexFlow.Core.Remote.ControlChannel.Local;
    public CodexFlow.Core.Remote.RemotePrincipalStamp? RemoteStamp { get; init; }

    public ExecutionDecision Validate()
    {
        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(SessionId))
        {
            return ExecutionDecision.Deny(ExecutionReasonCodes.InvalidTrustedContext, "user/session must be set by the host.");
        }

        if (Origin == ExecutionOrigin.DailyChat)
        {
            if (string.IsNullOrWhiteSpace(TurnId) || string.IsNullOrWhiteSpace(InvocationId))
            {
                return ExecutionDecision.Deny(ExecutionReasonCodes.InvalidTrustedContext, "DailyChat context must specify TurnId and InvocationId.");
            }
        }
        else if (string.IsNullOrWhiteSpace(JobId) || string.IsNullOrWhiteSpace(TaskId) || string.IsNullOrWhiteSpace(AttemptId))
        {
            return ExecutionDecision.Deny(ExecutionReasonCodes.InvalidTrustedContext, "user/session/job/task/attempt must all be set by the host.");
        }

        if (string.IsNullOrWhiteSpace(WorkspaceRoot) || !Path.IsPathRooted(WorkspaceRoot))
        {
            return ExecutionDecision.Deny(ExecutionReasonCodes.InvalidTrustedContext, "WorkspaceRoot must be a trusted absolute path.");
        }

        var now = DateTimeOffset.UtcNow;
        if (NotAfter is { } na && na <= now)
        {
            return ExecutionDecision.Deny(ExecutionReasonCodes.Timeout, "Invocation context already expired.");
        }

        if (Deadline is { } d && d <= now)
        {
            return ExecutionDecision.Deny(ExecutionReasonCodes.Timeout, "Execution deadline already passed.");
        }

        return ExecutionDecision.Allow();
    }
}

public interface ITrustedExecutionContextAccessor
{
    TrustedExecutionContext? Current { get; }

    IDisposable Begin(TrustedExecutionContext context);
}

/// <summary>AsyncLocal-backed accessor. All instances share one ambient slot so any tool instance sees the host-bound context.</summary>
public sealed class TrustedExecutionContextAccessor : ITrustedExecutionContextAccessor
{
    private static readonly AsyncLocal<TrustedExecutionContext?> Slot = new();

    public TrustedExecutionContext? Current => Slot.Value;

    public IDisposable Begin(TrustedExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = Slot.Value;
        Slot.Value = context;
        return new Restore(previous);
    }

    private sealed class Restore(TrustedExecutionContext? previous) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Slot.Value = previous;
            }
        }
    }
}

// ───────────────────────── Service surface ─────────────────────────

public enum IsolatedCommandStatus
{
    Completed,
    Failed,
    TimedOut,
    Canceled,
    Denied,
    Unavailable,
    ResourceExceeded
}

public sealed record CommandOutputLine(string Stream, string Text);

/// <param name="PinToBaseCommit">
/// When true the snapshot is built from the immutable git object <c>ctx.BaseCommit</c> (git ls-tree + cat-file with object-id verification; no checkout, no archive),
/// not from the live worktree. Export is refused in this mode. Use for verification.
/// </param>
public sealed record IsolatedSessionOptions(
    string Image,
    bool StageWorkspace = true,
    IReadOnlyList<string>? EvidenceGlobs = null,
    bool PinToBaseCommit = false,
    IReadOnlyList<IsolatedInputDirectory>? Inputs = null,
    GuestGitMode GuestGit = GuestGitMode.None);

public enum GuestGitMode
{
    /// <summary>No repository inside the guest.</summary>
    None = 0,

    /// <summary>Create the independent guest repository when the image has git; otherwise continue without one.</summary>
    IfAvailable,

    /// <summary>Create it or refuse the session (Unavailable) when the image has no git.</summary>
    Required
}

/// <summary>
/// Independent repository created INSIDE the guest from the private snapshot (never from the host repository): git dir
/// /tmp/cfgit with GIT_WORK_TREE=/workspace, so there is no ".git" in the workspace to export or to leak a host path.
/// Created from an empty template (no hooks), no remotes, no credentials, no system/global config.
/// </summary>
/// <param name="BaselineCommit">Commit id of the baseline commit inside the guest repository.</param>
/// <param name="BaselineTree">Tree id of that commit.</param>
/// <param name="HostBaseCommit">The host commit the (pinned) snapshot was taken from, when known.</param>
/// <param name="HostBaseTree">The host commit's tree id, when known.</param>
/// <param name="TreeMatchesHostCommit">True when the guest baseline tree is identical to the host commit's tree (verifiable mapping).</param>
/// <param name="ManifestSha256">Hash over the sorted path:sha256 baseline manifest of the snapshot (also for live-worktree snapshots).</param>
public sealed record GuestGitInfo(
    string BaselineCommit,
    string BaselineTree,
    string? HostBaseCommit,
    string? HostBaseTree,
    bool TreeMatchesHostCommit,
    string ManifestSha256);

/// <summary>
/// Host directory copied READ-ONLY-by-convention into the guest at /tmp/<see cref="GuestSubPath"/> (e.g. a skill folder).
/// It is copied like the workspace (no links, no .git, quotas), is never exported, and never mounted.
/// </summary>
public sealed record IsolatedInputDirectory(
    string HostPath,
    string GuestSubPath,
    string? ExpectedManifestSha256 = null,
    string? ExpectedEntryRelativePath = null,
    string? ExpectedEntrySha256 = null);

public sealed record IsolatedCommandRequest(
    IReadOnlyList<string> Argv,
    string? Cwd = null,
    string? StdIn = null,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string>? Env = null,
    Action<CommandOutputLine>? OnOutput = null,
    IReadOnlyDictionary<string, string?>? TrustedMinimalEnv = null,
    bool PreResolvedExecutable = false,
    bool ForceUtf8Encoding = false,
    bool IsolateProcessGroup = false);

public sealed record IsolatedCommandResult(
    ExecutionDecision Decision,
    IsolatedCommandStatus Status,
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool Truncated,
    TimeSpan Elapsed,
    string Evidence,
    int OrphanedProcessesCleaned = 0)
{
    public static IsolatedCommandResult Refused(ExecutionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return Build(decision);
    }

    private static IsolatedCommandResult Build(ExecutionDecision decision)
        => new(decision,
            decision.Kind == ExecutionDecisionKind.Unavailable ? IsolatedCommandStatus.Unavailable : IsolatedCommandStatus.Denied,
            null, string.Empty, decision.Message, false, TimeSpan.Zero, string.Empty);
}

public sealed record EvidenceFile(string RelativePath, long Size, string Sha256, ReadOnlyMemory<byte> Content);

public sealed record ExportedChange(string Path, string Kind, string? Sha256, long Size);

public sealed record ExportRejection(string Path, string ReasonCode, string Message);

public sealed record IsolatedExportResult(
    ExecutionDecision Decision,
    IReadOnlyList<ExportedChange> Applied,
    IReadOnlyList<ExportRejection> Rejected)
{
    public static IsolatedExportResult Refused(ExecutionDecision decision)
        => new(decision, Array.Empty<ExportedChange>(), Array.Empty<ExportRejection>());
}

public sealed record ExecutionLayout(
    ExecutionBackend Backend,
    string WorkspaceRoot,
    string HomeDir,
    string TempDir,
    string InputsRoot,
    IReadOnlyDictionary<string, string> CacheMounts)
{
    public static ExecutionLayout DockerDefault { get; } = new(
        ExecutionBackend.Docker,
        "/workspace",
        "/tmp/home",
        "/tmp",
        "/tmp",
        new Dictionary<string, string>(StringComparer.Ordinal));
}

public interface IIsolatedExecutionSession : IAsyncDisposable
{
    string SessionHandle { get; }

    ExecutionLayout Layout => ExecutionLayout.DockerDefault;

    /// <summary>The independent guest repository, when one was requested and created.</summary>
    GuestGitInfo? GuestGit => null;

    Task<IsolatedCommandResult> RunAsync(IsolatedCommandRequest request, CancellationToken ct);

    Task<IsolatedExportResult> ExportScopedChangesAsync(CancellationToken ct);

    Task<IReadOnlyList<EvidenceFile>> ReadEvidenceAsync(IReadOnlyList<string> globs, CancellationToken ct);

    Task AbortAsync();
}

public sealed record IsolatedSessionOpenResult(ExecutionDecision Decision, IIsolatedExecutionSession? Session)
{
    /// <summary>Opens a session and transfers its lifetime to the caller receiving the result.</summary>
    internal static IsolatedSessionOpenResult Open(Func<IIsolatedExecutionSession> createSession)
    {
        var decision = ExecutionDecision.Allow();
        return new IsolatedSessionOpenResult(decision, createSession());
    }
}

public interface IIsolatedExecutionService
{
    ExecutionBackend ActiveBackend => ExecutionBackend.Docker;

    bool SupportsNetworkMode(ExecutionNetworkMode mode) => mode == ExecutionNetworkMode.None;

    Task<IsolatedSessionOpenResult> OpenSessionAsync(TrustedExecutionContext context, IsolatedSessionOptions options, CancellationToken ct);

    Task<IsolatedSessionOpenResult> OpenPinnedBackendSessionAsync(
        ExecutionBackend pinnedBackend,
        TrustedExecutionContext context,
        IsolatedSessionOptions options,
        CancellationToken ct)
    {
        if (ActiveBackend != pinnedBackend)
        {
            return Task.FromResult(new IsolatedSessionOpenResult(
                ExecutionDecision.Deny(
                    ExecutionReasonCodes.ApprovalInvalidated,
                    $"Execution backend changed from approved '{pinnedBackend}' to '{ActiveBackend}' before session start."),
                null));
        }

        return OpenSessionAsync(context, options, ct);
    }
}

/// <summary>Backend used when no isolation is configured: always refuses, never falls back to the host.</summary>
public sealed class UnavailableIsolatedExecutionService(string reason = "No isolated execution backend is configured.") : IIsolatedExecutionService
{
    public Task<IsolatedSessionOpenResult> OpenSessionAsync(TrustedExecutionContext context, IsolatedSessionOptions options, CancellationToken ct)
        => Task.FromResult(new IsolatedSessionOpenResult(
            ExecutionDecision.Unavailable(ExecutionReasonCodes.IsolationNotConfigured, reason), null));
}
