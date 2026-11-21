// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections;
using System.Globalization;
using System.Text.Json;
using CodexFlow.Contracts;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Orchestration.Persistence;

internal static class OrchestrationPayloadJson
{
    public static string Write(params (string Name, object? Value)[] properties)
        => Object(properties).ToString(Newtonsoft.Json.Formatting.None);

    public static JObject Object(params (string Name, object? Value)[] properties)
    {
        var payload = new JObject();
        foreach (var (name, value) in properties)
        {
            payload.Add(name, Token(value));
        }

        return payload;
    }

    public static JObject Object(IEnumerable<KeyValuePair<string, object?>> properties)
    {
        var payload = new JObject();
        foreach (var (name, value) in properties)
        {
            payload.Add(name, Token(value));
        }

        return payload;
    }

    public static JArray Array(IEnumerable sequence) => new(sequence.Cast<object?>().Select(Token));

    public static JToken Token(object? value) => value switch
    {
        JToken json => json.DeepClone(),
        JsonElement element => ParseElement(element),
        Enum item => new JValue(Convert.ChangeType(item, Enum.GetUnderlyingType(item.GetType()), CultureInfo.InvariantCulture)),
        byte[] bytes => new JValue(bytes),
        IEnumerable<KeyValuePair<string, object?>> properties => Object(properties),
        IDictionary dictionary => Dictionary(dictionary),
        PlannerSummaryUpdate summary => ParseElement(JsonSerializer.SerializeToElement(
            summary, OrchestrationSnapshotJsonContext.Default.PlannerSummaryUpdate)),
        ForgeArtifactManifest manifest => Object(
            (nameof(manifest.Version), manifest.Version),
            (nameof(manifest.ArtifactId), manifest.ArtifactId),
            (nameof(manifest.UserId), manifest.UserId),
            (nameof(manifest.SessionId), manifest.SessionId),
            (nameof(manifest.JobId), manifest.JobId),
            (nameof(manifest.TaskId), manifest.TaskId),
            (nameof(manifest.Attempt), manifest.Attempt),
            (nameof(manifest.RepositoryIdentity), manifest.RepositoryIdentity),
            (nameof(manifest.WorktreePath), manifest.WorktreePath),
            (nameof(manifest.BaseCommit), manifest.BaseCommit),
            (nameof(manifest.HeadCommit), manifest.HeadCommit),
            (nameof(manifest.ScopeHash), manifest.ScopeHash),
            (nameof(manifest.PlanArtifactId), manifest.PlanArtifactId),
            (nameof(manifest.PlanVersion), manifest.PlanVersion),
            (nameof(manifest.PlanHash), manifest.PlanHash),
            (nameof(manifest.RequiredTaskIds), manifest.RequiredTaskIds),
            (nameof(manifest.ParentArtifactIds), manifest.ParentArtifactIds),
            (nameof(manifest.ChangedPaths), manifest.ChangedPaths),
            (nameof(manifest.CreatedAtUtc), manifest.CreatedAtUtc)),
        ScopeDenial denial => Object(
            (nameof(denial.Tool), denial.Tool), (nameof(denial.Path), denial.Path),
            (nameof(denial.Command), denial.Command), (nameof(denial.Reason), denial.Reason)),
        IEnumerable sequence when value is not string => Array(sequence),
        null or string or char or bool or sbyte or byte or short or ushort or int or uint or long or ulong or
            float or double or decimal or DateTime or DateTimeOffset or TimeSpan or Guid or Uri or System.Numerics.BigInteger => new JValue(value),
        _ => ConvertManagedValue(value)
    };

    private static JObject Dictionary(IDictionary entries)
    {
        var payload = new JObject();
        foreach (DictionaryEntry entry in entries)
        {
            payload.Add(Convert.ToString(entry.Key, CultureInfo.InvariantCulture)!, Token(entry.Value));
        }

        return payload;
    }

    private static JToken ParseElement(JsonElement element)
    {
        using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(element.GetRawText()))
        {
            DateParseHandling = Newtonsoft.Json.DateParseHandling.None
        };
        return JToken.ReadFrom(reader);
    }

    private static JToken ConvertManagedValue(object value)
        => ManagedPayloadReflection.TryToToken(value) ??
           throw new Newtonsoft.Json.JsonSerializationException($"No Native AOT payload representation for {value.GetType().FullName}.");
}
