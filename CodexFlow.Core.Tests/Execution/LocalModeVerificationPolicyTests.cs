// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

public sealed class LocalModeVerificationPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-p14-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_LocalDefaultsToSkip_AndHonorsBlock()
    {
        var defaultLocal = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Execution:Backend"] = "Local" })
            .Build();
        var evalDefault = LocalModeVerificationPolicy.Evaluate(defaultLocal);
        Assert.True(evalDefault.IsLocalBackend);
        Assert.Equal(LocalVerificationModePolicy.Skip, evalDefault.Policy);

        var blockLocal = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Execution:Backend"] = "Local",
                ["ArtifactVerification:LocalModePolicy"] = "Block"
            })
            .Build();
        var evalBlock = LocalModeVerificationPolicy.Evaluate(blockLocal);
        Assert.True(evalBlock.IsLocalBackend);
        Assert.Equal(LocalVerificationModePolicy.Block, evalBlock.Policy);

        var dockerConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Execution:Backend"] = "Docker" })
            .Build();
        Assert.False(LocalModeVerificationPolicy.Evaluate(dockerConfig).IsLocalBackend);
    }

    [Fact]
    public void ProjectConfig_CannotOverrideArtifactVerificationLocalModePolicy()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "codexflow.yml"),
            """
            ArtifactVerification:
              LocalModePolicy: Block
            """);

        var config = new ConfigurationBuilder()
            .AddCodexFlowLayers(_root, trustProjectConfig: true, globalDirectory: Path.Combine(_root, "global"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Execution:Backend"] = "Local" })
            .Build();

        var eval = LocalModeVerificationPolicy.Evaluate(config);
        Assert.Equal(LocalVerificationModePolicy.Skip, eval.Policy);
    }

    [Fact]
    public void ReceiptContract_SupportsSkippedWithoutIsolationMetadata()
    {
        var receipt = new ForgeArtifactVerificationReceipt
        {
            IdentityVerified = true,
            VerificationStatus = LocalModeVerificationPolicy.SkippedReasonCode,
            VerifiedWithoutIsolation = true,
            VerificationNotice = LocalModeVerificationPolicy.UnverifiedNotice
        };

        Assert.True(receipt.VerifiedWithoutIsolation);
        Assert.Equal("skipped(requires_docker_backend)", receipt.VerificationStatus);
        Assert.Equal("未在隔离环境中验证", receipt.VerificationNotice);
    }
}
