// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexFlow.Core.Documents;

public sealed record LibreOfficeConversionResult(
    bool Success,
    string? ConvertedFilePath,
    string? TempDirectoryToCleanup,
    bool BinaryAvailable,
    string? ErrorMessage);

/// <summary>
/// Discovers the bundled or system-installed LibreOffice (<c>soffice</c>) binary and runs isolated headless format conversions.
/// </summary>
public interface ILibreOfficeDocumentConverter
{
    string? ResolveExecutablePath();

    Task<LibreOfficeConversionResult> ConvertAsync(
        string sourceFilePath,
        string targetExtension,
        CancellationToken ct = default);
}

public sealed class LibreOfficeDocumentConverter : ILibreOfficeDocumentConverter
{
    public const string LibreOfficePathEnvVar = "CODEXFLOW_LIBREOFFICE_PATH";
    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromSeconds(30);

    private readonly string? _explicitExecutablePath;
    private readonly ILogger<LibreOfficeDocumentConverter> _logger;

    public LibreOfficeDocumentConverter(
        ILogger<LibreOfficeDocumentConverter>? logger = null,
        string? explicitExecutablePath = null)
    {
        _logger = logger ?? NullLogger<LibreOfficeDocumentConverter>.Instance;
        _explicitExecutablePath = explicitExecutablePath;
    }

    public string? ResolveExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_explicitExecutablePath))
        {
            var resolved = ResolveCandidate(_explicitExecutablePath);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        var envPath = Environment.GetEnvironmentVariable(LibreOfficePathEnvVar);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var resolved = ResolveCandidate(envPath);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        var baseDir = AppContext.BaseDirectory;
        var bundledCandidates = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(baseDir, "libreoffice", "program", "soffice.exe"),
                Path.Combine(baseDir, "libreoffice", "App", "libreoffice", "program", "soffice.exe"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "libreoffice", "program", "soffice.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "libreoffice", "App", "libreoffice", "program", "soffice.exe")),
                @"C:\Program Files\LibreOffice\program\soffice.exe",
                @"C:\Program Files (x86)\LibreOffice\program\soffice.exe"
            }
            : OperatingSystem.IsMacOS()
                ? new[]
                {
                    Path.Combine(baseDir, "libreoffice", "LibreOffice.app", "Contents", "MacOS", "soffice"),
                    Path.Combine(baseDir, "libreoffice", "program", "soffice"),
                    Path.GetFullPath(Path.Combine(baseDir, "..", "libreoffice", "LibreOffice.app", "Contents", "MacOS", "soffice")),
                    Path.GetFullPath(Path.Combine(baseDir, "..", "libreoffice", "program", "soffice")),
                    "/Applications/LibreOffice.app/Contents/MacOS/soffice"
                }
                : new[]
                {
                    Path.Combine(baseDir, "libreoffice", "program", "soffice"),
                    Path.GetFullPath(Path.Combine(baseDir, "..", "libreoffice", "program", "soffice")),
                    "/usr/bin/soffice",
                    "/usr/local/bin/soffice",
                    "/opt/libreoffice/program/soffice"
                };

        foreach (var candidate in bundledCandidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return FindOnPath(OperatingSystem.IsWindows() ? "soffice.exe" : "soffice");
    }

    public async Task<LibreOfficeConversionResult> ConvertAsync(
        string sourceFilePath,
        string targetExtension,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetExtension);

        var sofficePath = ResolveExecutablePath();
        if (string.IsNullOrWhiteSpace(sofficePath))
        {
            return new LibreOfficeConversionResult(
                Success: false,
                ConvertedFilePath: null,
                TempDirectoryToCleanup: null,
                BinaryAvailable: false,
                ErrorMessage: "未检测到内置或系统安装的 LibreOffice (soffice)。");
        }

        var normalizedTargetExt = targetExtension.Trim().TrimStart('.').ToLowerInvariant();
        if (normalizedTargetExt is not ("docx" or "pptx" or "xlsx" or "pdf"))
        {
            return new LibreOfficeConversionResult(
                Success: false,
                ConvertedFilePath: null,
                TempDirectoryToCleanup: null,
                BinaryAvailable: true,
                ErrorMessage: $"不支持的转换目标格式: {targetExtension}");
        }

        var workDir = Path.Combine(Path.GetTempPath(), "codexflow-lo-" + Guid.NewGuid().ToString("N"));
        var profileDir = Path.Combine(workDir, "profile");
        var outDir = Path.Combine(workDir, "out");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(outDir);

        try
        {
            var sourceExt = Path.GetExtension(sourceFilePath);
            var safeInputName = "input" + (string.IsNullOrWhiteSpace(sourceExt) ? ".bin" : sourceExt);
            var stagedInput = Path.Combine(workDir, safeInputName);
            await using (var src = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            await using (var dst = new FileStream(stagedInput, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            var profileUri = new Uri(profileDir + Path.DirectorySeparatorChar).AbsoluteUri;
            var psi = new ProcessStartInfo
            {
                FileName = sofficePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir
            };
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add("--norestore");
            psi.ArgumentList.Add("--nolockcheck");
            psi.ArgumentList.Add("--nodefault");
            psi.ArgumentList.Add($"-env:UserInstallation={profileUri}");
            psi.ArgumentList.Add("--convert-to");
            psi.ArgumentList.Add(normalizedTargetExt);
            psi.ArgumentList.Add("--outdir");
            psi.ArgumentList.Add(outDir);
            psi.ArgumentList.Add(stagedInput);

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                TryDeleteDirectory(workDir);
                return new LibreOfficeConversionResult(
                    Success: false,
                    ConvertedFilePath: null,
                    TempDirectoryToCleanup: null,
                    BinaryAvailable: true,
                    ErrorMessage: "无法启动 LibreOffice 转换进程。");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ConversionTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                TryDeleteDirectory(workDir);
                return new LibreOfficeConversionResult(
                    Success: false,
                    ConvertedFilePath: null,
                    TempDirectoryToCleanup: null,
                    BinaryAvailable: true,
                    ErrorMessage: string.Create(CultureInfo.InvariantCulture, $"LibreOffice 转换超时（>{ConversionTimeout.TotalSeconds:0} 秒）。"));
            }
            catch
            {
                TryKillProcessTree(process);
                TryDeleteDirectory(workDir);
                throw;
            }

            var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
            _ = await stdoutTask.ConfigureAwait(false);

            var expectedOutput = Path.Combine(outDir, "input." + normalizedTargetExt);
            if (process.ExitCode != 0 || !File.Exists(expectedOutput))
            {
                _logger.LogWarning(
                    "LibreOffice conversion failed with exit code {ExitCode}: {Stderr}",
                    process.ExitCode,
                    stderr);
                TryDeleteDirectory(workDir);
                return new LibreOfficeConversionResult(
                    Success: false,
                    ConvertedFilePath: null,
                    TempDirectoryToCleanup: null,
                    BinaryAvailable: true,
                    ErrorMessage: string.IsNullOrWhiteSpace(stderr)
                        ? $"LibreOffice 转换失败 (ExitCode={process.ExitCode})。"
                        : $"LibreOffice 转换失败: {stderr}");
            }

            return new LibreOfficeConversionResult(
                Success: true,
                ConvertedFilePath: expectedOutput,
                TempDirectoryToCleanup: workDir,
                BinaryAvailable: true,
                ErrorMessage: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDeleteDirectory(workDir);
            return new LibreOfficeConversionResult(
                Success: false,
                ConvertedFilePath: null,
                TempDirectoryToCleanup: null,
                BinaryAvailable: true,
                ErrorMessage: $"LibreOffice 转换异常: {ex.Message}");
        }
    }

    internal static void TryDeleteDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temporary LibreOffice profile/output directory.
        }
    }

    private static string? ResolveCandidate(string candidatePath)
    {
        var trimmed = candidatePath.Trim();
        if (File.Exists(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        if (Directory.Exists(trimmed))
        {
            var exeName = OperatingSystem.IsWindows() ? "soffice.exe" : "soffice";
            var direct = Path.Combine(trimmed, exeName);
            if (File.Exists(direct))
            {
                return Path.GetFullPath(direct);
            }

            var programExe = Path.Combine(trimmed, "program", exeName);
            if (File.Exists(programExe))
            {
                return Path.GetFullPath(programExe);
            }
        }

        return null;
    }

    private static string? FindOnPath(string executableName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar))
        {
            return null;
        }

        foreach (var segment in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var full = Path.Combine(segment, executableName);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore race when process already exited.
        }
    }
}
