// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Documents;

/// <summary>Routes supported office file extensions to the corresponding <see cref="IDocumentContentReader"/>.</summary>
public sealed class DocumentReaderRouter
{
    private readonly IReadOnlyList<IDocumentContentReader> _readers;

    public DocumentReaderRouter(IEnumerable<IDocumentContentReader> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        _readers = readers.ToArray();
    }

    public static DocumentReaderRouter CreateDefault(ILibreOfficeDocumentConverter? converter = null)
    {
        var word = new WordDocumentReader();
        var spreadsheet = new SpreadsheetDocumentReader();
        var presentation = new PresentationDocumentReader();
        var pdf = new PdfDocumentReader();
        var legacy = new LegacyOfficeDocumentReader(
            converter ?? new LibreOfficeDocumentConverter(),
            word,
            presentation);

        return new DocumentReaderRouter([word, spreadsheet, presentation, pdf, legacy]);
    }

    public bool CanRead(string? pathOrExtension)
    {
        var format = DocumentFileFormats.ResolveFormat(pathOrExtension);
        return format != DocumentFormatKind.Unknown && _readers.Any(r => r.CanRead(format));
    }

    public Task<DocumentReadResult> ReadAsync(
        string filePath,
        DocumentReadOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var effectiveOptions = options ?? new DocumentReadOptions();
        var format = DocumentFileFormats.ResolveFormat(filePath);
        if (format == DocumentFormatKind.Unknown)
        {
            return Task.FromResult(DocumentReadResult.Failed(
                DocumentFormatKind.Unknown,
                $"不支持的文档格式: {Path.GetExtension(filePath)}"));
        }

        var reader = _readers.FirstOrDefault(r => r.CanRead(format));
        if (reader is null)
        {
            return Task.FromResult(DocumentReadResult.Failed(
                format,
                $"未找到对应格式的文档读取器: {format}"));
        }

        return reader.ReadAsync(filePath, effectiveOptions, ct);
    }
}
