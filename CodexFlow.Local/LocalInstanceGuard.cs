// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;

namespace CodexFlow.Local;

public sealed class LocalInstanceAlreadyRunningException : InvalidOperationException
{
    public LocalInstanceAlreadyRunningException(string dataDirectory, string? existingInstanceJson, Exception? inner = null)
        : base($"Another CodexFlow.Local instance is already using data directory '{dataDirectory}'.", inner)
    {
        DataDirectory = dataDirectory;
        ExistingInstanceJson = existingInstanceJson;
    }

    public string DataDirectory { get; }
    public string? ExistingInstanceJson { get; }
}

public sealed record LocalInstanceDiscoveryInfo(
    int Pid,
    int Port,
    string InstanceId,
    string DataDirectory,
    DateTimeOffset StartedAtUtc);

/// <summary>
/// Enforces single-instance mutual exclusion per <c>Local:DataDirectory</c> via an exclusive OS lock file
/// (<c>instance.lock</c>) and writes a token-free discovery file (<c>instance.json</c>) once the HTTP port is bound.
/// </summary>
public sealed class LocalInstanceGuard : IDisposable
{
    public const string LockFileName = "instance.lock";
    public const string DiscoveryFileName = "instance.json";

    private readonly FileStream _lockStream;
    private readonly string _discoveryFilePath;
    private bool _disposed;

    private LocalInstanceGuard(
        string dataDirectory,
        string instanceId,
        FileStream lockStream,
        string discoveryFilePath)
    {
        DataDirectory = dataDirectory;
        InstanceId = instanceId;
        _lockStream = lockStream;
        _discoveryFilePath = discoveryFilePath;
    }

    public string DataDirectory { get; }
    public string InstanceId { get; }
    public string DiscoveryFilePath => _discoveryFilePath;

    public static LocalInstanceGuard Acquire(string dataDirectory, string? instanceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fullDir = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(fullDir);
        LocalHostCredentials.ApplyCurrentUserDirectoryPermissions(fullDir);

        var lockPath = Path.Combine(fullDir, LockFileName);
        var discoveryPath = Path.Combine(fullDir, DiscoveryFileName);
        var id = string.IsNullOrWhiteSpace(instanceId) ? Guid.NewGuid().ToString("N") : instanceId;

        FileStream stream;
        try
        {
            stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            string? existing = null;
            try
            {
                if (File.Exists(discoveryPath))
                {
                    existing = File.ReadAllText(discoveryPath);
                }
            }
            catch
            {
                // Best effort diagnostic only.
            }

            throw new LocalInstanceAlreadyRunningException(fullDir, existing, ex);
        }

        try
        {
            stream.SetLength(0);
            var marker = System.Text.Encoding.UTF8.GetBytes($"{Environment.ProcessId}:{id}");
            stream.Write(marker, 0, marker.Length);
            stream.Flush();
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        return new LocalInstanceGuard(fullDir, id, stream, discoveryPath);
    }

    public LocalInstanceDiscoveryInfo WriteDiscovery(int port)
    {
        var info = new LocalInstanceDiscoveryInfo(
            Pid: Environment.ProcessId,
            Port: port,
            InstanceId: InstanceId,
            DataDirectory: DataDirectory,
            StartedAtUtc: DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(info, LocalHostJsonContext.Indented.LocalInstanceDiscoveryInfo);

        var tempPath = Path.Combine(DataDirectory, $".{DiscoveryFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _discoveryFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        return info;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (File.Exists(_discoveryFilePath))
            {
                File.Delete(_discoveryFilePath);
            }
        }
        catch
        {
            // Ignore cleanup errors during shutdown.
        }

        _lockStream.Dispose();
    }
}
