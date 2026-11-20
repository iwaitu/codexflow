// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts;
using CodexFlow.Core.Services;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public sealed class ArtifactQualityGateTests
{
    [Fact]
    public void ComputeManifestHash_MatchesPersistedFormat()
    {
        var date = new DateTimeOffset(2026, 11, 17, 14, 12, 35, TimeSpan.FromMinutes(330)).AddTicks(1_234_567);
        var manifest = new ForgeArtifactManifest
        {
            Version = 1,
            ArtifactId = "artifact-制品",
            UserId = "user-🦊",
            SessionId = "session",
            JobId = "job",
            TaskId = "task-甲",
            Attempt = 7,
            RepositoryIdentity = "repo/<>&\"'",
            WorktreePath = "C:\\工作树\\source\n\t",
            BaseCommit = new string('b', 40),
            HeadCommit = new string('a', 40),
            ScopeHash = "scope-\u2028",
            PlanArtifactId = "plan-料",
            PlanVersion = "v1-\r",
            PlanHash = new string('c', 64),
            RequiredTaskIds = new List<string> { "task-乙", "task-甲" },
            ParentArtifactIds = ["制品-2", "artifact-1", "制品-2"],
            ChangedPaths = ["src/乙.cs", "src/甲.cs"],
            CreatedAtUtc = date
        };

        // These hashes were captured from the reflection-based serializer before the AOT change.
        Assert.Equal("FDE550F3DACA3ADD50A5EB9043A52B906A6E4B68FF89559260C7441B03110799",
            ArtifactQualityGate.ComputeManifestHash(manifest));
        Assert.Equal("0157AB0525F41110C917B59D84991B16AF95CA6F0DA5032B915461DA2426C95D",
            ArtifactQualityGate.ComputeManifestHash(manifest with { CreatedAtUtc = date.ToUniversalTime() }));
        Assert.Equal("FD93FD84337EDB33A5C5F8152BDBEA1B25FD6522F6F3E1CA7A2155068CC8201E",
            ArtifactQualityGate.ComputeManifestHash(manifest with { ChangedPaths = ["src/甲.cs", "src/乙.cs"] }));
        Assert.Equal("1DC852735EADDF62F897919DB6D0ADB76061456E94FFA2EE52B6BCE059FACD8F",
            ArtifactQualityGate.ComputeManifestHash(new()));
        Assert.Equal("AB50C47751CA3A4D2DC8E3E84D884E33EB27B9A8DBB20B5B94CCFDBD7D3A8927",
            ArtifactQualityGate.ComputeManifestHash(manifest with
            {
                ArtifactId = null!,
                RequiredTaskIds = null!,
                ParentArtifactIds = [null!, "", "a"],
                ChangedPaths = []
            }));
        Assert.Equal("74234E98AFE7498FB5DAF1F36AC2D78ACC339464F950703B8C019892F982B90B",
            ArtifactQualityGate.ComputeManifestHash(null!));
    }

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
