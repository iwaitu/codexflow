// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

public sealed class ExecutionBackendRoutingTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "codexflow-p11-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private sealed class MutableOptionsMonitor(ExecutionOptions current) : IOptionsMonitor<ExecutionOptions>
    {
        public ExecutionOptions CurrentValue { get; private set; } = current;

        public ExecutionOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ExecutionOptions, string?> listener) => null;

        public void Set(ExecutionOptions next) => CurrentValue = next;
    }

    private sealed class UnavailableDockerCli : IDockerCli
    {
        public Task<DockerRunResult> RunAsync(DockerInvocation invocation, CancellationToken ct)
            => Task.FromResult(new DockerRunResult(1, string.Empty, "Cannot connect to the Docker daemon", false, false, false));
    }

    private static TrustedExecutionContext CreateContext(ExecutionNetworkMode mode = ExecutionNetworkMode.None)
        => new()
        {
            UserId = "u1",
            SessionId = "s1",
            JobId = "j1",
            TaskId = "t1",
            AttemptId = "a1",
            WorkspaceRoot = Path.GetFullPath(Path.GetTempPath()),
            Network = new ExecutionNetworkPolicy(mode)
        };

    [Fact]
    public void TrustedExecutionContext_Validate_AllowsHostNetwork_WhileDockerAndGateRefuseIt()
    {
        var hostCtx = CreateContext(ExecutionNetworkMode.Host);
        Assert.True(hostCtx.Validate().IsAllowed);

        var docker = new DockerIsolatedExecutionService(
            new UnavailableDockerCli(),
            Options.Create(new IsolationOptions()),
            NullLogger<DockerIsolatedExecutionService>.Instance);
        Assert.False(docker.SupportsNetworkMode(ExecutionNetworkMode.Host));
        Assert.True(docker.SupportsNetworkMode(ExecutionNetworkMode.None));

        var accessor = new TrustedExecutionContextAccessor();
        using var scope = accessor.Begin(hostCtx);
        var gate = new ToolIsolationGate(docker, accessor, new IsolationOptions());
        var refusal = gate.TryAcquire("run_command", ToolCapability.ExecuteUntrustedCode, out _, out _);
        Assert.NotNull(refusal);
        Assert.NotEqual(CodexFlow.Core.Models.ToolResultStatus.Success, refusal.Status);
        Assert.Contains(ExecutionReasonCodes.NetworkNotPermitted, refusal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Router_DockerUnavailable_RefusesWithoutFallingBackToLocal()
    {
        var docker = new DockerIsolatedExecutionService(
            new UnavailableDockerCli(),
            Options.Create(new IsolationOptions()),
            NullLogger<DockerIsolatedExecutionService>.Instance);
        var localStub = new FakeIsolationService();
        var monitor = new MutableOptionsMonitor(new ExecutionOptions { Backend = ExecutionBackend.Docker });
        var router = new ExecutionBackendRouter(monitor, docker, localStub);

        var result = await router.OpenSessionAsync(CreateContext(), new IsolatedSessionOptions("alpine:3.20"), CancellationToken.None);
        Assert.False(result.Decision.IsAllowed);
        Assert.Equal(ExecutionDecisionKind.Unavailable, result.Decision.Kind);
        Assert.Empty(localStub.Requests);

        monitor.Set(new ExecutionOptions { Backend = ExecutionBackend.Local });
        var localResult = await router.OpenSessionAsync(CreateContext(), new IsolatedSessionOptions("alpine:3.20"), CancellationToken.None);
        Assert.True(localResult.Decision.IsAllowed);
        Assert.NotNull(localResult.Session);
    }

    [Fact]
    public void ProjectConfig_CannotOverrideExecutionSection()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(
            Path.Combine(_tempDir, "codexflow.yml"),
            """
            Execution:
              Backend: Local
              PermissionMode: FullAuto
            Project:
              Name: demo
            """);

        var config = new ConfigurationBuilder()
            .AddCodexFlowLayers(_tempDir, trustProjectConfig: true, globalDirectory: Path.Combine(_tempDir, "global"))
            .Build();

        Assert.Null(config["Execution:Backend"]);
        Assert.Null(config["Execution:PermissionMode"]);
        Assert.Equal("demo", config["Project:Name"]);
    }

    [Fact]
    public void ExecutionSettingsService_MigrationAndTightenOnlyReload()
    {
        Directory.CreateDirectory(_tempDir);
        var legacyPath = Path.Combine(_tempDir, "legacy.json");
        File.WriteAllText(legacyPath, """{ "Isolation": { "DefaultImage": "custom:1" } }""");

        var legacyService = new ExecutionSettingsService(legacyPath);
        Assert.True(legacyService.MigrateLegacyIsolationIfNeeded());
        Assert.Equal(ExecutionBackend.Docker, legacyService.Read().Backend);
        Assert.False(legacyService.MigrateLegacyIsolationIfNeeded());

        var freshPath = Path.Combine(_tempDir, "fresh.json");
        var freshService = new ExecutionSettingsService(freshPath);
        Assert.False(freshService.MigrateLegacyIsolationIfNeeded());
        Assert.Equal(ExecutionBackend.Local, freshService.Read().Backend);
        Assert.Equal(ExecutionPermissionMode.Ask, freshService.Read().PermissionMode);

        freshService.Update(new ExecutionSettingsUpdate(PermissionMode: ExecutionPermissionMode.AutoInWorkspace));
        Assert.Equal(ExecutionPermissionMode.AutoInWorkspace, freshService.EffectivePermissionMode);

        // Relaxing via file reload must be ignored.
        Assert.Equal(
            ExecutionPermissionMode.AutoInWorkspace,
            freshService.ApplyFileReloadedPermissionMode(ExecutionPermissionMode.FullAuto));

        // Tightening via file reload takes effect immediately.
        Assert.Equal(
            ExecutionPermissionMode.Ask,
            freshService.ApplyFileReloadedPermissionMode(ExecutionPermissionMode.Ask));
        Assert.Equal(
            ExecutionPermissionMode.Deny,
            freshService.ApplyFileReloadedPermissionMode(ExecutionPermissionMode.Deny));
    }

    [Fact]
    public void ExecutionPermissionClamp_PostConfigure_OnlyTightensAcrossReloads()
    {
        var clamp = new ExecutionPermissionClamp();
        var initial = new ExecutionOptions { PermissionMode = ExecutionPermissionMode.AutoInWorkspace };
        clamp.PostConfigure(null, initial);
        Assert.Equal(ExecutionPermissionMode.AutoInWorkspace, initial.PermissionMode);

        // File reload tries to relax to FullAuto -> clamped back to AutoInWorkspace.
        var relaxed = new ExecutionOptions { PermissionMode = ExecutionPermissionMode.FullAuto };
        clamp.PostConfigure(null, relaxed);
        Assert.Equal(ExecutionPermissionMode.AutoInWorkspace, relaxed.PermissionMode);

        // File reload tightens to Ask -> accepted.
        var tightened = new ExecutionOptions { PermissionMode = ExecutionPermissionMode.Ask };
        clamp.PostConfigure(null, tightened);
        Assert.Equal(ExecutionPermissionMode.Ask, tightened.PermissionMode);
    }

    [Fact]
    public void AddCodexIsolatedExecution_ValidatesDiContainerAndSharesPermissionClamp()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Execution:Backend"] = "Local",
                ["Execution:PermissionMode"] = "Ask"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodexIsolatedExecution(config);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var router = provider.GetRequiredService<IIsolatedExecutionService>();
        Assert.IsType<ExecutionBackendRouter>(router);
        Assert.Equal(ExecutionPermissionMode.Ask, provider.GetRequiredService<IOptionsMonitor<ExecutionOptions>>().CurrentValue.PermissionMode);
        var clamp = provider.GetRequiredService<ExecutionPermissionClamp>();
        Assert.Same(
            clamp,
            Assert.Single(provider.GetServices<IPostConfigureOptions<ExecutionOptions>>().OfType<ExecutionPermissionClamp>()));
    }

    [Fact]
    public void DefaultExecutionOptions_DefaultsToLocalAndAsk_AndHostPreflightDoesNotRequireDocker()
    {
        var emptyConfig = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodexIsolatedExecution(emptyConfig);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var effective = provider.GetRequiredService<IOptionsMonitor<ExecutionOptions>>().CurrentValue;
        Assert.Equal(ExecutionBackend.Local, effective.Backend);
        Assert.Equal(ExecutionPermissionMode.Ask, effective.PermissionMode);
        Assert.Equal(ExecutionBackend.Local, provider.GetRequiredService<IIsolatedExecutionService>().ActiveBackend);

        var clamp = provider.GetRequiredService<ExecutionPermissionClamp>();
        var resolved = clamp.ResolveEffective(provider.GetRequiredService<IOptionsMonitor<ExecutionOptions>>());
        Assert.Equal((ExecutionBackend.Local, ExecutionPermissionMode.Ask), resolved);
    }
}
