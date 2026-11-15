// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.AI;
using CodexFlow.Application.Jobs;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Hashline;
using CodexFlow.Core.Services;
using CodexFlow.Core.Services.Infra;
using CodexFlow.Core.TDD;
using CodexFlow.Core.TDD.Adapters;
using CodexFlow.Core.Telemetry;
using CodexFlow.Models;
using CodexFlow.Services;
using CodexFlow.Services.Background;
using CodexFlow.Telemetry;
using CodexFlow.Infrastructure.Jobs;
using Microsoft.Extensions.AI;

namespace CodexFlow.Orchestration.Hosting;

/// <summary>
/// Registers everything a host runs that does not depend on where the host keeps its data: the model client, the agent core, the
/// gateway, the job runner and host-side acceptance. The platform host and the local host both call these and then add their own
/// storage (<see cref="CodexFlow.Orchestration.Jobs.IJobDbContext"/>, the stores in this namespace, a key-value store, a job event
/// log, a lease store) and their own way to reach the user (<see cref="IRealtimeNotifier"/>).
/// </summary>
public static class OrchestrationServiceCollectionExtensions
{
    /// <summary>The model client described by the <c>VllmAgent</c> configuration section; it follows changes of that section.</summary>
    public static IServiceCollection AddCodexFlowModelClient(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var vllmConfig = configuration.GetSection("VllmAgent");

        services.AddSingleton<IChatClient>(sp => new ReloadableChatClient(
            () => BuildVllmChatClient(sp, vllmConfig),
            () => vllmConfig.GetReloadToken(),
            sp.GetRequiredService<ILogger<ReloadableChatClient>>()));
        services.AddKeyedSingleton<IChatClient>("VllmAgent", (sp, key) => sp.GetRequiredService<IChatClient>());
        return services;
    }

    private static IChatClient BuildVllmChatClient(IServiceProvider sp, IConfigurationSection vllmConfig)
    {
        var vllmHttpClient = new HttpClient(new VllmWireLoggingHandler(
            sp.GetRequiredService<ILogger<VllmWireLoggingHandler>>(),
            vllmConfig.GetValue<bool>("WireLogToolPayloads"))
        {
            InnerHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            }
        });

        var rawClient = VllmChatClientFactory.Create(vllmConfig, vllmHttpClient);

        var configuredTemperature = vllmConfig.GetValue<float?>("Temperature");
        var configuredTopP = vllmConfig.GetValue<float?>("TopP");

        return new ConfiguredSamplingChatClient(
            rawClient,
            configuredTemperature,
            configuredTopP,
            sp.GetRequiredService<ILogger<ConfiguredSamplingChatClient>>(),
            // true = never send temperature/top_p (models that deprecate them); unset = send, and stop after the model refuses once.
            vllmConfig.GetValue<bool?>("OmitSamplingParameters"),
            // The model's window in tokens. Set: a request that would not fit is shrunk before it is sent. Unset: the window is learned from
            // the provider's first "maximum context length" refusal, which is then also answered by shrinking and sending once more.
            vllmConfig.GetValue<int?>("ContextWindowTokens"));
    }

    /// <summary>The agent core, the gateway, the job runner and host-side acceptance.</summary>
    public static IServiceCollection AddCodexFlowRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IQueryLoopTelemetry, QueryLoopTelemetryService>();
        services.AddScoped<ICodexGuardrail, DefaultCodexGuardrail>(); // Phase 4A.1: Guardrail for Kernel runtime path
        services.AddScoped<ICodexAgentKernel, DefaultCodexKernel>();
        services.AddScoped<ICodexPlanner, DefaultCodexPlanner>();

        services.AddScoped<ICodexValidator, DefaultCodexValidator>();
        services.AddScoped<ICodexArchitect, DefaultCodexArchitect>();
        services.AddScoped<ClaudeCodexArchitect>(); // 娉ㄥ唽 Claude 鏋舵瀯甯?
        services.AddScoped<ICodexSecurityAuditor, DefaultCodexSecurityAuditor>();
        // 娉ㄥ唽濮斿憳浼氳鍒掓湇鍔?(Phase 0-4)
        services.Configure<CodexFlow.Core.Planning.Committee.CommitteePlanningOptions>(
            configuration.GetSection(CodexFlow.Core.Planning.Committee.CommitteePlanningOptions.SectionName));
        services.AddScoped<CodexFlow.Core.Planning.Committee.IComplexityClassifier,
            CodexFlow.Core.Planning.Committee.DefaultComplexityClassifier>();
        services.AddScoped<CodexFlow.Core.Planning.Committee.ICommitteeRoleChatClientFactory,
            CodexFlow.Core.Planning.Committee.CommitteeRoleChatClientFactory>();
        services.AddScoped<CodexFlow.Core.Planning.Committee.ICommitteePlanningService,
            CodexFlow.Core.Planning.Committee.DefaultCommitteePlanningService>();
        services.AddScoped<CodexFlow.Core.Planning.SelfCorrection.IPlanContractLinter,
            CodexFlow.Core.Planning.SelfCorrection.DefaultPlanContractLinter>();
        services.AddScoped<CodexFlow.Core.Planning.SelfCorrection.IPlanSelfCorrectionService,
            CodexFlow.Core.Planning.SelfCorrection.DefaultPlanSelfCorrectionService>();
        services.AddScoped<CodexFlow.Core.Planning.Deviation.IPlanDeviationDetector,
            CodexFlow.Core.Planning.Deviation.DefaultPlanDeviationDetector>();
        services.AddScoped<CodexFlow.Core.Planning.Patching.IPlanPatchService,
            CodexFlow.Core.Planning.Patching.DefaultPlanPatchService>();
        // 注册 Hashline 文件编辑服务
        services.AddHashlineServices(configuration.GetSection("Hashline"));
        // 瑷诲唺 IToolRegistry 宸ュ叿搴?
        services.AddScoped<CodexFlow.Core.Abstractions.IToolRegistry, CodexFlow.Core.Agents.DefaultToolRegistry>();
        services.AddScoped<CodexFlow.Core.Agents.ToolRegistryBootstrapper>(CodexFlow.Services.ToolRegistryComposition.Create);
        // 瑷诲唺 IAgentRoleRegistry 瑙掕壊搴?
        services.AddScoped<CodexFlow.Core.Abstractions.IAgentRoleRegistry, CodexFlow.Core.Services.DefaultAgentRoleRegistry>();
        services.AddScoped<CodexFlow.Core.Workers.IWorkerDefinitionRegistry, CodexFlow.Core.Workers.DefaultWorkerDefinitionRegistry>();
        // 瑷诲唺鎵瑰垽鎬ф€濈董鑸囦唬纰煎瑷堟湇鍕?
        services.AddScoped<CodexFlow.Core.Abstractions.ICodexCritiqueService, CodexFlow.Core.Services.SemanticDependencyScanner>();
        services.AddScoped<ICodeAnalysisService, SemanticDependencyScanner>();
        services.AddScoped<IGitService, GitService>();
        services.AddScoped<IArchitectureService, DefaultArchitectureService>();
        services.AddScoped<ICodexTestDesigner, DefaultCodexTestDesigner>();
        services.AddScoped<CodexFlow.Core.Agents.Tools.RunTestsTool>();
        // 瑷诲唺 TDD 閬╅厤鍣?
        services.AddScoped<ITestFrameworkAdapter, CSharpXUnitAdapter>();
        services.AddScoped<ITestFrameworkAdapter, PythonPytestAdapter>();
        services.AddScoped<ITestFrameworkAdapter, NodeJestAdapter>();
        services.AddScoped<ITestFrameworkAdapter, JavaJUnitAdapter>();
        // 瑷诲唺 Semantic Diff 鏈嶅嫏 (鏀寔澶氳獮瑷€)
        services.AddScoped<ILanguageSemanticDiffProvider, RoslynSemanticDiffProvider>();
        services.AddScoped<ILanguageSemanticDiffProvider, PythonSemanticDiffProvider>();
        services.AddScoped<ILanguageSemanticDiffProvider, NodeSemanticDiffProvider>();
        services.AddScoped<ILanguageSemanticDiffProvider, JavaSemanticDiffProvider>();
        services.AddScoped<ISemanticDiffService, CompositeSemanticDiffService>();
        services.AddSingleton<CodexFlow.Core.LanguageServices.ILanguageServiceRuntimeProbe, CodexFlow.Core.LanguageServices.DefaultLanguageServiceRuntimeProbe>();
        services.AddScoped<CodexFlow.Core.LanguageServices.ILanguageServiceProvider, CodexFlow.Core.LanguageServices.CSharpLanguageServiceProvider>();
        services.AddScoped<CodexFlow.Core.LanguageServices.ILanguageServiceProvider, CodexFlow.Core.LanguageServices.PythonLanguageServiceProvider>();
        services.AddScoped<CodexFlow.Core.LanguageServices.ILanguageServiceProvider, CodexFlow.Core.LanguageServices.TypeScriptLanguageServiceProvider>();
        services.AddScoped<CodexFlow.Core.LanguageServices.ILanguageServiceProvider, CodexFlow.Core.LanguageServices.JavaLanguageServiceProvider>();
        services.AddScoped<CodexFlow.Core.LanguageServices.ILanguageServiceRegistry, CodexFlow.Core.LanguageServices.DefaultLanguageServiceRegistry>();
        services.AddScoped<CodexFlow.Core.LanguageServices.DefaultLanguageServiceSessionFactory>();
        services.AddScoped<CodexFlow.Core.LanguageServices.ILanguageServiceSessionFactory>(sp => sp.GetRequiredService<CodexFlow.Core.LanguageServices.DefaultLanguageServiceSessionFactory>());
        services.AddScoped<CodexFlow.Core.LanguageServices.ILanguageServiceRefreshNotifier>(sp => sp.GetRequiredService<CodexFlow.Core.LanguageServices.DefaultLanguageServiceSessionFactory>());
        services.AddScoped<CodexFlow.Core.Services.ProjectScanner>();
        services.AddScoped<CodexFlow.Core.Agents.Tools.SearchFileIndexTool>();
        services.Configure<OpenSpecExecutionOptions>(configuration.GetSection(OpenSpecExecutionOptions.SectionName));
        services.AddSingleton(sp =>
        {
            var options = new CodexFlow.Core.Models.ContextCompressionTriggerOptions();
            configuration.GetSection(CodexFlow.Core.Models.ContextCompressionTriggerOptions.SectionName).Bind(options);

            var modelMaxContextTokens = configuration.GetSection("VllmAgent").GetValue<int?>("MaxTokensLength");
            if (modelMaxContextTokens.HasValue && modelMaxContextTokens.Value > 0)
            {
                options.ModelMaxContextTokens = modelMaxContextTokens.Value;
            }

            return options;
        });
        services.AddScoped<IProjectMemoryService, ProjectMemoryService>();
        services.AddScoped<CodexSessionManager>();
        // Phase 4/5/6: Memory governance services
        services.AddScoped<CodexFlow.Core.Abstractions.IFactGuardService, CodexFlow.Core.Services.DefaultFactGuardService>();
        services.AddScoped<CodexFlow.Core.Abstractions.IDriftGovernor, CodexFlow.Core.Services.DefaultDriftGovernor>();
        services.AddScoped<CodexFlow.Core.Abstractions.IMemoryOrchestrator, CodexFlow.Core.Services.DefaultMemoryOrchestrator>();
        // Phase 7: LLM call facade
        services.AddScoped<CodexFlow.Core.Abstractions.IMemoryContextAssembler, CodexFlow.Core.Services.DefaultMemoryContextAssembler>();
        services.AddScoped<CodexFlow.Core.Abstractions.ILLMExecutor, CodexFlow.Core.Services.DefaultLLMExecutor>();
        // Phase 0B/1: Unified Query Runtime
        services.AddScoped<CodexFlow.Core.Runtime.IContextWindowManager, CodexFlow.Core.Runtime.DefaultContextWindowManager>();
        services.Configure<CodexFlow.Core.Runtime.RuntimeHookOptions>(
            configuration.GetSection(CodexFlow.Core.Runtime.RuntimeHookOptions.SectionName));
        services.AddOptions<CodexFlow.Core.Runtime.QueryRuntimeBackendOptions>()
            .Bind(configuration.GetSection(CodexFlow.Core.Runtime.QueryRuntimeBackendOptions.SectionName))
            .Validate(
                static options => options.IsSupportedBackend,
                "Runtime:QueryRuntime:Backend must be qre-v2. Legacy core/qre execution was removed by the v2-only cutover.")
            .Validate(
                static options => options.ModelStreamTimeoutMs > 0,
                "Runtime:QueryRuntime:ModelStreamTimeoutMs must be greater than zero.")
            .ValidateOnStart();
        services.Configure<CodexFlow.Gateway.GatewayRuntimeOptions>(
            configuration.GetSection(CodexFlow.Gateway.GatewayRuntimeOptions.SectionName));
        services.Configure<CodexFlow.Core.Planning.Artifacts.PlanningOptions>(
            configuration.GetSection(CodexFlow.Core.Planning.Artifacts.PlanningOptions.SectionName));
        services.AddScoped<CodexFlow.Core.Runtime.IRuntimeHook, CodexFlow.Core.Runtime.RequiredToolExecutionRuntimeHook>();
        services.AddScoped<CodexFlow.Core.Runtime.IRuntimeHook, CodexFlow.Core.Runtime.IncompleteVerificationStopRuntimeHook>();
        services.AddScoped<CodexFlow.Core.Runtime.IRuntimeHook, CodexFlow.Core.Runtime.ConfiguredStopRuntimeHook>();
        services.AddScoped<CodexFlow.Core.Runtime.IRuntimeHookDispatcher, CodexFlow.Core.Runtime.RuntimeHookDispatcher>();
        services.AddScoped<CodexFlow.Core.Runtime.IQueryRuntimeEngine, CodexFlow.Core.Runtime.QreV2BackedQueryRuntimeEngine>();
        services.AddScoped<CodexFlow.Core.Abstractions.IPlanArtifactStore, CodexFlow.Infrastructure.Persistence.EfPlanArtifactStore>();
        services.AddScoped<CodexFlow.Core.Abstractions.IPlanFileService, CodexFlow.Core.Services.WorkspacePlanFileService>();
        services.AddScoped<CodexFlow.Core.Abstractions.IPlanBlueprintGenerator, CodexFlow.Core.Services.DefaultPlanBlueprintGenerator>();
        services.AddScoped<CodexFlow.Core.Abstractions.IPlanApprovalService, CodexFlow.Core.Services.DefaultPlanApprovalService>();
        services.AddScoped<CodexFlow.Core.Abstractions.IPlanProjectionService, CodexFlow.Core.Services.DefaultPlanProjectionService>();
        services.AddScoped<CodexFlow.Core.Abstractions.IPlanDiffService, CodexFlow.Core.Services.DefaultPlanDiffService>();
        // 瀵︽檪瑙€娓垏鏋舵瀹堣
        services.AddScoped<IPolicyValidator, DefaultPolicyValidator>();
        services.AddScoped<IPolicyRule, ForbiddenDependencyRule>();
        services.AddScoped<IPolicyRule, AsyncMethodNamingRule>();
        services.AddScoped<IPolicyRule, EntityIntegrityRule>();
        // 浠诲嫏娓呭柈鏈嶅嫏锛圧edis 蹇収 + SignalR 鎺ㄩ€侊級
        services.AddScoped<CodexFlow.Core.Abstractions.ITaskListService>(sp =>
            new CodexFlow.Infrastructure.Services.RedisTaskListService(
                sp.GetRequiredService<IKeyValueTextStore>(),
                sp.GetRequiredService<ILogger<CodexFlow.Infrastructure.Services.RedisTaskListService>>()));
        services.AddSingleton<CodexFlow.Core.Abstractions.ICronSchedulerService, CodexFlow.Core.Services.InMemoryCronSchedulerService>();
        services.AddSingleton<CodexFlow.Core.Abstractions.IRemoteTriggerService, CodexFlow.Core.Services.InMemoryRemoteTriggerService>();
        services.AddSingleton<CodexFlow.Core.Abstractions.IWorkflowAuditStore, CodexFlow.Core.Services.InMemoryWorkflowAuditStore>();
        // 鏍稿績瑾垮害鍣?
        services.AddScoped<CodexOrchestrator>();
        services.AddScoped<IPlannerSummaryPublisher, CodexEventSinkPlannerSummaryPublisher>();
        services.AddScoped<CodexFlow.Core.Protocols.IWorkerNotificationSerializer, CodexFlow.Core.Protocols.XmlWorkerNotificationSerializer>();
        services.AddScoped<CodexFlow.Application.Notifications.IWorkerNotificationProjector, CodexFlow.Services.Notifications.WorkerNotificationProjector>();
        services.AddScoped<CodexFlow.Application.Hooks.IWorkerHookDispatcher, CodexFlow.Services.Hooks.WorkerHookDispatcher>();
        services.AddScoped<CodexFlow.Application.Hooks.IWorkerHook, CodexFlow.Services.Notifications.WorkerNotificationHook>();
        services.AddScoped<ICodexEventSink, GatewayPlannerSummaryEventSink>();
        services.AddScoped<ICodexEventSink, OutboxPlannerSummaryEventSink>();
        // --- 5. Background Jobs (Level 9) ---
        CodexFlow.Core.Execution.IsolationServiceCollectionExtensions.AddCodexIsolatedExecution(services, configuration);
        services.AddHostedService<CodexFlow.Services.Background.IsolationReaperHostedService>();
        CodexFlow.Services.ArtifactAudit.ArtifactAuditServiceCollectionExtensions.AddArtifactAudit(services, configuration);
        // Language verifiers for host acceptance, tried in this order; the first one that recognises the project is used.
        services.AddScoped<CodexFlow.Services.Verification.IProjectVerifier, CodexFlow.Services.Verification.DotNetProjectVerifier>();
        services.AddScoped<CodexFlow.Services.Verification.IProjectVerifier, CodexFlow.Services.Verification.NodeProjectVerifier>();
        services.AddScoped<CodexFlow.Services.Verification.IProjectVerifier, CodexFlow.Services.Verification.PythonProjectVerifier>();
        services.AddScoped<IForgeArtifactVerificationService, ForgeArtifactVerificationService>();
        services.AddScoped<IBackgroundJobService, BackgroundJobService>();
        services.AddScoped<IPlanTaskDispatchService, PlanTaskDispatchService>();
        services.AddScoped<IBackgroundJobLeaseService, BackgroundJobLeaseService>();
        services.AddScoped<IBackgroundJobRunner, BackgroundJobRunner>();
        // Explicit factory: JobViewService keeps a Redis-based constructor for existing callers, which DI would find ambiguous.
        services.AddScoped<JobViewService>(sp => new JobViewService(
            sp.GetRequiredService<CodexFlow.Orchestration.Jobs.IJobDbContext>(),
            sp.GetRequiredService<IJobHotView>(),
            sp));
        services.AddScoped<IJobViewService>(sp => sp.GetRequiredService<JobViewService>());
        services.AddScoped<WorkerCoordinatorService>();
        services.AddSingleton<IOutboxSignal, OutboxSignal>();
        services.AddSingleton<IBackgroundJobSignal, BackgroundJobSignal>();
        services.AddScoped<IOutboxProjector, OutboxProjector>();
        services.AddHostedService<JobSupervisorHostedService>();
        services.AddHostedService<OutboxProjectorHostedService>();
        // Gateway 鈥?缁熶竴娑堟伅鍏ュ彛锛岀鐞?per-session 娑堟伅闃熷垪鍜?SSE 浜嬩欢娴?
        services.AddSingleton<CodexFlow.Core.Gateway.ICodexGateway, CodexFlow.Gateway.CodexGateway>();
        services.AddScoped<CodexFlow.Core.Gateway.IGatewayMessageProcessor, CodexFlow.Gateway.GatewayMessageProcessor>();
        services.Configure<CodexFlow.Models.ContextCompressionOptions>(
            configuration.GetSection(CodexFlow.Models.ContextCompressionOptions.SectionName));

        return services;
    }
}
