// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

/// <summary>
/// 真实 API 多轮 A/B/C 策略对比基准测试
///
/// 需要环境变量 RUN_REAL_LLM_TOKEN_TESTS=true 启用。
/// 从 appsettings.json 或环境变量读取 API 配置。
///
/// 对比策略：
/// A: 全量 catalog + 全量结构化 tools（每轮都注入所有工具 schema）
/// B: discovery hint + 最小常驻工具（每轮重建，tool_search 激活后增量注入）
/// C: 无 catalog + 最小常驻工具（同 B 但不注入 discovery hint）
///
/// 统计指标：
/// - 每轮 InputTokenCount（真实 API 返回）
/// - 全流程累计 InputTokenCount
/// - 任务成功率（模型是否产出非空/有效回复）
/// - 平均轮次
///
/// 测试链路：
/// 1. Gateway: 分析 → 规划 → 执行 → 总结
/// 2. Controller: 读文件 → 搜索 → tool_search 激活 git → 使用 git → 总结
/// </summary>
public sealed class MultiRoundRealApiBenchmarkTests(ITestOutputHelper output)
{
    /// <summary>
    /// Gateway 链路：analyze_project → generate_dev_plan → start_next_task → 总结
    /// </summary>
    [Fact]
    [Trait("Category", "RealProvider")]
    public async Task Gateway_MultiRound_RealApi_ABC_Comparison()
    {
        if (!TryLoadRealApiSettings(out var settings))
        {
            output.WriteLine("\n========== Gateway 多轮 A/B/C 对比（未执行） ==========");
            output.WriteLine("未启用真实模型测试。设置 RUN_REAL_LLM_TOKEN_TESTS=true 后可运行。");
            output.WriteLine("======================================================\n");
            Assert.Skip("NotRun: real-provider gate is off (set RUN_REAL_LLM_TOKEN_TESTS=true and provide model credentials)");
        }

        using var client = CreateChatClient(settings);
        var fixture = CreatePopulatedRegistry();

        var scenario = new RoundScenario[]
        {
            new("用户：请先分析这个项目的结构。", "analyze_project", "项目结构分析完成：12 个模块，37 个 C# 文件，NuGet 依赖 28 个。主入口为 Program.cs。"),
            new("用户：根据分析结果生成开发计划。", "generate_dev_plan", "计划已生成（5 个任务）：\n1. 重构认证中间件\n2. 添加单元测试覆盖\n3. 优化数据库查询\n4. 更新 API 文档\n5. 修复已知 bug"),
            new("用户：开始执行第一个任务。", "start_next_task", "✅ 已按计划顺序派发任务 [task-001] 重构认证中间件（作业 ID: job-abc123）。系统将在后台运行 TDD 闭环。"),
            new("用户：所有任务已完成，请总结执行结果。", null, null),
        };

        var gatewayBase = new[] { "git_clone", "analyze_project", "generate_dev_plan", "ivilson_ls", "ivilson_read", "search_in_files", "start_next_task", "tool_search" };

        var resultA = await RunMultiRoundAsync(client, fixture.Registry, fixture.AllToolNames, scenario, StrategyKind.A_FullCatalog, gatewayBase, null);
        var resultB = await RunMultiRoundAsync(client, fixture.Registry, fixture.AllToolNames, scenario, StrategyKind.B_DiscoveryHint, gatewayBase, null);
        var resultC = await RunMultiRoundAsync(client, fixture.Registry, fixture.AllToolNames, scenario, StrategyKind.C_NoCatalog, gatewayBase, null);

        PrintResults("Gateway", settings.Model, resultA, resultB, resultC);

        Assert.True(resultB.TotalInputTokens < resultA.TotalInputTokens,
            $"Gateway B ({resultB.TotalInputTokens}) 应低于 A ({resultA.TotalInputTokens})");
        Assert.True(resultC.TotalInputTokens < resultA.TotalInputTokens,
            $"Gateway C ({resultC.TotalInputTokens}) 应低于 A ({resultA.TotalInputTokens})");
    }

    /// <summary>
    /// Controller 链路：read → search → tool_search(git) → git_status → 总结
    /// 含 tool_search 激活后增量注入验证
    /// </summary>
    [Fact]
    [Trait("Category", "RealProvider")]
    public async Task Controller_MultiRound_RealApi_ABC_Comparison()
    {
        if (!TryLoadRealApiSettings(out var settings))
        {
            output.WriteLine("\n========== Controller 多轮 A/B/C 对比（未执行） ==========");
            output.WriteLine("未启用真实模型测试。设置 RUN_REAL_LLM_TOKEN_TESTS=true 后可运行。");
            output.WriteLine("=========================================================\n");
            Assert.Skip("NotRun: real-provider gate is off (set RUN_REAL_LLM_TOKEN_TESTS=true and provide model credentials)");
        }

        using var client = CreateChatClient(settings);
        var fixture = CreatePopulatedRegistry();

        var scenario = new RoundScenario[]
        {
            new("用户：请读取 Program.cs 的内容。", "ivilson_read", "using Microsoft.AspNetCore.Builder;\n\nvar builder = WebApplication.CreateBuilder(args);\nbuilder.Services.AddControllers();\nvar app = builder.Build();\napp.MapControllers();\napp.Run();"),
            new("用户：搜索项目中所有使用了 Authorize 特性的文件。", "search_in_files", "匹配 5 个文件：\n- Controllers/CodexController.cs:15\n- Controllers/AuthController.cs:8\n- Controllers/ChatController.cs:12\n- Hubs/CodexHub.cs:3\n- Controllers/AdminController.cs:6"),
            new("用户：我需要查看 git 状态，请激活 git 工具。", "tool_search", "{\"found\":2,\"query\":\"git\",\"tools\":[{\"name\":\"git_status\",\"activated\":true,\"available_now\":true},{\"name\":\"git_commit\",\"activated\":true,\"available_now\":true}]}"),
            new("用户：现在查看当前 git 状态。", "git_status", "On branch feature-auth-refactor\nChanges not staged for commit:\n  modified: Controllers/CodexController.cs\n  modified: Middleware/AuthMiddleware.cs\n\nno changes added to commit"),
            new("用户：总结当前项目状态。", null, null),
        };

        var controllerBase = new[] { "ivilson_ls", "ivilson_read", "search_in_files", "search_file_index", "exec_code", "run_tests", "analyze_project", "generate_dev_plan", "execute_code_task", "tool_search" };
        // tool_search 在第 3 轮激活 git_status 和 git_commit
        var activationsAtRound = new Dictionary<int, string[]> { [2] = new[] { "git_status", "git_commit" } };

        var resultA = await RunMultiRoundAsync(client, fixture.Registry, fixture.AllToolNames, scenario, StrategyKind.A_FullCatalog, controllerBase, activationsAtRound);
        var resultB = await RunMultiRoundAsync(client, fixture.Registry, fixture.AllToolNames, scenario, StrategyKind.B_DiscoveryHint, controllerBase, activationsAtRound);
        var resultC = await RunMultiRoundAsync(client, fixture.Registry, fixture.AllToolNames, scenario, StrategyKind.C_NoCatalog, controllerBase, activationsAtRound);

        PrintResults("Controller", settings.Model, resultA, resultB, resultC);

        Assert.True(resultB.TotalInputTokens < resultA.TotalInputTokens,
            $"Controller B ({resultB.TotalInputTokens}) 应低于 A ({resultA.TotalInputTokens})");
        Assert.True(resultC.TotalInputTokens < resultA.TotalInputTokens,
            $"Controller C ({resultC.TotalInputTokens}) 应低于 A ({resultA.TotalInputTokens})");
    }

    // ═══════════════════════════════════════════════════════
    // Core simulation engine — calls real API per round
    // ═══════════════════════════════════════════════════════

    private enum StrategyKind { A_FullCatalog, B_DiscoveryHint, C_NoCatalog }

    private async Task<MultiRoundResult> RunMultiRoundAsync(
        IChatClient client,
        DefaultToolRegistry registry,
        IReadOnlyCollection<string> allToolNames,
        RoundScenario[] scenario,
        StrategyKind strategy,
        string[] baseToolNames,
        Dictionary<int, string[]>? activationsAtRound)
    {
        var perRound = new List<RoundMetrics>();
        var messages = new List<ChatMessage>();
        var activatedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Build system prompt based on strategy
        var basePrompt = "你是 CodexFlow，一个高级 AI 战略工程师。帮助用户高效实现目标。当你需要使用工具时直接调用即可。";
        var systemPrompt = strategy switch
        {
            StrategyKind.A_FullCatalog => ToolCatalogPromptComposer.AppendCatalog(basePrompt, registry),
            StrategyKind.B_DiscoveryHint => ToolCatalogPromptComposer.AppendDiscoveryHint(basePrompt),
            StrategyKind.C_NoCatalog => basePrompt,
            _ => basePrompt
        };
        messages.Add(new ChatMessage(ChatRole.System, systemPrompt));

        int successCount = 0;

        for (int round = 0; round < scenario.Length; round++)
        {
            var scene = scenario[round];

            // Build tools for this round
            var roundToolNames = new HashSet<string>(baseToolNames, StringComparer.OrdinalIgnoreCase);
            if (strategy != StrategyKind.A_FullCatalog)
            {
                // B/C: only base + activated
                foreach (var name in activatedTools)
                    roundToolNames.Add(name);
            }
            else
            {
                // A: all registered tools every round, not just always-on/active subset
                foreach (var toolName in allToolNames)
                    roundToolNames.Add(toolName);
            }

            var aiTools = BuildAIFunctionsForNames(roundToolNames);
            var options = new ChatOptions
            {
                Tools = aiTools.Cast<AITool>().ToList(),
                Temperature = 0,
                MaxOutputTokens = 128
            };

            // Add user message
            messages.Add(new ChatMessage(ChatRole.User, scene.UserMessage));

            // If previous round had a tool call, inject the simulated result
            if (round > 0 && scenario[round - 1].ToolName != null)
            {
                var prevTool = scenario[round - 1].ToolName!;
                var prevResult = scenario[round - 1].ToolResult!;
                messages.Add(new ChatMessage(ChatRole.Assistant, $"我将使用 {prevTool} 工具来处理。"));
                messages.Add(new ChatMessage(ChatRole.User, $"[工具结果] {prevTool}: {prevResult}"));
            }

            // Call real API
            int inputTokens = 0;
            int outputTokens = 0;
            bool success = false;
            string responseText = "";

            try
            {
                var response = await client.GetResponseAsync(messages, options);
                inputTokens = (int)(response.Usage?.InputTokenCount ?? 0);
                outputTokens = (int)(response.Usage?.OutputTokenCount ?? 0);
                responseText = response.Messages?.LastOrDefault()?.Text ?? "";
                success = inputTokens > 0 && !string.IsNullOrWhiteSpace(responseText);
            }
            catch (Exception ex)
            {
                output.WriteLine($"  [WARN] 策略 {strategy} 第 {round + 1} 轮 API 调用失败: {ex.Message}");
            }

            if (success) successCount++;

            perRound.Add(new RoundMetrics(round + 1, inputTokens, outputTokens, success, roundToolNames.Count));

            // Track activations for next round
            if (activationsAtRound != null && activationsAtRound.TryGetValue(round, out var toActivate))
            {
                foreach (var name in toActivate)
                    activatedTools.Add(name);
            }

            // Add assistant response to history
            if (!string.IsNullOrWhiteSpace(responseText))
            {
                messages.Add(new ChatMessage(ChatRole.Assistant,
                    responseText.Length > 200 ? responseText[..200] + "..." : responseText));
            }
        }

        return new MultiRoundResult(
            strategy.ToString(),
            perRound,
            successCount,
            scenario.Length);
    }

    // ═══════════════════════════════════════════════════════
    // Output formatting
    // ═══════════════════════════════════════════════════════

    private void PrintResults(string pipeline, string model, MultiRoundResult a, MultiRoundResult b, MultiRoundResult c)
    {
        output.WriteLine($"\n{"═",-70}");
        output.WriteLine($"  {pipeline} 真实 API 多轮 A/B/C 对比");
        output.WriteLine($"  模型: {model}");
        output.WriteLine($"{"═",-70}");

        output.WriteLine($"\n{"轮次",-5} {"#tools",-8} {"A:input",-10} {"B:input",-10} {"C:input",-10} {"B节省%",-9} {"C节省%",-9}");
        output.WriteLine(new string('-', 70));

        for (int i = 0; i < a.Rounds.Count; i++)
        {
            var ar = a.Rounds[i];
            var br = b.Rounds[i];
            var cr = c.Rounds[i];
            var bSave = ar.InputTokens > 0 ? (ar.InputTokens - br.InputTokens) * 100.0 / ar.InputTokens : 0;
            var cSave = ar.InputTokens > 0 ? (ar.InputTokens - cr.InputTokens) * 100.0 / ar.InputTokens : 0;

            output.WriteLine(
                $"R{ar.Round,-4} {ar.ToolCount}/{br.ToolCount}/{cr.ToolCount,-5} " +
                $"{ar.InputTokens,-10} {br.InputTokens,-10} {cr.InputTokens,-10} " +
                $"{bSave:F1}%{"",-4} {cSave:F1}%");
        }

        output.WriteLine(new string('-', 70));
        var totalBSave = a.TotalInputTokens > 0 ? (a.TotalInputTokens - b.TotalInputTokens) * 100.0 / a.TotalInputTokens : 0;
        var totalCSave = a.TotalInputTokens > 0 ? (a.TotalInputTokens - c.TotalInputTokens) * 100.0 / a.TotalInputTokens : 0;
        output.WriteLine(
            $"{"总计",-14} " +
            $"{a.TotalInputTokens,-10} {b.TotalInputTokens,-10} {c.TotalInputTokens,-10} " +
            $"{totalBSave:F1}%{"",-4} {totalCSave:F1}%");

        output.WriteLine($"\n{"指标",-20} {"A",-15} {"B",-15} {"C",-15}");
        output.WriteLine(new string('-', 65));
        output.WriteLine($"{"成功率",-18} {a.SuccessRate,-15:P0} {b.SuccessRate,-15:P0} {c.SuccessRate,-15:P0}");
        output.WriteLine($"{"总轮次",-18} {a.TotalRounds,-15} {b.TotalRounds,-15} {c.TotalRounds,-15}");
        output.WriteLine($"{"累计 input token",-18} {a.TotalInputTokens,-15} {b.TotalInputTokens,-15} {c.TotalInputTokens,-15}");
        output.WriteLine($"{"累计 output token",-18} {a.TotalOutputTokens,-15} {b.TotalOutputTokens,-15} {c.TotalOutputTokens,-15}");
        output.WriteLine("");
    }

    // ═══════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════

    private static ToolFixture CreatePopulatedRegistry()
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var allToolNames = new List<string>();

        // Always-on
        RegisterTool(registry, allToolNames, "ivilson_ls", "列出目录下的文件和子目录", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, "ivilson_read", "读取指定文件的内容，支持行号范围", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, "write_file", "写入或覆盖指定路径的文件内容", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, "delete_file", "删除指定路径的文件", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, "search_in_files", "在工作区文件中搜索匹配模式的内容", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, "exec_code", "在沙箱中执行 Python 代码片段", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, "run_command", "在工作区中运行 shell 命令", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, "search_file_index", "全局文件索引搜索", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, "run_tests", "运行项目测试套件", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, "analyze_project", "分析项目结构、技术栈和代码质量", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, "analyze_code", "使用 Roslyn 引擎对 C# 代码片段进行静态分析", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, "generate_dev_plan", "根据需求描述生成结构化的开发计划", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, "execute_code_task", "执行计划中的代码任务（TDD 闭环）", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, "tool_search", "搜索并激活延迟加载的工具", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, "start_next_task", "按计划顺序启动下一个待执行任务", CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);

        // Deferred
        RegisterTool(registry, allToolNames, "git_clone", "从远程 GitHub 仓库拉取代码到工作区子目录", CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, "git_status", "查看当前仓库的 git 状态（分支、变更文件列表）", CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, "git_commit", "将当前工作区的更改提交到本地 git 仓库", CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, "git_push", "将本地提交推送到远程仓库", CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, "apply_patch", "将 unified diff 格式的补丁应用到指定文件", CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, "ivilson_smart_patch", "智能修复：自动定位并修复代码中的问题", CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, "openspec_revert_changes", "撤销当前任务的所有未提交改动", CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, "openspec_create_checkpoint", "手动创建阶段性快照", CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, "save_project_summary", "保存项目分析摘要到持久存储", CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, "user_learn_preference", "记录用户的全局偏好和技术决策", CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, "create_github_issue", "在 GitHub 仓库中创建 issue", CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, "create_pr", "在 GitHub 上创建 Pull Request", CodexFlow.Core.Abstractions.ToolLoading.Deferred);

        // Extra deferred tools to simulate realistic scale
        for (int i = 0; i < 20; i++)
        {
            RegisterTool(registry, allToolNames, $"plugin_tool_{i:D3}", $"扩展插件工具 {i}：提供额外的自动化能力", CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        }

        return new ToolFixture(registry, allToolNames);
    }

    private static void RegisterTool(DefaultToolRegistry registry, ICollection<string> allToolNames, string name, string description, CodexFlow.Core.Abstractions.ToolLoading loading)
    {
        registry.RegisterTool(
            new DelegateCodexTool(name, description, ToolCategory.System, Array.Empty<int>(),
                (_, _) => Task.FromResult(CodexToolResult.Succeeded("ok"))),
            loading);
        allToolNames.Add(name);
    }

    private static List<AIFunction> BuildAIFunctionsForNames(IEnumerable<string> toolNames)
    {
        return toolNames.Select(name =>
            AIFunctionFactory.Create(
                async (Dictionary<string, object?> args, CancellationToken ct) =>
                {
                    await Task.CompletedTask;
                    return "ok";
                },
                name: name,
                description: $"Tool: {name}"))
            .ToList();
    }

    // ═══════════════════════════════════════════════════════
    // Settings loader (same pattern as existing tests)
    // ═══════════════════════════════════════════════════════

    private static bool TryLoadRealApiSettings(out RealApiSettings settings)
    {
        settings = default!;

        var enabled = Environment.GetEnvironmentVariable("RUN_REAL_LLM_TOKEN_TESTS");
        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(enabled, "1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var apiUrl = Environment.GetEnvironmentVariable("IVILSON_VLLM_API_URL");
        var apiKey = Environment.GetEnvironmentVariable("IVILSON_VLLM_API_KEY");
        var model = Environment.GetEnvironmentVariable("IVILSON_VLLM_MODEL");
        var apiMode = Environment.GetEnvironmentVariable("IVILSON_VLLM_API_MODE");
        var temperature = ParseNullableFloat(Environment.GetEnvironmentVariable("IVILSON_VLLM_TEMPERATURE"));
        var topP = ParseNullableFloat(Environment.GetEnvironmentVariable("IVILSON_VLLM_TOP_P"));

        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            var configPath = FindRepositoryFile(Path.Combine("CodexFlow", "appsettings.json"));
            if (configPath == null) return false;

            using var doc = JsonDocument.Parse(
                File.ReadAllText(configPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (!doc.RootElement.TryGetProperty("VllmAgent", out var vllmAgent)) return false;

            apiUrl ??= vllmAgent.TryGetProperty("ApiUrl", out var u) ? u.GetString() : null;
            apiKey ??= vllmAgent.TryGetProperty("ApiKey", out var k) ? k.GetString() : null;
            model ??= vllmAgent.TryGetProperty("Model", out var m) ? m.GetString() : null;
            apiMode ??= vllmAgent.TryGetProperty("ApiMode", out var a) ? a.GetString() : null;
            temperature ??= vllmAgent.TryGetProperty("Temperature", out var t) ? t.GetSingle() : null;
            topP ??= vllmAgent.TryGetProperty("TopP", out var p) ? p.GetSingle() : null;
        }

        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
            return false;

        settings = new RealApiSettings(apiUrl, apiKey, model, apiMode, temperature, topP);
        return true;
    }

    private static IChatClient CreateChatClient(RealApiSettings settings)
        => VllmChatClientFactory.Create(settings.ApiUrl, settings.ApiKey, settings.Model, settings.ApiMode);

    private static float? ParseNullableFloat(string? value)
        => float.TryParse(value, out var parsed) ? parsed : null;

    private static string? FindRepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════
    // Data types
    // ═══════════════════════════════════════════════════════

    private sealed record RealApiSettings(string ApiUrl, string ApiKey, string Model, string? ApiMode, float? Temperature, float? TopP);
    private sealed record ToolFixture(DefaultToolRegistry Registry, IReadOnlyList<string> AllToolNames);
    private sealed record RoundScenario(string UserMessage, string? ToolName, string? ToolResult);
    private sealed record RoundMetrics(int Round, int InputTokens, int OutputTokens, bool Success, int ToolCount);

    private sealed record MultiRoundResult(
        string Strategy,
        IReadOnlyList<RoundMetrics> Rounds,
        int SuccessCount,
        int TotalRounds)
    {
        public int TotalInputTokens => Rounds.Sum(r => r.InputTokens);
        public int TotalOutputTokens => Rounds.Sum(r => r.OutputTokens);
        public double SuccessRate => TotalRounds > 0 ? (double)SuccessCount / TotalRounds : 0;
    }
}
