// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.LanguageServices;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.LanguageServices;

public class ShadowWorktreeLspRefreshTests
{
    [Fact]
    public async Task WriteFile_ShouldRefreshOnlyTargetWorkerSession_InShadowWorktree()
    {
        var shadowRoot = CreateShadowWorktree();
        var relativePath = "src/App.cs";
        var fullPath = Path.Combine(shadowRoot, "src", "App.cs");
        await File.WriteAllTextAsync(fullPath, "public class App\n{\n// BROKEN_DIAGNOSTIC\n");

        try
        {
            var registry = CreateRegistry();
            var factory = new DefaultLanguageServiceSessionFactory(registry, NullLogger<DefaultLanguageServiceSessionFactory>.Instance);

            var forgeClient = await factory.GetOrCreateAsync(new LanguageServiceSessionKey
            {
                WorkspacePath = shadowRoot,
                WorkerId = "forge-worker",
                Language = SupportedLanguage.CSharp
            });
            var verifyClient = await factory.GetOrCreateAsync(new LanguageServiceSessionKey
            {
                WorkspacePath = shadowRoot,
                WorkerId = "verify-worker",
                Language = SupportedLanguage.CSharp
            });

            var forgeBefore = await forgeClient.GetDiagnosticsAsync(relativePath);
            var verifyBefore = await verifyClient.GetDiagnosticsAsync(relativePath);
            Assert.True(forgeBefore.Diagnostics.Count > 0);
            Assert.Equal(forgeBefore.Diagnostics.Count, verifyBefore.Diagnostics.Count);

            var writeTool = new WriteFileTool(NullLogger<WriteFileTool>.Instance, factory);
            var writeResult = await writeTool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = shadowRoot,
                ["project_root"] = shadowRoot,
                ["worker_id"] = "forge-worker",
                ["path"] = relativePath,
                ["content"] = "public class App\n{\n}\n"
            });

            Assert.Equal(ToolResultStatus.Success, writeResult.Status);

            var forgeAfter = await forgeClient.GetDiagnosticsAsync(relativePath);
            var verifyAfter = await verifyClient.GetDiagnosticsAsync(relativePath);

            Assert.Empty(forgeAfter.Diagnostics);
            Assert.Equal(verifyBefore.Diagnostics.Count, verifyAfter.Diagnostics.Count);

            await verifyClient.NotifyFilesChangedAsync([relativePath]);
            var verifyRefreshed = await verifyClient.GetDiagnosticsAsync(relativePath);
            Assert.Empty(verifyRefreshed.Diagnostics);
        }
        finally
        {
            Directory.Delete(shadowRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExploreReadOnlyLspCall_ShouldNotTriggerWriteSideRefresh()
    {
        var shadowRoot = CreateShadowWorktree();
        var relativePath = "src/App.cs";
        var fullPath = Path.Combine(shadowRoot, "src", "App.cs");
        await File.WriteAllTextAsync(fullPath, "public class App\n{\n// BROKEN_DIAGNOSTIC\n");

        try
        {
            var registry = CreateRegistry();
            var factory = new DefaultLanguageServiceSessionFactory(registry, NullLogger<DefaultLanguageServiceSessionFactory>.Instance);
            var client = await factory.GetOrCreateAsync(new LanguageServiceSessionKey
            {
                WorkspacePath = shadowRoot,
                WorkerId = "explore-worker",
                Language = SupportedLanguage.CSharp
            });

            var initial = await client.GetDiagnosticsAsync(relativePath);
            Assert.True(initial.Diagnostics.Count > 0);

            await File.WriteAllTextAsync(fullPath, "public class App\n{\n}\n");

            var stillCached = await client.GetDiagnosticsAsync(relativePath);
            Assert.Equal(initial.Diagnostics.Count, stillCached.Diagnostics.Count);

            await client.NotifyFilesChangedAsync([relativePath]);
            var refreshed = await client.GetDiagnosticsAsync(relativePath);
            Assert.Empty(refreshed.Diagnostics);
        }
        finally
        {
            Directory.Delete(shadowRoot, recursive: true);
        }
    }

    private static DefaultLanguageServiceRegistry CreateRegistry()
        => new(
        [
            new CSharpLanguageServiceProvider(NullLogger<CSharpLanguageServiceProvider>.Instance),
            new PythonLanguageServiceProvider(new AlwaysAvailableProbe(), NullLogger<PythonLanguageServiceProvider>.Instance),
            new TypeScriptLanguageServiceProvider(new AlwaysAvailableProbe(), NullLogger<TypeScriptLanguageServiceProvider>.Instance),
            new JavaLanguageServiceProvider(new AlwaysAvailableProbe(), NullLogger<JavaLanguageServiceProvider>.Instance)
        ]);

    private static string CreateShadowWorktree()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codexflow-shadow-lsp",
            Guid.NewGuid().ToString("N"),
            "shadows",
            "repo",
            "TASK-001");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        return root;
    }

    private sealed class AlwaysAvailableProbe : ILanguageServiceRuntimeProbe
    {
        public ValueTask<LanguageServiceRuntimeProbeResult> ProbeAsync(
            IReadOnlyList<string> commandCandidates,
            CancellationToken ct = default)
            => ValueTask.FromResult(new LanguageServiceRuntimeProbeResult
            {
                Available = true,
                Command = commandCandidates.FirstOrDefault(),
                DegradedReason = LanguageServiceDegradedReason.None
            });
    }
}
