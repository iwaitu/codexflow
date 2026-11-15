// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Xunit;

namespace CodexFlow.Core.Tests;

/// <summary>
/// The approval hash binds the plan markdown, so the markdown must carry what the user actually asked for. Found in a live developer conversation:
/// the blueprint only had the first 80 characters of the request in its title, so constraints such as "leave SharpCompress alone" were not part
/// of what was approved.
/// </summary>
public sealed class PlanBlueprintGeneratorTests
{
    private const string Requirement =
        "把依赖风险简报里已取包的 7 个有漏洞的包升到简报给出的目标版本，OpenTelemetry 其他包一起对齐到 1.15.3。" +
        "SharpCompress 保持 0.30.1 不动，不加 NoWarn 或 NuGetAuditSuppress，不改 .cs 代码。";

    [Fact]
    public async Task TheFullRequirement_IsPartOfTheApprovedMarkdown_EvenWhenLongerThanTheTitle()
    {
        var markdown = await new DefaultPlanBlueprintGenerator().GenerateAsync(new CodexSession { Id = "s" }, Requirement, "ctx");

        Assert.Contains("## Requirement", markdown, StringComparison.Ordinal);
        Assert.Contains("SharpCompress 保持 0.30.1 不动", markdown, StringComparison.Ordinal);
        Assert.Contains("不加 NoWarn 或 NuGetAuditSuppress", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOversizedRequirement_IsBoundedButStillStartsTheSection()
    {
        var markdown = await new DefaultPlanBlueprintGenerator().GenerateAsync(new CodexSession { Id = "s" }, new string('x', 50_000), "ctx");

        Assert.True(markdown.Length < 20_000);
        Assert.Contains("## Requirement", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingRequirement_StillProducesAValidDocument()
    {
        var markdown = await new DefaultPlanBlueprintGenerator().GenerateAsync(new CodexSession { Id = "s" }, "", "ctx");

        Assert.Contains("## Scope", markdown, StringComparison.Ordinal);
    }
}
