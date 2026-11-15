// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma warning disable CS1591 // internal audit surface; documented in .committee-review/claude-audit-api.md
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodexFlow.Contracts;
using CodexFlow.Core.Execution;

namespace CodexFlow.Services.ArtifactAudit;

/// <summary>Stable machine-readable reasons (also used as <see cref="ArtifactAuditLayerResult.ReasonCode"/>).</summary>
public static class ArtifactAuditReasons
{
    public const string Clean = "Clean";
    public const string InvalidInput = "InvalidAuditInput";
    public const string ManifestHashMismatch = "ManifestHashMismatch";
    public const string SnapshotNotFresh = "SnapshotNotFresh";
    public const string SnapshotInventoryFailed = "SnapshotInventoryFailed";
    public const string ToolUnavailable = "ToolUnavailable";
    public const string ToolConfigUntrusted = "ToolConfigUntrusted";
    public const string ScanFailed = "ScanFailed";
    public const string MalformedOutput = "MalformedOutput";
    public const string SecretsFound = "SecretsFound";
    public const string ManifestChanged = "ManifestChangedDuringAudit";
    public const string RestoreFailed = "RestoreFailed";
    public const string CoverageIncomplete = "CoverageIncomplete";
    public const string UnsupportedEcosystem = "UnsupportedEcosystem";
    public const string NonStandardPackageSource = "NonStandardPackageSource";
    public const string AdvisoryUnavailable = "AdvisoryDataUnavailable";
    public const string AdvisoryStale = "AdvisoryDataStale";
    public const string AdvisoryInvalid = "AdvisoryDataInvalid";
    public const string VulnerableHigh = "VulnerableDependencyHighOrCritical";
    public const string VulnerableLow = "VulnerableDependencyLowOrModerate";
    public const string BaselineInvalid = "DependencyBaselineInvalid";
    public const string ModelNotConfigured = "ModelNotConfigured";
    public const string ModelSkipped = "ModelSkippedDeterministicLayerNotPass";
    public const string ModelError = "ModelCallFailed";
    public const string ModelPartialContext = "ModelPartialContext";
    public const string ModelNeedsReview = "ModelNeedsReview";
    public const string ModelUnboundEvidence = "ModelEvidenceNotBound";
    public const string ModelFindings = "ModelFindings";
}

public sealed record AuditLayerOutcome(
    string Name,
    ArtifactAuditState State,
    string ReasonCode,
    string Message,
    string Tool,
    string ToolVersion,
    string DataVersion,
    IReadOnlyList<ArtifactAuditFinding> Findings)
{
    /// <summary>Findings a reviewed baseline accepted; kept out of <see cref="Findings"/> so a Pass never carries findings.</summary>
    public IReadOnlyList<ArtifactAuditFinding> BaselineDebt { get; init; } = [];
    public IReadOnlyList<ArtifactDependencyRemediation> Remediation { get; init; } = [];

    public ArtifactAuditLayerResult ToLayerResult(DateTimeOffset completedAt) => new()
    {
        Name = Name, State = State, ReasonCode = ReasonCode, Message = AuditText.Limit(Message, 600), Tool = Tool,
        ToolVersion = ToolVersion, DataVersion = DataVersion, CompletedAtUtc = completedAt
    };
}

public static class AuditText
{
    private static readonly Regex TokenLike = new(@"[A-Za-z0-9+/=_\-]{24,}", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex PrivateKeyBlock = new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Limit(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>Removes control characters (log/terminal injection), collapses whitespace, bounds the length.</summary>
    public static string Sanitize(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        var lastSpace = false;
        foreach (var ch in text)
        {
            if (sb.Length >= max) { sb.Append('…'); break; }
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                if (!lastSpace && sb.Length > 0) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            lastSpace = false;
            sb.Append(ch);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Best-effort scrub so retained evidence never keeps raw secrets: long token-like runs and key blocks are masked.</summary>
    public static string Redact(string? text, int max)
    {
        var clean = Sanitize(text, Math.Max(max * 2, 64));
        try
        {
            clean = PrivateKeyBlock.Replace(clean, "[REDACTED-KEY-BLOCK]");
            var source = clean;
            clean = TokenLike.Replace(source, match => IsReadableName(match.Value) || IsCommitIdInContext(source, match) ? match.Value : "[REDACTED]");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTED-UNPARSEABLE]";
        }
        return Limit(clean, max);
    }

    /// <summary>
    /// "/" is part of the base64 alphabet, so a long run of path segments matches the token pattern. A run made only of letters, "/", "_" and "-" with
    /// at least one "/" is a path, not key material: real keys carry digits, "+" or "=" (or have no separator at all and stay masked).
    /// </summary>
    // Long run that is readable text rather than key material: every part between "/", "_" and "-" is a word (PascalCase/camelCase/lowercase
    // letters, optionally a few trailing digits) or a plain number. Paths with version/year segments, long type names, kebab/snake slugs
    // qualify; random base64 or hex does not (digits and case changes fall in the middle of its parts). Passphrase-like words would also pass:
    // this scrub is best-effort evidence hygiene, the secrets layer of the audit is the real gate.
    internal static bool IsReadableName(string run)
    {
        if (run.Contains('+') || run.Contains('='))
        {
            return false;
        }

        var parts = run.Split(['/', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var words = 0;
        foreach (var part in parts)
        {
            if (part.All(char.IsDigit))
            {
                if (part.Length > 8) return false;
                continue;
            }

            var letters = part.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (part.Length - letters.Length > 4) return false;
            // Short parts ("V", "of", "src") are connectors, not key material.
            if (letters.Length > 3 && !IsWordSequence(letters)) return false;
            if (letters.Length > 0 && !letters.All(static c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')) return false;
            words++;
        }

        return words > 0;
    }

    private static readonly Regex WordPiece = new(@"[A-Z][a-z]+|[a-z]+|[A-Z]+(?![a-z])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // Letters only, and the case changes fall on word boundaries: at least 80% of the letters sit in pieces of 3+ characters ("OrderHandler",
    // "HTTPClient"), while random mixed case breaks into one- and two-letter pieces.
    private static bool IsWordSequence(string letters)
    {
        if (letters.Length == 0 || !letters.All(static c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'))
        {
            return false;
        }

        var inWords = 0;
        foreach (Match piece in WordPiece.Matches(letters))
        {
            if (piece.Length > 16) return false; // no real word is this long; a run of one letter repeated or random lowercase is not text
            if (piece.Length >= 3) inWords += piece.Length;
        }

        return inWords * 10 >= letters.Length * 8;
    }

    private static readonly Regex CommitContext = new(@"(commit|head|sha|base|merge|parent|rev|@)\W{0,3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // A full commit id names a revision, not a secret, but only when the text around it says so; a bare 40/64-hex string stays masked.
    private static bool IsCommitIdInContext(string text, Match match)
    {
        // "HEAD=<id>": the pattern includes '=', so the id starts after a short label.
        var value = match.Value;
        var start = match.Index;
        var eq = value.IndexOf('=');
        if (eq is > 0 and <= 12 && value[..eq].All(char.IsLetter))
        {
            value = value[(eq + 1)..];
            start += eq + 1;
        }

        if (!IsFullCommitId(value))
        {
            return false;
        }

        var before = text[Math.Max(0, start - 24)..start];
        try
        {
            return CommitContext.IsMatch(before);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static string Fingerprint(params string[] parts) => Sha256Hex(string.Join('\u001f', parts))[..32];

    public static bool IsFullCommitId(string? value) =>
        value is { Length: 40 or 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool CommandSucceeded(IsolatedCommandResult r) =>
        r.Decision.IsAllowed && r.Status == IsolatedCommandStatus.Completed && r.ExitCode == 0 && !r.Truncated;

    /// <summary>Normalizes a repository-relative path; null for rooted, traversing, backslash/NUL/control or empty paths.</summary>
    public static string? NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        if (p.Length == 0 || p.Length > 400 || p[0] == '/' || p.Contains('\\') || p.Any(char.IsControl) || p.Contains(':')) return null;
        var segs = p.Split('/');
        if (segs.Any(s => s.Length == 0 || s is "." or ".." || s.EndsWith('.') || s.EndsWith(' '))) return null;
        return p;
    }
}

public static class AuditStatePolicy
{
    /// <summary>Fail dominates (a real risk exists), then Error, NeedsReview, NotRun; only all-Pass is Pass.</summary>
    public static ArtifactAuditState Combine(IEnumerable<ArtifactAuditState> states)
    {
        var list = states.ToList();
        if (list.Count == 0) return ArtifactAuditState.NotRun;
        if (list.Contains(ArtifactAuditState.Fail)) return ArtifactAuditState.Fail;
        if (list.Contains(ArtifactAuditState.Error)) return ArtifactAuditState.Error;
        if (list.Contains(ArtifactAuditState.NeedsReview)) return ArtifactAuditState.NeedsReview;
        if (list.Contains(ArtifactAuditState.NotRun)) return ArtifactAuditState.NotRun;
        return list.All(s => s == ArtifactAuditState.Pass) ? ArtifactAuditState.Pass : ArtifactAuditState.Error;
    }

    public static string Describe(TimeSpan span) => span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s";
}
