// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts;
using CodexFlow.Core.Services;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public sealed class ArtifactQualityGateTests
{
    private static ForgeArtifactVerificationReceipt Receipt()
    {
        var manifest = new ForgeArtifactManifest
        {
            ArtifactId = "artifact", UserId = "user", SessionId = "session", JobId = "job",
            ScopeHash = "scope", RepositoryIdentity = "repo", HeadCommit = new string('a', 40), TaskId = "task",
            PlanArtifactId = "plan", PlanVersion = "v1", PlanHash = new string('c', 64), RequiredTaskIds = ["task"],
            ChangedPaths = ["src/A.cs", "tests/T.cs"]
        };
        return new()
        {
            Manifest = manifest, ManifestHash = ArtifactQualityGate.ComputeManifestHash(manifest), IdentityVerified = true,
            BuildPassed = true, BuildExitCode = 0, TestsPassed = true, TestExitCode = 0, TestCount = 6,
            VerificationImage = "controlled-image",
            VerificationImageId = "sha256:" + new string('d', 64), VerificationProjects = ["App.csproj"],
            Commands = new[] { "restore", "build", "test" }.Select(stage => new ArtifactCommandEvidence(stage, ["dotnet", stage], 0, "Completed", new string('a', 64), 1)
                { IsolationEvidence = "image=sha256:" + new string('d', 64) + "; network=none" }).ToArray(),
            TestEvidenceHashes = [new string('b', 64)],
            Audit = new()
            {
                State = ArtifactAuditState.Pass, ArtifactId = "artifact", Commit = manifest.HeadCommit,
                ManifestHash = ArtifactQualityGate.ComputeManifestHash(manifest), Scanner = "dependency+secret", ScannerVersion = "1",
                Layers = new[] { "secrets", "dependencies", "model" }.Select(name => new ArtifactAuditLayerResult
                {
                    Name = name, State = ArtifactAuditState.Pass, Tool = name, ToolVersion = "1", DataVersion = "data-1",
                    CompletedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-2)
                }).ToArray(),
                ReviewedPaths = ["src/A.cs", "tests/T.cs", "src/FromParent.cs"],   // current paths + (server-verified) parent paths
                CompletedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1)
            },
            CompletedAtUtc = DateTimeOffset.UtcNow
        };
    }

    [Theory]
    [InlineData(ArtifactAuditState.NotRun)]
    [InlineData(ArtifactAuditState.Error)]
    [InlineData(ArtifactAuditState.Fail)]
    [InlineData(ArtifactAuditState.NeedsReview)]
    [InlineData((ArtifactAuditState)999)]
    public void NonPassingAudit_BlocksEvenWithBuildAndTests(ArtifactAuditState state)
    {
        var receipt = Receipt();
        Assert.False(ArtifactQualityGate.Passes(receipt with { Audit = receipt.Audit with { State = state } }, "session", "job"));
    }

    [Fact]
    public void ExactEvidencePasses_ButCrossCommitOrSessionDoesNot()
    {
        var receipt = Receipt();
        Assert.True(ArtifactQualityGate.Passes(receipt, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt, "other-session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt, "session", "other-job"));
        Assert.False(ArtifactQualityGate.Passes(receipt with { Audit = receipt.Audit with { Commit = new string('b', 40) } }, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt with { Audit = receipt.Audit with { ManifestHash = "stale" } }, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt with { TestCount = 0 }, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt with { Commands = [] }, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt with { VerificationImageId = "unresolved-tag" }, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt with { VerificationProjects = [] }, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt with { Commands = receipt.Commands.Select(command => command with { IsolationEvidence = "" }).ToArray() }, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt with { TestEvidenceHashes = [] }, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt with { Audit = receipt.Audit with { Layers = [] } }, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt with { IdentityVerified = false }, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt with { Audit = receipt.Audit with { Findings = [new("risk", "high", "a.cs", "confirmed")] } }, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(new(), "session", "job"));
    }

    [Fact]
    public void MissingOrEmptyReviewedPaths_NeverPass_SoAnOldBackendCannotPassTheNewGate()
    {
        var receipt = Receipt();
        Assert.True(ArtifactQualityGate.Passes(receipt, "session", "job"));
        // An older backend does not send the field at all: it deserializes to the empty default.
        Assert.False(ArtifactQualityGate.Passes(receipt with { Audit = receipt.Audit with { ReviewedPaths = [] } }, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt with { Audit = receipt.Audit with { ReviewedPaths = null! } }, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt with { Audit = receipt.Audit with { ReviewedPaths = [" ", "src/A.cs", "tests/T.cs"] } }, "session", "job"));
    }

    [Fact]
    public void CurrentChangedPathNotCoveredByReviewedPaths_NeverPasses()
    {
        var receipt = Receipt();
        Assert.False(ArtifactQualityGate.Passes(receipt with { Audit = receipt.Audit with { ReviewedPaths = ["src/A.cs"] } }, "session", "job"));
        Assert.False(ArtifactQualityGate.Passes(receipt with { Audit = receipt.Audit with { ReviewedPaths = ["src/FromParent.cs"] } }, "session", "job"));
        // ordinal comparison: a case difference is a different path
        Assert.False(ArtifactQualityGate.Passes(receipt with { Audit = receipt.Audit with { ReviewedPaths = ["SRC/A.cs", "tests/T.cs"] } }, "session", "job"));
    }

    [Fact]
    public void ExtraReviewedPaths_AreAllowed_BecauseParentPathsAreVerifiedByTheServer()
    {
        var receipt = Receipt();
        Assert.True(ArtifactQualityGate.Passes(receipt with { Audit = receipt.Audit with { ReviewedPaths = ["tests/T.cs", "src/A.cs", "x/Parent.cs", "y/Other.cs"] } }, "session", "job"));
    }
}
