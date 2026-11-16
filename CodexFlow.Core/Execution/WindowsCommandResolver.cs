// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Execution;

public sealed record ResolvedHostCommand(
    bool Allowed,
    string Executable,
    IReadOnlyList<string> Args,
    IReadOnlyList<string> InterpreterChain,
    string? RejectionReason = null)
{
    public string FormatAuditCommand()
    {
        var all = new List<string>(1 + Args.Count) { Executable };
        all.AddRange(Args);
        return string.Join(' ', all.Select(static a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));
    }
}

/// <summary>
/// Windows launch policy for <see cref="LocalExecutionService"/>:
/// - Native <c>.exe</c> binaries are launched directly via argv (<c>UseShellExecute=false</c>).
/// - <c>.cmd</c>/<c>.bat</c> shims are NEVER passed to <c>cmd /c</c>; known shims (<c>npm</c>, <c>npx</c>)
///   are translated via an explicit interpreter adapter table to <c>node.exe</c> + CLI script.
/// - Any unadapted <c>.cmd</c>/<c>.bat</c> is refused with an explicit reason.
/// </summary>
public static class WindowsCommandResolver
{
    private static readonly Dictionary<string, string> NodeCliAdapters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["npm"] = Path.Combine("node_modules", "npm", "bin", "npm-cli.js"),
        ["npx"] = Path.Combine("node_modules", "npm", "bin", "npx-cli.js")
    };

    public static ResolvedHostCommand Resolve(
        string command,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(env);

        if (string.IsNullOrWhiteSpace(command))
        {
            return new ResolvedHostCommand(false, string.Empty, args, [], "Command executable must not be empty.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return new ResolvedHostCommand(true, command, args, [command]);
        }

        var resolved = FindExecutableOnPath(command, env);
        if (resolved is null)
        {
            return new ResolvedHostCommand(
                false,
                command,
                args,
                [],
                $"Executable '{command}' was not found on PATH.");
        }

        var ext = Path.GetExtension(resolved);
        if (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedHostCommand(true, resolved, args, [resolved]);
        }

        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(resolved);
            if (!NodeCliAdapters.TryGetValue(stem, out var relativeJs))
            {
                return new ResolvedHostCommand(
                    false,
                    resolved,
                    args,
                    [],
                    $"Refusing to launch '{Path.GetFileName(resolved)}' via cmd.exe: '{stem}' is not in the Windows interpreter adapter table.");
            }

            return AdaptNodeCliShim(resolved, stem, relativeJs, args, env);
        }

        return new ResolvedHostCommand(
            false,
            resolved,
            args,
            [],
            $"Unsupported Windows executable extension '{ext}' for '{resolved}'.");
    }

    private static ResolvedHostCommand AdaptNodeCliShim(
        string shimPath,
        string stem,
        string relativeJs,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> env)
    {
        var shimDir = Path.GetDirectoryName(shimPath)!;
        var candidateScript = Path.Combine(shimDir, relativeJs);
        if (!File.Exists(candidateScript))
        {
            return new ResolvedHostCommand(
                false,
                shimPath,
                args,
                [],
                $"Adapter for '{stem}' expected CLI script at '{candidateScript}', which does not exist.");
        }

        var siblingNode = Path.Combine(shimDir, "node.exe");
        var nodeExe = File.Exists(siblingNode)
            ? siblingNode
            : FindExecutableOnPath("node.exe", env);
        if (nodeExe is null || !nodeExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedHostCommand(
                false,
                shimPath,
                args,
                [],
                $"Adapter for '{stem}' requires 'node.exe', which was not found.");
        }

        var adaptedArgs = new List<string>(1 + args.Count) { candidateScript };
        adaptedArgs.AddRange(args);
        return new ResolvedHostCommand(true, nodeExe, adaptedArgs, [shimPath, nodeExe, candidateScript]);
    }

    internal static string? FindExecutableOnPath(string command, IReadOnlyDictionary<string, string?> env)
    {
        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            var full = Path.GetFullPath(command);
            if (File.Exists(full))
            {
                return full;
            }

            foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
            {
                if (File.Exists(full + ext))
                {
                    return full + ext;
                }
            }

            return null;
        }

        var pathValue = env.TryGetValue("PATH", out var p1) ? p1 : (env.TryGetValue("Path", out var p2) ? p2 : null);
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var hasExt = Path.HasExtension(command);
        // Prefer .exe over .cmd/.bat when command has no extension.
        var extensions = hasExt ? [""] : new[] { ".exe", ".cmd", ".bat" };

        foreach (var dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleanDir = dir.Trim('"');
            if (string.IsNullOrWhiteSpace(cleanDir))
            {
                continue;
            }

            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(cleanDir, command + ext);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }
}
