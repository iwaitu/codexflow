// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

public class ToolResultTextFormatterTests
{
    [Fact]
    public void BuildWrappedPayload_ShouldPopulateFormattedDisplay_WhenResultHasNoDisplay()
    {
        var payload = ToolResultTextFormatter.BuildWrappedPayload(
            CodexToolResult.Error(
                "缺少必填参数 `prompt`。",
                systemHint: "请补充目标、范围和期望输出。"),
            "spawn_worker");

        Assert.Equal(true, payload["__icodex_tool_result"]);
        var display = Assert.IsType<string>(payload["display"]);
        Assert.Contains("工具 `spawn_worker` 执行失败。", display, StringComparison.Ordinal);
        Assert.Contains("Hint: 请补充目标、范围和期望输出。", display, StringComparison.Ordinal);
        Assert.Equal("Failed", payload["status"]);
    }

    [Fact]
    public void FormatToolNotFound_ShouldIncludeAvailableToolsPreview()
    {
        var text = ToolResultTextFormatter.FormatToolNotFound(
            "read_file",
            ["ivilson_read", "search_in_files", "tool_search"]);

        Assert.Contains("当前轮次不可用工具 `read_file`。", text, StringComparison.Ordinal);
        Assert.Contains("`ivilson_read`", text, StringComparison.Ordinal);
        Assert.Contains("`tool_search`", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatException_IncludesExplicitCommandDetailsInLegacyOrder()
    {
        var exception = new CommandFailure(" command failed\r\nsecond line ", 17, " error ", " output ");

        var text = ToolResultTextFormatter.FormatException(exception, " test_tool ");

        Assert.Equal(string.Join(Environment.NewLine,
            "工具 `test_tool` 执行异常。", "exit code: 17", "command failed", "second line", "error", "output"), text);
    }

    [Fact]
    public void FormatException_DeduplicatesStreamsAndAllowsMissingExitCode()
    {
        var exception = new CommandFailure(" failed ", null, "failed", " failed\r\n");

        var text = ToolResultTextFormatter.FormatException(exception);

        Assert.Equal(string.Join(Environment.NewLine, "工具 `tool` 执行异常。", "failed"), text);
    }

    [Fact]
    public void FormatException_UsesOnlyTheMessageForAnExceptionWithoutTheDetailsContract()
    {
        var text = ToolResultTextFormatter.FormatException(new LegacyShapedException());

        Assert.Equal(string.Join(Environment.NewLine, "工具 `tool` 执行异常。", "failed"), text);
    }

    [Fact]
    public void FormatException_UsesTheTypeNameWhenTheMessageIsBlank()
    {
        var text = ToolResultTextFormatter.FormatException(new InvalidOperationException(" \r\n "));

        Assert.Equal(string.Join(Environment.NewLine, "工具 `tool` 执行异常。", nameof(InvalidOperationException)), text);
    }

    private sealed class CommandFailure(string message, int? exitCode, string? standardError, string? standardOutput)
        : Exception(message), IToolCommandFailureDetails
    {
        public int? ExitCode { get; } = exitCode;
        public string? StandardError { get; } = standardError;
        public string? StandardOutput { get; } = standardOutput;
    }

    private sealed class LegacyShapedException() : Exception("failed")
    {
        public int ExitCode => 17;
        public string Stdout => "output";
        public string Stderr => "error";
    }
}
