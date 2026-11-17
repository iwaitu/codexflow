// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Documents;

/// <summary>Reads and normalizes an office document into structured plain/Markdown text (and optional page images).</summary>
public interface IDocumentContentReader
{
    bool CanRead(DocumentFormatKind format);

    Task<DocumentReadResult> ReadAsync(string filePath, DocumentReadOptions options, CancellationToken ct = default);
}
