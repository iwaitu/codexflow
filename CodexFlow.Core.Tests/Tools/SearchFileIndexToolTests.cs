// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public class SearchFileIndexToolTests
{
    [Fact]
    public void SearchFileIndexTool_ShouldBeAvailable_InStage0_AndStage3()
    {
        var manager = new CodexSessionManager(
            Mock.Of<ISessionStore>(),
            Mock.Of<IMemoryService>(),
            Mock.Of<ILogger<CodexSessionManager>>());
        var tool = new SearchFileIndexTool(manager, Mock.Of<ILogger<SearchFileIndexTool>>());

        Assert.Contains(0, tool.AllowedStages);
        Assert.Contains(3, tool.AllowedStages);
    }

    [Fact]
    public async Task ExecuteAsync_WithIndex_ReturnsMatches()
    {
        // Arrange
        var workspaceDir = Path.Combine(Path.GetTempPath(), "codex-sfi-idx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDir);
        try
        {
            var sessionId = "test-session";
            var index = new List<FileIndexEntry> {
                new FileIndexEntry { Path = "src/Core/Services/ProjectScanner.cs", Type = "C# Source", Size = 100 },
                new FileIndexEntry { Path = "src/Web/Controllers/CodexController.cs", Type = "C# Source", Size = 200 },
                new FileIndexEntry { Path = "README.md", Type = "Markdown", Size = 50 }
            };
            var indexJson = JsonConvert.SerializeObject(index);

            var session = new CodexSession {
                Id = sessionId,
                WorkspacePath = workspaceDir,
                ActiveFacts = {
                    new MemoryEntry("ProjectFileIndex", indexJson, "fact", DateTime.UtcNow)
                }
            };

            var mockStore = new Mock<ISessionStore>();
            mockStore.Setup(s => s.GetSessionAsync(sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);

            var mockMemory = new Mock<IMemoryService>();
            mockMemory.Setup(m => m.RecallFactsAsync(sessionId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new List<MemoryEntry> {
                          new MemoryEntry("ProjectFileIndex", indexJson, "fact", DateTime.UtcNow)
                      });

            var logger = Mock.Of<ILogger<CodexSessionManager>>();
            var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, logger);

            var toolLogger = Mock.Of<ILogger<SearchFileIndexTool>>();
            var tool = new SearchFileIndexTool(manager, toolLogger);

            // Act
            var result = await tool.ExecuteAsync(new Dictionary<string, object?> { ["session_id"] = sessionId, ["query"] = "ProjectScanner" });

            // Assert
            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("src/Core/Services/ProjectScanner.cs", result.Output);
            Assert.DoesNotContain("CodexController.cs", result.Output);
            Assert.NotNull(result.Summary);
            Assert.Contains("src/Core/Services/ProjectScanner.cs", result.Summary!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NoIndex_ReturnsError()
    {
        // Arrange
        var sessionId = "test-session-no-index";
        var session = new CodexSession { Id = sessionId }; // Empty facts

        var mockStore = new Mock<ISessionStore>();
        mockStore.Setup(s => s.GetSessionAsync(sessionId)).ReturnsAsync(session);

        var manager = new CodexSessionManager(mockStore.Object, Mock.Of<IMemoryService>(), Mock.Of<ILogger<CodexSessionManager>>());
        var tool = new SearchFileIndexTool(manager, Mock.Of<ILogger<SearchFileIndexTool>>());

        // Act
        var result = await tool.ExecuteAsync(new Dictionary<string, object?> { ["session_id"] = sessionId, ["query"] = "ProjectScanner" });

        // Assert
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("索引尚未建立", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_NoIndex_UsesWorkspaceFallbackScan()
    {
        var sessionId = "test-session-workspace-fallback";
        var session = new CodexSession { Id = sessionId };
        var workspacePath = Path.Combine(Path.GetTempPath(), "codex-search-file-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(Path.Combine(workspacePath, "CodexFlow.Core", "Runtime"));
        await File.WriteAllTextAsync(
            Path.Combine(workspacePath, "CodexFlow.Core", "Runtime", "QueryRuntimeEngine.cs"),
            "public sealed class QueryRuntimeEngine { }");

        try
        {
            var mockStore = new Mock<ISessionStore>();
            mockStore.Setup(s => s.GetSessionAsync(sessionId)).ReturnsAsync(session);

            var manager = new CodexSessionManager(
                mockStore.Object,
                Mock.Of<IMemoryService>(),
                Mock.Of<ILogger<CodexSessionManager>>());
            var tool = new SearchFileIndexTool(manager, Mock.Of<ILogger<SearchFileIndexTool>>());

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["workspace_path"] = workspacePath,
                ["query"] = "QueryRuntimeEngine"
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("live workspace scan", result.Output);
            Assert.Contains("CodexFlow.Core/Runtime/QueryRuntimeEngine.cs", result.Output, StringComparison.Ordinal);
            Assert.NotNull(result.Summary);
            Assert.Contains("CodexFlow.Core/Runtime/QueryRuntimeEngine.cs", result.Summary!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }
}
