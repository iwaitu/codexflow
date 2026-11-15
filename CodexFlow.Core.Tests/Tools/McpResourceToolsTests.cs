// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public sealed class McpResourceToolsTests
{
    [Fact]
    public async Task ListMcpResources_ShouldReturnLocalResourceUris()
    {
        var root = CreateMcpRoot();
        try
        {
            var tool = new ListMcpResourcesTool(NullLogger<ListMcpResourcesTool>.Instance, root);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["server"] = "codex-test-server",
                ["pattern"] = "README",
                ["max_results"] = 10
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("mcp://local/codex-test-server/README.md", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadMcpResource_ShouldReadTextResource()
    {
        var root = CreateMcpRoot();
        try
        {
            var tool = new ReadMcpResourceTool(NullLogger<ReadMcpResourceTool>.Instance, root);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["uri"] = "mcp://local/codex-test-server/README.md"
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("Test MCP server", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadMcpResource_ShouldRejectTraversalAndBinaryFiles()
    {
        var root = CreateMcpRoot();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "codex-test-server", "asset.bin"), [1, 2, 3]);
            var tool = new ReadMcpResourceTool(NullLogger<ReadMcpResourceTool>.Instance, root);

            var traversal = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["uri"] = "mcp://local/codex-test-server/../outside.md"
            });
            var binary = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["uri"] = "mcp://local/codex-test-server/asset.bin"
            });

            Assert.Equal(ToolResultStatus.Failed, traversal.Status);
            Assert.Equal(ToolResultStatus.Failed, binary.Status);
            Assert.Contains("not a supported text file", binary.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateMcpRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-mcp-resource-" + Guid.NewGuid().ToString("N"));
        var server = Path.Combine(root, "codex-test-server");
        Directory.CreateDirectory(server);
        File.WriteAllText(Path.Combine(server, "README.md"), "# Test MCP server");
        File.WriteAllText(Path.Combine(server, "server.py"), "print('server')");
        return root;
    }
}
