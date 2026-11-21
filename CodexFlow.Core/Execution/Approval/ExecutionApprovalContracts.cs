// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexFlow.Core.Execution.Approval;

public enum ExecutionApprovalDecisionKind
{
    Deny = 0,
    AllowOnce = 1,
    AllowSession = 2,
    AllowAlways = 3
}

public enum ApprovalOwnerKind
{
    Unspecified = 0,
    GatewayTurn = 1,
    BackgroundJob = 2
}

public sealed record PendingApprovalScriptSnapshot(
    string ApprovalId,
    string SessionId,
    string RequestDigest,
    string ScriptSha256,
    string? Shell,
    string ScriptText,
    int ScriptLength,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Deeply immutable snapshot of an untrusted-code execution request bound to a cryptographic SHA-256 digest.
/// </summary>
public sealed record ExecutionRequest
{
    /// <summary>
    /// Positive allow-list of utilities with fixed non-code-executing semantics. Every command outside this
    /// positive set (compilers, interpreters, build tools like <c>msbuild</c>/<c>dotnet</c>/<c>npm</c>, unknown binaries,
    /// or <c>git -c alias...</c>) is treated conservatively as capable of executing mutable workspace code and
    /// restricted to <c>allow_once</c>.
    /// </summary>
    private static readonly HashSet<string> FixedSemanticsNonCodeUtilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkdir", "cp", "mv", "ls", "dir", "cat", "head", "tail", "wc", "echo", "pwd", "which"
    };

    private readonly ReadOnlyCollection<string> _interpreterChain = ReadOnlyCollection<string>.Empty;
    private readonly ReadOnlyCollection<string> _argv = ReadOnlyCollection<string>.Empty;
    private readonly ReadOnlyCollection<KeyValuePair<string, string>> _environmentDelta = ReadOnlyCollection<KeyValuePair<string, string>>.Empty;

    public required string SessionId { get; init; }
    public string JobId { get; init; } = string.Empty;
    public required string TurnId { get; init; }
    public string WorkspaceRoot { get; init; } = string.Empty;
    public required string ToolName { get; init; }
    public required string Executable { get; init; }

    public required IReadOnlyList<string> InterpreterChain
    {
        get => _interpreterChain;
        init => _interpreterChain = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }

    public required IReadOnlyList<string> Argv
    {
        get => _argv;
        init => _argv = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }

    public required string Cwd { get; init; }
    public string? StdinSha256 { get; init; }
    public string? CodeSha256 { get; init; }
    public string? ScriptSha256 { get; init; }
    public string? InputsManifestSha256 { get; init; }

    public required IReadOnlyList<KeyValuePair<string, string>> EnvironmentDelta
    {
        get => _environmentDelta;
        init => _environmentDelta = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }

    public required ExecutionBackend Backend { get; init; }
    public required string Network { get; init; }
    public required string Filesystem { get; init; }
    public bool TrustedOriginVerified { get; init; }
    public ExecutionOrigin Origin { get; init; } = ExecutionOrigin.Unspecified;
    public int PolicyVersion { get; init; }
    public string? WrapperSha256 { get; init; }
    public string? ExportScopeSha256 { get; init; }
    public int TimeoutSeconds { get; init; }
    public string? ImageDigest { get; init; }
    public string? ShellKind { get; init; }
    public string? ScriptText { get; init; }
    public string? ScriptPreview { get; init; }
    public int? ScriptLength { get; init; }
    public string? BackendNotice { get; init; }
    public ApprovalOwnerKind OwnerKind { get; init; } = ApprovalOwnerKind.Unspecified;
    public string? OwnerId { get; init; }

    public bool ExecutesWorkspaceCode => !IsVerifiedNonCodeCommand(Executable, Argv);

    public IReadOnlyList<ExecutionApprovalDecisionKind> GetAllowedDecisions()
    {
        var list = new List<ExecutionApprovalDecisionKind>
        {
            ExecutionApprovalDecisionKind.Deny,
            ExecutionApprovalDecisionKind.AllowOnce
        };

        // Guard: run_shell executes arbitrary shell scripts and must NEVER be granted reusable allow_session or allow_always.
        if (ToolName.Equals("run_shell", StringComparison.OrdinalIgnoreCase))
        {
            return list;
        }

        var isSelfContainedSnippetOrSkill = ToolName.Equals("exec_code", StringComparison.OrdinalIgnoreCase) ||
                                            ToolName.Equals("skill", StringComparison.OrdinalIgnoreCase);

        // R2: ANY reusable grant (allow_session or allow_always) requires a verified trusted executable origin
        // so mutable binaries in the workspace or untrusted writable PATH directories can NEVER be reused after replacement!
        if (TrustedOriginVerified && (isSelfContainedSnippetOrSkill || !ExecutesWorkspaceCode))
        {
            list.Add(ExecutionApprovalDecisionKind.AllowSession);
        }

        // allow_always is restricted strictly to positively verified non-code commands from a trusted executable origin.
        if (!isSelfContainedSnippetOrSkill && !ExecutesWorkspaceCode && TrustedOriginVerified)
        {
            list.Add(ExecutionApprovalDecisionKind.AllowAlways);
        }

        return list;
    }

    /// <summary>
    /// Session-scoped digest binding SessionId, WorkspaceRoot, and all immutable execution inputs.
    /// TurnId is intentionally excluded here so <c>allow_session</c> can match identical requests across turns
    /// within the same session and workspace, while <c>allow_once</c> uses single-consumption tokens.
    /// </summary>
    public string ComputeRequestDigest() => ComputeDigestCore(includeSessionContext: true);

    public string ComputeSessionAgnosticDigest() => ComputeDigestCore(includeSessionContext: false);

    private string ComputeDigestCore(bool includeSessionContext)
    {
        var orderedEnv = _environmentDelta
            .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
            .ThenBy(static kv => kv.Value, StringComparer.Ordinal)
            .Select(static kv => new[] { kv.Key, kv.Value })
            .ToArray();

        var canonical = new ExecutionApprovalDigest
        {
            SessionId = includeSessionContext ? SessionId : null,
            WorkspaceRoot = includeSessionContext ? WorkspaceRoot : null,
            Tool = ToolName,
            Executable = Executable,
            TrustedOrigin = TrustedOriginVerified,
            InterpreterChain = _interpreterChain,
            Argv = _argv,
            Cwd = Cwd,
            StdinSha256 = StdinSha256,
            CodeSha256 = CodeSha256,
            ScriptSha256 = ScriptSha256,
            InputsManifestSha256 = InputsManifestSha256,
            EnvDelta = orderedEnv,
            Backend = Backend.ToString(),
            Network = Network,
            Filesystem = Filesystem,
            Origin = Origin.ToString(),
            PolicyVersion = PolicyVersion,
            WrapperSha256 = WrapperSha256,
            ExportScopeSha256 = ExportScopeSha256,
            TimeoutSeconds = TimeoutSeconds,
            ImageDigest = ImageDigest,
            ShellKind = ShellKind
        };

        var utf8 = JsonSerializer.SerializeToUtf8Bytes(canonical, ExecutionApprovalJsonContext.Canonical.ExecutionApprovalDigest);
        return Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant();
    }

    public static bool IsVerifiedNonCodeCommand(string executable, IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(executable);
        if (FixedSemanticsNonCodeUtilities.Contains(stem))
        {
            return true;
        }

        if (stem.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            if (AutoInWorkspaceEvaluator.IsStrictlyReadOnlyGitSyntax(argv, out _))
            {
                return true;
            }

            // Also allow pure read-only `git rev-parse` with safe inspection flags only (never branch/tag which may invoke editor/gpg)
            if (argv.Count > 0 && argv[0].Equals("rev-parse", StringComparison.Ordinal))
            {
                for (var i = 1; i < argv.Count; i++)
                {
                    var arg = argv[i];
                    if (arg.StartsWith('-'))
                    {
                        if (AutoInWorkspaceEvaluator.IsForbiddenGitFlag(arg) ||
                            arg is not ("--verify" or "--abbrev-ref" or "--show-toplevel" or "--short" or "--git-dir" or "--is-inside-work-tree"))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            return false;
        }

        if (stem.Equals("rg", StringComparison.OrdinalIgnoreCase) || stem.Equals("grep", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 0; i < argv.Count; i++)
            {
                var arg = argv[i];
                if (arg == "--")
                {
                    break;
                }

                if (arg.StartsWith("--pre", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-z", StringComparison.Ordinal) ||
                    arg.StartsWith("--search-zip", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("--hostname-bin", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    public static bool IsArbitraryCodeLauncher(string executable, IReadOnlyList<string> argv)
        => !IsVerifiedNonCodeCommand(executable, argv);

    public static string Sha256Text(string? content)
        => content is null
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public static string Sha256File(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static string ComputeDirectoryManifestSha256(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return string.Empty;
        }

        var root = Path.GetFullPath(directoryPath);
        var entries = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(static p => p, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var fileHash = Sha256File(file);
            entries.Add($"{rel}:{fileHash}");
        }

        return Sha256Text(string.Join('\n', entries));
    }
}

public sealed record PendingApprovalItem(
    string ApprovalId,
    string RequestDigest,
    string SessionId,
    string Tool,
    string Executable,
    IReadOnlyList<string> InterpreterChain,
    IReadOnlyList<string> Argv,
    string Cwd,
    string? StdinSha256,
    string? CodeSha256,
    string? ScriptSha256,
    string? InputsManifestSha256,
    string Backend,
    string Network,
    string Filesystem,
    string Reason,
    bool ExecutesWorkspaceCode,
    string? WorkspaceCodeWarning,
    IReadOnlyList<string> AllowedDecisions,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? ScriptPreview = null,
    int? ScriptLength = null,
    string? Shell = null,
    string? BackendNotice = null,
    string? Origin = null);

public sealed record ExecutionApprovalDecisionSubmission(
    string RequestDigest,
    ExecutionApprovalDecisionKind Decision,
    string? DecidedBy = null,
    string? Note = null);

public enum ApprovalSubmissionOutcome
{
    Resolved = 0,
    NotFound = 1,
    DigestMismatch = 2,
    DecisionNotAllowed = 3,
    AlreadyResolvedOrExpired = 4,
    TransientFailure = 5
}

public sealed record ApprovalSubmissionResult(
    ApprovalSubmissionOutcome Outcome,
    string Message,
    PendingApprovalItem? Item = null);

public sealed record ExecutionAuthorizationContext(
    ExecutionRequest Request,
    string WorkspaceRoot,
    string StagingRoot,
    string StagingWorkspaceDir,
    string? GuestGitDir,
    Func<bool>? VerifyStagedArtifactsUnchanged = null,
    Func<ExecutionRequest>? RefreshRequestOnInvalidate = null,
    Func<ExecutionBackend, ExecutionAuthorizationContext?>? RebuildForBackend = null);

public sealed record ExecutionAuthorizationResult(
    ExecutionDecision Decision,
    IReadOnlyList<string> EffectiveArgv,
    string EffectiveExecutable,
    IReadOnlyDictionary<string, string?>? ForcedEnvironment = null,
    bool RequiresGuestGit = false,
    ExecutionBackend ApprovedBackend = ExecutionBackend.Docker,
    bool IsPreResolvedExecutable = false);

public sealed class AmbientBackgroundJobApprovalContext : IDisposable
{
    private static readonly AsyncLocal<AmbientBackgroundJobApprovalContext?> Slot = new();

    private readonly CancellationTokenSource _suspendCts;
    private readonly TaskCompletionSource<bool> _persistenceBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AmbientBackgroundJobApprovalContext(string jobId, string resumeToken, CancellationToken parentToken)
    {
        JobId = jobId;
        ResumeToken = resumeToken;
        _suspendCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
    }

    public string JobId { get; }
    public string ResumeToken { get; }
    public CancellationToken EffectiveToken => _suspendCts.Token;
    public PendingApprovalItem? SuspendedApproval { get; private set; }
    public Task<bool> PersistenceReadyTask => _persistenceBarrier.Task;

    public static AmbientBackgroundJobApprovalContext? Current => Slot.Value;

    public static IDisposable BeginScope(
        string jobId,
        string resumeToken,
        CancellationToken parentToken,
        out AmbientBackgroundJobApprovalContext scopeContext)
    {
        var prev = Slot.Value;
        scopeContext = new AmbientBackgroundJobApprovalContext(jobId, resumeToken, parentToken);
        Slot.Value = scopeContext;
        return new Restore(prev, scopeContext);
    }

    public void TriggerSuspension(PendingApprovalItem approval)
    {
        SuspendedApproval = approval;
        try
        {
            _suspendCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void NotifyPersistenceCommitted() => _persistenceBarrier.TrySetResult(true);

    public void NotifyPersistenceAborted() => _persistenceBarrier.TrySetResult(false);

    public void Dispose()
    {
        _persistenceBarrier.TrySetResult(false);
        _suspendCts.Dispose();
    }

    private sealed class Restore(AmbientBackgroundJobApprovalContext? previous, AmbientBackgroundJobApprovalContext owned) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Slot.Value = previous;
                owned.Dispose();
            }
        }
    }
}

public enum SuspendedJobResolutionStatus
{
    Applied = 0,
    AlreadyLeftWaitingUser = 1,
    RetryableFailure = 2
}

public interface IBackgroundJobApprovalResumer
{
    Task<bool> OnSuspendedApprovalResolvedAsync(
        string jobId,
        string resumeToken,
        PendingApprovalItem item,
        ExecutionApprovalDecisionSubmission submission,
        CancellationToken ct);

    async Task<SuspendedJobResolutionStatus> TryApplyResolutionAsync(
        string jobId,
        string resumeToken,
        PendingApprovalItem item,
        ExecutionApprovalDecisionSubmission submission,
        CancellationToken ct)
        => await OnSuspendedApprovalResolvedAsync(jobId, resumeToken, item, submission, ct).ConfigureAwait(false)
            ? SuspendedJobResolutionStatus.Applied
            : SuspendedJobResolutionStatus.RetryableFailure;
}

public static class ControlPlaneAuthConstants
{
    public const string PlaneClaimType = "codexflow:plane";
    public const string PlaneNormal = "normal";
    public const string PlaneControl = "control";
    public const string ControlPlanePolicy = "ControlPlane";

    public static bool IsControlPlanePrincipal(
        System.Security.Claims.ClaimsPrincipal? principal,
        IControlPlaneStateProvider? controlPlaneState = null)
    {
        if (controlPlaneState is { IsControlPlaneAvailable: false })
        {
            return false;
        }

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var planeClaim = principal.FindFirst(PlaneClaimType);
        if (planeClaim is not null)
        {
            return string.Equals(planeClaim.Value, PlaneControl, StringComparison.Ordinal);
        }

        // When no local IControlPlaneStateProvider is registered (remote multi-tenant gateway), require an explicit
        // ControlPlane/Admin role claim on a non-Local* authenticated scheme; ordinary non-admin principals or LocalToken without plane=control ALWAYS fail closed.
        if (controlPlaneState is not null)
        {
            return false;
        }

        var authType = principal.Identity.AuthenticationType;
        if (string.IsNullOrWhiteSpace(authType) ||
            authType.StartsWith("Local", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, "local", StringComparison.Ordinal))
        {
            return false;
        }

        return principal.IsInRole("Admin") || principal.IsInRole(ControlPlanePolicy);
    }
}

public interface IControlPlaneStateProvider
{
    bool IsControlPlaneAvailable { get; }

    bool SelfApprovalProtection { get; }

    string TokenDeliveryMode { get; }
}

public interface IExecutionApprovalBroker
{
    Task<ExecutionAuthorizationResult> AuthorizeAsync(
        ExecutionAuthorizationContext authContext,
        CancellationToken ct);

    Task<ApprovalSubmissionResult> SubmitDecisionAsync(
        string approvalId,
        ExecutionApprovalDecisionSubmission submission,
        CancellationToken ct = default);

    IReadOnlyList<PendingApprovalItem> GetPendingApprovals(string sessionId);

    PendingApprovalItem? GetPendingApprovalById(string approvalId);

    int GetPendingApprovalCount(string sessionId);

    void CancelSessionApprovals(string sessionId, string reason = "Session or turn cancelled.");

    void CancelOwnerApprovals(string sessionId, ApprovalOwnerKind ownerKind, string ownerId, string reason = "Owner turn cancelled.")
        => CancelSessionApprovals(sessionId, reason);

    bool TryGetPendingApprovalScript(string approvalId, string sessionId, out PendingApprovalScriptSnapshot? snapshot)
    {
        snapshot = null;
        return false;
    }

    void ClearSessionAuthorizations(string sessionId);
}
