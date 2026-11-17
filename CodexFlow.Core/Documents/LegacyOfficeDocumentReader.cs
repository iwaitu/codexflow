// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;

namespace CodexFlow.Core.Documents;

/// <summary>
/// Reads legacy binary <c>.doc</c> and <c>.ppt</c> files by converting them via LibreOffice when available,
/// falling back to bounded UTF-16LE/ASCII text extraction with an explicit warning when LibreOffice is absent.
/// </summary>
public sealed class LegacyOfficeDocumentReader : IDocumentContentReader
{
    private readonly ILibreOfficeDocumentConverter _converter;
    private readonly WordDocumentReader _wordReader;
    private readonly PresentationDocumentReader _presentationReader;

    public LegacyOfficeDocumentReader(
        ILibreOfficeDocumentConverter converter,
        WordDocumentReader wordReader,
        PresentationDocumentReader presentationReader)
    {
        _converter = converter;
        _wordReader = wordReader;
        _presentationReader = presentationReader;
    }

    public bool CanRead(DocumentFormatKind format) => format is DocumentFormatKind.Doc or DocumentFormatKind.Ppt;

    public async Task<DocumentReadResult> ReadAsync(string filePath, DocumentReadOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        var format = DocumentFileFormats.ResolveFormat(filePath);
        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            return DocumentReadResult.Failed(format, $"文件不存在: {filePath}");
        }

        if (info.Length == 0)
        {
            return DocumentReadResult.Failed(format, "文件为空 (0 B)。");
        }

        if (info.Length > options.MaxFileBytes)
        {
            return DocumentReadResult.Failed(format, $"文件大小 ({info.Length} 字节) 超过解析上限 ({options.MaxFileBytes} 字节)。");
        }

        var targetExt = format == DocumentFormatKind.Doc ? "docx" : "pptx";
        var conversion = await _converter.ConvertAsync(filePath, targetExt, ct).ConfigureAwait(false);
        try
        {
            if (conversion.Success && !string.IsNullOrWhiteSpace(conversion.ConvertedFilePath))
            {
                var convertedResult = format == DocumentFormatKind.Doc
                    ? await _wordReader.ReadAsync(conversion.ConvertedFilePath, options, ct).ConfigureAwait(false)
                    : await _presentationReader.ReadAsync(conversion.ConvertedFilePath, options, ct).ConfigureAwait(false);

                return convertedResult with { Format = format };
            }
        }
        finally
        {
            LibreOfficeDocumentConverter.TryDeleteDirectory(conversion.TempDirectoryToCleanup);
        }

        // Fallback: best-effort printable run scan when LibreOffice is unavailable or conversion failed.
        var fallbackText = await ExtractPrintableRunsFallbackAsync(filePath, options.MaxOutputChars, ct).ConfigureAwait(false);
        var reason = conversion.ErrorMessage ?? "LibreOffice 转换不可用。";
        var warning = $"老式二进制 {Path.GetExtension(filePath)} 未能通过 LibreOffice 完整转换（{reason}），以下为降级纯文本扫描结果；建议另存为 .{targetExt} 以获得完整排版与表格。";

        if (string.IsNullOrWhiteSpace(fallbackText))
        {
            return DocumentReadResult.Failed(format, warning);
        }

        return new DocumentReadResult(
            Success: true,
            Format: format,
            Text: $"[提示] {warning}\n\n{fallbackText}",
            Warnings: [warning]);
    }

    private static async Task<string> ExtractPrintableRunsFallbackAsync(
        string filePath,
        int maxOutputChars,
        CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        var runs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var totalChars = 0;

        // Scan UTF-16LE text runs (common in MS-DOC/MS-PPT streams).
        CollectUtf16LeRuns(bytes, runs, seen, maxOutputChars, ref totalChars);

        if (totalChars < 128)
        {
            CollectAsciiRuns(bytes, runs, seen, maxOutputChars, ref totalChars);
        }

        return string.Join(Environment.NewLine, runs);
    }

    private static void CollectUtf16LeRuns(
        byte[] bytes,
        List<string> runs,
        HashSet<string> seen,
        int maxOutputChars,
        ref int totalChars)
    {
        for (var offset = 0; offset <= 1; offset++)
        {
            var sb = new StringBuilder();
            for (var i = offset; i + 1 < bytes.Length; i += 2)
            {
                var ch = (char)(bytes[i] | (bytes[i + 1] << 8));
                if (IsReadableCharacter(ch))
                {
                    sb.Append(ch);
                }
                else
                {
                    FlushRun(sb, minLength: 4, runs, seen, maxOutputChars, ref totalChars);
                    if (totalChars >= maxOutputChars)
                    {
                        return;
                    }
                }
            }

            FlushRun(sb, minLength: 4, runs, seen, maxOutputChars, ref totalChars);
            if (totalChars >= maxOutputChars)
            {
                return;
            }
        }
    }

    private static void CollectAsciiRuns(
        byte[] bytes,
        List<string> runs,
        HashSet<string> seen,
        int maxOutputChars,
        ref int totalChars)
    {
        var sb = new StringBuilder();
        foreach (var b in bytes)
        {
            if (b is >= 0x20 and <= 0x7E or (byte)'\t')
            {
                sb.Append((char)b);
            }
            else
            {
                FlushRun(sb, minLength: 8, runs, seen, maxOutputChars, ref totalChars);
                if (totalChars >= maxOutputChars)
                {
                    return;
                }
            }
        }

        FlushRun(sb, minLength: 8, runs, seen, maxOutputChars, ref totalChars);
    }

    private static void FlushRun(
        StringBuilder sb,
        int minLength,
        List<string> runs,
        HashSet<string> seen,
        int maxOutputChars,
        ref int totalChars)
    {
        if (sb.Length == 0)
        {
            return;
        }

        var candidate = sb.ToString().Trim();
        sb.Clear();

        if (candidate.Length < minLength || IsBoilerplateOleNoise(candidate) || !seen.Add(candidate))
        {
            return;
        }

        runs.Add(candidate);
        totalChars += candidate.Length + 1;
        if (totalChars > maxOutputChars)
        {
            totalChars = maxOutputChars;
        }
    }

    private static bool IsReadableCharacter(char ch)
        => ch is '\t' or (>= ' ' and <= '~') or (>= '\u4E00' and <= '\u9FFF') or (>= '\u3000' and <= '\u303F') or (>= '\uFF00' and <= '\uFFEF');

    private static bool IsBoilerplateOleNoise(string s)
        => s.StartsWith("Microsoft ", StringComparison.Ordinal)
           || s.Equals("Root Entry", StringComparison.Ordinal)
           || s.Equals("WordDocument", StringComparison.Ordinal)
           || s.Equals("PowerPoint Document", StringComparison.Ordinal)
           || s.Equals("SummaryInformation", StringComparison.Ordinal)
           || s.Equals("DocumentSummaryInformation", StringComparison.Ordinal);
}
