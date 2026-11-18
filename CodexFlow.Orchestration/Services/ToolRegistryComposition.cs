// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Services.Background;
using Microsoft.Extensions.Options;

namespace CodexFlow.Services;

/// <summary>
/// The production composition of <see cref="CodexFlow.Core.Agents.ToolRegistryBootstrapper"/> (Program.cs registers it as scoped).
/// It lives here, not inline in Program.cs, so integration tests exercise the SAME wiring - in particular that the isolation
/// gate reaches the execution tools (a missing gate surfaced in a real run as "exec_cmd unavailable: [IsolationNotConfigured]").
/// </summary>
public static class ToolRegistryComposition
{
    public static CodexFlow.Core.Agents.ToolRegistryBootstrapper Create(IServiceProvider sp)
    {
    var backgroundJobService = sp.GetRequiredService<IBackgroundJobService>();
    var workerCoordinatorService = sp.GetRequiredService<WorkerCoordinatorService>();
    return new CodexFlow.Core.Agents.ToolRegistryBootstrapper(
        sp.GetRequiredService<CodexFlow.Core.Abstractions.IGitService>(),
        sp.GetRequiredService<CodexFlow.Core.Agents.CodexOrchestrator>(),
        sp.GetRequiredService<CodexFlow.Core.Services.ProjectScanner>(),
        sp.GetRequiredService<CodexFlow.Core.Abstractions.ICodeAnalysisService>(),
        sp.GetRequiredService<CodexFlow.Core.Abstractions.IArchitectureService>(),
        sp.GetRequiredService<CodexFlow.Core.Abstractions.ICodexPlanner>(),
        sp.GetRequiredService<CodexFlow.Core.Agents.CodexSessionManager>(),
        sp.GetRequiredService<CodexFlow.Core.Abstractions.IProjectMemoryService>(),
        sp.GetRequiredService<CodexFlow.Core.Agents.Tools.SearchFileIndexTool>(),
        sp.GetRequiredService<CodexFlow.Core.Agents.Tools.RunTestsTool>(),
        async jobId =>
        {
            var result = await backgroundJobService.RetryJobAsync(jobId).ConfigureAwait(false);
            return result == CodexFlow.Application.Jobs.BackgroundJobCommandResult.Success;
        },
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CodexFlow.Core.Agents.ToolRegistryBootstrapper>>(),
        sp.GetService<CodexFlow.Core.Abstractions.IMemoryOrchestrator>(),
        sp.GetService<CodexFlow.Core.Abstractions.IHashlineFileService>(),
        sp.GetService<CodexFlow.Core.Hashline.Models.HashlineOptions>(),
        sp.GetService<CodexFlow.Core.LanguageServices.ILanguageServiceRegistry>(),
        sp.GetService<CodexFlow.Core.LanguageServices.ILanguageServiceSessionFactory>(),
        sp.GetService<CodexFlow.Core.LanguageServices.ILanguageServiceRefreshNotifier>(),
        sp.GetService<IHttpClientFactory>(),
        sp.GetService<CodexFlow.Core.Abstractions.ISkillScriptRunner>(),
        workerCoordinatorService.SpawnWorkerAsync,
        workerCoordinatorService.ContinueWorkerAsync,
        workerCoordinatorService.StopWorkerAsync,
        workerCoordinatorService.CleanupWorkerWorktreeAsync,
        workerCoordinatorService.ListWorkersAsync,
        workerCoordinatorService.GetWorkerOutputAsync,
        sp.GetService<CodexFlow.Core.Abstractions.ITaskListService>(),
        sp.GetRequiredService<CodexFlow.Core.Abstractions.ICronSchedulerService>(),
        sp.GetRequiredService<CodexFlow.Core.Abstractions.IRemoteTriggerService>(),
        sp.GetRequiredService<CodexFlow.Core.Abstractions.IPushNotificationService>(),
        sp.GetRequiredService<CodexFlow.Core.Abstractions.IWorkflowAuditStore>(),
        sp.GetRequiredService<CodexFlow.Core.Abstractions.IPlanArtifactStore>(),
        sp.GetRequiredService<CodexFlow.Core.Abstractions.IPlanFileService>(),
        sp.GetRequiredService<CodexFlow.Core.Abstractions.IPlanApprovalService>(),
        sp.GetRequiredService<CodexFlow.Core.Abstractions.IPlanProjectionService>(),
        sp.GetRequiredService<CodexFlow.Core.Abstractions.IPlanDiffService>(),
        sp.GetRequiredService<CodexFlow.Core.Abstractions.IPlanBlueprintGenerator>(),
        sp.GetRequiredService<CodexFlow.Core.Planning.Patching.IPlanPatchService>(),
        sp.GetRequiredService<IOptions<CodexFlow.Core.Planning.Artifacts.PlanningOptions>>(),
        // The isolation gate is what lets exec_cmd/exec_code/run_tests/skill run (in the container backend). Without it every
        // execution tool refuses with IsolationNotConfigured, so it must always be passed here.
        isolationGate: sp.GetService<CodexFlow.Core.Execution.ToolIsolationGate>(),
        documentReaderRouter: sp.GetService<CodexFlow.Core.Documents.DocumentReaderRouter>(),
        configuration: sp.GetService<IConfiguration>(),
        modelSettingsService: sp.GetService<CodexFlow.Core.Configuration.IModelSettingsService>(),
        planModeHostBridge: sp.GetService<CodexFlow.Core.Agents.Tools.IPlanModeHostBridge>(),
        workspaceSettingsService: sp.GetService<CodexFlow.Core.Configuration.IWorkspaceSettingsService>())
    {
        RetryJobDetailedFunc = async (jobId, guidance) =>
        {
            var outcome = await backgroundJobService.RetryJobDetailedAsync(jobId, guidance).ConfigureAwait(false);
            return new CodexFlow.Core.Agents.Tools.RetryJobResult(
                outcome.Result == CodexFlow.Application.Jobs.BackgroundJobCommandResult.Success,
                outcome.ReasonCode, outcome.Reason, outcome.FailureSummary);
        }
    };
    }
}
