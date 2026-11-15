// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public class ToolPermissionMetadataTests
{
    [Theory]
    [MemberData(nameof(HighRiskToolCases))]
    public void HighRiskTools_ShouldDeclarePermissionMetadata(ICodexTool tool, bool expectedDestructive)
    {
        var metadata = tool.Metadata;

        Assert.False(metadata.IsConcurrencySafe);
        Assert.False(metadata.IsReadOnly);
        Assert.Equal(expectedDestructive, metadata.IsDestructive);
        Assert.Equal(ToolInterruptBehavior.RequiresConfirmation, metadata.InterruptBehavior);
        Assert.NotNull(metadata.ResultSizeSoftLimitChars);
    }

    public static IEnumerable<object[]> HighRiskToolCases()
    {
        yield return
        [
            new RunCommandTool(NullLogger<RunCommandTool>.Instance),
            true
        ];
        yield return
        [
            new WriteFileTool(NullLogger<WriteFileTool>.Instance),
            true
        ];
        yield return
        [
            new EditFileTool(NullLogger<EditFileTool>.Instance),
            true
        ];
        yield return
        [
            new NotebookEditTool(NullLogger<NotebookEditTool>.Instance),
            true
        ];
        yield return
        [
            new DeleteFileTool(NullLogger<DeleteFileTool>.Instance),
            true
        ];
        yield return
        [
            new GitWorkspaceTool(
                "git_clone",
                "clone",
                [1],
                Mock.Of<IGitService>(),
                NullLogger<GitWorkspaceTool>.Instance),
            false
        ];
    }
}
