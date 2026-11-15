// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution;

namespace CodexFlow.Services.Verification;

/// <summary>
/// The language-specific part of host acceptance: recognise a project in the immutable snapshot, then restore,
/// build and test it and turn the result into evidence on the receipt. Everything that does not depend on the
/// language (identity, plan and ownership checks, the independent security audit, the quality gate) stays in
/// <see cref="CodexFlow.Services.Background.ForgeArtifactVerificationService"/>.
/// </summary>
public interface IProjectVerifier
{
    /// <summary>Short name of the language or toolchain, for logs and evidence ("dotnet", "node", "python").</summary>
    string Language { get; }

    /// <summary>
    /// Configuration key of the container image this language verifies in, or <see langword="null"/> to use the default
    /// <c>ArtifactVerification:Image</c>. When the key is set and names a different image, the host opens a new snapshot in it.
    /// </summary>
    string? ImageConfigurationKey => null;

    /// <summary>Whether this verifier handles the project in the snapshot.</summary>
    Task<bool> DetectAsync(IIsolatedExecutionSession snapshot, CancellationToken ct);

    /// <summary>
    /// Restores, builds and tests the project. Evidence goes onto <see cref="VerificationStageRunner.Receipt"/> as it
    /// is produced; the return value is a failure code, or <see langword="null"/> when build and tests passed.
    /// </summary>
    Task<string?> VerifyAsync(VerificationStageRunner runner, CancellationToken ct);
}

/// <summary>Picks the verifier for a snapshot.</summary>
public static class ProjectVerifierSelector
{
    /// <summary>
    /// Returns the first verifier, in registration order, that recognises the project; <see langword="null"/> when
    /// none does. A repository with several languages is therefore verified as the first language that matches.
    /// </summary>
    public static async Task<IProjectVerifier?> SelectAsync(
        IEnumerable<IProjectVerifier> verifiers, IIsolatedExecutionSession snapshot, CancellationToken ct)
    {
        foreach (var verifier in verifiers)
        {
            if (await verifier.DetectAsync(snapshot, ct))
            {
                return verifier;
            }
        }

        return null;
    }
}
