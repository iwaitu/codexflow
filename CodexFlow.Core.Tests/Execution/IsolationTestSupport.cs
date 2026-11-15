// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Formats.Tar;
using System.Security.Cryptography;
using CodexFlow.Core.Execution;

namespace CodexFlow.Core.Tests.Execution;

internal static class IsolationTestSupport
{
    public static string NewTempDir(string prefix = "cfiso")
    {
        var dir = Path.Combine(Path.GetTempPath(), "cfiso-tests", prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static string Sha(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static TrustedExecutionContext Context(string root, ExportScope? scope = null, ExecutionResourceLimits? limits = null, string? baseCommit = null)
        => new()
        {
            UserId = "u1",
            SessionId = "s1",
            JobId = "j1",
            TaskId = "t1",
            AttemptId = "j1:1",
            WorkspaceRoot = root,
            BaseCommit = baseCommit,
            Scope = scope ?? ExportScope.Empty,
            Limits = limits ?? new ExecutionResourceLimits { CommandTimeout = TimeSpan.FromMinutes(2) }
        };

    /// <summary>Builds a tar like `docker cp cid:/workspace -` would return.</summary>
    public static MemoryStream GuestTar(Action<TarBuilder> build)
    {
        var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
        {
            build(new TarBuilder(writer));
        }

        ms.Position = 0;
        return ms;
    }

    internal sealed class TarBuilder(TarWriter writer)
    {
        public TarBuilder File(string name, string content)
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)) });
            return this;
        }

        public TarBuilder Dir(string name)
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name));
            return this;
        }

        public TarBuilder Symlink(string name, string target)
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, name) { LinkName = target });
            return this;
        }

        public TarBuilder Hardlink(string name, string target)
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, name) { LinkName = target });
            return this;
        }

        public TarBuilder Fifo(string name)
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Fifo, name));
            return this;
        }
    }
}
