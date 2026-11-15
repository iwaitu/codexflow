// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;
using CodexFlow.Core.Models;
using CodexFlow.Services.ArtifactAudit;
using NuGet.Versioning;

namespace CodexFlow.Gateway;

/// <summary>A package version a plan sets that the host did not resolve and the user's message did not name.</summary>
public sealed record OutOfScopePackageChange(string Package, string Version, string? HostVersion);

/// <summary>The refusal of a plan outside the host's upgrade. A final one is a successful answer that ends the model's attempts.</summary>
/// <param name="Message">What the model gets back from the plan tool: the reason and what to do next (instructions for the model).</param>
/// <param name="Final">The model is told to stop planning and answer in text.</param>
/// <param name="UserReason">The reason alone, written for the person: it is stored on the refused plan and shown when they try to approve it.</param>
public sealed record PlanRejection(string Message, bool Final, string UserReason);

/// <summary>
/// Checked when a formal plan is created, before it can be shown for approval. In a session where the host resolved (and fetched) the versions
/// for a dependency upgrade, a plan may raise a package the project already references only to the version the host resolved - unless the
/// user's own message names that version. Seen live with a local model: asked to upgrade "only to the host-fetched versions, touch nothing
/// else", the plan added "OTel 全家桶对齐（方案 B，你已确认）" - four more packages at a version the host never fetched, with a confirmation the
/// user never gave - and the task failed at the offline restore after the user had approved it. A user who insists on such a version names it
/// ("也照样升到 1.15.3"), and that plan is accepted: its consequence is theirs and is reported when the restore fails.
/// </summary>
public static class PlanDependencyScope
{
    // A dotted package id, then its versions before the sentence moves on (the next id, a line end or a clause separator).
    private static readonly Regex PackageId = new(
        @"(?<![A-Za-z0-9_./\\-])(?<pkg>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)(?![A-Za-z0-9_\\/-])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));

    private static readonly Regex VersionLiteral = new(
        @"(?<![\d.])(?<ver>\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.]+)?)(?![\d.]*\d)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));

    private static readonly Regex PackageReference = new(
        @"<PackageReference\b(?<attrs>[^>]*)>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));

    private static readonly Regex IncludeAttribute = new(@"\bInclude\s*=\s*""(?<v>[^""]+)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex VersionAttribute = new(@"\bVersion\s*=\s*""(?<v>[^""]+)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly char[] ClauseEnd = ['\n', '\r', '，', '。', '；', ';', '、', '（', '(', '|'];
    private const int Window = 48;

    /// <summary>The plan's text: its markdown and, when it was projected, every task's title, description and checklist.</summary>
    public static string TextOf(string? markdown, IEnumerable<CodexTask>? tasks)
        => string.Join("\n", new[] { markdown ?? string.Empty }.Concat((tasks ?? []).SelectMany(static task =>
            new[] { task.Title, task.Description }.Concat(task.ChecklistItems.Select(static item => item.Text)))));

    /// <summary>Package id → the versions the project's csproj files declare for it today.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> DeclaredPackages(string? workspacePath)
    {
        var declared = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var file in Directory.EnumerateFiles(workspacePath, "*.csproj", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(workspacePath, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(static segment => segment is "bin" or "obj" or ".git" or "node_modules"))
            {
                continue;
            }

            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (Match match in PackageReference.Matches(text))
            {
                var attributes = match.Groups["attrs"].Value;
                var include = IncludeAttribute.Match(attributes);
                if (!include.Success)
                {
                    continue;
                }

                if (!declared.TryGetValue(include.Groups["v"].Value, out var versions))
                {
                    declared[include.Groups["v"].Value] = versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                // a version given as an attribute; one given as a child element or centrally is simply not known here
                if (VersionAttribute.Match(attributes) is { Success: true } version)
                {
                    versions.Add(version.Groups["v"].Value);
                }
            }
        }

        return declared.ToDictionary(static pair => pair.Key, static pair => (IReadOnlyCollection<string>)pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<OutOfScopePackageChange> OutOfScope(
        string? planText,
        IReadOnlyList<FetchedUpgrade> hostResolved,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> declared,
        string? userMessage)
    {
        // No dependency upgrade was resolved for this session: nothing to hold the plan to.
        if (string.IsNullOrWhiteSpace(planText) || hostResolved.Count == 0 || declared.Count == 0)
        {
            return [];
        }

        var resolved = hostResolved
            .GroupBy(static upgrade => upgrade.Package, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Version, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, OutOfScopePackageChange>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var ids = PackageId.Matches(planText);
            for (var i = 0; i < ids.Count; i++)
            {
                var package = ids[i].Groups["pkg"].Value;
                if (!declared.TryGetValue(package, out var current))
                {
                    continue; // not a package this project references (a file name, a namespace, a new package)
                }

                var start = ids[i].Index + ids[i].Length;
                var end = Math.Min(planText.Length, start + Window);
                if (i + 1 < ids.Count) end = Math.Min(end, ids[i + 1].Index);
                var clause = planText.IndexOfAny(ClauseEnd, start, end - start);
                if (clause >= 0) end = clause;

                foreach (Match version in VersionLiteral.Matches(planText[start..end]))
                {
                    var target = version.Groups["ver"].Value;
                    if (!IsRaise(target, current) ||
                        (resolved.TryGetValue(package, out var hostVersion) && SameVersion(hostVersion, target)) ||
                        (userMessage?.Contains(target, StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        continue;
                    }

                    found.TryAdd(package + "@" + target, new OutOfScopePackageChange(package, target, resolved.GetValueOrDefault(package)));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }

        return [.. found.Values.OrderBy(static change => change.Package, StringComparer.OrdinalIgnoreCase)];
    }

    public static string Describe(IReadOnlyList<OutOfScopePackageChange> changes)
        => Reject(changes, null, 1).Message;

    /// <summary>
    /// The answer to a plan that sets versions outside the host's upgrade. The first refusal names the packages and, when the user's message
    /// wrote a version, says that THAT version is the one that may be used: the check lets a named version through, and a model that is not
    /// told so sends the same plan again. The second and every later refusal is <see cref="PlanRejection.Final"/>: the model is told to stop
    /// planning and to tell the user in text, because a refusal it cannot get past is not a reason to ask again.
    /// </summary>
    public static PlanRejection Reject(IReadOnlyList<OutOfScopePackageChange> changes, string? userMessage, int rejectionNumber)
    {
        var packages = string.Join("；", changes.Take(12).Select(static change => change.HostVersion is null
            ? $"{change.Package} → {change.Version}（宿主没有为这个包解析升级）"
            : $"{change.Package} → {change.Version}（宿主解析的目标是 {change.HostVersion}）"));

        var userReason = "计划里这些包的版本不在宿主解析并取好的升级目标内：" + packages + "。验收环境没有网络，只能还原宿主取好的版本。";

        if (rejectionNumber >= 2)
        {
            return new PlanRejection(
                "计划没有创建（这不是错误，是宿主的最终答复）：下列包版本不在宿主解析并取好的升级目标内 — " + packages +
                "。同样的计划已经被拒绝过，再提交只会得到同样的结果。不要再调用 create_session_plan。" +
                "用文字告诉用户：这几个包的升级没有放进计划，原因是验收环境没有网络、只能还原宿主取好的版本；" +
                "请用户在“只升宿主已取包的版本”“明确写出包和版本（并自己承担验收可能失败）”“放弃这部分”中选一个。不要声称计划已经创建。",
                Final: true,
                UserReason: userReason);
        }

        var named = new List<string>();
        if (!string.IsNullOrWhiteSpace(userMessage))
        {
            try
            {
                foreach (Match version in VersionLiteral.Matches(userMessage))
                {
                    if (!named.Contains(version.Groups["ver"].Value)) named.Add(version.Groups["ver"].Value);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                named.Clear();
            }
        }

        var message = "计划没有创建：它要改动的下列包版本不在宿主解析并取好的升级目标内，用户这条消息也没有点名这些版本 — " + packages +
                      "。验收环境没有网络，只能还原宿主取好的版本。";
        message += named.Count > 0
            ? $"用户这条消息里写明的版本是 {string.Join("、", named)}：如果要升这些包，只能用用户写明的版本，不要自己换成别的（计划里写的是上面列出的版本）；" +
              "要么按用户写明的版本重新调用 create_session_plan，要么把这些包去掉。不要声称用户已经确认过计划里没有的版本。"
            : "请把这些改动从要求里去掉，只保留宿主解析的目标版本，然后重新调用 create_session_plan；不要声称用户已经确认过。" +
              "如果你认为确实需要这些升级，在回复里向用户说明并请用户自己明确写出包和版本。";
        return new PlanRejection(message, Final: false, UserReason: userReason);
    }

    // Higher than every version the project declares today: a version the plan merely mentions (the current one, an older one) is not a change.
    private static bool IsRaise(string target, IReadOnlyCollection<string> current)
    {
        if (!NuGetVersion.TryParse(target, out var targetVersion))
        {
            return false;
        }

        return current.Count == 0 || current.All(declared =>
            !VersionRange.TryParse(declared, out var range) || range.MinVersion is not { } min || min < targetVersion);
    }

    private static bool SameVersion(string left, string right)
        => NuGetVersion.TryParse(left, out var a) && NuGetVersion.TryParse(right, out var b)
            ? a == b
            : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
