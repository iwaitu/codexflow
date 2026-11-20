// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Browser;

public enum BrowserProfileMode
{
    Ephemeral = 0,
    Persistent = 1
}

public enum BrowserInteractionApprovalPolicy
{
    HighRiskOnly = 0,
    AlwaysAsk = 1,
    AutoInSession = 2
}

/// <summary>
/// Configuration for the built-in Web Browser Use engine (<c>Browser</c> section in appsettings.json).
/// </summary>
public sealed class BrowserOptions
{
    public const string SectionName = "Browser";

    public static readonly int[] DefaultDevelopmentLoopbackPorts =
    [
        3000, 3001, 4173, 4200, 5000, 5001, 5173, 5174, 5175, 5500,
        7000, 7001, 8000, 8001, 8080, 8081, 8888, 9000
    ];

    public bool Enabled { get; set; } = true;

    public string? ExecutablePath { get; set; }

    public string? RemoteCdpWsUrl { get; set; }

    public bool Headless { get; set; } = true;

    public BrowserProfileMode ProfileMode { get; set; } = BrowserProfileMode.Ephemeral;

    public BrowserInteractionApprovalPolicy InteractionApprovalPolicy { get; set; } = BrowserInteractionApprovalPolicy.HighRiskOnly;

    public bool AllowLocalhostInDailyMode { get; set; }

    public bool AllowControlledDownloads { get; set; }

    public List<int> DevelopmentAllowedLoopbackPorts { get; set; } = [.. DefaultDevelopmentLoopbackPorts];

    public List<string> AllowedDomains { get; set; } = [];

    public List<string> BlockedDomains { get; set; } = [];

    public int IdleTimeoutMinutes { get; set; } = 15;

    public int MaxConcurrentSessions { get; set; } = 3;

    public int DefaultViewportWidth { get; set; } = 1280;

    public int DefaultViewportHeight { get; set; } = 800;

    public int MaxScreenshotsPerTurn { get; set; } = 4;

    public static bool IsStricterOrEqual(BrowserInteractionApprovalPolicy candidate, BrowserInteractionApprovalPolicy current)
        => Rank(candidate) >= Rank(current);

    private static int Rank(BrowserInteractionApprovalPolicy policy) => policy switch
    {
        BrowserInteractionApprovalPolicy.AlwaysAsk => 3,
        BrowserInteractionApprovalPolicy.HighRiskOnly => 2,
        BrowserInteractionApprovalPolicy.AutoInSession => 1,
        _ => 3
    };
}
