// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.Configuration;

namespace CodexFlow.Core.Configuration;

public static class LayeredConfigurationExtensions
{
    /// <summary>The only section a project's own files may set. Model keys, endpoints and isolation limits are not in it.</summary>
    public static readonly IReadOnlySet<string> ProjectAllowedSections =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Project" };

    /// <summary>
    /// Adds the CodexFlow configuration layers, lowest precedence first:
    /// global <c>appsettings.json</c>, the project's <c>codexflow.yml</c>, the person's
    /// <c>.codexflow/codexflow.local.yml</c>. Environment variables must be added after this call so they win.
    /// </summary>
    /// <param name="builder">Configuration builder.</param>
    /// <param name="projectDirectory">The project the host works on.</param>
    /// <param name="trustProjectConfig">
    /// Whether the project's own files are read at all. They come with a cloned repository, so they are only read
    /// once the user has said the project is trusted, and even then only <see cref="ProjectAllowedSections"/> is used.
    /// </param>
    /// <param name="globalDirectory">Overrides the global directory (tests); default <see cref="CodexFlowPaths.GlobalDirectory"/>.</param>
    public static IConfigurationBuilder AddCodexFlowLayers(
        this IConfigurationBuilder builder,
        string projectDirectory,
        bool trustProjectConfig,
        string? globalDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var globalDir = globalDirectory ?? CodexFlowPaths.GlobalDirectory();
        Directory.CreateDirectory(globalDir);

        // Reloadable: editing the file (or saving through the settings service) reaches the next model call.
        builder.AddJsonFile(CodexFlowPaths.GlobalSettingsPath(globalDir), optional: true, reloadOnChange: true);

        if (trustProjectConfig)
        {
            builder.AddYamlFile(CodexFlowPaths.ProjectConfigPath(projectDirectory), ProjectAllowedSections);
            builder.AddYamlFile(CodexFlowPaths.LocalOverridePath(projectDirectory), ProjectAllowedSections);
        }

        return builder;
    }

    /// <summary>Adds an optional YAML file; <paramref name="allowedSections"/> restricts which top-level sections count.</summary>
    public static IConfigurationBuilder AddYamlFile(
        this IConfigurationBuilder builder,
        string path,
        IReadOnlySet<string>? allowedSections = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var source = new YamlConfigurationSource
        {
            Path = path,
            Optional = true,
            ReloadOnChange = false,
            AllowedSections = allowedSections
        };
        // Like AddJsonFile: an absolute path is read through a file provider rooted at its own directory.
        source.ResolveFileProvider();
        return builder.Add(source);
    }
}
