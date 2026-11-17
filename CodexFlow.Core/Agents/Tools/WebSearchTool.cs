// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 使用内置 HttpClient 执行 Web 搜索的工具 (免 API Key 版)。
/// </summary>
public class WebSearchTool : ICodexTool
{
    private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<WebSearchTool> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public string Name => "web_search";
    public string Description => "执行互联网搜索，查找实时信息、技术文档、最佳实践或问题排查方案。参数: query (搜索关键词)。如果是针对特定技术（如 C#），建议在关键词中指明，或直接包含 site:learn.microsoft.com 等限定符。Few-shot: web_search({\"query\":\"ASP.NET Core file upload security best practices\"})。";
    public ToolCategory Category => ToolCategory.Analysis;
    public IReadOnlyList<int> AllowedStages { get; } = [1, 2, 3, 4, 5]; // Available across most stages

    public WebSearchTool(ILogger<WebSearchTool> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var query = arguments.GetValueOrDefault("query")?.ToString();
        if (string.IsNullOrWhiteSpace(query))
        {
            return CodexToolResult.Error("Missing required parameter: query");
        }

        StructuredLog.Information(_logger, "web_search: Executing search for '{Query}'", query);

        using var client = _httpClientFactory.CreateClient();
        var (results, primaryError) = await TryExecuteProviderAsync(
            query,
            "DuckDuckGo",
            token => SendDuckDuckGoRequestAsync(client, query, token),
            ParseDuckDuckGoLite,
            ct).ConfigureAwait(false);

        string? fallbackError = null;
        if (results.Count == 0)
        {
            (results, fallbackError) = await TryExecuteProviderAsync(
                query,
                "Bing",
                token => SendBingRequestAsync(client, query, token),
                ParseBingHtml,
                ct).ConfigureAwait(false);
        }

        if (results.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(primaryError) && !string.IsNullOrWhiteSpace(fallbackError))
            {
                return CodexToolResult.Error($"搜索失败: {primaryError}；备用搜索源也失败: {fallbackError}");
            }

            if (!string.IsNullOrWhiteSpace(primaryError) && fallbackError is not null)
            {
                return CodexToolResult.Error($"搜索失败: {fallbackError}");
            }

            return CodexToolResult.Succeeded("未找到相关结果。请尝试更换搜索词或放宽 site 限制。");
        }

        var output = string.Join("\n\n", results.Take(5).Select((r, i) => $"### {i + 1}. {r.Title}\n**URL**: {r.Url}\n**Snippet**: {r.Snippet}"));
        return CodexToolResult.Succeeded($"找到以下搜索结果：\n\n{output}");
    }

    private async Task<(List<SearchResult> Results, string? Error)> TryExecuteProviderAsync(
        string query,
        string providerName,
        Func<CancellationToken, Task<HttpResponseMessage>> sendAsync,
        Func<string, List<SearchResult>> parseHtml,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);

        try
        {
            using var response = await sendAsync(timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            return (parseHtml(html), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            StructuredLog.Warning(_logger, "web_search ({Provider}) timed out for query '{Query}'", providerName, query);
            return ([], $"搜索请求超时（超过 {(int)RequestTimeout.TotalSeconds} 秒）");
        }
        catch (HttpRequestException ex)
        {
            StructuredLog.Warning(_logger, ex, "web_search ({Provider}) HTTP request failed for query '{Query}'", providerName, query);
            return ([], ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(_logger, ex, "web_search ({Provider}) failed for query '{Query}'", providerName, query);
            return ([], ex.Message);
        }
        catch (RegexMatchTimeoutException ex)
        {
            StructuredLog.Error(_logger, ex, "web_search ({Provider}) regex failed for query '{Query}'", providerName, query);
            return ([], ex.Message);
        }
    }

    private static async Task<HttpResponseMessage> SendDuckDuckGoRequestAsync(HttpClient client, string query, CancellationToken ct)
    {
        // 使用 DuckDuckGo Lite 作为免 key 的主数据源
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://lite.duckduckgo.com/lite/", UriKind.Absolute))
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", query)])
        };
        request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendBingRequestAsync(HttpClient client, string query, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://www.bing.com/search?q={encoded}&setlang=zh-CN", UriKind.Absolute));
        request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static List<SearchResult> ParseDuckDuckGoLite(string html)
    {
        var results = new List<SearchResult>();

        // DuckDuckGo Lite HTML 结构极其简单，可以通过正则快速提取
        // 提取标题和URL: <a class="result-url" href="...">...</a> (有时包裹在 td 中)
        // 提取摘要: <td class="result-snippet">...</td>

        var rowRegex = new Regex(@"<tr.*?>(.*?)</tr>", RegexOptions.Singleline);
        var titleUrlRegex = new Regex(@"<a class=""result-url"" href=""([^""]+)"".*?>(.*?)</a>", RegexOptions.Singleline);
        var snippetRegex = new Regex(@"<td class=""result-snippet"".*?>(.*?)</td>", RegexOptions.Singleline);

        SearchResult? currentResult = null;

        foreach (Match rowMatch in rowRegex.Matches(html))
        {
            var rowHtml = rowMatch.Groups[1].Value;

            var tuMatch = titleUrlRegex.Match(rowHtml);
            if (tuMatch.Success)
            {
                currentResult = new SearchResult
                {
                    Url = tuMatch.Groups[1].Value,
                    Title = CleanHtml(tuMatch.Groups[2].Value)
                };
            }
            else if (currentResult != null)
            {
                var sMatch = snippetRegex.Match(rowHtml);
                if (sMatch.Success)
                {
                    currentResult.Snippet = CleanHtml(sMatch.Groups[1].Value);
                    results.Add(currentResult);
                    currentResult = null; // 重置以匹配下一组
                }
            }
        }

        return results;
    }

    private static List<SearchResult> ParseBingHtml(string html)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return results;
        }

        var itemRegex = new Regex(@"<li\b[^>]*class=""[^""]*\bb_algo\b[^""]*""[^>]*>(.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var titleLinkRegex = new Regex(@"<h2\b[^>]*>.*?<a\b[^>]*href=""([^""]+)""[^>]*>(.*?)</a>.*?</h2>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var snippetRegex = new Regex(@"<div\b[^>]*class=""[^""]*\bb_caption\b[^""]*""[^>]*>(.*?)</div>|<p\b[^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match itemMatch in itemRegex.Matches(html))
        {
            var block = itemMatch.Groups[1].Value;
            var linkMatch = titleLinkRegex.Match(block);
            if (!linkMatch.Success)
            {
                continue;
            }

            var url = System.Net.WebUtility.HtmlDecode(linkMatch.Groups[1].Value).Trim();
            var title = CleanHtml(linkMatch.Groups[2].Value);
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var snippetMatch = snippetRegex.Match(block);
            var rawSnippet = snippetMatch.Success
                ? (!string.IsNullOrEmpty(snippetMatch.Groups[1].Value) ? snippetMatch.Groups[1].Value : snippetMatch.Groups[2].Value)
                : string.Empty;

            results.Add(new SearchResult
            {
                Url = url,
                Title = title,
                Snippet = CleanHtml(rawSnippet)
            });
        }

        return results;
    }

    private static string CleanHtml(string input)
    {
        input = Regex.Replace(input, "<.*?>", string.Empty); // 移除标签
        input = input.Replace("&#x27;", "'", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
        input = input.Replace("\n", " ", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
        return Regex.Replace(input, @"\s{2,}", " "); // 将多个空格替换为一个
    }

    private sealed class SearchResult
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Snippet { get; set; } = string.Empty;
    }
}

