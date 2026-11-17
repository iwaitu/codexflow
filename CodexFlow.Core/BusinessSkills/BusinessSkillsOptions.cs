// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.BusinessSkills;

/// <summary>
/// 内置企业插件与业务 Skill 构建器宿主选项。
/// 按设计第 15 节约定，功能开关默认为 false（关闭），仅在开发和测试阶段显式启用。
/// </summary>
public sealed class BusinessSkillsOptions
{
    public const string SectionName = "BusinessSkills";

    public bool Enabled { get; set; }

    public string? CredentialStoreDirectory { get; set; }

    /// <summary>默认运行记录保留天数（仅清理终态，OutcomeUnknown 需人工核对后保留）。</summary>
    public int RunHistoryRetentionDays { get; set; } = 30;

    /// <summary>默认步骤诊断报文摘要保留天数，超期裁剪为脱敏占位标记。</summary>
    public int DiagnosticsRetentionDays { get; set; } = 7;

    /// <summary>默认后台操作记录保留天数。</summary>
    public int OperationHistoryRetentionDays { get; set; } = 30;

    public ICollection<int> ForbiddenLocalPorts { get; } = new List<int>();
}
