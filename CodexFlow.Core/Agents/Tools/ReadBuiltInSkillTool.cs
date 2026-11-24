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
    public string Description => $"读取内置 Skill。前端页面调试或验收先读取 {BuiltInSkills.FrontendBrowserDebug}。操作原生桌面应用或验收桌面客户端先读取 {BuiltInSkills.DesktopControl}。创建或修改 Skill 时先读取 {BuiltInSkills.SkillCreator}。";
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
