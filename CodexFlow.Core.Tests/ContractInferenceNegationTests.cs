// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using Xunit;

namespace CodexFlow.Core.Tests;

public class ContractInferenceNegationTests
{
    private static CodexTask Normalized(string description)
    {
        var task = new CodexTask { Title = "t", Description = description };
        CodexTaskClassifier.NormalizeTask(task);
        return task;
    }

    private static bool ForbidsPath(CodexTask task, string path) =>
        task.ForbiddenStates.Any(a => a.Type == "file_not_exists" && a.Path.Contains(path, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void DeleteWordingStillForbidsTheFile()
    {
        var task = Normalized("删除 src/Legacy/OldHelper.cs");
        Assert.True(ForbidsPath(task, "OldHelper.cs"));
    }

    [Theory]
    [InlineData("保留 src/Legacy/OldHelper.cs，不删除它")]
    [InlineData("不要删除 src/Legacy/OldHelper.cs，只修改其中的方法")]
    [InlineData("无需移除 src/Legacy/OldHelper.cs")]
    [InlineData("避免删除 src/Legacy/OldHelper.cs")]
    public void NegatedDeleteDoesNotDemandTheFileBeGone(string description)
    {
        var task = Normalized(description);
        Assert.False(ForbidsPath(task, "OldHelper.cs"), "a task that says not to delete a file must not assert the file is absent");
    }
}
