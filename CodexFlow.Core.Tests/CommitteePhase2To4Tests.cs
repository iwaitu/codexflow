// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Committee;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexFlow.Core.Tests;

/// <summary>
/// Phase 2: Prompt templates, role client factory, meeting artifacts
/// Phase 3: JSON parser, multi-round loop, moderator rules
/// Phase 4: Plan projection, validation, shadow diff
/// </summary>
public class CommitteePhase2To4Tests
{
    #region Phase 2: CommitteePrompts

    [Fact]
    public void Fill_ReplacesPlaceholders()
    {
        var template = "Hello {name}, welcome to {project}.";
        var result = CommitteePrompts.Fill(template, new Dictionary<string, string>
        {
            ["name"] = "Alice",
            ["project"] = "Codex"
        });
        Assert.Equal("Hello Alice, welcome to Codex.", result);
    }

    [Fact]
    public void Fill_ThrowsOnNullTemplate()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CommitteePrompts.Fill(null!, new Dictionary<string, string>()));
    }

    [Fact]
    public void Fill_ThrowsOnNullVariables()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CommitteePrompts.Fill("template", null!));
    }

    [Fact]
    public void Fill_LeavesUnmatchedPlaceholders()
    {
        var result = CommitteePrompts.Fill("Hello {name}, {unknown}.",
            new Dictionary<string, string> { ["name"] = "Bob" });
        Assert.Equal("Hello Bob, {unknown}.", result);
    }

    [Fact]
    public void PromptTemplates_ContainExpectedPlaceholders()
    {
        Assert.Contains("{goal}", CommitteePrompts.InitialBlueprintPrompt);
        Assert.Contains("{project_summary}", CommitteePrompts.InitialBlueprintPrompt);
        Assert.Contains("{current_plan}", CommitteePrompts.AnalystReviewerPrompt);
        Assert.Contains("{review_history}", CommitteePrompts.AnalystReviewerPrompt);
        Assert.Contains("{pending_issues}", CommitteePrompts.AnalystReviewerPrompt);
        Assert.Contains("{current_plan}", CommitteePrompts.ArchitectReviewerPrompt);
        Assert.Contains("{analyst_feedback}", CommitteePrompts.ProjectManagerModeratorPrompt);
        Assert.Contains("{architect_feedback}", CommitteePrompts.ProjectManagerModeratorPrompt);
        Assert.Contains("{raw_text}", CommitteePrompts.JsonRepairPrompt);
        Assert.Contains("{final_plan}", CommitteePrompts.PlanProjectionPrompt);
    }

    #endregion

    #region Phase 2: MeetingArtifactService

    [Fact]
    public void InitializeMeetingDirectory_CreatesExpectedStructure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"committee-test-{Guid.NewGuid():N}");
        try
        {
            var state = new CommitteeMeetingState
            {
                MeetingId = "test-meeting",
                Goal = "test goal",
                StartedAt = new DateTime(2026, 3, 19, 10, 0, 0, DateTimeKind.Utc)
            };

            var service = new MeetingArtifactService(NullLogger.Instance);
            var meetingDir = service.InitializeMeetingDirectory(tempDir, ".logs", state);

            Assert.True(Directory.Exists(meetingDir));
            Assert.True(Directory.Exists(Path.Combine(meetingDir, "runtime")));
            Assert.True(File.Exists(Path.Combine(meetingDir, "meeting.json")));
            Assert.True(File.Exists(Path.Combine(meetingDir, "transcript.md")));
            Assert.True(File.Exists(Path.Combine(meetingDir, "runtime", "review_history.json")));
            Assert.Equal(meetingDir, state.MeetingDirectory);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void WriteInitialBlueprint_WritesFileAndUpdatesCurrentPlan()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"committee-test-{Guid.NewGuid():N}");
        try
        {
            var state = CreateStateWithDirectory(tempDir);
            var service = new MeetingArtifactService(NullLogger.Instance);

            service.WriteInitialBlueprint(state, "# Blueprint Content");

            var blueprintPath = Path.Combine(state.MeetingDirectory, "initial_blueprint.md");
            Assert.True(File.Exists(blueprintPath));
            Assert.Equal("# Blueprint Content", File.ReadAllText(blueprintPath));
            Assert.Equal("# Blueprint Content", state.CurrentPlan);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void WriteFinalPlan_WritesFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"committee-test-{Guid.NewGuid():N}");
        try
        {
            var state = CreateStateWithDirectory(tempDir);
            var service = new MeetingArtifactService(NullLogger.Instance);

            service.WriteFinalPlan(state, "# Final Plan");

            var path = Path.Combine(state.MeetingDirectory, "final_plan.md");
            Assert.True(File.Exists(path));
            Assert.Equal("# Final Plan", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AppendTranscript_AppendsToFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"committee-test-{Guid.NewGuid():N}");
        try
        {
            var state = CreateStateWithDirectory(tempDir);
            var service = new MeetingArtifactService(NullLogger.Instance);

            // Create initial transcript
            File.WriteAllText(Path.Combine(state.MeetingDirectory, "transcript.md"), "");

            service.AppendTranscript(state, "Entry 1");
            service.AppendTranscript(state, "Entry 2");

            var content = File.ReadAllText(Path.Combine(state.MeetingDirectory, "transcript.md"));
            Assert.Contains("Entry 1", content);
            Assert.Contains("Entry 2", content);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void WriteMeetingJson_SerializesState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"committee-test-{Guid.NewGuid():N}");
        try
        {
            var state = CreateStateWithDirectory(tempDir);
            state.MeetingId = "test-123";
            state.CurrentRound = 2;

            var service = new MeetingArtifactService(NullLogger.Instance);
            service.WriteMeetingJson(state);

            var json = File.ReadAllText(Path.Combine(state.MeetingDirectory, "meeting.json"));
            Assert.Contains("test-123", json);
            Assert.Contains("\"current_round\": 2", json);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void WriteAnalystFeedback_WritesRoundFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"committee-test-{Guid.NewGuid():N}");
        try
        {
            var state = CreateStateWithDirectory(tempDir);
            var service = new MeetingArtifactService(NullLogger.Instance);
            var feedback = new ReviewerFeedback { Status = "approve", Summary = "All good" };

            service.WriteAnalystFeedback(state, 1, feedback);

            var path = Path.Combine(state.MeetingDirectory, "round-01-analyst-feedback.md");
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.Contains("approve", content);
            Assert.Contains("All good", content);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    #endregion

    #region Phase 3: CommitteeJsonParser

    [Fact]
    public void CleanJsonResponse_StripsMarkdownCodeBlock()
    {
        Assert.Equal("{\"a\":1}", CommitteeJsonParser.CleanJsonResponse("```json\n{\"a\":1}\n```"));
        Assert.Equal("{\"a\":1}", CommitteeJsonParser.CleanJsonResponse("```\n{\"a\":1}\n```"));
        Assert.Equal("{\"a\":1}", CommitteeJsonParser.CleanJsonResponse("  {\"a\":1}  "));
    }

    [Fact]
    public void CleanJsonResponse_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CommitteeJsonParser.CleanJsonResponse(""));
        Assert.Equal(string.Empty, CommitteeJsonParser.CleanJsonResponse("   "));
    }

    [Fact]
    public void ParseReviewerFeedback_ValidJson_ReturnsFeedback()
    {
        var parser = new CommitteeJsonParser(NullLogger.Instance);
        var json = JsonConvert.SerializeObject(new
        {
            status = "approve",
            summary = "Looks good",
            blocking_items = Array.Empty<string>(),
            non_blocking_items = new[] { "Consider adding tests" }
        });

        var result = parser.ParseReviewerFeedback(json);

        Assert.NotNull(result);
        Assert.Equal("approve", result.Status);
        Assert.Equal("Looks good", result.Summary);
        Assert.Empty(result.BlockingItems);
        Assert.Single(result.NonBlockingItems);
    }

    [Fact]
    public void ParseReviewerFeedback_WithMarkdownWrapper_StillParses()
    {
        var parser = new CommitteeJsonParser(NullLogger.Instance);
        var json = "```json\n" + JsonConvert.SerializeObject(new
        {
            status = "changes_requested",
            summary = "Need fixes",
            blocking_items = new[] { "Missing validation" }
        }) + "\n```";

        var result = parser.ParseReviewerFeedback(json);

        Assert.NotNull(result);
        Assert.Equal("changes_requested", result.Status);
    }

    [Fact]
    public void ParseReviewerFeedback_InvalidJson_ReturnsNull()
    {
        var parser = new CommitteeJsonParser(NullLogger.Instance);
        var result = parser.ParseReviewerFeedback("this is not json at all");
        Assert.Null(result);
    }

    [Fact]
    public void ParseReviewerFeedback_JsonWithExtraText_ExtractsObject()
    {
        var parser = new CommitteeJsonParser(NullLogger.Instance);
        var json = "Here is my feedback: {\"status\":\"approve\",\"summary\":\"OK\"} end of feedback.";

        var result = parser.ParseReviewerFeedback(json);

        Assert.NotNull(result);
        Assert.Equal("approve", result.Status);
    }

    [Fact]
    public void ParseModeratorDecision_ValidJson_ReturnsDecision()
    {
        var parser = new CommitteeJsonParser(NullLogger.Instance);
        var json = JsonConvert.SerializeObject(new
        {
            decision = "finalize",
            reason = "Both approved",
            gpt_feedback = "No issues",
            unresolved_items = Array.Empty<string>(),
            updated_plan = "# Final Plan"
        });

        var result = parser.ParseModeratorDecision(json);

        Assert.NotNull(result);
        Assert.Equal("finalize", result.Decision);
        Assert.True(result.IsFinalize);
        Assert.Equal("# Final Plan", result.UpdatedPlan);
    }

    [Fact]
    public void ParseModeratorDecision_ContinueDecision_IsNotFinalize()
    {
        var parser = new CommitteeJsonParser(NullLogger.Instance);
        var json = "{\"decision\":\"continue\",\"reason\":\"blocking items\",\"unresolved_items\":[\"item1\"]}";

        var result = parser.ParseModeratorDecision(json);

        Assert.NotNull(result);
        Assert.False(result.IsFinalize);
        Assert.Single(result.UnresolvedItems);
    }

    [Fact]
    public void ParseModeratorDecision_InvalidJson_ReturnsNull()
    {
        var parser = new CommitteeJsonParser(NullLogger.Instance);
        Assert.Null(parser.ParseModeratorDecision("garbage text"));
    }

    #endregion

    #region Phase 3: Moderator Rule Enforcement

    [Fact]
    public void ReviewerFeedback_IsApproved_TrueWhenApproveAndNoBlockingItems()
    {
        var feedback = new ReviewerFeedback { Status = "approve", BlockingItems = [] };
        Assert.True(feedback.IsApproved);
    }

    [Fact]
    public void ReviewerFeedback_IsApproved_FalseWhenApproveButHasBlockingItems()
    {
        var feedback = new ReviewerFeedback { Status = "approve", BlockingItems = ["issue"] };
        Assert.False(feedback.IsApproved);
    }

    [Fact]
    public void ReviewerFeedback_IsApproved_FalseWhenChangesRequested()
    {
        var feedback = new ReviewerFeedback { Status = "changes_requested", BlockingItems = [] };
        Assert.False(feedback.IsApproved);
    }

    [Fact]
    public void ModeratorDecision_IsFinalize_CorrectlyIdentifiesFinalize()
    {
        Assert.True(new ModeratorDecision { Decision = "finalize" }.IsFinalize);
        Assert.True(new ModeratorDecision { Decision = "Finalize" }.IsFinalize);
        Assert.False(new ModeratorDecision { Decision = "continue" }.IsFinalize);
    }

    [Fact]
    public async Task RunCommitteePlanningAsync_BothApproveWithModeratorRewrite_FinalizesApprovedPlan()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"committee-service-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var blueprintPath = Path.Combine(tempDir, "seed-plan.md");
            await File.WriteAllTextAsync(blueprintPath, "# Approved Plan v1", CancellationToken.None);

            var analystClient = new Mock<IChatClient>();
            analystClient
                .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    "{\"status\":\"approve\",\"summary\":\"ok\",\"blocking_items\":[],\"non_blocking_items\":[],\"resolved_previous_items\":[],\"issue_statuses\":[]}")));

            var architectClient = new Mock<IChatClient>();
            architectClient
                .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    "{\"status\":\"approve\",\"summary\":\"ok\",\"blocking_items\":[],\"non_blocking_items\":[],\"resolved_previous_items\":[],\"issue_statuses\":[]}")));

            var moderatorClient = new Mock<IChatClient>();
            moderatorClient
                .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    "{\"decision\":\"continue\",\"reason\":\"apply polish\",\"gpt_feedback\":\"rewrote plan\",\"unresolved_items\":[],\"updated_plan\":\"# Rewritten Plan v2\"}")));

            var factory = new Mock<ICommitteeRoleChatClientFactory>();
            factory.Setup(x => x.GetClientForRole("Analyst")).Returns(analystClient.Object);
            factory.Setup(x => x.GetClientForRole("Architect")).Returns(architectClient.Object);
            factory.Setup(x => x.GetClientForRole("ProjectManager")).Returns(moderatorClient.Object);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Planning:CommitteeMaxRounds"] = "3",
                    ["Planning:CommitteeLogRoot"] = ".committee-tests"
                })
                .Build();

            var service = new DefaultCommitteePlanningService(
                factory.Object,
                config,
                NullLogger<DefaultCommitteePlanningService>.Instance);

            var result = await service.RunCommitteePlanningAsync(new CommitteePlanningRequest
            {
                Goal = "test",
                SessionId = "session-1",
                WorkspacePath = tempDir,
                ExistingBlueprintPath = blueprintPath,
                ProjectSummary = "summary"
            });

            Assert.True(result.Success);
            Assert.Equal(CommitteeMeetingStatus.Completed, result.Status);
            Assert.Equal(1, result.TotalRounds);
            Assert.Equal("# Approved Plan v1", result.FinalPlan);

            var finalPlanPath = Path.Combine(result.MeetingDirectory!, CommitteeConstants.FinalPlanFileName);
            Assert.True(File.Exists(finalPlanPath));
            Assert.Equal("# Approved Plan v1", File.ReadAllText(finalPlanPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    #endregion

    #region Phase 4: PlanProjectionService - Validation

    [Fact]
    public void Validate_EmptyList_ReturnsInvalid()
    {
        var service = new PlanProjectionService(NullLogger.Instance);
        var result = service.Validate([]);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("为空"));
    }

    [Fact]
    public void Validate_ValidTasks_ReturnsValid()
    {
        var service = new PlanProjectionService(NullLogger.Instance);
        var tasks = new List<ProjectedTask>
        {
            new()
            {
                Id = "task-01", Title = "任务一", Description = "描述一",
                TaskType = "code", RiskLevel = "Low", ComplexityLevel = 2,
                VerificationSteps = ["运行单元测试"]
            },
            new()
            {
                Id = "task-02", Title = "任务二", Description = "描述二",
                TaskType = "analysis", RiskLevel = "Medium", ComplexityLevel = 1,
                Dependencies = ["task-01"],
                VerificationSteps = ["检查分析报告"]
            }
        };

        var result = service.Validate(tasks);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_DuplicateIds_ReturnsInvalid()
    {
        var service = new PlanProjectionService(NullLogger.Instance);
        var tasks = new List<ProjectedTask>
        {
            new() { Id = "task-01", Title = "A", Description = "D", VerificationSteps = ["v1"] },
            new() { Id = "task-01", Title = "B", Description = "D", VerificationSteps = ["v2"] }
        };

        var result = service.Validate(tasks);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("重复"));
    }

    [Fact]
    public void Validate_MissingRequiredFields_ReturnsErrors()
    {
        var service = new PlanProjectionService(NullLogger.Instance);
        var tasks = new List<ProjectedTask>
        {
            new() { Id = "", Title = "", Description = "", VerificationSteps = ["v1"] }
        };

        var result = service.Validate(tasks);
        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 3); // missing id, title, description
    }

    [Fact]
    public void Validate_InvalidDependencyReference_ReturnsError()
    {
        var service = new PlanProjectionService(NullLogger.Instance);
        var tasks = new List<ProjectedTask>
        {
            new()
            {
                Id = "task-01", Title = "T", Description = "D",
                Dependencies = ["task-99"],
                VerificationSteps = ["v"]
            }
        };

        var result = service.Validate(tasks);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("引用无效"));
    }

    [Fact]
    public void Validate_MissingVerificationSteps_ReturnsError()
    {
        var service = new PlanProjectionService(NullLogger.Instance);
        var tasks = new List<ProjectedTask>
        {
            new() { Id = "task-01", Title = "T", Description = "D", VerificationSteps = [] }
        };

        var result = service.Validate(tasks);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("验证步骤"));
    }

    [Fact]
    public void Validate_NonExecutableVerificationStep_ReturnsError()
    {
        var service = new PlanProjectionService(NullLogger.Instance);
        var tasks = new List<ProjectedTask>
        {
            new()
            {
                Id = "task-01", Title = "T", Description = "D",
                VerificationSteps = ["手动验证功能是否正确"]
            }
        };

        var result = service.Validate(tasks);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("不可执行"));
    }

    [Fact]
    public void Validate_NonStandardTaskType_WarnsButValid()
    {
        var service = new PlanProjectionService(NullLogger.Instance);
        var tasks = new List<ProjectedTask>
        {
            new()
            {
                Id = "task-01", Title = "T", Description = "D",
                TaskType = "review", RiskLevel = "Low",
                VerificationSteps = ["运行测试"]
            }
        };

        var result = service.Validate(tasks);
        Assert.True(result.IsValid);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Validate_ComplexityOutOfRange_Warns()
    {
        var service = new PlanProjectionService(NullLogger.Instance);
        var tasks = new List<ProjectedTask>
        {
            new()
            {
                Id = "task-01", Title = "T", Description = "D",
                ComplexityLevel = 5,
                VerificationSteps = ["运行测试"]
            }
        };

        var result = service.Validate(tasks);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("complexity_level"));
    }

    #endregion

    #region Phase 4: ToCodexTasks

    [Fact]
    public void ToCodexTasks_ConvertsProjectedTasksToCodexTasks()
    {
        var projected = new List<ProjectedTask>
        {
            new()
            {
                Id = "task-01", Title = "实现登录功能", Description = "创建登录页面",
                TaskType = "code", RiskLevel = "High", ComplexityLevel = 3,
                Dependencies = [],
                VerificationSteps = ["运行登录测试", "验证 JWT 生成"],
                RequiredArtifacts = [new ArtifactAssertion("file_exists", "src/App/Login.cs")]
            }
        };

        var tasks = PlanProjectionService.ToCodexTasks(projected);

        Assert.Single(tasks);
        var task = tasks[0];
        Assert.Equal("task-01", task.Id);
        Assert.Equal("实现登录功能", task.Title);
        Assert.Equal("High", task.RiskLevel);
        Assert.Equal(3, task.ComplexityLevel);
        Assert.Contains(task.RequiredArtifacts, assertion => assertion.Path == "src/App/Login.cs");
        Assert.Contains("验证步骤", task.Description);
        Assert.Contains("运行登录测试", task.Description);
    }

    [Fact]
    public void ToCodexTasks_ClampsComplexityLevel()
    {
        var projected = new List<ProjectedTask>
        {
            new()
            {
                Id = "task-01", Title = "T", Description = "D",
                ComplexityLevel = 10,
                VerificationSteps = ["v"]
            }
        };

        var tasks = PlanProjectionService.ToCodexTasks(projected);
        Assert.Equal(3, tasks[0].ComplexityLevel);
    }

    [Fact]
    public void ToCodexTasks_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => PlanProjectionService.ToCodexTasks(null!));
    }

    #endregion

    #region Phase 4: GenerateShadowDiff

    [Fact]
    public void GenerateShadowDiff_ProducesExpectedStructure()
    {
        var baseline = new List<CodexFlow.Core.Models.CodexTask>
        {
            new() { Id = "task-01", Title = "A", Description = "desc" },
            new() { Id = "task-02", Title = "B", Description = "desc\n\n### 验证步骤\n1. dotnet test A", RiskLevel = "Low" }
        };
        baseline[1].ReplaceDependencies(["task-01"]);

        var committee = new List<CodexFlow.Core.Models.CodexTask>
        {
            new() { Id = "task-02", Title = "B", Description = "desc\n\n### 验证步骤\n1. dotnet test B", RiskLevel = "High" },
            new() { Id = "task-03", Title = "C", Description = "desc" }
        };
        committee[0].ReplaceDependencies(["task-03"]);

        var diff = PlanProjectionService.GenerateShadowDiff(baseline, committee);
        var json = JsonConvert.SerializeObject(diff);

        Assert.Contains("baseline_task_count", json);
        Assert.Contains("committee_task_count", json);
        Assert.Contains("task_diffs", json);
        Assert.Contains("dependency_diffs", json);
        Assert.Contains("validation_diffs", json);
        Assert.Contains("only_in_baseline", json);
        Assert.Contains("only_in_committee", json);
    }

    [Fact]
    public void GenerateShadowDiff_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PlanProjectionService.GenerateShadowDiff(null!, []));
        Assert.Throws<ArgumentNullException>(() =>
            PlanProjectionService.GenerateShadowDiff([], null!));
    }

    #endregion

    #region Phase 2: RoleConfiguredChatClient (via factory)

    [Fact]
    public void CommitteeRoleChatClientFactory_UnknownRole_ReturnsFallback()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var mockDefault = Mock.Of<IChatClient>();
        var factory = new CommitteeRoleChatClientFactory(
            config, mockDefault,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CommitteeRoleChatClientFactory>.Instance);

        var client = factory.GetClientForRole("UnknownRole");
        Assert.Same(mockDefault, client);
    }

    [Fact]
    public void CommitteeRoleChatClientFactory_MissingSectionConfig_ReturnsFallback()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Planning:CommitteeRoleBindings:Analyst"] = "VllmAnalyst"
                // But VllmAnalyst section doesn't exist
            })
            .Build();

        var mockDefault = Mock.Of<IChatClient>();
        var factory = new CommitteeRoleChatClientFactory(
            config, mockDefault,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CommitteeRoleChatClientFactory>.Instance);

        var client = factory.GetClientForRole("Analyst");
        Assert.Same(mockDefault, client);
    }

    [Fact]
    public void CommitteeRoleChatClientFactory_IncompleteConfig_ReturnsFallback()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Planning:CommitteeRoleBindings:Analyst"] = "VllmAnalyst",
                ["VllmAnalyst:ApiUrl"] = "http://localhost:8080"
                // Missing ApiKey and Model
            })
            .Build();

        var mockDefault = Mock.Of<IChatClient>();
        var factory = new CommitteeRoleChatClientFactory(
            config, mockDefault,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CommitteeRoleChatClientFactory>.Instance);

        var client = factory.GetClientForRole("Analyst");
        Assert.Same(mockDefault, client);
    }

    [Fact]
    public void CommitteeRoleChatClientFactory_CompleteConfig_ReturnsRoleClient()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Planning:CommitteeRoleBindings:Analyst"] = "VllmAnalyst",
                ["VllmAnalyst:ApiUrl"] = "http://localhost:8080",
                ["VllmAnalyst:ApiKey"] = "test-key",
                ["VllmAnalyst:Model"] = "qwen3.5-plus",
                ["VllmAnalyst:Temperature"] = "0.2"
            })
            .Build();

        var mockDefault = Mock.Of<IChatClient>();
        var factory = new CommitteeRoleChatClientFactory(
            config, mockDefault,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CommitteeRoleChatClientFactory>.Instance);

        var client = factory.GetClientForRole("Analyst");
        Assert.NotSame(mockDefault, client); // Should be a role-specific client
    }

    [Fact]
    public void CommitteeRoleChatClientFactory_CaseInsensitiveRoleLookup()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Planning:CommitteeRoleBindings:analyst"] = "VllmAnalyst",
                ["VllmAnalyst:ApiUrl"] = "http://localhost:8080",
                ["VllmAnalyst:ApiKey"] = "test-key",
                ["VllmAnalyst:Model"] = "qwen3.5-plus"
            })
            .Build();

        var mockDefault = Mock.Of<IChatClient>();
        var factory = new CommitteeRoleChatClientFactory(
            config, mockDefault,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CommitteeRoleChatClientFactory>.Instance);

        var client = factory.GetClientForRole("ANALYST");
        Assert.NotSame(mockDefault, client);
    }

    #endregion

    #region Phase 3: CommitteeJsonParser RepairJsonAsync

    [Fact]
    public async Task RepairJsonAsync_ThrowsOnNullClient()
    {
        var parser = new CommitteeJsonParser(NullLogger.Instance);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            parser.RepairJsonAsync("bad json", null!, CancellationToken.None));
    }

    #endregion

    #region Fix: Reject keyword priority

    [Theory]
    [InlineData("不同意")]
    [InlineData("不要启动")]
    [InlineData("别开始")]
    [InlineData("不好")]
    [InlineData("disagree")]
    [InlineData("no")]
    [InlineData("skip")]
    public void RejectKeywords_MatchBeforeAcceptSubstrings(string input)
    {
        var rejected = CommitteeConstants.RejectKeywords
            .Any(k => input.Contains(k, StringComparison.OrdinalIgnoreCase));
        Assert.True(rejected, $"Expected '{input}' to match a reject keyword");
    }

    [Theory]
    [InlineData("同意")]
    [InlineData("好的")]
    [InlineData("yes")]
    [InlineData("OK")]
    public void AcceptKeywords_NotBlockedByReject(string input)
    {
        var rejected = CommitteeConstants.RejectKeywords
            .Any(k => input.Contains(k, StringComparison.OrdinalIgnoreCase));
        var accepted = !rejected && CommitteeConstants.AcceptKeywords
            .Any(k => input.Contains(k, StringComparison.OrdinalIgnoreCase));
        Assert.True(accepted, $"Expected '{input}' to be accepted");
    }

    #endregion

    #region Fix: WriteShadowMetrics

    [Fact]
    public void WriteShadowMetrics_CreatesMetricsFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"committee-test-{Guid.NewGuid():N}");
        try
        {
            var state = CreateStateWithDirectory(tempDir);
            var service = new MeetingArtifactService(NullLogger.Instance);

            var baseline = new List<CodexTask>
            {
                new() { Id = "t1", Title = "A", RiskLevel = "High", ComplexityLevel = 3 },
                new() { Id = "t2", Title = "B", RiskLevel = "Low", ComplexityLevel = 1 }
            };
            var committee = new List<CodexTask>
            {
                new() { Id = "t1", Title = "A", RiskLevel = "Medium", ComplexityLevel = 2 },
                new() { Id = "t3", Title = "C", RiskLevel = "Low", ComplexityLevel = 1 },
                new() { Id = "t4", Title = "D", RiskLevel = "High", ComplexityLevel = 3 }
            };
            var committeeResult = new CommitteePlanningResult
            {
                MeetingId = "meeting-shadow-123",
                Success = true,
                Status = CommitteeMeetingStatus.Completed,
                TotalRounds = 3,
                UnresolvedItems = ["item1"]
            };

            service.WriteShadowMetrics(state, baseline, committee, committeeResult);

            var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.ShadowMetricsFileName);
            Assert.True(File.Exists(path));

            var json = JObject.Parse(File.ReadAllText(path));
            Assert.Equal(2, json["baseline"]!["task_count"]!.Value<int>());
            Assert.Equal(3, json["committee"]!["task_count"]!.Value<int>());
            Assert.Equal(1, json["baseline"]!["high_risk_count"]!.Value<int>());
            Assert.Equal(1, json["committee"]!["high_risk_count"]!.Value<int>());
            Assert.True(json["projection_succeeded"]!.Value<bool>());
            Assert.Equal(3, json["committee_total_rounds"]!.Value<int>());
            Assert.Equal("meeting-shadow-123", json["meeting_id"]!.Value<string>());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void WriteShadowMetrics_NullCommitteePlan_WritesProjectionFailed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"committee-test-{Guid.NewGuid():N}");
        try
        {
            var state = CreateStateWithDirectory(tempDir);
            var service = new MeetingArtifactService(NullLogger.Instance);

            var committeeResult = new CommitteePlanningResult
            {
                Success = true,
                Status = CommitteeMeetingStatus.Completed,
                TotalRounds = 2
            };

            service.WriteShadowMetrics(state, [], null, committeeResult);

            var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.ShadowMetricsFileName);
            var json = JObject.Parse(File.ReadAllText(path));
            Assert.False(json["projection_succeeded"]!.Value<bool>());
            Assert.Equal(JTokenType.Null, json["committee"]!.Type);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    #endregion

    #region Fix: meeting.json enriched schema

    [Fact]
    public void WriteMeetingJson_ContainsRoundsEventsAndArtifacts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"committee-test-{Guid.NewGuid():N}");
        try
        {
            var state = CreateStateWithDirectory(tempDir);
            state.MeetingId = "enriched-test";
            state.Goal = "test enriched json";
            state.CurrentRound = 2;
            state.Rounds.Add(new CommitteeRoundRecord
            {
                RoundNumber = 1,
                AnalystFeedback = new ReviewerFeedback { Status = "changes_requested", Summary = "Need changes", BlockingItems = ["issue1"] },
                ArchitectFeedback = new ReviewerFeedback { Status = "approve", Summary = "Looks good" },
                ModeratorDecision = new ModeratorDecision { Decision = "continue", Reason = "Blocking items", UnresolvedItems = ["issue1"] }
            });
            state.Rounds.Add(new CommitteeRoundRecord
            {
                RoundNumber = 2,
                AnalystFeedback = new ReviewerFeedback { Status = "approve", Summary = "All resolved" },
                ArchitectFeedback = new ReviewerFeedback { Status = "approve", Summary = "Good" },
                ModeratorDecision = new ModeratorDecision { Decision = "finalize", Reason = "Both approved" }
            });

            // Write some artifact files so artifacts index is populated
            File.WriteAllText(Path.Combine(state.MeetingDirectory, "transcript.md"), "test");
            File.WriteAllText(Path.Combine(state.MeetingDirectory, "initial_blueprint.md"), "test");

            var service = new MeetingArtifactService(NullLogger.Instance);
            service.WriteMeetingJson(state);

            var json = JObject.Parse(File.ReadAllText(Path.Combine(state.MeetingDirectory, "meeting.json")));

            // Verify rounds
            var rounds = json["rounds"] as JArray;
            Assert.NotNull(rounds);
            Assert.Equal(2, rounds.Count);
            Assert.Equal("changes_requested", rounds[0]!["analyst"]!["Status"]!.Value<string>());
            Assert.Equal("finalize", rounds[1]!["moderator"]!["Decision"]!.Value<string>());

            // Verify events
            var events = json["events"] as JArray;
            Assert.NotNull(events);
            Assert.True(events.Count >= 2); // at least meeting_started + round events

            // Verify artifacts
            var artifacts = json["artifacts"] as JArray;
            Assert.NotNull(artifacts);
            Assert.True(artifacts.Count >= 2); // at least transcript + initial_blueprint + meeting.json itself
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    #endregion

    #region Fix: Model matrix alignment

    [Theory]
    [InlineData("openai/gpt-oss-120b")]
    [InlineData("openai/gpt-5.3-codex")]
    [InlineData("google/gemini-3.1-pro-preview")]
    [InlineData("anthropic/claude-opus-4.6")]
    [InlineData("glm-5")]
    [InlineData("kimi-k2.5")]
    [InlineData("minimax-m2.5")]
    [InlineData("qwen3.5-plus")]
    public void CommitteeRoleChatClientFactory_SupportsAllProgramModels(string model)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Planning:CommitteeRoleBindings:Analyst"] = "VllmAnalyst",
                ["VllmAnalyst:ApiUrl"] = "http://localhost:8080",
                ["VllmAnalyst:ApiKey"] = "test-key",
                ["VllmAnalyst:Model"] = model
            })
            .Build();

        var mockDefault = Mock.Of<IChatClient>();
        var factory = new CommitteeRoleChatClientFactory(
            config, mockDefault,
            NullLogger<CommitteeRoleChatClientFactory>.Instance);

        var client = factory.GetClientForRole("Analyst");
        Assert.NotSame(mockDefault, client); // Must not fall back to default
    }

    #endregion

    #region Helpers

    private static CommitteeMeetingState CreateStateWithDirectory(string tempDir)
    {
        var meetingDir = Path.Combine(tempDir, "meeting-test");
        Directory.CreateDirectory(meetingDir);
        Directory.CreateDirectory(Path.Combine(meetingDir, "runtime"));

        return new CommitteeMeetingState
        {
            MeetingId = "test-meeting",
            Goal = "test goal",
            MeetingDirectory = meetingDir
        };
    }

    #endregion
}
