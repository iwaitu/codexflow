// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.Execution;

/// <summary>
/// Linux-container isolation backend. Per session: private tar snapshot (never a mount of the real worktree) copied
/// into a fresh container with no network, no capabilities, no docker socket, no host env/HOME/.git, bounded
/// cpu/memory/pids/time. Source changes leave the guest only through <see cref="IIsolatedExecutionSession.ExportScopedChangesAsync"/>
/// (tar stream analysed in memory + host-side re-hash + CAS publish). Any failure to isolate refuses execution.
/// </summary>
public sealed class DockerIsolatedExecutionService : IIsolatedExecutionService, IDisposable
{
    internal const string Label = "codexflow.isolation";
    internal const string GuestGitDir = "/tmp/cfgit";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);
    private IsolationOwnerLease? _lease;
    private readonly object _leaseGate = new();

    /// <summary>Unique id of this service instance. Containers and staging dirs are owned by it; only a DEAD owner's resources are ever reaped.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    internal string HostLabel { get; } = System.Text.RegularExpressions.Regex.Replace(Environment.MachineName, "[^A-Za-z0-9._-]", "_");

    internal string StagingRoot { get; }

    /// <summary>
    /// Identifies the staging store (and therefore the lease directory) this instance belongs to. Containers carry it, and
    /// the reaper only ever judges containers of ITS OWN store: a lease it cannot see (because it lives in another staging
    /// root, e.g. another host process or a test run) says nothing about whether that owner is alive.
    /// </summary>
    internal string StoreId { get; private set; } = string.Empty;
    private static readonly Regex ImageName = new(@"^[A-Za-z0-9][A-Za-z0-9._/:@-]{0,255}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IDockerCli _docker;
    private readonly IsolationOptions _options;
    private readonly IWorkspaceExportObserver? _observer;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private (DateTimeOffset At, bool Ok, string Message)? _probe;

    public DockerIsolatedExecutionService(
        IDockerCli docker,
        IOptions<IsolationOptions> options,
        ILogger<DockerIsolatedExecutionService> logger,
        IWorkspaceExportObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _options = options.Value;
        _logger = logger;
        _observer = observer;
        StagingRoot = _options.StagingRoot ?? Path.Combine(Path.GetTempPath(), "codexflow-isolation");
        StoreId = ComputeStoreId(StagingRoot);
    }

    internal static string ComputeStoreId(string stagingRoot)
    {
        var normalized = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
    }

    private bool TryEnsureLease(out string problem)
    {
        problem = string.Empty;
        lock (_leaseGate)
        {
            if (_lease is not null)
            {
                return true;
            }

            try
            {
                _lease = IsolationOwnerLease.Acquire(StagingRoot, InstanceId);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = "could not acquire the isolation owner lease: " + ex.Message;
                return false;
            }
        }
    }

    public bool SupportsNetworkMode(ExecutionNetworkMode mode) => mode == ExecutionNetworkMode.None;

    public async Task<IsolatedSessionOpenResult> OpenSessionAsync(TrustedExecutionContext context, IsolatedSessionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        if (!SupportsNetworkMode(context.Network.Mode))
        {
            return new IsolatedSessionOpenResult(
                ExecutionDecision.Unavailable(ExecutionReasonCodes.NetworkNotPermitted, "Docker isolation only supports network mode None."),
                null);
        }

        var valid = context.Validate();
        if (!valid.IsAllowed)
        {
            return Refuse(valid);
        }

        if (!ImageName.IsMatch(options.Image ?? string.Empty))
        {
            return Refuse(ExecutionDecision.Deny(ExecutionReasonCodes.InvalidRequest, "Invalid image name."));
        }

        if (_options.AllowedImages is { Count: > 0 } allowed && !allowed.Contains(options.Image!, StringComparer.OrdinalIgnoreCase))
        {
            return Refuse(ExecutionDecision.Deny(ExecutionReasonCodes.ImageUnavailable, $"Image '{options.Image}' is not in the allowed image list."));
        }

        if (options.PinToBaseCommit && !PinnedCommitSnapshotBuilder.IsFullCommitId(context.BaseCommit))
        {
            return Refuse(ExecutionDecision.Deny(ExecutionReasonCodes.InvalidTrustedContext, "PinToBaseCommit requires a full BaseCommit object id in the trusted context."));
        }

        if (!TryEnsureLease(out var leaseProblem))
        {
            return Refuse(ExecutionDecision.Unavailable(ExecutionReasonCodes.IsolationUnavailable, leaseProblem));
        }

        var probe = await ProbeAsync(ct).ConfigureAwait(false);
        if (!probe.Ok)
        {
            return Refuse(ExecutionDecision.Unavailable(ExecutionReasonCodes.IsolationUnavailable, probe.Message));
        }

        var inspect = await _docker.RunAsync(new DockerInvocation(["image", "inspect", "--format", "{{.Id}}", options.Image!]) { Timeout = TimeSpan.FromSeconds(30) }, ct).ConfigureAwait(false);
        if (inspect.ExitCode != 0)
        {
            if (!_options.PullMissingImages)
            {
                return Refuse(ExecutionDecision.Unavailable(ExecutionReasonCodes.ImageUnavailable, $"Image '{options.Image}' is not present locally and pulling is disabled."));
            }

            var pull = await _docker.RunAsync(new DockerInvocation(["pull", "--quiet", options.Image!]) { Timeout = TimeSpan.FromMinutes(15) }, ct).ConfigureAwait(false);
            if (pull.ExitCode != 0)
            {
                return Refuse(ExecutionDecision.Unavailable(ExecutionReasonCodes.ImageUnavailable, $"Could not pull image '{options.Image}': {Trim(pull.Stderr)}"));
            }

            inspect = await _docker.RunAsync(new DockerInvocation(["image", "inspect", "--format", "{{.Id}}", options.Image!]) { Timeout = TimeSpan.FromSeconds(30) }, ct).ConfigureAwait(false);
        }

        var imageId = inspect.Stdout.Trim();
        var handle = "iso_" + Guid.NewGuid().ToString("N");
        var stagingDir = Path.Combine(StagingRoot, InstanceId, handle);
        Directory.CreateDirectory(stagingDir);

        WorkspaceSnapshot snapshot;
        try
        {
            var tarPath = Path.Combine(stagingDir, "snapshot.tar");
            var limits = context.Limits;
            snapshot = await Task.Run(() =>
            {
                if (!options.StageWorkspace)
                {
                    using var fs = new FileStream(tarPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var w = new System.Formats.Tar.TarWriter(fs, System.Formats.Tar.TarEntryFormat.Gnu, leaveOpen: true);
                    w.WriteEntry(new System.Formats.Tar.GnuTarEntry(System.Formats.Tar.TarEntryType.Directory, "workspace")
                    {
                        Mode = (UnixFileMode)Convert.ToInt32("755", 8),
                        Uid = WorkspaceSnapshotBuilder.GuestUid,
                        Gid = WorkspaceSnapshotBuilder.GuestUid
                    });
                    return new WorkspaceSnapshot { TarPath = tarPath, Baseline = new Dictionary<string, BaselineEntry>() };
                }

                return options.PinToBaseCommit
                    ? PinnedCommitSnapshotBuilder.Build(context.WorkspaceRoot, context.BaseCommit!, tarPath, limits)
                    : WorkspaceSnapshotBuilder.Build(context.WorkspaceRoot, tarPath, limits);
            }, ct).ConfigureAwait(false);
        }
        catch (WorkspaceSnapshotException ex)
        {
            DeleteDir(stagingDir);
            var deny = ex.ReasonCode is ExecutionReasonCodes.IsolationUnavailable or ExecutionReasonCodes.WorkspaceNotFound
                ? ExecutionDecision.Unavailable(ex.ReasonCode, ex.Message)
                : ExecutionDecision.Deny(ex.ReasonCode, ex.Message);
            return Refuse(deny);
        }
        catch
        {
            DeleteDir(stagingDir);
            throw;
        }

        var containerId = string.Empty;
        GuestGitInfo? guestGitInfo = null;
        try
        {
            var create = await _docker.RunAsync(new DockerInvocation(BuildCreateArgs(handle, options.Image!, context, _options, InstanceId, HostLabel, snapshot.TotalBytes, StoreId, options.GuestGit != GuestGitMode.None)) { Timeout = TimeSpan.FromSeconds(60) }, ct).ConfigureAwait(false);
            if (create.ExitCode != 0)
            {
                DeleteDir(stagingDir);
                return Refuse(ExecutionDecision.Unavailable(ExecutionReasonCodes.IsolationUnavailable, "docker create failed: " + Trim(create.Stderr)));
            }

            containerId = create.Stdout.Trim();
            _active.TryAdd(containerId, 0);
            var start = await _docker.RunAsync(new DockerInvocation(["start", containerId]) { Timeout = TimeSpan.FromSeconds(60) }, ct).ConfigureAwait(false);
            if (start.ExitCode != 0)
            {
                await RemoveContainerAsync(containerId).ConfigureAwait(false);
                DeleteDir(stagingDir);
                return Refuse(ExecutionDecision.Unavailable(ExecutionReasonCodes.IsolationUnavailable, "docker start failed: " + Trim(start.Stderr)));
            }

            // The workspace is a size-capped tmpfs (docker cp cannot write into tmpfs), so the private snapshot is streamed in
            // through the image's own tar as the unprivileged guest user. The tar is host-built and trusted at this point.
            await using (var tarStream = new FileStream(snapshot.TarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var load = await _docker.RunAsync(new DockerInvocation(["exec", "-i", containerId, "tar", "-x", "-C", "/", "-f", "-"]) { StdIn = tarStream, Timeout = TimeSpan.FromMinutes(5) }, ct).ConfigureAwait(false);
                if (load.ExitCode != 0)
                {
                    await RemoveContainerAsync(containerId).ConfigureAwait(false);
                    DeleteDir(stagingDir);
                    return Refuse(ExecutionDecision.Unavailable(ExecutionReasonCodes.IsolationUnavailable,
                        "loading the snapshot into the container failed (the image needs a working 'tar'): " + Trim(load.Stderr)));
                }
            }

            DeleteQuiet(snapshot.TarPath);

            foreach (var (input, index) in (options.Inputs ?? []).Select((x, i) => (x, i)))
            {
                if (!WorkspacePath.TryNormalizeStrict(input.GuestSubPath, out var guestSub, out var guestWhy) || WorkspacePath.IsProtected(guestSub))
                {
                    await RemoveContainerAsync(containerId).ConfigureAwait(false);
                    DeleteDir(stagingDir);
                    return Refuse(ExecutionDecision.Deny(ExecutionReasonCodes.InvalidRequest, "input guest path rejected: " + guestWhy));
                }

                var inputTar = Path.Combine(stagingDir, $"input{index}.tar");
                try
                {
                    WorkspaceSnapshotBuilder.Build(input.HostPath, inputTar, context.Limits, "tmp/" + guestSub);
                }
                catch (WorkspaceSnapshotException ex)
                {
                    await RemoveContainerAsync(containerId).ConfigureAwait(false);
                    DeleteDir(stagingDir);
                    return Refuse(ExecutionDecision.Deny(ex.ReasonCode, "input directory refused: " + ex.Message));
                }

                await using var inputStream = new FileStream(inputTar, FileMode.Open, FileAccess.Read, FileShare.Read);
                var loadInput = await _docker.RunAsync(new DockerInvocation(["exec", "-i", containerId, "tar", "-x", "-C", "/", "-f", "-"]) { StdIn = inputStream, Timeout = TimeSpan.FromMinutes(2) }, ct).ConfigureAwait(false);
                if (loadInput.ExitCode != 0)
                {
                    await RemoveContainerAsync(containerId).ConfigureAwait(false);
                    DeleteDir(stagingDir);
                    return Refuse(ExecutionDecision.Unavailable(ExecutionReasonCodes.IsolationUnavailable, "loading an input directory failed: " + Trim(loadInput.Stderr)));
                }
            }

            if (options.GuestGit != GuestGitMode.None)
            {
                var git = await InitGuestGitAsync(containerId, options.GuestGit, snapshot, context, ct).ConfigureAwait(false);
                if (git.Failure is { } failure)
                {
                    await RemoveContainerAsync(containerId).ConfigureAwait(false);
                    DeleteDir(stagingDir);
                    return Refuse(failure);
                }

                guestGitInfo = git.Info;
            }
        }
        catch
        {
            if (containerId.Length > 0)
            {
                await RemoveContainerAsync(containerId).ConfigureAwait(false);
            }

            DeleteDir(stagingDir);
            throw;
        }

        var session = new DockerSession(this, handle, containerId, imageId, stagingDir, snapshot, context, options) { GuestGit = guestGitInfo };
        return new IsolatedSessionOpenResult(ExecutionDecision.Allow(), session);
    }

    /// <summary>
    /// Removes containers and staging dirs left behind by DEAD instances (crash/restart recovery). Ownership rules:
    /// only containers labelled with this machine AND this staging store (the lease directory the verdict is based on) are considered; a container owned by another instance is removed only
    /// when that instance's lease is no longer held; this instance's own live sessions are never touched (a container
    /// of this instance that is not an active session and is older than OrphanMaxAge counts as leaked).
    /// </summary>
    public async Task<int> ReapOrphansAsync(CancellationToken ct)
    {
        if (!TryEnsureLease(out _))
        {
            return 0;
        }

        var format = """{{.ID}}|{{.Label "codexflow.isolation.owner"}}|{{.Label "codexflow.isolation.host"}}|{{.Label "codexflow.isolation.created"}}""";
        var ps = await _docker.RunAsync(new DockerInvocation(
            ["ps", "-a", "--no-trunc", "--filter", $"label={Label}=1", "--filter", $"label=codexflow.isolation.store={StoreId}", "--format", format]) { Timeout = TimeSpan.FromSeconds(30) }, ct).ConfigureAwait(false);
        if (ps.ExitCode != 0)
        {
            return 0;
        }

        var removed = 0;
        var leakCutoff = DateTimeOffset.UtcNow - _options.OrphanMaxAge;
        foreach (var line in ps.Stdout.Split((char)10, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            if (parts.Length != 4 || parts[2] != HostLabel || parts[1].Length == 0)
            {
                continue;
            }

            var id = parts[0];
            if (parts[1] == InstanceId)
            {
                var leaked = !_active.ContainsKey(id) &&
                             long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix) &&
                             DateTimeOffset.FromUnixTimeSeconds(unix) < leakCutoff;
                if (!leaked)
                {
                    continue;
                }
            }
            else if (IsolationOwnerLease.IsAlive(StagingRoot, parts[1]))
            {
                continue;
            }

            await RemoveContainerAsync(id).ConfigureAwait(false);
            removed++;
        }

        if (Directory.Exists(StagingRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
            {
                var name = Path.GetFileName(dir);
                if (name.Length == 32 && name != InstanceId && name.All(Uri.IsHexDigit) && !IsolationOwnerLease.IsAlive(StagingRoot, name))
                {
                    DeleteDir(dir);
                }
            }
        }

        return removed;
    }

    private static readonly string[] SafeGitConfig =
    [
        "-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false", "-c", "core.autocrlf=false", "-c", "core.safecrlf=false",
        "-c", "commit.gpgsign=false", "-c", "core.untrackedCache=false", "-c", "gc.auto=0", "-c", "protocol.allow=never"
    ];

    private async Task<(ExecutionDecision? Failure, GuestGitInfo? Info)> InitGuestGitAsync(
        string containerId, GuestGitMode mode, WorkspaceSnapshot snapshot, TrustedExecutionContext ctx, CancellationToken ct)
    {
        async Task<DockerRunResult> Git(params string[] gitArgs)
        {
            var args = new List<string> { "exec", containerId, "git" };
            args.AddRange(SafeGitConfig);
            args.AddRange(gitArgs);
            return await _docker.RunAsync(new DockerInvocation(args) { Timeout = TimeSpan.FromMinutes(5) }, ct).ConfigureAwait(false);
        }

        var version = await _docker.RunAsync(new DockerInvocation(["exec", containerId, "git", "--version"]) { Timeout = TimeSpan.FromSeconds(30) }, ct).ConfigureAwait(false);
        if (version.ExitCode != 0)
        {
            return mode == GuestGitMode.Required
                ? (ExecutionDecision.Unavailable(ExecutionReasonCodes.ImageUnavailable, "A guest git repository was required but the image has no usable git."), null)
                : (null, null);
        }

        // Empty template => no hooks, no sample files. The repo is built from the private snapshot only.
        foreach (var step in new[]
        {
            new[] { "init", "-q", "--template=", "--initial-branch=baseline" },
            ["add", "-A", "-f"],
            ["commit", "-q", "--no-verify", "--no-gpg-sign", "--allow-empty", "-m", "codexflow guest baseline"]
        })
        {
            var r = await Git(step).ConfigureAwait(false);
            if (r.ExitCode != 0)
            {
                return (ExecutionDecision.Unavailable(ExecutionReasonCodes.IsolationUnavailable, $"guest git '{step[0]}' failed: {Trim(r.Stderr)}"), null);
            }
        }

        var head = await Git("rev-parse", "HEAD").ConfigureAwait(false);
        var tree = await Git("rev-parse", "HEAD^{tree}").ConfigureAwait(false);
        if (head.ExitCode != 0 || tree.ExitCode != 0)
        {
            return (ExecutionDecision.Unavailable(ExecutionReasonCodes.IsolationUnavailable, "guest git could not report its baseline commit"), null);
        }

        var manifest = new System.Text.StringBuilder();
        foreach (var kv in snapshot.Baseline.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            manifest.Append(kv.Key).Append(':').Append(kv.Value.Sha256).Append((char)10);
        }

        var guestTree = tree.Stdout.Trim();
        var info = new GuestGitInfo(
            head.Stdout.Trim(),
            guestTree,
            ctx.BaseCommit is { Length: > 0 } && snapshot.HostTreeId is not null ? ctx.BaseCommit : null,
            snapshot.HostTreeId,
            snapshot.HostTreeId is not null && string.Equals(snapshot.HostTreeId, guestTree, StringComparison.OrdinalIgnoreCase),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest.ToString()))));
        return (null, info);
    }

    internal static IReadOnlyList<string> BuildCreateArgs(
        string handle, string image, TrustedExecutionContext ctx, IsolationOptions? opts = null,
        string instanceId = "none", string hostLabel = "none", long snapshotBytes = 0, string storeId = "none", bool guestGit = false)
    {
        var workspaceBytes = Math.Max(ctx.Limits.GuestWorkspaceBytes, snapshotBytes + 64L * 1024 * 1024);
        var l = ctx.Limits;
        var args = new List<string>
        {
            "create",
            "--name", "cfiso-" + handle,
            "--label", Label + "=1",
            "--label", "codexflow.isolation.session=" + handle,
            "--label", "codexflow.isolation.owner=" + instanceId,
            "--label", "codexflow.isolation.host=" + hostLabel,
            "--label", "codexflow.isolation.store=" + storeId,
            "--label", "codexflow.isolation.created=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "--network", "none",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", l.Pids.ToString(CultureInfo.InvariantCulture),
            "--memory", l.MemoryBytes.ToString(CultureInfo.InvariantCulture),
            "--memory-swap", l.MemoryBytes.ToString(CultureInfo.InvariantCulture),
            "--cpus", l.Cpus.ToString("0.##", CultureInfo.InvariantCulture),
            "--ulimit", "nofile=4096:4096",
            "--ulimit", "fsize=" + Math.Min(l.MaxWorkspaceBytes, 512L * 1024 * 1024).ToString(CultureInfo.InvariantCulture),
            "--user", $"{WorkspaceSnapshotBuilder.GuestUid}:{WorkspaceSnapshotBuilder.GuestUid}",
            // Read-only root plus byte- and inode-capped tmpfs volumes: the guest has no unbounded writable storage at all,
            // so it cannot fill the Docker host disk with data or with tiny files.
            "--read-only",
            "--tmpfs", $"/workspace:rw,nosuid,nodev,exec,size={workspaceBytes},nr_inodes={ctx.Limits.GuestMaxInodes},mode=0755,uid={WorkspaceSnapshotBuilder.GuestUid},gid={WorkspaceSnapshotBuilder.GuestUid}",
            "--tmpfs", $"/tmp:rw,nosuid,nodev,exec,size={ctx.Limits.GuestTmpBytes},nr_inodes={ctx.Limits.GuestMaxInodes},mode=1777",
            "--shm-size", "64m",
            "--init",
            "--stop-timeout", "1",
            "--hostname", "sandbox",
            "--workdir", "/workspace",
            "-e", "HOME=/tmp/home",
            "-e", "TMPDIR=/tmp",
            "-e", "DOTNET_CLI_HOME=/tmp/home",
            "-e", "DOTNET_NOLOGO=1",
            "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "-e", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1",
            "-e", "NUGET_XMLDOC_MODE=skip"
        };

        var fallbackFolders = new List<string>();
        if (opts?.RestoreCacheHostPath is { Length: > 0 } cache && Directory.Exists(cache))
        {
            // The NuGet SDK resolver only looks in the GLOBAL packages folder (never in fallback folders), so the trusted
            // cache is also the global folder. It stays a read-only mount: with every package present NuGet writes nothing there.
            args.AddRange(["-v", Path.GetFullPath(cache) + ":/restore-cache:ro", "-e", "NUGET_PACKAGES=/restore-cache"]);
            fallbackFolders.Add("/restore-cache");
        }
        else
        {
            args.AddRange(["-e", "NUGET_PACKAGES=/tmp/home/.nuget/packages"]);
        }

        // Packages the host fetched for THIS session (located by the trusted session id, never by anything the guest or model chose):
        // a second read-only fallback folder, so security upgrades the base cache lacks can be restored without any network.
        if (RestoreOverlayLocator.Resolve(opts?.RestoreOverlayRoot, ctx.SessionId) is { } overlay)
        {
            args.AddRange(["-v", overlay + ":/restore-cache-extra:ro"]);
            fallbackFolders.Add("/restore-cache-extra");
        }

        if (fallbackFolders.Count > 0)
        {
            args.AddRange(["-e", "RestoreAdditionalProjectFallbackFolders=" + string.Join(';', fallbackFolders)]);
            // The container has no network. A package asking for "Y (>= 2.5.303)" makes NuGet look for exactly 2.5.303 first; with only 2.5.307 on disk
            // that lookup reaches for the unreachable official source and fails (NU1301) although a higher version satisfies the request. Ignoring the
            // unreachable source turns that into the usual NU1603 warning; a package that is really missing still fails (NU1102).
            args.AddRange(["-e", "RestoreIgnoreFailedSources=true"]);
        }

        // Offline caches of the other toolchains, read-only like the NuGet one: the verification restores from them, never from the network.
        if (opts?.NpmCacheHostPath is { Length: > 0 } npmCache && Directory.Exists(npmCache))
        {
            args.AddRange(["-v", Path.GetFullPath(npmCache) + ":/npm-cache:ro"]);
        }

        if (opts?.PipWheelhouseHostPath is { Length: > 0 } wheelhouse && Directory.Exists(wheelhouse))
        {
            args.AddRange(["-v", Path.GetFullPath(wheelhouse) + ":/pip-wheels:ro"]);
        }

        if (guestGit)
        {
            // Every guest process (build tools included) sees the independent guest repo; no host path, no global/system config.
            args.AddRange([
                "-e", "GIT_DIR=" + GuestGitDir, "-e", "GIT_WORK_TREE=/workspace", "-e", "GIT_CONFIG_NOSYSTEM=1",
                "-e", "GIT_CONFIG_GLOBAL=/dev/null", "-e", "GIT_TERMINAL_PROMPT=0", "-e", "GIT_OPTIONAL_LOCKS=0",
                "-e", "GIT_AUTHOR_NAME=codexflow-guest", "-e", "GIT_AUTHOR_EMAIL=guest@codexflow.invalid",
                "-e", "GIT_COMMITTER_NAME=codexflow-guest", "-e", "GIT_COMMITTER_EMAIL=guest@codexflow.invalid"
            ]);
        }

        args.AddRange(["--entrypoint", "tail", image, "-f", "/dev/null"]);
        return args;
    }

    private async Task<(bool Ok, string Message)> ProbeAsync(CancellationToken ct)
    {
        if (_probe is { } p && DateTimeOffset.UtcNow - p.At < TimeSpan.FromSeconds(30))
        {
            return (p.Ok, p.Message);
        }

        await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var r = await _docker.RunAsync(new DockerInvocation(["version", "--format", "{{.Server.Os}}"]) { Timeout = TimeSpan.FromSeconds(20) }, ct).ConfigureAwait(false);
            var ok = r.ExitCode == 0 && r.Stdout.Trim().Equals("linux", StringComparison.OrdinalIgnoreCase);
            var msg = ok ? "ok" : r.ExitCode != 0
                ? "Docker daemon is not reachable: " + Trim(r.Stderr)
                : $"Docker backend must be Linux containers (got '{r.Stdout.Trim()}').";
            _probe = (DateTimeOffset.UtcNow, ok, msg);
            return (ok, msg);
        }
        finally
        {
            _probeGate.Release();
        }
    }

    internal async Task RemoveContainerAsync(string containerId)
    {
        _active.TryRemove(containerId, out _);
        var r = await _docker.RunAsync(new DockerInvocation(["rm", "-f", "-v", containerId]) { Timeout = TimeSpan.FromSeconds(60) }, CancellationToken.None).ConfigureAwait(false);
        if (r.ExitCode != 0 && !r.Stderr.Contains("No such container", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Failed to remove isolation container {Container}: {Error}", containerId, Trim(r.Stderr));
        }
    }

    public void Dispose()
    {
        _probeGate.Dispose();
        lock (_leaseGate)
        {
            _lease?.Dispose();
            _lease = null;
        }
    }

    internal IDockerCli Docker => _docker;
    internal IsolationOptions Options => _options;
    internal IWorkspaceExportObserver? Observer => _observer;
    internal ILogger Logger => _logger;

    internal static IsolatedSessionOpenResult Refuse(ExecutionDecision decision) => new(decision, null);

    internal static string Trim(string s) => s.Length <= 500 ? s.Trim() : s[..500].Trim();

    internal static void DeleteQuiet(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static void DeleteDir(string dir)
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

internal sealed class DockerSession : IIsolatedExecutionSession
{
    private static readonly Regex EnvKey = new("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ForbiddenEnv = new("^(PATH|LD_.*|DOCKER.*|HOME|GIT_.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly DockerIsolatedExecutionService _svc;
    private readonly string _containerId;
    private readonly string _imageId;
    private readonly string _stagingDir;
    private readonly WorkspaceSnapshot _snapshot;
    private readonly TrustedExecutionContext _ctx;
    private readonly IsolatedSessionOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _abort = new();
    private int _terminated;
    private int _disposed;

    public DockerSession(
        DockerIsolatedExecutionService svc, string handle, string containerId, string imageId, string stagingDir,
        WorkspaceSnapshot snapshot, TrustedExecutionContext ctx, IsolatedSessionOptions options)
    {
        _svc = svc;
        SessionHandle = handle;
        _containerId = containerId;
        _imageId = imageId;
        _stagingDir = stagingDir;
        _snapshot = snapshot;
        _ctx = ctx;
        _options = options;
    }

    public string SessionHandle { get; }

    public GuestGitInfo? GuestGit { get; init; }

    private bool Terminated => Volatile.Read(ref _terminated) != 0;

    public async Task<IsolatedCommandResult> RunAsync(IsolatedCommandRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Argv is null || request.Argv.Count == 0 || request.Argv.Any(a => a is null || a.Contains('\0', StringComparison.Ordinal)) || string.IsNullOrWhiteSpace(request.Argv[0]))
        {
            return IsolatedCommandResult.Refused(ExecutionDecision.Deny(ExecutionReasonCodes.InvalidRequest, "argv must be a non-empty list without NUL."));
        }

        var workdir = "/workspace";
        if (!string.IsNullOrWhiteSpace(request.Cwd) && request.Cwd is not "." and not "./")
        {
            if (!WorkspacePath.TryNormalizeStrict(request.Cwd, out var cwd, out var why))
            {
                return IsolatedCommandResult.Refused(ExecutionDecision.Deny(ExecutionReasonCodes.InvalidRequest, "cwd rejected: " + why));
            }

            workdir += "/" + cwd;
        }

        var execArgs = new List<string> { "exec" };
        if (request.StdIn is not null)
        {
            execArgs.Add("-i");
        }

        execArgs.AddRange(["-w", workdir]);
        if (request.Env is not null)
        {
            foreach (var (k, v) in request.Env)
            {
                if (!EnvKey.IsMatch(k) || ForbiddenEnv.IsMatch(k) || v.Contains('\0', StringComparison.Ordinal))
                {
                    return IsolatedCommandResult.Refused(ExecutionDecision.Deny(ExecutionReasonCodes.InvalidRequest, $"environment variable '{k}' is not allowed."));
                }

                execArgs.AddRange(["-e", k + "=" + v]);
            }
        }

        execArgs.Add(_containerId);
        execArgs.AddRange(request.Argv);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Terminated || Volatile.Read(ref _disposed) != 0)
            {
                return IsolatedCommandResult.Refused(ExecutionDecision.Deny(ExecutionReasonCodes.SessionTerminated, "Session was terminated; no further commands run."));
            }

            var limits = _ctx.Limits;
            var timeout = request.Timeout is { } t && t < limits.CommandTimeout ? t : limits.CommandTimeout;
            if (_ctx.Deadline is { } deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    await KillAsync().ConfigureAwait(false);
                    return IsolatedCommandResult.Refused(ExecutionDecision.Deny(ExecutionReasonCodes.Timeout, "Execution deadline passed."));
                }

                if (remaining < timeout)
                {
                    timeout = remaining;
                }
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var truncated = false;
            long captured = 0;

            void Capture(string stream, string line)
            {
                request.OnOutput?.Invoke(new CommandOutputLine(stream, line));
                lock (stdout)
                {
                    if (captured >= limits.MaxOutputBytes)
                    {
                        truncated = true;
                        return;
                    }

                    captured += line.Length + 1;
                    (stream == "stdout" ? stdout : stderr).AppendLine(line);
                }
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _abort.Token);
            var started = DateTimeOffset.UtcNow;
            var run = await _svc.Docker.RunAsync(new DockerInvocation(execArgs)
            {
                StdInText = request.StdIn,
                Timeout = timeout,
                OnStdoutLine = l => Capture("stdout", l),
                OnStderrLine = l => Capture("stderr", l),
                MaxCaptureBytes = limits.MaxOutputBytes
            }, linked.Token).ConfigureAwait(false);

            var elapsed = DateTimeOffset.UtcNow - started;
            var evidence = $"image={_imageId}; cpus={limits.Cpus}; memory={limits.MemoryBytes}; pids={limits.Pids}; network=none; timeout={timeout.TotalSeconds:0}s";

            if (run.TimedOut || run.Canceled)
            {
                await KillAsync().ConfigureAwait(false);
                var status = run.TimedOut ? IsolatedCommandStatus.TimedOut : IsolatedCommandStatus.Canceled;
                var code = run.TimedOut ? ExecutionReasonCodes.Timeout : ExecutionReasonCodes.Canceled;
                return new IsolatedCommandResult(ExecutionDecision.Deny(code, status.ToString()), status, null, stdout.ToString(), stderr.ToString(), truncated, elapsed, evidence);
            }

            if (run.ExitCode == -1 && stderr.Length == 0 && run.Stderr.Length > 0)
            {
                return new IsolatedCommandResult(
                    ExecutionDecision.Unavailable(ExecutionReasonCodes.IsolationUnavailable, DockerIsolatedExecutionService.Trim(run.Stderr)),
                    IsolatedCommandStatus.Unavailable, null, string.Empty, run.Stderr, false, elapsed, evidence);
            }

            if (run.ExitCode == 137)
            {
                var oom = await _svc.Docker.RunAsync(new DockerInvocation(["inspect", "--format", "{{.State.OOMKilled}}", _containerId]) { Timeout = TimeSpan.FromSeconds(10) }, CancellationToken.None).ConfigureAwait(false);
                if (oom.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    await KillAsync().ConfigureAwait(false);
                    return new IsolatedCommandResult(
                        ExecutionDecision.Deny(ExecutionReasonCodes.ResourceLimitExceeded, "container exceeded its memory limit"),
                        IsolatedCommandStatus.ResourceExceeded, 137, stdout.ToString(), stderr.ToString(), truncated, elapsed, evidence);
                }
            }

            return new IsolatedCommandResult(
                ExecutionDecision.Allow(),
                run.ExitCode == 0 ? IsolatedCommandStatus.Completed : IsolatedCommandStatus.Failed,
                run.ExitCode, stdout.ToString(), stderr.ToString(), truncated || run.Truncated, elapsed, evidence);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IsolatedExportResult> ExportScopedChangesAsync(CancellationToken ct)
    {
        if (_options.PinToBaseCommit)
        {
            return IsolatedExportResult.Refused(ExecutionDecision.Deny(ExecutionReasonCodes.UnsupportedIsolation, "Pinned-commit sessions are verification-only; export is not available."));
        }

        if (!_options.StageWorkspace)
        {
            return IsolatedExportResult.Refused(ExecutionDecision.Deny(ExecutionReasonCodes.UnsupportedIsolation, "Session has no staged workspace to export."));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Terminated || Volatile.Read(ref _disposed) != 0)
            {
                return IsolatedExportResult.Refused(ExecutionDecision.Deny(ExecutionReasonCodes.SessionTerminated, "Session was terminated (cancel/timeout/limit); nothing is exported."));
            }

            var box = new GuestTarAnalysis?[1];
            var exportDir = Path.Combine(_stagingDir, "export");
            var cp = await _svc.Docker.RunAsync(new DockerInvocation(["exec", _containerId, "tar", "-c", "-C", "/", "workspace"])
            {
                Timeout = TimeSpan.FromMinutes(5),
                ConsumeStdout = s => box[0] = GuestTarAnalyzer.Analyze(s, _snapshot.Baseline, _ctx.Scope, _ctx.Limits, exportDir)
            }, ct).ConfigureAwait(false);
            var analysis = box[0];
            if (cp.ExitCode != 0 || analysis is null)
            {
                return IsolatedExportResult.Refused(ExecutionDecision.Deny(ExecutionReasonCodes.ExportFailed, "could not read guest workspace: " + DockerIsolatedExecutionService.Trim(cp.Stderr)));
            }

            if (analysis.Rejections.Count > 0)
            {
                return new IsolatedExportResult(
                    ExecutionDecision.Deny(analysis.Rejections[0].ReasonCode, $"export refused: {analysis.Rejections[0].Path}: {analysis.Rejections[0].Message}"),
                    Array.Empty<ExportedChange>(), analysis.Rejections);
            }

            return await ScopedExportPublisher.PublishAsync(_ctx.WorkspaceRoot, analysis.Changes, _snapshot.Baseline, _svc.Observer, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<EvidenceFile>> ReadEvidenceAsync(IReadOnlyList<string> globs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(globs);
        if (globs.Count == 0 || Volatile.Read(ref _disposed) != 0)
        {
            return [];
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<EvidenceFile> files = [];
            var cp = await _svc.Docker.RunAsync(new DockerInvocation(["exec", _containerId, "tar", "-c", "-C", "/", "workspace"])
            {
                Timeout = TimeSpan.FromMinutes(5),
                ConsumeStdout = s => files = GuestTarAnalyzer.ReadEvidence(s, globs)
            }, ct).ConfigureAwait(false);
            return cp.ExitCode == 0 ? files : [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AbortAsync()
    {
        await _abort.CancelAsync().ConfigureAwait(false);
        await KillAsync().ConfigureAwait(false);
    }

    private async Task KillAsync()
    {
        Interlocked.Exchange(ref _terminated, 1);
        await _svc.Docker.RunAsync(new DockerInvocation(["kill", _containerId]) { Timeout = TimeSpan.FromSeconds(30) }, CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _terminated, 1);
        await _abort.CancelAsync().ConfigureAwait(false);
        await _svc.RemoveContainerAsync(_containerId).ConfigureAwait(false);

        // Removing the container ends any in-flight exec, so the gate is released shortly; then nothing else can use it.
        await _gate.WaitAsync().ConfigureAwait(false);
        DockerIsolatedExecutionService.DeleteDir(_stagingDir);
        _abort.Dispose();
        _gate.Dispose();
    }
}
