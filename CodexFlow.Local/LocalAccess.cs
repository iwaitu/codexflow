// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.AccessControl;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CodexFlow.Local;

/// <summary>The one user of a local host.</summary>
public static class LocalUser
{
    /// <summary>Owner of every session, conversation and job in the local database.</summary>
    public const string Id = "local";
}

public enum LocalTokenDeliveryMode
{
    File = 0,
    Pipe = 1
}

/// <summary>
/// Dual-plane credentials for <c>CodexFlow.Local</c>:
/// - Normal plane (<c>host.token</c> or stdin pipe <c>accessToken</c>) grants regular API access (<c>ActiveUser</c>).
/// - Control plane (<c>control.token</c> or stdin pipe <c>controlToken</c>) is required to approve untrusted code,
///   approve/execute plans, relax permission modes, or manage persistent permission rules (<c>ControlPlane</c>).
/// </summary>
public sealed class LocalHostCredentials : IControlPlaneStateProvider
{
    public const string AccessTokenFileName = "host.token";
    public const string ControlTokenFileName = "control.token";

    private readonly byte[] _accessTokenBytes;
    private readonly string? _controlTokenFilePath;
    private readonly object _controlGate = new();
    private byte[]? _controlTokenBytes;
    private ExecutionBackend _activeBackend;

    private LocalHostCredentials(
        LocalTokenDeliveryMode deliveryMode,
        string accessToken,
        string? accessTokenPath,
        string? controlToken,
        string? controlTokenPath,
        ExecutionBackend initialBackend,
        bool unsafeFileControlToken)
    {
        DeliveryMode = deliveryMode;
        AccessToken = accessToken;
        AccessTokenPath = accessTokenPath;
        ControlToken = controlToken;
        _controlTokenFilePath = controlTokenPath;
        _accessTokenBytes = Encoding.UTF8.GetBytes(accessToken);
        _controlTokenBytes = string.IsNullOrEmpty(controlToken) ? null : Encoding.UTF8.GetBytes(controlToken);
        _activeBackend = initialBackend;
        UnsafeFileControlToken = unsafeFileControlToken;
    }

    public LocalTokenDeliveryMode DeliveryMode { get; }
    public string AccessToken { get; }
    public string? AccessTokenPath { get; }
    public string? ControlToken { get; private set; }
    public string? ControlTokenPath => _controlTokenFilePath;
    public bool UnsafeFileControlToken { get; }

    public string TokenDeliveryMode => DeliveryMode.ToString();

    public bool IsControlPlaneAvailable
    {
        get
        {
            lock (_controlGate)
            {
                if (DeliveryMode == LocalTokenDeliveryMode.Pipe)
                {
                    return _controlTokenBytes is not null;
                }

                if (_activeBackend == ExecutionBackend.Local && !UnsafeFileControlToken)
                {
                    return false;
                }

                return _controlTokenBytes is not null;
            }
        }
    }

    public bool SelfApprovalProtection
    {
        get
        {
            lock (_controlGate)
            {
                if (DeliveryMode == LocalTokenDeliveryMode.Pipe)
                {
                    return true;
                }

                // In File mode with Local backend and UnsafeFileControlToken=true, any local process running as the
                // same user can read control.token; self-approval protection does NOT hold.
                if (_activeBackend == ExecutionBackend.Local && UnsafeFileControlToken)
                {
                    return false;
                }

                return true;
            }
        }
    }

    /// <summary>
    /// Updates the tracked execution backend at runtime. If a File-delivery host switches from Docker to Local
    /// without <c>UnsafeFileControlToken</c>, any file-based control token is immediately revoked and deleted.
    /// </summary>
    public void UpdateActiveBackend(ExecutionBackend backend)
    {
        lock (_controlGate)
        {
            _activeBackend = backend;
            if (DeliveryMode == LocalTokenDeliveryMode.File &&
                backend == ExecutionBackend.Local &&
                !UnsafeFileControlToken)
            {
                _controlTokenBytes = null;
                ControlToken = null;
                TryDeleteFile(_controlTokenFilePath, logger: null);
            }
        }
    }

    public static bool ResolveUserLevelUnsafeFileControlToken(string globalSettingsPath, string[]? commandLineArgs = null)
    {
        // Only user-level ~/.codexflow/appsettings.json, explicit process environment variables, or CLI flags may enable this;
        // project-level codexflow.yml / workspace files can NEVER enable UnsafeFileControlToken.
        var envVal = Environment.GetEnvironmentVariable("Local__UnsafeFileControlToken");
        if (bool.TryParse(envVal, out var fromEnv) && fromEnv)
        {
            return true;
        }

        if (commandLineArgs is not null)
        {
            var cliConfig = new ConfigurationBuilder()
                .AddCommandLine(commandLineArgs)
                .Build();
            if (cliConfig.GetValue<bool>("Local:UnsafeFileControlToken"))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(globalSettingsPath) && File.Exists(globalSettingsPath))
        {
            var globalConfig = new ConfigurationBuilder()
                .AddJsonFile(globalSettingsPath, optional: true, reloadOnChange: false)
                .Build();
            return globalConfig.GetValue<bool>("Local:UnsafeFileControlToken");
        }

        return false;
    }

    public static LocalHostCredentials Initialize(
        string stateDirectory,
        LocalTokenDeliveryMode deliveryMode,
        ExecutionBackend initialBackend,
        bool unsafeFileControlToken,
        TextReader? pipeReader = null,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        var fullStateDir = Path.GetFullPath(stateDirectory);
        Directory.CreateDirectory(fullStateDir);
        ApplyCurrentUserDirectoryPermissions(fullStateDir);

        var accessFile = Path.Combine(fullStateDir, AccessTokenFileName);
        var controlFile = Path.Combine(fullStateDir, ControlTokenFileName);

        // Register credential directory and token files as protected host paths so workspace snapshots,
        // skill Inputs, exports, and managed read/write tools NEVER include or expose them (B2).
        WorkspacePath.RegisterProtectedHostPath(fullStateDir);
        WorkspacePath.RegisterProtectedHostPath(accessFile);
        WorkspacePath.RegisterProtectedHostPath(controlFile);

        if (deliveryMode == LocalTokenDeliveryMode.Pipe)
        {
            // Managed mode: read credentials from stdin pipe once; never write token files or environment variables.
            TryDeleteFile(accessFile, logger);
            TryDeleteFile(controlFile, logger);

            var reader = pipeReader ?? Console.In;
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidOperationException("Local:TokenDelivery=Pipe requires a JSON credential line on stdin.");
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var access = root.TryGetProperty("accessToken", out var ap) ? ap.GetString() : null;
            var control = root.TryGetProperty("controlToken", out var cp) ? cp.GetString() : null;
            if (string.IsNullOrWhiteSpace(access) || access.Length < 16 ||
                string.IsNullOrWhiteSpace(control) || control.Length < 16)
            {
                throw new InvalidOperationException("Pipe credential payload must provide non-empty 'accessToken' and 'controlToken' (min 16 chars each).");
            }

            if (string.Equals(access, control, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Normal accessToken and controlToken must be distinct secrets.");
            }

            return new LocalHostCredentials(
                LocalTokenDeliveryMode.Pipe,
                access,
                accessTokenPath: null,
                control,
                controlTokenPath: null,
                initialBackend,
                unsafeFileControlToken: false);
        }

        // Standalone File mode: write host.token with current-user-only ACL.
        var generatedAccess = GenerateRandomHexToken();
        WriteUserSecretFile(accessFile, generatedAccess);

        if (initialBackend == ExecutionBackend.Local && !unsafeFileControlToken)
        {
            TryDeleteFile(controlFile, logger);
            logger?.LogInformation(
                "Standalone Local execution mode: control.token was NOT generated (self-approval protection active; interactive Ask requests require a managed client or Docker backend).");
            return new LocalHostCredentials(
                LocalTokenDeliveryMode.File,
                generatedAccess,
                accessFile,
                controlToken: null,
                controlTokenPath: controlFile,
                initialBackend,
                unsafeFileControlToken: false);
        }

        var generatedControl = GenerateRandomHexToken();
        WriteUserSecretFile(controlFile, generatedControl);

        if (initialBackend == ExecutionBackend.Local && unsafeFileControlToken)
        {
            logger?.LogWarning(
                "UNSAFE COMPATIBILITY MODE ENABLED (Local:UnsafeFileControlToken=true): control.token written to {ControlTokenPath} while Execution:Backend=Local. Self-approval protection does NOT hold.",
                controlFile);
        }

        return new LocalHostCredentials(
            LocalTokenDeliveryMode.File,
            generatedAccess,
            accessFile,
            generatedControl,
            controlFile,
            initialBackend,
            unsafeFileControlToken);
    }

    public bool MatchesAccessToken(string? candidate)
        => !string.IsNullOrEmpty(candidate) &&
           FixedTimeMatches(candidate, _accessTokenBytes);

    public bool MatchesControlToken(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        lock (_controlGate)
        {
            if (DeliveryMode == LocalTokenDeliveryMode.File &&
                _activeBackend == ExecutionBackend.Local &&
                !UnsafeFileControlToken)
            {
                return false;
            }

            return _controlTokenBytes is not null && FixedTimeMatches(candidate, _controlTokenBytes);
        }
    }

    public static void ApplyCurrentUserDirectoryPermissions(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid is null)
            {
                return;
            }

            var dirInfo = new DirectoryInfo(directoryPath);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            dirInfo.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(
                directoryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static void WriteUserSecretFile(string filePath, string secret)
    {
        File.WriteAllText(filePath, secret);
        if (OperatingSystem.IsWindows())
        {
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid is null)
            {
                return;
            }

            var fileInfo = new FileInfo(filePath);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            fileInfo.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string GenerateRandomHexToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static bool FixedTimeMatches(string candidate, byte[] expected)
    {
        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        return candidateBytes.Length == expected.Length &&
               CryptographicOperations.FixedTimeEquals(candidateBytes, expected);
    }

    private static void TryDeleteFile(string? path, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Failed to delete stale credential file {TokenFilePath}; in-memory credential remains revoked.", path);
        }
    }
}

/// <summary>
/// Backwards-compatible wrapper over normal access token creation for existing callers.
/// </summary>
public sealed class LocalAccessToken
{
    public const string FileName = LocalHostCredentials.AccessTokenFileName;

    private readonly byte[] _valueBytes;

    public LocalAccessToken(string value, string path)
    {
        Value = value;
        Path = path;
        _valueBytes = Encoding.UTF8.GetBytes(value);
    }

    public string Value { get; }
    public string Path { get; }

    public static LocalAccessToken Create(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        LocalHostCredentials.ApplyCurrentUserDirectoryPermissions(stateDirectory);
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var path = System.IO.Path.Combine(stateDirectory, FileName);
        LocalHostCredentials.WriteUserSecretFile(path, value);
        return new LocalAccessToken(value, path);
    }

    public bool Matches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(candidate);
        return bytes.Length == _valueBytes.Length &&
               CryptographicOperations.FixedTimeEquals(bytes, _valueBytes);
    }
}

/// <summary>
/// Dual-plane Bearer authentication handler for <c>CodexFlow.Local</c>:
/// - Matching <see cref="LocalHostCredentials.AccessToken"/> issues <c>codexflow:plane = normal</c>.
/// - Matching <see cref="LocalHostCredentials.ControlToken"/> issues <c>codexflow:plane = control</c>.
/// </summary>
public sealed class LocalTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string Prefix = "Bearer ";
    private readonly LocalHostCredentials? _credentials;
    private readonly LocalAccessToken? _legacyToken;

    public LocalTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IServiceProvider serviceProvider) : base(options, logger, encoder)
    {
        _credentials = serviceProvider.GetService<LocalHostCredentials>();
        _legacyToken = serviceProvider.GetService<LocalAccessToken>();
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("Bearer scheme is required."));
        }

        var candidate = header[Prefix.Length..].Trim();
        string? plane = null;
        if (_credentials is not null)
        {
            if (_credentials.MatchesControlToken(candidate))
            {
                plane = ControlPlaneAuthConstants.PlaneControl;
            }
            else if (_credentials.MatchesAccessToken(candidate))
            {
                plane = ControlPlaneAuthConstants.PlaneNormal;
            }
        }
        else if (_legacyToken is not null && _legacyToken.Matches(candidate))
        {
            plane = ControlPlaneAuthConstants.PlaneNormal;
        }

        if (plane is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("The token is not valid for this host."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, LocalUser.Id),
                new Claim(ClaimTypes.Name, LocalUser.Id),
                new Claim(ControlPlaneAuthConstants.PlaneClaimType, plane)
            ],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
