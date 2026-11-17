// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace CodexFlow.Core.Documents;

/// <summary>Extracts per-slide text and speaker notes from OOXML PowerPoint (<c>.pptx</c>) files.</summary>
public sealed class PresentationDocumentReader : IDocumentContentReader
{
    public bool CanRead(DocumentFormatKind format) => format == DocumentFormatKind.Pptx;

    public Task<DocumentReadResult> ReadAsync(string filePath, DocumentReadOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        if (!ZipArchiveSafetyGuard.Validate(filePath, options.MaxFileBytes, options.MaxTotalDecompressedBytes, out var guardError))
        {
            return Task.FromResult(DocumentReadResult.Failed(DocumentFormatKind.Pptx, guardError ?? "无法读取 .pptx 文件。"));
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = PresentationDocument.Open(fs, isEditable: false);

            var presentationPart = doc.PresentationPart;
            var slideIds = presentationPart?.Presentation?.SlideIdList?.Elements<SlideId>().ToList() ?? [];
            if (presentationPart is null || slideIds.Count == 0)
            {
                return Task.FromResult(new DocumentReadResult(
                    Success: true,
                    Format: DocumentFormatKind.Pptx,
                    Text: "(空演示文稿)",
                    TotalPages: 0));
            }

            var totalSlides = slideIds.Count;
            var startSlide = options.StartPage.HasValue ? Math.Clamp(options.StartPage.Value, 1, totalSlides) : 1;
            var endSlide = options.EndPage.HasValue ? Math.Clamp(options.EndPage.Value, startSlide, totalSlides) : totalSlides;

            var sb = new StringBuilder();
            var truncated = false;

            for (var index = startSlide; index <= endSlide; index++)
            {
                ct.ThrowIfCancellationRequested();
                var slideId = slideIds[index - 1];
                var relId = slideId.RelationshipId?.Value;
                if (string.IsNullOrEmpty(relId))
                {
                    continue;
                }

                if (presentationPart.GetPartById(relId) is not SlidePart slidePart)
                {
                    continue;
                }

                var slideBlock = FormatSlide(index, totalSlides, slidePart);
                if (sb.Length + slideBlock.Length + 4 > options.MaxOutputChars)
                {
                    truncated = true;
                    sb.AppendLine($"[NOTE] 演示文稿内容超出最大输出长度 ({options.MaxOutputChars} 字符)，已截断。可通过 start_page/end_page 按页读取。");
                    break;
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.AppendLine(slideBlock);
            }

            var text = sb.ToString().Trim();
            if (text.Length == 0)
            {
                text = "(演示文稿无可提取文本)";
            }

            return Task.FromResult(new DocumentReadResult(
                Success: true,
                Format: DocumentFormatKind.Pptx,
                Text: text,
                TotalPages: totalSlides,
                StartPage: startSlide,
                EndPage: endSlide,
                Truncated: truncated));
        }
        catch (Exception ex) when (ex is IOException or OpenXmlPackageException or InvalidDataException)
        {
            return Task.FromResult(DocumentReadResult.Failed(DocumentFormatKind.Pptx, $"解析 .pptx 文件失败: {ex.Message}"));
        }
    }

    private static string FormatSlide(int slideNumber, int totalSlides, SlidePart slidePart)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"=== 第 {slideNumber} 页 / 共 {totalSlides} 页幻灯片 ==="));

        var bodyLines = ExtractDrawingTextLines(slidePart.Slide);
        if (bodyLines.Count == 0)
        {
            sb.AppendLine("(本页无文本内容)");
        }
        else
        {
            foreach (var line in bodyLines)
            {
                sb.AppendLine(line);
            }
        }

        var notesSlide = slidePart.NotesSlidePart?.NotesSlide;
        if (notesSlide is not null)
        {
            var noteLines = ExtractDrawingTextLines(notesSlide)
                .Where(l => !int.TryParse(l, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                .ToList();
            if (noteLines.Count > 0)
            {
                sb.AppendLine("[演讲者备注]");
                foreach (var note in noteLines)
                {
                    sb.AppendLine(note);
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static List<string> ExtractDrawingTextLines(OpenXmlElement? root)
    {
        var lines = new List<string>();
        if (root is null)
        {
            return lines;
        }

        foreach (var paragraph in root.Descendants<A.Paragraph>())
        {
            var sb = new StringBuilder();
            foreach (var node in paragraph.Descendants())
            {
                switch (node)
                {
                    case A.Text t:
                        sb.Append(t.Text);
                        break;
                    case A.Break:
                        sb.Append('\n');
                        break;
                }
            }

            var line = sb.ToString().Trim();
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }
}
