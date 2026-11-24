// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.CodeAnalysis;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;

namespace CodexFlow.Core.Agents;

public class RoslynCodeAnalysisTool : ICodexTool
{
    private readonly ICodeAnalysisService _analysisService;

    public string Name => "analyze_code";
    public string Description => "使用 Roslyn 引擎对指定的 C# 代码片段进行静态分析，返回编译错误和警告诊断信息。Few-shot: analyze_code({\"code\":\"class A { void M(){ int x = \\\"1\\\"; } }\"})。";
    public ToolCategory Category => ToolCategory.Analysis;
    public IReadOnlyList<int> AllowedStages => new[] { 1, 3, 4 }; // 允许在分析、执行、验证阶段调用

    public RoslynCodeAnalysisTool(ICodeAnalysisService analysisService)
    {
        _analysisService = analysisService;
    }

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!arguments.TryGetValue("code", out var codeObj) || codeObj is not string code)
        {
            return CodexToolResult.Error("缺少必需的参数 'code'。");
        }

        List<CodeDiagnostic> diagnostics;
        try
        {
            var analysis = _analysisService is ICancellableSnippetAnalysis cancellable
                ? cancellable.AnalyzeSnippetAsync(code, ct)
                : _analysisService.AnalyzeCodeAsync(code);
            diagnostics = await analysis.ConfigureAwait(false);
        }
        catch (CodeAnalysisUnavailableException exception)
        {
            return CodexToolResult.Error(exception.Message);
        }

        if (diagnostics.Count == 0) return CodexToolResult.Succeeded("✅ 代码分析通过，未发现错误或警告。");

        var sb = new StringBuilder();
        sb.AppendLine("❌ 发现以下诊断信息：");
        foreach (var diag in diagnostics)
        {
            sb.AppendLine(FormattableString.Invariant($"- [{diag.Severity}] {diag.Id}: {diag.Message} (行: {diag.Line}, 列: {diag.Column})"));
        }

        return CodexToolResult.Succeeded(sb.ToString(), ToolResultMetadata.From(diagnostics, SourceGeneratedToolJsonContext.Default.ListCodeDiagnostic));
    }
}
