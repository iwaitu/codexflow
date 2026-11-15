// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;

namespace CodexFlow.Core.Abstractions;

public interface IProjectMemoryService
{
    Task<ProjectMemoryDocument> LoadAsync(
        string workspacePath,
        string? projectRoot = null,
        Uri? projectUrl = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    Task<ProjectMemoryWriteResult> SaveAnalysisAsync(ProjectAnalysisMemoryInput input, CancellationToken ct = default);

    Task<ProjectMemoryWriteResult> SaveManualSummaryAsync(ProjectManualSummaryInput input, CancellationToken ct = default);

    Task<ProjectMemoryWriteResult> SaveExecutionResultAsync(ProjectExecutionMemoryInput input, CancellationToken ct = default);
}
