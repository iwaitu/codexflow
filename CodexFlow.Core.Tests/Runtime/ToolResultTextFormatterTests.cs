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
}
