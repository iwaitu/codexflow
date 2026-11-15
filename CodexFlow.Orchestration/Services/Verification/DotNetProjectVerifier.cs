// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Xml.Linq;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Services;

namespace CodexFlow.Services.Verification;

/// <summary>
/// .NET acceptance: restore, build and test every project of the snapshot through a generated solution, and accept the
/// tests only when the TRX evidence shows every executed test passed. This is the verification that used to be written
/// directly into <c>ForgeArtifactVerificationService</c>; its behaviour is unchanged.
/// </summary>
public sealed class DotNetProjectVerifier : IProjectVerifier
{
    private static readonly string[] ProjectGlobs = ["**/*.csproj", "**/*.fsproj", "**/*.vbproj"];

    public string Language => "dotnet";

    public async Task<bool> DetectAsync(IIsolatedExecutionSession snapshot, CancellationToken ct)
        => (await snapshot.ReadEvidenceAsync(ProjectGlobs, ct)).Count > 0;

    public async Task<string?> VerifyAsync(VerificationStageRunner runner, CancellationToken ct)
    {
        var isolated = runner.Session;

        // Enumerate the immutable snapshot, not a repository-selected solution that may omit the changed project.
        var projects = (await isolated.ReadEvidenceAsync(ProjectGlobs, ct))
            .Select(x => x.RelativePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (projects.Length == 0 || projects.Length > 50 || projects.Any(path =>
            !WorkspacePath.TryNormalizeStrict(path, out _, out _) || WorkspacePath.IsProtected(path)))
            return "ArtifactProjectCoverageUnavailable";
        runner.Receipt = runner.Receipt with { VerificationProjects = projects };
        var solution = new XElement("Solution", projects.Select(path => new XElement("Project", new XAttribute("Path", path)))).ToString();
        var stageSolution = await isolated.RunAsync(new(["sh", "-c", "cat > .codex-verification.slnx"], StdIn: solution), ct);
        if (!VerificationStageRunner.Succeeded(stageSolution)) return "ArtifactProjectCoverageUnavailable";

        var restore = await runner.RunAsync("restore", ["dotnet", "restore", ".codex-verification.slnx"], ct);
        if (!VerificationStageRunner.Succeeded(restore))
        {
            runner.Receipt = runner.Receipt with { BuildExitCode = restore.ExitCode };
            return "ArtifactRestoreFailed";
        }

        var build = await runner.RunAsync("build", ["dotnet", "build", ".codex-verification.slnx", "--no-restore"], ct);
        runner.Receipt = runner.Receipt with { BuildPassed = VerificationStageRunner.Succeeded(build), BuildExitCode = build.ExitCode };
        if (!runner.Receipt.BuildPassed) return "ArtifactBuildFailed";

        var cleanEvidence = await isolated.RunAsync(new(["rm", "-rf", "--", ".codex-verification-results"]), ct);
        if (!VerificationStageRunner.Succeeded(cleanEvidence)) return "ArtifactEvidenceCleanupFailed";

        var tests = await runner.RunAsync("test", ["dotnet", "test", ".codex-verification.slnx", "--no-build", "--no-restore", "--logger", "trx", "--results-directory", ".codex-verification-results"], ct);
        runner.Receipt = runner.Receipt with { TestExitCode = tests.ExitCode };
        if (!VerificationStageRunner.Succeeded(tests)) return "ArtifactTestsFailed";

        // Evidence is read from the private snapshot; no source changes are exported.
        var trx = await isolated.ReadEvidenceAsync([".codex-verification-results/*.trx"], ct);
        var count = 0;
        foreach (var file in trx)
        {
            using var stream = new MemoryStream(file.Content.ToArray(), writable: false);
            using var reader = System.Xml.XmlReader.Create(stream, new() { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            var counters = document.Descendants().SingleOrDefault(x => x.Name.LocalName == "Counters");
            var summary = document.Descendants().SingleOrDefault(x => x.Name.LocalName == "ResultSummary");
            var results = document.Descendants().Where(x => x.Name.LocalName == "UnitTestResult").ToArray();
            if (counters is null || !int.TryParse(counters.Attribute("total")?.Value, out var total) ||
                !int.TryParse(counters.Attribute("passed")?.Value, out var passed) || total != passed || total <= 0 ||
                !int.TryParse(counters.Attribute("executed")?.Value, out var executed) || executed != total ||
                summary?.Attribute("outcome")?.Value is not ("Completed" or "Passed") || results.Length != total ||
                results.Any(x => x.Attribute("outcome")?.Value != "Passed" || string.IsNullOrWhiteSpace(x.Attribute("executionId")?.Value)) ||
                results.Select(x => x.Attribute("executionId")!.Value).Distinct(StringComparer.Ordinal).Count() != total)
                return "ArtifactTestEvidenceInvalid";
            count = checked(count + total);
        }

        runner.Receipt = runner.Receipt with
        {
            TestCount = count,
            TestEvidenceHashes = trx.Select(x => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(x.Content.Span))).ToArray()
        };
        if (count == 0) return "ArtifactTestEvidenceMissing";
        runner.Receipt = runner.Receipt with { TestsPassed = true };
        return null;
    }
}
