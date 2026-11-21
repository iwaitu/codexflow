// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Runtime;

internal enum ToolCommandOutcome
{
    Unknown,
    Passed,
    Failed
}

/// <summary>Evidence from the command line and terminal receipt of one synchronous exec_cmd call.</summary>
internal readonly record struct ToolCommandObservation(
    string CommandLine,
    string? Output,
    ToolCommandOutcome Outcome)
{
    private const string CommandTaskIdPrefix = "command_task_id:";
    private const string SuccessReceiptPrefix = "✅ command_task_id:";
    private const string FailureReceiptPrefix = "⚠️ 命令执行完成";

    private static readonly Regex DotnetVerification = new(
        @"^\$[ \t]*dotnet[ \t]+(restore|build|test)(?:[ \t]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LegacyExitLine = new(
        @"^[ \t]*exit code[ \t]+(-?\d+)[ \t]*\r?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    public (string Kind, string Command)? Verification
    {
        get
        {
            var match = DotnetVerification.Match(CommandLine);
            if (match.Success)
            {
                var verb = match.Groups[1].Value.ToLowerInvariant();
                return ("dotnet_" + verb, "dotnet " + verb);
            }

            return TestCommandEvidence.Classify(CommandLine);
        }
    }

    public static ToolCommandObservation? Read(ToolExecutionResult result)
    {
        if (!string.Equals(result.ToolName, "exec_cmd", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var text = ReadReceiptText(result.Result, out var envelopeTruncated);
        var body = text.AsSpan().TrimStart();
        var first = TakeLine(ref body);
        var completed = first.StartsWith(SuccessReceiptPrefix, StringComparison.Ordinal);
        var failed = first.StartsWith(FailureReceiptPrefix, StringComparison.Ordinal);
        var commandLine = first;
        if (completed || failed)
        {
            commandLine = TakeLine(ref body);
            if (failed && commandLine.StartsWith(CommandTaskIdPrefix, StringComparison.Ordinal))
            {
                commandLine = TakeLine(ref body);
            }
        }

        // Inspect the echoed command line only. Neither a background start marker nor
        // a later dollar-prefixed line in stdout identifies a synchronous command.
        if (commandLine.Length < 2 || commandLine[0] != '$')
        {
            return null;
        }

        var metadata = ToolObservationMetadata.From(result.Metadata);
        if (!completed && !failed && body.IsWhiteSpace() &&
            metadata.GetInt("ExitCode", "exit_code") is null &&
            !IsTerminalStatus(metadata.GetString("Status")))
        {
            return null;
        }

        var output = result.IsOutputTruncated || envelopeTruncated || (metadata.IsProvided && !metadata.IsReadable)
            ? null
            : metadata.HasField("Stdout") ? metadata.GetString("Stdout") : body.ToString();
        var outcome = ReadOutcome(result.Success, completed, failed, output, metadata);
        return new ToolCommandObservation(commandLine.ToString(), output, outcome);
    }

    private static string? ReadReceiptText(string? result, out bool truncated)
    {
        truncated = false;
        if (!result.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return result;
        }

        try
        {
            // The AIFunction adapter carries the receipt inside the marked result
            // envelope. Only unwrap that envelope once; stdout can itself be JSON.
            using var document = JsonDocument.Parse(result!);
            var root = document.RootElement;
            if (!root.TryGetProperty("__icodex_tool_result", out var marker) || marker.ValueKind != JsonValueKind.True ||
                !(root.TryGetProperty("output", out var output) || root.TryGetProperty(nameof(CodexToolResult.Output), out output)) ||
                output.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            truncated = (root.TryGetProperty("isOutputTruncated", out var flag) ||
                         root.TryGetProperty(nameof(CodexToolResult.IsOutputTruncated), out flag)) &&
                        flag.ValueKind == JsonValueKind.True;
            return output.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ToolCommandOutcome ReadOutcome(
        bool toolSucceeded,
        bool completedReceipt,
        bool failedReceipt,
        string? output,
        ToolObservationMetadata metadata)
    {
        var exitCode = metadata.GetInt("ExitCode", "exit_code");
        var status = metadata.GetString("Status");
        if (failedReceipt || exitCode is not (null or 0) || IsFailedStatus(status))
        {
            return ToolCommandOutcome.Failed;
        }

        if (!toolSucceeded)
        {
            return ToolCommandOutcome.Unknown;
        }

        if (metadata.IsProvided)
        {
            var completedStatus = string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase);
            var noStatus = !metadata.HasNonNullValue("Status") || string.Equals(status, "null", StringComparison.OrdinalIgnoreCase);
            return exitCode == 0 && (completedStatus || noStatus)
                ? ToolCommandOutcome.Passed
                : ToolCommandOutcome.Unknown;
        }

        if (completedReceipt)
        {
            return ToolCommandOutcome.Passed;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return ToolCommandOutcome.Unknown;
        }

        // Legacy exec_cmd results may start directly with the command echo, with
        // no structured metadata or modern receipt header.
        var exit = LegacyExitLine.Match(output);
        if (!exit.Success)
        {
            return ToolCommandOutcome.Passed;
        }

        if (!int.TryParse(exit.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            return ToolCommandOutcome.Unknown;
        }

        return code == 0 ? ToolCommandOutcome.Passed : ToolCommandOutcome.Failed;
    }

    private static bool IsFailedStatus(string? status)
        => string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, "TimedOut", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, "Stopped", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalStatus(string? status)
        => string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) || IsFailedStatus(status);

    private static ReadOnlySpan<char> TakeLine(ref ReadOnlySpan<char> text)
    {
        var newline = text.IndexOf('\n');
        if (newline < 0)
        {
            var last = text.TrimEnd();
            text = [];
            return last;
        }

        var line = text[..newline].TrimEnd();
        text = text[(newline + 1)..];
        return line;
    }
}
