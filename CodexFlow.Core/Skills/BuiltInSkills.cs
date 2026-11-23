// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts;

namespace CodexFlow.Core.Skills;

public static class BuiltInSkills
{
    public const string FrontendBrowserDebug = "frontend-browser-debug";
    public const string DesktopControl = "desktop-control";
    private const string FrontendBrowserDebugResource = "CodexFlow.Skills.frontend-browser-debug.SKILL.md";
    private const string DesktopControlResource = "CodexFlow.Skills.desktop-control.SKILL.md";

    public static IReadOnlyList<BuiltInSkillDto> All { get; } = Array.AsReadOnly<BuiltInSkillDto>(
    [
        new(FrontendBrowserDebug, "前端浏览器调试",
            "用内置浏览器复现页面问题，查看截图、控制台和网络失败，并按原步骤复测。",
            "用内置浏览器打开这个前端项目的页面，复现问题并检查控制台报错。"),
        new(DesktopControl, "桌面控制",
            "锁定桌面窗口，执行键鼠操作，并根据新截图确认结果。需要时请求用户接管。",
            "用桌面控制在指定应用中点击目标按钮，再截图确认窗口是否响应。")
    ]);

    internal static async Task<string?> ReadAsync(string? name, CancellationToken cancellationToken)
    {
        var resourceName = name switch
        {
            FrontendBrowserDebug => FrontendBrowserDebugResource,
            DesktopControl => DesktopControlResource,
            _ => null
        };
        if (resourceName is null)
        {
            return null;
        }

        using var stream = typeof(BuiltInSkills).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The built-in skill resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
