// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Configuration;

/// <summary>What a reader of the model settings sees. The key itself is never returned.</summary>
public sealed record ModelSettingsView(
    string? EndpointTemplate,
    string? ApiMode,
    string? Model,
    int? MaxTokensLength,
    float? Temperature,
    float? TopP,
    bool ApiKeySet,
    string? ApiKeyLast4,
    bool SupportsMultimodal = false,
    string? MultimodalProbeStatus = null,
    string? MultimodalProbeMessage = null);

/// <summary>A change to the model settings. A <see langword="null"/> field is left as it is; the key can only be written.</summary>
public sealed record ModelSettingsUpdate(
    string? EndpointTemplate = null,
    string? ApiMode = null,
    string? Model = null,
    int? MaxTokensLength = null,
    float? Temperature = null,
    float? TopP = null,
    string? ApiKey = null,
    bool? SupportsMultimodal = null,
    string? MultimodalProbeStatus = null,
    string? MultimodalProbeMessage = null);

/// <summary>Internal credentials snapshot used exclusively by server-side capability probes.</summary>
public sealed record ModelProbeCredentials(
    string? EndpointTemplate,
    string? ApiMode,
    string? Model,
    string? ApiKey);

public interface IModelSettingsService
{
    /// <summary>Reads the user-level model settings from the global settings file.</summary>
    ModelSettingsView Read();

    /// <summary>
    /// Validates and writes a change to the global settings file. The write is atomic and the file is readable by
    /// the current user only. Throws <see cref="ArgumentException"/> for an invalid value; nothing is written then.
    /// </summary>
    ModelSettingsView Update(ModelSettingsUpdate update);

    /// <summary>Reads full model endpoint credentials for server-side capability probing without exposing secrets over HTTP.</summary>
    ModelProbeCredentials ReadProbeCredentials() => new(null, null, null, null);
}

/// <summary>
/// Reads and writes the <c>VllmAgent</c> section of <c>~/.codexflow/appsettings.json</c>. The configuration system watches
/// that file, so a change reaches the next model call without a restart.
/// </summary>
public sealed class ModelSettingsService : IModelSettingsService
{
    public const string SectionName = "VllmAgent";

    private static readonly string[] KnownApiModes = ["ChatCompletions", "Responses"];
    private readonly string _path;

    public ModelSettingsService(string globalSettingsPath)
    {
        _path = globalSettingsPath;
    }

    public ModelSettingsView Read()
        => GlobalSettingsFileStore.ReadLocked(_path, root => ToView(LoadSection(root)));

    public ModelProbeCredentials ReadProbeCredentials()
        => GlobalSettingsFileStore.ReadLocked(_path, root =>
        {
            var section = LoadSection(root);
            return new ModelProbeCredentials(
                EndpointTemplate: section["ApiUrl"]?.ToString(),
                ApiMode: section["ApiMode"]?.ToString(),
                Model: section["Model"]?.ToString(),
                ApiKey: section["ApiKey"]?.ToString());
        });

    public static bool IsMultimodalConfigured(IConfiguration? configuration)
    {
        if (configuration is null)
        {
            return false;
        }

        return configuration.GetValue<bool>($"{SectionName}:SupportsMultimodal");
    }

    public ModelSettingsView Update(ModelSettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Validate(update);

        return GlobalSettingsFileStore.UpdateAtomically(_path, root =>
        {
            var section = LoadSection(root);

            Set(section, "ApiUrl", update.EndpointTemplate);
            Set(section, "ApiMode", update.ApiMode);
            Set(section, "Model", update.Model);
            Set(section, "MaxTokensLength", update.MaxTokensLength);
            Set(section, "Temperature", update.Temperature);
            Set(section, "TopP", update.TopP);
            Set(section, "ApiKey", update.ApiKey);
            Set(section, "SupportsMultimodal", update.SupportsMultimodal);
            Set(section, "MultimodalProbeStatus", update.MultimodalProbeStatus);
            Set(section, "MultimodalProbeMessage", update.MultimodalProbeMessage);

            root[SectionName] = section;
            return (true, ToView(section));
        });
    }

    private static void Validate(ModelSettingsUpdate update)
    {
        if (update.EndpointTemplate is not null)
        {
            // The URL may carry a "{1}" placeholder for the endpoint name.
            var probe = update.EndpointTemplate.Replace("{1}", "x", StringComparison.Ordinal);
            if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException("ApiUrl must be an absolute http or https address.", nameof(update));
            }
        }

        if (update.ApiMode is not null && !KnownApiModes.Contains(update.ApiMode, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"ApiMode must be one of: {string.Join(", ", KnownApiModes)}.", nameof(update));
        }

        if (update.Model is { } model && string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model must not be empty.", nameof(update));
        }

        if (update.ApiKey is { } key && string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("ApiKey must not be empty.", nameof(update));
        }

        if (update.MaxTokensLength is { } max && max <= 0)
        {
            throw new ArgumentException("MaxTokensLength must be greater than 0.", nameof(update));
        }

        if (update.Temperature is { } temperature && (temperature < 0 || temperature > 2))
        {
            throw new ArgumentException("Temperature must be between 0 and 2.", nameof(update));
        }

        if (update.TopP is { } topP && (topP < 0 || topP > 1))
        {
            throw new ArgumentException("TopP must be between 0 and 1.", nameof(update));
        }
    }

    private static JObject LoadSection(JObject root)
        => root[SectionName] as JObject ?? new JObject();

    private static void Set(JObject section, string name, object? value)
    {
        if (value is not null)
        {
            section[name] = new JValue(value);
        }
    }

    private static ModelSettingsView ToView(JObject section)
    {
        var key = section["ApiKey"]?.ToString();
        return new ModelSettingsView(
            EndpointTemplate: section["ApiUrl"]?.ToString(),
            ApiMode: section["ApiMode"]?.ToString(),
            Model: section["Model"]?.ToString(),
            MaxTokensLength: section["MaxTokensLength"]?.Type == JTokenType.Null ? null : (int?)section["MaxTokensLength"],
            Temperature: section["Temperature"]?.Type == JTokenType.Null ? null : (float?)section["Temperature"],
            TopP: section["TopP"]?.Type == JTokenType.Null ? null : (float?)section["TopP"],
            ApiKeySet: !string.IsNullOrEmpty(key),
            ApiKeyLast4: string.IsNullOrEmpty(key) ? null : (key.Length <= 4 ? new string('*', key.Length) : key[^4..]),
            SupportsMultimodal: section["SupportsMultimodal"]?.Type == JTokenType.Boolean && (bool)section["SupportsMultimodal"]!,
            MultimodalProbeStatus: section["MultimodalProbeStatus"]?.ToString(),
            MultimodalProbeMessage: section["MultimodalProbeMessage"]?.ToString());
    }
}
