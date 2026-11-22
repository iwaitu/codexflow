// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Nodes;
using CodexFlow.Contracts.ComputerUse;
using CodexFlow.Controllers;

namespace CodexFlow.Gateway.Http.Remote;

/// <summary>
/// Model A output boundary sanitizer stripping all desktop preview screenshots (<c>previewImageBase64</c>)
/// before returning responses or streaming SSE frames over remote control channels.
/// Implemented without reflection so NativeAOT / reflection-disabled builds remain clean.
/// </summary>
public static class RemotePayloadSanitizer
{
    public static ComputerApprovalRequestDto SanitizeComputerApproval(ComputerApprovalRequestDto dto)
        => dto.PreviewImageBase64 is null ? dto : dto with { PreviewImageBase64 = null };

    public static IReadOnlyList<ComputerApprovalRequestDto>? SanitizeComputerApprovals(
        IReadOnlyList<ComputerApprovalRequestDto>? list)
    {
        if (list is null || list.Count == 0)
        {
            return list;
        }

        var sanitized = new List<ComputerApprovalRequestDto>(list.Count);
        foreach (var item in list)
        {
            sanitized.Add(SanitizeComputerApproval(item));
        }

        return sanitized;
    }

    public static PendingApprovalsResponse SanitizePendingApprovals(PendingApprovalsResponse response)
        => response.ComputerApprovals is null || response.ComputerApprovals.Count == 0
            ? response
            : response with { ComputerApprovals = SanitizeComputerApprovals(response.ComputerApprovals) };

    public static string SanitizeSsePayloadJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) ||
            (!json.Contains("previewImageBase64", StringComparison.OrdinalIgnoreCase) &&
             !json.Contains("preview_image_base64", StringComparison.OrdinalIgnoreCase)))
        {
            return json;
        }

        try
        {
            var node = JsonNode.Parse(json);
            if (node is null)
            {
                return json;
            }

            if (StripImageProperties(node))
            {
                return node.ToJsonString();
            }

            return json;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static bool StripImageProperties(JsonNode node)
    {
        var modified = false;
        switch (node)
        {
            case JsonObject obj:
                List<string>? toRemove = null;
                foreach (var kv in obj)
                {
                    if (string.Equals(kv.Key, "previewImageBase64", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(kv.Key, "preview_image_base64", StringComparison.OrdinalIgnoreCase))
                    {
                        toRemove ??= [];
                        toRemove.Add(kv.Key);
                        continue;
                    }

                    if (kv.Value is not null && StripImageProperties(kv.Value))
                    {
                        modified = true;
                    }
                }

                if (toRemove is not null)
                {
                    foreach (var key in toRemove)
                    {
                        obj.Remove(key);
                    }

                    modified = true;
                }

                break;

            case JsonArray arr:
                foreach (var child in arr)
                {
                    if (child is not null && StripImageProperties(child))
                    {
                        modified = true;
                    }
                }

                break;
        }

        return modified;
    }
}
