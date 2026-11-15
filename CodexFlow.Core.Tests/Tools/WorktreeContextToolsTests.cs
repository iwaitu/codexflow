// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public sealed class WorktreeContextToolsTests
{
    [Fact]
    public async Task EnterWorktree_RelativeSubdirectory_ShouldSwitchWorkspaceAndPersistMetadata()
    {
        var workspace = CreateTempDirectory();
        var repo = Directory.CreateDirectory(Path.Combine(workspace, "repo")).FullName;
        var session = new CodexSession { Id = "session-enter-worktree", WorkspacePath = workspace };
        var (sessionManager, sessionStore) = CreateHarness(session);
        var tool = new EnterWorktreeTool(sessionManager, NullLogger<EnterWorktreeTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = session.Id,
            ["path"] = "repo"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal(repo, session.WorkspacePath);
        Assert.Equal(workspace, session.Metadata[WorktreeContextMetadataKeys.OriginalWorkspacePath]);
        Assert.Equal(repo, session.Metadata[WorktreeContextMetadataKeys.ActiveWorktreePath]);
        sessionStore.Verify(x => x.SaveSessionAsync(session, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExitWorktree_ShouldRestoreOriginalWorkspaceAndClearMetadata()
    {
        var workspace = CreateTempDirectory();
        var repo = Directory.CreateDirectory(Path.Combine(workspace, "repo")).FullName;
        var session = new CodexSession { Id = "session-exit-worktree", WorkspacePath = repo };
        session.Metadata[WorktreeContextMetadataKeys.OriginalWorkspacePath] = workspace;
        session.Metadata[WorktreeContextMetadataKeys.ActiveWorktreePath] = repo;
        session.Metadata[WorktreeContextMetadataKeys.EnteredAtUtc] = DateTime.UtcNow.ToString("O");
        var (sessionManager, sessionStore) = CreateHarness(session);
        var tool = new ExitWorktreeTool(sessionManager, NullLogger<ExitWorktreeTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = session.Id
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal(workspace, session.WorkspacePath);
        Assert.DoesNotContain(WorktreeContextMetadataKeys.OriginalWorkspacePath, session.Metadata.Keys);
        Assert.DoesNotContain(WorktreeContextMetadataKeys.ActiveWorktreePath, session.Metadata.Keys);
        Assert.DoesNotContain(WorktreeContextMetadataKeys.EnteredAtUtc, session.Metadata.Keys);
        sessionStore.Verify(x => x.SaveSessionAsync(session, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnterWorktree_ExternalPath_ShouldRequireExplicitGitWorktree()
    {
        var workspace = CreateTempDirectory();
        var external = CreateTempDirectory();
        var session = new CodexSession { Id = "session-external-worktree", WorkspacePath = workspace };
        var (sessionManager, sessionStore) = CreateHarness(session);
        var tool = new EnterWorktreeTool(sessionManager, NullLogger<EnterWorktreeTool>.Instance);

        var blocked = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = session.Id,
            ["path"] = external
        });

        Assert.Equal(ToolResultStatus.Failed, blocked.Status);
        Assert.Equal(workspace, session.WorkspacePath);

        File.WriteAllText(Path.Combine(external, ".git"), "gitdir: ../.git/worktrees/external");
        var allowed = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["session_id"] = session.Id,
            ["path"] = external,
            ["allow_external"] = true
        });

        Assert.Equal(ToolResultStatus.Success, allowed.Status);
        Assert.Equal(external, session.WorkspacePath);
        Assert.Equal(workspace, session.Metadata[WorktreeContextMetadataKeys.OriginalWorkspacePath]);
        sessionStore.Verify(x => x.SaveSessionAsync(session, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (CodexSessionManager SessionManager, Mock<ISessionStore> SessionStore) CreateHarness(CodexSession session)
    {
        var sessionStore = new Mock<ISessionStore>();
        sessionStore
            .Setup(x => x.GetSessionAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessionStore
            .Setup(x => x.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var memoryService = new Mock<IMemoryService>();
        memoryService
            .Setup(x => x.RecallFactsAsync(session.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        memoryService
            .Setup(x => x.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var sessionManager = new CodexSessionManager(
            sessionStore.Object,
            memoryService.Object,
            NullLogger<CodexSessionManager>.Instance);

        return (sessionManager, sessionStore);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "codexflow-worktree-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }
}
