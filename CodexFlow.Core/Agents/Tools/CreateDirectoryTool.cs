// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Models;
using CodexFlow.Core.Remote;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 确保工作区目录存在（替代 MCP create_directory / ensure_directory）。
/// </summary>
public class CreateDirectoryTool(
    ILogger<CreateDirectoryTool> logger,
    IExecutionApprovalBroker? approvalBroker = null) : ICodexTool
{
    public string Name => "create_directory";
    public string Description => "在工作区中创建目录，支持递归创建多级目录（类似 mkdir -p）。\n" +
        "参数（JSON object）：\n" +
        "  - path (string, 必填): 相对于工作区根目录的目录路径\n" +
        "返回：目录创建成功的确认信息。\n" +
        "调用示例：\n" +
        "  create_directory({\"path\":\"src/Application/Repositories\"})\n" +
        "  create_directory({\"path\":\"CodexFlow.Core/Abstractions\"})";
    public ToolCategory Category => ToolCategory.Forge;
    public IReadOnlyList<int> AllowedStages => [3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var relativePath = arguments.GetValueOrDefault("path")?.ToString();

        if ((string.IsNullOrEmpty(workspacePath) && string.IsNullOrEmpty(projectRoot)) || string.IsNullOrEmpty(relativePath))
            return CodexToolResult.Error("Missing workspace_path or path.");

        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot) || !Directory.Exists(baseRoot))
            return CodexToolResult.Error("Workspace root does not exist.");
        if (ToolPathResolver.IsForeignAbsolutePath(relativePath, baseRoot))
            return CodexToolResult.Error("Write refused: the path is absolute and outside the trusted workspace root.");
        var normalizedPath = ToolPathResolver.NormalizeDuplicateRepoPrefix(relativePath, baseRoot);
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, normalizedPath));
        if (!ToolPathResolver.IsWithinRoot(fullPath, baseRoot))
            return CodexToolResult.Error("Path traversal not allowed.");
        if (!ToolPathResolver.IsSafeWriteTarget(fullPath, baseRoot, out var writeRefusal))
            return CodexToolResult.Error($"Write refused: {writeRefusal}");

        try
        {
            var remoteRefusal = await RemoteWorkspaceWriteGate.AuthorizeWriteIfRemoteAsync(
                Name, baseRoot, normalizedPath, $"create_directory {normalizedPath}", approvalBroker, arguments, ct).ConfigureAwait(false);
            if (remoteRefusal is not null)
            {
                return remoteRefusal;
            }

            ToolPathResolver.EnsureDirectoryVerified(fullPath, baseRoot);
            StructuredLog.Information(logger, "create_directory: {Path}", normalizedPath);
            return CodexToolResult.Succeeded($"✅ 目录已创建: {normalizedPath}");
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "create_directory failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(logger, ex, "create_directory failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Error(logger, ex, "create_directory failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            StructuredLog.Error(logger, ex, "create_directory failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
    }
}

