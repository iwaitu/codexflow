// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexFlow.Services.ArtifactAudit;

/// <summary>
/// Reads NuGet's restore output for the one thing an offline restore failure does not say: which package it could not find. A package that
/// asks for "Y (>= 2.5.303)" makes NuGet look for exactly 2.5.303 first; when only 2.5.307 is on disk the restore warns (NU1603) and resolves 2.5.307,
/// but with the official source unreachable the same lookup is an error (NU1301) that names nothing.
/// </summary>
public static partial class NuGetRestoreDiagnostics
{
    private const int MaxEntries = 8;

    [GeneratedRegex(@"NU1603: (?<from>[\w.\-]+) (?<fromVersion>\S+) depends on (?<id>[\w.\-]+) \(>= (?<wanted>[^)\s]+)\) but \k<id> \k<wanted> was not found\. (?<resolvedId>[\w.\-]+) (?<resolved>\S+) was resolved instead", RegexOptions.CultureInvariant)]
    private static partial Regex LowerBoundNotFound();

    /// <summary>Empty when the output holds no such warning; otherwise a sentence per package, ready to append to a failure message.</summary>
    public static string DescribeUnavailableLowerBounds(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.Empty;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder();
        foreach (Match match in LowerBoundNotFound().Matches(output))
        {
            var id = match.Groups["id"].Value;
            if (!seen.Add(id)) continue;
            if (seen.Count > MaxEntries) break;
            text.Append(CultureInfo.InvariantCulture, $" {match.Groups["from"].Value} {match.Groups["fromVersion"].Value} 要求 {id} 不低于 {match.Groups["wanted"].Value}，宿主取到的是 {match.Groups["resolved"].Value}，" +
                $"请在出问题的项目里直接固定 {id} {match.Groups["resolved"].Value}。");
        }

        return text.Length == 0 ? string.Empty : " 缺的包：" + text.ToString().TrimEnd();
    }
}
