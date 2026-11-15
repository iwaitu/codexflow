// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;

namespace CodexFlow.Core.Runtime;

public static class WorkerStatusTool
{
    public const string Name = "report_task_status";
    public const string Completed = "completed";
    public const string Blocked = "blocked";
}

/// <summary>
/// What a worker states about its own task through the <c>report_task_status</c> tool: done, or blocked with what is left. A structured call, so the
/// host never has to read the final prose to learn that a worker stopped half way.
/// </summary>
public sealed record WorkerStatusReport(string Status, string? Reason, IReadOnlyList<string> Unfinished)
{
    /// <summary>
    /// Verification the worker could not run or finish in its own environment although every change the task asks for is written. It is not a
    /// reason to fail the task: the host's acceptance (build, tests, dependency and secret scans) judges the change independently.
    /// </summary>
    public IReadOnlyList<string> Unverified { get; init; } = [];

    /// <summary>
    /// Blocked by its own word, or "completed" while listing changes it did not write: the tool defines completed as "every requested change is
    /// written", so a list of unfinished changes decides, not the label next to it.
    /// </summary>
    public bool IsBlocked => string.Equals(Status, WorkerStatusTool.Blocked, StringComparison.Ordinal) || Unfinished.Count > 0;

    public static WorkerStatusReport? FromArguments(JsonElement arguments)
    {
        // The generic tool adapter exposes a single `parameters` object, so a live call arrives as {"parameters":{"status":...}}.
        if (arguments.ValueKind == JsonValueKind.Object &&
            !arguments.TryGetProperty("status", out _) &&
            arguments.TryGetProperty("parameters", out var wrapped) &&
            wrapped.ValueKind == JsonValueKind.Object)
        {
            arguments = wrapped;
        }

        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("status", out var statusElement) ||
            statusElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var status = statusElement.GetString()?.Trim().ToLowerInvariant();
        if (status is not (WorkerStatusTool.Completed or WorkerStatusTool.Blocked))
        {
            return null;
        }

        var reason = arguments.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
            ? reasonElement.GetString()?.Trim()
            : null;
        return new WorkerStatusReport(status, string.IsNullOrWhiteSpace(reason) ? null : reason, ReadItems(arguments, "unfinished"))
        {
            Unverified = ReadItems(arguments, "unverified")
        };
    }

    // A list of strings, or a single string for one item.
    private static List<string> ReadItems(JsonElement arguments, string name)
    {
        var items = new List<string>();
        if (arguments.TryGetProperty(name, out var list))
        {
            if (list.ValueKind == JsonValueKind.Array)
            {
                items.AddRange(list.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString()!.Trim())
                    .Where(static item => item.Length > 0));
            }
            else if (list.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(list.GetString()))
            {
                items.Add(list.GetString()!.Trim());
            }
        }

        return items;
    }
}
