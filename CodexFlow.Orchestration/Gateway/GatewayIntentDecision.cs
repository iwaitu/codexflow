// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using CodexFlow.Core.Gateway;

namespace CodexFlow.Gateway;

/// <summary>
/// The single user-intent vocabulary shared by every Gateway dispatch / visibility / approval path.
/// <see cref="Unknown"/> is the default and never authorizes anything.
/// </summary>
public enum GatewayIntentKind
{
    /// <summary>No actionable, authorized intent was recognized. Never executes, approves or dispatches.</summary>
    Unknown = 0,

    /// <summary>The user asked for planning only (or deferred execution / approval to a later message).</summary>
    PlanOnly,

    /// <summary>The user forbade changes (read-only / do-not-modify).</summary>
    ReadOnly,

    /// <summary>Explicit, present-tense approval of the current plan.</summary>
    Approve,

    /// <summary>Explicit rejection (or revocation of approval) of the current plan.</summary>
    Reject,

    /// <summary>Explicit request to execute / continue / dispatch work.</summary>
    Execute,

    /// <summary>Explicit request to stop a running job / worker. A job-management action owned by the user.</summary>
    Stop
}

/// <summary>Where an intent decision came from.</summary>
public enum GatewayIntentSource
{
    /// <summary>No authority: empty text, non-user source, or nothing recognized.</summary>
    None = 0,

    /// <summary>A trusted structured action attached by server code (UI/CLI button), not parsed from text.</summary>
    Structured,

    /// <summary>The compatibility natural-language entry, parsed from the current user message only.</summary>
    NaturalLanguage
}

/// <summary>
/// One decision per user message. Tool visibility, direct dispatch, approve_plan execution and
/// required-tool prompts must all consume the same instance (see <see cref="GatewayIntentResolver"/>).
/// </summary>
public sealed record GatewayIntentDecision
{
    public GatewayIntentKind Kind { get; init; } = GatewayIntentKind.Unknown;
    public GatewayIntentSource Source { get; init; } = GatewayIntentSource.None;

    /// <summary>Stable machine-readable explanation, e.g. <c>approval_negated</c>, <c>approval_historical</c>.</summary>
    public string ReasonCode { get; init; } = "unknown";

    /// <summary>
    /// For <see cref="GatewayIntentKind.Approve"/>: the same message also asks to start execution.
    /// For <see cref="GatewayIntentKind.Execute"/>: always true.
    /// </summary>
    public bool StartsExecution { get; init; }

    /// <summary>The user explicitly forbade starting execution alongside an approval ("approve but do not start").</summary>
    public bool SuppressDispatch { get; init; }

    /// <summary>The message asks to modify/build/test code, so the execution-stage tool surface may be exposed for this turn.</summary>
    public bool ElevatesToExecutionStage { get; init; }

    /// <summary>The message asks to continue / run the next planned task (drives the dynamic start_next_task hint).</summary>
    public bool ContinuesPlan { get; init; }

    /// <summary>Tool the model must call this turn, or null. Only set for authorized (non-boundary, non-ambiguous) intents.</summary>
    public string? RequiredTool { get; init; }

    /// <summary>
    /// The message contained a contradictory / conditional / double-negated action. Nothing is executed;
    /// <see cref="ConfirmationPrompt"/> names the concrete action the user can confirm.
    /// </summary>
    public bool RequiresConfirmation { get; init; }

    public string? ConfirmationPrompt { get; init; }

    /// <summary>Further actions found in the same message that were NOT acted on; they need a fresh message.</summary>
    public IReadOnlyList<GatewayIntentKind> DeferredKinds { get; init; } = [];

    /// <summary>Trusted structured action, when <see cref="Source"/> is <see cref="GatewayIntentSource.Structured"/>.</summary>
    public GatewayStructuredAction? Structured { get; init; }

    /// <summary>Planning-only / read-only boundary: approval, dispatch and stage elevation are all refused.</summary>
    public bool IsBoundary => Kind is GatewayIntentKind.PlanOnly or GatewayIntentKind.ReadOnly;

    /// <summary>True when this decision may flip a plan to Approved. Ambiguous decisions never do.</summary>
    public bool AuthorizesApproval => Kind == GatewayIntentKind.Approve && !RequiresConfirmation;

    public bool AuthorizesRejection => Kind == GatewayIntentKind.Reject && !RequiresConfirmation;

    /// <summary>True when this decision may start / continue a task.</summary>
    public bool AuthorizesDispatch =>
        !RequiresConfirmation &&
        (Kind == GatewayIntentKind.Execute ||
         // approve_plan auto-starts the first task (product behavior), unless the user said "approve but do not start".
         (Kind == GatewayIntentKind.Approve && !SuppressDispatch));

    /// <summary>The "no authority" decision used for non-user sources and unrecognized input.</summary>
    public static GatewayIntentDecision NoAuthority(string reasonCode)
        => new() { Kind = GatewayIntentKind.Unknown, Source = GatewayIntentSource.None, ReasonCode = reasonCode };
}

/// <summary>Kinds of trusted structured actions a UI/CLI can send instead of free text.</summary>
public enum GatewayStructuredActionKind
{
    Approve,
    Reject,
    Execute,
    Stop,
    PlanOnly
}

/// <summary>
/// A trusted structured action. It is created by server code after authentication and attached with
/// <see cref="GatewayStructuredActions.Attach"/>; message text can never create one.
/// </summary>
/// <param name="Kind">The action.</param>
/// <param name="PlanArtifactId">Plan the user acted on (approve/reject): must equal the current plan.</param>
/// <param name="PlanVersion">Plan version the user acted on: must equal the current version.</param>
/// <param name="PlanHash">Plan content hash the user acted on: must equal the current hash.</param>
/// <param name="IdempotencyKey">Client supplied idempotency key; a repeat with the same key is a no-op.</param>
/// <param name="StartExecution">For Approve: also start execution (default true, matching approve_plan auto start).</param>
/// <param name="AcceptExistingDependencyDebt">
/// For Approve: the user also accepts the dependency advisories that already exist when the plan starts, so verification records them
/// as existing debt instead of blocking the fixes of this plan (never Critical, never what the change introduces). Only this trusted
/// action can set it: message text and model tool arguments cannot.
/// </param>
/// <param name="ApproveDependencyUpgrades">
/// For Approve: package ids whose upgrade needs the user (a major-version jump) and that the user approves. Only candidates the host
/// itself resolved from its scan can be approved: the user never supplies a version. Fetched into the session overlay at approval.
/// </param>
public sealed record GatewayStructuredAction(
    GatewayStructuredActionKind Kind,
    string? PlanArtifactId = null,
    string? PlanVersion = null,
    string? PlanHash = null,
    string? IdempotencyKey = null,
    bool StartExecution = true,
    bool AcceptExistingDependencyDebt = false,
    IReadOnlyList<string>? ApproveDependencyUpgrades = null);

/// <summary>
/// Side channel for trusted structured actions. <see cref="GatewayMessage"/> lives in Core (another owner),
/// so the action rides on the message instance instead of changing the shared contract.
/// Handoff: the authenticated controller/CLI entry point calls <see cref="Attach"/>.
/// </summary>
public static class GatewayStructuredActions
{
    private static readonly ConditionalWeakTable<GatewayMessage, GatewayStructuredAction> Table = new();

    public static void Attach(GatewayMessage message, GatewayStructuredAction action)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(action);
        Table.AddOrUpdate(message, action);
    }

    public static bool TryGet(GatewayMessage message, out GatewayStructuredAction? action)
    {
        action = null;
        return message != null && Table.TryGetValue(message, out action);
    }
}

/// <summary>
/// Resolves exactly one <see cref="GatewayIntentDecision"/> per message and caches it on the instance so every
/// consumer (tool visibility, direct dispatch, approve_plan, required-tool prompts) sees the same answer.
/// </summary>
public static class GatewayIntentResolver
{
    private static readonly ConditionalWeakTable<GatewayMessage, GatewayIntentDecision> Cache = new();

    public static GatewayIntentDecision Resolve(GatewayMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Cache.GetValue(message, static m => ResolveCore(m));
    }

    private static GatewayIntentDecision ResolveCore(GatewayMessage message)
    {
        // Only a live user turn can carry authority. Task notifications, system events and tool output
        // (which may contain the literal text "approve_plan") never approve, reject, stop or dispatch.
        if (message.Source != GatewayMessageSource.UserChat)
        {
            return GatewayIntentDecision.NoAuthority("non_user_source");
        }

        if (GatewayStructuredActions.TryGet(message, out var structured) && structured != null)
        {
            return FromStructured(structured);
        }

        if (message.ControlChannel == CodexFlow.Core.Remote.ControlChannel.Remote)
        {
            return GatewayIntentDecision.NoAuthority("remote_natural_language_disabled");
        }

        return GatewayIntentClassifier.Classify(message.Content);
    }

    private static GatewayIntentDecision FromStructured(GatewayStructuredAction action)
    {
        var kind = action.Kind switch
        {
            GatewayStructuredActionKind.Approve => GatewayIntentKind.Approve,
            GatewayStructuredActionKind.Reject => GatewayIntentKind.Reject,
            GatewayStructuredActionKind.Execute => GatewayIntentKind.Execute,
            GatewayStructuredActionKind.Stop => GatewayIntentKind.Stop,
            GatewayStructuredActionKind.PlanOnly => GatewayIntentKind.PlanOnly,
            _ => GatewayIntentKind.Unknown
        };

        return new GatewayIntentDecision
        {
            Kind = kind,
            Source = GatewayIntentSource.Structured,
            ReasonCode = "structured_action",
            StartsExecution = kind == GatewayIntentKind.Execute ||
                              (kind == GatewayIntentKind.Approve && action.StartExecution),
            SuppressDispatch = kind == GatewayIntentKind.Approve && !action.StartExecution,
            ContinuesPlan = kind == GatewayIntentKind.Execute,
            RequiredTool = kind switch
            {
                GatewayIntentKind.Approve => "approve_plan",
                GatewayIntentKind.Reject => "reject_plan",
                GatewayIntentKind.Execute => "start_next_task",
                GatewayIntentKind.Stop => "stop_worker",
                _ => null
            },
            Structured = action
        };
    }
}
