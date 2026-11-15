// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Agents;

public class DefaultCodexArchitect : ICodexArchitect
{
    private readonly ICodexAgentKernel _kernel;
    private readonly ICodexPlanner _planner; // Reusing existing planner logic for now
    private readonly ILogger<DefaultCodexArchitect> _logger;

    public DefaultCodexArchitect(
        ICodexAgentKernel kernel,
        ICodexPlanner planner,
        ILogger<DefaultCodexArchitect> logger)
    {
        _kernel = kernel;
        _planner = planner;
        _logger = logger;
    }

    public async Task<string> AnalyzeAsync(CodexSession session, string userGoal, CancellationToken ct = default)
    {
        StructuredLog.Information(_logger, "[Ivilson-Prime] Starting architectural analysis...");

        // 使用 Architect 角色执行 Kernel
        var response = await _kernel.RunLoopAsync(session, userGoal, CodexAgentRole.Architect, ct).ConfigureAwait(false);
        return response.Text;
    }

    public async Task<List<CodexTask>> PlanAsync(CodexSession session, string goal, CancellationToken ct = default)
    {
        StructuredLog.Information(_logger, "[Ivilson-Prime] Starting planning phase...");
        return await _planner.GeneratePlanAsync(session, goal, ct).ConfigureAwait(false);
    }
}

