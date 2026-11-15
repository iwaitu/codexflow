// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SharpToken;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

/// <summary>
/// 基准测试：对比传统工具注入方式和 tool_search 按需加载方式的 token 使用量
///
/// 测试场景：
/// 1. 传统方式：将所有工具的定义注入到 system prompt，每次请求都携带完整工具列表
/// 2. 工具搜索方式：只注入精简目录（catalog），通过 tool_search 按需加载工具
///
/// 说明：
/// - 使用真实 tokenizer（SharpToken）统计 token，而不是字符数估算
/// - 对比全量 catalog 和当前 50 条截断 catalog，避免夸大节省率
/// - 该测试仍是离线 benchmark，不等同于真实模型 API 返回的 usage
/// </summary>
public class ToolRegistryTokenUsageIntegrationTests
{
    private static readonly IReadOnlyList<string> TokenEncodings = ["cl100k_base", "o200k_base"];
    private readonly ITestOutputHelper _output;
    private readonly ToolRegistryWithSearch _registry;

    public ToolRegistryTokenUsageIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _registry = new ToolRegistryWithSearch();
        RegisterSampleTools();
    }

    /// <summary>
    /// 注册大量模拟工具，用于测试 token 消耗
    /// </summary>
    private void RegisterSampleTools()
    {
        // Always-on 工具（文件操作类）
        _registry.Register(new ToolDefinition(
            "read_file",
            "读取指定路径的文件内容，支持相对路径和绝对路径",
            ToolCategory.Read,
            ToolLoading.AlwaysOn,
            [1, 2, 3, 4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "file content" }));

        _registry.Register(new ToolDefinition(
            "write_file",
            "写入或覆盖指定路径的文件内容",
            ToolCategory.Forge,
            ToolLoading.AlwaysOn,
            [2, 3, 4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "file written" }));

        _registry.Register(new ToolDefinition(
            "delete_file",
            "删除指定路径的文件",
            ToolCategory.Forge,
            ToolLoading.AlwaysOn,
            [2, 3, 4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "file deleted" }));

        // Deferred 工具：git 操作类
        _registry.Register(new ToolDefinition(
            "git_clone",
            "从远程 GitHub 仓库拉取代码到本地工作区",
            ToolCategory.System,
            ToolLoading.Deferred,
            [3, 4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "repository cloned" }));

        _registry.Register(new ToolDefinition(
            "git_status",
            "查看当前仓库的 git 状态",
            ToolCategory.System,
            ToolLoading.Deferred,
            [1, 2, 3, 4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "git status" }));

        _registry.Register(new ToolDefinition(
            "git_commit",
            "提交当前工作区的更改到本地 git 仓库",
            ToolCategory.System,
            ToolLoading.Deferred,
            [4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "committed" }));

        _registry.Register(new ToolDefinition(
            "git_push",
            "将本地提交推送到远程仓库",
            ToolCategory.System,
            ToolLoading.Deferred,
            [4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "pushed" }));

        _registry.Register(new ToolDefinition(
            "git_branch",
            "列出、创建或删除 git 分支",
            ToolCategory.System,
            ToolLoading.Deferred,
            [3, 4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "branch info" }));

        _registry.Register(new ToolDefinition(
            "git_merge",
            "合并指定的分支到当前分支",
            ToolCategory.System,
            ToolLoading.Deferred,
            [4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "merged" }));

        _registry.Register(new ToolDefinition(
            "git_rebase",
            "将当前分支变基到目标分支",
            ToolCategory.System,
            ToolLoading.Deferred,
            [4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "rebased" }));

        _registry.Register(new ToolDefinition(
            "git_log",
            "查看 commit 历史记录",
            ToolCategory.Analysis,
            ToolLoading.Deferred,
            [1, 2, 3, 4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "log entries" }));

        // Deferred 工具：测试相关
        _registry.Register(new ToolDefinition(
            "run_tests",
            "运行项目中的单元测试并返回结果",
            ToolCategory.Sentry,
            ToolLoading.Deferred,
            [4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "test results" }));

        _registry.Register(new ToolDefinition(
            "run_integration_tests",
            "运行集成测试套件",
            ToolCategory.Sentry,
            ToolLoading.Deferred,
            [4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "integration test results" }));

        _registry.Register(new ToolDefinition(
            "coverage_report",
            "生成代码覆盖率报告",
            ToolCategory.Sentry,
            ToolLoading.Deferred,
            [4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "coverage report" }));

        // Deferred 工具：分析和架构类
        _registry.Register(new ToolDefinition(
            "analyze_code",
            "分析 C# 代码的质量和问题",
            ToolCategory.Analysis,
            ToolLoading.Deferred,
            [2, 3, 4],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "analysis results" }));

        _registry.Register(new ToolDefinition(
            "generate_architecture_diagram",
            "生成系统架构图",
            ToolCategory.Analysis,
            ToolLoading.Deferred,
            [2],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "architecture diagram" }));

        _registry.Register(new ToolDefinition(
            "find_dependencies",
            "查找项目依赖关系图",
            ToolCategory.Analysis,
            ToolLoading.Deferred,
            [2, 3],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "dependencies" }));

        _registry.Register(new ToolDefinition(
            "search_symbols",
            "在项目中搜索符号定义和使用位置",
            ToolCategory.Analysis,
            ToolLoading.Deferred,
            [1, 2, 3],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "symbol locations" }));

        // Deferred 工具：外部服务集成
        _registry.Register(new ToolDefinition(
            "slack_send_message",
            "通过 MCP 向 Slack 频道发送消息通知",
            ToolCategory.System,
            ToolLoading.Deferred,
            [3, 4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "message sent" }));

        _registry.Register(new ToolDefinition(
            "notify_teams",
            "发送到 Microsoft Teams webhook",
            ToolCategory.System,
            ToolLoading.Deferred,
            [3, 4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "notification sent" }));

        _registry.Register(new ToolDefinition(
            "create_github_issue",
            "在 GitHub 仓库中创建 issue",
            ToolCategory.System,
            ToolLoading.Deferred,
            [2, 3, 4, 5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "issue created" }));

        _registry.Register(new ToolDefinition(
            "create_pr",
            "在 GitHub 上创建 Pull Request",
            ToolCategory.System,
            ToolLoading.Deferred,
            [5],
            async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "PR created" }));

        // 添加更多工具来达到测试所需的数量
        for (int i = 0; i < 50; i++)
        {
            var category = (i % 6) switch
            {
                0 => ToolCategory.Read,
                1 => ToolCategory.Forge,
                2 => ToolCategory.Analysis,
                3 => ToolCategory.Planning,
                4 => ToolCategory.Sentry,
                _ => ToolCategory.System
            };

            _registry.Register(new ToolDefinition(
                $"tool_{i:D3}",
                $"这是一个模拟的工具描述，用于测试工具注册表的性能表现和功能验证。工具编号为{i}，类别为{category}",
                category,
                ToolLoading.Deferred,
                [1, 2, 3, 4, 5],
                async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "ok" }));
        }
    }

    #region Token 统计辅助方法

    /// <summary>
    /// 使用真实 tokenizer 统计 token 数量
    /// </summary>
    private static IReadOnlyList<TokenCountResult> CountTokens(string text)
    {
        return TokenEncodings
            .Select(encodingName =>
            {
                var encoding = GptEncoding.GetEncoding(encodingName);
                return new TokenCountResult(encodingName, encoding.CountTokens(text));
            })
            .ToList();
    }

    /// <summary>
    /// 生成传统方式的 system prompt（注入完整工具定义）
    /// </summary>
    private static string GenerateTraditionalSystemPrompt(IEnumerable<ToolDefinition> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是一个 AI 编程助手。以下是你可以使用的工具：\n");
        sb.AppendLine("=== AVAILABLE TOOLS ===\n");

        foreach (var tool in tools)
        {
            sb.AppendLine($"### {tool.Name} ###");
            sb.AppendLine($"Description: {tool.Description}");
            sb.AppendLine($"Category: {tool.Category}");
            sb.AppendLine($"Allowed Stages: {(tool.AllowedStages.Count > 0 ? string.Join(", ", tool.AllowedStages) : "all")}");
            sb.AppendLine("---");
            sb.AppendLine();
        }

        sb.AppendLine("\n请根据用户的请求选择合适的工具来完成任务。");

        return sb.ToString();
    }

    /// <summary>
    /// 生成精简 catalog；默认包含全部工具，必要时可限制条数模拟生产配置
    /// </summary>
    private static string GenerateCatalog(IEnumerable<ToolDefinition> tools, int? limit = null)
    {
        var lines = new List<string>
        {
            "| Tool | Description |",
            "|------|-------------|"
        };

        foreach (var tool in (limit.HasValue ? tools.Take(limit.Value) : tools))
        {
            lines.Add($"| {tool.Name} | {tool.Description.Replace("|", "\\|")} |");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 生成工具搜索方式的 system prompt（注入精简目录 + tool_search 说明）
    /// </summary>
    private static string GenerateSearchBasedSystemPrompt(string catalog, string alwaysOnToolsList)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是一个 AI 编程助手。以下是你可以使用的工具目录：\n");
        sb.AppendLine(catalog);
        sb.AppendLine();
        sb.AppendLine("=== ALWAYS-ACTIVE TOOLS ===");
        sb.AppendLine(alwaysOnToolsList);
        sb.AppendLine();
        sb.AppendLine("=== ON-DEMAND TOOL LOADING ===");
        sb.AppendLine("如果你需要使用某个工具但不确定如何调用，可以使用 tool_search(query: string) 来搜索工具。");
        sb.AppendLine("例如：tool_search(\"git\") 会返回所有与 git 相关的工具。\n");
        sb.AppendLine("请根据用户的请求选择合适的工具来完成任务。");

        return sb.ToString();
    }

    private static string FormatTokenCounts(IReadOnlyList<TokenCountResult> counts)
    {
        return string.Join(", ", counts.Select(c => $"{c.EncodingName}={c.TokenCount}"));
    }

    private static IReadOnlyList<TokenDeltaResult> CalculateSavings(
        IReadOnlyList<TokenCountResult> baseline,
        IReadOnlyList<TokenCountResult> candidate)
    {
        return baseline
            .Join(candidate,
                left => left.EncodingName,
                right => right.EncodingName,
                (left, right) =>
                {
                    var saved = left.TokenCount - right.TokenCount;
                    var percent = left.TokenCount > 0 ? saved * 100.0 / left.TokenCount : 0;
                    return new TokenDeltaResult(left.EncodingName, left.TokenCount, right.TokenCount, saved, percent);
                })
            .ToList();
    }

    private static string FormatSavings(IReadOnlyList<TokenDeltaResult> deltas)
    {
        return string.Join(", ", deltas.Select(d => $"{d.EncodingName}: {d.SavedTokens} ({d.SavedPercent:F1}%)"));
    }

    #endregion

    #region 测试方法

    [Fact]
    public void TraditionalVsSearch_ToolCatalogSize_Comparison()
    {
        var allDefs = _registry.GetAllToolDefinitions();
        var alwaysOnTools = _registry.GetAlwaysOnTools().Cast<ICodexTool>().ToList();
        var alwaysOnList = string.Join(", ", alwaysOnTools.Select(t => t.Name));
        var traditionalPrompt = GenerateTraditionalSystemPrompt(allDefs);
        var fullCatalogPrompt = GenerateSearchBasedSystemPrompt(GenerateCatalog(allDefs), alwaysOnList);
        var truncatedCatalogPrompt = GenerateSearchBasedSystemPrompt(
            GenerateCatalog(allDefs, ToolRegistryWithSearch.CatalogLimit),
            alwaysOnList);

        var traditionalCounts = CountTokens(traditionalPrompt);
        var fullCatalogCounts = CountTokens(fullCatalogPrompt);
        var truncatedCatalogCounts = CountTokens(truncatedCatalogPrompt);
        var fullCatalogSavings = CalculateSavings(traditionalCounts, fullCatalogCounts);
        var truncatedCatalogSavings = CalculateSavings(traditionalCounts, truncatedCatalogCounts);

        _output.WriteLine("\n========== 工具目录对比 ==========");
        _output.WriteLine($"总工具数：{_registry.ToolCount}");
        _output.WriteLine($"Always-on 工具数：{alwaysOnTools.Count}");
        _output.WriteLine($"=================================\n");

        _output.WriteLine("[传统方式]（完整工具定义）");
        _output.WriteLine($"Prompt 长度：{traditionalPrompt.Length} 字符");
        _output.WriteLine($"真实 tokens: {FormatTokenCounts(traditionalCounts)}");
        _output.WriteLine($"预览 (前 500 字符):\n{traditionalPrompt[..Math.Min(500, traditionalPrompt.Length)]}\n");

        _output.WriteLine("[工具搜索方式]（全量 catalog，公平对比）");
        _output.WriteLine($"Prompt 长度：{fullCatalogPrompt.Length} 字符");
        _output.WriteLine($"真实 tokens: {FormatTokenCounts(fullCatalogCounts)}");
        _output.WriteLine($"节省情况: {FormatSavings(fullCatalogSavings)}");
        _output.WriteLine($"预览 (前 500 字符):\n{fullCatalogPrompt[..Math.Min(500, fullCatalogPrompt.Length)]}\n");

        _output.WriteLine("[工具搜索方式]（当前 50 条截断 catalog）");
        _output.WriteLine($"Prompt 长度：{truncatedCatalogPrompt.Length} 字符");
        _output.WriteLine($"真实 tokens: {FormatTokenCounts(truncatedCatalogCounts)}");
        _output.WriteLine($"节省情况: {FormatSavings(truncatedCatalogSavings)}\n");

        _output.WriteLine("========== 对比结果 ==========");
        _output.WriteLine($"公平对比节省: {FormatSavings(fullCatalogSavings)}");
        _output.WriteLine($"截断目录节省: {FormatSavings(truncatedCatalogSavings)}");
        _output.WriteLine($"================================\n");

        Assert.True(fullCatalogPrompt.Length < traditionalPrompt.Length,
            $"全量 catalog prompt ({fullCatalogPrompt.Length}) 应该比传统方式 ({traditionalPrompt.Length}) 更短");
        Assert.All(fullCatalogSavings, delta =>
        {
            Assert.True(delta.CandidateTokens < delta.BaselineTokens,
                $"编码 {delta.EncodingName} 下，全量 catalog 应该节省 token；baseline={delta.BaselineTokens}, candidate={delta.CandidateTokens}");
            Assert.InRange(delta.SavedPercent, 0, 100);
        });
    }

    [Fact]
    public void TraditionalVsSearch_RealTokenizerComparison_ShouldBeStableAcrossEncodings()
    {
        var allDefs = _registry.GetAllToolDefinitions();
        var alwaysOnTools = _registry.GetAlwaysOnTools().Cast<ICodexTool>().ToList();
        var alwaysOnList = string.Join(", ", alwaysOnTools.Select(t => t.Name));
        var traditionalPrompt = GenerateTraditionalSystemPrompt(allDefs);
        var fullCatalogPrompt = GenerateSearchBasedSystemPrompt(GenerateCatalog(allDefs), alwaysOnList);
        var fairComparison = CalculateSavings(CountTokens(traditionalPrompt), CountTokens(fullCatalogPrompt));

        _output.WriteLine("\n========== 真实 Tokenizer 对比 ==========");
        foreach (var delta in fairComparison)
        {
            _output.WriteLine(
                $"{delta.EncodingName}: baseline={delta.BaselineTokens}, catalog={delta.CandidateTokens}, saved={delta.SavedTokens} ({delta.SavedPercent:F1}%)");
        }
        _output.WriteLine("=========================================\n");

        Assert.Equal(TokenEncodings.Count, fairComparison.Count);
        Assert.All(fairComparison, delta =>
        {
            Assert.True(delta.SavedTokens > 0,
                $"编码 {delta.EncodingName} 下应该节省 token；baseline={delta.BaselineTokens}, candidate={delta.CandidateTokens}");
        });
    }

    [Fact]
    [Trait("Category", "RealProvider")]
    public async Task TraditionalVsSearch_RealApiInputTokenComparison_Optional()
    {
        if (!TryLoadRealApiSettings(out var settings))
        {
            _output.WriteLine("\n========== 真实 API Token 对比（未执行） ==========");
            _output.WriteLine("未发现可用的模型配置。可通过以下任一方式启用：");
            _output.WriteLine("1. 设置环境变量 RUN_REAL_LLM_TOKEN_TESTS=true");
            _output.WriteLine("2. 提供 IVILSON_VLLM_API_URL / IVILSON_VLLM_API_KEY / IVILSON_VLLM_MODEL");
            _output.WriteLine("3. 或在 CodexFlow/appsettings.json 中配置 VllmAgent");
            _output.WriteLine("===============================================\n");
            Assert.Skip("NotRun: real-provider gate is off (set RUN_REAL_LLM_TOKEN_TESTS=true and provide model credentials)");
        }

        var allDefs = _registry.GetAllToolDefinitions();
        var alwaysOnTools = _registry.GetAlwaysOnTools().Cast<ICodexTool>().ToList();
        var alwaysOnList = string.Join(", ", alwaysOnTools.Select(t => t.Name));
        var traditionalPrompt = GenerateTraditionalSystemPrompt(allDefs);
        var fullCatalogPrompt = GenerateSearchBasedSystemPrompt(GenerateCatalog(allDefs), alwaysOnList);
        var truncatedCatalogPrompt = GenerateSearchBasedSystemPrompt(
            GenerateCatalog(allDefs, ToolRegistryWithSearch.CatalogLimit),
            alwaysOnList);

        using var client = CreateChatClient(settings);

        var traditionalUsage = await MeasureInputTokensAsync(client, traditionalPrompt);
        var fullCatalogUsage = await MeasureInputTokensAsync(client, fullCatalogPrompt);
        var truncatedCatalogUsage = await MeasureInputTokensAsync(client, truncatedCatalogPrompt);

        var fairSaved = traditionalUsage.InputTokens - fullCatalogUsage.InputTokens;
        var fairSavedPercent = traditionalUsage.InputTokens > 0
            ? fairSaved * 100.0 / traditionalUsage.InputTokens
            : 0;

        var truncatedSaved = traditionalUsage.InputTokens - truncatedCatalogUsage.InputTokens;
        var truncatedSavedPercent = traditionalUsage.InputTokens > 0
            ? truncatedSaved * 100.0 / traditionalUsage.InputTokens
            : 0;

        _output.WriteLine("\n========== 真实 API Token 对比 ==========");
        _output.WriteLine($"模型: {settings.Model}");
        _output.WriteLine($"传统方式: input={traditionalUsage.InputTokens}, output={traditionalUsage.OutputTokens}");
        _output.WriteLine($"全量 catalog: input={fullCatalogUsage.InputTokens}, output={fullCatalogUsage.OutputTokens}");
        _output.WriteLine($"截断 catalog: input={truncatedCatalogUsage.InputTokens}, output={truncatedCatalogUsage.OutputTokens}");
        _output.WriteLine($"公平对比节省: {fairSaved} ({fairSavedPercent:F1}%)");
        _output.WriteLine($"截断目录节省: {truncatedSaved} ({truncatedSavedPercent:F1}%)");
        _output.WriteLine("========================================\n");

        Assert.True(traditionalUsage.InputTokens > fullCatalogUsage.InputTokens,
            $"真实 API 下，全量 catalog 应节省输入 token；baseline={traditionalUsage.InputTokens}, candidate={fullCatalogUsage.InputTokens}");
        Assert.True(fullCatalogUsage.InputTokens > truncatedCatalogUsage.InputTokens,
            $"真实 API 下，截断 catalog 应比全量 catalog 更短；full={fullCatalogUsage.InputTokens}, truncated={truncatedCatalogUsage.InputTokens}");
    }

    [Fact]
    [Trait("Category", "RealProvider")]
    public async Task StructuredToolsVsCatalogPrompt_RealApiInputTokenComparison_Optional()
    {
        if (!TryLoadRealApiSettings(out var settings))
        {
            _output.WriteLine("\n========== 结构化 Tools vs Catalog（未执行） ==========");
            _output.WriteLine("未启用真实模型测试。设置 RUN_REAL_LLM_TOKEN_TESTS=true 后可运行。");
            _output.WriteLine("====================================================\n");
            Assert.Skip("NotRun: real-provider gate is off (set RUN_REAL_LLM_TOKEN_TESTS=true and provide model credentials)");
        }

        var allDefs = _registry.GetAllToolDefinitions();
        var alwaysOnDefs = allDefs.Where(d => d.Loading == ToolLoading.AlwaysOn).ToList();
        var minimalPrompt = "你是一个 AI 编程助手。根据用户请求选择合适的工具完成任务。";
        var catalogPrompt = GenerateSearchBasedSystemPrompt(
            GenerateCatalog(allDefs, ToolRegistryWithSearch.CatalogLimit),
            string.Join(", ", alwaysOnDefs.Select(t => t.Name)));

        var allStructuredTools = BuildStructuredTools(allDefs);
        var catalogStrategyTools = BuildCatalogStrategyTools(alwaysOnDefs);

        using var client = CreateChatClient(settings);

        var allStructuredUsage = await MeasureInputTokensAsync(
            client,
            minimalPrompt,
            new ChatOptions
            {
                Temperature = 0,
                TopP = 1,
                MaxOutputTokens = 8,
                Tools = allStructuredTools.Cast<AITool>().ToList()
            });

        var catalogStrategyUsage = await MeasureInputTokensAsync(
            client,
            catalogPrompt,
            new ChatOptions
            {
                Temperature = 0,
                TopP = 1,
                MaxOutputTokens = 8,
                Tools = catalogStrategyTools.Cast<AITool>().ToList()
            });

        var saved = allStructuredUsage.InputTokens - catalogStrategyUsage.InputTokens;
        var savedPercent = allStructuredUsage.InputTokens > 0
            ? saved * 100.0 / allStructuredUsage.InputTokens
            : 0;

        _output.WriteLine("\n========== 结构化 Tools vs Catalog ==========");
        _output.WriteLine($"模型: {settings.Model}");
        _output.WriteLine($"全量结构化 tools: count={allStructuredTools.Count}, input={allStructuredUsage.InputTokens}, output={allStructuredUsage.OutputTokens}");
        _output.WriteLine($"catalog 策略: count={catalogStrategyTools.Count}, input={catalogStrategyUsage.InputTokens}, output={catalogStrategyUsage.OutputTokens}");
        _output.WriteLine($"节省: {saved} ({savedPercent:F1}%)");
        _output.WriteLine("============================================\n");

        Assert.True(allStructuredUsage.InputTokens > catalogStrategyUsage.InputTokens,
            $"真实 API 下，catalog 策略应比全量结构化 tools 更省 token；allTools={allStructuredUsage.InputTokens}, catalog={catalogStrategyUsage.InputTokens}");
    }

    [Fact]
    public void ToolSearchWorkflow_EndToEnd_Simulated()
    {
        // 模拟完整的 agentic loop，展示工具搜索流程
        _output.WriteLine("\n========== 工具搜索工作流程模拟 ==========\n");

        // Round 0: 初始化
        var activeTools = new Dictionary<string, ICodexTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in _registry.GetAlwaysOnTools())
        {
            activeTools[tool.Name] = tool;
        }

        var toolSearch = new ToolSearchMetaTool(_registry, activeTools);

        _output.WriteLine($"[初始状态] Active tools: {activeTools.Count}");
        _output.WriteLine($"           {(string.Join(", ", activeTools.Keys))}\n");

        // Round 1: 用户请求需要 git 工具
        _output.WriteLine("Round 1: LLM 调用 tool_search(\"git\")");
        var searchResult1 = toolSearch.ExecuteAsync(new Dictionary<string, object?> { ["query"] = "git" }).Result;
        _output.WriteLine($"         → 新增工具，当前总数：{activeTools.Count}");
        _output.WriteLine($"         → Active: {(string.Join(", ", activeTools.Keys.Where(k => k.Contains("git"))))}\n");

        // Round 2: 用户请求需要通知工具
        _output.WriteLine("Round 2: LLM 调用 tool_search(\"slack notification\")");
        var searchResult2 = toolSearch.ExecuteAsync(new Dictionary<string, object?> { ["query"] = "slack notification" }).Result;
        _output.WriteLine($"         → 新增工具，当前总数：{activeTools.Count}");

        // 最终状态
        _output.WriteLine($"\n[最终状态] Active tools: {activeTools.Count}");
        _output.WriteLine($"           {string.Join(", ", activeTools.Keys.OrderBy(k => k))}\n");

        // 验证：应该有比初始更多的工具
        Assert.True(activeTools.Count > _registry.GetAlwaysOnTools().Count,
            "经过搜索后，active tools 数量应该增加");
    }

    [Fact]
    public void CatalogCompression_Ratio_Calculation()
    {
        var fullDefinitions = new StringBuilder();
        var allDefs = _registry.GetAllToolDefinitions();
        foreach (var toolDef in allDefs)
        {
            fullDefinitions.AppendLine($"### {toolDef.Name} ###");
            fullDefinitions.AppendLine($"Description: {toolDef.Description}");
            fullDefinitions.AppendLine($"Category: {toolDef.Category}");
            fullDefinitions.AppendLine($"Allowed Stages: {(toolDef.AllowedStages.Count > 0 ? string.Join(",", toolDef.AllowedStages) : "all")}");
            fullDefinitions.AppendLine();
        }

        var fullCatalog = GenerateCatalog(allDefs);
        var truncatedCatalog = GenerateCatalog(allDefs, ToolRegistryWithSearch.CatalogLimit);
        var fullDefinitionCounts = CountTokens(fullDefinitions.ToString());
        var fullCatalogSavings = CalculateSavings(fullDefinitionCounts, CountTokens(fullCatalog));
        var truncatedCatalogSavings = CalculateSavings(fullDefinitionCounts, CountTokens(truncatedCatalog));

        _output.WriteLine("\n========== 目录压缩比 ==========");
        _output.WriteLine($"总工具数：{_registry.ToolCount}");
        _output.WriteLine($"完整定义长度：{fullDefinitions.Length} 字符");
        _output.WriteLine($"全量目录长度：{fullCatalog.Length} 字符");
        _output.WriteLine($"截断目录长度：{truncatedCatalog.Length} 字符");
        _output.WriteLine($"全量目录节省：{FormatSavings(fullCatalogSavings)}");
        _output.WriteLine($"截断目录节省：{FormatSavings(truncatedCatalogSavings)}");
        _output.WriteLine($"================================\n");

        if (_registry.ToolCount >= 20)
        {
            Assert.All(fullCatalogSavings, delta =>
            {
                Assert.True(delta.CandidateTokens < delta.BaselineTokens,
                    $"当有 {_registry.ToolCount} 个工具时，全量目录在 {delta.EncodingName} 下应更短");
            });
        }
    }

    #endregion

    #region 辅助方法

    private List<ICodexTool> GetAllToolsAsICodexTool()
    {
        var result = new List<ICodexTool>();
        result.AddRange(_registry.GetAlwaysOnTools());
        return result;
    }

    #endregion

    private sealed record TokenCountResult(string EncodingName, int TokenCount);
    private sealed record TokenDeltaResult(string EncodingName, int BaselineTokens, int CandidateTokens, int SavedTokens, double SavedPercent);
    private sealed record RealApiSettings(string ApiUrl, string ApiKey, string Model, string? ApiMode, float? Temperature, float? TopP);
    private sealed record RealApiUsage(int InputTokens, int OutputTokens);

    private static async Task<RealApiUsage> MeasureInputTokensAsync(
        IChatClient client,
        string systemPrompt,
        ChatOptions? options = null)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, "请只回复 OK")
        };

        var effectiveOptions = options ?? new ChatOptions
        {
            Temperature = 0,
            TopP = 1,
            MaxOutputTokens = 8
        };

        var response = await client.GetResponseAsync(messages, effectiveOptions);

        var inputTokens = (int)(response.Usage?.InputTokenCount ?? 0);
        var outputTokens = (int)(response.Usage?.OutputTokenCount ?? 0);

        Assert.True(inputTokens > 0, "真实 API 返回的 InputTokenCount 应大于 0。");
        return new RealApiUsage(inputTokens, outputTokens);
    }

    private static IChatClient CreateChatClient(RealApiSettings settings)
        => VllmChatClientFactory.Create(settings.ApiUrl, settings.ApiKey, settings.Model, settings.ApiMode);

    private static List<AIFunction> BuildStructuredTools(IEnumerable<ToolDefinition> tools)
    {
        return tools
            .Select(tool => AIFunctionFactory.Create(
                async (Dictionary<string, object?> input_params, CancellationToken ct) =>
                {
                    await Task.CompletedTask;
                    return new CodexToolResult
                    {
                        Status = ToolResultStatus.Success,
                        Output = "ok"
                    };
                },
                name: tool.Name,
                description: tool.Description))
            .ToList();
    }

    private static List<AIFunction> BuildCatalogStrategyTools(IEnumerable<ToolDefinition> alwaysOnDefs)
    {
        var tools = BuildStructuredTools(alwaysOnDefs);
        tools.Add(AIFunctionFactory.Create(
            async (Dictionary<string, object?> input_params, CancellationToken ct) =>
            {
                await Task.CompletedTask;
                return new CodexToolResult
                {
                    Status = ToolResultStatus.Success,
                    Output = "[]"
                };
            },
            name: "tool_search",
            description: "搜索并激活延迟加载的工具。传入关键词来查找相关工具。匹配的工具会在后续轮次中可用。"));

        return tools;
    }

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
            temperature ??= vllmAgent.TryGetProperty("Temperature", out var temperatureProp) ? temperatureProp.GetSingle() : null;
            topP ??= vllmAgent.TryGetProperty("TopP", out var topPProp) ? topPProp.GetSingle() : null;
        }

        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        settings = new RealApiSettings(apiUrl, apiKey, model, apiMode, temperature, topP);
        return true;
    }

    private static float? ParseNullableFloat(string? value)
    {
        return float.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? FindRepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
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
}
