// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.Configuration;
using YamlDotNet.RepresentationModel;

namespace CodexFlow.Core.Configuration;

/// <summary>
/// Reads a YAML file into the configuration system, so YAML and JSON files share one layered configuration.
/// Mappings become colon-separated keys and sequences become indexed keys, exactly like the JSON provider.
/// </summary>
public sealed class YamlConfigurationSource : FileConfigurationSource
{
    /// <summary>
    /// When set, only keys whose first segment is in this set are kept; everything else in the file is ignored.
    /// Used for files that come with a cloned repository, which must not be able to set model keys or loosen isolation.
    /// </summary>
    public IReadOnlySet<string>? AllowedSections { get; set; }

    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new YamlConfigurationProvider(this);
    }
}

public sealed class YamlConfigurationProvider(YamlConfigurationSource source) : FileConfigurationProvider(source)
{
    public override void Load(Stream stream)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StreamReader(stream);
        var yaml = new YamlStream();
        yaml.Load(reader);

        if (yaml.Documents.Count > 0 && yaml.Documents[0].RootNode is YamlMappingNode root)
        {
            Flatten(root, prefix: null, data);
        }

        var allowed = source.AllowedSections;
        Data = allowed is null
            ? data
            : data.Where(pair => allowed.Contains(FirstSegment(pair.Key)))
                  .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string FirstSegment(string key)
    {
        var index = key.IndexOf(ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
        return index < 0 ? key : key[..index];
    }

    private static void Flatten(YamlNode node, string? prefix, Dictionary<string, string?> data)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var (keyNode, valueNode) in mapping.Children)
                {
                    var key = ((YamlScalarNode)keyNode).Value ?? string.Empty;
                    Flatten(valueNode, prefix is null ? key : ConfigurationPath.Combine(prefix, key), data);
                }
                break;

            case YamlSequenceNode sequence:
                for (var i = 0; i < sequence.Children.Count; i++)
                {
                    var index = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Flatten(sequence.Children[i], prefix is null ? index : ConfigurationPath.Combine(prefix, index), data);
                }
                break;

            case YamlScalarNode scalar when prefix is not null:
                // An explicit null or empty value is kept as null so it can clear a lower layer's value.
                data[prefix] = scalar.Style == YamlDotNet.Core.ScalarStyle.Plain && (scalar.Value is null or "~" or "null" or "")
                    ? null
                    : scalar.Value;
                break;
        }
    }
}
