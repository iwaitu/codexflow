// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexFlow.Core.Browser.Cdp;

internal sealed class CdpCommandRequest
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; init; }

    [JsonPropertyName("sessionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }
}

internal sealed class CdpIncomingMessage
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }

    [JsonPropertyName("error")]
    public CdpErrorPayload? Error { get; init; }
}

internal sealed class CdpErrorPayload
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }
}

public sealed record CdpEventMessage(
    string Method,
    string? SessionId,
    JsonElement Params);

internal sealed class CdpBoundingBox
{
    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("width")]
    public double Width { get; init; }

    [JsonPropertyName("height")]
    public double Height { get; init; }
}

internal sealed class CdpCrossOriginFrameSlot
{
    [JsonPropertyName("src")]
    public string? Src { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("width")]
    public double Width { get; init; }

    [JsonPropertyName("height")]
    public double Height { get; init; }
}

internal sealed class CdpAnnotationItem
{
    [JsonPropertyName("ref")]
    public string Ref { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("isSensitive")]
    public bool IsSensitive { get; init; }

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("width")]
    public double Width { get; init; }

    [JsonPropertyName("height")]
    public double Height { get; init; }
}

internal sealed class CdpVisualOverlaySpec
{
    [JsonPropertyName("secretRects")]
    public List<CdpBoundingBox> SecretRects { get; init; } = [];

    [JsonPropertyName("annotations")]
    public List<CdpAnnotationItem> Annotations { get; init; } = [];
}

internal sealed class CdpSnapshotPayload
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("pageText")]
    public string? PageText { get; init; }

    [JsonPropertyName("elements")]
    public List<CdpSnapshotElement>? Elements { get; init; }

    [JsonPropertyName("secretRects")]
    public List<CdpBoundingBox>? SecretRects { get; init; }

    [JsonPropertyName("crossOriginFrames")]
    public List<CdpCrossOriginFrameSlot>? CrossOriginFrames { get; init; }
}

internal sealed class CdpSnapshotElement
{
    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("tag")]
    public string Tag { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("href")]
    public string? Href { get; init; }

    [JsonPropertyName("inputType")]
    public string? InputType { get; init; }

    [JsonPropertyName("isPassword")]
    public bool IsPassword { get; init; }

    [JsonPropertyName("isSensitiveSubmit")]
    public bool IsSensitiveSubmit { get; init; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; init; }

    [JsonPropertyName("height")]
    public double Height { get; init; }

    [JsonPropertyName("frameLabel")]
    public string? FrameLabel { get; set; }

    [JsonIgnore]
    public string? FrameCdpSessionId { get; set; }

    [JsonIgnore]
    public string? FrameOrigin { get; set; }

    [JsonIgnore]
    public double FrameOffsetX { get; set; }

    [JsonIgnore]
    public double FrameOffsetY { get; set; }
}

internal sealed class CdpLiveRefProbe
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("isPassword")]
    public bool IsPassword { get; init; }

    [JsonPropertyName("isSensitiveSubmit")]
    public bool IsSensitiveSubmit { get; init; }

    [JsonPropertyName("obscured")]
    public bool Obscured { get; init; }

    [JsonPropertyName("pageUrl")]
    public string? PageUrl { get; init; }
}

internal sealed class CdpActiveElementProbe
{
    [JsonPropertyName("tag")]
    public string? Tag { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("isEditable")]
    public bool IsEditable { get; init; }

    [JsonPropertyName("isPassword")]
    public bool IsPassword { get; init; }

    [JsonPropertyName("isSensitiveSubmitContext")]
    public bool IsSensitiveSubmitContext { get; init; }

    [JsonPropertyName("inIframe")]
    public bool InIframe { get; init; }

    [JsonPropertyName("pageUrl")]
    public string? PageUrl { get; init; }
}

[JsonSerializable(typeof(CdpCommandRequest))]
[JsonSerializable(typeof(CdpIncomingMessage))]
[JsonSerializable(typeof(CdpErrorPayload))]
[JsonSerializable(typeof(CdpBoundingBox))]
[JsonSerializable(typeof(List<CdpBoundingBox>))]
[JsonSerializable(typeof(CdpCrossOriginFrameSlot))]
[JsonSerializable(typeof(CdpAnnotationItem))]
[JsonSerializable(typeof(CdpVisualOverlaySpec))]
[JsonSerializable(typeof(CdpSnapshotPayload))]
[JsonSerializable(typeof(CdpSnapshotElement))]
[JsonSerializable(typeof(CdpLiveRefProbe))]
[JsonSerializable(typeof(CdpActiveElementProbe))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CdpJsonContext : JsonSerializerContext
{
}
