// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;

namespace CodexFlow.Core.Tests.Support;

/// <summary>
/// Test-side wrapper that gives a fake tool an EXPLICIT trusted capability (what a host declares in production).
/// It never relaxes the registry: tools without a declaration still resolve to Unknown and are rejected, which
/// <c>McpToolsTests.ToolRegistry_UndeclaredCapability_IsStillRejected</c> keeps proving.
/// </summary>
internal sealed class DeclaredCapabilityTool : ICodexTool
{
    private readonly ICodexTool _inner;

    private DeclaredCapabilityTool(ICodexTool inner, ToolCapability capability)
    {
        if (capability == ToolCapability.Unknown)
        {
            throw new ArgumentException("A declaration must name a real capability; Unknown is the default-deny state.", nameof(capability));
        }

        _inner = inner;
        Capability = capability;
    }

    public ToolCapability Capability { get; }
    public string Name => _inner.Name;
    public string Description => _inner.Description;
    public ToolCategory Category => _inner.Category;
    public IReadOnlyList<int> AllowedStages => _inner.AllowedStages;
    public ToolExecutionMetadata Metadata => _inner.Metadata;

    public ValueTask<CodexToolValidationResult> ValidateInputAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
        => _inner.ValidateInputAsync(arguments, ct);

    public ValueTask<CodexToolPermissionResult> CheckPermissionsAsync(
        Dictionary<string, object?> arguments, QueryRuntimeRequest request, QueryRuntimeState? state = null, CancellationToken ct = default)
        => _inner.CheckPermissionsAsync(arguments, request, state, ct);

    public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
        => _inner.ExecuteAsync(arguments, ct);

    public static ICodexTool Declare(ICodexTool tool, ToolCapability capability) => new DeclaredCapabilityTool(tool, capability);

    /// <summary>Keeps the central catalog's capability when the name is already cataloged; otherwise declares <paramref name="fallback"/>.</summary>
    public static ICodexTool DeclareUnlessCataloged(ICodexTool tool, ToolCapability fallback)
        => ToolCapabilityCatalog.Resolve(tool.Name) == ToolCapability.Unknown ? Declare(tool, fallback) : tool;
}

/// <summary>Resolves the repository root from the SOURCE file location, never from the build output directory.</summary>
internal static class RepoRoot
{
    public static string Find([System.Runtime.CompilerServices.CallerFilePath] string callerFile = "")
    {
        var dir = string.IsNullOrEmpty(callerFile) ? null : Path.GetDirectoryName(callerFile);
        while (dir != null && !File.Exists(Path.Combine(dir, "CodexFlow.slnx")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        return dir ?? throw new DirectoryNotFoundException($"Repo root (CodexFlow.slnx) not found above '{callerFile}'");
    }
}
