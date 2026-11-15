// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution;

namespace CodexFlow.Services.Verification;

/// <summary>
/// Node acceptance for a project with a root <c>package.json</c> and <c>package-lock.json</c> that tests with Jest: install from
/// the offline npm cache (no network, lifecycle scripts off), run the optional <c>build</c> script, run Jest with a JSON report,
/// and accept the tests only when that report proves a clean run (<see cref="TestEvidence.TryAcceptJest"/>).
/// </summary>
/// <remarks>
/// The container has no network, so the restore can only use the cache mounted at <c>/npm-cache</c>
/// (<c>Isolation:NpmCacheHostPath</c>); a missing package makes the restore fail, never reach out.
/// </remarks>
public sealed class NodeProjectVerifier : IProjectVerifier
{
    private const string ResultsDirectory = ".codex-verification-results";
    private const string JestReport = ResultsDirectory + "/jest.json";

    public string Language => "node";

    public string? ImageConfigurationKey => "ArtifactVerification:NodeImage";

    public async Task<bool> DetectAsync(IIsolatedExecutionSession snapshot, CancellationToken ct)
        => (await snapshot.ReadEvidenceAsync(["package.json"], ct)).Count > 0;

    public async Task<string?> VerifyAsync(VerificationStageRunner runner, CancellationToken ct)
    {
        var isolated = runner.Session;

        // npm ci needs a lockfile: it installs exactly what is locked, which is what makes the restore reproducible.
        var lockfile = await isolated.ReadEvidenceAsync(["package-lock.json"], ct);
        if (lockfile.Count == 0) return "ArtifactRestoreFailed";
        runner.Receipt = runner.Receipt with { VerificationProjects = ["package.json"] };

        // The cache mount is read-only and npm writes to its cache, so it works on a copy in the tmpfs.
        var restore = await runner.RunAsync("restore", ["sh", "-c",
            "cp -r /npm-cache /tmp/npm-cache && npm ci --offline --ignore-scripts --no-audit --no-fund --cache /tmp/npm-cache"], ct);
        if (!VerificationStageRunner.Succeeded(restore))
        {
            runner.Receipt = runner.Receipt with { BuildExitCode = restore.ExitCode };
            return "ArtifactRestoreFailed";
        }

        var build = await runner.RunAsync("build", ["npm", "run", "build", "--if-present"], ct);
        runner.Receipt = runner.Receipt with { BuildPassed = VerificationStageRunner.Succeeded(build), BuildExitCode = build.ExitCode };
        if (!runner.Receipt.BuildPassed) return "ArtifactBuildFailed";

        var cleanEvidence = await isolated.RunAsync(new(["rm", "-rf", "--", ResultsDirectory]), ct);
        if (!VerificationStageRunner.Succeeded(cleanEvidence)) return "ArtifactEvidenceCleanupFailed";

        // Jest writes its report with a plain file write and does not create the directory (found in a real container).
        var prepareEvidence = await isolated.RunAsync(new(["mkdir", "-p", "--", ResultsDirectory]), ct);
        if (!VerificationStageRunner.Succeeded(prepareEvidence)) return "ArtifactEvidenceCleanupFailed";

        var tests = await runner.RunAsync("test", ["npx", "--no-install", "jest", "--ci", "--json", "--outputFile=" + JestReport], ct);
        runner.Receipt = runner.Receipt with { TestExitCode = tests.ExitCode };
        if (!VerificationStageRunner.Succeeded(tests)) return "ArtifactTestsFailed";

        var report = await isolated.ReadEvidenceAsync([JestReport], ct);
        if (report.Count != 1) return "ArtifactTestEvidenceMissing";
        if (!TestEvidence.TryAcceptJest(report[0].Content.Span, out var count)) return "ArtifactTestEvidenceInvalid";

        runner.Receipt = runner.Receipt with
        {
            TestCount = count,
            TestEvidenceHashes = [Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(report[0].Content.Span))],
            TestsPassed = true
        };
        return null;
    }
}
