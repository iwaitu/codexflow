// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WpText = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace CodexFlow.Core.Documents;

/// <summary>Extracts structured Markdown text and tables from OOXML Word (<c>.docx</c>) files.</summary>
public sealed class WordDocumentReader : IDocumentContentReader
{
    public bool CanRead(DocumentFormatKind format) => format == DocumentFormatKind.Docx;

    public Task<DocumentReadResult> ReadAsync(string filePath, DocumentReadOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        if (!ZipArchiveSafetyGuard.Validate(filePath, options.MaxFileBytes, options.MaxTotalDecompressedBytes, out var guardError))
        {
            return Task.FromResult(DocumentReadResult.Failed(DocumentFormatKind.Docx, guardError ?? "无法读取 .docx 文件。"));
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = WordprocessingDocument.Open(fs, isEditable: false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null)
            {
                return Task.FromResult(new DocumentReadResult(
                    Success: true,
                    Format: DocumentFormatKind.Docx,
                    Text: "(空文档)"));
            }

            var sb = new StringBuilder();
            var truncated = false;

            foreach (var element in body.Elements())
            {
                ct.ThrowIfCancellationRequested();

                if (element is Paragraph paragraph)
                {
                    var line = FormatParagraph(paragraph);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        if (!TryAppendBlock(sb, line, options.MaxOutputChars))
                        {
                            truncated = true;
                            break;
                        }
                    }
                }
                else if (element is Table table)
                {
                    var markdownTable = FormatTable(table);
                    if (!string.IsNullOrWhiteSpace(markdownTable))
                    {
                        if (!TryAppendBlock(sb, markdownTable, options.MaxOutputChars))
                        {
                            truncated = true;
                            break;
                        }
                    }
                }
            }

            var text = sb.ToString().Trim();
            if (text.Length == 0)
            {
                text = "(文档无可提取文本内容)";
            }

            return Task.FromResult(new DocumentReadResult(
                Success: true,
                Format: DocumentFormatKind.Docx,
                Text: text,
                Truncated: truncated));
        }
        catch (Exception ex) when (ex is IOException or OpenXmlPackageException or InvalidDataException)
        {
            return Task.FromResult(DocumentReadResult.Failed(DocumentFormatKind.Docx, $"解析 .docx 文件失败: {ex.Message}"));
        }
    }

    private static string FormatParagraph(Paragraph paragraph)
    {
        var rawText = ExtractParagraphText(paragraph).Trim();
        if (rawText.Length == 0)
        {
            return string.Empty;
        }

        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var headingLevel = ResolveHeadingLevel(styleId);
        if (headingLevel > 0)
        {
            return $"{new string('#', headingLevel)} {rawText}";
        }

        if (paragraph.ParagraphProperties?.NumberingProperties is not null)
        {
            return $"- {rawText}";
        }

        return rawText;
    }

    private static string ExtractParagraphText(OpenXmlElement container)
    {
        var sb = new StringBuilder();
        foreach (var node in container.Descendants())
        {
            switch (node)
            {
                case WpText t:
                    sb.Append(t.Text);
                    break;
                case TabChar:
                    sb.Append('\t');
                    break;
                case Break:
                    sb.Append('\n');
                    break;
            }
        }

        return sb.ToString();
    }

    private static int ResolveHeadingLevel(string? styleId)
    {
        if (string.IsNullOrWhiteSpace(styleId))
        {
            return 0;
        }

        if (styleId.Equals("Title", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (styleId.Equals("Subtitle", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(styleId.AsSpan("Heading".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
            && level is >= 1 and <= 6)
        {
            return level;
        }

        if (int.TryParse(styleId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericLevel)
            && numericLevel is >= 1 and <= 6)
        {
            return numericLevel;
        }

        return 0;
    }

    private static string FormatTable(Table table)
    {
        var rows = new List<List<string>>();
        var maxCols = 0;

        foreach (var row in table.Elements<TableRow>())
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements<TableCell>())
            {
                var cellLines = cell.Elements<Paragraph>()
                    .Select(p => ExtractParagraphText(p).Trim())
                    .Where(static s => s.Length > 0);
                var cellText = string.Join(" / ", cellLines)
                    .Replace("\r\n", " ", StringComparison.Ordinal)
                    .Replace("\n", " ", StringComparison.Ordinal)
                    .Replace("|", "\\|", StringComparison.Ordinal);
                cells.Add(cellText);
            }

            if (cells.Count > 0)
            {
                maxCols = Math.Max(maxCols, cells.Count);
                rows.Add(cells);
            }
        }

        if (rows.Count == 0 || maxCols == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            sb.Append('|');
            for (var c = 0; c < maxCols; c++)
            {
                sb.Append(' ');
                sb.Append(c < row.Count ? row[c] : string.Empty);
                sb.Append(" |");
            }
            sb.AppendLine();

            if (r == 0)
            {
                sb.Append('|');
                for (var c = 0; c < maxCols; c++)
                {
                    sb.Append(" --- |");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static bool TryAppendBlock(StringBuilder sb, string block, int maxOutputChars)
    {
        var addition = sb.Length == 0 ? block.Length : block.Length + 2;
        if (sb.Length + addition > maxOutputChars)
        {
            var remaining = maxOutputChars - sb.Length;
            if (remaining > 32)
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine();
                    remaining -= 2;
                }

                sb.Append(block.AsSpan(0, Math.Min(block.Length, remaining)));
            }

            return false;
        }

        if (sb.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
        }

        sb.Append(block);
        return true;
    }
}
