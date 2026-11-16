// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Configuration;

/// <summary>
/// Where CodexFlow keeps its files. Two levels, like Claude Code: a global directory that follows the user and a
/// <c>.codexflow</c> directory inside each project.
/// </summary>
public static class CodexFlowPaths
{
    /// <summary>Environment variable that moves the global directory.</summary>
    public const string HomeEnvironmentVariable = "CODEXFLOW_HOME";

    /// <summary><c>%USERPROFILE%\.codexflow</c> / <c>~/.codexflow</c>, unless <see cref="HomeEnvironmentVariable"/> is set.</summary>
    public static string GlobalDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codexflow")
            : overridden;
    }

    /// <summary>User-level settings: model key, endpoint, model name, context length. Holds secrets.</summary>
    public static string GlobalSettingsPath(string globalDirectory) => Path.Combine(globalDirectory, "appsettings.json");

    /// <summary>User-level persistent <c>allow_always</c> command authorization rules.</summary>
    public static string GlobalPermissionsPath(string globalDirectory) => Path.Combine(globalDirectory, "permissions.json");

    /// <summary>Project configuration in the project root; committed, shared by a team, never holds model settings.</summary>
    public static string ProjectConfigPath(string projectDirectory) => Path.Combine(projectDirectory, "codexflow.yml");

    /// <summary>The project's state directory: database, runs, personal overrides.</summary>
    public static string ProjectStateDirectory(string projectDirectory) => Path.Combine(projectDirectory, ".codexflow");

    /// <summary>A person's own overrides of the project configuration; not committed.</summary>
    public static string LocalOverridePath(string projectDirectory) =>
        Path.Combine(ProjectStateDirectory(projectDirectory), "codexflow.local.yml");

    /// <summary>The project's SQLite database.</summary>
    public static string StateDatabasePath(string projectDirectory) =>
        Path.Combine(ProjectStateDirectory(projectDirectory), "state.db");
}
