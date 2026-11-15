// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Domain.Planning;

public class PlanArtifactEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public string Markdown { get; set; } = string.Empty;
    public string? PreviewProjectedMarkdownHash { get; set; }
    public string? PlanFilePath { get; set; }
    public string TasksJson { get; set; } = "[]";
    public string ReviewsJson { get; set; } = "[]";
    public string? ApprovalJson { get; set; }
    public string? ProjectionJson { get; set; }
    public string? ParentPlanId { get; set; }
    public string? SupersededByPlanId { get; set; }
    public bool IsCurrent { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
