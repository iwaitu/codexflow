// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Configuration;

internal static class GlobalSettingsFileStore
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly ConcurrentDictionary<string, Lock> FileLocks = new(PathComparer);

    public static Lock GetLock(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return FileLocks.GetOrAdd(fullPath, static _ => new Lock());
    }

    public static T ReadLocked<T>(string path, Func<JObject, T> reader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(reader);

        lock (GetLock(path))
        {
            using var _ = AcquireCrossProcessLock(path);
            return reader(LoadRoot(path));
        }
    }

    public static T UpdateAtomically<T>(string path, Func<JObject, (bool Modified, T Result)> mutator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(mutator);

        lock (GetLock(path))
        {
            using var _ = AcquireCrossProcessLock(path);
            var root = LoadRoot(path);
            var (modified, result) = mutator(root);
            if (modified)
            {
                WriteAtomically(path, root.ToString(Newtonsoft.Json.Formatting.Indented));
            }

            return result;
        }
    }

    private static FileStream AcquireCrossProcessLock(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.lock");

        var deadline = Environment.TickCount64 + 15_000;
        var attempt = 0;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                attempt++;
                Thread.Sleep(Math.Min(50, 5 + (attempt * 3)));
            }
        }
    }

    public static JObject LoadRoot(string path)
    {
        if (!File.Exists(path))
        {
            return new JObject();
        }

        var text = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
    }

    public static void WriteAtomically(string path, string json)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, json);
            if (!OperatingSystem.IsWindows())
            {
                // Owner read/write only. On Windows the file inherits the user-profile ACL, which is already per user.
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
