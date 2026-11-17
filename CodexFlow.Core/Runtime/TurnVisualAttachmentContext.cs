// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Runtime;

public sealed record TurnVisualAttachment(
    string Id,
    string FileName,
    int? PageNumber,
    int? ImageIndexOnPage,
    string MimeType,
    ReadOnlyMemory<byte> Bytes);

/// <summary>
/// Per-turn in-memory store for images attached by the user or extracted from PDF pages during tool execution.
/// Keeps QRE messages purely text-based while letting <see cref="ExecutorChatClientBridge"/> hydrate
/// real <see cref="Microsoft.Extensions.AI.DataContent"/> blocks right before calling the LLM.
/// </summary>
public sealed class TurnVisualAttachmentStore
{
    private const int MaxStoredVisualsPerTurn = 16;
    private static readonly Regex MarkerRegex = new(
        @"<codexflow_visual\s+id=""([a-f0-9]+)""(?:\s+page=""(\d+)"")?(?:\s+index=""(\d+)"")?\s*/>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ConcurrentDictionary<string, TurnVisualAttachment> _items = new(StringComparer.Ordinal);
    private int _count;

    public TurnVisualAttachmentStore(bool isMultimodalEnabled)
    {
        IsMultimodalEnabled = isMultimodalEnabled;
    }

    public bool IsMultimodalEnabled { get; }

    public TurnVisualAttachment? Register(
        string fileName,
        string mimeType,
        ReadOnlyMemory<byte> bytes,
        int? pageNumber = null,
        int? imageIndexOnPage = null)
    {
        if (bytes.IsEmpty)
        {
            return null;
        }

        var nextCount = Interlocked.Increment(ref _count);
        if (nextCount > MaxStoredVisualsPerTurn)
        {
            Interlocked.Decrement(ref _count);
            return null;
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        var item = new TurnVisualAttachment(
            id,
            fileName,
            pageNumber,
            imageIndexOnPage,
            string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType,
            bytes);
        _items[id] = item;
        return item;
    }

    public bool TryGet(string id, out TurnVisualAttachment? attachment)
        => _items.TryGetValue(id, out attachment);

    public static string FormatMarker(TurnVisualAttachment item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var pageAttr = item.PageNumber.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $" page=\"{item.PageNumber.Value}\"")
            : string.Empty;
        var indexAttr = item.ImageIndexOnPage.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $" index=\"{item.ImageIndexOnPage.Value}\"")
            : string.Empty;
        return $"<codexflow_visual id=\"{item.Id}\"{pageAttr}{indexAttr} />";
    }

    public IReadOnlyList<TurnVisualAttachment> ExtractReferencedAttachments(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var found = new List<TurnVisualAttachment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in MarkerRegex.Matches(text))
        {
            var id = match.Groups[1].Value;
            if (seen.Add(id) && _items.TryGetValue(id, out var item))
            {
                found.Add(item);
            }
        }

        return found;
    }

    public static string StripMarkers(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : MarkerRegex.Replace(text, string.Empty).Trim();
}

/// <summary>Ambient AsyncLocal scope linking a chat turn's tools and <see cref="ExecutorChatClientBridge"/>.</summary>
public static class AmbientTurnVisualAttachmentContext
{
    private static readonly AsyncLocal<TurnVisualAttachmentStore?> CurrentStore = new();

    public static TurnVisualAttachmentStore? Current => CurrentStore.Value;

    public static IDisposable BeginScope(TurnVisualAttachmentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var previous = CurrentStore.Value;
        CurrentStore.Value = store;
        return new ScopeGuard(previous);
    }

    private sealed class ScopeGuard(TurnVisualAttachmentStore? previous) : IDisposable
    {
        public void Dispose() => CurrentStore.Value = previous;
    }
}
