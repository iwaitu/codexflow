// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// The test commands of the non-.NET stacks, recognised from the command line <c>exec_cmd</c> echoes ("$ python -m pytest ..."), never from
/// what the worker says it ran. Seen live (bugfix-python-pager): Python workers that ran pytest cleanly and had nothing to change were still
/// failed, because only <c>dotnet build</c>/<c>dotnet test</c> counted as proof that the project behaves.
/// </summary>
internal static class TestCommandEvidence
{
    public const string PythonTest = "python_test";
    public const string NodeTest = "node_test";

    // python -m pytest / python3 -m unittest / py -3 -m pytest / pytest
    private static readonly Regex PythonTestCommand = new(
        @"^\$\s*(?:(?:python[\d.]*|py)\s+(?:-[^\s]+\s+)*?-m\s+(?:pytest|unittest)|pytest)(?:[ \t]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // npm test / npm run test / pnpm test / yarn test / node --test / npx jest|vitest|mocha
    private static readonly Regex NodeTestCommand = new(
        @"^\$\s*(?:(?:npm|pnpm|yarn)\s+(?:run\s+)?test|node\s+--test|npx\s+(?:-[^\s]+\s+)*?(?:jest|vitest|mocha))(?:[ \t]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The kind and canonical command for a single echoed command line.</summary>
    public static (string Kind, string Command)? Classify(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var newline = commandLine.IndexOf('\n', StringComparison.Ordinal);
        if (newline >= 0)
        {
            commandLine = commandLine[..newline].TrimEnd('\r');
        }

        if (PythonTestCommand.IsMatch(commandLine))
        {
            return (PythonTest, "python -m pytest");
        }

        return NodeTestCommand.IsMatch(commandLine) ? (NodeTest, "npm test") : null;
    }

    /// <summary>True for the verification kinds that show the project builds or its tests pass (a restore or a listing shows neither).</summary>
    public static bool ProvesBehaviour(string? kind)
        => kind is "dotnet_build" or "dotnet_test" or PythonTest or NodeTest;
}
