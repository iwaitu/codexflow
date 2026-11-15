// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SharpToken;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

/// <summary>
/// A/B/C 多轮工具注入策略基准测试
///
/// 策略对比：
/// A: 当前实现 — 结构化 tools 全量注入 + catalog 文本注入（每轮都发送）
/// B: 极小常驻 + 每轮重建 + discovery hint（本次实施方案）
/// C: 极小常驻 + 每轮重建 + 无 catalog（仅依赖 tool_search）
///
/// 对两条真实链路统计 每轮 InputTokenCount / 全流程累计 InputTokenCount。
/// </summary>
public sealed class MultiRoundToolInjectionBenchmarkTests(ITestOutputHelper output)
{
    private const string Encoding = "cl100k_base";

    // ═══════════════════════════════════════════════════════
    // Gateway 链路：analyze_project → generate_dev_plan → start_next_task → final
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void Gateway_MultiRound_StrategyComparison()
    {
        var fixture = CreatePopulatedRegistry();
        var gatewayBaseTools = new[] { "git_clone", "analyze_project", "generate_dev_plan", "ivilson_ls", "ivilson_read", "search_in_files", "start_next_task", "tool_search" };

        var rounds = new[]
        {
            new SimulatedRound("analyze_project", "项目结构分析完成：12 个模块，37 个文件。", null),
            new SimulatedRound("generate_dev_plan", "计划已生成：5 个任务。", null),
            new SimulatedRound("start_next_task", "任务 task-001 已提交后台执行。", null),
            new SimulatedRound(null, "所有任务已完成，请查看结果。", null),
        };

        var resultA = SimulateMultiRound("A", fixture.Registry, fixture.AllToolNames, rounds, StrategyA_FullCatalog, gatewayBaseTools);
        var resultB = SimulateMultiRound("B", fixture.Registry, fixture.AllToolNames, rounds, StrategyB_DiscoveryHint, gatewayBaseTools);
        var resultC = SimulateMultiRound("C", fixture.Registry, fixture.AllToolNames, rounds, StrategyC_NoCatalog, gatewayBaseTools);

        PrintComparison("Gateway", resultA, resultB, resultC);

        // B 和 C 的总 input token 应该明显低于 A
        Assert.True(resultB.TotalInputTokens < resultA.TotalInputTokens,
            $"策略 B 总 token ({resultB.TotalInputTokens}) 应低于策略 A ({resultA.TotalInputTokens})");
        Assert.True(resultC.TotalInputTokens < resultA.TotalInputTokens,
            $"策略 C 总 token ({resultC.TotalInputTokens}) 应低于策略 A ({resultA.TotalInputTokens})");
        Assert.True(resultC.TotalInputTokens <= resultB.TotalInputTokens,
            $"策略 C 总 token ({resultC.TotalInputTokens}) 应 <= 策略 B ({resultB.TotalInputTokens})");
    }

    // ═══════════════════════════════════════════════════════
    // Controller 链路：read → search → tool_search → activated tool → final
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void Controller_MultiRound_StrategyComparison()
    {
        var fixture = CreatePopulatedRegistry();
        var controllerBaseTools = new[] { "ivilson_ls", "ivilson_read", "search_in_files", "search_file_index", "exec_code", "run_tests", "analyze_project", "generate_dev_plan", "execute_code_task", "tool_search" };

        var rounds = new[]
        {
            new SimulatedRound("ivilson_read", "文件内容：public class Foo { ... }", null),
            new SimulatedRound("search_in_files", "匹配到 5 处结果。", null),
            new SimulatedRound("tool_search", "{\"found\":2,\"query\":\"git\",\"tools\":[{\"name\":\"git_status\",\"activated\":true,\"available_now\":true},{\"name\":\"git_commit\",\"activated\":true,\"available_now\":true}]}", new[] { "git_status", "git_commit" }),
            new SimulatedRound("git_status", "On branch main, nothing to commit.", null),
            new SimulatedRound(null, "项目状态正常，无需修改。", null),
        };

        var resultA = SimulateMultiRound("A", fixture.Registry, fixture.AllToolNames, rounds, StrategyA_FullCatalog, controllerBaseTools);
        var resultB = SimulateMultiRound("B", fixture.Registry, fixture.AllToolNames, rounds, StrategyB_DiscoveryHint, controllerBaseTools);
        var resultC = SimulateMultiRound("C", fixture.Registry, fixture.AllToolNames, rounds, StrategyC_NoCatalog, controllerBaseTools);

        PrintComparison("Controller", resultA, resultB, resultC);

        Assert.True(resultB.TotalInputTokens < resultA.TotalInputTokens,
            $"策略 B 总 token ({resultB.TotalInputTokens}) 应低于策略 A ({resultA.TotalInputTokens})");
        Assert.True(resultC.TotalInputTokens < resultA.TotalInputTokens,
            $"策略 C 总 token ({resultC.TotalInputTokens}) 应低于策略 A ({resultA.TotalInputTokens})");
    }

    // ═══════════════════════════════════════════════════════
    // tool_search 激活后下一轮可用验证
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void ActivatedTools_ShouldAppearInNextRound_NotCurrentRound()
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);

        var alwaysOn = CreateMockTool("ivilson_read", "Read files");
        var deferred = CreateMockTool("git_status", "Show git status");

        registry.RegisterTool(alwaysOn, CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        registry.RegisterTool(deferred, CodexFlow.Core.Abstractions.ToolLoading.Deferred);

        // Round 1: only always-on tools are active
        var round1Active = registry.GetActiveToolNames();
        Assert.Contains("ivilson_read", round1Active);
        Assert.DoesNotContain("git_status", round1Active);

        // tool_search activates git_status
        registry.ActivateTool("git_status");

        // Round 2: now git_status is active
        var round2Active = registry.GetActiveToolNames();
        Assert.Contains("ivilson_read", round2Active);
        Assert.Contains("git_status", round2Active);
    }

    // ═══════════════════════════════════════════════════════
    // Strategies
    // ═══════════════════════════════════════════════════════

    /// <summary>策略 A: 全量 catalog + 全量结构化 tools（当前实现）</summary>
    private static string StrategyA_FullCatalog(string basePrompt, DefaultToolRegistry registry)
    {
        return ToolCatalogPromptComposer.AppendCatalog(basePrompt, registry);
    }

    /// <summary>策略 B: 极短 discovery hint（本次实施方案）</summary>
    private static string StrategyB_DiscoveryHint(string basePrompt, DefaultToolRegistry _)
    {
        return ToolCatalogPromptComposer.AppendDiscoveryHint(basePrompt);
    }

    /// <summary>策略 C: 无 catalog，纯依赖 tool_search</summary>
    private static string StrategyC_NoCatalog(string basePrompt, DefaultToolRegistry _)
    {
        return basePrompt;
    }

    // ═══════════════════════════════════════════════════════
    // Simulation engine
    // ═══════════════════════════════════════════════════════

    private static BenchmarkResult SimulateMultiRound(
        string strategyName,
        DefaultToolRegistry registry,
        IReadOnlyCollection<string> allToolNames,
        SimulatedRound[] rounds,
        Func<string, DefaultToolRegistry, string> promptStrategy,
        string[] baseToolNames)
    {
        var encoding = GptEncoding.GetEncoding(Encoding);
        var perRoundTokens = new List<int>();
        var messages = new List<string>();

        var baseSystemPrompt = "你是 CodexFlow，一个高级 AI 战略工程师。帮助用户高效实现目标。";
        var systemPrompt = promptStrategy(baseSystemPrompt, registry);

        // Accumulate tool schemas for strategy A (all tools every round)
        // For B/C, only base tools + activated tools
        var activatedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rounds.Length; i++)
        {
            var round = rounds[i];
            var sb = new StringBuilder();

            // System prompt (sent every round in typical LLM API usage)
            sb.AppendLine(systemPrompt);

            // Tool schemas (simulated)
            if (strategyName == "A")
            {
                // Strategy A: all registered tools every round
                foreach (var name in allToolNames)
                {
                    sb.AppendLine($"[TOOL_SCHEMA] {name}");
                }
            }
            else
            {
                // Strategies B/C: only base tools + activated tools
                foreach (var name in baseToolNames)
                {
                    sb.AppendLine($"[TOOL_SCHEMA] {name}");
                }
                foreach (var name in activatedTools)
                {
                    if (!baseToolNames.Contains(name))
                    {
                        sb.AppendLine($"[TOOL_SCHEMA] {name}");
                    }
                }
            }

            // Conversation history (all previous messages)
            foreach (var msg in messages)
            {
                sb.AppendLine(msg);
            }

            // Current user message
            sb.AppendLine("[USER] 请继续执行。");

            var inputText = sb.ToString();
            var tokens = encoding.CountTokens(inputText);
            perRoundTokens.Add(tokens);

            // Simulate AI response
            var responseText = round.ToolName != null
                ? $"[TOOL_CALL] {round.ToolName}\n[TOOL_RESULT] {round.ToolResult}"
                : $"[ASSISTANT] {round.ToolResult}";
            messages.Add($"[USER] 请继续执行。");
            messages.Add(responseText);

            // Track tool activations
            if (round.ActivatedTools != null)
            {
                foreach (var t in round.ActivatedTools)
                {
                    activatedTools.Add(t);
                }
            }
        }

        return new BenchmarkResult(strategyName, perRoundTokens);
    }

    private void PrintComparison(string pipeline, BenchmarkResult a, BenchmarkResult b, BenchmarkResult c)
    {
        output.WriteLine($"\n{'=',-60}");
        output.WriteLine($"  {pipeline} 多轮工具注入策略对比 (encoding: {Encoding})");
        output.WriteLine($"{'=',-60}");

        output.WriteLine($"\n{"轮次",-6} {"A:全量catalog",-18} {"B:discovery hint",-18} {"C:无catalog",-18} {"B节省%",-10} {"C节省%",-10}");
        output.WriteLine(new string('-', 80));

        for (int i = 0; i < a.PerRoundTokens.Count; i++)
        {
            var aTokens = a.PerRoundTokens[i];
            var bTokens = b.PerRoundTokens[i];
            var cTokens = c.PerRoundTokens[i];
            var bSaving = aTokens > 0 ? (aTokens - bTokens) * 100.0 / aTokens : 0;
            var cSaving = aTokens > 0 ? (aTokens - cTokens) * 100.0 / aTokens : 0;

            output.WriteLine($"R{i + 1,-5} {aTokens,-18} {bTokens,-18} {cTokens,-18} {bSaving:F1}%{"",-5} {cSaving:F1}%");
        }

        output.WriteLine(new string('-', 80));
        var totalBSaving = a.TotalInputTokens > 0 ? (a.TotalInputTokens - b.TotalInputTokens) * 100.0 / a.TotalInputTokens : 0;
        var totalCSaving = a.TotalInputTokens > 0 ? (a.TotalInputTokens - c.TotalInputTokens) * 100.0 / a.TotalInputTokens : 0;
        output.WriteLine($"{"总计",-6} {a.TotalInputTokens,-18} {b.TotalInputTokens,-18} {c.TotalInputTokens,-18} {totalBSaving:F1}%{"",-5} {totalCSaving:F1}%");
        output.WriteLine("");
    }

    // ═══════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════

    private static ToolFixture CreatePopulatedRegistry()
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var allToolNames = new List<string>();

        // Always-on: 核心文件操作
        RegisterTool(registry, allToolNames, CreateMockTool("ivilson_ls", "列出目录下的文件和子目录"), CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, CreateMockTool("ivilson_read", "读取文件内容"), CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, CreateMockTool("write_file", "写入文件"), CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, CreateMockTool("delete_file", "删除文件"), CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, CreateMockTool("search_in_files", "在文件中搜索"), CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, CreateMockTool("exec_code", "执行代码"), CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, CreateMockTool("run_command", "运行命令"), CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, CreateMockTool("search_file_index", "全局文件索引搜索"), CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, CreateMockTool("run_tests", "运行测试"), CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, CreateMockTool("analyze_project", "分析项目结构"), CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, CreateMockTool("analyze_code", "Roslyn 代码分析"), CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, CreateMockTool("generate_dev_plan", "生成开发计划"), CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, CreateMockTool("execute_code_task", "执行代码任务"), CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);
        RegisterTool(registry, allToolNames, CreateMockTool("tool_search", "搜索并激活延迟工具"), CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);

        // Deferred: 长尾工具
        RegisterTool(registry, allToolNames, CreateMockTool("git_clone", "从远程仓库克隆代码"), CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, CreateMockTool("git_status", "查看 git 状态"), CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, CreateMockTool("git_commit", "提交更改"), CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, CreateMockTool("git_push", "推送到远程"), CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, CreateMockTool("apply_patch", "应用补丁"), CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, CreateMockTool("ivilson_smart_patch", "智能修复"), CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, CreateMockTool("openspec_revert_changes", "撤销改动"), CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, CreateMockTool("openspec_create_checkpoint", "创建快照"), CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, CreateMockTool("save_project_summary", "保存项目摘要"), CodexFlow.Core.Abstractions.ToolLoading.Deferred);
        RegisterTool(registry, allToolNames, CreateMockTool("user_learn_preference", "记录用户偏好"), CodexFlow.Core.Abstractions.ToolLoading.Deferred);

        // 额外 30 个 deferred 工具模拟真实规模
        for (int i = 0; i < 30; i++)
        {
            registry.RegisterTool(
                CreateMockTool($"extra_tool_{i:D3}", $"模拟工具 {i}，用于测试 token 消耗"),
                CodexFlow.Core.Abstractions.ToolLoading.Deferred);
            allToolNames.Add($"extra_tool_{i:D3}");
        }

        return new ToolFixture(registry, allToolNames);
    }

    private static void RegisterTool(DefaultToolRegistry registry, ICollection<string> allToolNames, ICodexTool tool, CodexFlow.Core.Abstractions.ToolLoading loading)
    {
        registry.RegisterTool(tool, loading);
        allToolNames.Add(tool.Name);
    }

    private static ICodexTool CreateMockTool(string name, string description)
    {
        // Explicit trusted capability for the fake tool (read-only); undeclared tools would be rejected at registration.
        return CodexFlow.Core.Tests.Support.DeclaredCapabilityTool.Declare(
            new DelegateCodexTool(
                name,
                description,
                ToolCategory.System,
                Array.Empty<int>(),
                (_, _) => Task.FromResult(CodexToolResult.Succeeded("ok"))),
            CodexFlow.Core.Execution.ToolCapability.ReadOnly);
    }

    private sealed record SimulatedRound(string? ToolName, string ToolResult, string[]? ActivatedTools);
    private sealed record ToolFixture(DefaultToolRegistry Registry, IReadOnlyList<string> AllToolNames);

    private sealed record BenchmarkResult(string Strategy, IReadOnlyList<int> PerRoundTokens)
    {
        public int TotalInputTokens => PerRoundTokens.Sum();
    }
}
