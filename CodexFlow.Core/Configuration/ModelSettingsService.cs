// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Configuration;

/// <summary>What a reader of the model settings sees. The key itself is never returned.</summary>
public sealed record ModelSettingsView(
    string? ApiUrl,
    string? ApiMode,
    string? Model,
    int? MaxTokensLength,
    float? Temperature,
    float? TopP,
    bool ApiKeySet,
    string? ApiKeyLast4);

/// <summary>A change to the model settings. A <see langword="null"/> field is left as it is; the key can only be written.</summary>
public sealed record ModelSettingsUpdate(
    string? ApiUrl = null,
    string? ApiMode = null,
    string? Model = null,
    int? MaxTokensLength = null,
    float? Temperature = null,
    float? TopP = null,
    string? ApiKey = null);

public interface IModelSettingsService
{
    /// <summary>Reads the user-level model settings from the global settings file.</summary>
    ModelSettingsView Read();

    /// <summary>
    /// Validates and writes a change to the global settings file. The write is atomic and the file is readable by
    /// the current user only. Throws <see cref="ArgumentException"/> for an invalid value; nothing is written then.
    /// </summary>
    ModelSettingsView Update(ModelSettingsUpdate update);
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
    private readonly object _gate = new();

    public ModelSettingsService(string globalSettingsPath)
    {
        _path = globalSettingsPath;
    }

    public ModelSettingsView Read()
    {
        lock (_gate)
        {
            return ToView(LoadSection(LoadRoot()));
        }
    }

    public ModelSettingsView Update(ModelSettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Validate(update);

        lock (_gate)
        {
            var root = LoadRoot();
            var section = LoadSection(root);

            Set(section, "ApiUrl", update.ApiUrl);
            Set(section, "ApiMode", update.ApiMode);
            Set(section, "Model", update.Model);
            Set(section, "MaxTokensLength", update.MaxTokensLength);
            Set(section, "Temperature", update.Temperature);
            Set(section, "TopP", update.TopP);
            Set(section, "ApiKey", update.ApiKey);

            root[SectionName] = section;
            WriteAtomically(root.ToString(Newtonsoft.Json.Formatting.Indented));
            return ToView(section);
        }
    }

    private static void Validate(ModelSettingsUpdate update)
    {
        if (update.ApiUrl is not null)
        {
            // The URL may carry a "{1}" placeholder for the endpoint name.
            var probe = update.ApiUrl.Replace("{1}", "x", StringComparison.Ordinal);
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

    private JObject LoadRoot()
    {
        if (!File.Exists(_path))
        {
            return new JObject();
        }

        var text = File.ReadAllText(_path);
        return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
    }

    private static JObject LoadSection(JObject root)
        => root[SectionName] as JObject ?? new JObject();

    private static void Set(JObject section, string name, object? value)
    {
        if (value is not null)
        {
            section[name] = JToken.FromObject(value);
        }
    }

    private static ModelSettingsView ToView(JObject section)
    {
        var key = section["ApiKey"]?.ToString();
        return new ModelSettingsView(
            ApiUrl: section["ApiUrl"]?.ToString(),
            ApiMode: section["ApiMode"]?.ToString(),
            Model: section["Model"]?.ToString(),
            MaxTokensLength: section["MaxTokensLength"]?.Type == JTokenType.Null ? null : (int?)section["MaxTokensLength"],
            Temperature: section["Temperature"]?.Type == JTokenType.Null ? null : (float?)section["Temperature"],
            TopP: section["TopP"]?.Type == JTokenType.Null ? null : (float?)section["TopP"],
            ApiKeySet: !string.IsNullOrEmpty(key),
            ApiKeyLast4: string.IsNullOrEmpty(key) ? null : (key.Length <= 4 ? new string('*', key.Length) : key[^4..]));
    }

    private void WriteAtomically(string json)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, json);
            if (!OperatingSystem.IsWindows())
            {
                // Owner read/write only. On Windows the file inherits the user-profile ACL, which is already per user.
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
