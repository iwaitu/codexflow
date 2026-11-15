// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using CodexFlow.Core.Abstractions;
using CodexFlow.Services.ArtifactAudit;

namespace CodexFlow.Services.Background;

/// <summary>
/// A read-only worker in a session whose dependency upgrade the host already resolved gets the host's answer and no web tools.
/// <para>
/// Seen in the developer simulation (replayed with <c>qre replay --audit-file</c>): an ExploreWorker asked to review the upgrade read every
/// project file and then spent the rest of its rounds on <c>web_search</c> / <c>fetch_webpage</c> looking up advisories and versions, until
/// the round budget ran out without a report (MaxSteps, 55 tool calls). The advisories and the target versions were already decided by the
/// host from verified data; what the web says can only disagree with the packages that were actually fetched, and a fetched page is
/// untrusted text that would flow into the plan. A prompt asking it not to search is a wish; removing the tools is a fact.
/// </para>
/// </summary>
internal static class ReadOnlyWorkerDependencyBriefing
{
    private static readonly HashSet<string> WebTools = new(StringComparer.OrdinalIgnoreCase) { "web_search", "fetch_webpage" };

    public static bool Applies(IReadOnlyList<FetchedUpgrade>? fetched) => fetched is { Count: > 0 };

    /// <summary>The tools without the web ones when the host resolved the session's upgrade; unchanged otherwise.</summary>
    public static IReadOnlyList<ICodexTool> FilterTools(IReadOnlyList<ICodexTool> tools, IReadOnlyList<FetchedUpgrade>? fetched) =>
        Applies(fetched) ? tools.Where(static tool => !WebTools.Contains(tool.Name)).ToList() : tools;

    /// <summary>The prompt section; empty when the host resolved nothing for the session.</summary>
    public static string Build(IReadOnlyList<FetchedUpgrade>? fetched)
    {
        if (!Applies(fetched))
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.Append("\n\n# 宿主已确定的依赖升级（事实，以此为准）\n");
        text.Append("漏洞公告和目标版本由宿主根据已校验的公告数据选定，目标版本的包已经取好。不需要、也无法上网核实：本任务没有联网工具。\n");
        text.Append("只需阅读仓库里的项目文件，确认下表每个包在哪些项目里、以什么方式被引用，然后给出结论。\n\n");
        text.Append("| 包 | 当前版本 | 目标版本 | 依赖方式 | 受影响项目 |\n|---|---|---|---|---|\n");
        foreach (var package in fetched!.OrderBy(static f => f.Package, StringComparer.OrdinalIgnoreCase))
        {
            text.Append("| ").Append(Cell(package.Package))
                .Append(" | ").Append(Cell(string.Join("、", package.CurrentVersions)))
                .Append(" | ").Append(Cell(package.Version))
                .Append(" | ").Append(package.Transitive ? "传递依赖" : "直接依赖")
                .Append(" | ").Append(Cell(string.Join("、", package.Projects))).Append(" |\n");
        }

        return text.ToString();
    }

    // Package ids, versions and project paths come from the scanned repository: keep them from breaking the table or carrying markup.
    private static string Cell(string value) =>
        value.Replace("|", "/", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
