// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using CodexFlow.Orchestration.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

public sealed class QreRuntimeAuditEnvironmentTests : IDisposable
{
    private readonly string _auditDir = Path.Combine(Path.GetTempPath(), "qre-env-audit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_auditDir))
        {
            Directory.Delete(_auditDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData(null)]
    public async Task AddCodexFlowRuntime_DisablesQreAuditRecording_OutsideDevelopment(string? environmentName)
    {
        using var provider = BuildServiceProvider(environmentName, _auditDir);
        var options = provider.GetRequiredService<IOptions<QueryRuntimeBackendOptions>>();
        Assert.Null(options.Value.AuditDirectory);

        var engine = new QreV2BackedQueryRuntimeEngine(
            new StubLlmExecutor("no-audit-in-release"),
            contextWindowManager: null,
            telemetry: null,
            NullLogger<QreV2BackedQueryRuntimeEngine>.Instance,
            backendOptions: options);

        await engine.ExecuteAsync(CreateRequest(), new NoopEventSink());

        Assert.False(Directory.Exists(_auditDir));
    }

    [Fact]
    public async Task AddCodexFlowRuntime_PreservesQreAuditRecording_InDevelopment()
    {
        using var provider = BuildServiceProvider(Environments.Development, _auditDir);
        var options = provider.GetRequiredService<IOptions<QueryRuntimeBackendOptions>>();
        Assert.Equal(_auditDir, options.Value.AuditDirectory);

        var engine = new QreV2BackedQueryRuntimeEngine(
            new StubLlmExecutor("dev-audit-marker"),
            contextWindowManager: null,
            telemetry: null,
            NullLogger<QreV2BackedQueryRuntimeEngine>.Instance,
            backendOptions: options);

        await engine.ExecuteAsync(CreateRequest(), new NoopEventSink());

        Assert.True(Directory.Exists(_auditDir));
        var files = Directory.GetFiles(_auditDir, "*", SearchOption.AllDirectories);
        Assert.Contains(files, f => f.Contains(Path.Combine(".qre", "v2", "private", "runs"), StringComparison.Ordinal));
    }

    private static ServiceProvider BuildServiceProvider(string? environmentName, string auditDirectory)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{QueryRuntimeBackendOptions.SectionName}:Backend"] = QueryRuntimeBackendOptions.QreV2Backend,
                [$"{QueryRuntimeBackendOptions.SectionName}:ModelStreamTimeoutMs"] = "180000",
                [$"{QueryRuntimeBackendOptions.SectionName}:AuditDirectory"] = auditDirectory
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        if (environmentName is not null)
        {
            services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(environmentName));
        }

        services.AddCodexFlowRuntime(configuration);
        return services.BuildServiceProvider();
    }

    private static QueryRuntimeRequest CreateRequest()
        => new()
        {
            SessionId = "qre-env-session",
            EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
            InitialMessages = [new ChatMessage(ChatRole.User, "hello")],
            MaxRounds = 2,
            EnableTools = false,
            Scenario = MemoryInjectionScenario.Chat
        };

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "CodexFlow.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubLlmExecutor(string responseText) : ILLMExecutor
    {
        public Task<ChatResponse> ExecuteAsync(LLMExecutionRequest request, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            LLMExecutionRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(responseText)]);
        }
    }

    private sealed class NoopEventSink : IQueryRuntimeEventSink
    {
        public bool IsEnabled(QueryRuntimeEventType eventType) => false;

        public ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent) => ValueTask.CompletedTask;
    }
}
