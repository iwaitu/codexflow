// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts;

namespace CodexFlow.Core.Skills;

public static class BuiltInSkills
{
    public const string FrontendBrowserDebug = "frontend-browser-debug";
    private const string FrontendBrowserDebugResource = "CodexFlow.Skills.frontend-browser-debug.SKILL.md";

    public static IReadOnlyList<BuiltInSkillDto> All { get; } = Array.AsReadOnly<BuiltInSkillDto>(
    [
        new(FrontendBrowserDebug, "前端浏览器调试",
            "用内置浏览器复现页面问题，查看截图、控制台和网络失败，并按原步骤复测。",
            "用内置浏览器打开这个前端项目的页面，复现问题并检查控制台报错。")
    ]);

    internal static async Task<string?> ReadAsync(string? name, CancellationToken cancellationToken)
    {
        if (!string.Equals(name, FrontendBrowserDebug, StringComparison.Ordinal))
        {
            return null;
        }

        using var stream = typeof(BuiltInSkills).Assembly.GetManifestResourceStream(FrontendBrowserDebugResource)
            ?? throw new InvalidOperationException("The built-in frontend browser debug skill resource is missing.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
