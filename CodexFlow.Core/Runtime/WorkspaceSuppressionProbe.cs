// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Runtime;

/// <summary>What the project files of a workspace really say about vulnerability-audit suppression.</summary>
public enum SuppressionEvidence
{
    /// <summary>No workspace, not a git work tree, or git could not be run: the files cannot be consulted.</summary>
    Unknown,

    /// <summary>The changes since HEAD (and new project files) add no audit suppression.</summary>
    NoneAdded,

    /// <summary>The changes add NuGetAuditSuppress, turn NuGetAudit off, or add NU1901-NU1904 to NoWarn.</summary>
    Added,
}

/// <summary>
/// Reads the real change to the project files instead of the worker's account of it: a report that says "I suppressed the warnings" is
/// narrative, a line added to a csproj/props file is fact. Only lines ADDED relative to HEAD count, so a suppression that was already in the
/// repository is not blamed on the worker.
/// </summary>
public static class WorkspaceSuppressionProbe
{
    private static readonly string[] ProjectFilePatterns = ["*.csproj", "*.props", "*.targets"];

    private static readonly Regex Suppression = new(
        @"NuGetAuditSuppress|<NuGetAudit>\s*false\s*</NuGetAudit>|<NoWarn>[^<]*NU190[1-4]|<NuGetAuditMode>\s*direct\s*</NuGetAuditMode>|WarningsNotAsErrors[^<]*NU190[1-4]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public static async Task<SuppressionEvidence> ProbeAsync(string? workspacePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return SuppressionEvidence.Unknown;
        }

        try
        {
            var diff = await RunGitAsync(workspacePath, ["diff", "HEAD", "-U0", "--no-color", "--", .. ProjectFilePatterns], ct).ConfigureAwait(false);
            var untracked = await RunGitAsync(workspacePath, ["ls-files", "--others", "--exclude-standard", "--", .. ProjectFilePatterns], ct).ConfigureAwait(false);
            var status = await RunGitAsync(workspacePath, ["status", "--porcelain"], ct).ConfigureAwait(false);
            if (diff is null || untracked is null || status is null)
            {
                return SuppressionEvidence.Unknown;
            }

            // A work tree with no change at all is not evidence of anything: it may simply not be the tree the worker edited.
            if (string.IsNullOrWhiteSpace(status))
            {
                return SuppressionEvidence.Unknown;
            }

            return AddsSuppression(diff, untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), workspacePath)
                ? SuppressionEvidence.Added
                : SuppressionEvidence.NoneAdded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SuppressionEvidence.Unknown;
        }
    }

    internal static bool AddsSuppression(string unifiedDiff, IEnumerable<string> untrackedProjectFiles, string workspacePath)
    {
        foreach (var line in unifiedDiff.Split('\n'))
        {
            if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal) && Suppression.IsMatch(line))
            {
                return true;
            }
        }

        foreach (var relative in untrackedProjectFiles)
        {
            var full = Path.GetFullPath(Path.Combine(workspacePath, relative));
            if (full.StartsWith(Path.GetFullPath(workspacePath), StringComparison.OrdinalIgnoreCase) && File.Exists(full) && Suppression.IsMatch(File.ReadAllText(full)))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string?> RunGitAsync(string workspacePath, IReadOnlyList<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workspacePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info);
        if (process is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? output : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return null;
        }
    }
}
