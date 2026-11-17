// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Documents;

/// <summary>Supported office document formats routed through <see cref="DocumentReaderRouter"/>.</summary>
public enum DocumentFormatKind
{
    Unknown = 0,
    Docx,
    Doc,
    Xlsx,
    Xls,
    Pptx,
    Ppt,
    Pdf
}

/// <summary>Centralized extension and MIME type registry for office documents and standalone images.</summary>
public static class DocumentFileFormats
{
    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".pdf"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"
    };

    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".doc"] = "application/msword",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".xls"] = "application/vnd.ms-excel",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp"
    };

    public static IReadOnlyCollection<string> SupportedOfficeExtensions => OfficeExtensions;
    public static IReadOnlyCollection<string> SupportedImageExtensions => ImageExtensions;

    public static bool IsOfficeDocument(string? pathOrExtension)
    {
        var ext = NormalizeExtension(pathOrExtension);
        return ext.Length > 0 && OfficeExtensions.Contains(ext);
    }

    public static bool IsImageFile(string? pathOrExtension, string? contentType = null)
    {
        var ext = NormalizeExtension(pathOrExtension);
        if (ext.Length > 0 && ImageExtensions.Contains(ext))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(contentType)
            && contentType.Trim().StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }

    public static DocumentFormatKind ResolveFormat(string? pathOrExtension)
        => NormalizeExtension(pathOrExtension).ToUpperInvariant() switch
        {
            ".DOCX" => DocumentFormatKind.Docx,
            ".DOC" => DocumentFormatKind.Doc,
            ".XLSX" => DocumentFormatKind.Xlsx,
            ".XLS" => DocumentFormatKind.Xls,
            ".PPTX" => DocumentFormatKind.Pptx,
            ".PPT" => DocumentFormatKind.Ppt,
            ".PDF" => DocumentFormatKind.Pdf,
            _ => DocumentFormatKind.Unknown
        };

    public static string ResolveImageMimeType(string? pathOrExtension, string? fallbackContentType = null)
    {
        var ext = NormalizeExtension(pathOrExtension);
        if (ext.Length > 0 && ImageExtensions.Contains(ext) && MimeByExtension.TryGetValue(ext, out var mapped))
        {
            return mapped;
        }

        if (!string.IsNullOrWhiteSpace(fallbackContentType)
            && fallbackContentType.Trim().StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return fallbackContentType.Trim();
        }

        return "image/png";
    }

    public static string NormalizeExtension(string? pathOrExtension)
    {
        if (string.IsNullOrWhiteSpace(pathOrExtension))
        {
            return string.Empty;
        }

        var trimmed = pathOrExtension.Trim();
        if (trimmed.StartsWith('.') && !trimmed.Contains('/', StringComparison.Ordinal) && !trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return trimmed;
        }

        return Path.GetExtension(trimmed);
    }
}

/// <summary>Controls pagination, image extraction, and safety budgets when reading office documents.</summary>
public sealed record DocumentReadOptions(
    int? StartPage = null,
    int? EndPage = null,
    bool ExtractImages = false,
    int MaxOutputChars = 120_000,
    int MaxPagesPerRead = 10,
    int MaxImages = 6,
    long MaxImageBytes = 4L * 1024 * 1024,
    long MaxTotalDecompressedBytes = 64L * 1024 * 1024,
    long MaxFileBytes = 50L * 1024 * 1024);

/// <summary>Image extracted from a specific 1-based page of a PDF document.</summary>
public sealed record DocumentExtractedImage(
    int PageNumber,
    int ImageIndexOnPage,
    string MimeType,
    ReadOnlyMemory<byte> Bytes,
    int Width,
    int Height);

/// <summary>Per-page summary of detected images, used for multimodal feeding or non-multimodal notices.</summary>
public sealed record DocumentPageImageNotice(
    int PageNumber,
    int ImageCount,
    bool HasText);

/// <summary>Structured extraction output produced by <see cref="IDocumentContentReader"/> implementations.</summary>
public sealed record DocumentReadResult(
    bool Success,
    DocumentFormatKind Format,
    string Text,
    int? TotalPages = null,
    int? StartPage = null,
    int? EndPage = null,
    bool Truncated = false,
    bool HasImages = false,
    bool IsLikelyScanned = false,
    IReadOnlyList<DocumentExtractedImage>? Images = null,
    IReadOnlyList<DocumentPageImageNotice>? PageImageNotices = null,
    IReadOnlyList<string>? Warnings = null,
    string? ErrorMessage = null)
{
    public IReadOnlyList<DocumentExtractedImage> SafeImages => Images ?? [];
    public IReadOnlyList<DocumentPageImageNotice> SafePageImageNotices => PageImageNotices ?? [];
    public IReadOnlyList<string> SafeWarnings => Warnings ?? [];

    public static DocumentReadResult Failed(DocumentFormatKind format, string errorMessage)
        => new(
            Success: false,
            Format: format,
            Text: string.Empty,
            ErrorMessage: errorMessage);
}
