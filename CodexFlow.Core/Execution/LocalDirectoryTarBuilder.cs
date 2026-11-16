// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexFlow.Core.Execution;

public sealed record LocalTarBuildResult(
    bool Success,
    IReadOnlyList<ExportRejection> Rejections);

/// <summary>
/// Builds a tar stream from a local staging directory while enforcing Windows and cross-platform
/// path normalization rules BEFORE handing the tar stream to <see cref="GuestTarAnalyzer"/>:
/// - Refuses all reparse points (symlinks, junctions, mount points) without following them.
/// - Refuses NTFS alternate data streams, reserved device names, trailing dots/spaces, and 8.3 short-name aliases.
/// - Refuses two entries in the same directory whose names differ only by case.
/// - Refuses protected paths (e.g. .git) case-insensitively.
/// </summary>
public static class LocalDirectoryTarBuilder
{
    public static LocalTarBuildResult BuildTar(
        string workspaceDirectory,
        Stream outputTarStream,
        ExecutionResourceLimits limits,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceDirectory);
        ArgumentNullException.ThrowIfNull(outputTarStream);
        ArgumentNullException.ThrowIfNull(limits);

        var root = Path.GetFullPath(workspaceDirectory);
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists)
        {
            return new LocalTarBuildResult(
                false,
                [new ExportRejection(string.Empty, ExecutionReasonCodes.WorkspaceNotFound, $"Staging workspace not found: {root}")]);
        }

        if (rootInfo.LinkTarget is not null || rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return new LocalTarBuildResult(
                false,
                [new ExportRejection(string.Empty, ExecutionReasonCodes.ExportRejectedLink, "staging workspace root is a reparse point or link")]);
        }

        if (OperatingSystem.IsWindows() && HasAlternateDataStream(root))
        {
            return new LocalTarBuildResult(
                false,
                [new ExportRejection(string.Empty, ExecutionReasonCodes.ExportRejectedUnsafePath, "NTFS alternate data stream is not allowed on workspace root")]);
        }

        var rejections = new List<ExportRejection>();
        var filesSeen = 0;
        long bytesSeen = 0;

        using var writer = new TarWriter(outputTarStream, TarEntryFormat.Pax, leaveOpen: true);
        var pending = new Stack<(string FullPath, string RelPath)>();
        pending.Push((root, string.Empty));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dirPath, relDir) = pending.Pop();

            FileSystemInfo[] children;
            try
            {
                children = new DirectoryInfo(dirPath).EnumerateFileSystemInfos().ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rejections.Add(new ExportRejection(relDir, ExecutionReasonCodes.ExportFailed, ex.Message));
                return new LocalTarBuildResult(false, rejections);
            }

            var siblingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                var name = child.Name;
                var rel = relDir.Length == 0 ? name : relDir + "/" + name;

                if (!siblingNames.Add(name))
                {
                    rejections.Add(new ExportRejection(rel, ExecutionReasonCodes.ExportRejectedUnsafePath, "case-colliding entries in the same directory"));
                    return new LocalTarBuildResult(false, rejections);
                }

                if (child.LinkTarget is not null || child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    rejections.Add(new ExportRejection(rel, ExecutionReasonCodes.ExportRejectedLink, "reparse point or symbolic link is not allowed"));
                    return new LocalTarBuildResult(false, rejections);
                }

                if (OperatingSystem.IsWindows() && IsWindowsShortNameAlias(child.FullName, name))
                {
                    rejections.Add(new ExportRejection(rel, ExecutionReasonCodes.ExportRejectedUnsafePath, "8.3 short-name alias is not allowed"));
                    return new LocalTarBuildResult(false, rejections);
                }

                if (OperatingSystem.IsWindows() && HasAlternateDataStream(child.FullName))
                {
                    rejections.Add(new ExportRejection(rel, ExecutionReasonCodes.ExportRejectedUnsafePath, "NTFS alternate data stream is not allowed"));
                    return new LocalTarBuildResult(false, rejections);
                }

                if (WorkspacePath.IsGeneratedSegment(name))
                {
                    continue;
                }

                if (!WorkspacePath.TryNormalizeStrict(rel, out var normalized, out var reason))
                {
                    rejections.Add(new ExportRejection(rel, ExecutionReasonCodes.ExportRejectedUnsafePath, reason));
                    return new LocalTarBuildResult(false, rejections);
                }

                if (WorkspacePath.IsProtected(normalized))
                {
                    rejections.Add(new ExportRejection(normalized, ExecutionReasonCodes.ExportRejectedProtectedPath, "protected path is not allowed"));
                    return new LocalTarBuildResult(false, rejections);
                }

                if (child is DirectoryInfo)
                {
                    var dirEntry = new PaxTarEntry(TarEntryType.Directory, normalized + "/")
                    {
                        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    };
                    writer.WriteEntry(dirEntry);
                    pending.Push((child.FullName, normalized));
                    continue;
                }

                if (child is FileInfo file)
                {
                    filesSeen++;
                    if (filesSeen > limits.MaxFiles)
                    {
                        rejections.Add(new ExportRejection(normalized, ExecutionReasonCodes.ExportRejectedQuota, "staging file count exceeds quota"));
                        return new LocalTarBuildResult(false, rejections);
                    }

                    var length = file.Length;
                    bytesSeen += length;
                    if (bytesSeen > limits.MaxWorkspaceBytes)
                    {
                        rejections.Add(new ExportRejection(normalized, ExecutionReasonCodes.ExportRejectedQuota, "staging workspace size exceeds quota"));
                        return new LocalTarBuildResult(false, rejections);
                    }

                    using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (HasMultipleHardLinks(file.FullName, fs.SafeFileHandle))
                    {
                        rejections.Add(new ExportRejection(normalized, ExecutionReasonCodes.ExportRejectedLink, "hard link is not allowed"));
                        return new LocalTarBuildResult(false, rejections);
                    }

                    var entry = new PaxTarEntry(TarEntryType.RegularFile, normalized)
                    {
                        DataStream = fs,
                        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                    };
                    writer.WriteEntry(entry);
                }
            }
        }

        return new LocalTarBuildResult(true, rejections);
    }

    internal static bool IsWindowsShortNameAlias(string fullPath, string expectedName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var buffer = new StringBuilder(1024);
        var len = GetLongPathNameW(fullPath, buffer, (uint)buffer.Capacity);
        if (len == 0 || len >= buffer.Capacity)
        {
            return false;
        }

        var longName = Path.GetFileName(buffer.ToString());
        return !string.IsNullOrEmpty(longName) && !string.Equals(longName, expectedName, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetLongPathNameW(
        string lpszShortPath,
        [Out] char[] lpszLongPath,
        uint cchBuffer);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    internal static bool HasMultipleHardLinks(string fullPath, SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            // Fail-closed: if link count cannot be verified, refuse export.
            if (!GetFileInformationByHandle(handle, out var info))
            {
                return true;
            }

            return info.NumberOfLinks > 1;
        }

        // Cross-platform Unix: on Linux query /proc/<pid>/fd/<fd> with -L so stat targets the exact open handle (no TOCTOU).
        if (OperatingSystem.IsLinux())
        {
            var addedRef = false;
            try
            {
                handle.DangerousAddRef(ref addedRef);
                var fd = handle.DangerousGetHandle().ToInt64();
                var procFd = $"/proc/{Environment.ProcessId}/fd/{fd}";
                var linuxStat = HostProcessRunner.RunAsync(
                    new HostProcessInvocation("stat", ["-L", "-c", "%h", procFd]) { Timeout = TimeSpan.FromSeconds(5) },
                    CancellationToken.None).GetAwaiter().GetResult();
                if (!linuxStat.Started || linuxStat.ExitCode != 0 || !uint.TryParse(linuxStat.Stdout.Trim(), out var linuxLinks))
                {
                    return true;
                }

                return linuxLinks > 1;
            }
            finally
            {
                if (addedRef)
                {
                    handle.DangerousRelease();
                }
            }
        }

        var statArgs = OperatingSystem.IsMacOS()
            ? new[] { "-f", "%l", fullPath }
            : new[] { "-c", "%h", fullPath };
        var result = HostProcessRunner.RunAsync(
            new HostProcessInvocation("stat", statArgs) { Timeout = TimeSpan.FromSeconds(5) },
            CancellationToken.None).GetAwaiter().GetResult();
        if (!result.Started || result.ExitCode != 0 || !uint.TryParse(result.Stdout.Trim(), out var links))
        {
            return true;
        }

        return links > 1;
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr FindFirstStreamW(
        string lpFileName,
        int infoLevel,
        out Win32FindStreamData lpFindStreamData,
        uint dwFlags);

    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(
        IntPtr hFindStream,
        out Win32FindStreamData lpFindStreamData);

    [DllImport("kernel32.dll", EntryPoint = "FindClose", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string cStreamName;
    }

    internal static bool HasAlternateDataStream(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var invalidHandle = new IntPtr(-1);
        var handle = FindFirstStreamW(fullPath, 0 /* FindStreamInfoStandard */, out var data, 0);
        if (handle == invalidHandle)
        {
            return false;
        }

        try
        {
            do
            {
                var streamName = data.cStreamName;
                if (!string.IsNullOrEmpty(streamName) &&
                    !string.Equals(streamName, "::$DATA", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(streamName, "::$INDEX_ALLOCATION", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            while (FindNextStreamW(handle, out data));
        }
        finally
        {
            FindClose(handle);
        }

        return false;
    }

    private static uint GetLongPathNameW(string shortPath, StringBuilder buffer, uint capacity)
    {
        var chars = new char[capacity];
        var len = GetLongPathNameW(shortPath, chars, capacity);
        if (len > 0 && len < capacity)
        {
            buffer.Append(chars, 0, (int)len);
        }

        return len;
    }
}
