// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Browser;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.Agents.Tools;

public static class BrowserToolNames
{
    public const string Navigate = "browser_navigate";
    public const string Snapshot = "browser_snapshot";
    public const string Act = "browser_act";
    public const string Tabs = "browser_tabs";

    public static readonly string[] All = [Navigate, Snapshot, Act, Tabs];
}

internal static class BrowserToolHelpers
{
    public static bool TryResolveCaller(out BrowserTrustedCallerContext caller, out CodexToolResult? error)
    {
        if (AmbientBrowserCallerContext.Current is { } current && !string.IsNullOrWhiteSpace(current.SessionId))
        {
            caller = current;
            error = null;
            return true;
        }

        caller = default!;
        error = CodexToolResult.Error(
            "[missing_trusted_browser_context] 浏览器工具仅可在受信任的主会话对话上下文中执行（Worker 与无人值守后台任务不可用）。");
        return false;
    }

    public static bool ResolveMultimodalEnabled(IModelSettingsService? modelSettings)
    {
        if (AmbientTurnVisualAttachmentContext.Current is { } store)
        {
            return store.IsMultimodalEnabled;
        }

        return modelSettings?.Read().SupportsMultimodal == true;
    }

    public static string? TryAttachScreenshotVisual(
        BrowserScreenshotData? screenshot,
        IModelSettingsService? modelSettings,
        IOptionsMonitor<BrowserOptions>? browserOptions)
    {
        if (screenshot is null || screenshot.Bytes.IsEmpty)
        {
            return null;
        }

        if (!ResolveMultimodalEnabled(modelSettings))
        {
            return null;
        }

        var store = AmbientTurnVisualAttachmentContext.Current;
        if (store is null)
        {
            return null;
        }

        var budget = browserOptions?.CurrentValue.MaxScreenshotsPerTurn ?? 4;
        if (!AmbientBrowserCallerContext.TryConsumeScreenshotBudget(budget))
        {
            return null;
        }

        var fileName = string.Create(CultureInfo.InvariantCulture, $"browser-screenshot-{screenshot.ScreenshotSeq}.jpg");
        var registered = store.Register(fileName, screenshot.MimeType, screenshot.Bytes);
        if (registered is null)
        {
            return null;
        }

        return $"\n[浏览器页面截图已注入多模态视觉上下文] {TurnVisualAttachmentStore.FormatMarker(registered)}";
    }

    public static bool TryGetBool(object? value, bool defaultValue = false)
    {
        if (value is null) return defaultValue;
        if (value is bool b) return b;
        return bool.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue;
    }

    public static int? TryGetInt(object? value)
    {
        if (value is null) return null;
        if (value is int i) return i;
        if (value is long l) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}

public sealed class BrowserNavigateTool(
    IBrowserSessionManager manager) : ICodexTool
{
    public string Name => BrowserToolNames.Navigate;

    public string Description =>
        "使用内置受管浏览器打开或导航网页（支持现代 JS/SPA 渲染、开发模式 localhost 前端联调验收与日常网页浏览）。\n" +
        "注意：静态文档阅读优先用 fetch_webpage；需要渲染、查看页面状态或交互时使用浏览器工具。\n" +
        "参数（JSON object）：\n" +
        "  - url (string, 当 action=\"goto\" 时必填): 目标 http:// 或 https:// 链接\n" +
        "  - action (string, 可选): goto（默认）| back | forward | reload";

    public ToolCategory Category => ToolCategory.Analysis;

    public ToolCapability Capability => ToolCapability.BrowserRead;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 16_384);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        if (!BrowserToolHelpers.TryResolveCaller(out var caller, out var err))
        {
            return err!;
        }

        var action = arguments.GetValueOrDefault("action")?.ToString() ?? "goto";
        var url = arguments.GetValueOrDefault("url")?.ToString();

        var result = await manager.NavigateAsync(caller, action, url, ct).ConfigureAwait(false);
        return result.Succeeded
            ? CodexToolResult.Succeeded(result.Output, new { result.Url, result.Title }, summary: result.Summary)
            : CodexToolResult.Error(result.Output, new { result.ErrorCode }, summary: result.Summary);
    }
}

public sealed class BrowserSnapshotTool(
    IBrowserSessionManager manager,
    IModelSettingsService? modelSettings = null,
    IOptionsMonitor<BrowserOptions>? browserOptions = null) : ICodexTool
{
    public string Name => BrowserToolNames.Snapshot;

    public string Description =>
        "获取当前浏览器页面的结构化交互快照（带 [e1] 元素编号供 browser_act 点击/输入）与页面文本；可选同时采集浏览器 Console 日志、网络错误以及页面截图（多模态模型可直接看图）。\n" +
        "参数（JSON object）：\n" +
        "  - max_chars (int, 可选): 返回快照字符上限，默认 12000，范围 1000-24000\n" +
        "  - include_console (bool, 可选): 是否附带近期页面 console 日志与未捕获异常（开发调试推荐）\n" +
        "  - include_network_errors (bool, 可选): 是否附带近期失败网络请求\n" +
        "  - include_screenshot (bool, 可选): 是否截取当前视口截图刷新右侧预览并在多模态模型下注入视觉上下文";

    public ToolCategory Category => ToolCategory.Analysis;

    public ToolCapability Capability => ToolCapability.BrowserRead;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 24_576);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        if (!BrowserToolHelpers.TryResolveCaller(out var caller, out var err))
        {
            return err!;
        }

        var maxChars = BrowserToolHelpers.TryGetInt(arguments.GetValueOrDefault("max_chars")) ?? 12000;
        var includeConsole = BrowserToolHelpers.TryGetBool(arguments.GetValueOrDefault("include_console"));
        var includeNetErrors = BrowserToolHelpers.TryGetBool(arguments.GetValueOrDefault("include_network_errors"));
        var includeScreenshot = BrowserToolHelpers.TryGetBool(arguments.GetValueOrDefault("include_screenshot"));

        var result = await manager
            .SnapshotAsync(caller, maxChars, includeConsole, includeNetErrors, includeScreenshot, ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return CodexToolResult.Error(result.Output, new { result.ErrorCode }, summary: result.Summary);
        }

        var output = result.Output;
        if (includeScreenshot)
        {
            var visualSuffix = BrowserToolHelpers.TryAttachScreenshotVisual(result.Screenshot, modelSettings, browserOptions);
            if (!string.IsNullOrEmpty(visualSuffix))
            {
                output += visualSuffix;
            }
            else if (result.Screenshot is not null)
            {
                output += "\n[提示] 截图已更新至桌面右侧「浏览器」预览面板（当前模型未开启多模态或已达本轮视觉注入上限，请结合上方元素快照继续交互）。";
            }
        }

        return CodexToolResult.Succeeded(
            output,
            new { result.Url, result.Title, screenshotSeq = result.Screenshot?.ScreenshotSeq },
            summary: result.Summary);
    }
}

public sealed class BrowserActTool(
    IBrowserSessionManager manager) : ICodexTool
{
    public string Name => BrowserToolNames.Act;

    public string Description =>
        "在当前浏览器页面执行交互动作（基于 browser_snapshot 返回的元素 ref 如 \"e3\"）。安全策略禁止直接向密码或验证码/OTP 字段输入；敏感提交操作将触发人工审批。\n" +
        "参数（JSON object）：\n" +
        "  - action (string, 必填): click | double_click | hover | type | clear | press_key | select_option | scroll | wait\n" +
        "  - ref (string, 点击/输入/选择时必填): 元素编号，如 \"e3\"\n" +
        "  - text (string, type 或 select_option 时必填): 要输入的文本或下拉框选项值\n" +
        "  - key (string, press_key 时必填): 按键名，如 \"Enter\"、\"Escape\"、\"Tab\"\n" +
        "  - delta_y (int, scroll 可选): 垂直滚动像素，正数向下、负数向上，默认 500\n" +
        "  - timeout_ms (int, wait 可选): 等待毫秒数，默认 1000（范围 100-15000）";

    public ToolCategory Category => ToolCategory.System;

    public ToolCapability Capability => ToolCapability.BrowserInteract;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 16_384);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        if (!BrowserToolHelpers.TryResolveCaller(out var caller, out var err))
        {
            return err!;
        }

        if (caller.PlanModeActive)
        {
            return CodexToolResult.Error("[plan_mode_active] 当前处于只读 Plan Mode，禁止执行 browser_act 页面交互。");
        }

        var action = arguments.GetValueOrDefault("action")?.ToString() ?? string.Empty;
        var req = new BrowserActRequest(
            Action: action,
            Ref: arguments.GetValueOrDefault("ref")?.ToString(),
            Text: arguments.GetValueOrDefault("text")?.ToString(),
            Key: arguments.GetValueOrDefault("key")?.ToString(),
            DeltaY: BrowserToolHelpers.TryGetInt(arguments.GetValueOrDefault("delta_y")),
            TimeoutMs: BrowserToolHelpers.TryGetInt(arguments.GetValueOrDefault("timeout_ms")));

        var result = await manager.ActAsync(caller, req, ct).ConfigureAwait(false);
        return result.Succeeded
            ? CodexToolResult.Succeeded(result.Output, new { result.Url, result.Title }, summary: result.Summary)
            : CodexToolResult.Error(result.Output, new { result.ErrorCode }, summary: result.Summary);
    }
}

public sealed class BrowserTabsTool(
    IBrowserSessionManager manager) : ICodexTool
{
    public string Name => BrowserToolNames.Tabs;

    public string Description =>
        "管理当前会话的浏览器标签页，或关闭整个浏览器会话。\n" +
        "参数（JSON object）：\n" +
        "  - action (string, 必填): list | new | switch | close | close_browser\n" +
        "  - tab_id (string, switch 或 close 可选/必填): 标签页 ID\n" +
        "  - url (string, new 可选): 新建标签页时自动打开的 URL";

    public ToolCategory Category => ToolCategory.Analysis;

    public ToolCapability Capability => ToolCapability.BrowserRead;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 8_192);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        if (!BrowserToolHelpers.TryResolveCaller(out var caller, out var err))
        {
            return err!;
        }

        var action = arguments.GetValueOrDefault("action")?.ToString() ?? "list";
        var tabId = arguments.GetValueOrDefault("tab_id")?.ToString();
        var url = arguments.GetValueOrDefault("url")?.ToString();

        var result = await manager.TabsAsync(caller, action, tabId, url, ct).ConfigureAwait(false);
        return result.Succeeded
            ? CodexToolResult.Succeeded(result.Output, new { result.Url, result.Title }, summary: result.Summary)
            : CodexToolResult.Error(result.Output, new { result.ErrorCode }, summary: result.Summary);
    }
}
