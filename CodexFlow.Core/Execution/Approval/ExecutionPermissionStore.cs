// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Execution.Approval;

public sealed record PersistedPermissionRule(
    string Id,
    string SessionAgnosticDigest,
    string ToolName,
    string Executable,
    IReadOnlyList<string> Argv,
    DateTimeOffset CreatedAtUtc,
    string? Note = null);

public sealed record StagedAllowAlwaysReservation(
    string ReservationId,
    string SessionAgnosticDigest,
    bool AlreadyCommittedAtStageTime);

public interface IExecutionPermissionStore
{
    bool IsAllowedAlways(string sessionAgnosticDigest);

    PersistedPermissionRule AddAllowAlways(ExecutionRequest request, string? note = null);

    StagedAllowAlwaysReservation StageAllowAlways(ExecutionRequest request, string? note = null);

    PersistedPermissionRule? CommitStagedAllowAlways(StagedAllowAlwaysReservation reservation);

    void AbortStagedAllowAlways(StagedAllowAlwaysReservation reservation);

    IReadOnlyList<PersistedPermissionRule> ListRules();

    bool RemoveRule(string ruleId);
}

/// <summary>
/// Loads persistent <c>allow_always</c> rules ONCE at startup into memory and only mutates the active in-memory
/// rule set via authenticated control-plane operations (<see cref="AddAllowAlways"/> / <see cref="StageAllowAlways"/> /
/// <see cref="CommitStagedAllowAlways"/> / <see cref="RemoveRule"/>).
/// Uncommitted staged reservations are kept strictly separate from active <c>rules</c> both in memory and on disk (B8-1):
/// they NEVER grant authorization prior to commit, NEVER survive process restarts if uncommitted, and retain background
/// cleanup state if disk flush transiently fails during abort.
/// </summary>
public sealed class ExecutionPermissionStore : IExecutionPermissionStore, IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<PersistedPermissionRule> _rules;
    private readonly HashSet<string> _digests;
    private readonly Dictionary<string, PersistedPermissionRule> _stagedReservations = new(StringComparer.Ordinal);
    private bool _pendingStagedCleanupFlush;
    private int _cleanupPumpRunning;
    private int _disposed;

    public ExecutionPermissionStore(string permissionsFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionsFilePath);
        _path = permissionsFilePath;
        _rules = LoadCommittedRulesFromDiskOnce(_path, out var hadLeftoverStaged);
        _digests = new HashSet<string>(
            _rules.Select(static r => r.SessionAgnosticDigest),
            StringComparer.Ordinal);

        if (hadLeftoverStaged)
        {
            _pendingStagedCleanupFlush = true;
            _ = EnsureCleanupFlushPumpAsync();
        }
    }

    public bool HasPendingDiskCleanup
    {
        get
        {
            lock (_gate)
            {
                return _pendingStagedCleanupFlush;
            }
        }
    }

    public bool IsAllowedAlways(string sessionAgnosticDigest)
    {
        if (string.IsNullOrWhiteSpace(sessionAgnosticDigest))
        {
            return false;
        }

        lock (_gate)
        {
            return _digests.Contains(sessionAgnosticDigest);
        }
    }

    public PersistedPermissionRule AddAllowAlways(ExecutionRequest request, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAllowAlwaysPermitted(request);

        var digest = request.ComputeSessionAgnosticDigest();
        lock (_gate)
        {
            var existing = _rules.FirstOrDefault(r =>
                string.Equals(r.SessionAgnosticDigest, digest, StringComparison.Ordinal));
            if (existing is not null)
            {
                return existing;
            }

            var created = CreateRuleRecord(request, digest, note);

            // Persist atomically to disk BEFORE publishing into in-memory sets so a disk I/O failure
            // never leaves an uncommitted ghost grant active in memory!
            var nextRules = new List<PersistedPermissionRule>(_rules.Count + 1);
            nextRules.AddRange(_rules);
            nextRules.Add(created);
            SaveSnapshotUnsafe(nextRules, _stagedReservations.Values);
            _pendingStagedCleanupFlush = false;

            _rules.Add(created);
            _digests.Add(digest);
            return created;
        }
    }

    public StagedAllowAlwaysReservation StageAllowAlways(ExecutionRequest request, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAllowAlwaysPermitted(request);

        var digest = request.ComputeSessionAgnosticDigest();
        lock (_gate)
        {
            if (_digests.Contains(digest))
            {
                return new StagedAllowAlwaysReservation(
                    Guid.NewGuid().ToString("N"),
                    digest,
                    AlreadyCommittedAtStageTime: true);
            }

            var stagedRule = CreateRuleRecord(request, digest, note);
            var nextStaged = new List<PersistedPermissionRule>(_stagedReservations.Count + 1);
            nextStaged.AddRange(_stagedReservations.Values);
            nextStaged.Add(stagedRule);

            // Write ONLY into `stagedRules` (NEVER into active `rules` or `_digests`!) to verify disk writability up front
            // without activating the rule either in memory or across restarts before the background job resume commits!
            SaveSnapshotUnsafe(_rules, nextStaged);
            _pendingStagedCleanupFlush = false;

            _stagedReservations[stagedRule.Id] = stagedRule;
            return new StagedAllowAlwaysReservation(
                stagedRule.Id,
                digest,
                AlreadyCommittedAtStageTime: false);
        }
    }

    public PersistedPermissionRule? CommitStagedAllowAlways(StagedAllowAlwaysReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        lock (_gate)
        {
            var existingCommitted = _rules.FirstOrDefault(r =>
                string.Equals(r.SessionAgnosticDigest, reservation.SessionAgnosticDigest, StringComparison.Ordinal));

            if (reservation.AlreadyCommittedAtStageTime)
            {
                return existingCommitted;
            }

            if (!_stagedReservations.TryGetValue(reservation.ReservationId, out var stagedRule))
            {
                return existingCommitted;
            }

            var nextStaged = _stagedReservations
                .Where(kv => !string.Equals(kv.Key, reservation.ReservationId, StringComparison.Ordinal))
                .Select(static kv => kv.Value)
                .ToList();

            if (existingCommitted is not null)
            {
                // Another concurrent submission already committed the same digest! Clean up our staged entry only.
                _stagedReservations.Remove(reservation.ReservationId);
                TryFlushOrScheduleCleanupUnsafe(_rules, nextStaged);
                return existingCommitted;
            }

            var nextRules = new List<PersistedPermissionRule>(_rules.Count + 1);
            nextRules.AddRange(_rules);
            nextRules.Add(stagedRule);

            try
            {
                // Persist into committed `rules` FIRST; if this throws, `_rules` and `_digests` remain untouched!
                SaveSnapshotUnsafe(nextRules, nextStaged);
                _pendingStagedCleanupFlush = false;
            }
            catch
            {
                // Ensure the failed staged entry is evicted from memory and scheduled for background disk cleanup!
                _stagedReservations.Remove(reservation.ReservationId);
                _pendingStagedCleanupFlush = true;
                _ = EnsureCleanupFlushPumpAsync();
                throw;
            }

            _stagedReservations.Remove(reservation.ReservationId);
            _rules.Add(stagedRule);
            _digests.Add(stagedRule.SessionAgnosticDigest);
            return stagedRule;
        }
    }

    public void AbortStagedAllowAlways(StagedAllowAlwaysReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (reservation.AlreadyCommittedAtStageTime)
        {
            return;
        }

        var scheduleBackgroundCleanup = false;
        lock (_gate)
        {
            if (!_stagedReservations.Remove(reservation.ReservationId))
            {
                return;
            }

            try
            {
                SaveSnapshotUnsafe(_rules, _stagedReservations.Values);
                _pendingStagedCleanupFlush = false;
            }
            catch
            {
                // B8-1: Disk flush transiently failed (e.g. file handle locked on Windows).
                // Because staged rules were NEVER added to `_digests` or `rules`, authorization remains strictly FALSE
                // both in memory and across restarts, and the background cleanup pump flushes disk once unlocked!
                _pendingStagedCleanupFlush = true;
                scheduleBackgroundCleanup = true;
            }
        }

        if (scheduleBackgroundCleanup)
        {
            _ = EnsureCleanupFlushPumpAsync();
        }
    }

    public IReadOnlyList<PersistedPermissionRule> ListRules()
    {
        lock (_gate)
        {
            return _rules.ToArray();
        }
    }

    public bool RemoveRule(string ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return false;
        }

        lock (_gate)
        {
            var idx = _rules.FindIndex(r => string.Equals(r.Id, ruleId, StringComparison.Ordinal));
            if (idx < 0)
            {
                return false;
            }

            var nextRules = new List<PersistedPermissionRule>(_rules);
            var removed = nextRules[idx];
            nextRules.RemoveAt(idx);
            SaveSnapshotUnsafe(nextRules, _stagedReservations.Values);
            _pendingStagedCleanupFlush = false;

            _rules.RemoveAt(idx);
            if (_rules.All(r => !string.Equals(r.SessionAgnosticDigest, removed.SessionAgnosticDigest, StringComparison.Ordinal)))
            {
                _digests.Remove(removed.SessionAgnosticDigest);
            }

            return true;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    private async Task EnsureCleanupFlushPumpAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _cleanupPumpRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var backoffMs = 40;
            while (Volatile.Read(ref _disposed) == 0)
            {
                var stillDirty = false;
                lock (_gate)
                {
                    if (!_pendingStagedCleanupFlush)
                    {
                        break;
                    }

                    try
                    {
                        SaveSnapshotUnsafe(_rules, _stagedReservations.Values);
                        _pendingStagedCleanupFlush = false;
                    }
                    catch
                    {
                        stillDirty = true;
                    }
                }

                if (!stillDirty)
                {
                    break;
                }

                await Task.Delay(backoffMs).ConfigureAwait(false);
                backoffMs = Math.Min(250, backoffMs * 2);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _cleanupPumpRunning, 0);
            if (Volatile.Read(ref _disposed) == 0 &&
                HasPendingDiskCleanup &&
                Interlocked.CompareExchange(ref _cleanupPumpRunning, 1, 0) == 0)
            {
                Interlocked.Exchange(ref _cleanupPumpRunning, 0);
                _ = EnsureCleanupFlushPumpAsync();
            }
        }
    }

    private void TryFlushOrScheduleCleanupUnsafe(
        List<PersistedPermissionRule> rules,
        IEnumerable<PersistedPermissionRule> staged)
    {
        try
        {
            SaveSnapshotUnsafe(rules, staged);
            _pendingStagedCleanupFlush = false;
        }
        catch
        {
            _pendingStagedCleanupFlush = true;
            _ = EnsureCleanupFlushPumpAsync();
        }
    }

    private static void EnsureAllowAlwaysPermitted(ExecutionRequest request)
    {
        if (!request.GetAllowedDecisions().Contains(ExecutionApprovalDecisionKind.AllowAlways))
        {
            throw new InvalidOperationException(
                "allow_always is only permitted for verified non-arbitrary-code commands from a trusted executable origin.");
        }
    }

    private static PersistedPermissionRule CreateRuleRecord(ExecutionRequest request, string digest, string? note)
        => new(
            Guid.NewGuid().ToString("N"),
            digest,
            request.ToolName,
            request.Executable,
            request.Argv.ToArray(),
            DateTimeOffset.UtcNow,
            note);

    private static List<PersistedPermissionRule> LoadCommittedRulesFromDiskOnce(string path, out bool hadLeftoverStaged)
    {
        var root = GlobalSettingsFileStore.LoadRoot(path);
        hadLeftoverStaged = root["stagedRules"] is JArray { Count: > 0 };
        if (root["rules"] is not JArray arr)
        {
            return [];
        }

        return arr.ToObject<List<PersistedPermissionRule>>() ?? [];
    }

    private void SaveSnapshotUnsafe(
        IEnumerable<PersistedPermissionRule> committedRules,
        IEnumerable<PersistedPermissionRule> stagedRules)
    {
        var stagedArray = JArray.FromObject(stagedRules);
        var root = new JObject
        {
            ["version"] = 1,
            ["rules"] = JArray.FromObject(committedRules)
        };
        if (stagedArray.Count > 0)
        {
            root["stagedRules"] = stagedArray;
        }

        GlobalSettingsFileStore.WriteAtomically(_path, root.ToString(Formatting.Indented));
    }
}
