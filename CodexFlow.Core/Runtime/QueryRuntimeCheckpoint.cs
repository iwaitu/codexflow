// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Compact runtime working set that can be persisted alongside a completed query turn.
/// It stores handles and summaries, not raw tool output.
/// </summary>
public sealed record QueryRuntimeCheckpoint
{
    public const string MetadataKey = "QueryRuntimeCheckpoint";

    public required Guid QueryId { get; init; }

    public required string SessionId { get; init; }

    public required QueryLoopEntryPoint EntryPoint { get; init; }

    public required int Round { get; init; }

    public required QueryRuntimeLoopPhase CurrentPhase { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string? ActiveTaskSummary { get; init; }

    public string? WorkerType { get; init; }

    public string? WorkerDisplayName { get; init; }

    public string? WorkerIsolationMode { get; init; }

    public string? RequiredToolContractName { get; init; }

    public IReadOnlyList<string> RequiredToolContractToolNames { get; init; } = [];

    public string? RequiredToolNameForNextRound { get; init; }

    public required bool RequiredToolContractSatisfied { get; init; }

    public required int TotalToolCalls { get; init; }

    public required int WriteToolCalls { get; init; }

    public required int RecoveryCount { get; init; }

    public string? LastContinueReason { get; init; }

    public required QueryEvidenceLedgerSnapshot EvidenceLedger { get; init; }

    public PromptAssemblySnapshot? LastPromptAssemblySnapshot { get; init; }
}

public sealed record QueryEvidenceLedgerSnapshot
{
    public IReadOnlyList<FileEvidence> Files { get; init; } = [];

    public IReadOnlyList<ToolEvidence> ToolResults { get; init; } = [];

    public IReadOnlyList<ChangedFileEvidence> ChangedFiles { get; init; } = [];

    public IReadOnlyList<DiffChangeEvidence> DiffChanges { get; init; } = [];

    public IReadOnlyList<RuntimeVerificationEvidence> Verifications { get; init; } = [];

    public IReadOnlyList<PendingModificationEvidence> PendingModifications { get; init; } = [];

    public string? LastToolBatchSummary { get; init; }

    public IReadOnlyList<RuntimeFailureEvidence> Failures { get; init; } = [];

    public IReadOnlyList<string> RepeatedEvidenceKeys { get; init; } = [];

    public static QueryEvidenceLedgerSnapshot FromLedger(QueryEvidenceLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        return new QueryEvidenceLedgerSnapshot
        {
            Files = ledger.Files.ToArray(),
            ToolResults = ledger.ToolResults.ToArray(),
            ChangedFiles = ledger.ChangedFiles.ToArray(),
            DiffChanges = ledger.DiffChanges.ToArray(),
            Verifications = ledger.Verifications.ToArray(),
            PendingModifications = ledger.PendingModifications.ToArray(),
            LastToolBatchSummary = ledger.LastToolBatchSummary,
            Failures = ledger.Failures.ToArray(),
            RepeatedEvidenceKeys = ledger.RepeatedEvidenceKeys.ToArray()
        };
    }
}

public static class QueryRuntimeCheckpointBuilder
{
    private const int ActiveTaskSummaryMaxChars = 1_200;

    public static QueryRuntimeCheckpoint Build(
        QueryRuntimeState state,
        Guid queryId,
        QueryRuntimeRequest request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);

        var contract = request.RequiredToolContract ?? request.WorkerContext?.RequiredToolContract;

        return new QueryRuntimeCheckpoint
        {
            QueryId = queryId,
            SessionId = request.SessionId,
            EntryPoint = request.EntryPoint,
            Round = state.Round,
            CurrentPhase = state.CurrentPhase,
            ActiveTaskSummary = BuildActiveTaskSummary(request.InitialMessages),
            WorkerType = request.WorkerContext?.WorkerType.ToString(),
            WorkerDisplayName = request.WorkerContext?.DisplayName,
            WorkerIsolationMode = request.WorkerContext?.IsolationMode.ToString(),
            RequiredToolContractName = contract?.Name,
            RequiredToolContractToolNames = contract?.AnyOfToolNames.ToArray() ?? [],
            RequiredToolNameForNextRound = state.RequiredToolNameForNextRound,
            RequiredToolContractSatisfied = contract?.IsSatisfiedBy(state.ExecutedToolNames, state.SuccessfulToolNames) == true,
            TotalToolCalls = state.TotalToolCalls,
            WriteToolCalls = state.TotalWriteToolCalls,
            RecoveryCount = state.RecoveryCount,
            LastContinueReason = state.LastContinueReason,
            EvidenceLedger = QueryEvidenceLedgerSnapshot.FromLedger(state.EvidenceLedger),
            LastPromptAssemblySnapshot = state.LastPromptAssemblySnapshot
        };
    }

    private static string? BuildActiveTaskSummary(IReadOnlyList<ChatMessage> messages)
    {
        var text = messages
            .LastOrDefault(static message => message.Role == ChatRole.User)
            ?.Text?
            .Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Length <= ActiveTaskSummaryMaxChars
            ? text
            : text[..(ActiveTaskSummaryMaxChars - 3)] + "...";
    }

    public static bool TryRestoreEvidenceLedgerFromSession(
        QueryRuntimeRequest request,
        QueryRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(state);

        if (request.Session?.Metadata == null ||
            !request.Session.Metadata.TryGetValue(QueryRuntimeCheckpoint.MetadataKey, out var rawCheckpoint) ||
            string.IsNullOrWhiteSpace(rawCheckpoint))
        {
            return false;
        }

        QueryRuntimeCheckpoint? checkpoint;
        try
        {
            checkpoint = SessionMetadataJson.ReadCheckpoint(rawCheckpoint);
        }
        catch
        {
            return false;
        }

        if (checkpoint?.EvidenceLedger == null)
        {
            return false;
        }

        RestoreEvidenceLedger(state, checkpoint.EvidenceLedger);
        return true;
    }

    private static void RestoreEvidenceLedger(
        QueryRuntimeState state,
        QueryEvidenceLedgerSnapshot snapshot)
    {
        AddMissingFiles(state.EvidenceLedger.Files, snapshot.Files);
        AddMissingToolResults(state.EvidenceLedger.ToolResults, snapshot.ToolResults);
        AddMissingChangedFiles(state.EvidenceLedger.ChangedFiles, snapshot.ChangedFiles);
        AddMissingDiffChanges(state.EvidenceLedger.DiffChanges, snapshot.DiffChanges);
        AddMissingVerifications(state.EvidenceLedger.Verifications, snapshot.Verifications);
        AddMissingPendingModifications(state.EvidenceLedger.PendingModifications, snapshot.PendingModifications);
        AddMissingFailures(state.EvidenceLedger.Failures, snapshot.Failures);
        AddMissingStrings(state.EvidenceLedger.RepeatedEvidenceKeys, snapshot.RepeatedEvidenceKeys);
        RestoreReadEvidenceIndexes(state, snapshot);

        if (!string.IsNullOrWhiteSpace(snapshot.LastToolBatchSummary) &&
            string.IsNullOrWhiteSpace(state.EvidenceLedger.LastToolBatchSummary))
        {
            state.EvidenceLedger.LastToolBatchSummary = snapshot.LastToolBatchSummary;
            state.LastToolBatchSummaryPrompt ??= snapshot.LastToolBatchSummary;
        }
    }

    private static void RestoreReadEvidenceIndexes(
        QueryRuntimeState state,
        QueryEvidenceLedgerSnapshot snapshot)
    {
        foreach (var file in snapshot.Files)
        {
            if (string.IsNullOrWhiteSpace(file.FilePath))
            {
                continue;
            }

            var key = BuildReadEvidenceKey(file);
            if (!state.EvidenceLedger.SeenReadEvidenceKeys.ContainsKey(key))
            {
                state.EvidenceLedger.SeenReadEvidenceKeys[key] = 1;
            }
        }

        if (state.EvidenceLedger.RepeatedEvidenceKeys.Count > 0 && state.ConsecutiveRepeatedReadRounds == 0)
        {
            state.ConsecutiveRepeatedReadRounds = 1;
        }
    }

    private static string BuildReadEvidenceKey(FileEvidence evidence)
    {
        var displayTarget = evidence.FilePath.Replace('\\', '/');
        var toolName = string.IsNullOrWhiteSpace(evidence.ToolName) ? "hs_read" : evidence.ToolName;
        if (!string.IsNullOrWhiteSpace(evidence.FileFingerprint))
        {
            return $"{toolName}|{displayTarget}|fp:{evidence.FileFingerprint}";
        }

        if (evidence.WindowStartLine.HasValue || evidence.WindowEndLine.HasValue)
        {
            return $"{toolName}|{displayTarget}|range:{evidence.WindowStartLine?.ToString() ?? "?"}-{evidence.WindowEndLine?.ToString() ?? "?"}";
        }

        return $"{toolName}|{displayTarget}";
    }

    private static void AddMissingFiles(List<FileEvidence> target, IReadOnlyList<FileEvidence> source)
    {
        foreach (var item in source)
        {
            if (!target.Any(existing => string.Equals(existing.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(item);
            }
        }
    }

    private static void AddMissingToolResults(List<ToolEvidence> target, IReadOnlyList<ToolEvidence> source)
    {
        foreach (var item in source)
        {
            if (!target.Any(existing =>
                    !string.IsNullOrWhiteSpace(item.CallId) &&
                    string.Equals(existing.CallId, item.CallId, StringComparison.Ordinal)))
            {
                target.Add(item);
            }
        }
    }

    private static void AddMissingChangedFiles(List<ChangedFileEvidence> target, IReadOnlyList<ChangedFileEvidence> source)
    {
        foreach (var item in source)
        {
            if (!target.Any(existing => string.Equals(existing.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(item);
            }
        }
    }

    private static void AddMissingDiffChanges(List<DiffChangeEvidence> target, IReadOnlyList<DiffChangeEvidence> source)
    {
        foreach (var item in source)
        {
            if (!target.Any(existing => string.Equals(existing.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(item);
            }
        }
    }

    private static void AddMissingVerifications(List<RuntimeVerificationEvidence> target, IReadOnlyList<RuntimeVerificationEvidence> source)
    {
        foreach (var item in source)
        {
            if (!target.Any(existing => string.Equals(existing.Kind, item.Kind, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(item);
            }
        }
    }

    private static void AddMissingPendingModifications(
        List<PendingModificationEvidence> target,
        IReadOnlyList<PendingModificationEvidence> source)
    {
        foreach (var item in source)
        {
            if (!target.Any(existing =>
                    string.Equals(existing.Source, item.Source, StringComparison.Ordinal) &&
                    string.Equals(existing.AssistantPlanSummary, item.AssistantPlanSummary, StringComparison.Ordinal)))
            {
                target.Add(item);
            }
        }
    }

    private static void AddMissingFailures(List<RuntimeFailureEvidence> target, IReadOnlyList<RuntimeFailureEvidence> source)
    {
        foreach (var item in source)
        {
            if (!target.Any(existing =>
                    string.Equals(existing.Source, item.Source, StringComparison.Ordinal) &&
                    string.Equals(existing.ToolName, item.ToolName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Message, item.Message, StringComparison.Ordinal)))
            {
                target.Add(item);
            }
        }
    }

    private static void AddMissingStrings(List<string> target, IReadOnlyList<string> source)
    {
        foreach (var item in source)
        {
            if (!string.IsNullOrWhiteSpace(item) &&
                !target.Contains(item, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(item);
            }
        }
    }
}
