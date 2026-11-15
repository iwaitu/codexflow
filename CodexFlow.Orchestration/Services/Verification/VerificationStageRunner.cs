// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts;
using CodexFlow.Core.Execution;

namespace CodexFlow.Services.Verification;

/// <summary>
/// Runs the stages of a project verification in the isolated snapshot and keeps the receipt up to date: every command
/// leaves evidence (arguments, exit code, status, a hash of its output, timing, the image it ran in). Shared by all
/// language verifiers so their evidence looks the same.
/// </summary>
public sealed class VerificationStageRunner
{
    private readonly string _jobId;
    private readonly ILogger? _logger;
    private readonly List<ArtifactCommandEvidence> _commands = [];

    public VerificationStageRunner(IIsolatedExecutionSession session, ForgeArtifactVerificationReceipt receipt, string jobId, ILogger? logger)
    {
        Session = session;
        Receipt = receipt;
        _jobId = jobId;
        _logger = logger;
    }

    /// <summary>The immutable snapshot the verification runs in.</summary>
    public IIsolatedExecutionSession Session { get; }

    /// <summary>The receipt as far as the verification has got.</summary>
    public ForgeArtifactVerificationReceipt Receipt { get; set; }

    /// <summary>Whether a command ran to completion with exit code 0 and untruncated output.</summary>
    public static bool Succeeded(IsolatedCommandResult result) =>
        result.Decision.IsAllowed && result.Status == IsolatedCommandStatus.Completed && result.ExitCode == 0 && !result.Truncated;

    /// <summary>Runs a command as a named stage and records its evidence in the receipt.</summary>
    public async Task<IsolatedCommandResult> RunAsync(string stage, string[] args, CancellationToken ct)
    {
        var result = await Session.RunAsync(new(args), ct);
        var imageId = result.Evidence?.Split(';', StringSplitOptions.TrimEntries)
            .FirstOrDefault(part => part.StartsWith("image=sha256:", StringComparison.Ordinal))?[6..] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(Receipt.VerificationImageId))
        {
            Receipt = Receipt with { VerificationImageId = imageId };
        }

        _commands.Add(new(stage, args, result.ExitCode, result.Status.ToString(),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(result.Stdout + "\0" + result.Stderr))),
            (long)result.Elapsed.TotalMilliseconds) { IsolationEvidence = result.Evidence ?? string.Empty });
        Receipt = Receipt with { Commands = _commands.ToArray() };

        if (!Succeeded(result))
        {
            // The receipt keeps only a hash of the output; without this the reason a restore/build/test failed is unrecoverable.
            var output = (result.Stdout + "\n" + result.Stderr).Trim();
            _logger?.LogWarning("Verification stage {Stage} failed (exit {ExitCode}, {Status}) for job {JobId}. Output tail: {Output}",
                stage, result.ExitCode, result.Status, _jobId, output.Length > 1500 ? output[^1500..] : output);
        }

        return result;
    }
}
