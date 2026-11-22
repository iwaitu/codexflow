// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json.Serialization;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.ComputerUse;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.Agents.Tools;

public static class ComputerToolNames
{
    public const string ScreenInfo = "computer_screen_info";
    public const string Screenshot = "computer_screenshot";
    public const string Act = "computer_act";
    public const string RequestTakeover = "computer_request_takeover";

    public static readonly string[] All = [ScreenInfo, Screenshot, Act, RequestTakeover];
}

internal sealed record ComputerScreenToolMetadata(
    int DisplayCount,
    int WindowCount,
    string? PinnedProcessName);

internal sealed record ComputerScreenshotToolMetadata(
    [property: JsonPropertyName("screenshotSeq")]
    [property: Newtonsoft.Json.JsonProperty("screenshotSeq")]
    long? ScreenshotSeq,
    int? Width,
    int? Height,
    string? CoordinateMode,
    string? PinnedWindowHandle,
    bool ScreenshotAttached = false);

internal sealed record ComputerActToolMetadata(
    string Action,
    [property: JsonPropertyName("screenshotSeq")]
    [property: Newtonsoft.Json.JsonProperty("screenshotSeq")]
    long? ScreenshotSeq,
    int? Width,
    int? Height,
    string? CoordinateMode,
    bool ScreenshotAttached = false);

internal sealed record ComputerFailureToolMetadata(string? ErrorCode);

internal static class ComputerToolHelpers
{
    public static bool TryResolveCaller(out ComputerTrustedCallerContext caller, out CodexToolResult? error)
    {
        if (AmbientComputerCallerContext.Current is { } current && !string.IsNullOrWhiteSpace(current.SessionId))
        {
            caller = current;
            error = null;
            return true;
        }

        caller = default!;
        error = CodexToolResult.Error(
            "[missing_trusted_computer_context] Computer Use 工具仅可在受信任的主会话对话上下文中执行（Worker 与无人值守后台任务不可用）。",
            new ComputerFailureToolMetadata("missing_trusted_computer_context"));
        return false;
    }

    public static bool EnsureMultimodalEnabled(IModelSettingsService? modelSettings, out CodexToolResult? error)
    {
        var enabled = AmbientTurnVisualAttachmentContext.Current?.IsMultimodalEnabled
                      ?? (modelSettings is null || modelSettings.Read().SupportsMultimodal);
        if (!enabled)
        {
            error = CodexToolResult.Error(
                "[multimodal_required_for_computer_use] 当前模型未启用多模态视觉能力（SupportsMultimodal=false）。一期纯视觉 Computer Use 已被安全门禁禁用，请先在设置中切换支持视觉的多模态模型。",
                new ComputerFailureToolMetadata("multimodal_required_for_computer_use"));
            return false;
        }

        error = null;
        return true;
    }

    public static (string Output, bool ScreenshotAttached) AttachScreenshotFeedback(
        ComputerOperationResult result,
        IModelSettingsService? modelSettings,
        IOptionsMonitor<ComputerOptions>? computerOptions)
    {
        var visual = TryAttachScreenshotVisual(result.Screenshot, modelSettings, computerOptions);
        return visual is not null
            ? (result.Output + visual, true)
            : (result.Output + "\n[提示] 本次未向模型附加新截图。继续使用坐标前请重新截图，视觉额度用完时请停止坐标操作。", false);
    }

    private static string? TryAttachScreenshotVisual(
        ComputerScreenshotData? screenshot,
        IModelSettingsService? modelSettings,
        IOptionsMonitor<ComputerOptions>? computerOptions)
    {
        if (screenshot is null || screenshot.Bytes.IsEmpty)
        {
            return null;
        }

        var multimodalEnabled = AmbientTurnVisualAttachmentContext.Current?.IsMultimodalEnabled
                                ?? modelSettings?.Read().SupportsMultimodal == true;
        if (!multimodalEnabled)
        {
            return null;
        }

        var store = AmbientTurnVisualAttachmentContext.Current;
        if (store is null)
        {
            return null;
        }

        var budget = computerOptions?.CurrentValue.MaxScreenshotsPerTurn ?? 12;
        if (!AmbientComputerCallerContext.TryConsumeScreenshotBudget(budget))
        {
            return null;
        }

        var retentionCount = Math.Clamp(computerOptions?.CurrentValue.SlidingVisualRetentionCount ?? 3, 1, 8);
        var ext = string.Equals(screenshot.MimeType, "image/png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
        var fileName = string.Create(CultureInfo.InvariantCulture, $"computer-screenshot-{screenshot.ScreenshotSeq}.{ext}");
        var registered = store.RegisterSliding(
            category: "computer-screenshot",
            maxRetained: retentionCount,
            fileName: fileName,
            mimeType: screenshot.MimeType,
            bytes: screenshot.Bytes);
        if (registered is null)
        {
            return null;
        }

        AmbientComputerCallerContext.SetAttachedScreenshot(screenshot);
        return $"\n[桌面屏幕截图已注入多模态视觉上下文（滑动窗口最近 {retentionCount} 帧）] {TurnVisualAttachmentStore.FormatMarker(registered)}";
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
        if (value is double d) return (int)Math.Round(d);
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public static double? TryGetDouble(object? value)
    {
        if (value is null) return null;
        if (value is double d) return d;
        if (value is float f) return f;
        if (value is int i) return i;
        if (value is long l) return l;
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}

public sealed class ComputerScreenInfoTool(
    IComputerSessionManager manager) : ICodexTool
{
    public string Name => ComputerToolNames.ScreenInfo;

    public string Description =>
        "查询当前本机显示器拓扑与可见桌面窗口列表（窗口句柄、进程名、标题与屏幕位置），并支持锁定/前置特定目标窗口以提升后续截屏清晰度与定位精度。\n" +
        "注意：网页任务请优先使用 browser_* 工具；本工具仅用于本地原生桌面软件操作或桌面客户端 GUI 验收。\n" +
        "参数（JSON object）：\n" +
        "  - pin_window_handle (string, 可选): 指定要锁定并置于前台的窗口句柄（来自本工具返回的 handle 字段）\n" +
        "  - pin_process_name (string, 可选): 按进程名（如 \"notepad\"、\"electron\"）查找并锁定目标窗口\n" +
        "  - unpin (bool, 可选): 解除当前窗口锁定，恢复默认全屏视野";

    public ToolCategory Category => ToolCategory.Analysis;

    public ToolCapability Capability => ToolCapability.ComputerRead;

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

        if (!ComputerToolHelpers.TryResolveCaller(out var caller, out var err))
        {
            return err!;
        }

        var pinHandle = arguments.GetValueOrDefault("pin_window_handle")?.ToString();
        var pinProcess = arguments.GetValueOrDefault("pin_process_name")?.ToString();
        var unpin = ComputerToolHelpers.TryGetBool(arguments.GetValueOrDefault("unpin"));
        if (unpin || !string.IsNullOrWhiteSpace(pinHandle) || !string.IsNullOrWhiteSpace(pinProcess))
        {
            AmbientComputerCallerContext.ConsumeAttachedScreenshot();
        }

        var result = await manager
            .GetScreenInfoAsync(caller, pinHandle, pinProcess, unpin, ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return CodexToolResult.Error(result.Output, new ComputerFailureToolMetadata(result.ErrorCode), summary: result.Summary);
        }

        var state = manager.GetSessionState(caller.SessionId);
        return CodexToolResult.Succeeded(
            result.Output,
            new ComputerScreenToolMetadata(result.Displays?.Count ?? 0, result.Windows?.Count ?? 0, state.PinnedProcessName),
            summary: result.Summary);
    }
}

public sealed class ComputerScreenshotTool(
    IComputerSessionManager manager,
    IModelSettingsService? modelSettings = null,
    IOptionsMonitor<ComputerOptions>? computerOptions = null) : ICodexTool
{
    public string Name => ComputerToolNames.Screenshot;

    public string Description =>
        "捕获当前桌面或已锁定窗口的屏幕截图，按设置叠加坐标网格，并标注画面内的光标位置。截图注入多模态视觉上下文并同步至右侧「桌面」面板。CodexFlow 受保护窗口可出现在画面中，输入仍受保护。\n" +
        "必须使用支持视觉的多模态模型才可调用。\n" +
        "参数（JSON object）：\n" +
        "  - full_desktop (bool, 可选): 为 true 时捕获全虚拟桌面（含多显示器），默认 false（优先捕获锁定窗口或主显示器）\n" +
        "  - display_index (int, 可选): 指定捕获第几个显示器（从 0 开始）\n" +
        "  - include_grid (bool, 可选): 是否叠加坐标标尺网格（默认遵循宿主设置，开启可显著提高坐标点击精度）";

    public ToolCategory Category => ToolCategory.Analysis;

    public ToolCapability Capability => ToolCapability.ComputerRead;

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

        if (!ComputerToolHelpers.TryResolveCaller(out var caller, out var err))
        {
            return err!;
        }

        if (!ComputerToolHelpers.EnsureMultimodalEnabled(modelSettings, out var multimodalErr))
        {
            return multimodalErr!;
        }

        var fullDesktop = ComputerToolHelpers.TryGetBool(arguments.GetValueOrDefault("full_desktop"));
        var displayIndex = ComputerToolHelpers.TryGetInt(arguments.GetValueOrDefault("display_index"));
        bool? includeGrid = arguments.ContainsKey("include_grid")
            ? ComputerToolHelpers.TryGetBool(arguments.GetValueOrDefault("include_grid"), defaultValue: true)
            : null;

        AmbientComputerCallerContext.ConsumeAttachedScreenshot();
        var result = await manager
            .CaptureScreenshotAsync(caller, fullDesktop, displayIndex, includeGrid, ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return CodexToolResult.Error(result.Output, new ComputerFailureToolMetadata(result.ErrorCode), summary: result.Summary);
        }

        var (output, screenshotAttached) = ComputerToolHelpers.AttachScreenshotFeedback(result, modelSettings, computerOptions);

        var state = manager.GetSessionState(caller.SessionId);
        return CodexToolResult.Succeeded(
            output,
            new ComputerScreenshotToolMetadata(
                result.Screenshot?.ScreenshotSeq,
                result.Screenshot?.Width,
                result.Screenshot?.Height,
                result.Viewport?.EffectiveMode.ToString(),
                state.PinnedWindowHandle,
                screenshotAttached),
            summary: result.Summary);
    }
}

public sealed class ComputerActTool(
    IComputerSessionManager manager,
    IModelSettingsService? modelSettings = null,
    IOptionsMonitor<ComputerOptions>? computerOptions = null) : ICodexTool
{
    public string Name => ComputerToolNames.Act;

    public string Description =>
        "在宿主机桌面执行鼠标或键盘操作。坐标动作需要本轮已附加的最新桌面截图。键鼠事件提交后，通常等待 350ms 并返回新截图。红圈标注请求落点，请根据新截图确认目标窗口是否响应。严禁操作 CodexFlow 自身控制台窗口及敏感密码管理软件，高风险与跨应用操作将经控制面人工审批。只读 Plan Mode 下禁用。\n" +
        "参数（JSON object）：\n" +
        "  - action (string, 必填): click | double_click | right_click | middle_click | move | drag | scroll | type_text | key_combo | wait\n" +
        "  - x (number, 鼠标类动作必填): 目标 X 坐标（遵循最近截图所示坐标体系：图像像素坐标或 [0,1000] 千分比坐标）\n" +
        "  - y (number, 鼠标类动作必填): 目标 Y 坐标\n" +
        "  - end_x (number, drag 必填): 拖拽终点 X 坐标\n" +
        "  - end_y (number, drag 必填): 拖拽终点 Y 坐标\n" +
        "  - button (string, 可选): left（默认）| right | middle\n" +
        "  - text (string, type_text 必填): 要输入的文本（使用系统 Unicode 注入，自动绕过中文输入法干扰）\n" +
        "  - keys (string, key_combo 必填): 组合键字符串，如 \"Ctrl+C\"、\"Cmd+V\"、\"Enter\"、\"Escape\"\n" +
        "  - delta_x (int, scroll 可选): 水平滚动偏移，默认 0\n" +
        "  - delta_y (int, scroll 可选): 垂直滚动偏移（正数向下、负数向上），默认 480\n" +
        "  - duration_ms (int, wait 可选): 等待毫秒数，默认 1000（范围 100-15000）\n" +
        "  - coordinate_space (string, 可选): 显式覆盖坐标体系，pixels | normalized_1000";

    public ToolCategory Category => ToolCategory.System;

    public ToolCapability Capability => ToolCapability.ComputerInteract;

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

        if (!ComputerToolHelpers.TryResolveCaller(out var caller, out var err))
        {
            return err!;
        }

        if (caller.PlanModeActive)
        {
            return CodexToolResult.Error(
                "[plan_mode_active] 当前处于只读 Plan Mode，禁止执行 computer_act 桌面交互。",
                new ComputerFailureToolMetadata(ExecutionReasonCodes.PlanModeActive));
        }

        if (!ComputerToolHelpers.EnsureMultimodalEnabled(modelSettings, out var multimodalErr))
        {
            return multimodalErr!;
        }

        var action = arguments.GetValueOrDefault("action")?.ToString() ?? string.Empty;
        var req = new ComputerActRequest(
            Action: action,
            X: ComputerToolHelpers.TryGetDouble(arguments.GetValueOrDefault("x")),
            Y: ComputerToolHelpers.TryGetDouble(arguments.GetValueOrDefault("y")),
            EndX: ComputerToolHelpers.TryGetDouble(arguments.GetValueOrDefault("end_x")),
            EndY: ComputerToolHelpers.TryGetDouble(arguments.GetValueOrDefault("end_y")),
            Button: arguments.GetValueOrDefault("button")?.ToString(),
            Text: arguments.GetValueOrDefault("text")?.ToString(),
            Keys: arguments.GetValueOrDefault("keys")?.ToString(),
            DeltaX: ComputerToolHelpers.TryGetInt(arguments.GetValueOrDefault("delta_x")),
            DeltaY: ComputerToolHelpers.TryGetInt(arguments.GetValueOrDefault("delta_y")),
            DurationMs: ComputerToolHelpers.TryGetInt(arguments.GetValueOrDefault("duration_ms")),
            CoordinateSpace: arguments.GetValueOrDefault("coordinate_space")?.ToString())
        {
            RequireModelScreenshot = true,
            ModelScreenshot = AmbientComputerCallerContext.ConsumeAttachedScreenshot()
        };

        var result = await manager.ActAsync(caller, req, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return CodexToolResult.Error(result.Output, new ComputerFailureToolMetadata(result.ErrorCode), summary: result.Summary);
        }

        var (output, screenshotAttached) = ComputerToolHelpers.AttachScreenshotFeedback(result, modelSettings, computerOptions);

        return CodexToolResult.Succeeded(
            output,
            new ComputerActToolMetadata(
                action,
                result.Screenshot?.ScreenshotSeq,
                result.Screenshot?.Width,
                result.Screenshot?.Height,
                result.Viewport?.EffectiveMode.ToString(),
                screenshotAttached),
            summary: result.Summary);
    }
}

public sealed class ComputerRequestTakeoverTool(
    IComputerSessionManager manager,
    IModelSettingsService? modelSettings = null,
    IOptionsMonitor<ComputerOptions>? computerOptions = null) : ICodexTool
{
    public string Name => ComputerToolNames.RequestTakeover;

    public string Description =>
        "当桌面操作遇到 Windows UAC 管理员提权弹窗、系统密码输入框、硬件 Key、手机扫码、复杂验证，或重新截图后光标仍无法到位时，请求由用户本人接管桌面操作，并暂停回合计时直至用户在桌面端点击「完成并交回 Agent」。\n" +
        "参数（JSON object）：\n" +
        "  - reason (string, 必填): 向用户说明需要人工操作的具体原因与指引\n" +
        "  - timeout_seconds (int, 可选): 最长等待秒数，默认 300（范围 30-900）";

    public ToolCategory Category => ToolCategory.System;

    public ToolCapability Capability => ToolCapability.ComputerInteract;

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

        if (!ComputerToolHelpers.TryResolveCaller(out var caller, out var err))
        {
            return err!;
        }

        if (caller.PlanModeActive)
        {
            return CodexToolResult.Error(
                "[plan_mode_active] 当前处于只读 Plan Mode，禁止发起桌面人工接管。",
                new ComputerFailureToolMetadata(ExecutionReasonCodes.PlanModeActive));
        }

        var reason = arguments.GetValueOrDefault("reason")?.ToString() ?? string.Empty;
        var timeoutSeconds = ComputerToolHelpers.TryGetInt(arguments.GetValueOrDefault("timeout_seconds")) ?? 300;

        AmbientComputerCallerContext.ConsumeAttachedScreenshot();
        var result = await manager.RequestTakeoverAsync(caller, reason, timeoutSeconds, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return CodexToolResult.Error(result.Output, new ComputerFailureToolMetadata(result.ErrorCode), summary: result.Summary);
        }

        var (output, screenshotAttached) = ComputerToolHelpers.AttachScreenshotFeedback(result, modelSettings, computerOptions);

        return CodexToolResult.Succeeded(
            output,
            new ComputerScreenshotToolMetadata(
                result.Screenshot?.ScreenshotSeq,
                result.Screenshot?.Width,
                result.Screenshot?.Height,
                result.Viewport?.EffectiveMode.ToString(),
                manager.GetSessionState(caller.SessionId).PinnedWindowHandle,
                screenshotAttached),
            summary: result.Summary);
    }
}
