// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Protocols;
using CodexFlow.Core.Runtime;
using Xunit;

namespace CodexFlow.Core.Tests;

public class TransientFailuresTests
{
    [Theory]
    [InlineData("model_stream_failed", "QRE-backed QueryRuntime model stream timed out after 60s")]
    [InlineData("TimeoutException", "")]
    [InlineData("", "Vllm error: event:error")]
    [InlineData("", "429 rate limit exceeded")]
    [InlineData("", "Throttling.RateQuota")]
    [InlineData("", "InternalError.Algo.InternalPostprocessError")]
    [InlineData("", "The operation was canceled.")]
    [InlineData("", "connection closed unexpectedly")]
    // Live run (dependency-upgrade-compliant): a worker's connection to the model failed before any answer and the runtime's own retries ran
    // out; the job failed for good instead of being tried again, and the rest of the plan never ran.
    [InlineData("provider_transport_failed", "An error occurred while sending the request.")]
    public void ProviderSideFailuresAreTransient(string detail, string message)
    {
        Assert.True(TransientFailures.IsTransientProviderFailure(detail, message));
        Assert.True(TransientFailures.IsTransientWorkerFailure(detail, message));
    }

    [Theory]
    [InlineData("WorkspaceUnavailable", "")]
    [InlineData("", "trying to start process '/usr/bin/git'")]
    [InlineData("", "No such file or directory")]
    public void HostEnvironmentFailuresAreTransientForWorkersOnly(string detail, string message)
    {
        Assert.True(TransientFailures.IsTransientWorkerFailure(detail, message));
        Assert.False(TransientFailures.IsTransientProviderFailure(detail, message));
    }

    [Theory]
    [InlineData("offline_restore_unavailable", "NU1301 offline restore")]
    [InlineData(JobFailureClassifier.RepositoryOwnershipCode, "Git 拒绝访问工作区仓库，因为其所有者与运行 CodexFlow 的账户不一致。")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void OrdinaryFailuresAreNot(string? detail, string? message) =>
        Assert.False(TransientFailures.IsTransientWorkerFailure(detail, message));

    [Fact]
    public void TransportExceptionsAreJudgedByTypeThenMessage()
    {
        Assert.True(TransientFailures.IsTransientTransport(new HttpRequestException("x"), default));
        Assert.True(TransientFailures.IsTransientTransport(new IOException("Response ended prematurely"), default));
        Assert.True(TransientFailures.IsTransientTransport(new InvalidOperationException("outer", new IOException("connection reset by peer")), default));
        Assert.False(TransientFailures.IsTransientTransport(new InvalidOperationException("bad request body"), default));
    }

    // Live run (local vLLM, bugfix-paging): "An error occurred while sending the request." ended the turn as a non-retryable model_stream_failed.
    [Fact]
    public void AFailedConnectionCanBeSentAgain_AnHttpStatusOrACancellationCannot()
    {
        Assert.True(TransientFailures.IsConnectionFailure(new HttpRequestException("An error occurred while sending the request.")));
        Assert.True(TransientFailures.IsConnectionFailure(new InvalidOperationException("outer", new System.Net.Sockets.SocketException(10061))));
        Assert.True(TransientFailures.IsConnectionFailure(new InvalidOperationException("outer", new IOException("Response ended prematurely"))));
        // a provider SDK that rethrows as a plain exception keeps the message
        Assert.True(TransientFailures.IsConnectionFailure(new InvalidOperationException("An error occurred while sending the request.")));

        // the server answered: its status decides, not this rule
        Assert.False(TransientFailures.IsConnectionFailure(new HttpRequestException("bad request", null, System.Net.HttpStatusCode.BadRequest)));
        Assert.False(TransientFailures.IsConnectionFailure(new InvalidOperationException("bad request body")));
        Assert.False(TransientFailures.IsConnectionFailure(new TaskCanceledException("timeout", new IOException("aborted"))));
    }

    [Fact]
    public void CancellationTheCallerAskedForIsNeverTransient()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(TransientFailures.IsTransientTransport(new OperationCanceledException(), cts.Token));
        Assert.True(TransientFailures.IsTransientTransport(new TaskCanceledException(), default));
    }
}
