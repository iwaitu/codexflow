// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.ComputerUse;

public enum ComputerInteractionApprovalPolicy
{
    AlwaysAsk = 0,
    AppScopedInSession = 1,
    HighRiskOnly = 2
}

public enum ComputerCoordinateMode
{
    Auto = 0,
    ScreenshotPixels = 1,
    Normalized1000 = 2
}

/// <summary>
/// Configuration for the built-in desktop Computer Use engine (<c>ComputerUse</c> section in appsettings.json).
/// Uses source-generated configuration binding compatible properties only.
/// </summary>
public sealed class ComputerOptions
{
    public const string SectionName = "ComputerUse";

    public static readonly string[] DefaultBlockedProcessNames =
    [
        "keepass",
        "keepassxc",
        "1password",
        "bitwarden",
        "enpass",
        "dashlane",
        "credwiz",
        "keychain access",
        "securityagent"
    ];

    public bool Enabled { get; set; } = true;

    public ComputerInteractionApprovalPolicy InteractionApprovalPolicy { get; set; } =
        ComputerInteractionApprovalPolicy.AlwaysAsk;

    public ComputerCoordinateMode CoordinateMode { get; set; } = ComputerCoordinateMode.Auto;

    public int MaxImageLongEdge { get; set; } = 1280;

    public int MaxScreenshotsPerTurn { get; set; } = 12;

    public int SlidingVisualRetentionCount { get; set; } = 3;

    public bool EnableGridOverlay { get; set; } = true;

    public bool AutoMinimizeHostWindow { get; set; } = true;

    public string EmergencyStopShortcut { get; set; } = "CommandOrControl+.";

    public int IdleTimeoutMinutes { get; set; } = 15;

    public IReadOnlyList<string> BlockedProcessNames { get; set; } = [.. DefaultBlockedProcessNames];

    public static bool IsStricterOrEqual(
        ComputerInteractionApprovalPolicy candidate,
        ComputerInteractionApprovalPolicy current)
        => Rank(candidate) >= Rank(current);

    public static ComputerCoordinateMode ResolveEffectiveCoordinateMode(
        ComputerCoordinateMode configured,
        string? modelName)
    {
        if (configured != ComputerCoordinateMode.Auto)
        {
            return configured;
        }

        if (string.IsNullOrWhiteSpace(modelName))
        {
            return ComputerCoordinateMode.ScreenshotPixels;
        }

        var lower = modelName.Trim().ToLowerInvariant();
        if (lower.Contains("qwen", StringComparison.Ordinal) ||
            lower.Contains("glm", StringComparison.Ordinal) ||
            lower.Contains("ui-tars", StringComparison.Ordinal) ||
            lower.Contains("uitars", StringComparison.Ordinal) ||
            lower.Contains("seed-vl", StringComparison.Ordinal) ||
            lower.Contains("minicpm", StringComparison.Ordinal))
        {
            return ComputerCoordinateMode.Normalized1000;
        }

        return ComputerCoordinateMode.ScreenshotPixels;
    }

    private static int Rank(ComputerInteractionApprovalPolicy policy) => policy switch
    {
        ComputerInteractionApprovalPolicy.AlwaysAsk => 3,
        ComputerInteractionApprovalPolicy.AppScopedInSession => 2,
        ComputerInteractionApprovalPolicy.HighRiskOnly => 1,
        _ => 3
    };
}
