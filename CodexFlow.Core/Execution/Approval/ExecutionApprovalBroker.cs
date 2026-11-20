// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Gateway;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.Execution.Approval;

/// <summary>
/// Central broker enforcing <see cref="ExecutionPermissionMode"/>, cryptographic <see cref="ExecutionRequest"/>
/// digest binding, full request reconstruction on backend/mode switches, pre-launch copy verification,
/// single-use <c>allow_once</c>, session-scoped <c>allow_session</c>, persistent <c>allow_always</c>,
/// timeout pausing, race-free background job suspension/resumption, and SSE / audit reporting.
/// </summary>
public sealed class ExecutionApprovalBroker : IExecutionApprovalBroker, IDisposable
{
    private const string WorkspaceCodeWarningText =
        "批准解释器、包管理器或构建工具命令等于信任它在执行那一刻从工作区读取到的项目代码（这些文件未包含在 requestDigest 中）。";

    private readonly IOptionsMonitor<ExecutionOptions> _options;
    private readonly IExecutionPermissionStore _permissionStore;
    private readonly IServiceProvider? _serviceProvider;
    private readonly ICodexGateway? _explicitGateway;
    private readonly IWorkflowAuditStore? _explicitAuditStore;
    private readonly IControlPlaneStateProvider? _explicitControlPlane;
    private readonly IBackgroundJobApprovalResumer? _explicitJobResumer;
    private readonly ILogger<ExecutionApprovalBroker> _logger;

    private readonly ConcurrentDictionary<string, PendingApprovalEntry> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (PendingApprovalEntry Entry, string Reason)> _pendingTerminations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessionAllowances = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string SessionId, string JobId, string RequestDigest), SingleUsePreApproval> _resumedOnceAllowances = new();
    private readonly ConcurrentDictionary<(string SessionId, string JobId), TaskCompletionSource> _inFlightResumePublishGates = new();
    private int _terminationPumpRunning;
    private int _disposed;

    public ExecutionApprovalBroker(
        IOptionsMonitor<ExecutionOptions> options,
        IExecutionPermissionStore permissionStore,
        ILogger<ExecutionApprovalBroker> logger,
        IServiceProvider? serviceProvider = null,
        ICodexGateway? gateway = null,
        IWorkflowAuditStore? auditStore = null,
        IControlPlaneStateProvider? controlPlane = null,
        IBackgroundJobApprovalResumer? jobResumer = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _permissionStore = permissionStore ?? throw new ArgumentNullException(nameof(permissionStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider;
        _explicitGateway = gateway;
        _explicitAuditStore = auditStore;
        _explicitControlPlane = controlPlane;
        _explicitJobResumer = jobResumer;
    }

    private ICodexGateway? Gateway => _explicitGateway ?? _serviceProvider?.GetService<ICodexGateway>();
    private IControlPlaneStateProvider? ControlPlane => _explicitControlPlane ?? _serviceProvider?.GetService<IControlPlaneStateProvider>();
    private IBackgroundJobApprovalResumer? JobResumer => _explicitJobResumer ?? _serviceProvider?.GetService<IBackgroundJobApprovalResumer>();

    /// <summary>Deterministic synchronization hook invoked right before awaiting <c>PersistenceReadyTask</c> in tests.</summary>
    internal Action<string>? OnAwaitingSuspendedPersistenceBarrierForTesting { get; set; }

    /// <summary>Deterministic synchronization hook invoked right before awaiting an active resume publish gate in tests.</summary>
    internal Action<string, string>? OnAwaitingResumePublishGateForTesting { get; set; }

    /// <summary>Deterministic synchronization hook invoked right after rollback releases in-flight claim and right before <c>finally</c> runs in tests.</summary>
    internal Action<string>? OnAfterRollbackBeforeFinallyForTesting { get; set; }

    public async Task<ExecutionAuthorizationResult> AuthorizeAsync(
        ExecutionAuthorizationContext authContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authContext);
        ct.ThrowIfCancellationRequested();

        var activeCtx = authContext;

        // B6-2: If a resumed WaitingUser job starts running before SubmitDecisionAsync finishes publishing
        // session/persistent grants after ResumeJobAsync committed Queued, hold at entry until grant publication finishes!
        var effectiveJobId = AmbientBackgroundJobApprovalContext.Current?.JobId ?? activeCtx.Request.JobId;
        if (!string.IsNullOrEmpty(effectiveJobId) &&
            _inFlightResumePublishGates.TryGetValue((activeCtx.Request.SessionId, effectiveJobId), out var pendingPublishGate))
        {
            OnAwaitingResumePublishGateForTesting?.Invoke(activeCtx.Request.SessionId, effectiveJobId);
            await pendingPublishGate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        // Loop up to 4 attempts so that if permission mode / backend changes while waiting, or if staged copy hashes
        // change before launch, the old request is invalidated and rebuilt from scratch for the new active backend.
        for (var cycle = 0; cycle < 4; cycle++)
        {
            ct.ThrowIfCancellationRequested();

            var opts = _options.CurrentValue;
            var rawModeSnapshot = opts.PermissionMode;
            var backendSnapshot = opts.Backend;

            if (cycle > 0 || activeCtx.Request.Backend != backendSnapshot)
            {
                if (activeCtx.RebuildForBackend is not null)
                {
                    var rebuilt = activeCtx.RebuildForBackend(backendSnapshot);
                    if (rebuilt is null)
                    {
                        return Refused(activeCtx.Request, backendSnapshot, ExecutionDecision.Deny(
                            ExecutionReasonCodes.InvalidRequest,
                            $"Command cannot be resolved for switched execution backend '{backendSnapshot}'."));
                    }

                    activeCtx = rebuilt;
                }
                else if (cycle > 0 && activeCtx.RefreshRequestOnInvalidate is not null)
                {
                    activeCtx = activeCtx with { Request = activeCtx.RefreshRequestOnInvalidate() };
                }
                else if (activeCtx.Request.Backend != backendSnapshot)
                {
                    return Refused(activeCtx.Request, backendSnapshot, ExecutionDecision.Deny(
                        ExecutionReasonCodes.ApprovalInvalidated,
                        $"Execution backend changed from '{activeCtx.Request.Backend}' to '{backendSnapshot}'."));
                }
            }

            var request = activeCtx.Request;
            var modeSnapshot = EffectivePermissionModeForRequest(request, rawModeSnapshot);

            if (modeSnapshot == ExecutionPermissionMode.Deny)
            {
                await RecordAuditAsync(request, "denied_by_mode", "system", "PermissionMode=Deny", ct).ConfigureAwait(false);
                return Refused(request, backendSnapshot, ExecutionDecision.Deny(
                    ExecutionReasonCodes.PermissionDenied,
                    "Execution is disabled in read-only analysis mode (PermissionMode=Deny)."));
            }

            if (modeSnapshot == ExecutionPermissionMode.FullAuto)
            {
                if (!VerifyArtifactsOrInvalidate(activeCtx))
                {
                    return Refused(request, backendSnapshot, ExecutionDecision.Deny(
                        ExecutionReasonCodes.ApprovalInvalidated,
                        "Staged script or input files changed before execution."));
                }

                await RecordAuditAsync(
                    request,
                    "auto_allowed_full_auto",
                    "system",
                    "FullAuto mode active: trusts arbitrary code executable by the model.",
                    ct).ConfigureAwait(false);
                return Allowed(request, backendSnapshot, "allowed (FullAuto: arbitrary code trusted)");
            }

            var requestDigest = request.ComputeRequestDigest();
            var allowedDecisions = request.GetAllowedDecisions();

            // Check single-use pre-approval granted while a background job was suspended in WaitingUser.
            // Bound to (SessionId, JobId, RequestDigest, ApprovedBackend, ExpiresAtUtc).
            if (!string.IsNullOrWhiteSpace(request.JobId))
            {
                var ticketKey = (request.SessionId, request.JobId, requestDigest);
                if (_resumedOnceAllowances.TryRemove(ticketKey, out var onceTicket))
                {
                    if (onceTicket.IsValidFor(backendSnapshot, DateTimeOffset.UtcNow) &&
                        onceTicket.TryConsumeOnce() &&
                        VerifyArtifactsOrInvalidate(activeCtx))
                    {
                        await RecordAuditAsync(request, "allowed_by_resumed_once_grant", onceTicket.DecidedBy, onceTicket.Note, ct).ConfigureAwait(false);
                        return Allowed(request, backendSnapshot, "allowed (allow_once resumed)");
                    }
                }
            }

            // Check session-scoped allowance (exact RequestDigest match within the same session and backend).
            if (allowedDecisions.Contains(ExecutionApprovalDecisionKind.AllowSession) &&
                _sessionAllowances.TryGetValue(request.SessionId, out var sessionDigests) &&
                sessionDigests.ContainsKey(requestDigest))
            {
                if (!VerifyArtifactsOrInvalidate(activeCtx))
                {
                    sessionDigests.TryRemove(requestDigest, out _);
                    continue;
                }

                await RecordAuditAsync(request, "allowed_by_session_grant", "session_cache", null, ct).ConfigureAwait(false);
                return Allowed(request, backendSnapshot, "allowed (allow_session)");
            }

            // Check persistent allow_always rule (exact session-agnostic digest + trusted origin required).
            if (allowedDecisions.Contains(ExecutionApprovalDecisionKind.AllowAlways))
            {
                var agnosticDigest = request.ComputeSessionAgnosticDigest();
                if (_permissionStore.IsAllowedAlways(agnosticDigest))
                {
                    if (!VerifyArtifactsOrInvalidate(activeCtx))
                    {
                        continue;
                    }

                    await RecordAuditAsync(request, "allowed_by_always_rule", "permissions_store", null, ct).ConfigureAwait(false);
                    return Allowed(request, backendSnapshot, "allowed (allow_always)");
                }
            }

            // Evaluate AutoInWorkspace strict read-only allow-list when configured.
            var askReason = "PermissionMode=Ask requires interactive user approval.";
            if (modeSnapshot == ExecutionPermissionMode.AutoInWorkspace)
            {
                var sessionTemp = Path.Combine(activeCtx.StagingRoot, "auto-tmp", request.SessionId);
                var eval = AutoInWorkspaceEvaluator.Evaluate(
                    request.ToolName,
                    request.Executable,
                    request.Argv,
                    activeCtx.WorkspaceRoot,
                    activeCtx.StagingRoot,
                    activeCtx.StagingWorkspaceDir,
                    activeCtx.GuestGitDir,
                    sessionTemp,
                    opts.TrustedExecutablePaths.ToArray(),
                    request.Backend);

                if (eval.AutoAllowed)
                {
                    await RecordAuditAsync(request, "auto_allowed_in_workspace", "system", eval.Reason, ct).ConfigureAwait(false);
                    return new ExecutionAuthorizationResult(
                        ExecutionDecision.Allow(),
                        eval.RewrittenArgs,
                        eval.ResolvedExecutable,
                        eval.ForcedEnvironment,
                        eval.RequiresGuestGit,
                        backendSnapshot,
                        IsPreResolvedExecutable: backendSnapshot == ExecutionBackend.Local);
                }

                askReason = eval.Reason;
            }

            // Interactive Ask path: verify control plane availability first.
            var controlPlane = ControlPlane;
            if (controlPlane is { IsControlPlaneAvailable: false })
            {
                await RecordAuditAsync(
                    request,
                    ExecutionReasonCodes.ApprovalUnavailable,
                    "system",
                    "Control plane unavailable in standalone Local mode.",
                    ct).ConfigureAwait(false);
                return Refused(request, backendSnapshot, ExecutionDecision.Unavailable(
                    ExecutionReasonCodes.ApprovalUnavailable,
                    "请使用托管客户端，或切换到 Docker 后端（当前独立运行模式未启用本地控制面，无法交互式审批）。"));
            }

            var gateway = Gateway;
            var hasSubscribers = gateway?.HasActiveSubscribers(request.SessionId) ?? true;
            var ambientBgJob = AmbientBackgroundJobApprovalContext.Current;
            var shouldSuspendUnsubscribedJob = !hasSubscribers &&
                                               ambientBgJob is not null &&
                                               (opts.SuspendUnsubscribedBackgroundJobs
                                                ?? string.Equals(controlPlane?.TokenDeliveryMode, "Pipe", StringComparison.OrdinalIgnoreCase));

            var waitTimeout = hasSubscribers ? opts.ApprovalTimeout : opts.HeadlessApprovalTimeout;
            if (waitTimeout <= TimeSpan.Zero && !shouldSuspendUnsubscribedJob)
            {
                await RecordAuditAsync(request, ExecutionReasonCodes.ApprovalUnavailable, "system", "Headless approval timeout is zero.", ct).ConfigureAwait(false);
                return Refused(request, backendSnapshot, ExecutionDecision.Unavailable(
                    ExecutionReasonCodes.ApprovalUnavailable,
                    "No interactive subscriber is connected to approve this execution request."));
            }

            var now = DateTimeOffset.UtcNow;
            var approvalId = $"appr-{Guid.NewGuid():N}";
            var itemExpiresAt = shouldSuspendUnsubscribedJob
                ? now.Add(opts.ApprovalTimeout > TimeSpan.Zero ? opts.ApprovalTimeout : TimeSpan.FromHours(24))
                : now.Add(waitTimeout);

            var warningText = !string.IsNullOrWhiteSpace(request.BackendNotice)
                ? request.BackendNotice
                : (request.ExecutesWorkspaceCode ? WorkspaceCodeWarningText : null);

            var item = new PendingApprovalItem(
                ApprovalId: approvalId,
                RequestDigest: requestDigest,
                SessionId: request.SessionId,
                Tool: request.ToolName,
                Executable: request.Executable,
                InterpreterChain: request.InterpreterChain,
                Argv: request.Argv,
                Cwd: request.Cwd,
                StdinSha256: request.StdinSha256,
                CodeSha256: request.CodeSha256,
                ScriptSha256: request.ScriptSha256,
                InputsManifestSha256: request.InputsManifestSha256,
                Backend: request.Backend.ToString(),
                Network: request.Network,
                Filesystem: request.Filesystem,
                Reason: askReason,
                ExecutesWorkspaceCode: request.ExecutesWorkspaceCode,
                WorkspaceCodeWarning: warningText,
                AllowedDecisions: allowedDecisions.Select(ToWireDecision).ToArray(),
                CreatedAtUtc: now,
                ExpiresAtUtc: itemExpiresAt,
                ScriptPreview: request.ScriptPreview,
                ScriptLength: request.ScriptLength,
                Shell: request.ShellKind,
                BackendNotice: request.BackendNotice,
                Origin: request.Origin == ExecutionOrigin.Unspecified ? null : request.Origin.ToString());

            var entry = new PendingApprovalEntry(item, request, allowedDecisions, backendSnapshot, modeSnapshot);

            // R4: Pre-bind zero-wait background job suspension BEFORE exposing the entry in _pending or attaching the config watcher
            // so concurrent early SubmitDecisionAsync calls ALWAYS take the suspended-job barrier path!
            if (shouldSuspendUnsubscribedJob && waitTimeout <= TimeSpan.Zero)
            {
                entry.PresetZeroWaitSuspension(ambientBgJob!);
            }

            // B3 (Round 3): Attach configuration watcher directly to `entry` so its lifetime spans BOTH foreground waits
            // AND suspended WaitingUser background jobs until the entry is resolved, cancelled, expired, or invalidated!
            entry.AttachConfigWatcher(_options.OnChange((updated, optionName) =>
            {
                _ = optionName;
                var updatedEffectiveMode = EffectivePermissionModeForRequest(request, updated.PermissionMode);
                if (updated.Backend != backendSnapshot || updatedEffectiveMode != modeSnapshot)
                {
                    if (entry.InvalidateForConfigChangeAndCheckShouldTerminate())
                    {
                        _pending.TryRemove(approvalId, out _);
                        EnqueueSystemTermination(approvalId, entry, "Approval invalidated due to execution backend or permission mode change.");
                    }
                    else
                    {
                        _pending.TryRemove(approvalId, out _);
                    }
                }
            }));

            _pending[approvalId] = entry;
            if (!entry.TryCommitExposure(_options))
            {
                _pending.TryRemove(approvalId, out _);
                entry.DisposeWatcher();
                continue;
            }

            if (shouldSuspendUnsubscribedJob && waitTimeout <= TimeSpan.Zero)
            {
                // B6-3: Schedule independent expiration timer so suspended WaitingUser jobs expire into the termination queue
                // even when zero SSE subscribers or HTTP approval queries ever arrive!
                ScheduleSuspendedExpirationTimer(approvalId, entry);
            }

            await PublishSseAsync(request.SessionId, GatewaySseEventType.ToolApprovalRequest, new
            {
                approvalId = item.ApprovalId,
                requestDigest = item.RequestDigest,
                sessionId = item.SessionId,
                tool = item.Tool,
                executable = item.Executable,
                interpreterChain = item.InterpreterChain,
                argv = item.Argv,
                cwd = item.Cwd,
                stdinSha256 = item.StdinSha256,
                codeSha256 = item.CodeSha256,
                scriptSha256 = item.ScriptSha256,
                inputsManifestSha256 = item.InputsManifestSha256,
                backend = item.Backend,
                network = item.Network,
                filesystem = item.Filesystem,
                reason = item.Reason,
                executesWorkspaceCode = item.ExecutesWorkspaceCode,
                workspaceCodeWarning = item.WorkspaceCodeWarning,
                allowedDecisions = item.AllowedDecisions,
                expiresAt = item.ExpiresAtUtc,
                scriptPreview = item.ScriptPreview,
                scriptLength = item.ScriptLength,
                shell = item.Shell,
                backendNotice = item.BackendNotice,
                origin = item.Origin
            }, ct).ConfigureAwait(false);

            if (shouldSuspendUnsubscribedJob && waitTimeout <= TimeSpan.Zero)
            {
                ambientBgJob!.TriggerSuspension(item);
                throw new OperationCanceledException("Background job suspended for interactive approval.", ambientBgJob.EffectiveToken);
            }

            using var pauseGuard = AmbientTurnTimeoutController.Current?.PauseTimers();
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(waitTimeout);

            var keepEntryForSuspendedJob = false;
            PendingResolution resolution;
            try
            {
                using var reg = waitCts.Token.Register(() =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        entry.CancelByCaller("turn_cancelled");
                    }
                    else if (shouldSuspendUnsubscribedJob)
                    {
                        if (entry.TryTransitionToSuspendedForBackgroundJob(ambientBgJob!, _options))
                        {
                            ScheduleSuspendedExpirationTimer(approvalId, entry);
                        }
                    }
                    else
                    {
                        entry.Expire();
                    }
                });

                resolution = await entry.Completion.Task.ConfigureAwait(false);
                if (resolution.State == PendingResolutionState.SuspendedToBackgroundJob)
                {
                    keepEntryForSuspendedJob = true;
                }
            }
            finally
            {
                if (!keepEntryForSuspendedJob)
                {
                    _pending.TryRemove(approvalId, out _);
                    entry.DisposeWatcher();
                }
            }

            if (resolution.State == PendingResolutionState.SuspendedToBackgroundJob)
            {
                ambientBgJob!.TriggerSuspension(item);
                throw new OperationCanceledException("Background job suspended for interactive approval.", ambientBgJob.EffectiveToken);
            }

            if (resolution.State == PendingResolutionState.InvalidatedByConfigChange)
            {
                await PublishResolvedSseAsync(item, "invalidated", "Execution backend or permission mode changed while waiting.", ct).ConfigureAwait(false);
                continue;
            }

            if (resolution.State == PendingResolutionState.Cancelled)
            {
                await PublishResolvedSseAsync(item, "deny", resolution.Note ?? "Turn cancelled.", CancellationToken.None).ConfigureAwait(false);
                await RecordAuditAsync(request, "denied_cancelled", "system", resolution.Note, CancellationToken.None).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return Refused(request, backendSnapshot, ExecutionDecision.Deny(ExecutionReasonCodes.Canceled, "Execution approval cancelled."));
            }

            if (resolution.State == PendingResolutionState.Expired)
            {
                var code = hasSubscribers ? ExecutionReasonCodes.ApprovalExpired : ExecutionReasonCodes.ApprovalUnavailable;
                await PublishResolvedSseAsync(item, "deny", code, CancellationToken.None).ConfigureAwait(false);
                await RecordAuditAsync(request, code, "system", "Approval timed out.", CancellationToken.None).ConfigureAwait(false);
                return hasSubscribers
                    ? Refused(request, backendSnapshot, ExecutionDecision.Deny(ExecutionReasonCodes.ApprovalExpired, "Execution approval request timed out."))
                    : Refused(request, backendSnapshot, ExecutionDecision.Unavailable(
                        ExecutionReasonCodes.ApprovalUnavailable,
                        $"No interactive subscriber approved the request within {(int)waitTimeout.TotalSeconds}s."));
            }

            // Re-verify active backend & permission mode have not drifted right after resolution!
            var postResolveOpts = _options.CurrentValue;
            var postResolveEffectiveMode = EffectivePermissionModeForRequest(request, postResolveOpts.PermissionMode);
            if (postResolveOpts.Backend != backendSnapshot || postResolveEffectiveMode != modeSnapshot)
            {
                await PublishResolvedSseAsync(item, "invalidated", "Execution configuration changed before launch; re-evaluating.", ct).ConfigureAwait(false);
                continue;
            }

            // User submitted an explicit decision: enforce atomic single consumption.
            if (!entry.TryConsumeOnce())
            {
                return Refused(request, backendSnapshot, ExecutionDecision.Deny(ExecutionReasonCodes.InvalidOperation, "Approval token was already consumed."));
            }

            var submission = resolution.Submission!;
            if (submission.Decision == ExecutionApprovalDecisionKind.Deny)
            {
                await RecordAuditAsync(request, "denied_by_user", submission.DecidedBy ?? "user", submission.Note, ct).ConfigureAwait(false);
                return Refused(request, backendSnapshot, ExecutionDecision.Deny(
                    ExecutionReasonCodes.PermissionDenied,
                    string.IsNullOrWhiteSpace(submission.Note) ? "User denied execution." : $"User denied execution: {submission.Note}"));
            }

            // Re-hash staged script / input copies right before launch to prevent TOCTOU tampering while waiting for approval.
            if (!VerifyArtifactsOrInvalidate(activeCtx))
            {
                _logger.LogWarning(
                    "Staged artifacts for session {SessionId} changed while awaiting approval {ApprovalId}; invalidating and re-requesting.",
                    request.SessionId,
                    approvalId);
                await PublishResolvedSseAsync(item, "invalidated", "Staged script or input hash changed before launch.", ct).ConfigureAwait(false);
                await RecordAuditAsync(request, "invalidated_hash_mismatch", "system", "Staged copy modified during approval wait.", ct).ConfigureAwait(false);
                continue;
            }

            ApplyPersistedOrSessionGrant(request, requestDigest, submission);

            await RecordAuditAsync(
                request,
                ToWireDecision(submission.Decision),
                submission.DecidedBy ?? "control_plane",
                submission.Note,
                ct).ConfigureAwait(false);

            return Allowed(request, backendSnapshot, $"allowed ({ToWireDecision(submission.Decision)})");
        }

        return Refused(activeCtx.Request, _options.CurrentValue.Backend, ExecutionDecision.Deny(
            ExecutionReasonCodes.ApprovalInvalidated,
            "Approval could not stabilize after repeated configuration or artifact changes."));
    }

    public async Task<ApprovalSubmissionResult> SubmitDecisionAsync(
        string approvalId,
        ExecutionApprovalDecisionSubmission submission,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentNullException.ThrowIfNull(submission);

        if (!_pending.TryGetValue(approvalId, out var entry))
        {
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.NotFound,
                "Approval request was not found, already resolved, or expired.");
        }

        if (DateTimeOffset.UtcNow >= entry.Item.ExpiresAtUtc)
        {
            entry.Expire();
            _pending.TryRemove(approvalId, out _);
            await EnqueueSystemTerminationAndTryOnceAsync(approvalId, entry, "Approval expired.").ConfigureAwait(false);
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.AlreadyResolvedOrExpired,
                "Approval request has expired.");
        }

        // Reject submission if active execution backend OR effective permission mode drifted since this request was created (B5 / B3).
        var currentOpts = _options.CurrentValue;
        if (!entry.MatchesExecutionConfig(currentOpts))
        {
            entry.InvalidateForConfigChange();
            _pending.TryRemove(approvalId, out _);
            await EnqueueSystemTerminationAndTryOnceAsync(approvalId, entry, "Approval invalidated due to configuration change.").ConfigureAwait(false);
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.AlreadyResolvedOrExpired,
                $"Execution configuration changed from '{entry.BackendSnapshot}/{entry.ModeSnapshot}' to '{currentOpts.Backend}/{EffectivePermissionModeForRequest(entry.Request, currentOpts.PermissionMode)}'; approval invalidated.",
                entry.Item);
        }

        if (!string.Equals(entry.Item.RequestDigest, submission.RequestDigest, StringComparison.OrdinalIgnoreCase))
        {
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.DigestMismatch,
                "Submitted requestDigest does not match the immutable pending execution request.",
                entry.Item);
        }

        if (!entry.AllowedDecisions.Contains(submission.Decision))
        {
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.DecisionNotAllowed,
                $"Decision '{ToWireDecision(submission.Decision)}' is not permitted for this command.",
                entry.Item);
        }

        // B3 (Round 4): Single atomic lock decision choosing between immediate foreground resolution,
        // routing to the suspended background-job barrier (NEVER deleting _pending if it just transitioned!), or terminal failure.
        var route = entry.TryResolveForegroundOrRouteToSuspended(submission, _options, out var suspendedCtx, out var fgFailureReason);
        if (route == ForegroundResolutionRoute.ResolvedForeground)
        {
            _pending.TryRemove(approvalId, out _);
            entry.DisposeWatcher();
            await PublishResolvedSseAsync(entry.Item, ToWireDecision(submission.Decision), submission.Note, ct).ConfigureAwait(false);
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.Resolved,
                "Approval decision recorded.",
                entry.Item);
        }

        if (route == ForegroundResolutionRoute.TerminalFailure)
        {
            _pending.TryRemove(approvalId, out _);
            await EnqueueSystemTerminationAndTryOnceAsync(approvalId, entry, fgFailureReason).ConfigureAwait(false);
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.AlreadyResolvedOrExpired,
                fgFailureReason,
                entry.Item);
        }

        // MustUseSuspendedBarrier: Wait until BackgroundJobRunner has durably committed WaitingUser + ResumeToken to SQLite!
        OnAwaitingSuspendedPersistenceBarrierForTesting?.Invoke(approvalId);
        bool committed;
        try
        {
            committed = await suspendedCtx!.PersistenceReadyTask
                .WaitAsync(TimeSpan.FromSeconds(30), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller HTTP request cancelled: if the entry expired or config drifted meanwhile, still enqueue independent system termination!
            if (entry.TryCheckTerminalFailure(_options, out var cancelDriftReason))
            {
                _pending.TryRemove(approvalId, out _);
                EnqueueSystemTermination(approvalId, entry, cancelDriftReason);
            }

            throw;
        }

        if (!committed)
        {
            _pending.TryRemove(approvalId, out _);
            entry.DisposeWatcher();
            return new ApprovalSubmissionResult(
                ApprovalSubmissionOutcome.AlreadyResolvedOrExpired,
                "Suspended background job failed to persist WaitingUser state or was cancelled.",
                entry.Item);
        }

        // B3-1 (Round 5): Two-phase user decision claim — Phase 1 marks `_userDecisionInFlight = true` WITHOUT prematurely
        // marking `_suspendedJobResolved` or `_suspendedTerminationCompleted` before the DB transition succeeds!
        if (!entry.TryBeginSuspendedUserDecision(_options, out var claimFailureReason, out var requiresSystemTermination, out var isTransientBusy))
        {
            if (requiresSystemTermination)
            {
                _pending.TryRemove(approvalId, out _);
                await EnqueueSystemTerminationAndTryOnceAsync(approvalId, entry, claimFailureReason).ConfigureAwait(false);
            }

            return new ApprovalSubmissionResult(
                isTransientBusy ? ApprovalSubmissionOutcome.TransientFailure : ApprovalSubmissionOutcome.AlreadyResolvedOrExpired,
                claimFailureReason,
                entry.Item);
        }

        var ticketKey = (entry.Request.SessionId, suspendedCtx.JobId, entry.Item.RequestDigest);
        var preMintedOnceTicket = false;
        if (submission.Decision == ExecutionApprovalDecisionKind.AllowOnce)
        {
            _resumedOnceAllowances[ticketKey] = new SingleUsePreApproval(
                submission.DecidedBy ?? "control_plane",
                submission.Note,
                entry.BackendSnapshot,
                DateTimeOffset.UtcNow.AddMinutes(30));
            preMintedOnceTicket = true;
        }

        // B6-2 & B7-2: Install a job-scoped authorization publish gate BEFORE ResumeJobAsync marks the DB row Queued,
        // and clean it up strictly BEFORE releasing `_userDecisionInFlight` AND strictly by matching the exact gate instance
        // so an older failed submission's `finally` can NEVER evict a newer retry's publish gate!
        TaskCompletionSource? publishGate = null;
        var publishGateReleased = 0;
        var publishGateKey = (entry.Request.SessionId, suspendedCtx.JobId);
        if (submission.Decision != ExecutionApprovalDecisionKind.Deny)
        {
            publishGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlightResumePublishGates[publishGateKey] = publishGate;
        }

        void ReleaseOwnedPublishGate()
        {
            if (publishGate is null || Interlocked.Exchange(ref publishGateReleased, 1) != 0)
            {
                return;
            }

            ((ICollection<KeyValuePair<(string SessionId, string JobId), TaskCompletionSource>>)_inFlightResumePublishGates)
                .Remove(new KeyValuePair<(string SessionId, string JobId), TaskCompletionSource>(publishGateKey, publishGate));
            publishGate.TrySetResult();
        }

        StagedAllowAlwaysReservation? stagedAlwaysReservation = null;
        void AbortStagedAlwaysIfPresent()
        {
            if (stagedAlwaysReservation is not { } reservation)
            {
                return;
            }

            stagedAlwaysReservation = null;
            _permissionStore.AbortStagedAllowAlways(reservation);
        }

        var jobResumedInDb = false;
        var degradedAlwaysToOnce = false;
        try
        {
            if (submission.Decision == ExecutionApprovalDecisionKind.AllowAlways)
            {
                // B8-1: Stage into uncommitted `stagedRules` FIRST (never active in `_digests` or `rules` before DB resume succeeds!).
                stagedAlwaysReservation = _permissionStore.StageAllowAlways(entry.Request, submission.Note);
            }

            if (JobResumer is { } resumer)
            {
                var status = await resumer.TryApplyResolutionAsync(
                    suspendedCtx.JobId,
                    suspendedCtx.ResumeToken,
                    entry.Item,
                    submission,
                    ct).ConfigureAwait(false);

                if (status == SuspendedJobResolutionStatus.AlreadyLeftWaitingUser)
                {
                    if (preMintedOnceTicket)
                    {
                        _resumedOnceAllowances.TryRemove(ticketKey, out _);
                    }

                    AbortStagedAlwaysIfPresent();
                    ReleaseOwnedPublishGate();
                    entry.MarkAlreadyLeftWaitingUser();
                    _pending.TryRemove(approvalId, out _);
                    _pendingTerminations.TryRemove(approvalId, out _);
                    entry.DisposeWatcher();
                    return new ApprovalSubmissionResult(
                        ApprovalSubmissionOutcome.AlreadyResolvedOrExpired,
                        "Suspended background job already left WaitingUser.",
                        entry.Item);
                }

                if (status != SuspendedJobResolutionStatus.Applied)
                {
                    if (preMintedOnceTicket)
                    {
                        _resumedOnceAllowances.TryRemove(ticketKey, out _);
                    }

                    AbortStagedAlwaysIfPresent();
                    // Release our publish gate BEFORE releasing `_userDecisionInFlight`!
                    ReleaseOwnedPublishGate();
                    var becameTerminal = RollbackUserDecisionOrScheduleTermination(approvalId, entry);
                    OnAfterRollbackBeforeFinallyForTesting?.Invoke(approvalId);
                    return new ApprovalSubmissionResult(
                        becameTerminal ? ApprovalSubmissionOutcome.AlreadyResolvedOrExpired : ApprovalSubmissionOutcome.TransientFailure,
                        becameTerminal
                            ? "Approval expired or invalidated while updating suspended background job."
                            : "Transient failure updating suspended background job; request remains retryable.",
                        entry.Item);
                }

                jobResumedInDb = true;
            }

            // Phase 2: DB transition durably succeeded! Commit grants & finalize entry BEFORE opening publishGate!
            if (submission.Decision == ExecutionApprovalDecisionKind.AllowSession)
            {
                ApplyPersistedOrSessionGrant(entry.Request, entry.Item.RequestDigest, submission);
            }
            else if (stagedAlwaysReservation is { } reservationToCommit)
            {
                stagedAlwaysReservation = null;
                try
                {
                    _ = _permissionStore.CommitStagedAllowAlways(reservationToCommit);
                }
                catch (Exception commitEx) when (jobResumedInDb)
                {
                    // DB row already transitioned to Queued, while persistent `rules` flush failed (in-memory `_digests` stays unpolluted!).
                    // Mint a single-use ticket strictly for this resumed job so the worker does not re-hang, and record degraded audit.
                    _logger.LogError(
                        commitEx,
                        "Failed to commit persistent allow_always rule after resuming job {JobId}; degrading this approval to single-use allow_once without activating persistent rule.",
                        suspendedCtx.JobId);
                    _resumedOnceAllowances[ticketKey] = new SingleUsePreApproval(
                        submission.DecidedBy ?? "control_plane",
                        submission.Note,
                        entry.BackendSnapshot,
                        DateTimeOffset.UtcNow.AddMinutes(30));
                    degradedAlwaysToOnce = true;
                }
            }

            entry.CommitSuspendedUserDecision();
            _pending.TryRemove(approvalId, out _);
            _pendingTerminations.TryRemove(approvalId, out _);
            entry.DisposeWatcher();
            ReleaseOwnedPublishGate();
        }
        catch (Exception ex)
        {
            if (preMintedOnceTicket)
            {
                _resumedOnceAllowances.TryRemove(ticketKey, out _);
            }

            AbortStagedAlwaysIfPresent();
            // Release our publish gate BEFORE releasing `_userDecisionInFlight`!
            ReleaseOwnedPublishGate();
            var becameTerminal = RollbackUserDecisionOrScheduleTermination(approvalId, entry);
            OnAfterRollbackBeforeFinallyForTesting?.Invoke(approvalId);
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning(ex, "Transient error applying user approval decision to suspended job {JobId}; entry returned to retryable state.", suspendedCtx.JobId);
            return new ApprovalSubmissionResult(
                becameTerminal ? ApprovalSubmissionOutcome.AlreadyResolvedOrExpired : ApprovalSubmissionOutcome.TransientFailure,
                becameTerminal
                    ? "Approval expired or invalidated while updating suspended background job."
                    : "Transient error updating suspended background job; request remains retryable.",
                entry.Item);
        }
        finally
        {
            ReleaseOwnedPublishGate();
        }

        var finalWireDecision = degradedAlwaysToOnce ? "allow_once" : ToWireDecision(submission.Decision);
        await PublishResolvedSseAsync(entry.Item, finalWireDecision, submission.Note, CancellationToken.None).ConfigureAwait(false);
        await RecordAuditAsync(
            entry.Request,
            degradedAlwaysToOnce ? "suspended_job_allow_always_degraded_to_once_on_store_io_error" : $"suspended_job_{ToWireDecision(submission.Decision)}",
            submission.DecidedBy ?? "control_plane",
            submission.Note,
            CancellationToken.None).ConfigureAwait(false);

        return new ApprovalSubmissionResult(
            ApprovalSubmissionOutcome.Resolved,
            degradedAlwaysToOnce
                ? "Background job resumed with single-use authorization because persistent allow_always rule could not be saved to disk."
                : "Approval decision recorded and background job updated.",
            entry.Item);
    }

    public IReadOnlyList<PendingApprovalItem> GetPendingApprovals(string sessionId)
    {
        SweepExpiredEntries();
        var now = DateTimeOffset.UtcNow;
        return _pending.Values
            .Where(e => string.Equals(e.Item.SessionId, sessionId, StringComparison.Ordinal) &&
                        e.Item.ExpiresAtUtc > now &&
                        e.IsStillAwaitingUser)
            .Select(e => e.Item)
            .OrderBy(i => i.CreatedAtUtc)
            .ToArray();
    }

    public PendingApprovalItem? GetPendingApprovalById(string approvalId)
    {
        if (string.IsNullOrWhiteSpace(approvalId) || !_pending.TryGetValue(approvalId, out var entry))
        {
            return null;
        }

        return entry.Item.ExpiresAtUtc > DateTimeOffset.UtcNow && entry.IsStillAwaitingUser
            ? entry.Item
            : null;
    }

    public int GetPendingApprovalCount(string sessionId)
    {
        SweepExpiredEntries();
        var now = DateTimeOffset.UtcNow;
        return _pending.Values.Count(e =>
            string.Equals(e.Item.SessionId, sessionId, StringComparison.Ordinal) &&
            e.Item.ExpiresAtUtc > now &&
            e.IsStillAwaitingUser);
    }

    public void CancelOwnerApprovals(string sessionId, ApprovalOwnerKind ownerKind, string ownerId, string reason = "Owner turn cancelled.")
    {
        if (string.IsNullOrWhiteSpace(sessionId) || ownerKind == ApprovalOwnerKind.Unspecified || string.IsNullOrWhiteSpace(ownerId))
        {
            return;
        }

        foreach (var (id, entry) in _pending)
        {
            if (string.Equals(entry.Item.SessionId, sessionId, StringComparison.Ordinal) &&
                entry.Request.OwnerKind == ownerKind &&
                string.Equals(entry.Request.OwnerId, ownerId, StringComparison.Ordinal))
            {
                entry.CancelByCaller(reason);
                if (_pending.TryRemove(id, out _))
                {
                    EnqueueSystemTermination(id, entry, reason);
                }
            }
        }
    }

    public bool TryGetPendingApprovalScript(string approvalId, string sessionId, out PendingApprovalScriptSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(approvalId) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            !_pending.TryGetValue(approvalId, out var entry))
        {
            return false;
        }

        if (!string.Equals(entry.Item.SessionId, sessionId, StringComparison.Ordinal) ||
            entry.Item.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            !entry.IsStillAwaitingUser ||
            entry.Request.ScriptText is null)
        {
            return false;
        }

        var text = entry.Request.ScriptText;
        var hash = entry.Request.ScriptSha256 ?? ExecutionRequest.Sha256Text(text);
        snapshot = new PendingApprovalScriptSnapshot(
            entry.Item.ApprovalId,
            entry.Item.SessionId,
            entry.Item.RequestDigest,
            hash,
            entry.Request.ShellKind,
            text,
            entry.Request.ScriptLength ?? text.Length,
            entry.Item.ExpiresAtUtc);
        return true;
    }

    public void CancelSessionApprovals(string sessionId, string reason = "Session or turn cancelled.")
    {
        foreach (var (id, entry) in _pending)
        {
            if (string.Equals(entry.Item.SessionId, sessionId, StringComparison.Ordinal))
            {
                entry.CancelByCaller(reason);
                if (_pending.TryRemove(id, out _))
                {
                    EnqueueSystemTermination(id, entry, reason);
                }
            }
        }

        foreach (var key in _resumedOnceAllowances.Keys)
        {
            if (string.Equals(key.SessionId, sessionId, StringComparison.Ordinal))
            {
                _resumedOnceAllowances.TryRemove(key, out _);
            }
        }
    }

    public void ClearSessionAuthorizations(string sessionId)
    {
        _sessionAllowances.TryRemove(sessionId, out _);
        CancelSessionApprovals(sessionId, "Session closed.");
    }

    private void SweepExpiredEntries()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, entry) in _pending)
        {
            if (entry.Item.ExpiresAtUtc <= now && _pending.TryRemove(id, out _))
            {
                entry.Expire();
                EnqueueSystemTermination(id, entry, "Approval expired.");
            }
        }

        foreach (var (key, ticket) in _resumedOnceAllowances)
        {
            if (ticket.ExpiresAtUtc <= now)
            {
                _resumedOnceAllowances.TryRemove(key, out _);
            }
        }

        if (!_pendingTerminations.IsEmpty)
        {
            _ = EnsureTerminationPumpAsync();
        }
    }

    private bool RollbackUserDecisionOrScheduleTermination(string approvalId, PendingApprovalEntry entry)
    {
        if (entry.AbortSuspendedUserDecision(_options, out var abortReason))
        {
            // Entry expired or config drifted while user decision was in flight -> move to independent system termination queue!
            _pending.TryRemove(approvalId, out _);
            EnqueueSystemTermination(approvalId, entry, abortReason);
            return true;
        }

        return false;
    }

    private void EnqueueSystemTermination(string approvalId, PendingApprovalEntry entry, string reason)
    {
        entry.DisposeWatcher();
        if (!entry.IsSuspendedToBackgroundJob(out _))
        {
            return;
        }

        _pendingTerminations.TryAdd(approvalId, (entry, reason));
        _ = EnsureTerminationPumpAsync();
    }

    private async Task EnqueueSystemTerminationAndTryOnceAsync(string approvalId, PendingApprovalEntry entry, string reason)
    {
        entry.DisposeWatcher();
        if (!entry.IsSuspendedToBackgroundJob(out _))
        {
            return;
        }

        _pendingTerminations.TryAdd(approvalId, (entry, reason));
        // Attempt one immediate host-scoped pass (NEVER tied to caller request CancellationToken); if transient DB error occurs,
        // entry stays in _pendingTerminations and EnsureTerminationPumpAsync continues retrying until DB reaches terminal state!
        if (await TryProcessSingleTerminationAsync(approvalId, entry, reason).ConfigureAwait(false) == TerminationAttemptOutcome.Completed)
        {
            _pendingTerminations.TryRemove(approvalId, out _);
        }
        else
        {
            _ = EnsureTerminationPumpAsync();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var (_, entry) in _pending)
        {
            entry.DisposeWatcher();
        }

        foreach (var (_, item) in _pendingTerminations)
        {
            item.Entry.DisposeWatcher();
        }

        foreach (var (_, gate) in _inFlightResumePublishGates)
        {
            gate.TrySetResult();
        }
    }

    private void ScheduleSuspendedExpirationTimer(string approvalId, PendingApprovalEntry entry)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var remaining = entry.Item.ExpiresAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            OnSuspendedEntryTimerExpired(approvalId, entry);
            return;
        }

        var dueMs = (long)Math.Ceiling(remaining.TotalMilliseconds);
        if (dueMs > int.MaxValue - 1)
        {
            dueMs = int.MaxValue - 1;
        }

        var timer = new Timer(
            static state =>
            {
                var (broker, id, targetEntry) = ((ExecutionApprovalBroker, string, PendingApprovalEntry))state!;
                broker.OnSuspendedEntryTimerExpired(id, targetEntry);
            },
            (this, approvalId, entry),
            dueTime: (int)Math.Max(1, dueMs),
            period: Timeout.Infinite);

        if (!entry.TryAttachExpiryTimer(timer))
        {
            timer.Dispose();
        }
    }

    private void OnSuspendedEntryTimerExpired(string approvalId, PendingApprovalEntry entry)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < entry.Item.ExpiresAtUtc)
        {
            ScheduleSuspendedExpirationTimer(approvalId, entry);
            return;
        }

        if (entry.TryExpireSuspendedFromTimer())
        {
            _pending.TryRemove(approvalId, out _);
            EnqueueSystemTermination(approvalId, entry, "Approval expired.");
        }
    }

    private async Task EnsureTerminationPumpAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _terminationPumpRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var backoffMs = 40;
            while (Volatile.Read(ref _disposed) == 0 && !_pendingTerminations.IsEmpty)
            {
                var sawReadyRetryableFailure = false;
                foreach (var (approvalId, item) in _pendingTerminations.ToArray())
                {
                    var outcome = await TryProcessSingleTerminationAsync(approvalId, item.Entry, item.Reason).ConfigureAwait(false);
                    if (outcome == TerminationAttemptOutcome.Completed)
                    {
                        _pendingTerminations.TryRemove(approvalId, out _);
                    }
                    else if (outcome == TerminationAttemptOutcome.RetryAfterBackoff)
                    {
                        sawReadyRetryableFailure = true;
                    }
                }

                if (_pendingTerminations.IsEmpty || !sawReadyRetryableFailure)
                {
                    // All remaining items (if any) are awaiting their own PersistenceReadyTask continuation wakeup!
                    break;
                }

                await Task.Delay(backoffMs).ConfigureAwait(false);
                backoffMs = Math.Min(250, backoffMs * 2);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _terminationPumpRunning, 0);
            if (Volatile.Read(ref _disposed) == 0 &&
                _pendingTerminations.Values.Any(v => v.Entry.IsReadyForTerminationPumpPass) &&
                Interlocked.CompareExchange(ref _terminationPumpRunning, 1, 0) == 0)
            {
                Interlocked.Exchange(ref _terminationPumpRunning, 0);
                _ = EnsureTerminationPumpAsync();
            }
        }
    }

    private enum TerminationAttemptOutcome
    {
        Completed = 0,
        WaitingForPersistenceBarrier = 1,
        RetryAfterBackoff = 2
    }

    private async Task<TerminationAttemptOutcome> TryProcessSingleTerminationAsync(
        string approvalId,
        PendingApprovalEntry entry,
        string reason)
    {
        _ = approvalId;
        entry.DisposeWatcher();
        if (JobResumer is not { } resumer)
        {
            return TerminationAttemptOutcome.Completed;
        }

        if (entry.IsSuspendedTerminationCompleted)
        {
            return TerminationAttemptOutcome.Completed;
        }

        if (!entry.IsSuspendedToBackgroundJob(out var peekCtx) || peekCtx is null)
        {
            return TerminationAttemptOutcome.Completed;
        }

        // Advisory 1 (Round 6): Never block ready termination items behind an uncommitted persistence barrier!
        // Attach a one-shot continuation wakeup and skip this item until PersistenceReadyTask completes.
        if (!peekCtx.PersistenceReadyTask.IsCompleted)
        {
            if (entry.TryRegisterPersistenceWakeupOnce())
            {
                _ = peekCtx.PersistenceReadyTask.ContinueWith(
                    t =>
                    {
                        _ = t;
                        if (Volatile.Read(ref _disposed) == 0 && !_pendingTerminations.IsEmpty)
                        {
                            _ = EnsureTerminationPumpAsync();
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            if (!peekCtx.PersistenceReadyTask.IsCompleted)
            {
                return TerminationAttemptOutcome.WaitingForPersistenceBarrier;
            }
        }

        if (!entry.TryBeginSuspendedJobTerminationAttempt(out var suspendedCtx) || suspendedCtx is null)
        {
            return entry.IsSuspendedTerminationCompleted
                ? TerminationAttemptOutcome.Completed
                : TerminationAttemptOutcome.RetryAfterBackoff;
        }

        var succeeded = false;
        try
        {
            var committed = await suspendedCtx.PersistenceReadyTask.ConfigureAwait(false);
            if (!committed)
            {
                succeeded = true;
                return TerminationAttemptOutcome.Completed;
            }

            var denySubmission = new ExecutionApprovalDecisionSubmission(
                entry.Item.RequestDigest,
                ExecutionApprovalDecisionKind.Deny,
                "system",
                reason);

            var status = await resumer.TryApplyResolutionAsync(
                suspendedCtx.JobId,
                suspendedCtx.ResumeToken,
                entry.Item,
                denySubmission,
                CancellationToken.None).ConfigureAwait(false);

            if (status is SuspendedJobResolutionStatus.Applied or SuspendedJobResolutionStatus.AlreadyLeftWaitingUser)
            {
                succeeded = true;
                return TerminationAttemptOutcome.Completed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transient error terminating invalidated/expired suspended job {JobId}; retained in recovery queue.", suspendedCtx.JobId);
        }
        finally
        {
            entry.CompleteSuspendedJobTerminationAttempt(succeeded);
        }

        return TerminationAttemptOutcome.RetryAfterBackoff;
    }

    public static string ToWireDecision(ExecutionApprovalDecisionKind kind) => kind switch
    {
        ExecutionApprovalDecisionKind.AllowOnce => "allow_once",
        ExecutionApprovalDecisionKind.AllowSession => "allow_session",
        ExecutionApprovalDecisionKind.AllowAlways => "allow_always",
        _ => "deny"
    };

    public static bool TryParseWireDecision(string? raw, out ExecutionApprovalDecisionKind kind)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "allow_once":
            case "allowonce":
                kind = ExecutionApprovalDecisionKind.AllowOnce;
                return true;
            case "allow_session":
            case "allowsession":
                kind = ExecutionApprovalDecisionKind.AllowSession;
                return true;
            case "allow_always":
            case "allowalways":
                kind = ExecutionApprovalDecisionKind.AllowAlways;
                return true;
            case "deny":
                kind = ExecutionApprovalDecisionKind.Deny;
                return true;
            default:
                kind = ExecutionApprovalDecisionKind.Deny;
                return false;
        }
    }

    private void ApplyPersistedOrSessionGrant(
        ExecutionRequest request,
        string requestDigest,
        ExecutionApprovalDecisionSubmission submission)
    {
        if (submission.Decision == ExecutionApprovalDecisionKind.AllowSession)
        {
            var dict = _sessionAllowances.GetOrAdd(request.SessionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            dict[requestDigest] = 0;
        }
        else if (submission.Decision == ExecutionApprovalDecisionKind.AllowAlways)
        {
            _permissionStore.AddAllowAlways(request, submission.Note);
        }
    }

    private static ExecutionPermissionMode EffectivePermissionModeForRequest(
        ExecutionRequest _,
        ExecutionPermissionMode globalMode)
        => globalMode;

    private static bool VerifyArtifactsOrInvalidate(ExecutionAuthorizationContext ctx)
        => ctx.VerifyStagedArtifactsUnchanged is null || ctx.VerifyStagedArtifactsUnchanged();

    private static ExecutionAuthorizationResult Allowed(ExecutionRequest req, ExecutionBackend backend, string message)
        => new(
            new ExecutionDecision(ExecutionDecisionKind.Allowed, ExecutionReasonCodes.Allowed, message),
            req.Argv,
            req.Executable,
            ApprovedBackend: backend,
            IsPreResolvedExecutable: backend == ExecutionBackend.Local);

    private static ExecutionAuthorizationResult Refused(ExecutionRequest req, ExecutionBackend backend, ExecutionDecision decision)
        => new(decision, req.Argv, req.Executable, ApprovedBackend: backend);

    private async Task PublishSseAsync(string sessionId, string eventType, object payload, CancellationToken ct)
    {
        var gateway = Gateway;
        if (gateway is null)
        {
            return;
        }

        try
        {
            await gateway.PublishEventAsync(
                sessionId,
                new GatewaySseEvent { Type = eventType, Payload = payload },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to publish SSE approval event {EventType} for session {SessionId}.", eventType, sessionId);
        }
    }

    private Task PublishResolvedSseAsync(PendingApprovalItem item, string decision, string? note, CancellationToken ct)
        => PublishSseAsync(item.SessionId, GatewaySseEventType.ToolApprovalResolved, new
        {
            approvalId = item.ApprovalId,
            requestDigest = item.RequestDigest,
            sessionId = item.SessionId,
            decision,
            note
        }, ct);

    private async Task RecordAuditAsync(
        ExecutionRequest request,
        string outcome,
        string decidedBy,
        string? note,
        CancellationToken ct)
    {
        var selfProtection = ControlPlane?.SelfApprovalProtection ?? true;
        var auditStore = _explicitAuditStore;
        IServiceScope? scope = null;
        if (auditStore is null && _serviceProvider is not null)
        {
            scope = _serviceProvider.CreateScope();
            auditStore = scope.ServiceProvider.GetService<IWorkflowAuditStore>();
        }

        try
        {
            if (auditStore is null)
            {
                return;
            }

            var auditId = $"exec-approval-{Guid.NewGuid():N}";
            var payload = JsonSerializer.Serialize(new ExecutionApprovalAudit(
                request.ComputeRequestDigest(),
                request.ToolName,
                request.Executable,
                request.InterpreterChain,
                request.Argv,
                request.Cwd,
                request.Backend.ToString(),
                request.Network,
                request.Filesystem,
                outcome,
                decidedBy,
                selfProtection,
                request.Origin.ToString(),
                request.PolicyVersion,
                request.ShellKind,
                note), ExecutionApprovalJsonContext.Default.ExecutionApprovalAudit);

            await auditStore.StartAsync(new WorkflowRunRecord
            {
                Id = auditId,
                SessionId = request.SessionId,
                Name = "execution_approval",
                Status = outcome,
                StartedAtUtc = DateTime.UtcNow
            }, ct).ConfigureAwait(false);
            await auditStore.CompleteAsync(auditId, outcome, payload, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to record execution approval audit for session {SessionId}.", request.SessionId);
        }
        finally
        {
            scope?.Dispose();
        }
    }

    private enum PendingResolutionState
    {
        Submitted = 0,
        Expired = 1,
        Cancelled = 2,
        InvalidatedByConfigChange = 3,
        SuspendedToBackgroundJob = 4
    }

    private sealed record PendingResolution(
        PendingResolutionState State,
        ExecutionApprovalDecisionSubmission? Submission = null,
        string? Note = null);

    private sealed class SingleUsePreApproval(
        string decidedBy,
        string? note,
        ExecutionBackend approvedBackend,
        DateTimeOffset expiresAtUtc)
    {
        private int _consumed;
        public string DecidedBy { get; } = decidedBy;
        public string? Note { get; } = note;
        public ExecutionBackend ApprovedBackend { get; } = approvedBackend;
        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;

        public bool IsValidFor(ExecutionBackend currentBackend, DateTimeOffset now)
            => currentBackend == ApprovedBackend && now < ExpiresAtUtc;

        public bool TryConsumeOnce() => Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
    }

    private enum ForegroundResolutionRoute
    {
        ResolvedForeground = 0,
        MustUseSuspendedBarrier = 1,
        TerminalFailure = 2
    }

    private sealed class PendingApprovalEntry(
        PendingApprovalItem item,
        ExecutionRequest request,
        IReadOnlyList<ExecutionApprovalDecisionKind> allowedDecisions,
        ExecutionBackend backendSnapshot,
        ExecutionPermissionMode modeSnapshot)
    {
        private readonly object _suspendLock = new();
        private int _consumed;
        private bool _exposed;
        private bool _suspendedToJob;
        private bool _userDecisionInFlight;
        private bool _suspendedJobResolved;
        private bool _suspendedTerminationInFlight;
        private bool _suspendedTerminationCompleted;
        private bool _persistenceWakeupRegistered;
        private bool _invalidated;
        private IDisposable? _configWatcher;
        private Timer? _expiryTimer;
        private AmbientBackgroundJobApprovalContext? _suspendedContext;

        public PendingApprovalItem Item { get; } = item;
        public ExecutionRequest Request { get; } = request;
        public IReadOnlyList<ExecutionApprovalDecisionKind> AllowedDecisions { get; } = allowedDecisions;
        public ExecutionBackend BackendSnapshot { get; } = backendSnapshot;
        public ExecutionPermissionMode ModeSnapshot { get; } = modeSnapshot;
        public TaskCompletionSource<PendingResolution> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool MatchesExecutionConfig(ExecutionOptions live)
            => live.Backend == BackendSnapshot && EffectivePermissionModeForRequest(Request, live.PermissionMode) == ModeSnapshot;

        public void AttachConfigWatcher(IDisposable? watcher)
        {
            lock (_suspendLock)
            {
                _configWatcher = watcher;
            }
        }

        public void DisposeWatcher()
        {
            IDisposable? w;
            Timer? t;
            lock (_suspendLock)
            {
                w = _configWatcher;
                _configWatcher = null;
                t = _expiryTimer;
                _expiryTimer = null;
            }

            w?.Dispose();
            t?.Dispose();
        }

        public bool TryAttachExpiryTimer(Timer timer)
        {
            Timer? oldTimer;
            lock (_suspendLock)
            {
                if (_invalidated || _suspendedJobResolved || _suspendedTerminationCompleted)
                {
                    return false;
                }

                oldTimer = _expiryTimer;
                _expiryTimer = timer;
            }

            oldTimer?.Dispose();
            return true;
        }

        public bool TryExpireSuspendedFromTimer()
        {
            lock (_suspendLock)
            {
                if (!_suspendedToJob || _suspendedJobResolved || _invalidated || _userDecisionInFlight)
                {
                    return false;
                }

                _suspendedJobResolved = true;
                Completion.TrySetResult(new PendingResolution(PendingResolutionState.Expired));
                return true;
            }
        }

        public bool TryRegisterPersistenceWakeupOnce()
        {
            lock (_suspendLock)
            {
                if (_persistenceWakeupRegistered)
                {
                    return false;
                }

                _persistenceWakeupRegistered = true;
                return true;
            }
        }

        public bool IsReadyForTerminationPumpPass
        {
            get
            {
                lock (_suspendLock)
                {
                    return _suspendedToJob &&
                           _suspendedContext is { PersistenceReadyTask.IsCompleted: true } &&
                           !_suspendedTerminationCompleted &&
                           !_suspendedTerminationInFlight &&
                           !_userDecisionInFlight;
                }
            }
        }

        public bool IsStillAwaitingUser
        {
            get
            {
                lock (_suspendLock)
                {
                    if (_invalidated)
                    {
                        return false;
                    }

                    if (_suspendedToJob)
                    {
                        return !_suspendedJobResolved;
                    }

                    return !Completion.Task.IsCompleted;
                }
            }
        }

        public ForegroundResolutionRoute TryResolveForegroundOrRouteToSuspended(
            ExecutionApprovalDecisionSubmission submission,
            IOptionsMonitor<ExecutionOptions> optionsMonitor,
            out AmbientBackgroundJobApprovalContext? suspendedContext,
            out string failureReason)
        {
            lock (_suspendLock)
            {
                suspendedContext = null;
                if (_invalidated)
                {
                    failureReason = "Approval request was invalidated by an execution configuration change.";
                    return ForegroundResolutionRoute.TerminalFailure;
                }

                if (_suspendedJobResolved)
                {
                    failureReason = "Approval request was already resolved, cancelled, or expired.";
                    return ForegroundResolutionRoute.TerminalFailure;
                }

                if (Item.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    _suspendedJobResolved = true;
                    Completion.TrySetResult(new PendingResolution(PendingResolutionState.Expired));
                    failureReason = "Approval request has expired.";
                    return ForegroundResolutionRoute.TerminalFailure;
                }

                var live = optionsMonitor.CurrentValue;
                if (!MatchesExecutionConfig(live))
                {
                    _invalidated = true;
                    _suspendedJobResolved = true;
                    Completion.TrySetResult(new PendingResolution(PendingResolutionState.InvalidatedByConfigChange));
                    failureReason = $"Execution configuration changed from '{BackendSnapshot}/{ModeSnapshot}' to '{live.Backend}/{EffectivePermissionModeForRequest(Request, live.PermissionMode)}'; approval invalidated.";
                    return ForegroundResolutionRoute.TerminalFailure;
                }

                if (_suspendedToJob && _suspendedContext is not null)
                {
                    suspendedContext = _suspendedContext;
                    failureReason = string.Empty;
                    return ForegroundResolutionRoute.MustUseSuspendedBarrier;
                }

                if (!Completion.TrySetResult(new PendingResolution(PendingResolutionState.Submitted, submission, submission.Note)))
                {
                    // If a concurrent timeout callback just transitioned to suspended before Completion settled, route to barrier; otherwise terminal!
                    if (_suspendedToJob && _suspendedContext is not null && !_suspendedJobResolved && !_invalidated)
                    {
                        suspendedContext = _suspendedContext;
                        failureReason = string.Empty;
                        return ForegroundResolutionRoute.MustUseSuspendedBarrier;
                    }

                    failureReason = "Approval request was already resolved, cancelled, or expired.";
                    return ForegroundResolutionRoute.TerminalFailure;
                }

                failureReason = string.Empty;
                return ForegroundResolutionRoute.ResolvedForeground;
            }
        }

        public void Expire()
        {
            lock (_suspendLock)
            {
                _suspendedJobResolved = true;
                Completion.TrySetResult(new PendingResolution(PendingResolutionState.Expired));
            }
        }

        public void CancelByCaller(string reason)
        {
            lock (_suspendLock)
            {
                _suspendedJobResolved = true;
                Completion.TrySetResult(new PendingResolution(PendingResolutionState.Cancelled, Note: reason));
            }
        }

        public void PresetZeroWaitSuspension(AmbientBackgroundJobApprovalContext context)
        {
            lock (_suspendLock)
            {
                _suspendedToJob = true;
                _suspendedContext = context;
                Completion.TrySetResult(new PendingResolution(PendingResolutionState.SuspendedToBackgroundJob));
            }
        }

        public bool TryCommitExposure(IOptionsMonitor<ExecutionOptions> optionsMonitor)
        {
            lock (_suspendLock)
            {
                var live = optionsMonitor.CurrentValue;
                if (_invalidated || !MatchesExecutionConfig(live))
                {
                    _invalidated = true;
                    _suspendedToJob = false;
                    _suspendedContext = null;
                    _suspendedTerminationCompleted = true;
                    Completion.TrySetResult(new PendingResolution(PendingResolutionState.InvalidatedByConfigChange));
                    return false;
                }

                _exposed = true;
                return true;
            }
        }

        public void InvalidateForConfigChange()
        {
            lock (_suspendLock)
            {
                _invalidated = true;
                _suspendedJobResolved = true;
                Completion.TrySetResult(new PendingResolution(PendingResolutionState.InvalidatedByConfigChange));
            }
        }

        public bool InvalidateForConfigChangeAndCheckShouldTerminate()
        {
            lock (_suspendLock)
            {
                _invalidated = true;
                _suspendedJobResolved = true;
                Completion.TrySetResult(new PendingResolution(PendingResolutionState.InvalidatedByConfigChange));
                return _exposed && _suspendedToJob && _suspendedContext is not null;
            }
        }

        public bool TryTransitionToSuspendedForBackgroundJob(
            AmbientBackgroundJobApprovalContext context,
            IOptionsMonitor<ExecutionOptions> optionsMonitor)
        {
            lock (_suspendLock)
            {
                if (Completion.Task.IsCompleted || _suspendedJobResolved || _invalidated)
                {
                    return false;
                }

                var live = optionsMonitor.CurrentValue;
                if (!MatchesExecutionConfig(live))
                {
                    _invalidated = true;
                    _suspendedJobResolved = true;
                    Completion.TrySetResult(new PendingResolution(PendingResolutionState.InvalidatedByConfigChange));
                    return false;
                }

                _suspendedToJob = true;
                _suspendedContext = context;
                return Completion.TrySetResult(new PendingResolution(PendingResolutionState.SuspendedToBackgroundJob));
            }
        }

        public bool IsSuspendedToBackgroundJob(out AmbientBackgroundJobApprovalContext? suspendedContext)
        {
            lock (_suspendLock)
            {
                suspendedContext = _suspendedContext;
                return _suspendedToJob && _suspendedContext is not null;
            }
        }

        public bool IsSuspendedTerminationCompleted
        {
            get
            {
                lock (_suspendLock)
                {
                    return _suspendedTerminationCompleted || !_suspendedToJob;
                }
            }
        }

        public bool TryCheckTerminalFailure(IOptionsMonitor<ExecutionOptions> optionsMonitor, out string reason)
        {
            lock (_suspendLock)
            {
                if (_invalidated)
                {
                    reason = "Approval invalidated by configuration change.";
                    return true;
                }

                if (Item.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    _suspendedJobResolved = true;
                    reason = "Approval expired.";
                    return true;
                }

                var live = optionsMonitor.CurrentValue;
                if (!MatchesExecutionConfig(live))
                {
                    _invalidated = true;
                    _suspendedJobResolved = true;
                    reason = $"Execution configuration changed from '{BackendSnapshot}/{ModeSnapshot}' to '{live.Backend}/{EffectivePermissionModeForRequest(Request, live.PermissionMode)}'.";
                    return true;
                }

                reason = string.Empty;
                return false;
            }
        }

        public bool TryBeginSuspendedUserDecision(
            IOptionsMonitor<ExecutionOptions> optionsMonitor,
            out string failureReason,
            out bool requiresSystemTermination,
            out bool isTransientBusy)
        {
            lock (_suspendLock)
            {
                isTransientBusy = false;
                if (_invalidated)
                {
                    requiresSystemTermination = true;
                    failureReason = "Approval request was invalidated by an execution configuration change.";
                    return false;
                }

                if (!_suspendedToJob || _suspendedJobResolved)
                {
                    requiresSystemTermination = false;
                    failureReason = "Approval request was already resolved or cancelled.";
                    return false;
                }

                if (Item.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    _suspendedJobResolved = true;
                    requiresSystemTermination = true;
                    failureReason = "Approval request expired before persistence/claim completed.";
                    return false;
                }

                var live = optionsMonitor.CurrentValue;
                if (!MatchesExecutionConfig(live))
                {
                    _invalidated = true;
                    _suspendedJobResolved = true;
                    requiresSystemTermination = true;
                    failureReason = $"Execution configuration changed from '{BackendSnapshot}/{ModeSnapshot}' to '{live.Backend}/{EffectivePermissionModeForRequest(Request, live.PermissionMode)}'; suspended approval invalidated.";
                    return false;
                }

                if (_userDecisionInFlight)
                {
                    isTransientBusy = true;
                    requiresSystemTermination = false;
                    failureReason = "Another approval submission for this suspended job is currently in progress.";
                    return false;
                }

                _userDecisionInFlight = true;
                requiresSystemTermination = false;
                failureReason = string.Empty;
                return true;
            }
        }

        public void CommitSuspendedUserDecision()
        {
            lock (_suspendLock)
            {
                _userDecisionInFlight = false;
                _suspendedJobResolved = true;
                _suspendedTerminationCompleted = true;
            }
        }

        public void MarkAlreadyLeftWaitingUser()
        {
            lock (_suspendLock)
            {
                _userDecisionInFlight = false;
                _suspendedJobResolved = true;
                _suspendedTerminationCompleted = true;
            }
        }

        public bool AbortSuspendedUserDecision(
            IOptionsMonitor<ExecutionOptions> optionsMonitor,
            out string terminalReason)
        {
            lock (_suspendLock)
            {
                _userDecisionInFlight = false;
                if (_invalidated)
                {
                    _suspendedJobResolved = true;
                    terminalReason = "Approval invalidated by configuration change.";
                    return true;
                }

                if (Item.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    _suspendedJobResolved = true;
                    terminalReason = "Approval expired during failed submission attempt.";
                    return true;
                }

                var live = optionsMonitor.CurrentValue;
                if (!MatchesExecutionConfig(live))
                {
                    _invalidated = true;
                    _suspendedJobResolved = true;
                    terminalReason = $"Execution configuration changed from '{BackendSnapshot}/{ModeSnapshot}' to '{live.Backend}/{EffectivePermissionModeForRequest(Request, live.PermissionMode)}'.";
                    return true;
                }

                terminalReason = string.Empty;
                return false;
            }
        }

        public bool TryBeginSuspendedJobTerminationAttempt(out AmbientBackgroundJobApprovalContext? suspendedContext)
        {
            lock (_suspendLock)
            {
                suspendedContext = _suspendedContext;
                if (!_suspendedToJob || _suspendedContext is null || _suspendedTerminationCompleted || _suspendedTerminationInFlight || _userDecisionInFlight)
                {
                    return false;
                }

                _suspendedTerminationInFlight = true;
                return true;
            }
        }

        public void CompleteSuspendedJobTerminationAttempt(bool succeeded)
        {
            lock (_suspendLock)
            {
                _suspendedTerminationInFlight = false;
                if (succeeded)
                {
                    _suspendedTerminationCompleted = true;
                }
            }
        }

        public bool TryConsumeOnce()
            => Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
    }
}
