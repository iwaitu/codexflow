// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;

namespace CodexFlow.Core.Execution;

/// <summary>Notified after an export was atomically published (e.g. to invalidate Hashline snapshots).</summary>
public interface IWorkspaceExportObserver
{
    void OnPublished(string workspaceRoot, IReadOnlyList<string> changedRelativePaths);
}

/// <summary>
/// Publishes already validated guest changes into the real worktree under a workspace lock with compare-and-swap:
/// every modified/deleted file must still equal the baseline hash and every added file must still be absent.
/// Host side re-hashes everything itself; no guest-provided metadata is trusted.
/// </summary>
public static class ScopedExportPublisher
{
    public static async Task<IsolatedExportResult> PublishAsync(
        string workspaceRoot,
        IReadOnlyList<GuestChange> changes,
        IReadOnlyDictionary<string, BaselineEntry> baseline,
        IWorkspaceExportObserver? observer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(baseline);
        if (changes.Count == 0)
        {
            return new IsolatedExportResult(ExecutionDecision.Allow(), Array.Empty<ExportedChange>(), Array.Empty<ExportRejection>());
        }

        var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        ct.ThrowIfCancellationRequested();

        // One mutual-exclusion object per root, shared with every structured tool write (WorkspaceFileWriter), so an export and
        // a tool write cannot interleave inside this process. (Other processes are covered by the compare-and-swap itself.)
        lock (WorkspaceFileWriter.GateFor(root))
        {
            return Publish(root, changes, baseline, observer);
        }
    }

    private static IsolatedExportResult Publish(
        string root,
        IReadOnlyList<GuestChange> changes,
        IReadOnlyDictionary<string, BaselineEntry> baseline,
        IWorkspaceExportObserver? observer)
    {
        // Validation only decides WHAT may be written. Path strings are re-checked against the real file system by the
        // safe writer at the moment of every operation (pinned/no-follow directory chain, exclusive or exchange-based CAS).
        var rejections = new List<ExportRejection>();
        var ops = new List<SafeWriteOp>();
        foreach (var change in changes)
        {
            if (!WorkspacePath.TryNormalizeStrict(change.Path, out var norm, out var why) || WorkspacePath.IsProtected(norm))
            {
                rejections.Add(new ExportRejection(change.Path, ExecutionReasonCodes.ExportRejectedUnsafePath, string.IsNullOrEmpty(why) ? "protected path" : why));
                continue;
            }

            byte[]? bytes = null;
            if (change.Kind != GuestChangeKind.Delete)
            {
                bytes = File.ReadAllBytes(change.StagedFile!);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), change.Sha256, StringComparison.Ordinal))
                {
                    rejections.Add(new ExportRejection(norm, ExecutionReasonCodes.ExportFailed, "staged content no longer matches its hash"));
                    continue;
                }
            }

            string? expected = null;
            if (change.Kind != GuestChangeKind.Add)
            {
                if (!baseline.TryGetValue(norm, out var entry))
                {
                    rejections.Add(new ExportRejection(norm, ExecutionReasonCodes.ExportRejectedConcurrentModification, "no snapshot baseline for an existing file"));
                    continue;
                }

                expected = entry.Sha256;
            }

            ops.Add(new SafeWriteOp(norm, change.Kind, bytes, expected));
        }

        if (rejections.Count > 0)
        {
            return new IsolatedExportResult(
                ExecutionDecision.Deny(rejections[0].ReasonCode, "export refused: " + rejections[0].Message),
                Array.Empty<ExportedChange>(), rejections);
        }

        var result = SafeTreeWriter.Apply(root, ops);
        if (!result.Success)
        {
            return new IsolatedExportResult(
                ExecutionDecision.Deny(result.ReasonCode, "export refused: " + result.Message),
                Array.Empty<ExportedChange>(),
                [new ExportRejection(result.FailedPath, result.ReasonCode, result.Message)]);
        }

        var applied = changes.Select(c => new ExportedChange(c.Path, c.Kind.ToString(), c.Sha256, c.Size)).ToList();
        // The files are already on disk: an observer failure must not turn a completed export into a failed one.
        var message = "allowed";
        try
        {
            observer?.OnPublished(root, applied.Select(a => a.Path).ToList());
        }
        catch (Exception ex)
        {
            message = $"published; the export observer failed ({ex.GetType().Name}: {ex.Message}) - derived state (e.g. Hashline snapshots) may be stale";
        }

        return new IsolatedExportResult(new ExecutionDecision(ExecutionDecisionKind.Allowed, ExecutionReasonCodes.Allowed, message), applied, Array.Empty<ExportRejection>());
    }
}
