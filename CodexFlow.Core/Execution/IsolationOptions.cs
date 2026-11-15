// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Execution;

/// <summary>Host configuration of the Docker isolation backend (bind from "Isolation" section).</summary>
public sealed class IsolationOptions
{
    public const string SectionName = "Isolation";

    public string DockerExecutable { get; set; } = "docker";

    /// <summary>Default image for exec_cmd/run_tests (needs the toolchain; e.g. a .NET SDK image).</summary>
    public string CommandImage { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";

    /// <summary>exec_code language -> image.</summary>
    public Dictionary<string, string> LanguageImages { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = "python:3.12-alpine",
        ["node"] = "node:22-alpine",
        ["java"] = "eclipse-temurin:21-jdk-alpine"
    };

    /// <summary>First argv token -> image override (e.g. npm -> node image). Falls back to <see cref="CommandImage"/>.</summary>
    public Dictionary<string, string> ExecutableImages { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node"] = "node:22-alpine",
        ["npm"] = "node:22-alpine",
        ["npx"] = "node:22-alpine",
        ["python"] = "python:3.12-alpine",
        ["python3"] = "python:3.12-alpine",
        ["pip"] = "python:3.12-alpine",
        ["pip3"] = "python:3.12-alpine"
    };

    /// <summary>When set, only these images may run.</summary>
    public List<string>? AllowedImages { get; set; }

    /// <summary>Pull a missing image from the registry (host side, not the guest). Off by default.</summary>
    public bool PullMissingImages { get; set; }

    /// <summary>Private staging area for snapshots. Defaults to the OS temp dir.</summary>
    public string? StagingRoot { get; set; }

    /// <summary>
    /// Optional host-managed NuGet package folder mounted READ-ONLY at /restore-cache and exposed as a restore
    /// fallback folder. This is the only host mount ever created. Network stays off.
    /// </summary>
    public string? RestoreCacheHostPath { get; set; }

    /// <summary>
    /// Optional host directory holding per-session overlays of packages the host fetched itself (security upgrades the base cache does not
    /// contain). The overlay of a session is located by the host-trusted session id (<see cref="RestoreOverlayLocator"/>) and mounted
    /// READ-ONLY at /restore-cache-extra as a second restore fallback folder. Network stays off.
    /// </summary>
    public string? RestoreOverlayRoot { get; set; }

    /// <summary>
    /// Optional host-managed npm cache mounted READ-ONLY at /npm-cache, from which a Node verification restores with
    /// <c>npm ci --offline</c>. Network stays off, so a package missing from it fails the restore.
    /// </summary>
    public string? NpmCacheHostPath { get; set; }

    /// <summary>
    /// Optional host-managed directory of Python wheels mounted READ-ONLY at /pip-wheels, from which a Python verification
    /// installs with <c>pip --no-index --find-links</c>. It must contain pytest and the project's dependencies.
    /// </summary>
    public string? PipWheelhouseHostPath { get; set; }

    public ExecutionResourceLimits Limits { get; set; } = new();

    /// <summary>Containers carrying our label and older than this are removed by the orphan reaper.</summary>
    public TimeSpan OrphanMaxAge { get; set; } = TimeSpan.FromHours(2);

    /// <summary>How often the hosted reaper looks for containers/staging owned by dead instances.</summary>
    public TimeSpan ReapInterval { get; set; } = TimeSpan.FromMinutes(5);
}
