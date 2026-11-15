// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents;
using Xunit;

namespace CodexFlow.Core.Tests.Planning;

/// <summary>
/// The step that turns a planning draft into tasks never sees the repository, so a task's declared outputs were whatever the model imagined
/// ("OtherProject1/*.csproj"). Run by hand with the same model, opencode read every csproj before it planned and named real paths. The
/// projection prompt now carries the list of manifest files that exist, and says that outputs come from it.
/// </summary>
public class RepositoryManifestIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "manifest-index-" + Guid.NewGuid().ToString("N"));

    public RepositoryManifestIndexTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Touch(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    [Fact]
    public void ItListsTheManifestsThatExist_WithForwardSlashes_Sorted_AndSkipsBuildOutputAndDependencyFolders()
    {
        foreach (var file in new[]
                 {
                     "src/CleanApp/CleanApp.csproj", "test/CleanAppTests/CleanAppTests.csproj", "package.json", "requirements.txt",
                     "Directory.Packages.props", "CleanApp.slnx", "README.md", "src/CleanApp/Program.cs",
                     "node_modules/left-pad/package.json", "src/CleanApp/bin/Debug/Old.csproj", "src/CleanApp/obj/x.csproj", ".git/hooks/x.csproj"
                 })
        {
            Touch(file);
        }

        var manifests = RepositoryManifestIndex.List(_root);

        Assert.Equal(
            ["CleanApp.slnx", "Directory.Packages.props", "package.json", "requirements.txt", "src/CleanApp/CleanApp.csproj", "test/CleanAppTests/CleanAppTests.csproj"],
            manifests);
    }

    [Fact]
    public void ItNeverListsMoreThanTheCap_AndSaysHowManyWereLeftOut()
    {
        for (var i = 0; i < RepositoryManifestIndex.MaxEntries + 15; i++)
        {
            Touch($"src/P{i:000}/P{i:000}.csproj");
        }

        Assert.Equal(RepositoryManifestIndex.MaxEntries, RepositoryManifestIndex.List(_root).Count);
        Assert.Contains("另有 15 个未列出", RepositoryManifestIndex.Describe(_root)!);
    }

    [Fact]
    public void ARootThatDoesNotExistOrHoldsNoManifest_DescribesNothing()
    {
        Assert.Null(RepositoryManifestIndex.Describe(Path.Combine(_root, "nope")));
        Assert.Null(RepositoryManifestIndex.Describe(null));
        Touch("notes.txt");
        Assert.Null(RepositoryManifestIndex.Describe(_root));
    }

    [Fact]
    public void TheProjectionPrompt_CarriesTheListAndTheRules_WhenThereAreManifests()
    {
        Touch("src/A/A.csproj");

        var prompt = DefaultCodexPlanner.BuildPlanProjectionPrompt("升级依赖", "草稿", RepositoryManifestIndex.Describe(_root));

        Assert.Contains("[仓库里实际存在的清单文件]", prompt);
        Assert.Contains("- src/A/A.csproj", prompt);
        Assert.Contains(DefaultCodexPlanner.PlanRealPathsRule, prompt, StringComparison.Ordinal);
        Assert.Contains(DefaultCodexPlanner.PlanRiskIsolationRule, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProjectionPrompt_WithoutAList_StillCarriesTheRules_AndInventsNoList()
    {
        var prompt = DefaultCodexPlanner.BuildPlanProjectionPrompt("升级依赖", "草稿");

        Assert.DoesNotContain("[仓库里实际存在的清单文件]", prompt);
        Assert.Contains(DefaultCodexPlanner.PlanRealPathsRule, prompt, StringComparison.Ordinal);
    }

    // Live (dependency-upgrade-compliant, x5 run 1): given a ForbiddenPaths field, the planner filled it with every directory outside the task's own
    // project and so locked the manifests the task had to change. The field is for what the user said not to touch, not for "everything else".
    [Fact]
    public void TheForbiddenPathsRule_SaysItIsNotForEverythingOutsideTheTask_AndThatOnlyTheHostKnowsWherePackagesLive()
    {
        var rule = DefaultCodexPlanner.PlanForbiddenPathsRule;

        Assert.Contains("不要把", rule);
        Assert.Contains("其他任务", rule);
        Assert.Contains("范围之外", rule);
        Assert.Contains("宿主", rule);
    }

    [Fact]
    public void TheRules_SayWhatTheyAreFor()
    {
        Assert.Contains("占位", DefaultCodexPlanner.PlanRealPathsRule);
        Assert.Contains("通配符", DefaultCodexPlanner.PlanRealPathsRule);
        Assert.Contains("Outputs", DefaultCodexPlanner.PlanRealPathsRule);
        Assert.Contains("单独的任务", DefaultCodexPlanner.PlanRiskIsolationRule);
        Assert.Contains("风险", DefaultCodexPlanner.PlanRiskIsolationRule);
    }
}
