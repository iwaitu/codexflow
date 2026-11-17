// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using ExcelDataReader;

namespace CodexFlow.Core.Documents;

/// <summary>Extracts worksheet tables as Markdown from <c>.xlsx</c> and <c>.xls</c> workbooks.</summary>
public sealed class SpreadsheetDocumentReader : IDocumentContentReader
{
    static SpreadsheetDocumentReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public bool CanRead(DocumentFormatKind format) => format is DocumentFormatKind.Xlsx or DocumentFormatKind.Xls;

    public Task<DocumentReadResult> ReadAsync(string filePath, DocumentReadOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        var format = DocumentFileFormats.ResolveFormat(filePath);
        if (format == DocumentFormatKind.Xlsx)
        {
            if (!ZipArchiveSafetyGuard.Validate(filePath, options.MaxFileBytes, options.MaxTotalDecompressedBytes, out var guardError))
            {
                return Task.FromResult(DocumentReadResult.Failed(format, guardError ?? "无法读取 .xlsx 文件。"));
            }
        }
        else
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return Task.FromResult(DocumentReadResult.Failed(format, $"文件不存在: {filePath}"));
            }

            if (info.Length == 0)
            {
                return Task.FromResult(DocumentReadResult.Failed(format, "文件为空 (0 B)。"));
            }

            if (info.Length > options.MaxFileBytes)
            {
                return Task.FromResult(DocumentReadResult.Failed(format, $"文件大小 ({info.Length} 字节) 超过解析上限 ({options.MaxFileBytes} 字节)。"));
            }
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(fs);

            var totalSheets = reader.ResultsCount;
            var startSheet = options.StartPage.HasValue ? Math.Clamp(options.StartPage.Value, 1, Math.Max(1, totalSheets)) : 1;
            var endSheet = options.EndPage.HasValue
                ? Math.Clamp(options.EndPage.Value, startSheet, Math.Max(startSheet, totalSheets))
                : totalSheets;

            var sb = new StringBuilder();
            var truncated = false;
            var sheetIndex = 0;

            do
            {
                ct.ThrowIfCancellationRequested();
                sheetIndex++;
                if (sheetIndex < startSheet || sheetIndex > endSheet)
                {
                    continue;
                }

                var sheetName = string.IsNullOrWhiteSpace(reader.Name) ? $"Sheet{sheetIndex}" : reader.Name;
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"## Sheet {sheetIndex}/{Math.Max(1, totalSheets)}: {sheetName}"));

                var rowIndex = 0;
                var sheetHasRows = false;
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var fieldCount = reader.FieldCount;
                    var cells = new List<string>(fieldCount);
                    var lastNonEmpty = -1;

                    for (var c = 0; c < fieldCount; c++)
                    {
                        var val = FormatCellValue(reader.GetValue(c));
                        if (val.Length > 0)
                        {
                            lastNonEmpty = c;
                        }

                        cells.Add(val);
                    }

                    if (lastNonEmpty < 0)
                    {
                        continue;
                    }

                    sheetHasRows = true;
                    rowIndex++;
                    var line = BuildMarkdownRow(cells, lastNonEmpty + 1);
                    if (sb.Length + line.Length + 64 > options.MaxOutputChars)
                    {
                        truncated = true;
                        sb.AppendLine($"[NOTE] 表格内容超出最大输出长度 ({options.MaxOutputChars} 字符)，后续内容已截断。可通过 start_page/end_page 指定工作表序号，或配合 start_line/end_line 分段读取。");
                        break;
                    }

                    sb.AppendLine(line);
                    if (rowIndex == 1)
                    {
                        sb.AppendLine(BuildSeparatorRow(lastNonEmpty + 1));
                    }
                }

                if (!sheetHasRows)
                {
                    sb.AppendLine("(空工作表)");
                }

                if (truncated)
                {
                    break;
                }
            }
            while (reader.NextResult());

            var text = sb.ToString().Trim();
            if (text.Length == 0)
            {
                text = "(工作簿无数据)";
            }

            return Task.FromResult(new DocumentReadResult(
                Success: true,
                Format: format,
                Text: text,
                TotalPages: totalSheets > 0 ? totalSheets : null,
                StartPage: totalSheets > 0 ? startSheet : null,
                EndPage: totalSheets > 0 ? endSheet : null,
                Truncated: truncated));
        }
        catch (Exception ex) when (ex is IOException or ExcelDataReader.Exceptions.ExcelReaderException or InvalidDataException)
        {
            return Task.FromResult(DocumentReadResult.Failed(format, $"解析电子表格失败: {ex.Message}"));
        }
    }

    private static string FormatCellValue(object? raw)
    {
        if (raw is null or DBNull)
        {
            return string.Empty;
        }

        var text = raw switch
        {
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero
                ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
            _ => raw.ToString() ?? string.Empty
        };

        return text
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Trim();
    }

    private static string BuildMarkdownRow(List<string> cells, int colCount)
    {
        var sb = new StringBuilder();
        sb.Append('|');
        for (var i = 0; i < colCount; i++)
        {
            sb.Append(' ');
            sb.Append(i < cells.Count ? cells[i] : string.Empty);
            sb.Append(" |");
        }

        return sb.ToString();
    }

    private static string BuildSeparatorRow(int colCount)
    {
        var sb = new StringBuilder();
        sb.Append('|');
        for (var i = 0; i < colCount; i++)
        {
            sb.Append(" --- |");
        }

        return sb.ToString();
    }
}
