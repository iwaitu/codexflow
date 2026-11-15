// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

/// <summary>
/// 验证：统一工具注册表 + tool_search 元工具模式
///
/// 设计思路（参考 Python demo）：
/// 1. ToolRegistry 存储所有工具的完整定义（包括 deferred 工具）
/// 2. System prompt 注入精简目录（catalog），让 LLM 知道有什么工具可用
/// 3. always_on 工具：每次请求都注入到 active_tools
/// 4. deferred 工具：通过 tool_search 元工具按需加载
/// 5. tool_search 将匹配的工具注入到当前 agentic round 的 active_tools
/// </summary>
public class ToolRegistryWithSearchTests
{
    private readonly ToolRegistryWithSearch _registry;
    private readonly Dictionary<string, ICodexTool> _activeTools;
    private readonly ToolSearchMetaTool _toolSearch;

    public ToolRegistryWithSearchTests()
    {
        _registry = new ToolRegistryWithSearch();
        _activeTools = new Dictionary<string, ICodexTool>(StringComparer.OrdinalIgnoreCase);
        _toolSearch = new ToolSearchMetaTool(_registry, _activeTools);

        RegisterSampleTools();
    }

    /// <summary>
    /// 注册样本工具，模拟真实场景
    /// </summary>
    private void RegisterSampleTools()
    {
        // Always-on 工具：语义搜索文档（相当于 search_documents）
        _registry.Register(new ToolDefinition(
            "search_documents",
            "在上传的文档中进行语义搜索",
            ToolCategory.Analysis,
            ToolLoading.AlwaysOn,
            [1, 2, 3, 4, 5],
            async (args, ct) =>
            {
                var query = args["query"]?.ToString() ?? "";
                var topK = (int?)(args["top_k"] ?? 5);
                return new CodexToolResult
                {
                    Status = ToolResultStatus.Success,
                    Output = $"[模拟搜索结果] 关于 '{query}' 找到 {topK} 个文档..."
                };
            }
        ));

        // Deferred 工具：Slack 消息（相当于 slack_send_message via MCP）
        _registry.Register(new ToolDefinition(
            "slack_send_message",
            "通过 MCP 向 Slack 频道发送消息",
            ToolCategory.System,
            ToolLoading.Deferred,
            [3, 4, 5],
            async (args, ct) =>
            {
                var channel = args["channel"]?.ToString() ?? "#general";
                var text = args["text"]?.ToString() ?? "";
                return new CodexToolResult
                {
                    Status = ToolResultStatus.Success,
                    Output = $"[模拟 Slack] 已发送到 {channel}: {text}"
                };
            }
        ));

        // Deferred 工具：SQL 查询（相当于 run_sql via Skill）
        _registry.Register(new ToolDefinition(
            "run_sql",
            "在分析数据库上执行只读 SQL 查询",
            ToolCategory.Analysis,
            ToolLoading.Deferred,
            [2, 3, 4],
            async (args, ct) =>
            {
                var query = args["query"]?.ToString() ?? "";
                return new CodexToolResult
                {
                    Status = ToolResultStatus.Success,
                    Output = $"[模拟 SQL] 执行查询：{query}，返回 1 行数据"
                };
            }
        ));

        // 另一个 always-on 工具：读取文件
        _registry.Register(new ToolDefinition(
            "read_file",
            "读取文件内容",
            ToolCategory.Read,
            ToolLoading.AlwaysOn,
            [1, 2, 3, 4, 5],
            async (args, ct) =>
            {
                var path = args["path"]?.ToString() ?? "";
                return new CodexToolResult
                {
                    Status = ToolResultStatus.Success,
                    Output = $"[模拟读取] 文件：{path}"
                };
            }
        ));

        // Deferred 工具：git commit
        _registry.Register(new ToolDefinition(
            "git_commit",
            "提交代码到本地 git 仓库",
            ToolCategory.System,
            ToolLoading.Deferred,
            [4, 5],
            async (args, ct) =>
            {
                var message = args["message"]?.ToString() ?? "";
                return new CodexToolResult
                {
                    Status = ToolResultStatus.Success,
                    Output = $"[模拟 Git] 提交：{message}"
                };
            }
        ));
    }

    #region 基础功能测试

    [Fact]
    public void Register_Tool_ShouldStoreInCatalog()
    {
        // Act & Assert
        Assert.Equal(5, _registry.ToolCount);
    }

    [Fact]
    public void Search_Tool_ByKeyword_ShouldReturnMatches()
    {
        // Act
        var matches = _registry.Search("slack");

        // Assert
        Assert.Single(matches);
        Assert.Equal("slack_send_message", matches[0].Name);
    }

    [Fact]
    public void Search_Tool_ByRegex_ShouldReturnMatches()
    {
        // Act
        var matches = _registry.Search("^(git|run)_");

        // Assert
        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, t => t.Name == "git_commit");
        Assert.Contains(matches, t => t.Name == "run_sql");
    }

    [Fact]
    public void GetAlwaysOnTools_ShouldReturnOnlyAlwaysOn()
    {
        // Act
        var alwaysOn = _registry.GetAlwaysOnTools();

        // Assert
        Assert.Equal(2, alwaysOn.Count);
        Assert.Contains(alwaysOn, t => t.Name == "search_documents");
        Assert.Contains(alwaysOn, t => t.Name == "read_file");
    }

    [Fact]
    public void Catalog_ShouldReturnMarkdownTable()
    {
        // Act
        var catalog = _registry.Catalog();

        // Assert
        Assert.Contains("| Tool | Description |", catalog);
        Assert.Contains("| search_documents |", catalog);
        Assert.Contains("| slack_send_message |", catalog);
        Assert.Contains("| run_sql |", catalog);
    }

    #endregion

    #region Agentic Loop 模拟测试

    [Fact]
    public void RunAgentTurn_Should_DemonstrateToolDiscoveryFlow()
    {
        // =====================================================
        // 阶段 1：初始化 - 每次请求的生命周期开始
        // =====================================================
        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("👤 User: Send the Q3 revenue report to #general on Slack");
        Console.WriteLine(new string('=', 60));

        // 初始化 active_tools：只包含 always_on 工具
        _activeTools.Clear();
        foreach (var tool in _registry.GetAlwaysOnTools())
        {
            _activeTools[tool.Name] = tool;
        }

        // 注入 system prompt 的精简目录
        Console.WriteLine("\n📋 System prompt catalog:");
        Console.WriteLine(_registry.Catalog());

        // =====================================================
        // Round 1: LLM 调用 tool_search 发现 Slack 工具
        // =====================================================
        Console.WriteLine("\n🤖 LLM calls: tool_search(query='slack')");
        var searchResult = _toolSearch.ExecuteAsync(new Dictionary<string, object?>
        {
            ["query"] = "slack"
        });

        searchResult.Wait();
        var result = searchResult.Result;

        Console.WriteLine($"   → active tools now: {string.Join(", ", _activeTools.Keys)}");

        // 验证：slack 工具已被注入到 active_tools
        Assert.Contains("slack_send_message", _activeTools.Keys);
        Assert.Equal(3, _activeTools.Count); // search_documents, read_file, slack_send_message

        // =====================================================
        // Round 2: LLM 直接调用刚加载的 slack 工具
        // =====================================================
        Console.WriteLine("\n🤖 LLM calls: slack_send_message(channel='#general', text='Hello!')");
        var slackTool = _activeTools["slack_send_message"];
        var slackResult = slackTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["channel"] = "#general",
            ["text"] = "Hello from the agent!"
        });

        slackResult.Wait();
        Console.WriteLine($"   → result: {slackResult.Result.Output}");

        Assert.True(slackResult.Result.Status == ToolResultStatus.Success);

        // =====================================================
        // Round 3: 使用 always_on 的 search_documents 工具
        // =====================================================
        Console.WriteLine("\n🤖 LLM calls: search_documents(query='revenue report')");
        var docTool = _activeTools["search_documents"];
        var docResult = docTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["query"] = "revenue report",
            ["top_k"] = 3
        });

        docResult.Wait();
        Console.WriteLine($"   → result: {docResult.Result.Output}");

        Assert.True(docResult.Result.Status == ToolResultStatus.Success);

        // =====================================================
        // 验证完成
        // =====================================================
        Console.WriteLine("\n✅ Turn complete.");
        Console.WriteLine($"   Total active tools this turn: {_activeTools.Count}");

        // 最终验证：本 turn 使用了 3 个工具
        Assert.Equal(3, _activeTools.Count);
        Assert.Contains("search_documents", _activeTools.Keys);
        Assert.Contains("read_file", _activeTools.Keys);
        Assert.Contains("slack_send_message", _activeTools.Keys);
    }

    [Fact]
    public void RunAgentTurn_WithMultipleDeferredTools_ShouldShowLazyLoading()
    {
        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("👤 User: Run a SQL query and commit the results");
        Console.WriteLine(new string('=', 60));

        // 初始化
        _activeTools.Clear();
        foreach (var tool in _registry.GetAlwaysOnTools())
        {
            _activeTools[tool.Name] = tool;
        }

        Console.WriteLine($"\n🔧 Initial active tools: {string.Join(", ", _activeTools.Keys)}");
        Assert.Equal(2, _activeTools.Count); // 只有 always_on 工具

        // Round 1: 搜索并加载 SQL 工具
        Console.WriteLine("\n🤖 LLM calls: tool_search(query='sql')");
        var sqlSearch = _toolSearch.ExecuteAsync(new Dictionary<string, object?> { ["query"] = "sql" });
        sqlSearch.Wait();
        Console.WriteLine($"   → loaded: {string.Join(", ", _activeTools.Keys)}");

        Assert.Contains("run_sql", _activeTools.Keys);

        // Round 2: 执行 SQL
        Console.WriteLine("\n🤖 LLM calls: run_sql(query='SELECT * FROM revenue')");
        var sqlResult = _activeTools["run_sql"].ExecuteAsync(new Dictionary<string, object?>
        {
            ["query"] = "SELECT * FROM revenue"
        });
        sqlResult.Wait();
        Console.WriteLine($"   → {sqlResult.Result.Output}");

        // Round 3: 搜索并加载 git 工具
        Console.WriteLine("\n🤖 LLM calls: tool_search(query='git')");
        var gitSearch = _toolSearch.ExecuteAsync(new Dictionary<string, object?> { ["query"] = "git" });
        gitSearch.Wait();
        Console.WriteLine($"   → loaded: {string.Join(", ", _activeTools.Keys)}");

        Assert.Contains("git_commit", _activeTools.Keys);

        // Round 4: 执行 git commit
        Console.WriteLine("\n🤖 LLM calls: git_commit(message='Saved revenue data')");
        var gitResult = _activeTools["git_commit"].ExecuteAsync(new Dictionary<string, object?>
        {
            ["message"] = "Saved revenue data"
        });
        gitResult.Wait();
        Console.WriteLine($"   → {gitResult.Result.Output}");

        Console.WriteLine($"\n✅ Final active tools: {string.Join(", ", _activeTools.Keys)}");
        Assert.Equal(4, _activeTools.Count);
    }

    [Fact]
    public void ToolSearch_NoMatch_ShouldReturnEmpty()
    {
        // Act
        var result = _toolSearch.ExecuteAsync(new Dictionary<string, object?>
        {
            ["query"] = "nonexistent_tool_xyz"
        });

        result.Wait();

        // Assert
        Assert.Contains("No tools matched", result.Result.Output);
        Assert.False(result.Result.Output.Contains("Success: true") || result.Result.Output.Contains("\"success\":true"));
    }

    #endregion

    #region 边界条件测试

    [Fact]
    public void Catalog_LimitTo50_ShouldTruncateWhenExceeds()
    {
        // Arrange
        for (int i = 0; i < 60; i++)
        {
            _registry.Register(new ToolDefinition(
                $"extra_tool_{i}",
                $"Extra test tool number {i}",
                ToolCategory.Analysis,
                ToolLoading.Deferred,
                [3],
                async (_, _) => new CodexToolResult { Status = ToolResultStatus.Success, Output = "ok" }
            ));
        }

        // Act
        var catalog = _registry.Catalog();

        // Assert
        var lines = catalog.Split('\n').Where(l => l.Trim().StartsWith("|")).ToList();
        Assert.Equal(52, lines.Count); // 1 header + 1 separator + 50 tools
        Assert.DoesNotContain("extra_tool_59", catalog); // 应该被截断
    }

    [Fact]
    public void Search_CaseInsensitive_ShouldMatchRegardlessOfCase()
    {
        // Act
        var matches1 = _registry.Search("SQL");
        var matches2 = _registry.Search("sql");
        var matches3 = _registry.Search("Sql");

        // Assert
        Assert.Equal(matches1.Count, matches2.Count);
        Assert.Equal(matches2.Count, matches3.Count);
        Assert.Single(matches1);
    }

    #endregion
}

/// <summary>
/// 增强版工具注册表，支持搜索和按需加载
/// </summary>
public class ToolRegistryWithSearch
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);
    public const int CatalogLimit = 50;

    public int ToolCount => _tools.Count;

    public void Register(ToolDefinition tool)
    {
        _tools[tool.Name] = tool;
    }

    public void Clear()
    {
        _tools.Clear();
    }

    /// <summary>
    /// 用关键词或正则搜索工具，返回匹配的完整定义
    /// </summary>
    public List<ToolDefinition> Search(string query)
    {
        try
        {
            var pattern = new System.Text.RegularExpressions.Regex(query, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return _tools.Values.Where(t =>
                pattern.IsMatch(t.Name) ||
                pattern.IsMatch(t.Description)
            ).ToList();
        }
        catch
        {
            // 如果正则表达式无效，退化为简单的包含匹配
            return _tools.Values.Where(t =>
                t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }
    }

    public List<DelegatingCodexTool> GetAlwaysOnTools()
    {
        return _tools.Values.Where(t => t.Loading == ToolLoading.AlwaysOn)
            .Select(t => new DelegatingCodexTool(t))
            .ToList();
    }

    public List<ToolDefinition> GetAllToolDefinitions()
    {
        return _tools.Values.ToList();
    }

    public ToolDefinition? Get(string name)
    {
        return _tools.TryGetValue(name, out var tool) ? tool : null;
    }

    /// <summary>
    /// 返回精简目录（name + 一句话描述），注入 system prompt 用
    /// </summary>
    public string Catalog()
    {
        var lines = new List<string>
        {
            "| Tool | Description |",
            "|------|-------------|"
        };

        foreach (var t in _tools.Values.Take(CatalogLimit))
        {
            lines.Add($"| {t.Name} | {t.Description} |");
        }

        return string.Join("\n", lines);
    }
}

/// <summary>
/// tool_search 元工具：LLM 调用这个来按需加载工具
/// </summary>
public class ToolSearchMetaTool
{
    private readonly ToolRegistryWithSearch _registry;
    private readonly Dictionary<string, ICodexTool> _activeTools;

    public ToolSearchMetaTool(ToolRegistryWithSearch registry, Dictionary<string, ICodexTool> activeTools)
    {
        _registry = registry;
        _activeTools = activeTools;
    }

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var query = arguments["query"]?.ToString() ?? "";
        var matches = _registry.Search(query);

        if (!matches.Any())
        {
            return new CodexToolResult
            {
                Status = ToolResultStatus.Failed,
                Output = $"No tools matched '{query}'"
            };
        }

        var result = new List<object>();
        foreach (var t in matches)
        {
            // 将工具注入到当前 agentic round 的可用工具集
            _activeTools[t.Name] = new DelegatingCodexTool(t);

            result.Add(new
            {
                name = t.Name,
                description = t.Description,
                category = t.Category.ToString()
            });
        }

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            found = result.Count,
            tools = result,
            active_count = _activeTools.Count
        }, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = false
        });

        return new CodexToolResult
        {
            Status = ToolResultStatus.Success,
            Output = json
        };
    }
}

/// <summary>
/// 工具定义数据结构
/// </summary>
public class ToolDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public ToolCategory Category { get; set; }
    public ToolLoading Loading { get; set; }
    public IReadOnlyList<int> AllowedStages { get; set; } = new List<int>();
    public Func<Dictionary<string, object?>, CancellationToken, Task<CodexToolResult>> Executor { get; set; } = default!;

    // 主构造函数，支持位置参数
    public ToolDefinition(string name, string description, ToolCategory category, ToolLoading loading, IReadOnlyList<int> allowedStages, Func<Dictionary<string, object?>, CancellationToken, Task<CodexToolResult>> executor)
    {
        Name = name;
        Description = description;
        Category = category;
        Loading = loading;
        AllowedStages = allowedStages;
        Executor = executor;
    }
}

/// <summary>
/// 工具加载策略
/// </summary>
public enum ToolLoading
{
    AlwaysOn,   // 总是加载，每次请求都注入
    Deferred    // 延迟加载，需要通过 tool_search 激活
}

/// <summary>
/// 将 ToolDefinition 转换为 ICodexTool 的委托包装器
/// </summary>
public class DelegatingCodexTool : ICodexTool
{
    private readonly ToolDefinition _definition;

    public DelegatingCodexTool(ToolDefinition definition)
    {
        _definition = definition;
    }

    public string Name => _definition.Name;
    public CodexFlow.Core.Execution.ToolCapability Capability => CodexFlow.Core.Execution.ToolCapability.ReadOnly;
    public string Description => _definition.Description;
    public ToolCategory Category => _definition.Category;
    public IReadOnlyList<int> AllowedStages => _definition.AllowedStages;

    public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        return _definition.Executor(arguments, ct);
    }
}
