// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;

namespace CodexFlow.Core.Models;

public sealed record ToolSystemHint(
    string Message,
    string? RequiredToolName = null,
    bool ToolCallRequired = false);

public enum ToolResultStatus
{
    Success,
    PartialSuccess,
    Failed,
    BlockedByGuardrail,
    ValidationRequired
}

public class CodexToolResult
{
    private JsonElement? _metadata;

    public ToolResultStatus Status { get; set; } = ToolResultStatus.Success;

    // 供 Agent 阅读的主要输出字符串
    public string Output { get; set; } = string.Empty;

    // 稳定短摘要，供 UI / telemetry / notification 侧优先消费
    public string? Summary { get; set; }

    // 供 Agent/UI 展示的首选文本；若为空则回退到 Output
    public string? Display { get; set; }

    // 标记 Output/Display 是否经过截断
    public bool IsOutputTruncated { get; set; }

    // 结构化元数据 (例如: 修改的文件列表, 编译报错详情, 搜索结果)
    public JsonElement? Metadata
    {
        get => _metadata;
        set => _metadata = ToolResultMetadata.Own(value);
    }

    // 系统级警告或建议
    public string? SystemHint { get; set; }

    // 结构化系统提示，避免从自然语言提示中反向解析必需工具。
    public ToolSystemHint? SystemHintDetail { get; set; }

    public static CodexToolResult Succeeded(
        string output,
        JsonElement? metadata = null,
        string? summary = null,
        string? display = null,
        bool isOutputTruncated = false,
        string? systemHint = null,
        string? requiredToolName = null,
        bool toolCallRequired = false)
        => new()
        {
            Status = ToolResultStatus.Success,
            Output = output,
            Summary = summary,
            Display = display,
            IsOutputTruncated = isOutputTruncated,
            Metadata = metadata,
            SystemHint = systemHint,
            SystemHintDetail = CreateSystemHintDetail(systemHint, requiredToolName, toolCallRequired)
        };

    public static CodexToolResult Error(
        string message,
        JsonElement? metadata = null,
        string? summary = null,
        string? display = null,
        bool isOutputTruncated = false,
        string? systemHint = null,
        string? requiredToolName = null,
        bool toolCallRequired = false)
        => new()
        {
            Status = ToolResultStatus.Failed,
            Output = message,
            Summary = summary,
            Display = display,
            IsOutputTruncated = isOutputTruncated,
            Metadata = metadata,
            SystemHint = systemHint,
            SystemHintDetail = CreateSystemHintDetail(systemHint, requiredToolName, toolCallRequired)
        };

    public override string ToString() => string.IsNullOrWhiteSpace(Display) ? Output : Display;

    private static ToolSystemHint? CreateSystemHintDetail(
        string? message,
        string? requiredToolName,
        bool toolCallRequired)
    {
        if (string.IsNullOrWhiteSpace(message) &&
            string.IsNullOrWhiteSpace(requiredToolName) &&
            !toolCallRequired)
        {
            return null;
        }

        return new ToolSystemHint(
            message?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(requiredToolName) ? null : requiredToolName.Trim(),
            toolCallRequired);
    }
}
