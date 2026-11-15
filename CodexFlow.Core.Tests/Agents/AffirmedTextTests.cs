// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Text;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public sealed class AffirmedTextTests
{
    [Theory]
    [InlineData("构建失败：NU1605 仍然存在。", "NU1605", true)]
    [InlineData("没有 NU1605 降级警告。", "NU1605", false)]
    [InlineData("There is no NU1605 and no package downgrade.", "NU1605", false)]
    [InlineData("NU1605 did not appear.", "NU1605", false)]
    [InlineData("build failed 0 times", "build failed", false)]
    [InlineData("the build failed on CI", "build failed", true)]
    public void ReportStyle_ReadsTheSentencesOwnDenial(string text, string needle, bool expected) =>
        Assert.Equal(expected, AffirmedText.ContainsAffirmed(text, AffirmedText.Style.Report, needle));

    [Theory]
    [InlineData("不修改测试文件", "测试", false)]
    [InlineData("不需要构建验证", "构建", false)]
    [InlineData("无需运行测试", "测试", false)]
    [InlineData("不涉及构建配置", "构建", false)]
    [InlineData("构建和测试除外", "测试", false)]
    [InlineData("Do not run the tests", "tests", false)]
    [InlineData("no need to build", "build", false)]
    [InlineData("修改后执行构建", "构建", true)]
    [InlineData("不仅要修复还要测试", "测试", true)]
    [InlineData("新增测试", "测试", true)]
    public void TaskStyle_ReadsScopeLimits(string text, string needle, bool expected) =>
        Assert.Equal(expected, AffirmedText.ContainsAffirmed(text, AffirmedText.Style.Task, needle));

    [Fact]
    public void AnOccurrenceThatIsAffirmedAnywhereCounts_EvenWhenAnotherIsDenied() =>
        Assert.True(AffirmedText.ContainsAffirmed("不修改测试文件。但需要新增测试并运行测试。", AffirmedText.Style.Task, "测试"));

    [Fact]
    public void NothingToMatch_IsFalse()
    {
        Assert.False(AffirmedText.ContainsAffirmed("", AffirmedText.Style.Task, "测试"));
        Assert.False(AffirmedText.ContainsAffirmed("abc", AffirmedText.Style.Report));
    }
}
