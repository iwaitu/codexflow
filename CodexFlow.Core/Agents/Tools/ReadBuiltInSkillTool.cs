// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using CodexFlow.Core.Skills;

namespace CodexFlow.Core.Agents.Tools;

public sealed class ReadBuiltInSkillTool : ICodexTool
{
    public const string ToolName = "read_builtin_skill";

    public string Name => ToolName;
    public string Description => "读取内置 Skill。使用内置浏览器调试或验收前端页面时，先读取 frontend-browser-debug。";
    public ToolCategory Category => ToolCategory.Read;
    public ToolCapability Capability => ToolCapability.ReadOnly;
    public IReadOnlyList<int> AllowedStages => Array.Empty<int>();

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ct.ThrowIfCancellationRequested();
        var content = await BuiltInSkills.ReadAsync(arguments.GetValueOrDefault("name") as string, ct).ConfigureAwait(false);
        return content is null
            ? CodexToolResult.Error("未找到指定的内置 Skill。")
            : CodexToolResult.Succeeded(content);
    }
}
