// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents;
using CodexFlow.Core.Execution;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public sealed class OrchestratorWorkspaceWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cf-orchestrator-write-" + Guid.NewGuid().ToString("N"));

    public OrchestratorWorkspaceWriteTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("../sentinel.txt")]
    [InlineData(".git/config")]
    [InlineData("/absolute.cs")]
    [InlineData("C:\\outside.cs")]
    [InlineData("")]
    public async Task ModelGeneratedPath_CannotEscapeOrModifyGit(string path)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => CodexOrchestrator.WriteWorkspaceTextAsync(_root, path, "untrusted", default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task AuthorizedArtifact_CreatesParentsAndReplacesWithoutBackupFiles()
    {
        await CodexOrchestrator.WriteWorkspaceTextAsync(_root, "reports/plan.json", "first", default);
        await CodexOrchestrator.WriteWorkspaceTextAsync(_root, "reports/plan.json", "second", default);
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(_root, "reports/plan.json")));
        Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CanceledStage_DoesNotCreateArtifact()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodexOrchestrator.WriteWorkspaceTextAsync(_root, "plan.md", "text", canceled.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TrustedContext_RejectsDifferentRootOrOutOfScopeArtifact(bool differentRoot)
    {
        var shadow = Path.Combine(_root, "shadow");
        Directory.CreateDirectory(shadow);
        using var context = new TrustedExecutionContextAccessor().Begin(new TrustedExecutionContext
        {
            UserId = "user", SessionId = "session", JobId = "job", TaskId = "task", AttemptId = "job:1",
            WorkspaceRoot = shadow,
            Scope = new ExportScope { AllowedFiles = ["allowed.txt"] }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => CodexOrchestrator.WriteWorkspaceTextAsync(
            differentRoot ? _root : shadow, differentRoot ? "allowed.txt" : "outside.txt", "denied", default));
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        await CodexOrchestrator.WriteWorkspaceTextAsync(shadow, "allowed.txt", "authorized", default);
        Assert.Equal("authorized", await File.ReadAllTextAsync(Path.Combine(shadow, "allowed.txt")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
