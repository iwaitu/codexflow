// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.Execution;

/// <summary>
/// Local host execution backend. Stages a private copy of the workspace under
/// <c>{StagingRoot}/local/{instance}/{session}</c> and routes exports back through
/// <see cref="LocalDirectoryTarBuilder"/> -> <see cref="GuestTarAnalyzer"/> -> <see cref="ScopedExportPublisher"/>.
/// Host processes run with the current user's filesystem and network permissions
/// (truthfully recorded as <c>filesystem=unrestricted</c> and <c>Network=Host/None</c>).
/// </summary>
public sealed class LocalExecutionService : IIsolatedExecutionService, IDisposable
{
    private readonly IsolationOptions _options;
    private readonly ILogger<LocalExecutionService> _logger;
    private readonly IWorkspaceExportObserver? _observer;
    private readonly IOptionsMonitor<ExecutionOptions>? _executionOptions;
    private readonly Func<HostProcessInvocation, CancellationToken, Task<HostProcessResult>> _processRunner;
    private readonly ConcurrentDictionary<string, byte> _activeSessions = new(StringComparer.Ordinal);
    private readonly object _leaseGate = new();
    private IsolationOwnerLease? _lease;

    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    internal string StagingRoot { get; }
    internal string LocalRoot => Path.Combine(StagingRoot, "local");
    public ExecutionBackend ActiveBackend => ExecutionBackend.Local;

    public LocalExecutionService(
        IOptions<IsolationOptions> options,
        ILogger<LocalExecutionService> logger,
        IWorkspaceExportObserver? observer = null,
        Func<HostProcessInvocation, CancellationToken, Task<HostProcessResult>>? processRunner = null,
        IOptionsMonitor<ExecutionOptions>? executionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _observer = observer;
        _processRunner = processRunner ?? HostProcessRunner.RunAsync;
        _executionOptions = executionOptions;
        StagingRoot = _options.StagingRoot ?? Path.Combine(Path.GetTempPath(), "codexflow-isolation");
    }

    internal Task<HostProcessResult> RunProcessAsync(HostProcessInvocation invocation, CancellationToken ct)
        => _processRunner(invocation, ct);

    public bool SupportsNetworkMode(ExecutionNetworkMode mode)
        => mode is ExecutionNetworkMode.None or ExecutionNetworkMode.Host;

    public async Task<IsolatedSessionOpenResult> OpenSessionAsync(
        TrustedExecutionContext context,
        IsolatedSessionOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        if (!SupportsNetworkMode(context.Network.Mode))
        {
            return new IsolatedSessionOpenResult(
                ExecutionDecision.Unavailable(ExecutionReasonCodes.NetworkNotPermitted, $"Local execution does not support network mode '{context.Network.Mode}'."),
                null);
        }

        var valid = context.Validate();
        if (!valid.IsAllowed)
        {
            return new IsolatedSessionOpenResult(valid, null);
        }

        if (!TryEnsureLease(out var leaseError))
        {
            return new IsolatedSessionOpenResult(
                ExecutionDecision.Unavailable(ExecutionReasonCodes.IsolationUnavailable, leaseError),
                null);
        }

        var sessionHandle = $"local-{Guid.NewGuid():N}";
        var sessionDir = Path.Combine(LocalRoot, "sessions", InstanceId, sessionHandle);
        var workspaceDir = Path.Combine(sessionDir, "workspace");
        var homeDir = Path.Combine(sessionDir, "home");
        var tempDir = Path.Combine(sessionDir, "tmp");
        var inputsDir = Path.Combine(sessionDir, "inputs");
        var gitDir = Path.Combine(sessionDir, "git");
        var snapshotTarPath = Path.Combine(sessionDir, "snapshot.tar");

        Directory.CreateDirectory(workspaceDir);
        Directory.CreateDirectory(homeDir);
        Directory.CreateDirectory(tempDir);

        WorkspaceSnapshot? snapshot = null;
        try
        {
            if (options.PinToBaseCommit)
            {
                if (string.IsNullOrWhiteSpace(context.BaseCommit))
                {
                    return RefuseAndCleanup(sessionDir, ExecutionDecision.Deny(
                        ExecutionReasonCodes.InvalidTrustedContext,
                        "BaseCommit is required when PinToBaseCommit is enabled."));
                }

                snapshot = PinnedCommitSnapshotBuilder.Build(
                    context.WorkspaceRoot,
                    context.BaseCommit,
                    snapshotTarPath,
                    context.Limits);
                ExtractTarFilesOnly(snapshotTarPath, workspaceDir, stripPrefix: "workspace/");
            }
            else if (options.StageWorkspace)
            {
                snapshot = WorkspaceSnapshotBuilder.Build(
                    context.WorkspaceRoot,
                    snapshotTarPath,
                    context.Limits);
                ExtractTarFilesOnly(snapshotTarPath, workspaceDir, stripPrefix: "workspace/");
            }

            if (File.Exists(snapshotTarPath))
            {
                File.Delete(snapshotTarPath);
            }

            if (options.Inputs is { Count: > 0 })
            {
                CopyInputDirectories(options.Inputs, inputsDir, context.Limits);
            }

            GuestGitInfo? guestGit = null;
            if (options.GuestGit != GuestGitMode.None && snapshot is not null)
            {
                var (gitInfo, gitError) = await InitializeGuestGitAsync(
                    workspaceDir,
                    gitDir,
                    homeDir,
                    tempDir,
                    context.BaseCommit,
                    snapshot.HostTreeId,
                    ct).ConfigureAwait(false);
                if (gitError is not null && options.GuestGit == GuestGitMode.Required)
                {
                    return RefuseAndCleanup(sessionDir, ExecutionDecision.Unavailable(
                        ExecutionReasonCodes.IsolationUnavailable,
                        "Guest git repository initialization failed: " + gitError));
                }

                guestGit = gitInfo;
            }

            _activeSessions[sessionHandle] = 0;
            var session = new LocalExecutionSession(
                this,
                sessionHandle,
                sessionDir,
                workspaceDir,
                homeDir,
                tempDir,
                inputsDir,
                gitDir,
                snapshot,
                context,
                canExport: options.StageWorkspace && !options.PinToBaseCommit,
                _observer,
                _logger)
            {
                GuestGit = guestGit
            };

            return new IsolatedSessionOpenResult(ExecutionDecision.Allow(), session);
        }
        catch (WorkspaceSnapshotException ex)
        {
            return RefuseAndCleanup(sessionDir, ExecutionDecision.Deny(ex.ReasonCode, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RefuseAndCleanup(sessionDir, ExecutionDecision.Unavailable(
                ExecutionReasonCodes.IsolationUnavailable,
                "Failed to prepare local staging session: " + ex.Message));
        }
    }

    internal void MarkSessionClosed(string handle) => _activeSessions.TryRemove(handle, out _);

    public async Task<int> ReapOrphansAsync(CancellationToken ct = default)
    {
        var sessionsRoot = Path.Combine(LocalRoot, "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return 0;
        }

        var removed = 0;
        foreach (var ownerDir in Directory.EnumerateDirectories(sessionsRoot))
        {
            ct.ThrowIfCancellationRequested();
            var ownerId = Path.GetFileName(ownerDir);
            if (ownerId.StartsWith(".pending-delete-", StringComparison.Ordinal))
            {
                if (TryDeleteDirectory(ownerDir))
                {
                    removed++;
                }

                continue;
            }

            if (string.Equals(ownerId, InstanceId, StringComparison.Ordinal))
            {
                foreach (var child in Directory.EnumerateDirectories(ownerDir, ".pending-delete-*"))
                {
                    if (TryDeleteDirectory(child))
                    {
                        removed++;
                    }
                }

                continue;
            }

            if (!IsolationOwnerLease.IsAlive(LocalRoot, ownerId) && TryDeleteDirectory(ownerDir))
            {
                removed++;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return removed;
    }

    private bool TryEnsureLease(out string error)
    {
        error = string.Empty;
        lock (_leaseGate)
        {
            if (_lease is not null)
            {
                return true;
            }

            try
            {
                _lease = IsolationOwnerLease.Acquire(LocalRoot, InstanceId);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = "could not acquire local staging owner lease: " + ex.Message;
                return false;
            }
        }
    }

    private static IsolatedSessionOpenResult RefuseAndCleanup(string sessionDir, ExecutionDecision decision)
    {
        TryDeleteDirectory(sessionDir);
        return new IsolatedSessionOpenResult(decision, null);
    }

    private static void ExtractTarFilesOnly(string tarPath, string destinationRoot, string? stripPrefix)
    {
        using var fs = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new TarReader(fs);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
            {
                continue;
            }

            var name = entry.Name.TrimStart('.', '/');
            if (!string.IsNullOrEmpty(stripPrefix) && name.StartsWith(stripPrefix, StringComparison.Ordinal))
            {
                name = name[stripPrefix.Length..];
            }

            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!WorkspacePath.TryNormalizeStrict(name, out var normalized, out _))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destinationRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
            }
            else if (entry.EntryType == TarEntryType.RegularFile && entry.DataStream is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var outStream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                entry.DataStream.CopyTo(outStream);
            }
        }
    }

    internal static void CopyInputDirectories(
        IReadOnlyList<IsolatedInputDirectory> inputs,
        string inputsRoot,
        ExecutionResourceLimits limits)
    {
        var files = 0;
        long bytes = 0;
        foreach (var input in inputs)
        {
            if (!WorkspacePath.TryNormalizeStrict(input.GuestSubPath, out var subNorm, out var why))
            {
                throw new WorkspaceSnapshotException(ExecutionReasonCodes.InvalidRequest, $"Invalid input sub-path '{input.GuestSubPath}': {why}");
            }

            var srcRoot = Path.GetFullPath(input.HostPath);
            if (!Directory.Exists(srcRoot))
            {
                throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceNotFound, $"Input directory not found: {srcRoot}");
            }

            // Reject input roots that are links/junctions or resolve into protected host credential/state directories!
            var srcDirInfo = new DirectoryInfo(srcRoot);
            var resolvedSrcRoot = Approval.AutoInWorkspaceEvaluator.ResolveFinalTargetPath(srcRoot);
            if (srcDirInfo.LinkTarget is not null ||
                srcDirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                resolvedSrcRoot is null ||
                WorkspacePath.IsProtectedHostPath(srcRoot) ||
                WorkspacePath.IsProtectedHostPath(resolvedSrcRoot) ||
                resolvedSrcRoot.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(static s => s.Equals(".codexflow", StringComparison.OrdinalIgnoreCase)))
            {
                throw new WorkspaceSnapshotException(
                    ExecutionReasonCodes.InvalidRequest,
                    $"Input directory root '{srcRoot}' is a link/reparse point or targets a protected host directory.");
            }

            var dstRoot = Path.Combine(inputsRoot, subNorm.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dstRoot);

            var stack = new Stack<(string Src, string Dst, string Rel)>();
            stack.Push((srcRoot, dstRoot, string.Empty));
            while (stack.Count > 0)
            {
                var (src, dst, rel) = stack.Pop();
                foreach (var child in new DirectoryInfo(src).EnumerateFileSystemInfos())
                {
                    if (child.LinkTarget is not null || child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    var childRel = rel.Length == 0 ? child.Name : rel + "/" + child.Name;
                    if (!WorkspacePath.TryNormalizeStrict(childRel, out var normRel, out _) ||
                        WorkspacePath.IsProtected(normRel) ||
                        WorkspacePath.IsProtectedHostPath(child.FullName))
                    {
                        continue;
                    }

                    var target = Path.Combine(dst, child.Name);
                    if (child is DirectoryInfo)
                    {
                        if (WorkspaceSnapshotBuilder.IsExcludedDirectoryName(child.Name))
                        {
                            continue;
                        }

                        Directory.CreateDirectory(target);
                        stack.Push((child.FullName, target, normRel));
                    }
                    else if (child is FileInfo file)
                    {
                        if (WorkspaceSnapshotBuilder.IsExcludedFileName(child.Name))
                        {
                            continue;
                        }

                        files++;
                        bytes += file.Length;
                        if (files > limits.MaxFiles || bytes > limits.MaxWorkspaceBytes)
                        {
                            throw new WorkspaceSnapshotException(ExecutionReasonCodes.WorkspaceTooLarge, "Input directories exceed staging quota.");
                        }

                        file.CopyTo(target, overwrite: false);
                    }
                }
            }

            if (!string.IsNullOrEmpty(input.ExpectedManifestSha256))
            {
                var actualManifest = Approval.ExecutionRequest.ComputeDirectoryManifestSha256(dstRoot);
                if (!string.Equals(actualManifest, input.ExpectedManifestSha256, StringComparison.Ordinal))
                {
                    throw new WorkspaceSnapshotException(
                        ExecutionReasonCodes.ApprovalInvalidated,
                        $"Staged input directory manifest for '{subNorm}' differed from approved digest.");
                }
            }

            if (!string.IsNullOrEmpty(input.ExpectedEntryRelativePath) && !string.IsNullOrEmpty(input.ExpectedEntrySha256))
            {
                var entryPath = Path.Combine(dstRoot, input.ExpectedEntryRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(entryPath) ||
                    !string.Equals(Approval.ExecutionRequest.Sha256File(entryPath), input.ExpectedEntrySha256, StringComparison.Ordinal))
                {
                    throw new WorkspaceSnapshotException(
                        ExecutionReasonCodes.ApprovalInvalidated,
                        $"Staged input entry file '{input.ExpectedEntryRelativePath}' differed from approved digest.");
                }
            }
        }
    }

    private async Task<(GuestGitInfo? Info, string? Error)> InitializeGuestGitAsync(
        string workspaceDir,
        string gitDir,
        string homeDir,
        string tempDir,
        string? hostBaseCommit,
        string? hostTreeId,
        CancellationToken ct)
    {
        Directory.CreateDirectory(gitDir);
        var env = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            ["HOME"] = homeDir,
            ["USERPROFILE"] = homeDir,
            ["TEMP"] = tempDir,
            ["TMP"] = tempDir,
            ["GIT_DIR"] = gitDir,
            ["GIT_WORK_TREE"] = workspaceDir,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_AUTHOR_NAME"] = "CodexFlow",
            ["GIT_AUTHOR_EMAIL"] = "codexflow@local",
            ["GIT_COMMITTER_NAME"] = "CodexFlow",
            ["GIT_COMMITTER_EMAIL"] = "codexflow@local"
        };
        foreach (var varName in new[] { "SystemRoot", "WINDIR", "windir", "OS" })
        {
            var val = Environment.GetEnvironmentVariable(varName);
            if (!string.IsNullOrEmpty(val))
            {
                env[varName] = val;
            }
        }

        var configuredTrusted = _executionOptions?.CurrentValue.TrustedExecutablePaths?.ToArray() ?? [];
        if (!Approval.AutoInWorkspaceEvaluator.IsTrustedExecutableOrigin(
                "git",
                workspaceDir,
                StagingRoot,
                configuredTrusted,
                out var trustedGit))
        {
            return (null, "Trusted git executable was not found in verified installation locations; refusing untrusted PATH fallback for GuestGit.");
        }

        async Task<HostProcessResult> Git(params string[] args)
            => await _processRunner(
                new HostProcessInvocation(trustedGit, args)
                {
                    WorkingDirectory = workspaceDir,
                    Environment = env,
                    ClearEnvironment = true,
                    Timeout = TimeSpan.FromSeconds(30)
                },
                ct).ConfigureAwait(false);

        var init = await Git("init", "-q").ConfigureAwait(false);
        if (!init.Started || init.ExitCode != 0)
        {
            return (null, init.StartError ?? init.Stderr);
        }

        var add = await Git("add", "-A").ConfigureAwait(false);
        if (add.ExitCode != 0)
        {
            return (null, add.Stderr);
        }

        var commit = await Git("commit", "--allow-empty", "-q", "-m", "codexflow-baseline").ConfigureAwait(false);
        if (commit.ExitCode != 0)
        {
            return (null, commit.Stderr);
        }

        var rev = await Git("rev-parse", "HEAD").ConfigureAwait(false);
        var tree = await Git("rev-parse", "HEAD^{tree}").ConfigureAwait(false);
        var baselineCommit = rev.Stdout.Trim();
        var baselineTree = tree.Stdout.Trim();
        var matchesHost = !string.IsNullOrEmpty(hostTreeId) &&
            string.Equals(baselineTree, hostTreeId, StringComparison.OrdinalIgnoreCase);

        return (new GuestGitInfo(baselineCommit, baselineTree, hostBaseCommit, hostTreeId, matchesHost, string.Empty), null);
    }

    internal static bool TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    ClearReadOnlyAttributes(path);
                }

                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 2)
                {
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
        }

        try
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is not null)
            {
                var pendingName = Path.Combine(parent, ".pending-delete-" + Guid.NewGuid().ToString("N"));
                Directory.Move(path, pendingName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_leaseGate)
        {
            _lease?.Dispose();
            _lease = null;
        }
    }
}

internal sealed class LocalExecutionSession : IIsolatedExecutionSession
{
    private static readonly string[] InheritedHostEnvVars =
    [
        "PATH", "Path", "SystemRoot", "WINDIR", "windir", "COMSPEC", "ComSpec", "PATHEXT",
        "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "ProgramFiles", "ProgramFiles(x86)",
        "CommonProgramFiles", "DOTNET_ROOT", "DOTNET_CLI_TELEMETRY_OPTOUT",
        "NUGET_PACKAGES", "npm_config_cache", "PIP_CACHE_DIR",
        "LANG", "LC_ALL", "TERM"
    ];

    private static readonly string[] StrippedEnvPrefixes =
    [
        "GIT_", "VllmAgent__", "CODEXFLOW_"
    ];

    private readonly LocalExecutionService _service;
    private readonly string _sessionDir;
    private readonly string _workspaceDir;
    private readonly string _homeDir;
    private readonly string _tempDir;
    private readonly string _gitDir;
    private readonly WorkspaceSnapshot? _snapshot;
    private readonly TrustedExecutionContext _context;
    private readonly bool _canExport;
    private readonly IWorkspaceExportObserver? _observer;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _sessionCts = new();
    private int _disposed;

    public LocalExecutionSession(
        LocalExecutionService service,
        string sessionHandle,
        string sessionDir,
        string workspaceDir,
        string homeDir,
        string tempDir,
        string inputsDir,
        string gitDir,
        WorkspaceSnapshot? snapshot,
        TrustedExecutionContext context,
        bool canExport,
        IWorkspaceExportObserver? observer,
        ILogger logger)
    {
        _service = service;
        SessionHandle = sessionHandle;
        _sessionDir = sessionDir;
        _workspaceDir = workspaceDir;
        _homeDir = homeDir;
        _tempDir = tempDir;
        _gitDir = gitDir;
        _snapshot = snapshot;
        _context = context;
        _canExport = canExport;
        _observer = observer;
        _logger = logger;
        Layout = new ExecutionLayout(
            ExecutionBackend.Local,
            workspaceDir,
            homeDir,
            tempDir,
            inputsDir,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    public string SessionHandle { get; }
    public ExecutionLayout Layout { get; }
    public GuestGitInfo? GuestGit { get; init; }

    public async Task<IsolatedCommandResult> RunAsync(IsolatedCommandRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return IsolatedCommandResult.Refused(ExecutionDecision.Deny(ExecutionReasonCodes.SessionTerminated, "Local execution session has been terminated."));
        }

        if (request.Argv.Count == 0 || string.IsNullOrWhiteSpace(request.Argv[0]))
        {
            return IsolatedCommandResult.Refused(ExecutionDecision.Deny(ExecutionReasonCodes.InvalidRequest, "Command argv must not be empty."));
        }

        var workingDir = ResolveWorkingDirectory(request.Cwd);
        if (workingDir is null)
        {
            return IsolatedCommandResult.Refused(ExecutionDecision.Deny(
                ExecutionReasonCodes.InvalidRequest,
                "Initial working directory must resolve inside the session staging directory."));
        }

        var env = BuildEnvironment(request.Env, request.TrustedMinimalEnv);
        ResolvedHostCommand resolved;
        if (request.PreResolvedExecutable)
        {
            var exe = request.Argv[0];
            var args = request.Argv.Skip(1).ToArray();
            if (!Path.IsPathRooted(exe) || !File.Exists(exe))
            {
                return IsolatedCommandResult.Refused(ExecutionDecision.Deny(
                    ExecutionReasonCodes.ApprovalInvalidated,
                    $"Pre-resolved approved executable '{exe}' is missing or not a canonical rooted path."));
            }

            if (OperatingSystem.IsWindows() && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return IsolatedCommandResult.Refused(ExecutionDecision.Deny(
                    ExecutionReasonCodes.InvalidRequest,
                    $"Pre-resolved Windows executable '{exe}' must be a native .exe binary."));
            }

            resolved = new ResolvedHostCommand(true, exe, args, [exe]);
        }
        else
        {
            resolved = WindowsCommandResolver.Resolve(request.Argv[0], request.Argv.Skip(1).ToArray(), env);
            if (!resolved.Allowed)
            {
                return IsolatedCommandResult.Refused(ExecutionDecision.Deny(
                    ExecutionReasonCodes.InvalidRequest,
                    resolved.RejectionReason ?? "Command rejected by Windows launch policy."));
            }
        }

        var timeout = request.Timeout ?? _context.Limits.CommandTimeout;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _service.RunProcessAsync(
            new HostProcessInvocation(resolved.Executable, resolved.Args)
            {
                WorkingDirectory = workingDir,
                Environment = env,
                ClearEnvironment = true,
                StdInText = request.StdIn,
                OnStdoutLine = line => request.OnOutput?.Invoke(new CommandOutputLine("stdout", line)),
                OnStderrLine = line => request.OnOutput?.Invoke(new CommandOutputLine("stderr", line)),
                Timeout = timeout,
                MaxCaptureBytes = _context.Limits.MaxOutputBytes
            },
            linked.Token).ConfigureAwait(false);
        sw.Stop();

        var evidence = $"backend=Local; network=Host; requestedNetwork={_context.Network.Mode}; filesystem=unrestricted; cwd={workingDir}; cmd={resolved.FormatAuditCommand()}";
        _logger.LogInformation(
            "Local execution completed for session {SessionId}: cmd={Cmd} exit={ExitCode} {Evidence}",
            _context.SessionId, request.Argv[0], result.ExitCode, evidence);

        if (!result.Started)
        {
            return new IsolatedCommandResult(
                ExecutionDecision.Unavailable(ExecutionReasonCodes.IsolationUnavailable, result.StartError ?? "failed to start command"),
                IsolatedCommandStatus.Unavailable,
                null,
                string.Empty,
                result.StartError ?? string.Empty,
                false,
                sw.Elapsed,
                evidence);
        }

        var status = result.TimedOut
            ? IsolatedCommandStatus.TimedOut
            : result.Canceled
                ? IsolatedCommandStatus.Canceled
                : result.ExitCode == 0
                    ? IsolatedCommandStatus.Completed
                    : IsolatedCommandStatus.Failed;

        return new IsolatedCommandResult(
            ExecutionDecision.Allow(),
            status,
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            result.Truncated,
            sw.Elapsed,
            evidence);
    }

    public async Task<IsolatedExportResult> ExportScopedChangesAsync(CancellationToken ct)
    {
        if (!_canExport || _snapshot is null)
        {
            return IsolatedExportResult.Refused(ExecutionDecision.Deny(
                ExecutionReasonCodes.InvalidOperation,
                "Export is not permitted for non-staged or pinned-commit sessions."));
        }

        using var tarStream = new MemoryStream();
        var build = LocalDirectoryTarBuilder.BuildTar(_workspaceDir, tarStream, _context.Limits, ct);
        if (!build.Success)
        {
            var first = build.Rejections[0];
            return new IsolatedExportResult(
                ExecutionDecision.Deny(first.ReasonCode, "export refused: " + first.Message),
                Array.Empty<ExportedChange>(),
                build.Rejections);
        }

        tarStream.Position = 0;
        var analyzerTemp = Path.Combine(_sessionDir, "export-stage");
        var analysis = GuestTarAnalyzer.Analyze(
            tarStream,
            _snapshot.Baseline,
            _context.Scope,
            _context.Limits,
            analyzerTemp,
            guestRoot: string.Empty);

        if (analysis.Rejections.Count > 0)
        {
            var first = analysis.Rejections[0];
            return new IsolatedExportResult(
                ExecutionDecision.Deny(first.ReasonCode, "export refused: " + first.Message),
                Array.Empty<ExportedChange>(),
                analysis.Rejections);
        }

        return await ScopedExportPublisher.PublishAsync(
            _context.WorkspaceRoot,
            analysis.Changes,
            _snapshot.Baseline,
            _observer,
            ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<EvidenceFile>> ReadEvidenceAsync(IReadOnlyList<string> globs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(globs);
        var results = new List<EvidenceFile>();
        long totalBytes = 0;

        if (!Directory.Exists(_workspaceDir))
        {
            return Task.FromResult<IReadOnlyList<EvidenceFile>>(results);
        }

        foreach (var file in new DirectoryInfo(_workspaceDir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var rel = Path.GetRelativePath(_workspaceDir, file.FullName).Replace('\\', '/');
            if (!WorkspacePath.TryNormalizeStrict(rel, out var norm, out _) || WorkspacePath.IsProtected(norm))
            {
                continue;
            }

            if (globs.Count > 0 && !globs.Any(g => MatchesSimpleGlob(norm, g)))
            {
                continue;
            }

            if (file.Length > GuestTarAnalyzer.MaxEvidenceFileBytes ||
                totalBytes + file.Length > GuestTarAnalyzer.MaxEvidenceTotalBytes ||
                results.Count >= GuestTarAnalyzer.MaxEvidenceFiles)
            {
                break;
            }

            var bytes = File.ReadAllBytes(file.FullName);
            totalBytes += bytes.Length;
            results.Add(new EvidenceFile(norm, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), bytes));
        }

        return Task.FromResult<IReadOnlyList<EvidenceFile>>(results);
    }

    public async Task AbortAsync()
    {
        await DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            _sessionCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _sessionCts.Dispose();
        _service.MarkSessionClosed(SessionHandle);
        LocalExecutionService.TryDeleteDirectory(_sessionDir);
        return ValueTask.CompletedTask;
    }

    private string? ResolveWorkingDirectory(string? requested)
    {
        var sessionFull = Path.GetFullPath(_sessionDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(requested))
        {
            return _workspaceDir;
        }

        var candidate = Path.IsPathRooted(requested)
            ? Path.GetFullPath(requested)
            : Path.GetFullPath(Path.Combine(_workspaceDir, requested));

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.Equals(_sessionDir, comparison) && !candidate.StartsWith(sessionFull, comparison))
        {
            return null;
        }

        return Directory.Exists(candidate) ? candidate : null;
    }

    private Dictionary<string, string?> BuildEnvironment(
        IReadOnlyDictionary<string, string>? overrides,
        IReadOnlyDictionary<string, string?>? trustedMinimalEnv)
    {
        var env = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (trustedMinimalEnv is not null)
        {
            foreach (var sysName in new[] { "SystemRoot", "WINDIR", "windir", "OS" })
            {
                var val = Environment.GetEnvironmentVariable(sysName);
                if (!string.IsNullOrEmpty(val))
                {
                    env[sysName] = val;
                }
            }

            env["HOME"] = _homeDir;
            env["USERPROFILE"] = _homeDir;
            env["TEMP"] = _tempDir;
            env["TMP"] = _tempDir;

            if (GuestGit is not null)
            {
                env["GIT_DIR"] = _gitDir;
                env["GIT_WORK_TREE"] = _workspaceDir;
            }

            foreach (var (k, v) in trustedMinimalEnv)
            {
                if (v is null)
                {
                    env.Remove(k);
                }
                else
                {
                    env[k] = v;
                }
            }

            return env;
        }

        foreach (var name in InheritedHostEnvVars)
        {
            var val = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(val))
            {
                env[name] = val;
            }
        }

        env["HOME"] = _homeDir;
        env["USERPROFILE"] = _homeDir;
        env["TEMP"] = _tempDir;
        env["TMP"] = _tempDir;

        // Preserve user package caches so isolated HOME does not force re-downloading dependencies.
        PreserveCacheIfUnset(env, "NUGET_PACKAGES", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"));
        PreserveCacheIfUnset(env, "npm_config_cache", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache"));
        PreserveCacheIfUnset(env, "PIP_CACHE_DIR", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pip", "Cache"));

        if (GuestGit is not null)
        {
            env["GIT_DIR"] = _gitDir;
            env["GIT_WORK_TREE"] = _workspaceDir;
        }

        if (overrides is not null)
        {
            foreach (var (k, v) in overrides)
            {
                if (StrippedEnvPrefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
                    k.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                env[k] = v;
            }
        }

        return env;
    }

    private static void PreserveCacheIfUnset(Dictionary<string, string?> env, string varName, string defaultPath)
    {
        if (!env.ContainsKey(varName) && !string.IsNullOrWhiteSpace(defaultPath) && Directory.Exists(defaultPath))
        {
            env[varName] = defaultPath;
        }
    }

    private static bool MatchesSimpleGlob(string path, string glob)
    {
        var g = glob.Replace('\\', '/').TrimStart('.', '/');
        if (g is "*" or "**" or "**/*")
        {
            return true;
        }

        if (g.StartsWith("*.", StringComparison.Ordinal))
        {
            return path.EndsWith(g[1..], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(path, g, StringComparison.OrdinalIgnoreCase);
    }
}
