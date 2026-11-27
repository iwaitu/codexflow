// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, you can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.TDD.Adapters;

internal sealed class NodeTestAdapter : ITestFrameworkAdapter
{
    public string Language => "javascript";
    public string FrameworkName => "node:test";

    private static readonly Regex TapCounts = new(
        @"^# (pass|fail) ([0-9]+)\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public string GetPromptTemplate(string taskDescription, string targetCodeContext) =>
        $$"""
        使用 Node.js 内置的 node:test 和 node:assert/strict 编写一个失败的单元测试。
        使用 ES 模块语法，输出纯 JavaScript 代码。

        [任务描述]
        {{taskDescription}}

        [被测代码上下文]
        {{targetCodeContext}}
        """;

    public (string Command, string Args) GetTestCommand(string testFilePath)
        => ("node", $"--test --test-reporter=tap -- \"{testFilePath}\"");

    public TestExecutionResult ParseOutput(string consoleOutput, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(consoleOutput);

        var result = new TestExecutionResult { OutputSummary = consoleOutput };
        foreach (Match match in TapCounts.Matches(consoleOutput))
        {
            if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                continue;

            if (match.Groups[1].Value == "pass")
                result.PassedCount = count;
            else
                result.FailedCount = count;
        }

        result.Success = exitCode == 0 && result.PassedCount > 0 && result.FailedCount == 0;
        if (!result.Success)
        {
            foreach (var line in consoleOutput.Split('\n'))
            {
                if (line.TrimStart().StartsWith("not ok ", StringComparison.Ordinal))
                    result.FailureDetails.Add(line.Trim());
            }

            if (result.FailureDetails.Count == 0)
            {
                result.FailureDetails.Add(exitCode != 0
                    ? $"Node.js test runner exited with code {exitCode}."
                    : "Node.js test runner reported no passing tests.");
            }
        }

        return result;
    }

    public string GetDefaultTestFileName(string targetFileName)
        => $"{Path.GetFileNameWithoutExtension(targetFileName)}.test.mjs";
}
