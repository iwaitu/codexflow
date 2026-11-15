// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;

namespace CodexFlow.Core.Execution;

public sealed record WorkspaceWriteResult(bool Success, string ReasonCode, string Message)
{
    public static WorkspaceWriteResult Ok { get; } = new(true, ExecutionReasonCodes.Allowed, "applied");
}

/// <summary>
/// The single last-write point of every structured workspace write (write_file, edit_file, notebook_edit, delete_file,
/// create_directory, apply_patch, hs_write). It is a thin, typed front for <see cref="SafeTreeWriter"/>: the caller passes the
/// hash of the version it READ (or null for "must not exist"), and the write only happens if that identity still holds at the
/// moment of the write, through a pinned/no-follow directory chain (see SafeTreeWriter for the per-OS mechanism).
/// The root is the host's trusted workspace root; paths outside it, protected paths (.git) and links are refused.
/// </summary>
internal sealed record WorkspaceChange(string FullPath, byte[]? NewBytes, string? ExpectedSha256, bool Delete);

public static class WorkspaceFileWriter
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    internal static object GateFor(string root)
        => Gates.GetOrAdd(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), _ => new object());

    /// <summary>Creates (expectedSha256 == null, must not exist) or replaces (must still equal expectedSha256) a file.</summary>
    public static WorkspaceWriteResult WriteFile(string root, string fullPath, byte[] newBytes, string? expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(newBytes);
        return Apply(root, fullPath, expectedSha256 is null ? GuestChangeKind.Add : GuestChangeKind.Modify, newBytes, expectedSha256);
    }

    /// <summary>Deletes a file only if it still equals the version that was read.</summary>
    public static WorkspaceWriteResult DeleteFile(string root, string fullPath, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(expectedSha256);
        return Apply(root, fullPath, GuestChangeKind.Delete, null, expectedSha256);
    }

    /// <summary>Creates a directory (and parents) without ever following a link on the way.</summary>
    public static WorkspaceWriteResult EnsureDirectory(string root, string fullPath)
        => Apply(root, fullPath, GuestChangeKind.Mkdir, null, null);

    /// <summary>
    /// Several changes as ONE all-or-nothing, identity-checked batch (each change: expected version of what was read, or
    /// null for "must not exist"; Delete removes the file only if it still equals the version read).
    /// </summary>
    internal static WorkspaceWriteResult ApplyBatch(string root, IReadOnlyList<WorkspaceChange> changes)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(changes);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var ops = new List<SafeWriteOp>(changes.Count);
        foreach (var change in changes)
        {
            var target = Path.GetFullPath(change.FullPath);
            if (!target.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison))
            {
                return new WorkspaceWriteResult(false, ExecutionReasonCodes.ExportRejectedUnsafePath, "target is not inside the trusted workspace root");
            }

            var relative = target[(rootFull.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            if (!WorkspacePath.TryNormalizeStrict(relative, out var normalized, out var why) || WorkspacePath.IsProtected(normalized))
            {
                return new WorkspaceWriteResult(false, ExecutionReasonCodes.ExportRejectedUnsafePath, string.IsNullOrEmpty(why) ? "protected path" : why);
            }

            var kind = change.Delete ? GuestChangeKind.Delete : change.ExpectedSha256 is null ? GuestChangeKind.Add : GuestChangeKind.Modify;
            ops.Add(new SafeWriteOp(normalized, kind, change.NewBytes, change.ExpectedSha256));
        }

        SafeWriteResult result;
        lock (GateFor(rootFull))
        {
            result = SafeTreeWriter.Apply(rootFull, ops);
        }

        return result.Success ? WorkspaceWriteResult.Ok : new WorkspaceWriteResult(false, result.ReasonCode, result.Message);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    private static WorkspaceWriteResult Apply(string root, string fullPath, GuestChangeKind kind, byte[]? bytes, string? expected)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(fullPath);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(fullPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison))
        {
            return new WorkspaceWriteResult(false, ExecutionReasonCodes.ExportRejectedUnsafePath, "target is not inside the trusted workspace root");
        }

        var relative = target[(rootFull.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        if (!WorkspacePath.TryNormalizeStrict(relative, out var normalized, out var why) || WorkspacePath.IsProtected(normalized))
        {
            return new WorkspaceWriteResult(false, ExecutionReasonCodes.ExportRejectedUnsafePath, string.IsNullOrEmpty(why) ? "protected path" : why);
        }

        SafeWriteResult result;
        lock (GateFor(rootFull))
        {
            result = SafeTreeWriter.Apply(rootFull, [new SafeWriteOp(normalized, kind, bytes, expected)]);
        }

        return result.Success
            ? WorkspaceWriteResult.Ok
            : new WorkspaceWriteResult(false, result.ReasonCode, result.Message);
    }
}
