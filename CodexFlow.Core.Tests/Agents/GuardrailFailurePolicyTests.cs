// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Adapters;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public sealed class GuardrailFailurePolicyTests
{
    private static CodexSession Session() => new() { Id = "guard-policy", WorkspacePath = Path.GetTempPath() };
    private static Dictionary<string, object?> Arguments() => new() { ["path"] = "new.cs" };
    private static DefaultCodexGuardrail Guard(Mock<ICodeAnalysisService> analysis) =>
        new(analysis.Object, NullLogger<DefaultCodexGuardrail>.Instance);

    [Fact]
    public void UninitializedDecision_DoesNotAuthorizeWrite() => Assert.False(new GuardrailCheckResult().IsAllowed);

    [Fact]
    public async Task AdapterFailure_DoesNotTurnDenialIntoPermission()
    {
        var guard = new Mock<ICodexGuardrail>();
        guard.Setup(x => x.CheckAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("unavailable"));
        var adapter = new KernelRuntimeEventAdapter(Session(), CodexAgentRole.Forge, null, guard.Object, NullLogger.Instance, null);
        var decision = await adapter.OnToolCallRequestedAsync("hs_write", Arguments(), Session());
        Assert.True(decision.ShouldBlock);
        Assert.Equal("AnalysisUnavailable", decision.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingGraphOrDecision_DeniesWrite(bool missingDecision)
    {
        var analysis = new Mock<ICodeAnalysisService>();
        analysis.Setup(x => x.BuildGraphAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(missingDecision ? new DependencyGraph() : null!);
        var result = await Guard(analysis).CheckAsync(Session(), "write_file", Arguments());
        Assert.False(result.IsAllowed);
        Assert.Equal("AnalysisUnavailable", result.ReasonCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalysisFailure_DeniesWrite(bool timeout)
    {
        var analysis = new Mock<ICodeAnalysisService>();
        analysis.Setup(x => x.BuildGraphAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(timeout ? new TimeoutException() : new IOException("analysis failed"));
        var result = await Guard(analysis).CheckAsync(Session(), "write_file", Arguments());
        Assert.False(result.IsAllowed);
        Assert.Equal(timeout ? "AnalysisTimeout" : "AnalysisUnavailable", result.ReasonCode);
    }

    [Fact]
    public async Task SuccessfulEmptyGraph_StillUsesExplicitPolicyDecision()
    {
        var analysis = new Mock<ICodeAnalysisService>();
        analysis.Setup(x => x.BuildGraphAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DependencyGraph());
        analysis.Setup(x => x.CheckGuardrailAsync(It.IsAny<DependencyGraph>(), "new.cs", It.IsAny<string>()))
            .ReturnsAsync(new GuardrailResult(false, null));
        Assert.True((await Guard(analysis).CheckAsync(Session(), "write_file", Arguments())).IsAllowed);
        analysis.Verify(x => x.CheckGuardrailAsync(It.IsAny<DependencyGraph>(), "new.cs", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var analysis = new Mock<ICodeAnalysisService>();
        analysis.Setup(x => x.BuildGraphAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => { cancellation.Cancel(); return Task.FromCanceled<DependencyGraph>(cancellation.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Guard(analysis).CheckAsync(Session(), "write_file", Arguments(), cancellation.Token).AsTask());
    }
}
