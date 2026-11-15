// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution;

namespace CodexFlow.Services.Verification;

/// <summary>
/// Python acceptance for a project that tests with pytest: build a virtualenv in the tmpfs from the offline wheelhouse (no
/// network), byte-compile everything as the build step, run pytest with a JUnit report, and accept the tests only when that
/// report proves a clean run (<see cref="TestEvidence.TryAcceptJUnit"/>).
/// </summary>
/// <remarks>
/// The restore can only use the wheels mounted at <c>/pip-wheels</c> (<c>Isolation:PipWheelhouseHostPath</c>), which must
/// include pytest and the project's dependencies; a missing wheel makes the restore fail, never reach out.
/// </remarks>
public sealed class PythonProjectVerifier : IProjectVerifier
{
    private const string ResultsDirectory = ".codex-verification-results";
    private const string JUnitReport = ResultsDirectory + "/junit.xml";
    private static readonly string[] MarkerFiles = ["pyproject.toml", "requirements.txt", "setup.py", "setup.cfg"];

    public string Language => "python";

    public string? ImageConfigurationKey => "ArtifactVerification:PythonImage";

    public async Task<bool> DetectAsync(IIsolatedExecutionSession snapshot, CancellationToken ct)
        => (await snapshot.ReadEvidenceAsync(MarkerFiles, ct)).Count > 0;

    public async Task<string?> VerifyAsync(VerificationStageRunner runner, CancellationToken ct)
    {
        var isolated = runner.Session;
        var markers = (await isolated.ReadEvidenceAsync(MarkerFiles, ct)).Select(x => x.RelativePath).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (markers.Length == 0) return "ArtifactProjectCoverageUnavailable";
        runner.Receipt = runner.Receipt with { VerificationProjects = markers };

        const string pip = "/tmp/venv/bin/pip install --no-index --find-links /pip-wheels";
        var restore = await runner.RunAsync("restore", ["sh", "-c",
            "python -m venv /tmp/venv && " + pip + " pytest && " +
            "if [ -f requirements.txt ]; then " + pip + " -r requirements.txt; else " + pip + " .; fi"], ct);
        if (!VerificationStageRunner.Succeeded(restore))
        {
            runner.Receipt = runner.Receipt with { BuildExitCode = restore.ExitCode };
            return "ArtifactRestoreFailed";
        }

        // Python has no build; compiling every file is the cheapest check that the sources at least parse.
        var build = await runner.RunAsync("build", ["/tmp/venv/bin/python", "-m", "compileall", "-q", "."], ct);
        runner.Receipt = runner.Receipt with { BuildPassed = VerificationStageRunner.Succeeded(build), BuildExitCode = build.ExitCode };
        if (!runner.Receipt.BuildPassed) return "ArtifactBuildFailed";

        var cleanEvidence = await isolated.RunAsync(new(["rm", "-rf", "--", ResultsDirectory]), ct);
        if (!VerificationStageRunner.Succeeded(cleanEvidence)) return "ArtifactEvidenceCleanupFailed";

        var tests = await runner.RunAsync("test", ["/tmp/venv/bin/python", "-m", "pytest", "-q", "-p", "no:cacheprovider", "--junitxml=" + JUnitReport], ct);
        runner.Receipt = runner.Receipt with { TestExitCode = tests.ExitCode };
        if (!VerificationStageRunner.Succeeded(tests)) return "ArtifactTestsFailed";

        var report = await isolated.ReadEvidenceAsync([JUnitReport], ct);
        if (report.Count != 1) return "ArtifactTestEvidenceMissing";
        if (!TestEvidence.TryAcceptJUnit(report[0].Content.Span, out var count)) return "ArtifactTestEvidenceInvalid";

        runner.Receipt = runner.Receipt with
        {
            TestCount = count,
            TestEvidenceHashes = [Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(report[0].Content.Span))],
            TestsPassed = true
        };
        return null;
    }
}
