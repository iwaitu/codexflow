// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.IO.Compression;
using System.Text;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Documents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Services;
using CodexFlow.Orchestration.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace CodexFlow.Core.Tests.Documents;

public sealed class DocumentReadersAndToolsTests : IDisposable
{
    private readonly string _root;

    public DocumentReadersAndToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "codexflow-doc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup race on Windows.
        }
    }

    [Fact]
    public async Task WordDocumentReader_And_ReadFileTool_ExtractHeadingsAndMarkdownTables()
    {
        var path = Path.Combine(_root, "spec.docx");
        CreateSampleDocx(path);

        var tool = new ReadFileTool(NullLogger<ReadFileTool>.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _root,
            ["path"] = "spec.docx"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("# Project Overview", result.Output, StringComparison.Ordinal);
        Assert.Contains("Deliverable summary paragraph.", result.Output, StringComparison.Ordinal);
        Assert.Contains("| Name | Status |", result.Output, StringComparison.Ordinal);
        Assert.Contains("| Parser | Done |", result.Output, StringComparison.Ordinal);

        // Hashline mode must refuse office binary files cleanly.
        var hashlineAttempt = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _root,
            ["path"] = "spec.docx",
            ["mode"] = "hashline"
        });
        Assert.Equal(ToolResultStatus.Failed, hashlineAttempt.Status);
        Assert.Contains("hashline", hashlineAttempt.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpreadsheetDocumentReader_SupportsSheetPaginationAndMarkdownFormatting()
    {
        var path = Path.Combine(_root, "budget.xlsx");
        CreateSampleXlsx(path);

        var router = DocumentReaderRouter.CreateDefault();
        var sheet2Only = await router.ReadAsync(path, new DocumentReadOptions(StartPage: 2, EndPage: 2));

        Assert.True(sheet2Only.Success);
        Assert.Equal(2, sheet2Only.TotalPages);
        Assert.DoesNotContain("## Sheet 1/2", sheet2Only.Text, StringComparison.Ordinal);
        Assert.Contains("## Sheet 2/2: Details", sheet2Only.Text, StringComparison.Ordinal);
        Assert.Contains("| Item | Cost |", sheet2Only.Text, StringComparison.Ordinal);
        Assert.Contains("| Server | 200 |", sheet2Only.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PresentationDocumentReader_ExtractsSlidesAndNotesWithPagination()
    {
        var path = Path.Combine(_root, "deck.pptx");
        CreateSamplePptx(path);

        var tool = new ReadFileTool(NullLogger<ReadFileTool>.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _root,
            ["path"] = "deck.pptx",
            ["start_page"] = 2,
            ["end_page"] = 2
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.DoesNotContain("第 1 页", result.Output, StringComparison.Ordinal);
        Assert.Contains("第 2 页 / 共 2 页幻灯片", result.Output, StringComparison.Ordinal);
        Assert.Contains("Slide Two Body", result.Output, StringComparison.Ordinal);
        Assert.Contains("[演讲者备注]", result.Output, StringComparison.Ordinal);
        Assert.Contains("Remember Q4 metrics", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdfDocumentReader_ReadsByPage_ReportsNonMultimodalNotice_AndTagsPageImagesForMultimodal()
    {
        var pdfPath = Path.Combine(_root, "report.pdf");
        CreateMultiPagePdfWithImageOnPage1(pdfPath);

        // 1. Non-multimodal: reads page 1, warns about images on page 1, extracts no binary images.
        var nonMultimodalRouter = DocumentReaderRouter.CreateDefault();
        var nonMultimodalPage1 = await nonMultimodalRouter.ReadAsync(
            pdfPath,
            new DocumentReadOptions(StartPage: 1, EndPage: 1, ExtractImages: false));

        Assert.True(nonMultimodalPage1.Success);
        Assert.Equal(2, nonMultimodalPage1.TotalPages);
        Assert.Contains("第 1 页 / 共 2 页", nonMultimodalPage1.Text, StringComparison.Ordinal);
        Assert.Contains("Hello PDF Page 1", nonMultimodalPage1.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello PDF Page 2", nonMultimodalPage1.Text, StringComparison.Ordinal);
        Assert.Contains("当前模型未启用多模态，无法读取图片内容", nonMultimodalPage1.Text, StringComparison.Ordinal);
        Assert.Empty(nonMultimodalPage1.SafeImages);

        // 2. Multimodal via ReadFileTool + ExecutorChatClientBridge hydration preserves page number!
        var visualStore = new TurnVisualAttachmentStore(isMultimodalEnabled: true);
        using var scope = AmbientTurnVisualAttachmentContext.BeginScope(visualStore);
        var tool = new ReadFileTool(NullLogger<ReadFileTool>.Instance);
        var toolRes = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _root,
            ["path"] = "report.pdf",
            ["start_page"] = 1,
            ["end_page"] = 1
        });

        Assert.Equal(ToolResultStatus.Success, toolRes.Status);
        Assert.Contains("第 1 页 图片 #1", toolRes.Output, StringComparison.Ordinal);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "请看第1页"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "ivilson_read")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", toolRes.Output)])
        };

        var hydrated = ExecutorChatClientBridge.HydrateVisualMessages(messages, visualStore);
        Assert.Equal(4, hydrated.Count);
        var trailingUser = hydrated[^1];
        Assert.Equal(ChatRole.User, trailingUser.Role);
        Assert.Contains(trailingUser.Contents.OfType<TextContent>(), t => t.Text.Contains("第 1 页", StringComparison.Ordinal));
        Assert.Contains(trailingUser.Contents.OfType<DataContent>(), d => d.MediaType == "image/png");
    }

    [Fact]
    public async Task PdfDocumentReader_FlagsScannedImageOnlyPdf_WhenNonMultimodal()
    {
        var scannedPdf = Path.Combine(_root, "scanned.pdf");
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(595, 842);
        var png = DefaultMultimodalCapabilityProbe.BuiltInTestPngBytes.ToArray();
        page.AddPng(png, new PdfRectangle(50, 500, 150, 600));
        await File.WriteAllBytesAsync(scannedPdf, builder.Build());

        var router = DocumentReaderRouter.CreateDefault();
        var result = await router.ReadAsync(scannedPdf, new DocumentReadOptions(ExtractImages: false));

        Assert.True(result.Success);
        Assert.True(result.IsLikelyScanned);
        Assert.True(result.HasImages);
        Assert.Contains("疑似扫描件", result.Text, StringComparison.Ordinal);
        Assert.Contains("无法读取", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFileTool_RefusesStandaloneImage_WhenModelIsNotMultimodal()
    {
        var imgPath = Path.Combine(_root, "diagram.png");
        await File.WriteAllBytesAsync(imgPath, DefaultMultimodalCapabilityProbe.BuiltInTestPngBytes.ToArray());

        var visualStore = new TurnVisualAttachmentStore(isMultimodalEnabled: false);
        using var scope = AmbientTurnVisualAttachmentContext.BeginScope(visualStore);
        var tool = new ReadFileTool(NullLogger<ReadFileTool>.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _root,
            ["path"] = "diagram.png"
        });

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("多模态", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyOfficeDocumentReader_UsesLibreOfficeConverter_AndFallsBackGracefullyWithoutIt()
    {
        var docPath = Path.Combine(_root, "legacy.doc");
        await File.WriteAllBytesAsync(docPath, Encoding.Unicode.GetBytes("Legacy Contract Body Text\0\0"));

        // 1. With mock LibreOffice converter producing a .docx temp copy
        var tempOutDir = Path.Combine(_root, "lo-out");
        Directory.CreateDirectory(tempOutDir);
        var convertedDocx = Path.Combine(tempOutDir, "input.docx");
        CreateSampleDocx(convertedDocx);

        var fakeConverter = new FakeLibreOfficeConverter(new LibreOfficeConversionResult(
            Success: true,
            ConvertedFilePath: convertedDocx,
            TempDirectoryToCleanup: tempOutDir,
            BinaryAvailable: true,
            ErrorMessage: null));

        var routerWithConverter = DocumentReaderRouter.CreateDefault(fakeConverter);
        var convertedResult = await routerWithConverter.ReadAsync(docPath);
        Assert.True(convertedResult.Success);
        Assert.Equal(DocumentFormatKind.Doc, convertedResult.Format);
        Assert.Contains("# Project Overview", convertedResult.Text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(tempOutDir));

        // 2. Without LibreOffice -> falls back to readable UTF-16LE runs + warning
        var missingConverter = new FakeLibreOfficeConverter(new LibreOfficeConversionResult(
            Success: false,
            ConvertedFilePath: null,
            TempDirectoryToCleanup: null,
            BinaryAvailable: false,
            ErrorMessage: "未检测到 LibreOffice"));
        var routerFallback = DocumentReaderRouter.CreateDefault(missingConverter);
        var fallbackResult = await routerFallback.ReadAsync(docPath);
        Assert.True(fallbackResult.Success);
        Assert.Contains("Legacy Contract Body Text", fallbackResult.Text, StringComparison.Ordinal);
        Assert.NotEmpty(fallbackResult.SafeWarnings);
    }

    [Fact]
    public async Task ZipArchiveSafetyGuard_RejectsZipBombAndCorruptDocx_AndWorkspacePreviewReadsOfficeDocs()
    {
        var corruptDocx = Path.Combine(_root, "broken.docx");
        await File.WriteAllTextAsync(corruptDocx, "not-a-zip");

        var router = DocumentReaderRouter.CreateDefault();
        var brokenRes = await router.ReadAsync(corruptDocx);
        Assert.False(brokenRes.Success);

        // Oversized uncompressed check
        var bigZipDocx = Path.Combine(_root, "huge.docx");
        CreateSampleDocx(bigZipDocx);
        var tinyCapResult = await router.ReadAsync(bigZipDocx, new DocumentReadOptions(MaxTotalDecompressedBytes: 64));
        Assert.False(tinyCapResult.Success);
        Assert.Contains("安全上限", tinyCapResult.ErrorMessage, StringComparison.Ordinal);

        // WorkspaceFileExplorerService previews valid .docx as text rather than binary
        var validDocx = Path.Combine(_root, "preview.docx");
        CreateSampleDocx(validDocx);
        var preview = await WorkspaceFileExplorerService.ReadFileContentAsync(_root, "preview.docx");
        Assert.True(preview.Success);
        Assert.False(preview.IsBinary);
        Assert.Equal("document-markdown", preview.Encoding);
        Assert.Contains("# Project Overview", preview.Content, StringComparison.Ordinal);
    }

    private sealed class FakeLibreOfficeConverter(LibreOfficeConversionResult result) : ILibreOfficeDocumentConverter
    {
        public string? ResolveExecutablePath() => result.BinaryAvailable ? "soffice" : null;

        public Task<LibreOfficeConversionResult> ConvertAsync(string sourceFilePath, string targetExtension, CancellationToken ct = default)
            => Task.FromResult(result);
    }

    private static void CreateSampleDocx(string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());
        var body = mainPart.Document.Body!;

        body.AppendChild(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
            new W.Run(new W.Text("Project Overview"))));
        body.AppendChild(new Paragraph(new W.Run(new W.Text("Deliverable summary paragraph."))));

        var table = new W.Table(
            new W.TableRow(
                new W.TableCell(new Paragraph(new W.Run(new W.Text("Name")))),
                new W.TableCell(new Paragraph(new W.Run(new W.Text("Status"))))),
            new W.TableRow(
                new W.TableCell(new Paragraph(new W.Run(new W.Text("Parser")))),
                new W.TableCell(new Paragraph(new W.Run(new W.Text("Done"))))));
        body.AppendChild(table);
    }

    private static void CreateSampleXlsx(string path)
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wbPart = doc.AddWorkbookPart();
        wbPart.Workbook = new Workbook();
        var sheets = wbPart.Workbook.AppendChild(new Sheets());

        AddWorksheet(wbPart, sheets, 1, "Summary", [["Quarter", "Total"], ["Q1", "100"]]);
        AddWorksheet(wbPart, sheets, 2, "Details", [["Item", "Cost"], ["Server", "200"]]);
    }

    private static void AddWorksheet(
        WorkbookPart wbPart,
        Sheets sheets,
        uint sheetId,
        string name,
        string[][] rows)
    {
        var wsPart = wbPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        foreach (var r in rows)
        {
            var row = new Row();
            foreach (var cellText in r)
            {
                row.Append(new Cell
                {
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text(cellText))
                });
            }
            sheetData.Append(row);
        }

        wsPart.Worksheet = new Worksheet(sheetData);
        sheets.Append(new Sheet
        {
            Id = wbPart.GetIdOfPart(wsPart),
            SheetId = sheetId,
            Name = name
        });
    }

    private static void CreateSamplePptx(string path)
    {
        using var doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presPart = doc.AddPresentationPart();
        presPart.Presentation = new Presentation(new SlideIdList());
        var slideIdList = presPart.Presentation.SlideIdList!;

        AddSlide(presPart, slideIdList, 256U, "Slide One Title", null);
        AddSlide(presPart, slideIdList, 257U, "Slide Two Body", "Remember Q4 metrics");
    }

    private static void AddSlide(
        PresentationPart presPart,
        SlideIdList slideIdList,
        uint id,
        string bodyText,
        string? noteText)
    {
        var slidePart = presPart.AddNewPart<SlidePart>();
        slidePart.Slide = new Slide(
            new CommonSlideData(
                new ShapeTree(
                    new P.NonVisualGroupShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                        new P.NonVisualGroupShapeDrawingProperties(),
                        new ApplicationNonVisualDrawingProperties()),
                    new GroupShapeProperties(),
                    new P.Shape(
                        new P.NonVisualShapeProperties(
                            new P.NonVisualDrawingProperties { Id = 2U, Name = "TextBox" },
                            new P.NonVisualShapeDrawingProperties(),
                            new ApplicationNonVisualDrawingProperties()),
                        new P.ShapeProperties(),
                        new P.TextBody(
                            new A.BodyProperties(),
                            new A.ListStyle(),
                            new A.Paragraph(new A.Run(new A.Text(bodyText))))))));

        if (!string.IsNullOrWhiteSpace(noteText))
        {
            var notesPart = slidePart.AddNewPart<NotesSlidePart>();
            notesPart.NotesSlide = new NotesSlide(
                new CommonSlideData(
                    new ShapeTree(
                        new P.NonVisualGroupShapeProperties(
                            new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                            new P.NonVisualGroupShapeDrawingProperties(),
                            new ApplicationNonVisualDrawingProperties()),
                        new GroupShapeProperties(),
                        new P.Shape(
                            new P.NonVisualShapeProperties(
                                new P.NonVisualDrawingProperties { Id = 2U, Name = "Notes" },
                                new P.NonVisualShapeDrawingProperties(),
                                new ApplicationNonVisualDrawingProperties()),
                            new P.ShapeProperties(),
                            new P.TextBody(
                                new A.BodyProperties(),
                                new A.ListStyle(),
                                new A.Paragraph(new A.Run(new A.Text(noteText))))))));
        }

        slideIdList.Append(new SlideId { Id = id, RelationshipId = presPart.GetIdOfPart(slidePart) });
    }

    private static void CreateMultiPagePdfWithImageOnPage1(string path)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        var page1 = builder.AddPage(595, 842);
        page1.AddText("Hello PDF Page 1", 12, new PdfPoint(50, 750), font);
        page1.AddPng(DefaultMultimodalCapabilityProbe.BuiltInTestPngBytes.ToArray(), new PdfRectangle(50, 550, 150, 650));

        var page2 = builder.AddPage(595, 842);
        page2.AddText("Hello PDF Page 2", 12, new PdfPoint(50, 750), font);

        File.WriteAllBytes(path, builder.Build());
    }
}
