// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Tokens;

namespace CodexFlow.Core.Documents;

/// <summary>
/// Reads PDF documents strictly page-by-page, extracting text and tagging every extracted image with its 1-based page number.
/// </summary>
public sealed class PdfDocumentReader : IDocumentContentReader
{
    private const int MinMeaningfulImageDimensionPx = 16;

    public bool CanRead(DocumentFormatKind format) => format == DocumentFormatKind.Pdf;

    public Task<DocumentReadResult> ReadAsync(string filePath, DocumentReadOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            return Task.FromResult(DocumentReadResult.Failed(DocumentFormatKind.Pdf, $"文件不存在: {filePath}"));
        }

        if (info.Length == 0)
        {
            return Task.FromResult(DocumentReadResult.Failed(DocumentFormatKind.Pdf, "PDF 文件为空 (0 B)。"));
        }

        if (info.Length > options.MaxFileBytes)
        {
            return Task.FromResult(DocumentReadResult.Failed(
                DocumentFormatKind.Pdf,
                $"PDF 文件大小 ({info.Length} 字节) 超过解析上限 ({options.MaxFileBytes} 字节)。"));
        }

        try
        {
            using var document = PdfDocument.Open(filePath);
            var totalPages = document.NumberOfPages;
            if (totalPages <= 0)
            {
                return Task.FromResult(new DocumentReadResult(
                    Success: true,
                    Format: DocumentFormatKind.Pdf,
                    Text: "(空 PDF 文档，总页数为 0)",
                    TotalPages: 0));
            }

            var (startPage, endPage, windowTruncated) = ResolvePageWindow(totalPages, options);
            var sb = new StringBuilder();
            var extractedImages = new List<DocumentExtractedImage>();
            var pageNotices = new List<DocumentPageImageNotice>();
            var warnings = new List<string>();
            var anyPageHasImages = false;
            var anyPageHasText = false;
            var textTruncated = false;

            for (var pageNumber = startPage; pageNumber <= endPage; pageNumber++)
            {
                ct.ThrowIfCancellationRequested();

                var page = document.GetPage(pageNumber);
                var rawText = ExtractPageText(page);
                var hasText = !string.IsNullOrWhiteSpace(rawText);
                if (hasText)
                {
                    anyPageHasText = true;
                }

                var pageImages = page.GetImages()
                    .Where(IsMeaningfulImage)
                    .ToList();
                var imageCount = pageImages.Count;
                if (imageCount > 0)
                {
                    anyPageHasImages = true;
                    pageNotices.Add(new DocumentPageImageNotice(pageNumber, imageCount, hasText));
                }

                var pageSection = new StringBuilder();
                pageSection.AppendLine(string.Create(CultureInfo.InvariantCulture, $"=== 第 {pageNumber} 页 / 共 {totalPages} 页 ==="));
                pageSection.AppendLine(hasText ? rawText : "(本页无可提取文本)");

                if (imageCount > 0)
                {
                    if (!options.ExtractImages)
                    {
                        pageSection.AppendLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"[第 {pageNumber} 页包含 {imageCount} 张图片；当前模型未启用多模态，无法读取图片内容]"));
                    }
                    else
                    {
                        var addedOnPage = 0;
                        var imageIndex = 0;
                        foreach (var pdfImage in pageImages)
                        {
                            ct.ThrowIfCancellationRequested();
                            imageIndex++;
                            if (extractedImages.Count >= options.MaxImages)
                            {
                                warnings.Add(string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"单次最多附带 {options.MaxImages} 张 PDF 图片，第 {pageNumber} 页及后续部分图片未注入；请缩小 start_page/end_page 页码范围继续查看。"));
                                break;
                            }

                            if (TryExtractImageBytes(pdfImage, options.MaxImageBytes, out var imgBytes, out var mimeType))
                            {
                                extractedImages.Add(new DocumentExtractedImage(
                                    PageNumber: pageNumber,
                                    ImageIndexOnPage: imageIndex,
                                    MimeType: mimeType,
                                    Bytes: imgBytes,
                                    Width: pdfImage.WidthInSamples,
                                    Height: pdfImage.HeightInSamples));
                                addedOnPage++;
                            }
                        }

                        if (addedOnPage > 0)
                        {
                            pageSection.AppendLine(string.Create(
                                CultureInfo.InvariantCulture,
                                $"[第 {pageNumber} 页已提取 {addedOnPage} 张图片供多模态模型识别（所属页码: 第 {pageNumber} 页）]"));
                        }
                        else
                        {
                            pageSection.AppendLine(string.Create(
                                CultureInfo.InvariantCulture,
                                $"[第 {pageNumber} 页包含 {imageCount} 张图片，但受格式或单张大小限制未能全部提取]"));
                        }
                    }
                }

                var block = pageSection.ToString().TrimEnd();
                if (sb.Length + block.Length + 4 > options.MaxOutputChars)
                {
                    textTruncated = true;
                    sb.AppendLine();
                    sb.AppendLine($"[NOTE] 文本输出达到上限 ({options.MaxOutputChars} 字符)，已停止继续拼接后续页；请使用 start_page/end_page 缩小页码范围读取。");
                    break;
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.AppendLine(block);
            }

            var isLikelyScanned = !anyPageHasText && anyPageHasImages;
            var headerNotes = new StringBuilder();
            headerNotes.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"[PDF 页码信息] 总页数: {totalPages}，本次读取: 第 {startPage}-{endPage} 页。"));

            if (isLikelyScanned && !options.ExtractImages)
            {
                headerNotes.AppendLine("[提示] 所读取页面均无可提取文本且包含图片（疑似扫描件/纯图片 PDF），当前模型非多模态模型，无法读取 PDF 中的图片内容。");
            }
            else if (anyPageHasImages && !options.ExtractImages)
            {
                headerNotes.AppendLine("[提示] 本次读取的 PDF 页面中包含内嵌图片，当前模型非多模态模型，仅能读取文字层，图片/图表内容已被跳过。");
            }

            foreach (var warning in warnings.Distinct(StringComparer.Ordinal))
            {
                headerNotes.AppendLine($"[提示] {warning}");
            }

            if (windowTruncated && endPage < totalPages)
            {
                headerNotes.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[分页提示] 仅读取第 {startPage}-{endPage} 页，如需继续请传入 start_page={endPage + 1}。"));
            }

            var finalText = headerNotes.ToString().TrimEnd() + "\n\n" + sb.ToString().Trim();
            return Task.FromResult(new DocumentReadResult(
                Success: true,
                Format: DocumentFormatKind.Pdf,
                Text: finalText,
                TotalPages: totalPages,
                StartPage: startPage,
                EndPage: endPage,
                Truncated: windowTruncated || textTruncated,
                HasImages: anyPageHasImages,
                IsLikelyScanned: isLikelyScanned,
                Images: extractedImages,
                PageImageNotices: pageNotices,
                Warnings: warnings));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or FormatException)
        {
            return Task.FromResult(DocumentReadResult.Failed(DocumentFormatKind.Pdf, $"解析 PDF 文件失败: {ex.Message}"));
        }
    }

    private static (int StartPage, int EndPage, bool WindowTruncated) ResolvePageWindow(int totalPages, DocumentReadOptions options)
    {
        var maxPages = Math.Max(1, options.MaxPagesPerRead);
        var start = options.StartPage.HasValue ? Math.Clamp(options.StartPage.Value, 1, totalPages) : 1;
        var requestedEnd = options.EndPage.HasValue
            ? Math.Clamp(options.EndPage.Value, start, totalPages)
            : Math.Min(totalPages, start + maxPages - 1);

        var cappedEnd = Math.Min(requestedEnd, start + maxPages - 1);
        var truncated = cappedEnd < requestedEnd || (!options.EndPage.HasValue && cappedEnd < totalPages);
        return (start, cappedEnd, truncated);
    }

    private static string ExtractPageText(Page page)
    {
        try
        {
            var structured = ContentOrderTextExtractor.GetText(page);
            if (!string.IsNullOrWhiteSpace(structured))
            {
                return structured.Trim();
            }
        }
        catch
        {
            // Fall back to basic page.Text when layout ordering encounters malformed PDF operators.
        }

        return (page.Text ?? string.Empty).Trim();
    }

    private static bool IsMeaningfulImage(IPdfImage image)
        => image.WidthInSamples >= MinMeaningfulImageDimensionPx
           && image.HeightInSamples >= MinMeaningfulImageDimensionPx;

    private static bool TryExtractImageBytes(
        IPdfImage image,
        long maxImageBytes,
        out byte[] bytes,
        out string mimeType)
    {
        bytes = [];
        mimeType = "image/png";

        try
        {
            if (image.TryGetPng(out var pngBytes) && pngBytes is { Length: > 0 } && pngBytes.Length <= maxImageBytes)
            {
                bytes = pngBytes;
                mimeType = "image/png";
                return true;
            }
        }
        catch
        {
            // Try JPEG raw stream fallback below.
        }

        try
        {
            if (IsJpegImage(image) && image.RawMemory.Length > 0 && image.RawMemory.Length <= maxImageBytes)
            {
                bytes = image.RawMemory.ToArray();
                mimeType = "image/jpeg";
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsJpegImage(IPdfImage image)
    {
        if (image.ImageDictionary is null)
        {
            return false;
        }

        if (!image.ImageDictionary.TryGet(NameToken.Filter, out var filterToken))
        {
            return false;
        }

        return filterToken is NameToken name && string.Equals(name.Data, "DCTDecode", StringComparison.OrdinalIgnoreCase);
    }
}
