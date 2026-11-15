// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("CodexFlow.Core.Tests")]

namespace CodexFlow.Core.Execution;

internal sealed record SafeWriteOp(string Path, GuestChangeKind Kind, byte[]? NewBytes, string? ExpectedSha256);

/// <summary>Carries a precise failure out of the nested helpers of a publish batch.</summary>
public sealed class SafeWriteAbortException : Exception
{
    public SafeWriteAbortException()
        : this(ExecutionReasonCodes.ExportFailed, "safe write aborted", string.Empty)
    {
    }

    public SafeWriteAbortException(string message)
        : this(ExecutionReasonCodes.ExportFailed, message, string.Empty)
    {
    }

    public SafeWriteAbortException(string message, Exception innerException)
        : base(message, innerException)
    {
        ReasonCode = ExecutionReasonCodes.ExportFailed;
        FailedPath = string.Empty;
    }

    public SafeWriteAbortException(string reasonCode, string message, string path)
        : base(message)
    {
        ReasonCode = reasonCode;
        FailedPath = path;
    }

    public string ReasonCode { get; }

    public string FailedPath { get; }
}

internal sealed record SafeWriteResult(bool Success, string ReasonCode, string Message, string FailedPath)
{
    public static SafeWriteResult Ok { get; } = new(true, ExecutionReasonCodes.Allowed, "applied", string.Empty);

    public static SafeWriteResult Fail(string reason, string message, string path) => new(false, reason, message, path);
}

/// <summary>
/// Publishes a verified change set into a real worktree without trusting path strings at the moment of the write.
/// <list type="bullet">
/// <item><description>Linux: every directory is opened relative to the previous one with O_NOFOLLOW|O_DIRECTORY (a swapped-in
/// symlink fails instead of redirecting the write); the identity of every cached directory fd is re-verified (through
/// /proc/self/fd) before each operation and again before the batch is declared successful, so a directory the host renamed
/// away mid-batch fails the batch instead of silently receiving later writes; files are replaced with
/// renameat2(RENAME_EXCHANGE) and the displaced file is re-hashed, so a concurrent writer that touched the target after our
/// check is detected and its file is put back (RENAME_NOREPLACE for adds). Applied changes are undone if a later change of
/// the batch fails for ANY reason, and the backup temp files are always removed, except one that holds host data a rollback
/// could not put back (it is kept and reported, never deleted).</description></item>
/// <item><description>Windows: the whole directory chain is pinned with open handles that deny delete/rename (so a directory
/// cannot be swapped for a junction while we work), every pinned directory and target is verified against its FINAL path
/// (so an 8.3 short-name alias such as GIT~1 can never reach a protected directory), every target is opened exclusively
/// (share none, no reparse following) and verified before anything is written, and all handles are held until the whole batch
/// is committed, so no other process can observe or modify a half-applied state. Crash atomicity is not provided here;
/// in-memory backups cover failures (including a failed write after truncation and a delete flag set on an earlier item).
/// Directories created by a failed batch are removed.</description></item>
/// </list>
/// Operation shape is validated up front with distinct reasons (missing expected hash, missing bytes, unsafe path) before the
/// file system is touched. Residual limits (documented, not hidden): other processes may still write to an inode they already
/// held open; replacing a file creates a new inode on Linux (owner/ACL/xattrs/hard links of the original are not carried over,
/// the mode is); the host filesystem above the worktree root is trusted.
/// </summary>
internal static class SafeTreeWriter
{
    private static readonly Regex Hex64 = new("^[0-9A-Fa-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Test seam invoked at the TOCTOU windows (stage names like "verified:path", "writing:path", "deleting:path").</summary>
    internal static Action<string>? TestHook { get; set; }

    /// <summary>Test seam for the Linux native calls: return true to make the named call fail (e.g. "renameat2:2").</summary>
    internal static Func<string, bool>? FaultInjector { get; set; }

    internal static void Hook(string stage) => TestHook?.Invoke(stage);

    internal static bool ShouldFail(string call) => FaultInjector?.Invoke(call) == true;

    public static SafeWriteResult Apply(string root, IReadOnlyList<SafeWriteOp> ops)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(ops);
        var invalid = Validate(ops, out var normalized);
        if (invalid is not null)
        {
            return invalid;
        }

        if (OperatingSystem.IsWindows())
        {
            return WindowsSafeFs.Apply(root, normalized);
        }

        if (OperatingSystem.IsLinux())
        {
            return PosixSafeFs.Apply(root, normalized);
        }

        return SafeWriteResult.Fail(ExecutionReasonCodes.UnsupportedIsolation, "No safe publish mechanism exists for this operating system; export is refused.", string.Empty);
    }

    /// <summary>Shape check before touching the file system: every failure here has its own reason, never "concurrent modification".</summary>
    private static SafeWriteResult? Validate(IReadOnlyList<SafeWriteOp> ops, out List<SafeWriteOp> normalized)
    {
        normalized = new List<SafeWriteOp>(ops.Count);
        foreach (var op in ops)
        {
            if (op is null || string.IsNullOrEmpty(op.Path))
            {
                return SafeWriteResult.Fail(ExecutionReasonCodes.InvalidOperation, "operation without a path", string.Empty);
            }

            if (!WorkspacePath.TryNormalizeStrict(op.Path, out var norm, out var why) || !string.Equals(norm, op.Path, StringComparison.Ordinal) || WorkspacePath.IsProtected(norm))
            {
                return SafeWriteResult.Fail(ExecutionReasonCodes.ExportRejectedUnsafePath, $"path is not an allowed workspace-relative path: {(string.IsNullOrEmpty(why) ? "protected or non-canonical" : why)}", op.Path);
            }

            switch (op.Kind)
            {
                case GuestChangeKind.Add:
                    if (op.NewBytes is null)
                    {
                        return SafeWriteResult.Fail(ExecutionReasonCodes.InvalidOperation, "Add requires the new content", op.Path);
                    }

                    if (op.ExpectedSha256 is not null)
                    {
                        return SafeWriteResult.Fail(ExecutionReasonCodes.InvalidOperation, "Add must not carry an expected hash (the file must not exist)", op.Path);
                    }

                    normalized.Add(op);
                    break;
                case GuestChangeKind.Modify:
                case GuestChangeKind.Delete:
                    if (op.ExpectedSha256 is null || !Hex64.IsMatch(op.ExpectedSha256))
                    {
                        return SafeWriteResult.Fail(ExecutionReasonCodes.MissingExpectedHash, $"{op.Kind} requires the SHA-256 of the version that was read", op.Path);
                    }

                    if (op.Kind == GuestChangeKind.Modify && op.NewBytes is null)
                    {
                        return SafeWriteResult.Fail(ExecutionReasonCodes.InvalidOperation, "Modify requires the new content", op.Path);
                    }

                    normalized.Add(op with { ExpectedSha256 = op.ExpectedSha256.ToUpperInvariant() });
                    break;
                case GuestChangeKind.Mkdir:
                    normalized.Add(op);
                    break;
                default:
                    return SafeWriteResult.Fail(ExecutionReasonCodes.InvalidOperation, "unknown operation kind", op.Path);
            }
        }

        return null;
    }

    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static SafeWriteResult FromException(Exception ex)
        => ex is SafeWriteAbortException abort
            ? SafeWriteResult.Fail(abort.ReasonCode, abort.Message, abort.FailedPath)
            : SafeWriteResult.Fail(ExecutionReasonCodes.ExportFailed, $"{ex.GetType().Name}: {ex.Message}", string.Empty);
}

// ───────────────────────────── Linux ─────────────────────────────

#pragma warning disable SYSLIB1054 // LibraryImport needs AllowUnsafeBlocks, which this project does not enable.
#pragma warning disable CA5392 // libc is resolved by the loader; there is no app-local copy to point DefaultDllImportSearchPaths at.
internal static class PosixNative
{
    private const int EIO = 5;

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern int OpenAt(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern int MkdirAt(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern int UnlinkAt(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int NativeRenameAt2(int olddirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath, int newdirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath, uint flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern int Close(int fd);

    /// <summary>renameat2 with a fault-injection seam ("renameat2:&lt;flags&gt;") so failure paths of the rollback can be tested.</summary>
    internal static int RenameAt2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags)
    {
        if (SafeTreeWriter.ShouldFail("renameat2:" + flags))
        {
            Marshal.SetLastPInvokeError(EIO);
            return -1;
        }

        return NativeRenameAt2(olddirfd, oldpath, newdirfd, newpath, flags);
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal static class PosixSafeFs
{
    private const int ORdonly = 0;
    private const int OWronly = 1;
    private const int OCreat = 0x40;
    private const int OExcl = 0x80;
    private const int OCloexec = 0x80000;
    private const int AtRemoveDir = 0x200;
    private const uint RenameNoReplace = 1;
    private const uint RenameExchange = 2;
    private const int ENOENT = 2;
    private const int EEXIST = 17;
    private const int ENOTDIR = 20;
    private const int ELOOP = 40;

    private static int ODirectory => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;

    private static int ONoFollow => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;

    /// <summary>State of one batch: directory fds, undo actions and the temp files that must disappear again.</summary>
    private sealed class Batch(int rootFd, string rootPath)
    {
        public int RootFd { get; } = rootFd;
        public string RootPath { get; } = rootPath;
        public Dictionary<string, int> DirFds { get; } = new(StringComparer.Ordinal) { [string.Empty] = rootFd };
        public List<Func<string?>> Undo { get; } = [];
        public List<(int DirFd, string Name)> Temps { get; } = [];
        public List<string> Notes { get; } = [];

        public void Keep(string name) => Temps.RemoveAll(t => t.Name == name);
    }

    public static SafeWriteResult Apply(string root, IReadOnlyList<SafeWriteOp> ops)
    {
        int rootFd;
        try
        {
            rootFd = PosixNative.Open(root, ORdonly | ODirectory | OCloexec, 0);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return SafeWriteResult.Fail(ExecutionReasonCodes.UnsupportedIsolation, "libc entry points for safe publishing are unavailable: " + ex.Message, string.Empty);
        }

        if (rootFd < 0)
        {
            return SafeWriteResult.Fail(ExecutionReasonCodes.ExportFailed, $"cannot open workspace root (errno {Marshal.GetLastPInvokeError()})", string.Empty);
        }

        var rootPath = FdPath(rootFd);
        if (rootPath is null)
        {
            PosixNative.Close(rootFd);
            return SafeWriteResult.Fail(ExecutionReasonCodes.UnsupportedIsolation, "/proc/self/fd is not readable, so directory identity cannot be verified; export is refused.", string.Empty);
        }

        var batch = new Batch(rootFd, rootPath);
        SafeWriteResult result;
        try
        {
            foreach (var op in ops)
            {
                AssertDirectoriesStable(batch, op.Path);
                ApplyOne(op, batch);
            }

            AssertDirectoriesStable(batch, "(end of batch)");
            result = SafeWriteResult.Ok;
        }
        catch (Exception ex)
        {
            // Any failure (not only IO) undoes everything already applied, newest first.
            for (var i = batch.Undo.Count - 1; i >= 0; i--)
            {
                try
                {
                    var note = batch.Undo[i]();
                    if (note is not null)
                    {
                        batch.Notes.Add(note);
                    }
                }
                catch (Exception undoEx)
                {
                    batch.Notes.Add("undo failed: " + undoEx.Message);
                }
            }

            var failed = SafeTreeWriter.FromException(ex);
            result = batch.Notes.Count > 0 ? failed with { Message = failed.Message + " | rollback notes: " + string.Join("; ", batch.Notes) } : failed;
        }
        finally
        {
            // Backup/temp files never stay in the worktree (except one that holds host data a rollback could not restore: Keep()).
            foreach (var (dirFd, name) in batch.Temps)
            {
                PosixNative.UnlinkAt(dirFd, name, 0);
            }

            foreach (var fd in batch.DirFds.Values)
            {
                PosixNative.Close(fd);
            }
        }

        return result;
    }

    private static void ApplyOne(SafeWriteOp op, Batch batch)
    {
        var parts = op.Path.Split('/');
        if (op.Kind == GuestChangeKind.Mkdir)
        {
            ResolveDir(op, parts, batch, create: true);
            return;
        }

        var name = parts[^1];
        // Only an Add may create missing parents; a Modify/Delete of a file whose parent is missing fails without creating anything.
        var dirFd = ResolveDir(op, parts[..^1], batch, create: op.Kind == GuestChangeKind.Add);
        var tmp = ".cfx-" + Guid.NewGuid().ToString("N");

        switch (op.Kind)
        {
            case GuestChangeKind.Add:
            {
                batch.Temps.Add((dirFd, tmp));
                WriteTemp(dirFd, tmp, op.NewBytes!, null, op.Path);
                SafeTreeWriter.Hook("verified:" + op.Path);
                if (PosixNative.RenameAt2(dirFd, tmp, dirFd, name, RenameNoReplace) != 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    throw errno == EEXIST
                        ? new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedConcurrentModification, "file was created on host after the snapshot", op.Path)
                        : new SafeWriteAbortException(ExecutionReasonCodes.ExportFailed, $"rename failed (errno {errno})", op.Path);
                }

                var ours = SafeTreeWriter.Sha(op.NewBytes!);
                batch.Undo.Add(() => HashOf(dirFd, name) == ours
                    ? (PosixNative.UnlinkAt(dirFd, name, 0) == 0 ? null : $"could not undo add of {op.Path}")
                    : $"{op.Path} changed after export; left as is");
                break;
            }

            case GuestChangeKind.Modify:
            {
                var (_, mode) = ReadVerified(dirFd, name, op);
                batch.Temps.Add((dirFd, tmp));
                WriteTemp(dirFd, tmp, op.NewBytes!, mode, op.Path);
                SafeTreeWriter.Hook("verified:" + op.Path);
                if (PosixNative.RenameAt2(dirFd, tmp, dirFd, name, RenameExchange) != 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    throw new SafeWriteAbortException(
                        errno is ENOENT or ELOOP ? ExecutionReasonCodes.ExportRejectedConcurrentModification : ExecutionReasonCodes.ExportFailed,
                        $"exchange failed (errno {errno})", op.Path);
                }

                // `tmp` now holds whatever was at `name` at the instant of the exchange. It must still be the baseline.
                if (HashOf(dirFd, tmp) != op.ExpectedSha256)
                {
                    if (PosixNative.RenameAt2(dirFd, tmp, dirFd, name, RenameExchange) != 0)
                    {
                        // The concurrent writer's file is still in `tmp`: never delete it; keep and report it.
                        batch.Keep(tmp);
                        throw new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedConcurrentModification,
                            $"host file changed between verification and replace and could not be put back; it is preserved as '{tmp}'", op.Path);
                    }

                    throw new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedConcurrentModification, "host file changed between verification and replace; the host version was restored", op.Path);
                }

                var newSha = SafeTreeWriter.Sha(op.NewBytes!);
                batch.Undo.Add(() =>
                {
                    if (HashOf(dirFd, name) != newSha)
                    {
                        return $"{op.Path} changed again after export; left as is";   // the baseline in `tmp` is dropped by the finally block
                    }

                    if (PosixNative.RenameAt2(dirFd, tmp, dirFd, name, RenameExchange) != 0)
                    {
                        batch.Keep(tmp);   // keep the original content rather than losing it
                        return $"could not restore {op.Path}; the original is preserved as '{tmp}'";
                    }

                    return null;
                });
                break;
            }

            case GuestChangeKind.Delete:
            {
                _ = ReadVerified(dirFd, name, op);
                SafeTreeWriter.Hook("verified:" + op.Path);
                batch.Temps.Add((dirFd, tmp));
                if (PosixNative.RenameAt2(dirFd, name, dirFd, tmp, RenameNoReplace) != 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    throw new SafeWriteAbortException(
                        errno is ENOENT ? ExecutionReasonCodes.ExportRejectedConcurrentModification : ExecutionReasonCodes.ExportFailed,
                        $"move-aside failed (errno {errno})", op.Path);
                }

                if (HashOf(dirFd, tmp) != op.ExpectedSha256)
                {
                    if (PosixNative.RenameAt2(dirFd, tmp, dirFd, name, RenameNoReplace) != 0)
                    {
                        batch.Keep(tmp);   // the host's modified file: never delete
                        throw new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedConcurrentModification,
                            $"host file changed between verification and delete and could not be restored; it is preserved as '{tmp}'", op.Path);
                    }

                    throw new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedConcurrentModification, "host file changed between verification and delete; it was restored", op.Path);
                }

                batch.Undo.Add(() => PosixNative.RenameAt2(dirFd, tmp, dirFd, name, RenameNoReplace) == 0
                    ? null
                    : $"could not restore deleted {op.Path} (the host recreated it); the host version stays");
                break;
            }
        }
    }

    private static int ResolveDir(SafeWriteOp op, string[] dirParts, Batch batch, bool create)
    {
        var current = batch.RootFd;
        var key = string.Empty;
        foreach (var part in dirParts)
        {
            key = key.Length == 0 ? part : key + "/" + part;
            if (batch.DirFds.TryGetValue(key, out var cached))
            {
                current = cached;
                continue;
            }

            var fd = PosixNative.OpenAt(current, part, ORdonly | ODirectory | ONoFollow | OCloexec, 0);
            if (fd < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == ENOENT && create)
                {
                    if (PosixNative.MkdirAt(current, part, Convert.ToInt32("755", 8)) != 0 && Marshal.GetLastPInvokeError() != EEXIST)
                    {
                        throw new SafeWriteAbortException(ExecutionReasonCodes.ExportFailed, "mkdir failed", op.Path);
                    }

                    var parentForUndo = current;
                    var createdName = part;
                    batch.Undo.Add(() => PosixNative.UnlinkAt(parentForUndo, createdName, AtRemoveDir) == 0 ? null : $"could not remove created directory '{createdName}'");
                    fd = PosixNative.OpenAt(current, part, ORdonly | ODirectory | ONoFollow | OCloexec, 0);
                    errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
                }

                if (fd < 0)
                {
                    throw errno is ELOOP or ENOTDIR
                        ? new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedLink, "a link or non-directory replaced a path component", op.Path)
                        : new SafeWriteAbortException(errno == ENOENT ? ExecutionReasonCodes.ExportRejectedConcurrentModification : ExecutionReasonCodes.ExportFailed, $"cannot open directory '{part}' (errno {errno})", op.Path);
                }
            }

            batch.DirFds[key] = fd;
            current = fd;
        }

        return current;
    }

    /// <summary>
    /// A cached directory fd keeps pointing at the same inode even after the host renamed/replaced it. Before each operation and
    /// at the end of the batch, every cached fd must still resolve to the path the batch believes it is.
    /// </summary>
    private static void AssertDirectoriesStable(Batch batch, string operationPath)
    {
        var rootNow = FdPath(batch.RootFd);
        if (!string.Equals(rootNow, batch.RootPath, StringComparison.Ordinal))
        {
            throw new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedConcurrentModification, "the workspace root was renamed or replaced during the batch", operationPath);
        }

        foreach (var (key, fd) in batch.DirFds)
        {
            if (key.Length == 0)
            {
                continue;
            }

            var expected = batch.RootPath.TrimEnd('/') + "/" + key;
            var actual = FdPath(fd);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedConcurrentModification,
                    $"directory '{key}' was renamed, replaced or removed during the batch (now {actual ?? "unknown"})", operationPath);
            }
        }
    }

    private static string? FdPath(int fd)
    {
        try
        {
            return new FileInfo($"/proc/self/fd/{fd}").LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (byte[] Bytes, UnixFileMode Mode) ReadVerified(int dirFd, string name, SafeWriteOp op)
    {
        var fd = PosixNative.OpenAt(dirFd, name, ORdonly | ONoFollow | OCloexec, 0);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw errno == ELOOP
                ? new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedLink, "target is a symlink", op.Path)
                : new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedConcurrentModification, "target vanished or is unreadable (errno " + errno + ")", op.Path);
        }

        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        byte[] bytes;
        try
        {
            bytes = ReadAll(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedConcurrentModification, "target is not a regular file: " + ex.Message, op.Path);
        }

        if (SafeTreeWriter.Sha(bytes) != op.ExpectedSha256)
        {
            throw new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedConcurrentModification, "host file changed since the snapshot", op.Path);
        }

        return (bytes, File.GetUnixFileMode(handle));
    }

    private static void WriteTemp(int dirFd, string tmp, byte[] bytes, UnixFileMode? mode, string path)
    {
        var fd = PosixNative.OpenAt(dirFd, tmp, OWronly | OCreat | OExcl | ONoFollow | OCloexec, Convert.ToInt32("600", 8));
        if (fd < 0)
        {
            throw new SafeWriteAbortException(ExecutionReasonCodes.ExportFailed, $"cannot create temp file (errno {Marshal.GetLastPInvokeError()})", path);
        }

        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        RandomAccess.Write(handle, bytes, 0);
        File.SetUnixFileMode(handle, mode ?? (UnixFileMode)Convert.ToInt32("644", 8));
        RandomAccess.FlushToDisk(handle);
    }

    private static string? HashOf(int dirFd, string name)
    {
        var fd = PosixNative.OpenAt(dirFd, name, ORdonly | ONoFollow | OCloexec, 0);
        if (fd < 0)
        {
            return null;
        }

        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            return SafeTreeWriter.Sha(ReadAll(handle));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[] ReadAll(SafeFileHandle handle)
    {
        var length = RandomAccess.GetLength(handle);
        var buffer = new byte[length];
        long offset = 0;
        while (offset < length)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan((int)offset), offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset == length ? buffer : buffer[..(int)offset];
    }
}

// ──────────────────────────── Windows ────────────────────────────

internal static class WindowsNative
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint Delete = 0x00010000;
    internal const uint ShareRead = 1;
    internal const uint ShareWrite = 2;
    internal const uint CreateNew = 1;
    internal const uint OpenExisting = 3;
    internal const uint FlagBackupSemantics = 0x02000000;
    internal const uint FlagOpenReparsePoint = 0x00200000;
    internal const uint AttributeNormal = 0x80;
    internal const uint AttributeDirectory = 0x10;
    internal const uint AttributeReparsePoint = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FileDispositionInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] buffer, uint size, uint flags);
}
#pragma warning restore CA5392
#pragma warning restore SYSLIB1054

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WindowsSafeFs
{
    private const int FileDispositionInfoClass = 4;
    private const int ErrorSharingViolation = 32;
    private const int ErrorFileExists = 80;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    private sealed class Locked(SafeWriteOp op, FileStream stream, byte[]? backup)
    {
        public SafeWriteOp Op { get; } = op;
        public FileStream Stream { get; } = stream;
        public byte[]? Backup { get; } = backup;
        public bool Written { get; set; }
        public bool DeleteFlagged { get; set; }
    }

    private static string Long(string path) => path.Length > 240 && !path.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\\?\" + path : path;

    public static SafeWriteResult Apply(string root, IReadOnlyList<SafeWriteOp> ops)
    {
        var pins = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
        var createdDirs = new List<string>();
        var locked = new List<Locked>();
        SafeWriteResult result;
        try
        {
            // The root's FINAL path is the base of every expected path: short names / aliases in the caller's string never count.
            var rootPath = PinDirectory(root, pins, createIfMissing: false, op: null, expectedFinalPath: null, createdDirs: null);
            foreach (var op in ops)
            {
                var parts = op.Path.Split('/');
                var dirParts = op.Kind == GuestChangeKind.Mkdir ? parts : parts[..^1];
                var dir = rootPath;
                foreach (var part in dirParts)
                {
                    dir = Path.Combine(dir, part);
                    if (!pins.ContainsKey(dir))
                    {
                        PinDirectory(dir, pins, createIfMissing: op.Kind is GuestChangeKind.Add or GuestChangeKind.Mkdir, op, expectedFinalPath: dir, createdDirs);
                    }
                }

                if (op.Kind == GuestChangeKind.Mkdir)
                {
                    continue;
                }

                locked.Add(LockTarget(op, Path.Combine(dir, parts[^1])));
            }

            SafeTreeWriter.Hook("verified:*");

            // Everything is pinned, exclusively locked and verified: now write. Nothing else can see or touch these files.
            foreach (var item in locked)
            {
                switch (item.Op.Kind)
                {
                    case GuestChangeKind.Add:
                    case GuestChangeKind.Modify:
                        item.Written = true;   // from here on a failure must restore the original (a truncated file is "written")
                        item.Stream.SetLength(0);
                        SafeTreeWriter.Hook("writing:" + item.Op.Path);
                        item.Stream.Write(item.Op.NewBytes!, 0, item.Op.NewBytes!.Length);
                        item.Stream.Flush(flushToDisk: true);
                        break;
                    case GuestChangeKind.Delete:
                    case GuestChangeKind.Mkdir:
                        break;
                }
            }

            foreach (var item in locked.Where(l => l.Op.Kind == GuestChangeKind.Delete))
            {
                SafeTreeWriter.Hook("deleting:" + item.Op.Path);
                var info = new WindowsNative.FileDispositionInfo { DeleteFile = true };
                if (!WindowsNative.SetFileInformationByHandle(item.Stream.SafeFileHandle, FileDispositionInfoClass, ref info, (uint)Marshal.SizeOf<WindowsNative.FileDispositionInfo>()))
                {
                    throw new SafeWriteAbortException(ExecutionReasonCodes.ExportFailed, "delete failed (win32 " + Marshal.GetLastWin32Error() + ")", item.Op.Path);
                }

                item.DeleteFlagged = true;
            }

            result = SafeWriteResult.Ok;
        }
        catch (Exception ex)
        {
            Rollback(locked);
            result = SafeTreeWriter.FromException(ex);
        }
        finally
        {
            foreach (var item in locked)
            {
                item.Stream.Dispose();
            }

            foreach (var pin in pins.Values)
            {
                pin.Dispose();
            }
        }

        if (!result.Success)
        {
            // Directories this failed batch created are removed (children first); pins are released by now.
            for (var i = createdDirs.Count - 1; i >= 0; i--)
            {
                try
                {
                    Directory.Delete(createdDirs[i], recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result = result with { Message = result.Message + $" | rollback notes: could not remove created directory '{createdDirs[i]}'" };
                }
            }
        }

        return result;
    }

    private static void Rollback(List<Locked> locked)
    {
        foreach (var item in locked)
        {
            try
            {
                if (item.Op.Kind == GuestChangeKind.Add)
                {
                    SetDeleteDisposition(item.Stream.SafeFileHandle, true);   // the file this batch created
                }
                else if (item.Op.Kind == GuestChangeKind.Modify && item.Written && item.Backup is not null)
                {
                    item.Stream.SetLength(0);
                    item.Stream.Write(item.Backup, 0, item.Backup.Length);
                    item.Stream.Flush(flushToDisk: true);
                }
                else if (item.Op.Kind == GuestChangeKind.Delete && item.DeleteFlagged)
                {
                    SetDeleteDisposition(item.Stream.SafeFileHandle, false);   // un-flag: handles close without deleting it
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // best effort; the failure result already tells the caller the export did not complete
            }
        }
    }

    private static void SetDeleteDisposition(SafeFileHandle handle, bool delete)
    {
        var info = new WindowsNative.FileDispositionInfo { DeleteFile = delete };
        WindowsNative.SetFileInformationByHandle(handle, FileDispositionInfoClass, ref info, (uint)Marshal.SizeOf<WindowsNative.FileDispositionInfo>());
    }

    /// <summary>The path Windows itself reports for a handle (long names, no 8.3 aliases, no \\?\ prefix).</summary>
    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        var length = WindowsNative.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            throw new SafeWriteAbortException(ExecutionReasonCodes.ExportFailed, "cannot resolve the final path of a handle (win32 " + Marshal.GetLastWin32Error() + ")", string.Empty);
        }

        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + path[@"\\?\UNC\".Length..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>Pins the directory and returns its FINAL path. For non-root directories the final path must equal the expected one.</summary>
    private static string PinDirectory(
        string path, Dictionary<string, SafeFileHandle> pins, bool createIfMissing, SafeWriteOp? op, string? expectedFinalPath, List<string>? createdDirs)
    {
        // Share read+write but NOT delete: the directory cannot be renamed, deleted or replaced while we hold the handle.
        SafeFileHandle Open() => WindowsNative.CreateFile(
            Long(path), WindowsNative.GenericRead, WindowsNative.ShareRead | WindowsNative.ShareWrite, IntPtr.Zero,
            WindowsNative.OpenExisting, WindowsNative.FlagBackupSemantics | WindowsNative.FlagOpenReparsePoint, IntPtr.Zero);

        var handle = Open();
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if ((error is ErrorFileNotFound or ErrorPathNotFound) && createIfMissing)
            {
                Directory.CreateDirectory(path);
                createdDirs?.Add(path);
                handle = Open();
                error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            }

            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new SafeWriteAbortException(
                    error is ErrorFileNotFound or ErrorPathNotFound ? ExecutionReasonCodes.ExportRejectedConcurrentModification : ExecutionReasonCodes.ExportFailed,
                    $"cannot pin directory (win32 {error})", op?.Path ?? path);
            }
        }

        try
        {
            if (!WindowsNative.GetFileInformationByHandle(handle, out var info))
            {
                throw new SafeWriteAbortException(ExecutionReasonCodes.ExportFailed, "cannot inspect directory", op?.Path ?? path);
            }

            if ((info.FileAttributes & WindowsNative.AttributeReparsePoint) != 0 || (info.FileAttributes & WindowsNative.AttributeDirectory) == 0)
            {
                throw new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedLink, "a link/junction or non-directory is on the path", op?.Path ?? path);
            }

            var finalPath = FinalPath(handle).TrimEnd(Path.DirectorySeparatorChar);
            if (expectedFinalPath is not null && !string.Equals(finalPath, expectedFinalPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                // An 8.3 alias (GIT~1), a case/short-name trick or any other alias resolves somewhere else than the name says.
                throw new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedUnsafePath, $"path component resolves to '{finalPath}' instead of the expected '{expectedFinalPath}' (alias)", op?.Path ?? path);
            }

            pins[finalPath] = handle;
            return finalPath;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static Locked LockTarget(SafeWriteOp op, string full)
    {
        var create = op.Kind == GuestChangeKind.Add;
        var access = WindowsNative.GenericWrite | WindowsNative.Delete | WindowsNative.GenericRead;

        var handle = WindowsNative.CreateFile(
            Long(full), access, 0, IntPtr.Zero,
            create ? WindowsNative.CreateNew : WindowsNative.OpenExisting,
            WindowsNative.FlagOpenReparsePoint | WindowsNative.AttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw error switch
            {
                ErrorFileExists => new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedConcurrentModification, "file was created on host after the snapshot", op.Path),
                ErrorSharingViolation => new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedConcurrentModification, "file is in use by another process", op.Path),
                ErrorFileNotFound or ErrorPathNotFound => new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedConcurrentModification, "host file vanished since the snapshot", op.Path),
                _ => new SafeWriteAbortException(ExecutionReasonCodes.ExportFailed, $"cannot lock target (win32 {error})", op.Path)
            };
        }

        FileStream? stream = null;
        try
        {
            if (!WindowsNative.GetFileInformationByHandle(handle, out var info) ||
                (info.FileAttributes & (WindowsNative.AttributeReparsePoint | WindowsNative.AttributeDirectory)) != 0)
            {
                throw new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedLink, "target is a link or a directory", op.Path);
            }

            var finalPath = FinalPath(handle);
            if (!string.Equals(finalPath, full, StringComparison.OrdinalIgnoreCase))
            {
                if (create)
                {
                    // we just created an aliased name: undo that file before refusing
                    SetDeleteDisposition(handle, true);
                }

                throw new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedUnsafePath, $"file resolves to '{finalPath}' instead of the expected '{full}' (alias)", op.Path);
            }

            stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
            byte[]? backup = null;
            if (!create)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                backup = ms.ToArray();
                if (SafeTreeWriter.Sha(backup) != op.ExpectedSha256)
                {
                    throw new SafeWriteAbortException(ExecutionReasonCodes.ExportRejectedConcurrentModification, "host file changed since the snapshot", op.Path);
                }

                stream.Position = 0;
            }

            SafeTreeWriter.Hook("verified:" + op.Path);
            return new Locked(op, stream, backup);
        }
        catch
        {
            if (create)
            {
                // a file this call just created must not survive a failed lock/verify step
                try
                {
                    SetDeleteDisposition(handle, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                }
            }

            if (stream is not null)
            {
                stream.Dispose();
            }
            else
            {
                handle.Dispose();
            }

            throw;
        }
    }
}
