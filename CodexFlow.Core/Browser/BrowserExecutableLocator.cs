// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Text.RegularExpressions;
using CodexFlow.Contracts.Browser;

namespace CodexFlow.Core.Browser;

public sealed record BrowserExecutableInfo(
    string BrowserName,
    string ExecutablePath,
    string? Version,
    string Source);

public interface IBrowserExecutableLocator
{
    BrowserExecutableStatusDto Detect(string? configuredExecutablePath = null);

    bool IsTrustedExecutablePath(string candidatePath);
}

/// <summary>
/// Cross-platform locator for installed Chromium-family browsers (Chrome, Edge, Chromium, Brave).
/// Resolves only from trusted OS installation directories (never blind PATH lookup) to prevent binary hijacking.
/// </summary>
public sealed partial class BrowserExecutableLocator : IBrowserExecutableLocator
{
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _versionProber;

    public BrowserExecutableLocator(
        Func<string, bool>? fileExists = null,
        Func<string, string?>? versionProber = null)
    {
        _fileExists = fileExists ?? File.Exists;
        _versionProber = versionProber ?? ProbeVersionSafe;
    }

    public BrowserExecutableStatusDto Detect(string? configuredExecutablePath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredExecutablePath))
        {
            var normalized = TryNormalizeFullPath(configuredExecutablePath);
            if (normalized is null)
            {
                return new BrowserExecutableStatusDto(
                    Available: false,
                    BrowserName: null,
                    ExecutablePath: configuredExecutablePath,
                    Version: null,
                    Source: "configured",
                    DiagnosticMessage: "配置的浏览器路径格式无效。");
            }

            if (!_fileExists(normalized))
            {
                return new BrowserExecutableStatusDto(
                    Available: false,
                    BrowserName: InferBrowserName(normalized),
                    ExecutablePath: normalized,
                    Version: null,
                    Source: "configured",
                    DiagnosticMessage: $"未在指定路径找到浏览器可执行文件: {normalized}");
            }

            return new BrowserExecutableStatusDto(
                Available: true,
                BrowserName: InferBrowserName(normalized),
                ExecutablePath: normalized,
                Version: _versionProber(normalized),
                Source: "configured");
        }

        foreach (var (name, candidate) in EnumerateTrustedCandidates())
        {
            if (_fileExists(candidate))
            {
                return new BrowserExecutableStatusDto(
                    Available: true,
                    BrowserName: name,
                    ExecutablePath: candidate,
                    Version: _versionProber(candidate),
                    Source: "auto");
            }
        }

        return new BrowserExecutableStatusDto(
            Available: false,
            BrowserName: null,
            ExecutablePath: null,
            Version: null,
            Source: "auto",
            DiagnosticMessage: "未在本机可信安装目录检测到 Chrome / Edge / Chromium / Brave。请安装任一 Chromium 浏览器或在「设置 → 浏览器」中配置可执行路径。");
    }

    public bool IsTrustedExecutablePath(string candidatePath)
    {
        ArgumentNullException.ThrowIfNull(candidatePath);
        var normalized = TryNormalizeFullPath(candidatePath);
        if (normalized is null)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var (_, trusted) in EnumerateTrustedCandidates())
        {
            if (string.Equals(normalized, trusted, comparison))
            {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<(string Name, string Path)> EnumerateTrustedCandidates()
    {
        var list = new List<(string Name, string Path)>();

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            void AddIfNotEmpty(string name, string root, params string[] parts)
            {
                if (!string.IsNullOrWhiteSpace(root))
                {
                    list.Add((name, Path.GetFullPath(Path.Combine([root, .. parts]))));
                }
            }

            // Prefer Chrome, then Edge, then Brave/Chromium
            AddIfNotEmpty("Google Chrome", programFiles, "Google", "Chrome", "Application", "chrome.exe");
            AddIfNotEmpty("Google Chrome", programFilesX86, "Google", "Chrome", "Application", "chrome.exe");
            AddIfNotEmpty("Google Chrome", localAppData, "Google", "Chrome", "Application", "chrome.exe");

            AddIfNotEmpty("Microsoft Edge", programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe");
            AddIfNotEmpty("Microsoft Edge", programFiles, "Microsoft", "Edge", "Application", "msedge.exe");
            AddIfNotEmpty("Microsoft Edge", localAppData, "Microsoft", "Edge", "Application", "msedge.exe");

            AddIfNotEmpty("Brave Browser", programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
            AddIfNotEmpty("Brave Browser", localAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
            AddIfNotEmpty("Chromium", localAppData, "Chromium", "Application", "chrome.exe");
        }
        else if (OperatingSystem.IsMacOS())
        {
            list.Add(("Google Chrome", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"));
            list.Add(("Microsoft Edge", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"));
            list.Add(("Brave Browser", "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser"));
            list.Add(("Chromium", "/Applications/Chromium.app/Contents/MacOS/Chromium"));
        }
        else
        {
            list.Add(("Google Chrome", "/usr/bin/google-chrome"));
            list.Add(("Google Chrome", "/usr/bin/google-chrome-stable"));
            list.Add(("Microsoft Edge", "/usr/bin/microsoft-edge"));
            list.Add(("Microsoft Edge", "/usr/bin/microsoft-edge-stable"));
            list.Add(("Chromium", "/usr/bin/chromium"));
            list.Add(("Chromium", "/usr/bin/chromium-browser"));
            list.Add(("Brave Browser", "/usr/bin/brave-browser"));
            list.Add(("Chromium (Snap)", "/snap/bin/chromium"));
        }

        return list;
    }

    private static string InferBrowserName(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.Contains("msedge", StringComparison.Ordinal) || lower.Contains("microsoft edge", StringComparison.Ordinal) || lower.Contains("microsoft-edge", StringComparison.Ordinal))
        {
            return "Microsoft Edge";
        }

        if (lower.Contains("brave", StringComparison.Ordinal))
        {
            return "Brave Browser";
        }

        if (lower.Contains("chromium", StringComparison.Ordinal))
        {
            return "Chromium";
        }

        if (lower.Contains("chrome", StringComparison.Ordinal))
        {
            return "Google Chrome";
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    private static string? TryNormalizeFullPath(string rawPath)
    {
        try
        {
            var trimmed = rawPath.Trim();
            return Path.IsPathRooted(trimmed) ? Path.GetFullPath(trimmed) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? ProbeVersionSafe(string executablePath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var info = FileVersionInfo.GetVersionInfo(executablePath);
                if (!string.IsNullOrWhiteSpace(info.ProductVersion))
                {
                    return info.ProductVersion.Trim();
                }

                if (!string.IsNullOrWhiteSpace(info.FileVersion))
                {
                    return info.FileVersion.Trim();
                }
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return null;
            }

            if (!process.WaitForExit(2500))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            var match = VersionRegex().Match(output);
            return match.Success ? match.Value : (string.IsNullOrWhiteSpace(output) ? null : output);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+(?:\.\d+)?")]
    private static partial Regex VersionRegex();
}
