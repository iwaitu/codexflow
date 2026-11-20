// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Security;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Browser.Cdp;

/// <summary>
/// Manages the shared headless Chromium-family process, its ephemeral runtime user-data-dir,
/// Windows Job Object cleanup guard, fallback strict outbound proxy, and CDP WebSocket connection.
/// </summary>
internal sealed partial class CdpManagedBrowserProcess : ICdpBrowserRuntime
{
    private const string EphemeralPrefix = "cf-browser-runtime-";
    private const string OwnerPidFileName = ".codexflow.pid";
    private const string PersistentLockFileName = ".codexflow.profile.lock";

    private readonly Process _process;
    private readonly ProcessJobGuard _jobGuard;
    private readonly BrowserOutboundProxy _fallbackStrictProxy;
    private readonly ProtectedLocalPortRegistry _portRegistry;
    private readonly string _userDataDir;
    private readonly bool _deleteUserDataDirOnDispose;
    private readonly FileStream? _profileLockStream;
    private readonly ILogger _logger;
    private int _disposed;
    private int _terminatedNotified;

    public event Action? Terminated;

    public CdpConnection Connection { get; }

    public int DebugPort { get; }

    public string ExecutablePath { get; }

    public bool IsRemote => false;

    public bool Headless { get; }

    public BrowserProfileMode ProfileMode { get; }

    public string RuntimeKey => string.Create(CultureInfo.InvariantCulture, $"local:{Headless}:{ProfileMode}:{ExecutablePath}");

    public bool IsAlive => _disposed == 0 && !_process.HasExited && Connection.IsConnected;

    private CdpManagedBrowserProcess(
        Process process,
        ProcessJobGuard jobGuard,
        BrowserOutboundProxy fallbackStrictProxy,
        ProtectedLocalPortRegistry portRegistry,
        CdpConnection connection,
        int debugPort,
        string executablePath,
        string userDataDir,
        bool deleteUserDataDirOnDispose,
        FileStream? profileLockStream,
        bool headless,
        BrowserProfileMode profileMode,
        ILogger logger)
    {
        _process = process;
        _jobGuard = jobGuard;
        _fallbackStrictProxy = fallbackStrictProxy;
        _portRegistry = portRegistry;
        Connection = connection;
        DebugPort = debugPort;
        ExecutablePath = executablePath;
        _userDataDir = userDataDir;
        _deleteUserDataDirOnDispose = deleteUserDataDirOnDispose;
        _profileLockStream = profileLockStream;
        Headless = headless;
        ProfileMode = profileMode;
        _logger = logger;

        _process.Exited += (_, _) => RaiseTerminated();
        Connection.Disconnected += _ => RaiseTerminated();
    }

    private void RaiseTerminated()
    {
        if (Interlocked.Exchange(ref _terminatedNotified, 1) != 0)
        {
            return;
        }

        var handler = Terminated;
        if (handler is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Unhandled exception in browser Terminated callback.");
                }
            });
        }
    }

    public static void ReapOrphanedRuntimeDirectories(string runtimeBaseDir, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(runtimeBaseDir) || !Directory.Exists(runtimeBaseDir))
        {
            return;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(runtimeBaseDir, EphemeralPrefix + "*"))
            {
                if (IsOwnedByLiveProcess(dir))
                {
                    continue;
                }

                TryDeleteDirectory(dir, logger);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to enumerate orphaned browser runtime directories under {Dir}", runtimeBaseDir);
        }
    }

    private static bool IsOwnedByLiveProcess(string dir)
    {
        var pidFile = Path.Combine(dir, OwnerPidFileName);
        if (!File.Exists(pidFile))
        {
            // Legacy/incomplete dir older than 5 minutes may be reaped; recent dir may still be initializing.
            return (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir)) < TimeSpan.FromMinutes(5);
        }

        try
        {
            var raw = File.ReadAllText(pidFile).Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ownerPid) || ownerPid <= 0)
            {
                return false;
            }

            if (ownerPid == Environment.ProcessId)
            {
                return true;
            }

            using var proc = Process.GetProcessById(ownerPid);
            return !proc.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static async Task<CdpManagedBrowserProcess> LaunchAsync(
        string executablePath,
        string runtimeBaseDir,
        BrowserTargetPolicyGuard policyGuard,
        ProtectedLocalPortRegistry portRegistry,
        ILogger logger,
        CancellationToken ct,
        bool headless = true,
        BrowserProfileMode profileMode = BrowserProfileMode.Ephemeral,
        string? persistentProfileDir = null,
        Func<SessionWorkMode>? processProxyWorkModeProvider = null,
        Func<IReadOnlySet<int>?>? processProxyApprovedPortsProvider = null)
    {
        var isPersistent = profileMode == BrowserProfileMode.Persistent;
        string userDataDir;
        FileStream? profileLockStream = null;

        if (isPersistent)
        {
            userDataDir = string.IsNullOrWhiteSpace(persistentProfileDir)
                ? Path.Combine(runtimeBaseDir, "persistent-default")
                : persistentProfileDir;
            Directory.CreateDirectory(userDataDir);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(
                        userDataDir,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch (Exception)
                {
                }
            }

            var lockPath = Path.Combine(userDataDir, PersistentLockFileName);
            try
            {
                profileLockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    "持久化浏览器 Profile 目录当前正被另一个 CodexFlow 实例独占使用，请先关闭另一个实例的浏览器或切换为会话隔离 Profile。",
                    ex);
            }
        }
        else
        {
            Directory.CreateDirectory(runtimeBaseDir);
            userDataDir = Path.Combine(runtimeBaseDir, EphemeralPrefix + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(userDataDir);
            await File.WriteAllTextAsync(
                Path.Combine(userDataDir, OwnerPidFileName),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                ct).ConfigureAwait(false);
        }

        // In Ephemeral mode, the process-wide fallback proxy runs under strict Daily mode with no approved loopback ports.
        // In Persistent mode, the default context uses the process-wide proxy bound to the single active persistent session.
        var fallbackProxy = new BrowserOutboundProxy(
            policyGuard,
            portRegistry,
            processProxyWorkModeProvider ?? (static () => SessionWorkMode.Daily),
            processProxyApprovedPortsProvider ?? (static () => null),
            logger);

        var jobGuard = new ProcessJobGuard();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = headless
        };

        if (headless)
        {
            startInfo.ArgumentList.Add("--headless=new");
            startInfo.ArgumentList.Add("--disable-gpu");
        }
        else
        {
            // Desktop off-screen headed launch for lossless human takeover (Browser.setWindowBounds brings it on-screen).
            startInfo.ArgumentList.Add("--window-position=-32000,-32000");
            startInfo.ArgumentList.Add("--disable-backgrounding-occluded-windows");
            startInfo.ArgumentList.Add("--disable-renderer-backgrounding");
            startInfo.ArgumentList.Add("--disable-background-timer-throttling");
        }

        startInfo.ArgumentList.Add("--remote-debugging-port=0");
        startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        startInfo.ArgumentList.Add($"--user-data-dir={userDataDir}");
        startInfo.ArgumentList.Add($"--proxy-server={fallbackProxy.ProxyServerUrl}");
        startInfo.ArgumentList.Add("--proxy-bypass-list=<-loopback>");
        startInfo.ArgumentList.Add("--disable-quic");
        startInfo.ArgumentList.Add("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--disable-background-networking");
        startInfo.ArgumentList.Add("--disable-sync");
        startInfo.ArgumentList.Add("--disable-extensions");
        startInfo.ArgumentList.Add("--disable-component-update");
        startInfo.ArgumentList.Add("--disable-features=MediaRouter,DialMediaRouteProvider,CalculateNativeWinOcclusion");
        startInfo.ArgumentList.Add("--password-store=basic");
        startInfo.ArgumentList.Add("--hide-crash-restore-bubble");
        startInfo.ArgumentList.Add("--mute-audio");
        startInfo.ArgumentList.Add("--window-size=1280,800");
        startInfo.ArgumentList.Add("about:blank");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        int debugPort = 0;

        try
        {
            var wsTcs = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderrTail = new StringBuilder();

            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is null)
                {
                    return;
                }

                lock (stderrTail)
                {
                    if (stderrTail.Length < 4096)
                    {
                        stderrTail.AppendLine(args.Data);
                    }
                }

                var match = DevToolsListeningRegex().Match(args.Data);
                if (match.Success && Uri.TryCreate(match.Groups[1].Value.Trim(), UriKind.Absolute, out var wsUri))
                {
                    wsTcs.TrySetResult(wsUri);
                }
            };

            process.Exited += (_, _) =>
            {
                string diag;
                lock (stderrTail) { diag = stderrTail.ToString().Trim(); }
                var exitCodeText = "unknown";
                try { exitCodeText = process.ExitCode.ToString(CultureInfo.InvariantCulture); } catch (Exception) { }
                wsTcs.TrySetException(new InvalidOperationException(
                    $"浏览器进程意外退出 (exitCode={exitCodeText})。{diag}"));
            };

            if (!process.Start())
            {
                throw new InvalidOperationException($"无法启动浏览器进程: {executablePath}");
            }

            jobGuard.TryAssign(process);
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(20));

            Uri wsEndpoint;
            try
            {
                wsEndpoint = await wsTcs.Task.WaitAsync(startupTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                string diag;
                lock (stderrTail) { diag = stderrTail.ToString().Trim(); }
                if (diag.Contains("RemoteDebuggingAllowed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("本机企业策略禁用了浏览器远程调试 (RemoteDebuggingAllowed=false)，无法启动受管浏览器。");
                }

                throw new TimeoutException($"等待浏览器 DevTools 端口就绪超时（20s）。{diag}");
            }

            debugPort = wsEndpoint.Port;
            portRegistry.RegisterPort(debugPort, "browser-cdp");

            var connection = await CdpConnection.ConnectAsync(wsEndpoint, logger, startupTimeout.Token).ConfigureAwait(false);
            logger.LogInformation(
                "Managed browser started via {Executable} on CDP port {Port} (headless={Headless}, profile={Profile})",
                executablePath,
                debugPort,
                headless,
                profileMode);

            return new CdpManagedBrowserProcess(
                process,
                jobGuard,
                fallbackProxy,
                portRegistry,
                connection,
                debugPort,
                executablePath,
                userDataDir,
                deleteUserDataDirOnDispose: !isPersistent,
                profileLockStream,
                headless,
                profileMode,
                logger);
        }
        catch
        {
            if (debugPort > 0)
            {
                portRegistry.UnregisterPort(debugPort);
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup.
            }

            process.Dispose();

            jobGuard.Dispose();
            await fallbackProxy.DisposeAsync().ConfigureAwait(false);
            profileLockStream?.Dispose();
            if (!isPersistent)
            {
                TryDeleteDirectory(userDataDir, logger);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _portRegistry.UnregisterPort(DebugPort);

        try
        {
            if (Connection.IsConnected)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Connection.SendAsync("Browser.close", ct: cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Ignore shutdown command errors.
        }

        await Connection.DisposeAsync().ConfigureAwait(false);

        try
        {
            if (!_process.HasExited)
            {
                if (!_process.WaitForExit(1500))
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(1500);
                }
            }
        }
        catch (Exception)
        {
            // Ignore process kill errors.
        }
        finally
        {
            _process.Dispose();
            _jobGuard.Dispose();
        }

        await _fallbackStrictProxy.DisposeAsync().ConfigureAwait(false);
        _profileLockStream?.Dispose();
        if (_deleteUserDataDirOnDispose)
        {
            TryDeleteDirectory(_userDataDir, _logger);
        }
    }

    private static void TryDeleteDirectory(string path, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2)
                {
                    logger.LogDebug(ex, "Could not delete temporary browser runtime dir {Dir}", path);
                    return;
                }

                Thread.Sleep(150);
            }
        }
    }

    [GeneratedRegex(@"DevTools listening on (ws://[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DevToolsListeningRegex();
}
