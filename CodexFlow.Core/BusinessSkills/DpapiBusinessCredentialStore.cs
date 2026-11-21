// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.BusinessSkills;

/// <summary>
/// 本机企业插件凭据加密存储：
/// Windows 使用当前用户 DPAPI (DataProtectionScope.CurrentUser) + 当前用户独享目录/文件 ACL；
/// 其他平台若未接入原生安全存储实现，则 fail-closed 拒绝持久化，绝不回退明文文件。
/// </summary>
public sealed class DpapiBusinessCredentialStore : IBusinessCredentialStore
{
    private readonly string _directory;

    public DpapiBusinessCredentialStore(IOptions<BusinessSkillsOptions>? options = null, IConfiguration? configuration = null)
        : this(ResolveDirectory(options?.Value, configuration))
    {
    }

    public DpapiBusinessCredentialStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public bool IsPersistentProtectionAvailable => OperatingSystem.IsWindows();

    public async Task<StoredBusinessCredentialResult> StoreSecretAsync(
        string credentialRef,
        BusinessAuthKind authKind,
        string secretJson,
        string revision,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialRef);
        ArgumentNullException.ThrowIfNull(secretJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Persistent business credential protection is not implemented on this operating system; plaintext storage is forbidden.");
        }

        EnsureProtectedDirectory();
        // A candidate revision must not overwrite the cipher referenced by the active database revision.
        var fileName = BuildSafeCipherFileName(credentialRef, revision);
        var filePath = ResolveSafePath(fileName);

        var plainBytes = Encoding.UTF8.GetBytes(secretJson);
        var entropy = BuildEntropy(credentialRef);
        var cipherBytes = ProtectWindows(plainBytes, entropy);

        var tempPath = filePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, cipherBytes, ct).ConfigureAwait(false);
        ApplyCurrentUserFilePermissions(tempPath);
        File.Move(tempPath, filePath, overwrite: true);
        ApplyCurrentUserFilePermissions(filePath);

        var summary = new CredentialRefSummaryDto
        {
            CredentialRef = credentialRef,
            AuthKind = authKind,
            MaskedHint = BuildMaskedHint(authKind, secretJson),
            Revision = revision,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        return new StoredBusinessCredentialResult(summary, fileName);
    }

    public async Task<string?> ReadSecretAsync(
        string credentialRef,
        string cipherFileName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(cipherFileName);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Persistent business credential protection is not implemented on this operating system.");
        }

        var filePath = ResolveSafePath(cipherFileName);
        if (!File.Exists(filePath))
        {
            return null;
        }

        var cipherBytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        var plainBytes = UnprotectWindows(cipherBytes, BuildEntropy(credentialRef));
        return Encoding.UTF8.GetString(plainBytes);
    }

    public Task DeleteSecretAsync(
        string credentialRef,
        string cipherFileName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(cipherFileName);
        ct.ThrowIfCancellationRequested();

        var filePath = ResolveSafePath(cipherFileName);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return Task.CompletedTask;
    }

    public string BuildMaskedHint(BusinessAuthKind authKind, string secretJson)
    {
        if (authKind == BusinessAuthKind.None || string.IsNullOrWhiteSpace(secretJson))
        {
            return "(none)";
        }

        try
        {
            using var doc = JsonDocument.Parse(secretJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("username", out var userProp) && userProp.ValueKind == JsonValueKind.String)
                {
                    var u = userProp.GetString() ?? string.Empty;
                    return $"user:{MaskTail(u)}";
                }

                if (doc.RootElement.TryGetProperty("headerName", out var headerProp) && headerProp.ValueKind == JsonValueKind.String)
                {
                    var key = doc.RootElement.TryGetProperty("apiKey", out var k) && k.ValueKind == JsonValueKind.String
                        ? k.GetString() ?? string.Empty
                        : string.Empty;
                    return $"{headerProp.GetString()}:{MaskTail(key)}";
                }

                if (doc.RootElement.TryGetProperty("token", out var tokenProp) && tokenProp.ValueKind == JsonValueKind.String)
                {
                    return $"token:{MaskTail(tokenProp.GetString() ?? string.Empty)}";
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 结构时按纯文本尾部脱敏处理
        }

        return MaskTail(secretJson);
    }

    private static string ResolveDirectory(BusinessSkillsOptions? options, IConfiguration? configuration)
    {
        if (!string.IsNullOrWhiteSpace(options?.CredentialStoreDirectory))
        {
            return options.CredentialStoreDirectory;
        }

        var credDir = configuration?["Local:CredentialDirectory"];
        if (!string.IsNullOrWhiteSpace(credDir))
        {
            return Path.Combine(credDir, "business-plugins");
        }

        var dataDir = configuration?["Local:DataDirectory"];
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            return Path.Combine(dataDir, "credentials", "business-plugins");
        }

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrWhiteSpace(localApp) ? Path.GetTempPath() : localApp, "CodexFlow", "credentials", "business-plugins");
    }

    private string ResolveSafePath(string fileName)
    {
        if (Path.IsPathRooted(fileName) ||
            fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar) ||
            fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid credential cipher file name.", nameof(fileName));
        }

        var full = Path.GetFullPath(Path.Combine(_directory, fileName));
        var normalizedRoot = _directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(normalizedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved credential file escapes directory boundary.");
        }

        return full;
    }

    private static string BuildSafeCipherFileName(string credentialRef, string revision)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{credentialRef}\0{revision}"));
        return Convert.ToHexString(hash).ToLowerInvariant() + ".dpapi";
    }

    private static byte[] BuildEntropy(string credentialRef)
        => Encoding.UTF8.GetBytes($"codexflow:business-credential:v1:{credentialRef}");

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] plainBytes, byte[] entropy)
        => ProtectedData.Protect(plainBytes, entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] cipherBytes, byte[] entropy)
        => ProtectedData.Unprotect(cipherBytes, entropy, DataProtectionScope.CurrentUser);

    private void EnsureProtectedDirectory()
    {
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ApplyCurrentUserDirectoryPermissions(_directory);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyCurrentUserDirectoryPermissions(string directory)
    {
        var sid = WindowsIdentity.GetCurrent().User;
        if (sid is null)
        {
            return;
        }

        var info = new DirectoryInfo(directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyCurrentUserFilePermissions(string filePath)
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

    private static string MaskTail(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "****";
        }

        if (trimmed.Length <= 4)
        {
            return "****";
        }

        return "****" + trimmed[^4..];
    }
}
