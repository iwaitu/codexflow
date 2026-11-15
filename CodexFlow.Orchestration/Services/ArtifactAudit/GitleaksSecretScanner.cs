// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma warning disable CS1591 // internal audit surface; documented in .committee-review/claude-audit-api.md
using System.Globalization;
using System.Text.Json;
using CodexFlow.Contracts;
using CodexFlow.Core.Execution;

namespace CodexFlow.Services.ArtifactAudit;

/// <summary>
/// Secret scan of the fresh immutable snapshot, executed by gitleaks inside the isolated guest.
/// Trust model: rule config and (empty) ignore file are image-owned paths outside the workspace; project
/// <c>.gitleaksignore</c>, <c>.gitleaks.toml</c>, inline <c>gitleaks:allow</c> and env config can never relax the scan.
/// Only rule id / file / line are read from the report; the matched secret text is never parsed or retained.
/// </summary>
public sealed class GitleaksSecretScanner(ArtifactAuditOptions options)
{
    public const string ToolName = "gitleaks";
    private const string Layer = "secrets";

    public async Task<AuditLayerOutcome> ScanAsync(IIsolatedExecutionSession session, CancellationToken ct)
    {
        var version = await session.RunAsync(new(["gitleaks", "version"], Timeout: TimeSpan.FromSeconds(30)), ct).ConfigureAwait(false);
        var versionText = AuditText.Sanitize(version.Stdout, 40);
        if (!AuditText.CommandSucceeded(version) || versionText.Length == 0)
            return Outcome(ArtifactAuditState.Error, ArtifactAuditReasons.ToolUnavailable, $"gitleaks is not runnable in the verifier image ({Describe(version)}).", versionText, string.Empty);
        if (options.ExpectedGitleaksVersion is { Length: > 0 } expected &&
            !string.Equals(versionText.TrimStart('v'), expected.TrimStart('v'), StringComparison.Ordinal))
            return Outcome(ArtifactAuditState.Error, ArtifactAuditReasons.ToolConfigUntrusted, $"gitleaks version '{versionText}' differs from the pinned '{expected}'.", versionText, string.Empty);

        // Trusted configuration is image-owned and must not drift: record its hash and require the ignore list to be empty.
        var hash = await session.RunAsync(new(["sha256sum", "--", options.GitleaksConfigPath], Timeout: TimeSpan.FromSeconds(30)), ct).ConfigureAwait(false);
        var configHash = AuditText.CommandSucceeded(hash) ? hash.Stdout.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty : string.Empty;
        if (configHash.Length != 64 || !configHash.All(Uri.IsHexDigit))
            return Outcome(ArtifactAuditState.Error, ArtifactAuditReasons.ToolConfigUntrusted, "Trusted gitleaks configuration is missing or unreadable.", versionText, string.Empty);
        configHash = configHash.ToLowerInvariant();
        if (options.ExpectedGitleaksConfigSha256 is { Length: > 0 } pinned && !string.Equals(pinned, configHash, StringComparison.OrdinalIgnoreCase))
            return Outcome(ArtifactAuditState.Error, ArtifactAuditReasons.ToolConfigUntrusted, "Trusted gitleaks configuration hash does not match the pinned value.", versionText, "config:" + configHash[..16]);
        var ignoreSize = await session.RunAsync(new(["stat", "-c", "%s", "--", options.GitleaksIgnorePath], Timeout: TimeSpan.FromSeconds(30)), ct).ConfigureAwait(false);
        if (!AuditText.CommandSucceeded(ignoreSize) || ignoreSize.Stdout.Trim() != "0")
            return Outcome(ArtifactAuditState.Error, ArtifactAuditReasons.ToolConfigUntrusted, "Trusted gitleaks ignore file is missing or not empty.", versionText, "config:" + configHash[..16]);
        var dataVersion = "config:" + configHash[..16];

        // gitleaks ALSO auto-loads <source>/.gitleaksignore despite --gitleaks-ignore-path (verified against 8.30.1): disarm every
        // project ignore file by renaming it (it is still scanned as ordinary content). Unique name => nothing is overwritten.
        var disabledName = "gitleaksignore-disabled-" + Guid.NewGuid().ToString("N");
        var disarm = await session.RunAsync(new(["find", ".", "-name", ".gitleaksignore", "-type", "f", "-execdir", "mv", "-n", "--", ".gitleaksignore", disabledName, ";"],
            Timeout: TimeSpan.FromSeconds(60)), ct).ConfigureAwait(false);
        if (!AuditText.CommandSucceeded(disarm))
            return Outcome(ArtifactAuditState.Error, ArtifactAuditReasons.ToolConfigUntrusted, "Project .gitleaksignore files could not be neutralised.", versionText, dataVersion);
        var leftover = await session.RunAsync(new(["find", ".", "-name", ".gitleaksignore"], Timeout: TimeSpan.FromSeconds(60)), ct).ConfigureAwait(false);
        if (!AuditText.CommandSucceeded(leftover) || leftover.Stdout.Trim().Length != 0)
            return Outcome(ArtifactAuditState.Error, ArtifactAuditReasons.ToolConfigUntrusted, "A project .gitleaksignore is still present.", versionText, dataVersion);

        var scan = await session.RunAsync(new(
        [
            "gitleaks", "dir", ".", "--no-banner", "--redact=100",
            "--report-format", "json", "--report-path", "-",
            "--exit-code", "2",
            "--config", options.GitleaksConfigPath,
            "--gitleaks-ignore-path", options.GitleaksIgnorePath,
            "--ignore-gitleaks-allow",
            "--max-decode-depth", options.GitleaksMaxDecodeDepth.ToString(CultureInfo.InvariantCulture),
            "--log-level", "error"
        ], Timeout: options.SecretScanTimeout), ct).ConfigureAwait(false);

        if (!scan.Decision.IsAllowed || scan.Status is not (IsolatedCommandStatus.Completed or IsolatedCommandStatus.Failed))
            return Outcome(ArtifactAuditState.Error, ArtifactAuditReasons.ScanFailed, $"Secret scan did not run to completion ({Describe(scan)}).", versionText, dataVersion);

        switch (scan.ExitCode)
        {
            case 0:
            {
                if (scan.Truncated) return Outcome(ArtifactAuditState.Error, ArtifactAuditReasons.MalformedOutput, "Secret scan output was truncated.", versionText, dataVersion);
                if (!GitleaksReportParser.TryParse(scan.Stdout, options.MaxReportedFindings, out var clean, out _) || clean.Count != 0)
                    return Outcome(ArtifactAuditState.Error, ArtifactAuditReasons.MalformedOutput, "Secret scan exit code and report disagree.", versionText, dataVersion);
                return Outcome(ArtifactAuditState.Pass, ArtifactAuditReasons.Clean, "No secrets found by the trusted rule set.", versionText, dataVersion);
            }
            case 2:
            {
                var parsed = !scan.Truncated && GitleaksReportParser.TryParse(scan.Stdout, options.MaxReportedFindings, out var found, out var omitted)
                    ? (Findings: found, Omitted: omitted) : (Findings: [], Omitted: 0);
                var findings = parsed.Findings.ToList();
                if (findings.Count == 0)
                    findings.Add(new ArtifactAuditFinding(AuditText.Fingerprint(Layer, "unparsed"), "High", "(snapshot)", "gitleaks reported secrets but the details could not be parsed or were truncated (value never retained).")
                        { Layer = Layer, Category = "Secret" });
                if (parsed.Omitted > 0)
                    findings.Add(new ArtifactAuditFinding(AuditText.Fingerprint(Layer, "omitted", parsed.Omitted.ToString(CultureInfo.InvariantCulture)), "High", "(snapshot)", $"{parsed.Omitted} further secret findings omitted from this report.")
                        { Layer = Layer, Category = "Secret" });
                return new AuditLayerOutcome(Layer, ArtifactAuditState.Fail, ArtifactAuditReasons.SecretsFound,
                    $"{findings.Count} secret finding(s) (values redacted).", ToolName, versionText, dataVersion, findings);
            }
            default:
                return Outcome(ArtifactAuditState.Error, ArtifactAuditReasons.ScanFailed, $"gitleaks exited with code {scan.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"}.", versionText, dataVersion);
        }
    }

    private static string Describe(IsolatedCommandResult r) =>
        $"{r.Status}/{r.Decision.ReasonCode}/exit {r.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    private static AuditLayerOutcome Outcome(ArtifactAuditState state, string reason, string message, string tool, string data) =>
        new(Layer, state, reason, message, ToolName, tool, data, []);
}

/// <summary>Strict, bounded parser for gitleaks JSON reports. Never reads <c>Secret</c>/<c>Match</c>.</summary>
public static class GitleaksReportParser
{
    public static bool TryParse(string json, int maxFindings, out List<ArtifactAuditFinding> findings, out int omitted)
    {
        findings = [];
        omitted = 0;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("RuleID", out var rule) || rule.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("File", out var file) || file.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("StartLine", out var line) || line.ValueKind != JsonValueKind.Number || !line.TryGetInt32(out var lineNo))
                    return false;
                if (findings.Count >= maxFindings) { omitted++; continue; }
                var ruleId = AuditText.Sanitize(rule.GetString(), 80);
                var path = AuditText.Sanitize(file.GetString(), 300);
                if (ruleId.Length == 0 || path.Length == 0) return false;
                findings.Add(new ArtifactAuditFinding(
                    AuditText.Fingerprint("secret", ruleId, path, lineNo.ToString(CultureInfo.InvariantCulture)),
                    "High", $"{path}:{lineNo.ToString(CultureInfo.InvariantCulture)}",
                    $"gitleaks rule '{ruleId}' matched (value redacted, not retained).")
                { Layer = "secrets", Category = "Secret" });
            }
            return true;
        }
        catch (JsonException)
        {
            findings = [];
            return false;
        }
    }
}
