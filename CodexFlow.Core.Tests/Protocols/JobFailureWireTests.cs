// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Protocols;
using Newtonsoft.Json;
using Xunit;

namespace CodexFlow.Core.Tests.Protocols;

/// <summary>The failure crosses the notification pipeline as text (an envelope's string metadata); it must come out the other end the same.</summary>
public class JobFailureWireTests
{
    [Fact]
    public void AFailure_SurvivesTheRoundTrip_WithItsKindRemedyPathsAndEvidence()
    {
        var failure = JobFailureClassifier.Classify("stalled", JsonConvert.SerializeObject(new
        {
            detailCode = "stall_detected",
            scopeDenials = new[] { new { tool = "edit_file", path = "src/Other/Other.csproj", reason = "x" } },
            workerReport = new { reason = "needs the project file" }
        }));

        var back = JobFailureWire.FromJson(JobFailureWire.ToJson(failure));

        Assert.NotNull(back);
        Assert.Equal(failure.Code, back!.Code);
        Assert.Equal(JobFailureKind.ScopeBoundary, back.Kind);
        Assert.Equal(JobFailureRemedy.ChangePlanOrScope, back.Remedy);
        Assert.Equal(failure.RefusedPaths, back.RefusedPaths);
        Assert.Equal(failure.Evidence, back.Evidence);
        Assert.False(back.Retryable);
    }

    [Fact]
    public void TheJson_UsesTheNamesOfTheKindAndRemedy_NotTheirNumbers()
    {
        var json = JobFailureWire.ToJson(JobFailureClassifier.Classify("x", """{"detailCode":"transient_provider_failure"}"""));

        Assert.Contains("\"Transient\"", json);
        Assert.Contains("\"RetryAsIs\"", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"code\":")]
    public void ABrokenOrMissingValue_IsNoFailure_AndNeverThrows(string? json)
        => Assert.Null(JobFailureWire.FromJson(json));
}
