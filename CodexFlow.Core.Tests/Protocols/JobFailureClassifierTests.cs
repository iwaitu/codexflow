// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Protocols;
using Newtonsoft.Json;
using Xunit;

namespace CodexFlow.Core.Tests.Protocols;

/// <summary>
/// A failed job used to reach the coordinator as a free-text message. What the coordinator needs to decide is structured: what kind of failure
/// it was, what evidence there is (which paths the scope guard refused, which packages came out at the wrong version), and what would change the
/// outcome (try again, try again with guidance, change the plan or scope, ask the user, fix the environment).
/// </summary>
public class JobFailureClassifierTests
{
    private static string Payload(object value) => JsonConvert.SerializeObject(value);

    [Theory]
    [InlineData("transient_provider_failure", JobFailureKind.Transient, JobFailureRemedy.RetryAsIs, true)]
    [InlineData("transport_failure", JobFailureKind.Transient, JobFailureRemedy.RetryAsIs, true)]
    [InlineData("no_relevant_change", JobFailureKind.Quality, JobFailureRemedy.RetryWithGuidance, true)]
    [InlineData("no_persisted_changes", JobFailureKind.Quality, JobFailureRemedy.RetryWithGuidance, true)]
    [InlineData("stall_detected", JobFailureKind.Quality, JobFailureRemedy.RetryWithGuidance, true)]
    [InlineData("max_rounds_reached", JobFailureKind.Quality, JobFailureRemedy.RetryWithGuidance, true)]
    [InlineData("dependency_contract_unmet", JobFailureKind.Quality, JobFailureRemedy.RetryWithGuidance, true)]
    [InlineData("offline_restore_unavailable", JobFailureKind.HostDecision, JobFailureRemedy.ChangePlanOrScope, false)]
    [InlineData("plan_not_approved", JobFailureKind.HostDecision, JobFailureRemedy.AskUser, false)]
    [InlineData("session_not_owned_by_user", JobFailureKind.HostDecision, JobFailureRemedy.AskUser, false)]
    [InlineData("scope_violation", JobFailureKind.ScopeBoundary, JobFailureRemedy.ChangePlanOrScope, false)]
    [InlineData("provider_parameter_incompatibility", JobFailureKind.Infrastructure, JobFailureRemedy.FixEnvironment, false)]
    [InlineData("git_service_unavailable", JobFailureKind.Infrastructure, JobFailureRemedy.FixEnvironment, false)]
    public void EveryKnownCode_HasAKindAndARemedy_AndOnlyTheRemediesThatCanChangeTheOutcomeAreRetryable(
        string code, JobFailureKind kind, JobFailureRemedy remedy, bool retryable)
    {
        var failure = JobFailureClassifier.Classify("job failed", Payload(new { detailCode = code }));

        Assert.Equal(code, failure.Code);
        Assert.Equal(kind, failure.Kind);
        Assert.Equal(remedy, failure.Remedy);
        Assert.Equal(retryable, failure.Retryable);
    }

    [Fact]
    public void AnUnknownCode_IsUnknown_AndMayBeRetriedWithGuidance_ButNeverAsIs()
    {
        var failure = JobFailureClassifier.Classify("boom", Payload(new { detailCode = "something_new" }));

        Assert.Equal("something_new", failure.Code);
        Assert.Equal(JobFailureKind.Unknown, failure.Kind);
        Assert.Equal(JobFailureRemedy.RetryWithGuidance, failure.Remedy);
    }

    [Fact]
    public void AnExceptionNameAsDetailCode_IsAnInfrastructureFailure()
    {
        var failure = JobFailureClassifier.Classify("boom", Payload(new { detailCode = "IOException: disk full" }));

        Assert.Equal(JobFailureKind.Infrastructure, failure.Kind);
        Assert.Equal(JobFailureRemedy.FixEnvironment, failure.Remedy);
    }

    [Fact]
    public void WhenThePayloadHasNoCode_TheCodeInTheErrorMessageIsUsed()
    {
        var failure = JobFailureClassifier.Classify(
            "Forge failed [DetailCode=offline_restore_unavailable, Termination=Completed, Rounds=3]", null);

        Assert.Equal("offline_restore_unavailable", failure.Code);
        Assert.Equal(JobFailureKind.HostDecision, failure.Kind);
    }

    [Fact]
    public void TheMessage_ComesFromThePayloadFirst_AndIsBounded()
    {
        var failure = JobFailureClassifier.Classify(
            "short", Payload(new { detailCode = "stall_detected", message = new string('x', 5000) }));

        Assert.StartsWith("xxx", failure.Message);
        Assert.True(failure.Message.Length <= JobFailureClassifier.MaxMessageChars + 1);
    }

    [Fact]
    public void RefusedPaths_AreTakenFromTheScopeDenials_AndTheOutOfScopeFiles_WithoutDuplicates()
    {
        var failure = JobFailureClassifier.Classify("blocked", Payload(new
        {
            detailCode = "worker_reported_blocked",
            scopeDenials = new object[]
            {
                new { tool = "edit_file", path = "src/Other/Other.csproj", reason = "outside allowed scope" },
                new { tool = "write_file", path = "src/Other/Other.csproj", reason = "outside allowed scope" },
                new { tool = "edit_file", path = "package.json", reason = "outside allowed scope" }
            },
            outOfScopeFiles = new[] { "src/Third/Third.csproj", "package.json" }
        }));

        Assert.Equal(["src/Other/Other.csproj", "package.json", "src/Third/Third.csproj"], failure.RefusedPaths);
    }

    [Fact]
    public void AWorkerThatStoppedAfterTheGuardRefusedItsPaths_IsAScopeBoundaryFailure_NotAQualityOne()
    {
        // The worker did not do a poor job: it was not allowed to touch the file the task needs. Retrying with the same scope is refused again.
        var failure = JobFailureClassifier.Classify("stalled", Payload(new
        {
            detailCode = "stall_detected",
            scopeDenials = new object[] { new { tool = "edit_file", path = "src/Other/Other.csproj", reason = "outside allowed scope" } }
        }));

        Assert.Equal(JobFailureKind.ScopeBoundary, failure.Kind);
        Assert.Equal(JobFailureRemedy.ChangePlanOrScope, failure.Remedy);
        Assert.False(failure.Retryable);
        Assert.Equal(["src/Other/Other.csproj"], failure.RefusedPaths);
    }

    [Fact]
    public void ATransientFailure_StaysTransient_EvenIfTheGuardRefusedSomethingEarlier()
    {
        var failure = JobFailureClassifier.Classify("provider", Payload(new
        {
            detailCode = "transient_provider_failure",
            scopeDenials = new object[] { new { tool = "edit_file", path = "a.csproj", reason = "x" } }
        }));

        Assert.Equal(JobFailureKind.Transient, failure.Kind);
    }

    [Fact]
    public void UnmetPackages_AndTheWorkersOwnReport_BecomeEvidence()
    {
        var failure = JobFailureClassifier.Classify("unmet", Payload(new
        {
            detailCode = "dependency_contract_unmet",
            unmetPackages = new object[] { new { package = "Newtonsoft.Json", project = "src/App/App.csproj", expected = "13.0.3", actual = "12.0.3" } },
            workerReport = new { status = "blocked", reason = "needs a version the host did not fetch", unfinished = new[] { "upgrade Serilog" } }
        }));

        Assert.Contains(failure.Evidence, e => e.Contains("Newtonsoft.Json") && e.Contains("13.0.3") && e.Contains("12.0.3") && e.Contains("src/App/App.csproj"));
        Assert.Contains(failure.Evidence, e => e.Contains("needs a version the host did not fetch"));
        Assert.Contains(failure.Evidence, e => e.Contains("upgrade Serilog"));
    }

    [Fact]
    public void NoPayloadAndNoMessage_StillYieldsAFailure_WithAnUnknownCode()
    {
        var failure = JobFailureClassifier.Classify(null, null);

        Assert.Equal("unknown", failure.Code);
        Assert.Equal(JobFailureKind.Unknown, failure.Kind);
        Assert.Empty(failure.RefusedPaths);
    }

    [Fact]
    public void ThePromptText_NamesTheCode_TheRefusedPaths_AndWhatWouldChangeTheOutcome()
    {
        var failure = JobFailureClassifier.Classify("stalled", Payload(new
        {
            detailCode = "worker_reported_blocked",
            message = "needs to edit the project file",
            scopeDenials = new object[] { new { tool = "edit_file", path = "src/Other/Other.csproj", reason = "outside allowed scope" } }
        }));

        var text = failure.ToPromptText();

        Assert.Contains("worker_reported_blocked", text);
        Assert.Contains("src/Other/Other.csproj", text);
        Assert.Contains("范围", text);
        Assert.DoesNotContain("重试即可", text);
    }
}
