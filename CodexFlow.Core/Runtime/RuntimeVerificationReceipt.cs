// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;

namespace CodexFlow.Core.Runtime;

/// <summary>A terminal tool receipt for this attempt. It is diagnostic until bound to a plan check and candidate.</summary>
public sealed record RuntimeVerificationReceipt(
    string Kind, string Outcome, int? ExitCode, string? Reason = null, int? Passed = null, int? Failed = null)
{
    private static readonly Regex NodeBuild = new(
        @"^\$[ \t]*(?:npm|pnpm|yarn)[ \t]+(?:run[ \t]+)?build(?:[ \t]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static RuntimeVerificationReceipt? Read(ToolExecutionResult result, ToolCommandObservation? command)
    {
        var metadata = ToolObservationMetadata.From(result.Metadata);
        var exitCode = metadata.GetInt("ExitCode", "exit_code");
        var preparationFailed = metadata.IsTrue("DependencyPreparationFailed");
        if (string.Equals(result.ToolName, "run_tests", StringComparison.OrdinalIgnoreCase))
        {
            var test = metadata.GetObject("testResult");
            if (!test.IsReadable)
            {
                return null;
            }

            var passed = test.GetInt("PassedCount");
            var failed = test.GetInt("FailedCount");
            if (passed is null or < 0 || failed is null or < 0)
            {
                return null;
            }

            if (failed > 0)
            {
                return new("run_tests", "failed", exitCode, "assertions_failed", passed, failed);
            }

            if (result.Success && test.IsTrue("Success") && exitCode == 0 && passed > 0)
            {
                return new("run_tests", "passed", exitCode, Passed: passed, Failed: failed);
            }

            return passed == 0
                ? new("run_tests", "not_run", exitCode,
                    preparationFailed ? "dependency_preparation_failed" : "no_assertions_observed")
                : new("run_tests", "failed", exitCode, "test_process_failed", passed, failed);
        }

        if (command is not { } run || !metadata.IsReadable ||
            (run.Verification?.Kind ?? (NodeBuild.IsMatch(run.CommandLine) ? "node_build" : null)) is not { } kind)
        {
            return null;
        }

        if (metadata.IsTrue("ScopedExportRejected"))
        {
            return new(kind, "not_run", exitCode, "scoped_export_rejected");
        }

        return run.Outcome switch
        {
            ToolCommandOutcome.Passed => new(kind, "passed", exitCode),
            ToolCommandOutcome.Failed when preparationFailed => new(kind, "not_run", exitCode, "dependency_preparation_failed"),
            ToolCommandOutcome.Failed => new(kind, "failed", exitCode, "command_failed"),
            _ => null
        };
    }
}
