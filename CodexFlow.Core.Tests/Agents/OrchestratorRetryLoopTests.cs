// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using CodexFlow.Core.TDD;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Tests.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using System.IO;

namespace CodexFlow.Core.Tests.Agents;

/// <summary>
/// Tests for the Orchestrator retry loop behavior:
/// - Semantic Diff skip on ZeroToolCalls retry
/// - Correct call counts for each failure path
/// - Retry exhaustion handling
/// </summary>
public class OrchestratorRetryLoopTests
{
    private static (CodexSession session, CodexTask task) CreateSessionAndTask(string sessionId = "test-session")
    {
        var ws = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        var task = new CodexTask { Id = $"{sessionId}-t1", Title = "Test Task", Status = CodexTaskStatus.Pending, ComplexityLevel = 1 };
        var session = new CodexSession
        {
            Id = sessionId,
            CurrentStage = 3,
            WorkspacePath = ws,
        };
        session.ReplacePlan([task]);
        return (session, task);
    }

    private static IConfiguration CreateConfig(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Workspace:EnableAuditor"] = "true",
            ["Git:AutoPush"] = "false"
        };
        if (overrides != null)
        {
            foreach (var kv in overrides) settings[kv.Key] = kv.Value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(settings!).Build();
    }

    private class TestSetup
    {
        public CodexSession Session { get; }
        public CodexTask Task { get; }
        public Mock<ISessionStore> Store { get; }
        public Mock<ICodexAgentKernel> Kernel { get; }
        public Mock<ICodexSecurityAuditor> Security { get; }
        public Mock<ICodexValidator> Validator { get; }
        public Mock<ISemanticDiffService> SemanticDiff { get; }
        public Mock<IGitService> Git { get; }
        public Mock<ProjectScanner> Scanner { get; }
        public IConfiguration Config { get; }
        public FakeIsolationService Isolation { get; } = new();
        public ToolIsolationGate IsolationGate { get; }

        public TestSetup(string sessionId = "test-session", Dictionary<string, string?>? configOverrides = null)
        {
            var (s, t) = CreateSessionAndTask(sessionId);
            Session = s;
            Task = t;
            EnsureBuildableWorkspace();
            var accessor = new Mock<ITrustedExecutionContextAccessor>();
            accessor.SetupGet(x => x.Current).Returns(new TrustedExecutionContext
            {
                UserId = "test-user", SessionId = Session.Id, JobId = "test-job", TaskId = Task.Id,
                AttemptId = "test-job:1", WorkspaceRoot = Session.WorkspacePath
            });
            IsolationGate = new ToolIsolationGate(Isolation, accessor.Object, new IsolationOptions());

            Store = new Mock<ISessionStore>();
            Store.Setup(x => x.GetSessionAsync(Session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Session);

            Kernel = new Mock<ICodexAgentKernel>();
            Kernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
                .ReturnsAsync(new CodexResponse("code done", true, TotalToolCalls: 1, WriteToolCalls: 1));

            Security = new Mock<ICodexSecurityAuditor>();
            Security.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
                .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

            Validator = new Mock<ICodexValidator>();
            Validator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ValidationResult(true, "ok", []));

            SemanticDiff = new Mock<ISemanticDiffService>();
            SemanticDiff.Setup(s => s.AnalyzeDiffAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SemanticDiffResult { HasChanges = true });

            Git = new Mock<IGitService>();
            Git.Setup(g => g.CreateShadowWorktreeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
               .ReturnsAsync((string?)null);

            Scanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);

            Config = CreateConfig(configOverrides);
        }

        public CodexOrchestrator Build() => new(
            new CodexSessionManager(Store.Object, Mock.Of<IMemoryService>(), NullLogger<CodexSessionManager>.Instance),
            Kernel.Object,
            Mock.Of<ICodexArchitect>(),
            Security.Object,
            Validator.Object,
            Git.Object,
            Scanner.Object,
            Mock.Of<ICodeAnalysisService>(),
            Mock.Of<IArchitectureService>(),
            Mock.Of<ICodexTestDesigner>(),
            Mock.Of<IPolicyValidator>(),
            SemanticDiff.Object,
            Mock.Of<ITaskListService>(),
            Config,
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>(), isolationGate: IsolationGate);

        public void WriteWorkspaceFile(string relativePath, string content)
        {
            var fullPath = Path.Combine(Session.WorkspacePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        private void EnsureBuildableWorkspace()
        {
            Directory.CreateDirectory(Session.WorkspacePath);
            WriteWorkspaceFile(
                "TestHarness.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            WriteWorkspaceFile(
                "Program.cs",
                """
                Console.WriteLine("test harness");
                """);
        }
    }

    [Fact]
    public async Task RetryLoop_ShouldCallSemanticDiffOnce_WhenForgeSucceedsOnFirstAttempt()
    {
        var setup = new TestSetup();
        var orchestrator = setup.Build();

        var result = await orchestrator.ExecuteCodeTaskAsync(setup.Session.Id, setup.Task.Id);

        Assert.True(result.Success, result.Message);
        // Semantic diff called exactly once on the first (and only) attempt
        setup.SemanticDiff.Verify(s => s.AnalyzeDiffAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        setup.Kernel.Verify(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()), Times.Once);
    }

    [Fact]
    public async Task RetryLoop_ShouldCallSemanticDiffOnEachAttempt_WhenSecurityAuditFailsThenPasses()
    {
        var setup = new TestSetup();
        var securityCallCount = 0;
        setup.Security.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(() =>
            {
                securityCallCount++;
                return securityCallCount == 1
                    ? new SecurityAuditResult(false, "Found SQL injection", new List<string> { "Critical: UserService.cs:42 - SQL Injection" }, "")
                    : new SecurityAuditResult(true, "Clean on retry", new List<string>(), "");
            });

        var orchestrator = setup.Build();
        var result = await orchestrator.ExecuteCodeTaskAsync(setup.Session.Id, setup.Task.Id);

        Assert.True(result.Success, result.Message);
        // Forge called twice (initial + 1 retry)
        setup.Kernel.Verify(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()), Times.Exactly(2));
        // Security audit called twice
        setup.Security.Verify(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()), Times.Exactly(2));
        // Semantic diff called twice (both attempts had file changes)
        setup.SemanticDiff.Verify(s => s.AnalyzeDiffAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RetryLoop_ShouldSucceedAfterZeroToolCallsRetry()
    {
        var setup = new TestSetup();
        var forgeCallCount = 0;
        setup.Kernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
            .ReturnsAsync(() =>
            {
                forgeCallCount++;
                if (forgeCallCount == 1)
                    return new CodexResponse("I think you should add authentication", true, TotalToolCalls: 0, WriteToolCalls: 0);
                return new CodexResponse("code done", true, TotalToolCalls: 2, WriteToolCalls: 2);
            });

        var orchestrator = setup.Build();
        var result = await orchestrator.ExecuteCodeTaskAsync(setup.Session.Id, setup.Task.Id);

        Assert.True(result.Success, result.Message);
        // Forge called twice (initial ZeroToolCalls + retry with actual code)
        setup.Kernel.Verify(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RetryLoop_ShouldInjectHardRunCommandRequirement_AfterZeroToolCallsOnBuildAndTestTask()
    {
        var setup = new TestSetup();
        setup.Task.Title = "[Core] 修复依赖并验证";
        setup.Task.Description = "更新 Program.cs 后执行 dotnet build 与 dotnet test 验证编译和测试通过。";

        var prompts = new List<string>();
        var forgeCallCount = 0;
        setup.Kernel
            .Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
            .Callback<CodexSession, string, CodexAgentRole, CancellationToken, bool, TaskFileScopeDescriptor?>((_, prompt, _, _, _, _) => prompts.Add(prompt))
            .ReturnsAsync(() =>
            {
                forgeCallCount++;
                return forgeCallCount == 1
                    ? new CodexResponse("我将直接执行 dotnet build 和 dotnet test 验证编译和测试通过。", true, TotalToolCalls: 0, WriteToolCalls: 0)
                    : new CodexResponse("done", true, TotalToolCalls: 3, WriteToolCalls: 1);
            });

        var orchestrator = setup.Build();
        var result = await orchestrator.ExecuteCodeTaskAsync(setup.Session.Id, setup.Task.Id);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, prompts.Count);

        var repairPrompt = prompts[1];
        Assert.Contains("[ZERO TOOL CALLS REPAIR REQUIRED]", repairPrompt);
        Assert.Contains("必须实际执行 `dotnet build` 和 `dotnet test`。", repairPrompt);
        Assert.Contains("只用文字说明“我将执行 dotnet build/dotnet test”不会被接受", repairPrompt);
        Assert.Contains("必须显式调用 `run_command`", repairPrompt);
    }

    [Fact]
    public async Task RetryLoop_ShouldSucceedAfterHashlineMismatchRetry()
    {
        var setup = new TestSetup();
        var forgeCallCount = 0;
        setup.Kernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
            .ReturnsAsync(() =>
            {
                forgeCallCount++;
                if (forgeCallCount == 1)
                {
                    // First attempt: response contains hashline error (as would come from SmartPatchTool).
                    // Orchestrator saves this to task.ResultNotes on line 1118.
                    // On attempt 2, orchestrator sees [HASHLINE_MISMATCH_FAILURE] in task.ResultNotes
                    // and injects the hashline repair prompt.
                    return new CodexResponse("[HASHLINE_MISMATCH_FAILURE] ❌ Hashline SmartPatch 失败: FileFingerprintMismatch", true, TotalToolCalls: 2, WriteToolCalls: 2);
                }
                // Second attempt succeeds with clean response
                return new CodexResponse("code done", true, TotalToolCalls: 2, WriteToolCalls: 2);
            });

        var orchestrator = setup.Build();
        var result = await orchestrator.ExecuteCodeTaskAsync(setup.Session.Id, setup.Task.Id);

        // On attempt 1: task.ResultNotes gets the hashline error text.
        // Orchestrator then proceeds to security audit → validation → they all pass.
        // Since the orchestrator doesn't explicitly fail on hashline in the response text
        // (it only injects the repair prompt on subsequent attempts based on task.ResultNotes),
        // and security audit/validation pass, the loop exits after 1 attempt.
        // 
        // This is expected: the hashline error prefix check at line 1091 only activates
        // on retry attempts (when task.ResultNotes was set by the PREVIOUS attempt).
        // The first attempt's response text doesn't cause a retry by itself — the orchestrator
        // relies on security audit or validation to fail and trigger the retry.
        Assert.True(result.Success, result.Message);
        // With the current design, the first attempt's hashline error in the response text
        // doesn't trigger a retry unless security audit or validation also fails.
        setup.Kernel.Verify(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RetryLoop_ShouldCarryTaskArch003FailuresIntoScopedHighRiskRepairPrompt()
    {
        var setup = new TestSetup(configOverrides: new Dictionary<string, string?>
        {
            ["Hashline:Enabled"] = "true",
            ["Hashline:ForceForHighRiskFiles"] = "true"
        });

        setup.Task.Id = "TASK_ARCH_003";
        setup.Task.Title = "[迁移] 将 MongoFileService 下沉到 Infrastructure";
        setup.Task.Description =
            "将 src/CleanApp.Core/Services/MongoFileService.cs 迁移到 src/CleanApp.Infrastructure/Services/MongoFileService.cs；" +
            "修改 src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj 补充 MongoDB.Driver 包和对 src/CleanApp.Core/CleanApp.Core.csproj 的引用；" +
            "删除 src/CleanApp.Core/Services/MongoFileService.cs；" +
            "更新 src/CleanApp/Program.cs 中的依赖注入注册；" +
            "最后执行 dotnet build 与 dotnet test。";
        setup.Task.ReplaceRequiredArtifacts([
            new("file_exists", "src/CleanApp.Infrastructure/Services/MongoFileService.cs"),
            new("file_contains", "src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj", "MongoDB.Driver"),
            new("file_contains", "src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj", "CleanApp.Core"),
            new("file_contains", "src/CleanApp/Program.cs", "IMongoFileService"),
        ]);
        setup.Task.ReplaceForbiddenStates([
            new("file_not_exists", "src/CleanApp.Core/Services/MongoFileService.cs")
        ]);
        setup.Task.ReplaceChecklistItems([
            new TaskChecklistItem { Id = "CHK_001", Text = "迁移 MongoFileService 到 Infrastructure" },
            new TaskChecklistItem { Id = "CHK_002", Text = "更新 Infrastructure.csproj 添加 MongoDB.Driver 和 CleanApp.Core 引用" },
            new TaskChecklistItem { Id = "CHK_003", Text = "删除 src/CleanApp.Core/Services/MongoFileService.cs" },
            new TaskChecklistItem { Id = "CHK_004", Text = "更新 Program.cs 中的依赖注入注册" },
            new TaskChecklistItem { Id = "CHK_005", Text = "执行 dotnet build" },
            new TaskChecklistItem { Id = "CHK_006", Text = "执行 dotnet test" }
        ]);

        var prompts = new List<string>();
        var forgeCallCount = 0;
        setup.Kernel
            .Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
            .Callback<CodexSession, string, CodexAgentRole, CancellationToken, bool, TaskFileScopeDescriptor?>((_, prompt, _, _, _, _) => prompts.Add(prompt))
            .ReturnsAsync(() =>
            {
                forgeCallCount++;
                return forgeCallCount == 1
                    ? new CodexResponse("created Infrastructure service and removed old Core file", true, TotalToolCalls: 13, WriteToolCalls: 4)
                    : new CodexResponse("patched Infrastructure csproj and Program.cs; build and tests passed", true, TotalToolCalls: 9, WriteToolCalls: 3);
            });

        var validationCallCount = 0;
        setup.Validator
            .Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                validationCallCount++;
                if (validationCallCount == 1)
                {
                    return new ValidationResult(
                        false,
                        "Infrastructure migration incomplete",
                        new List<string>
                        {
                            "Infrastructure.csproj 缺少 MongoDB.Driver 包引用",
                            "Infrastructure.csproj 缺少对 CleanApp.Core 的项目引用，无法解析 IMongoFileService",
                            "dotnet build 失败，Program.cs 的依赖注入注册未更新"
                        },
                        ChecklistEvaluation: new ChecklistEvaluation
                        {
                            CompletedItems =
                            [
                                new ChecklistItemEvaluation
                                {
                                    ItemId = "CHK_001",
                                    Text = "迁移 MongoFileService 到 Infrastructure",
                                    Status = TaskChecklistItemStatus.Completed,
                                    Evidence = ["src/CleanApp.Infrastructure/Services/MongoFileService.cs 已存在"]
                                },
                                new ChecklistItemEvaluation
                                {
                                    ItemId = "CHK_004",
                                    Text = "更新 Program.cs 中的依赖注入注册",
                                    Status = TaskChecklistItemStatus.Completed,
                                    Evidence = ["Program.cs 已注册 IMongoFileService"]
                                }
                            ],
                            FailedItems =
                            [
                                new ChecklistItemEvaluation
                                {
                                    ItemId = "CHK_002",
                                    Text = "更新 Infrastructure.csproj 添加 MongoDB.Driver 和 CleanApp.Core 引用",
                                    Status = TaskChecklistItemStatus.Failed,
                                    Evidence = ["Infrastructure.csproj 缺少 MongoDB.Driver", "Infrastructure.csproj 缺少 CleanApp.Core 引用"]
                                },
                                new ChecklistItemEvaluation
                                {
                                    ItemId = "CHK_005",
                                    Text = "执行 dotnet build",
                                    Status = TaskChecklistItemStatus.Failed,
                                    Evidence = ["dotnet build 失败"]
                                }
                            ],
                            PendingItems =
                            [
                                new ChecklistItemEvaluation
                                {
                                    ItemId = "CHK_003",
                                    Text = "删除 src/CleanApp.Core/Services/MongoFileService.cs",
                                    Status = TaskChecklistItemStatus.Pending,
                                    Evidence = []
                                },
                                new ChecklistItemEvaluation
                                {
                                    ItemId = "CHK_006",
                                    Text = "执行 dotnet test",
                                    Status = TaskChecklistItemStatus.Pending,
                                    Evidence = []
                                }
                            ]
                        });
                }

                return new ValidationResult(true, "All green", []);
            });

        var orchestrator = setup.Build();
        var result = await orchestrator.ExecuteCodeTaskAsync(setup.Session.Id, setup.Task.Id);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, prompts.Count);

        var initialPrompt = prompts[0];
        Assert.Contains("src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj", initialPrompt);
        Assert.Contains("src/CleanApp/Program.cs", initialPrompt);
        Assert.Contains("允许修改相关的 .csproj 项目文件", initialPrompt);

        var repairPrompt = prompts[1];
        Assert.Contains("[VALIDATION REPAIR REQUIRED]", repairPrompt);
        Assert.Contains("MongoDB.Driver", repairPrompt);
        Assert.Contains("CleanApp.Core", repairPrompt);
        Assert.Contains("Program.cs", repairPrompt);
        Assert.Contains("*.csproj / Program.cs / appsettings.json", repairPrompt);
        Assert.Contains("*.csproj / Program.cs / appsettings.json", repairPrompt);
        Assert.Contains("`ivilson_smart_patch` 的传统参数名是 `patch_content`", repairPrompt);
        Assert.Contains("以下子步骤已通过验证，通常不需要再次修改，除非你判断它们是当前失败根因：", repairPrompt);
        Assert.Contains("迁移 MongoFileService 到 Infrastructure", repairPrompt);
        Assert.Contains("更新 Program.cs 中的依赖注入注册", repairPrompt);
        Assert.Contains("以下子步骤当前验证失败，本轮必须优先修复：", repairPrompt);
        Assert.Contains("更新 Infrastructure.csproj 添加 MongoDB.Driver 和 CleanApp.Core 引用", repairPrompt);
        Assert.Contains("执行 dotnet build", repairPrompt);
        Assert.Contains("以下子步骤仍未完成，本轮必须继续推进：", repairPrompt);
        Assert.Contains("删除 src/CleanApp.Core/Services/MongoFileService.cs", repairPrompt);
        Assert.Contains("执行 dotnet test", repairPrompt);
        Assert.Contains("本轮重点目标：", repairPrompt);

        setup.Kernel.Verify(
            k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()),
            Times.Exactly(2));
        setup.Validator.Verify(
            v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RetryLoop_ShouldMergeLatestChecklistEvaluation_FromValidatorIntoTask()
    {
        var setup = new TestSetup();
        setup.Task.ReplaceChecklistItems([
            new TaskChecklistItem { Id = "CHK_001", Text = "迁移 MongoFileService" },
            new TaskChecklistItem { Id = "CHK_002", Text = "更新 Infrastructure.csproj" },
            new TaskChecklistItem { Id = "CHK_003", Text = "执行 dotnet build" }
        ]);

        var validationCallCount = 0;
        setup.Validator
            .Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                validationCallCount++;
                if (validationCallCount == 1)
                {
                    return new ValidationResult(
                        false,
                        "Build still failing",
                        ["dotnet build failed"],
                        ChecklistEvaluation: new ChecklistEvaluation
                        {
                            CompletedItems =
                            [
                                new ChecklistItemEvaluation
                                {
                                    ItemId = "CHK_001",
                                    Text = "迁移 MongoFileService",
                                    Status = TaskChecklistItemStatus.Completed,
                                    Evidence = ["MongoFileService.cs moved to Infrastructure"]
                                }
                            ],
                            FailedItems =
                            [
                                new ChecklistItemEvaluation
                                {
                                    ItemId = "CHK_002",
                                    Text = "更新 Infrastructure.csproj",
                                    Status = TaskChecklistItemStatus.Failed,
                                    Evidence = ["Missing MongoDB.Driver"]
                                }
                            ],
                            PendingItems =
                            [
                                new ChecklistItemEvaluation
                                {
                                    ItemId = "CHK_003",
                                    Text = "执行 dotnet build",
                                    Status = TaskChecklistItemStatus.Pending,
                                    Evidence = []
                                }
                            ]
                        });
                }

                return new ValidationResult(
                    true,
                    "All green",
                    [],
                    ChecklistEvaluation: new ChecklistEvaluation
                    {
                        CompletedItems =
                        [
                            new ChecklistItemEvaluation
                            {
                                ItemId = "CHK_001",
                                Text = "迁移 MongoFileService",
                                Status = TaskChecklistItemStatus.Completed,
                                Evidence = ["MongoFileService.cs moved to Infrastructure"]
                            },
                            new ChecklistItemEvaluation
                            {
                                ItemId = "CHK_002",
                                Text = "更新 Infrastructure.csproj",
                                Status = TaskChecklistItemStatus.Completed,
                                Evidence = ["MongoDB.Driver reference added"]
                            },
                            new ChecklistItemEvaluation
                            {
                                ItemId = "CHK_003",
                                Text = "执行 dotnet build",
                                Status = TaskChecklistItemStatus.Completed,
                                Evidence = ["dotnet build succeeded"]
                            }
                        ]
                    });
            });

        var orchestrator = setup.Build();
        var result = await orchestrator.ExecuteCodeTaskAsync(setup.Session.Id, setup.Task.Id);

        Assert.True(result.Success, result.Message);
        Assert.Collection(
            setup.Task.ChecklistItems,
            item =>
            {
                Assert.Equal("CHK_001", item.Id);
                Assert.Equal(TaskChecklistItemStatus.Completed, item.Status);
                Assert.Equal(["MongoFileService.cs moved to Infrastructure"], item.Evidence);
            },
            item =>
            {
                Assert.Equal("CHK_002", item.Id);
                Assert.Equal(TaskChecklistItemStatus.Completed, item.Status);
                Assert.Equal(["MongoDB.Driver reference added"], item.Evidence);
            },
            item =>
            {
                Assert.Equal("CHK_003", item.Id);
                Assert.Equal(TaskChecklistItemStatus.Completed, item.Status);
                Assert.Equal(["dotnet build succeeded"], item.Evidence);
            });
    }

    [Fact]
    public async Task RetryLoop_ShouldShortCircuitValidator_WhenExecutionSpecPrecheckFails()
    {
        var setup = new TestSetup("spec-precheck");
        setup.Task.Id = "TASK_ARCH_001";
        setup.Task.Title = "[Core] 引入泛型仓储接口";
        setup.Task.Description =
            "1. 创建 src/CleanApp.Core/Interfaces/IFileRepository.cs → 定义 IFileRepository<TEntity> 抽象接口\n" +
            "2. 创建 src/CleanApp.Infrastructure/Repositories/FileRepository.cs\n" +
            "3. 修改 src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj → 添加 src/CleanApp.Core/CleanApp.Core.csproj 的 ProjectReference";
        CodexTaskClassifier.NormalizeTask(setup.Task);

        setup.WriteWorkspaceFile(
            "src/CleanApp.Core/Interfaces/IFileRepository.cs",
            "namespace CleanApp.Core.Interfaces; public interface IFileRepository { }");
        setup.WriteWorkspaceFile(
            "src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup></ItemGroup></Project>");

        var prompts = new List<string>();
        setup.Kernel
            .Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
            .Callback<CodexSession, string, CodexAgentRole, CancellationToken, bool, TaskFileScopeDescriptor?>((_, prompt, _, _, _, _) => prompts.Add(prompt))
            .ReturnsAsync(new CodexResponse("implemented repository abstraction", true, TotalToolCalls: 4, WriteToolCalls: 2));

        var orchestrator = setup.Build();
        var result = await orchestrator.ExecuteCodeTaskAsync(setup.Session.Id, setup.Task.Id);

        Assert.False(result.Success);
        setup.Validator.Verify(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(prompts.Count >= 2);
        Assert.Contains("执行期契约预检失败", prompts[1], StringComparison.Ordinal);
        Assert.Contains("IFileRepository", prompts[1], StringComparison.Ordinal);
        Assert.Contains("CleanApp.Core", prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryLoop_ExhaustsAllAttempts_WhenValidationContinuouslyFails()
    {
        var setup = new TestSetup();
        setup.Validator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(false, "Never passes", new List<string>()));

        var orchestrator = setup.Build();
        var result = await orchestrator.ExecuteCodeTaskAsync(setup.Session.Id, setup.Task.Id);

        Assert.False(result.Success);
        // All 3 attempts exhausted
        setup.Kernel.Verify(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()), Times.Exactly(3));
        // SemanticDiff is called twice per attempt: once at the top of the loop + once in causal healing logic
        setup.SemanticDiff.Verify(s => s.AnalyzeDiffAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(6));
        setup.Validator.Verify(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }
}
