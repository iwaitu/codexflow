// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace CodexFlow.Core.Browser;

internal sealed record StagedUploadFile(
    string CanonicalSourcePath,
    string StagedFilePath,
    string TempSubDir,
    long SizeBytes,
    string Sha256Hex);

/// <summary>
/// OS-level canonical real-path validation and immutable staging for workspace file uploads and
/// controlled browser downloads. Defeats multi-hop directory symlinks/junctions, hardlinks, and TOCTOU swaps.
/// </summary>
internal static class BrowserWorkspacePathGuard
{
    public static async Task<(StagedUploadFile? Staged, BrowserOperationResult? Error)> StageWorkspaceUploadFileAsync(
        string? workspaceRoot,
        string? requestedPath,
        string sessionStagingRoot,
        long maxBytes,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return (null, Fail("workspace_root_missing", "当前会话未绑定工作区路径，禁止执行本地文件上传。"));
        }

        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return (null, Fail("missing_file_path", "动作 'upload_file' 必须提供位于当前工作区内的 file_path 参数。"));
        }

        if (string.IsNullOrWhiteSpace(sessionStagingRoot))
        {
            return (null, Fail("staging_root_missing", "浏览器会话暂存目录未配置。"));
        }

        if (!TryResolveCanonicalExistingPath(
                workspaceRoot.Trim(),
                isDirectory: true,
                rejectHardLinks: false,
                out var canonicalRoot,
                out var rootErrCode,
                out var rootErrMsg))
        {
            return (null, Fail(rootErrCode ?? "workspace_root_invalid", rootErrMsg ?? "无法解析当前工作区真实路径。"));
        }

        if (IsFilesystemRoot(canonicalRoot))
        {
            return (null, Fail("workspace_root_forbidden", "安全策略禁止以文件系统根目录作为浏览器上传工作区边界。"));
        }

        string rawCandidate;
        try
        {
            var trimmedReq = requestedPath.Trim();
            rawCandidate = Path.IsPathRooted(trimmedReq)
                ? Path.GetFullPath(trimmedReq)
                : Path.GetFullPath(Path.Combine(canonicalRoot, trimmedReq));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, Fail("upload_path_invalid", $"无效的文件路径：{ex.Message}"));
        }

        var comparison = GetPathComparison();
        if (!IsStrictlyInsideRoot(rawCandidate, canonicalRoot, comparison))
        {
            return (null, Fail("upload_path_outside_workspace", "安全策略禁止上传当前工作区以外的文件。"));
        }

        if (!File.Exists(rawCandidate))
        {
            return (null, Fail("upload_file_not_found", $"待上传文件不存在：{requestedPath}"));
        }

        if (!TryResolveCanonicalExistingPath(
                rawCandidate,
                isDirectory: false,
                rejectHardLinks: true,
                out var canonicalFile,
                out var fileErrCode,
                out var fileErrMsg))
        {
            return (null, Fail(fileErrCode ?? "upload_path_invalid", fileErrMsg ?? $"无法解析待上传文件真实路径：{requestedPath}"));
        }

        if (!IsStrictlyInsideRoot(canonicalFile, canonicalRoot, comparison))
        {
            return (null, Fail("upload_symlink_escape", "安全策略拒绝通过符号链接或目录连接点上传工作区外部文件。"));
        }

        var tempSubDir = Path.Combine(sessionStagingRoot, Guid.NewGuid().ToString("N")[..12]);
        var originalName = Path.GetFileName(canonicalFile);
        if (string.IsNullOrWhiteSpace(originalName))
        {
            originalName = "upload.bin";
        }

        var stagedFilePath = Path.Combine(tempSubDir, originalName);

        try
        {
            Directory.CreateDirectory(tempSubDir);

            await using var sourceStream = new FileStream(
                canonicalFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);

            if (OperatingSystem.IsWindows())
            {
                var openedFinalPath = ResolveWindowsHandleFinalPath(sourceStream.SafeFileHandle);
                if (string.IsNullOrWhiteSpace(openedFinalPath) ||
                    !string.Equals(TrimTrailingSeparators(openedFinalPath), canonicalFile, comparison) ||
                    !IsStrictlyInsideRoot(openedFinalPath, canonicalRoot, comparison))
                {
                    TryDeleteDirectory(tempSubDir);
                    return (null, Fail("upload_symlink_escape", "安全策略检测到文件在读取瞬间指向工作区外部或发生了路径切换。"));
                }

                if (GetWindowsHardLinkCount(sourceStream.SafeFileHandle) > 1)
                {
                    TryDeleteDirectory(tempSubDir);
                    return (null, Fail("upload_hardlink_forbidden", "安全策略禁止上传存在多个硬链接的本地文件以防工作区外敏感文件泄露。"));
                }
            }
            else if (GetUnixHardLinkCount(canonicalFile) > 1)
            {
                TryDeleteDirectory(tempSubDir);
                return (null, Fail("upload_hardlink_forbidden", "安全策略禁止上传存在多个硬链接的本地文件以防工作区外敏感文件泄露。"));
            }

            if (sourceStream.Length > maxBytes)
            {
                TryDeleteDirectory(tempSubDir);
                return (null, Fail("upload_file_too_large", "待上传文件超过 50MB 安全限制。"));
            }

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var destStream = new FileStream(
                stagedFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true);

            var buffer = new byte[64 * 1024];
            long totalRead = 0;
            while (true)
            {
                var read = await sourceStream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
                if (totalRead > maxBytes)
                {
                    await destStream.DisposeAsync().ConfigureAwait(false);
                    TryDeleteDirectory(tempSubDir);
                    return (null, Fail("upload_file_too_large", "待上传文件在读取过程中超过 50MB 安全限制。"));
                }

                hasher.AppendData(buffer, 0, read);
                await destStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            await destStream.FlushAsync(ct).ConfigureAwait(false);
            var shaHex = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            return (new StagedUploadFile(canonicalFile, stagedFilePath, tempSubDir, totalRead, shaHex), null);
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(tempSubDir);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            TryDeleteDirectory(tempSubDir);
            return (null, Fail("upload_read_failed", $"读取并暂存待上传文件失败：{ex.Message}"));
        }
    }

    public static bool TryPrepareControlledDownloadDirectory(string? workspaceRoot, out string? canonicalDownloadDir)
    {
        canonicalDownloadDir = null;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return false;
        }

        if (!TryResolveCanonicalExistingPath(
                workspaceRoot.Trim(),
                isDirectory: true,
                rejectHardLinks: false,
                out var canonicalRoot,
                out _,
                out _) ||
            IsFilesystemRoot(canonicalRoot))
        {
            return false;
        }

        var comparison = GetPathComparison();
        var outputsDir = Path.Combine(canonicalRoot, "outputs");
        try
        {
            Directory.CreateDirectory(outputsDir);
            if (!TryResolveCanonicalExistingPath(
                    outputsDir,
                    isDirectory: true,
                    rejectHardLinks: false,
                    out var canonicalOutputsDir,
                    out _,
                    out _) ||
                !IsStrictlyInsideRoot(canonicalOutputsDir, canonicalRoot, comparison))
            {
                return false;
            }

            var targetDir = Path.Combine(canonicalOutputsDir, "browser-downloads");
            Directory.CreateDirectory(targetDir);
            if (!TryResolveCanonicalExistingPath(
                    targetDir,
                    isDirectory: true,
                    rejectHardLinks: false,
                    out var resolvedTarget,
                    out _,
                    out _) ||
                !IsStrictlyInsideRoot(resolvedTarget, canonicalRoot, comparison))
            {
                return false;
            }

            canonicalDownloadDir = resolvedTarget;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool TryResolveCanonicalExistingPath(
        string rawPath,
        bool isDirectory,
        bool rejectHardLinks,
        out string canonicalPath,
        out string? errorCode,
        out string? errorMessage)
    {
        canonicalPath = string.Empty;
        errorCode = null;
        errorMessage = null;

        try
        {
            var fullPath = TrimTrailingSeparators(Path.GetFullPath(rawPath));
            if (OperatingSystem.IsWindows())
            {
                return TryResolveCanonicalWindows(fullPath, isDirectory, rejectHardLinks, out canonicalPath, out errorCode, out errorMessage);
            }

            return TryResolveCanonicalUnix(fullPath, isDirectory, rejectHardLinks, out canonicalPath, out errorCode, out errorMessage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            errorCode = "upload_path_invalid";
            errorMessage = $"路径解析失败：{ex.Message}";
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryResolveCanonicalWindows(
        string fullPath,
        bool isDirectory,
        bool rejectHardLinks,
        out string canonicalPath,
        out string? errorCode,
        out string? errorMessage)
    {
        canonicalPath = string.Empty;
        errorCode = null;
        errorMessage = null;

        var flags = isDirectory ? WindowsNative.FlagBackupSemantics : 0u;
        using var handle = WindowsNative.CreateFile(
            fullPath,
            WindowsNative.GenericRead,
            WindowsNative.ShareRead | WindowsNative.ShareWrite,
            IntPtr.Zero,
            WindowsNative.OpenExisting,
            flags,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            errorCode = isDirectory ? "workspace_root_invalid" : "upload_file_not_found";
            errorMessage = string.Create(CultureInfo.InvariantCulture, $"无法打开目标路径进行安全校验 (win32={err})。");
            return false;
        }

        if (!isDirectory && rejectHardLinks && GetWindowsHardLinkCount(handle) > 1)
        {
            errorCode = "upload_hardlink_forbidden";
            errorMessage = "安全策略禁止上传存在多个硬链接的本地文件以防工作区外敏感文件泄露。";
            return false;
        }

        var resolved = ResolveWindowsHandleFinalPath(handle);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            errorCode = "upload_path_invalid";
            errorMessage = "无法解析 Windows 最终物理路径。";
            return false;
        }

        canonicalPath = TrimTrailingSeparators(Path.GetFullPath(resolved));
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static string? ResolveWindowsHandleFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[4096];
        var len = WindowsNative.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (len == 0)
        {
            return null;
        }

        if (len >= buffer.Length)
        {
            buffer = new char[len + 1];
            len = WindowsNative.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (len == 0 || len >= buffer.Length)
            {
                return null;
            }
        }

        var raw = new string(buffer, 0, (int)len);
        if (raw.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + raw[8..];
        }

        if (raw.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return raw[4..];
        }

        return raw;
    }

    [SupportedOSPlatform("windows")]
    private static uint GetWindowsHardLinkCount(SafeFileHandle handle)
    {
        if (WindowsNative.GetFileInformationByHandle(handle, out var info))
        {
            return info.NumberOfLinks;
        }

        return 1;
    }

    private static bool TryResolveCanonicalUnix(
        string fullPath,
        bool isDirectory,
        bool rejectHardLinks,
        out string canonicalPath,
        out string? errorCode,
        out string? errorMessage)
    {
        canonicalPath = string.Empty;
        errorCode = null;
        errorMessage = null;

        var resolved = ResolveRealPathStepByStepUnix(fullPath, maxHops: 40);
        if (resolved is null)
        {
            errorCode = "upload_symlink_escape";
            errorMessage = "符号链接层级过深、存在环或指向无效路径。";
            return false;
        }

        if (isDirectory && !Directory.Exists(resolved))
        {
            errorCode = "workspace_root_invalid";
            errorMessage = "工作区目录不存在。";
            return false;
        }

        if (!isDirectory)
        {
            if (!File.Exists(resolved))
            {
                errorCode = "upload_file_not_found";
                errorMessage = "待上传文件不存在。";
                return false;
            }

            if (rejectHardLinks && GetUnixHardLinkCount(resolved) > 1)
            {
                errorCode = "upload_hardlink_forbidden";
                errorMessage = "安全策略禁止上传存在多个硬链接的本地文件以防工作区外敏感文件泄露。";
                return false;
            }
        }

        canonicalPath = TrimTrailingSeparators(Path.GetFullPath(resolved));
        return true;
    }

    private static string? ResolveRealPathStepByStepUnix(string fullPath, int maxHops)
    {
        var pending = new Stack<string>();
        PushSegments(pending, fullPath);
        var current = Path.GetPathRoot(fullPath) ?? "/";
        var hops = 0;

        while (pending.Count > 0)
        {
            var segment = pending.Pop();
            if (segment is "." or "")
            {
                continue;
            }

            if (segment == "..")
            {
                var parent = Path.GetDirectoryName(TrimTrailingSeparators(current));
                if (!string.IsNullOrEmpty(parent))
                {
                    current = parent;
                }

                continue;
            }

            var next = Path.Combine(current, segment);
            var info = Directory.Exists(next) ? (FileSystemInfo)new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists && info.LinkTarget is null)
            {
                return null;
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
            {
                if (++hops > maxHops)
                {
                    return null;
                }

                var linkTarget = info.LinkTarget;
                if (string.IsNullOrWhiteSpace(linkTarget))
                {
                    linkTarget = info.ResolveLinkTarget(returnFinalTarget: false)?.FullName;
                }

                if (string.IsNullOrWhiteSpace(linkTarget))
                {
                    return null;
                }

                if (Path.IsPathRooted(linkTarget))
                {
                    var normalizedTarget = Path.GetFullPath(linkTarget);
                    current = Path.GetPathRoot(normalizedTarget) ?? "/";
                    PushSegments(pending, normalizedTarget);
                }
                else
                {
                    PushSegments(pending, linkTarget);
                }
            }
            else
            {
                current = next;
            }
        }

        return current;
    }

    private static void PushSegments(Stack<string> stack, string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var remainder = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
        var parts = remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            stack.Push(parts[i]);
        }
    }

    private static int GetUnixHardLinkCount(string canonicalFilePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return 1;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsMacOS() ? "/usr/bin/stat" : "stat",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("%l");
            }
            else
            {
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("%h");
            }
            psi.ArgumentList.Add(canonicalFilePath);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return 1;
            }

            if (!proc.WaitForExit(1500))
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
                return 1;
            }

            var output = proc.StandardOutput.ReadToEnd().Trim();
            if (proc.ExitCode == 0 && int.TryParse(output, NumberStyles.Integer, CultureInfo.InvariantCulture, out var links))
            {
                return links;
            }
        }
        catch (Exception)
        {
        }

        return 1;
    }

    internal static StringComparison GetPathComparison()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    internal static bool IsStrictlyInsideRoot(string candidatePath, string canonicalRoot, StringComparison comparison)
    {
        var trimmed = TrimTrailingSeparators(Path.GetFullPath(candidatePath));
        return trimmed.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, comparison) ||
               trimmed.StartsWith(canonicalRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static bool IsFilesystemRoot(string canonicalPath)
    {
        var trimmed = TrimTrailingSeparators(canonicalPath);
        var root = TrimTrailingSeparators(Path.GetPathRoot(canonicalPath) ?? string.Empty);
        return string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, root, GetPathComparison());
    }

    private static string TrimTrailingSeparators(string path)
    {
        var root = Path.GetPathRoot(path);
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root is not null && trimmed.Length < root.Length)
        {
            return root;
        }

        return trimmed;
    }

    internal static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private static BrowserOperationResult Fail(string code, string message)
        => new(Succeeded: false, Output: $"[{code}] {message}", Summary: message, ErrorCode: code);

#pragma warning disable SYSLIB1054
#pragma warning disable CA5392
    private static class WindowsNative
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint ShareRead = 1;
        internal const uint ShareWrite = 2;
        internal const uint OpenExisting = 3;
        internal const uint FlagBackupSemantics = 0x02000000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeFileHandle CreateFile(
            string name,
            uint access,
            uint share,
            IntPtr security,
            uint disposition,
            uint flags,
            IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] buffer, uint size, uint flags);
    }
#pragma warning restore CA5392
#pragma warning restore SYSLIB1054
}
