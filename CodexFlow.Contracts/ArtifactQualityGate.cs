// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexFlow.Contracts;

/// <summary>Pure evidence validation. Transport authentication and trusted storage are required by the caller.</summary>
public static class ArtifactQualityGate
{
    public static string ComputeManifestHash(ForgeArtifactManifest manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ArtifactManifestJsonContext.Default.ForgeArtifactManifest);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static bool Passes(ForgeArtifactVerificationReceipt receipt, string sessionId, string jobId)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var manifest = receipt.Manifest;
        var audit = receipt.Audit;
        return manifest is not null && audit is not null && manifest.Version == 1 &&
            !string.IsNullOrWhiteSpace(manifest.ArtifactId) &&
            !string.IsNullOrWhiteSpace(manifest.UserId) &&
            !string.IsNullOrWhiteSpace(manifest.ScopeHash) &&
            !string.IsNullOrWhiteSpace(manifest.RepositoryIdentity) &&
            !string.IsNullOrWhiteSpace(manifest.PlanArtifactId) && !string.IsNullOrWhiteSpace(manifest.PlanVersion) &&
            manifest.PlanHash is { Length: 64 } && manifest.PlanHash.All(Uri.IsHexDigit) &&
            manifest.RequiredTaskIds is { Count: > 0 } && manifest.RequiredTaskIds.Contains(manifest.TaskId, StringComparer.Ordinal) &&
            manifest.RequiredTaskIds.All(id => !string.IsNullOrWhiteSpace(id)) &&
            manifest.RequiredTaskIds.Distinct(StringComparer.Ordinal).Count() == manifest.RequiredTaskIds.Count &&
            !string.IsNullOrWhiteSpace(receipt.VerificationImage) &&
            receipt.VerificationImageId is { Length: 71 } && receipt.VerificationImageId.StartsWith("sha256:", StringComparison.Ordinal) &&
            receipt.VerificationImageId[7..].All(Uri.IsHexDigit) &&
            receipt.VerificationProjects is { Count: > 0 } && receipt.VerificationProjects.All(path => !string.IsNullOrWhiteSpace(path)) &&
            receipt.Commands is { Count: 3 } && new[] { "restore", "build", "test" }.All(stage =>
                receipt.Commands.Count(command => command is not null && command.Stage == stage && command.ExitCode == 0 && command.Status == "Completed" &&
                    command.Arguments is { Count: > 1 } && command.Arguments[0] == "dotnet" && command.Arguments[1] == stage &&
                    command.OutputHash is { Length: 64 } && command.OutputHash.All(Uri.IsHexDigit) &&
                    command.IsolationEvidence is { Length: > 0 } &&
                    command.IsolationEvidence.Split(';', StringSplitOptions.TrimEntries).Contains("image=" + receipt.VerificationImageId, StringComparer.Ordinal) &&
                    command.IsolationEvidence.Split(';', StringSplitOptions.TrimEntries).Contains("network=none", StringComparer.Ordinal)) == 1) &&
            receipt.TestEvidenceHashes is { Count: > 0 } && receipt.TestEvidenceHashes.All(hash => hash is { Length: 64 } && hash.All(Uri.IsHexDigit)) &&
            string.Equals(manifest.SessionId, sessionId, StringComparison.Ordinal) &&
            string.Equals(manifest.JobId, jobId, StringComparison.Ordinal) &&
            receipt.IdentityVerified && receipt.BuildPassed && receipt.BuildExitCode == 0 &&
            receipt.TestsPassed && receipt.TestExitCode == 0 && receipt.TestCount > 0 &&
            string.IsNullOrEmpty(receipt.FailureCode) &&
            string.Equals(receipt.ManifestHash, ComputeManifestHash(manifest), StringComparison.Ordinal) &&
            audit.State == ArtifactAuditState.Pass && audit.Findings is { Count: 0 } &&
            // The model audit must PROVE what it reviewed: a receipt without ReviewedPaths (an older backend, or a model layer that
            // did not complete) never passes, and every path of the CURRENT manifest must be covered. The complete set of parent
            // artifacts' paths is verified by the server; the client can only see (and must enforce) the current ChangedPaths.
            audit.ReviewedPaths is { Count: > 0 } && audit.ReviewedPaths.All(path => !string.IsNullOrWhiteSpace(path)) &&
            manifest.ChangedPaths is not null && manifest.ChangedPaths.All(changed =>
                audit.ReviewedPaths.Contains(changed, StringComparer.Ordinal)) &&
            audit.Layers is { Count: 3 } && new[] { "secrets", "dependencies", "model" }.All(name =>
                audit.Layers.Count(layer => layer is not null && layer.Name == name && layer.State == ArtifactAuditState.Pass &&
                    !string.IsNullOrWhiteSpace(layer.Tool) && !string.IsNullOrWhiteSpace(layer.ToolVersion) &&
                    !string.IsNullOrWhiteSpace(layer.DataVersion) && layer.CompletedAtUtc != default &&
                    layer.CompletedAtUtc <= audit.CompletedAtUtc) == 1) &&
            string.Equals(audit.ArtifactId, manifest.ArtifactId, StringComparison.Ordinal) &&
            string.Equals(audit.Commit, manifest.HeadCommit, StringComparison.Ordinal) &&
            string.Equals(audit.ManifestHash, receipt.ManifestHash, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(audit.Scanner) && !string.IsNullOrWhiteSpace(audit.ScannerVersion) &&
            audit.CompletedAtUtc != default && receipt.CompletedAtUtc >= audit.CompletedAtUtc;
    }
}

// Persisted receipt hashes depend on the default JSON encoding and manifest property order.
[JsonSerializable(typeof(ForgeArtifactManifest))]
internal sealed partial class ArtifactManifestJsonContext : JsonSerializerContext;
