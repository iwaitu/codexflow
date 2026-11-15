// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;

namespace CodexFlow.Core.Abstractions;

/// <summary>
/// 架構守衛接口，用於在代碼提交前進行語義級規範校驗。
/// </summary>
public interface IPolicyValidator
{
    /// <summary>
    /// 執行策略校驗。
    /// </summary>
    Task<PolicyResult> ValidateAsync(CodexSession session, CodexTask task, string shadowPath, CancellationToken ct = default);
}

public interface IPolicyRule
{
    string Name { get; }
    string Description { get; }
    Task<RuleResult> EvaluateAsync(CodexSession session, string shadowPath, CancellationToken ct = default);
}
