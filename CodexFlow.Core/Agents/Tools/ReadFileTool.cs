// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Documents;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Hashline.Services;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 读取工作区文件内容（替代 MCP read_file），支持普通模式、Hashline 模式以及办公文档/PDF按页读取。
/// </summary>
public class ReadFileTool : ICodexTool
{
    private const int MaxAutoReadLines = 500; // 默认读取前 500 行，防止撑爆上下文
    private const long MaxStandaloneImageBytes = 10L * 1024 * 1024;
    private readonly ILogger<ReadFileTool> _logger;
    private readonly IHashlineFileService? _hashlineService;
    private readonly HashlineOptions? _hashlineOptions;
    private readonly DocumentReaderRouter _documentRouter;
    private readonly IConfiguration? _configuration;
    private readonly IModelSettingsService? _modelSettingsService;

    public ReadFileTool(
        ILogger<ReadFileTool> logger,
        IHashlineFileService? hashlineService = null,
        HashlineOptions? hashlineOptions = null,
        DocumentReaderRouter? documentRouter = null,
        IConfiguration? configuration = null,
        IModelSettingsService? modelSettingsService = null)
    {
        _logger = logger;
        _hashlineService = hashlineService;
        _hashlineOptions = hashlineOptions;
        _documentRouter = documentRouter ?? DocumentReaderRouter.CreateDefault();
        _configuration = configuration;
        _modelSettingsService = modelSettingsService;
    }

    public string Name => "ivilson_read";
    public string Description => "读取工作区文件内容，支持普通模式、Hashline 模式及日常办公文档（docx/doc/xlsx/xls/pptx/ppt/pdf）按页提取。\n" +
        "参数（JSON object）：\n" +
        "  - path (string, 必填): 相对于工作区根目录的文件路径\n" +
        "  - mode (string, 可选): 读取模式，\"plain\"（默认）或 \"hashline\"（用于精准编辑）\n" +
        "  - start_line (int, 可选): 起始行号（从1开始），不指定则从文件开头读取\n" +
        "  - end_line (int, 可选): 结束行号，不指定则读取到文件末尾（普通模式）\n" +
        "  - start_page (int, 可选): 办公文档/PDF 的起始页码或 Sheet 序号（从1开始）\n" +
        "  - end_page (int, 可选): 办公文档/PDF 的结束页码或 Sheet 序号（从1开始）\n" +
        "  - window_start_line (int, 可选): Hashline 分段读取起始行（1-based）\n" +
        "  - window_line_count (int, 可选): Hashline 分段读取返回的最大行数\n" +
        "\n" +
        "【Hashline 模式说明】\n" +
        "当你准备修改既有文件且担心 patch 上下文不稳定、目标片段重复、或文件可能并发变化时，\n" +
        "优先使用 mode=\"hashline\" 获取带锚点快照，再基于该快照发起编辑。\n" +
        "\n" +
        "【高风险文件策略】\n" +
        "对以下高风险文件，修改前必须使用 mode=\"hashline\" 读取：\n" +
        "  - Program.cs, Program.*.cs, Startup.cs\n" +
        "  - *.csproj, *.sln, Directory.Build.props\n" +
        "  - appsettings.json, appsettings.*.json\n" +
        "  - Auth/Identity 相关文件\n" +
        "\n" +
        "Hashline 模式返回：\n" +
        "  - snapshotId: 快照标识\n" +
        "  - fileFingerprint: 文件指纹（用于乐观并发控制）\n" +
        "  - renderedText: 带行锚点的文本，格式如 \"1#A1B2C3|using System;\"\n" +
        "  - encodingName: 文件编码\n" +
        "  - hasBom: 是否有 BOM\n" +
        "  - newLineStyle: 换行风格\n" +
        "\n" +
        "【后续编辑错误恢复】\n" +
        "如果后续 apply_patch/ivilson_smart_patch 报错：\n" +
        "  - FILE_FINGERPRINT_MISMATCH: 文件已被并发修改，必须重新 ivilson_read({\"path\":\"...\", \"mode\":\"hashline\"}) 获取最新快照\n" +
        "  - ANCHOR_MISMATCH: 行内容与锚点不匹配，必须重新读取快照，禁止猜测旧上下文\n" +
        "  - LINE_OUT_OF_RANGE: 行号超出范围，重新读取获取当前行数\n" +
        "\n" +
        "后续编辑必须引用刚读取到的 snapshotId 与 fileFingerprint。\n" +
        "\n" +
        "如果文件路径不确定，先用 `search_file_index` 或 `ivilson_ls({\"path\":\".\"})` 定位真实路径，不要猜测 `src`/`app`/`lib`。\n" +
        "\n" +
        "返回：文件内容文本，包含文件路径、行数和大小。\n" +
        "调用示例：\n" +
        "  ivilson_read({\"path\":\"Program.cs\"})\n" +
        "  ivilson_read({\"path\":\"CodexFlow.Core/Runtime/QreV2BackedQueryRuntimeEngine.cs\", \"start_line\":1, \"end_line\":100})\n" +
        "  ivilson_read({\"path\":\"Program.cs\", \"mode\":\"hashline\"})";
    public ToolCategory Category => ToolCategory.Read;
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var relativePath = arguments.GetValueOrDefault("path")?.ToString();
        var explicitMode = arguments.GetValueOrDefault("mode")?.ToString()?.ToLowerInvariant();
        var startLine = TryGetInt(arguments.GetValueOrDefault("start_line"));
        var endLine = TryGetInt(arguments.GetValueOrDefault("end_line"));
        var startPage = TryGetInt(arguments.GetValueOrDefault("start_page"));
        var endPage = TryGetInt(arguments.GetValueOrDefault("end_page"));
        var (windowStartLine, windowLineCount) = ParseWindowArguments(arguments);

        if ((string.IsNullOrEmpty(workspacePath) && string.IsNullOrEmpty(projectRoot)) || string.IsNullOrEmpty(relativePath))
            return CodexToolResult.Error("Missing workspace_path or path.");

        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot) || !Directory.Exists(baseRoot))
            return CodexToolResult.Error("Workspace root does not exist.");

        var normalizedPath = relativePath;
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, normalizedPath));

        if (!File.Exists(fullPath))
        {
            var fallbackPath = ToolPathResolver.NormalizeDuplicateRepoPrefix(relativePath, baseRoot);
            if (!string.Equals(fallbackPath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                var retriedFullPath = Path.GetFullPath(Path.Combine(baseRoot, fallbackPath));
                if (File.Exists(retriedFullPath))
                {
                    StructuredLog.Information(_logger,
                        "Normalized ivilson_read path by stripping duplicated repo prefix: {OriginalPath} -> {NormalizedPath}",
                        relativePath,
                        fallbackPath);
                    normalizedPath = fallbackPath;
                    fullPath = retriedFullPath;
                }
            }
        }

        if (!ToolPathResolver.IsSafeReadTarget(fullPath, baseRoot, out var unsafeReason))
            return CodexToolResult.Error(unsafeReason);

        if (!File.Exists(fullPath))
            return CodexToolResult.Error(
                $"File not found: {relativePath}",
                systemHint: "路径不确定时，先用 `search_file_index` 或 `ivilson_ls({\"path\":\".\"})` 定位真实文件，不要假设 `src`/`app`/`lib` 存在。");

        if (DocumentFileFormats.IsImageFile(fullPath))
        {
            return await ExecuteImageReadAsync(fullPath, normalizedPath, ct).ConfigureAwait(false);
        }

        if (_documentRouter.CanRead(fullPath))
        {
            if (explicitMode == "hashline")
            {
                return CodexToolResult.Error(
                    $"办公文档不支持 hashline 锚点编辑模式，请使用默认 plain 模式读取: {normalizedPath}");
            }

            return await ExecuteDocumentModeAsync(fullPath, normalizedPath, startPage, endPage, startLine, endLine, ct)
                .ConfigureAwait(false);
        }

        // 决定使用哪种模式：显式指定 > 配置默认值
        var useHashlineMode = explicitMode switch
        {
            "hashline" => true,
            "plain" => false,
            _ => _hashlineOptions?.IsHashlinePipelineEnabled() ?? false
        };

        // Hashline 模式
        if (useHashlineMode && _hashlineService != null)
        {
            return await ExecuteHashlineModeAsync(
                fullPath,
                normalizedPath,
                baseRoot,
                windowStartLine,
                windowLineCount,
                ct).ConfigureAwait(false);
        }

        // 普通模式
        return await ExecutePlainModeAsync(fullPath, normalizedPath, startLine, endLine, ct).ConfigureAwait(false);
    }

    private async Task<CodexToolResult> ExecutePlainModeAsync(
        string fullPath, string normalizedPath,
        int? startLine, int? endLine,
        CancellationToken ct)
    {
        try
        {
            var allLines = await File.ReadAllLinesAsync(fullPath, ct).ConfigureAwait(false);
            var info = new FileInfo(fullPath);
            return BuildSlicedTextResult(normalizedPath, info.Length, allLines, startLine, endLine);
        }
        catch (IOException ex)
        {
            StructuredLog.Error(_logger, ex, "ivilson_read failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(_logger, ex, "ivilson_read failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Error(_logger, ex, "ivilson_read failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            StructuredLog.Error(_logger, ex, "ivilson_read failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
    }

    private async Task<CodexToolResult> ExecuteDocumentModeAsync(
        string fullPath,
        string normalizedPath,
        int? startPage,
        int? endPage,
        int? startLine,
        int? endLine,
        CancellationToken ct)
    {
        var isMultimodal = ResolveIsMultimodalEnabled();
        var docOptions = new DocumentReadOptions(
            StartPage: startPage,
            EndPage: endPage,
            ExtractImages: isMultimodal);

        var docResult = await _documentRouter.ReadAsync(fullPath, docOptions, ct).ConfigureAwait(false);
        if (!docResult.Success)
        {
            return CodexToolResult.Error(docResult.ErrorMessage ?? $"读取办公文档失败: {normalizedPath}");
        }

        var info = new FileInfo(fullPath);
        var normalizedText = docResult.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var allLines = normalizedText.Split('\n');
        var (slicedContent, selStart, selEnd, totalLines, isTruncated) = SliceLines(allLines, startLine, endLine);

        var header = docResult.TotalPages.HasValue
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"--- Document: {normalizedPath} ({docResult.Format}, pages {docResult.StartPage ?? 1}-{docResult.EndPage ?? docResult.TotalPages.Value}/{docResult.TotalPages.Value}, lines {selStart}-{selEnd}/{totalLines}, {FormatSize(info.Length)}) ---")
            : $"--- Document: {normalizedPath} ({docResult.Format}, lines {selStart}-{selEnd}/{totalLines}, {FormatSize(info.Length)}) ---";

        var outputBuilder = new StringBuilder();
        outputBuilder.AppendLine(header);
        outputBuilder.Append(slicedContent);

        if (isTruncated || selEnd < totalLines)
        {
            outputBuilder.AppendLine();
            outputBuilder.Append($"[NOTE] Content truncated. Total lines: {totalLines}. Use 'start_line' and 'end_line' (or 'start_page'/'end_page') to read more.");
        }

        var attachedVisualCount = 0;
        if (isMultimodal && docResult.SafeImages.Count > 0 && AmbientTurnVisualAttachmentContext.Current is { } visualStore)
        {
            var fileName = Path.GetFileName(normalizedPath);
            foreach (var img in docResult.SafeImages)
            {
                var registered = visualStore.Register(
                    fileName,
                    img.MimeType,
                    img.Bytes,
                    pageNumber: img.PageNumber,
                    imageIndexOnPage: img.ImageIndexOnPage);
                if (registered is not null)
                {
                    attachedVisualCount++;
                    outputBuilder.AppendLine();
                    outputBuilder.Append(string.Create(
                        CultureInfo.InvariantCulture,
                        $"[PDF图像引用: {fileName} 第 {img.PageNumber} 页 图片 #{img.ImageIndexOnPage}] {TurnVisualAttachmentStore.FormatMarker(registered)}"));
                }
            }
        }

        return CodexToolResult.Succeeded(
            outputBuilder.ToString(),
            ToolResultMetadata.From(new DocumentReadMetadata(
                normalizedPath, docResult.Format.ToString(), docResult.TotalPages, docResult.StartPage, docResult.EndPage,
                totalLines, info.Length, selStart, selEnd, isTruncated || docResult.Truncated || selEnd < totalLines,
                docResult.HasImages, docResult.IsLikelyScanned, attachedVisualCount), SourceGeneratedToolJsonContext.Default.DocumentReadMetadata));
    }

    private async Task<CodexToolResult> ExecuteImageReadAsync(
        string fullPath,
        string normalizedPath,
        CancellationToken ct)
    {
        if (!ResolveIsMultimodalEnabled())
        {
            return CodexToolResult.Error(
                $"当前模型未启用多模态能力，无法读取图片文件 `{normalizedPath}`。请告知用户在「设置 -> 模型配置」中配置支持多模态的模型。");
        }

        var info = new FileInfo(fullPath);
        if (info.Length == 0)
        {
            return CodexToolResult.Error($"图片文件为空: {normalizedPath}");
        }

        if (info.Length > MaxStandaloneImageBytes)
        {
            return CodexToolResult.Error($"图片文件过大 ({FormatSize(info.Length)})，单张最大支持 {FormatSize(MaxStandaloneImageBytes)}。");
        }

        var visualStore = AmbientTurnVisualAttachmentContext.Current;
        if (visualStore is null)
        {
            return CodexToolResult.Error($"当前执行上下文不支持向模型注入图片内容: {normalizedPath}");
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
        var mime = DocumentFileFormats.ResolveImageMimeType(fullPath);
        var registered = visualStore.Register(Path.GetFileName(normalizedPath), mime, bytes);
        if (registered is null)
        {
            return CodexToolResult.Error($"本轮对话已达到最大附带图片数量限制，无法继续读取 `{normalizedPath}`。");
        }

        var output = $"--- Image: {normalizedPath} ({mime}, {FormatSize(info.Length)}) ---\n" +
                     $"图片已加载并将在下一步送入多模态模型：{TurnVisualAttachmentStore.FormatMarker(registered)}";
        return CodexToolResult.Succeeded(output, ToolResultMetadata.From(new ImageReadMetadata(normalizedPath, mime, info.Length, registered.Id), SourceGeneratedToolJsonContext.Default.ImageReadMetadata));
    }

    private bool ResolveIsMultimodalEnabled()
        => AmbientTurnVisualAttachmentContext.Current?.IsMultimodalEnabled
           ?? _modelSettingsService?.Read().SupportsMultimodal
           ?? ModelSettingsService.IsMultimodalConfigured(_configuration);

    private static CodexToolResult BuildSlicedTextResult(
        string normalizedPath,
        long fileSizeBytes,
        string[] allLines,
        int? startLine,
        int? endLine)
    {
        var (content, selStart, selEnd, totalLines, isTruncated) = SliceLines(allLines, startLine, endLine);
        var output = $"--- File: {normalizedPath} ({selStart}-{selEnd}/{totalLines} lines, {FormatSize(fileSizeBytes)}) ---\n{content}";
        if (isTruncated || selEnd < totalLines)
        {
            output += $"\n[NOTE] Content truncated. Total lines: {totalLines}. Use 'start_line' and 'end_line' to read more.";
        }

        return CodexToolResult.Succeeded(
            output,
            ToolResultMetadata.From(new TextReadMetadata(normalizedPath, totalLines, fileSizeBytes, selStart, selEnd, isTruncated || selEnd < totalLines), SourceGeneratedToolJsonContext.Default.TextReadMetadata));
    }

    private static (string Content, int StartLine, int EndLine, int TotalLines, bool IsAutoTruncated) SliceLines(
        string[] allLines,
        int? startLine,
        int? endLine)
    {
        var totalLines = allLines.Length;
        var selStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
        var selEnd = endLine.HasValue ? Math.Min(totalLines, endLine.Value) : totalLines;

        var isAutoTruncated = false;
        if (!startLine.HasValue && !endLine.HasValue && totalLines > MaxAutoReadLines)
        {
            selEnd = MaxAutoReadLines;
            isAutoTruncated = true;
        }

        var selectedLines = allLines.Skip(selStart - 1).Take(Math.Max(0, selEnd - selStart + 1));
        var content = string.Join(Environment.NewLine, selectedLines);
        return (content, selStart, selEnd, totalLines, isAutoTruncated);
    }

    private async Task<CodexToolResult> ExecuteHashlineModeAsync(
        string fullPath,
        string normalizedPath,
        string workspaceRoot,
        int? windowStartLine,
        int? windowLineCount,
        CancellationToken ct)
    {
        try
        {
            var snapshot = await _hashlineService!
                .ReadAsync(fullPath, workspaceRoot, windowStartLine, windowLineCount, ct)
                .ConfigureAwait(false);

            var resultContent = $"--- File (Hashline): {normalizedPath} ---\n" +
                $"SnapshotId: {snapshot.SnapshotId}\n" +
                $"Fingerprint: {snapshot.FileFingerprint}\n" +
                $"Lines: {snapshot.Lines.Count}\n" +
                $"TotalLines: {snapshot.TotalLineCount}\n" +
                $"Window: {snapshot.WindowStartLine}-{snapshot.WindowEndLine}\n" +
                $"Encoding: {snapshot.EncodingName}\n" +
                $"NewLine: {snapshot.NewLineStyle.Replace("\n", "\\n").Replace("\r\n", "\\r\\n")}\n" +
                $"BOM: {snapshot.HasBom}\n" +
                $"\n" +
                $"{snapshot.RenderedText}";

            return CodexToolResult.Succeeded(resultContent, ToolResultMetadata.From(new HashlineReadMetadata(
                normalizedPath, snapshot.SnapshotId, snapshot.FileFingerprint, snapshot.Lines.Count, snapshot.TotalLineCount,
                snapshot.WindowStartLine, snapshot.WindowEndLine, snapshot.IsPartialWindow, snapshot.EncodingName,
                snapshot.HasBom, snapshot.NewLineStyle, snapshot.RenderedText), SourceGeneratedToolJsonContext.Default.HashlineReadMetadata));
        }
        catch (HashlineException ex)
        {
            StructuredLog.Error(_logger, ex, "ivilson_read hashline mode failed: {Path}", normalizedPath);
            return CodexToolResult.Error($"Hashline error: {ex.ErrorCode} - {ex.Message}");
        }
        catch (Exception ex)
        {
            StructuredLog.Error(_logger, ex, "ivilson_read hashline mode failed: {Path}", normalizedPath);
            return CodexToolResult.Error($"Hashline error: {ex.Message}");
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        int i = 0;
        double size = bytes;
        while (i < units.Length - 1 && size >= 1024) { i++; size /= 1024; }
        return $"{size:F1} {units[i]}";
    }

    private static (int? WindowStartLine, int? WindowLineCount) ParseWindowArguments(Dictionary<string, object?> arguments)
    {
        var startLine = TryGetInt(arguments.GetValueOrDefault("window_start_line"));
        var lineCount = TryGetInt(arguments.GetValueOrDefault("window_line_count"));

        if (arguments.GetValueOrDefault("window") is Dictionary<string, object?> windowDict)
        {
            startLine ??= TryGetInt(windowDict.GetValueOrDefault("startLine"));
            lineCount ??= TryGetInt(windowDict.GetValueOrDefault("lineCount"));
        }
        else if (arguments.GetValueOrDefault("window") is JsonElement windowElement && windowElement.ValueKind == JsonValueKind.Object)
        {
            if (windowElement.TryGetProperty("startLine", out var startElement))
            {
                startLine ??= TryGetInt(startElement);
            }
            if (windowElement.TryGetProperty("lineCount", out var countElement))
            {
                lineCount ??= TryGetInt(countElement);
            }
        }

        return (startLine, lineCount);
    }

    private static int? TryGetInt(object? value)
    {
        return value switch
        {
            null => null,
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            double doubleValue when doubleValue is >= int.MinValue and <= int.MaxValue => (int)doubleValue,
            JsonElement { ValueKind: JsonValueKind.Number } numberElement when numberElement.TryGetInt32(out var jsonIntValue) => jsonIntValue,
            JsonElement { ValueKind: JsonValueKind.String } stringElement when int.TryParse(stringElement.GetString(), out var parsed) => parsed,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => null
        };
    }
}

internal sealed record TextReadMetadata(
    string FilePath, int Lines, long Size, int StartLine, int EndLine, bool IsTruncated);

internal sealed record DocumentReadMetadata(
    string FilePath, string Format, int? TotalPages, int? StartPage, int? EndPage,
    int Lines, long Size, int StartLine, int EndLine, bool IsTruncated,
    bool HasImages, bool IsLikelyScanned, int AttachedVisualCount);

internal sealed record ImageReadMetadata(string FilePath, string MimeType, long Size, string VisualId);

internal sealed record HashlineReadMetadata(
    string FilePath, string SnapshotId, string FileFingerprint, int Lines, int TotalLineCount,
    int WindowStartLine, int WindowEndLine, bool IsPartialWindow, string EncodingName,
    bool HasBom, string NewLineStyle, string RenderedText);
