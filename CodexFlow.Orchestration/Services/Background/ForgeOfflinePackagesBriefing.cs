// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using CodexFlow.Services.ArtifactAudit;

namespace CodexFlow.Services.Background;

/// <summary>
/// Tells a forge worker what the host already knows about the session's dependency upgrade: which package versions were fetched, where they
/// are mounted, and which projects use them.
/// <para>
/// Seen in the developer simulation (replayed with <c>qre replay</c>): the worker listed <c>/restore-cache</c>, found only the old versions,
/// wrote references to the target versions, and then watched <c>dotnet restore --source /restore-cache</c> succeed. The target versions were in
/// <c>/restore-cache-extra</c>, a NuGet fallback folder nothing had told it about, so the only explanation it could find was that the command
/// container did not contain its edits. It discarded its own passing build and tests as evidence and reported a finished upgrade as blocked.
/// </para>
/// </summary>
internal static class ForgeOfflinePackagesBriefing
{
    /// <summary>The prompt section for a session with host-fetched upgrades; empty when the host fetched nothing.</summary>
    public static string Build(IReadOnlyList<FetchedUpgrade>? fetched)
    {
        if (fetched is not { Count: > 0 })
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.Append("\n\n# 宿主已取好的依赖包（事实，直接使用，不需要自己再确认）\n");
        text.Append("目标版本由宿主选定并已经取好。命令容器没有网络，离线包在两个只读目录里：\n");
        text.Append("- `/restore-cache`：项目现有依赖的基础缓存，里面只有升级前的版本；\n");
        text.Append("- `/restore-cache-extra`：宿主为本次会话取好的目标版本（下表）。\n");
        text.Append("两个目录都已配置为 NuGet 的备用包目录，`dotnet restore` / `build` / `test` 会自动使用它们，不需要加 `--source`。\n");
        text.Append("所以引用了下表的目标版本之后还原成功是正常的：这些版本不在 `/restore-cache` 里，在 `/restore-cache-extra` 里。");
        text.Append("命令容器里的源码就是你修改之后的工作区副本，还原、构建、测试的结果可以直接作为验证证据。\n\n");
        text.Append("| 包 | 当前版本 | 目标版本 | 依赖方式 | 受影响项目 |\n|---|---|---|---|---|\n");
        foreach (var package in fetched.OrderBy(static f => f.Package, StringComparer.OrdinalIgnoreCase))
        {
            text.Append("| ").Append(Cell(package.Package))
                .Append(" | ").Append(Cell(string.Join("、", package.CurrentVersions)))
                .Append(" | ").Append(Cell(package.Version))
                .Append(" | ").Append(package.Transitive ? "传递依赖" : "直接依赖")
                .Append(" | ").Append(Cell(string.Join("、", package.Projects))).Append(" |\n");
        }

        text.Append("\n直接依赖：把受影响项目里现有引用的版本改为目标版本。传递依赖：在受影响项目里添加一条指向目标版本的直接引用。");
        text.Append("一个包有多个当前版本时，它们都升到同一个目标版本，不需要分清哪个项目对应哪个旧版本。\n");
        text.Append("要查看解析结果，用 `dotnet list <项目文件> package --include-transitive`。");
        return text.ToString();
    }

    // Package ids, versions and project paths come from the scanned repository: keep them from breaking the table or carrying markup.
    private static string Cell(string value) =>
        value.Replace("|", "/", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
