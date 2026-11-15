// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Planning.Committee;
using CodexFlow.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests;

public sealed class CommitteePlanningRealLlmSmokeTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "RealProvider")]
    [Trait("Category", "RealLlmSmoke")]
    [Trait("Area", "CommitteePlanning")]
    public async Task CommitteePlanning_RealLlm_Smoke_ProducesFinalPlanContent()
    {
        if (!TryLoadRealApiSettings(out var settings))
        {
            output.WriteLine("未启用委员会真实模型 smoke test。设置 RUN_COMMITTEE_PLANNING_REAL_SMOKE_TESTS=true 后可运行。");
            Assert.Skip("NotRun: committee real-provider gate is off (set RUN_COMMITTEE_PLANNING_REAL_SMOKE_TESTS=true and provide model credentials)");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"committee-real-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var seedBlueprintPath = Path.Combine(tempDir, "seed-blueprint.md");
        await File.WriteAllTextAsync(seedBlueprintPath, CreateSeedBlueprint(), TestContext.Current.CancellationToken);

        try
        {
            using var defaultClient = CreateChatClient(settings);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Planning:CommitteeMaxRounds"] = "1",
                    ["Planning:CommitteeLogRoot"] = ".committee-smoke-logs",
                    ["Planning:CommitteeRoleBindings:Analyst"] = "VllmCommitteeSmoke",
                    ["Planning:CommitteeRoleBindings:Architect"] = "VllmCommitteeSmoke",
                    ["Planning:CommitteeRoleBindings:ProjectManager"] = "VllmCommitteeSmoke",
                    ["VllmCommitteeSmoke:ApiUrl"] = settings.ApiUrl,
                    ["VllmCommitteeSmoke:ApiKey"] = settings.ApiKey,
                    ["VllmCommitteeSmoke:Model"] = settings.Model
                })
                .Build();

            var factory = new CommitteeRoleChatClientFactory(
                configuration,
                defaultClient,
                NullLogger<CommitteeRoleChatClientFactory>.Instance);
            var service = new DefaultCommitteePlanningService(
                factory,
                configuration,
                NullLogger<DefaultCommitteePlanningService>.Instance);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(GetSmokeTimeout());

            var request = new CommitteePlanningRequest
            {
                Goal = "为 ASP.NET Core 后端设计一份审计日志与认证失败告警的实施蓝图，要求覆盖范围、阶段拆分、风险与验证策略。",
                SessionId = "committee-real-llm-smoke",
                WorkspacePath = tempDir,
                ExistingBlueprintPath = seedBlueprintPath,
                ProjectSummary = "现有系统是 ASP.NET Core 单体后端，已有基础认证流程和日志框架，但缺少统一审计日志模型、认证失败告警链路，以及对应的验证策略。请产出可执行的 Markdown 蓝图。"
            };

            var result = await service.RunCommitteePlanningAsync(request, cts.Token);

            output.WriteLine("Committee status: {0}", result.Status);
            output.WriteLine("Committee success: {0}", result.Success);
            output.WriteLine("Committee rounds: {0}", result.TotalRounds);
            output.WriteLine("Committee meeting dir: {0}", result.MeetingDirectory ?? "<null>");
            output.WriteLine("Committee final plan preview: {0}", Preview(result.FinalPlan));

            Assert.NotNull(result.MeetingDirectory);

            var finalPlanPath = Path.Combine(result.MeetingDirectory!, CommitteeConstants.FinalPlanFileName);
            var transcriptPath = Path.Combine(result.MeetingDirectory!, CommitteeConstants.TranscriptFileName);
            var transcript = await File.ReadAllTextAsync(transcriptPath, cts.Token);
            output.WriteLine("Committee transcript preview: {0}", Preview(transcript));

            Assert.True(result.Success, $"Committee planning failed: {result.FailureReason}");
            Assert.True(
                result.Status is CommitteeMeetingStatus.Completed or CommitteeMeetingStatus.MaxRoundsReached,
                $"Unexpected committee status: {result.Status}");
            Assert.False(string.IsNullOrWhiteSpace(result.FinalPlan));
            Assert.True(result.FinalPlan!.Length >= 120, $"Final plan too short: {result.FinalPlan.Length}");
            Assert.Contains("审计", result.FinalPlan, StringComparison.Ordinal);
            Assert.Contains("告警", result.FinalPlan, StringComparison.Ordinal);
            Assert.True(result.TotalRounds >= 1);
            Assert.True(File.Exists(finalPlanPath), "final_plan.md was not created.");
            Assert.True(File.Exists(transcriptPath), "transcript.md was not created.");

            Assert.Contains("Round 1", transcript, StringComparison.Ordinal);
            Assert.Contains("Analyst", transcript, StringComparison.Ordinal);
            Assert.Contains("ProjectManager", transcript, StringComparison.Ordinal);
        }
        finally
        {
            if (!ShouldKeepArtifacts() && Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static bool TryLoadRealApiSettings(out RealApiSettings settings)
    {
        settings = default!;

        var enabled = Environment.GetEnvironmentVariable("RUN_COMMITTEE_PLANNING_REAL_SMOKE_TESTS");
        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(enabled, "1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var apiUrl = Environment.GetEnvironmentVariable("IVILSON_VLLM_API_URL");
        var apiKey = Environment.GetEnvironmentVariable("IVILSON_VLLM_API_KEY");
        var model = Environment.GetEnvironmentVariable("IVILSON_VLLM_MODEL");
        var apiMode = Environment.GetEnvironmentVariable("IVILSON_VLLM_API_MODE");

        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            var configPath = FindRepositoryFile(Path.Combine("CodexFlow", "appsettings.json"));
            if (configPath == null)
            {
                return false;
            }

            using var doc = JsonDocument.Parse(
                File.ReadAllText(configPath),
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            if (!doc.RootElement.TryGetProperty("VllmAgent", out var vllmAgent))
            {
                return false;
            }

            apiUrl ??= vllmAgent.TryGetProperty("ApiUrl", out var apiUrlProp) ? apiUrlProp.GetString() : null;
            apiKey ??= vllmAgent.TryGetProperty("ApiKey", out var apiKeyProp) ? apiKeyProp.GetString() : null;
            model ??= vllmAgent.TryGetProperty("Model", out var modelProp) ? modelProp.GetString() : null;
            apiMode ??= vllmAgent.TryGetProperty("ApiMode", out var apiModeProp) ? apiModeProp.GetString() : null;
        }

        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        settings = new RealApiSettings(apiUrl, apiKey, model, apiMode);
        return true;
    }

    private static IChatClient CreateChatClient(RealApiSettings settings)
        => VllmChatClientFactory.Create(settings.ApiUrl, settings.ApiKey, settings.Model, settings.ApiMode);

    private static string? FindRepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool ShouldKeepArtifacts()
    {
        var raw = Environment.GetEnvironmentVariable("KEEP_COMMITTEE_SMOKE_ARTIFACTS");
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan GetSmokeTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("COMMITTEE_PLANNING_REAL_SMOKE_TIMEOUT_SECONDS");
        if (int.TryParse(raw, out var seconds) && seconds >= 60)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromMinutes(8);
    }

    private static string CreateSeedBlueprint()
    {
        return
            """
            # ASP.NET Core 审计日志与认证失败告警实施蓝图

            ## 目标
            - 建立统一审计日志模型，覆盖登录、登出、认证失败、权限拒绝、关键管理操作。
            - 在认证失败事件出现异常峰值时触发告警，并支持按用户、IP、租户、时间范围查询。

            ## 范围
            - 后端接口、中间件、领域事件、存储模型、告警规则、运营验证脚本。
            - 不包含前端页面改版，仅提供后端查询接口与运维接入说明。

            ## 分阶段实施
            1. 统一事件模型与落库结构。
            2. 接入认证失败与关键操作埋点。
            3. 提供审计日志查询接口与权限控制。
            4. 接入认证失败告警与值班通知。
            5. 增加自动化测试、审计抽样与发布验证。

            ## 风险
            - 高并发下日志写入放大。
            - 敏感字段泄漏。
            - 告警噪声过高导致值班失效。

            ## 验证
            - 集成测试覆盖认证成功/失败、权限拒绝、查询权限边界。
            - 发布前执行告警联调、压测与抽样核对。
            """;
    }

    private static string Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<empty>";
        }

        var normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 200 ? normalized : normalized[..200] + "...";
    }

    private sealed record RealApiSettings(string ApiUrl, string ApiKey, string Model, string? ApiMode);
}
