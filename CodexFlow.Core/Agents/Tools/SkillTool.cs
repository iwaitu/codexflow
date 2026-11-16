// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

public sealed class SkillTool(
    ILogger<SkillTool> logger,
    ISkillScriptRunner? scriptRunner = null,
    string? skillRoot = null,
    ToolIsolationGate? isolationGate = null) : ICodexTool
{
    // The host-process runner is accepted only for source compatibility and is never invoked.
    private readonly bool _hostRunnerPresent = scriptRunner is not null;
    private readonly ToolIsolationGate _gate = isolationGate ?? ToolIsolationGate.Unconfigured;

    public ToolCapability Capability => ToolCapability.ExecuteUntrustedCode;

    public string Name => "skill";

    public string Description => "统一管理本地 skills。参数: action(list/read/run_script), name?, script_path?, args?。list 列出 SKILL.md，read 读取指定 SKILL.md，run_script 运行 skill 目录内脚本。";

    public ToolCategory Category => ToolCategory.Read;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 16_384);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        var action = arguments.GetValueOrDefault("action")?.ToString() ?? "list";
        var root = ResolveSkillRoot(skillRoot);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return CodexToolResult.Error("Skills directory not found.");
        }

        return action.Trim().ToLowerInvariant() switch
        {
            "list" => ListSkills(root),
            "read" => await ReadSkillAsync(root, arguments, ct).ConfigureAwait(false),
            "run_script" => await RunScriptAsync(root, arguments, ct).ConfigureAwait(false),
            _ => CodexToolResult.Error("action must be list, read, or run_script.")
        };
    }

    private static CodexToolResult ListSkills(string root)
    {
        var skills = Directory
            .EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories)
            .Select(path => new
            {
                Name = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty,
                RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/')
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var output = skills.Length == 0
            ? "No skills found."
            : $"Found {skills.Length} skill(s):\n- " + string.Join("\n- ", skills.Select(item => $"{item.Name} ({item.RelativePath})"));

        return CodexToolResult.Succeeded(output, new { Root = root, Skills = skills }, summary: $"skills listed: {skills.Length}");
    }

    private static async Task<CodexToolResult> ReadSkillAsync(string root, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var name = NormalizeSkillName(arguments.GetValueOrDefault("name")?.ToString()
            ?? arguments.GetValueOrDefault("fileName")?.ToString());
        if (string.IsNullOrWhiteSpace(name))
        {
            return CodexToolResult.Error("Missing name.");
        }

        if (!TryResolveSkillFile(root, name, out var skillFile))
        {
            return CodexToolResult.Error($"Skill not found: {name}");
        }

        var content = await File.ReadAllTextAsync(skillFile, ct).ConfigureAwait(false);
        return CodexToolResult.Succeeded(
            content,
            new { Skill = name, Path = skillFile },
            summary: $"skill read: {name}");
    }

    /// <summary>
    /// run_script executes a skill script. It only ever runs inside the isolation backend: the skill directory is copied into
    /// the guest (/tmp/skill-inputs/&lt;name&gt;, no links, quotas, never exported) and the script runs as the unprivileged guest
    /// user with no network. The host-process ISkillScriptRunner is intentionally NOT used. Without a host-bound trusted
    /// context or backend the action is refused (no host fallback).
    /// </summary>
    private async Task<CodexToolResult> RunScriptAsync(string root, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var name = NormalizeSkillName(arguments.GetValueOrDefault("name")?.ToString());
        var scriptPath = arguments.GetValueOrDefault("script_path")?.ToString();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(scriptPath))
        {
            return CodexToolResult.Error("Missing name or script_path.");
        }

        if (!TryResolveSkillDirectory(root, name, out var skillDirectory))
        {
            return CodexToolResult.Error($"Skill not found: {name}");
        }

        var normalizedScriptPath = scriptPath.Trim().TrimStart('/', (char)92);
        var fullScriptPath = Path.GetFullPath(Path.Combine(skillDirectory, normalizedScriptPath));
        if (!ToolPathResolver.IsWithinRoot(fullScriptPath, skillDirectory) || !File.Exists(fullScriptPath))
        {
            return CodexToolResult.Error("script_path must point to an existing file under the skill directory.");
        }

        var guestSkillName = Path.GetFileName(skillDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!WorkspacePath.TryNormalizeStrict(normalizedScriptPath.Replace((char)92, '/'), out var guestScript, out var scriptReason))
        {
            return CodexToolResult.Error("script_path rejected: " + scriptReason);
        }

        var interpreter = Path.GetExtension(guestScript).ToLowerInvariant() switch
        {
            ".py" => ("python", "python3"),
            ".js" or ".mjs" => ("node", "node"),
            ".sh" => ("python", "sh"),
            _ => (string.Empty, string.Empty)
        };
        if (interpreter.Item1.Length == 0)
        {
            return CodexToolResult.Error("Unsupported script type (supported: .py, .js, .mjs, .sh).");
        }

        var refusal = _gate.TryAcquire(Name, ToolCapability.ExecuteUntrustedCode, out var context, out var backend);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!_gate.Options.LanguageImages.TryGetValue(interpreter.Item1, out var image))
        {
            return ToolIsolationGate.Refuse(Name, ExecutionDecision.Unavailable(ExecutionReasonCodes.UnsupportedIsolation, $"No isolation image is configured for '{interpreter.Item1}'."));
        }

        var args = ParseArgs(arguments.GetValueOrDefault("args"));
        var guestSubPath = "skill-inputs/" + guestSkillName;
        var open = await backend.OpenSessionAsync(
            context,
            new IsolatedSessionOptions(image, Inputs: [new IsolatedInputDirectory(skillDirectory, guestSubPath)]),
            ct).ConfigureAwait(false);
        if (!open.Decision.IsAllowed || open.Session is null)
        {
            return ToolIsolationGate.Refuse(Name, open.Decision);
        }

        StructuredLog.Information(logger, "skill run_script runs isolated: {Skill}/{Script} (host runner present={Present}, never used)", name, normalizedScriptPath, _hostRunnerPresent);
        await using var session = open.Session;
        var resolvedScriptPath = session.Layout.Backend == ExecutionBackend.Docker
            ? $"{session.Layout.InputsRoot.TrimEnd('/')}/{guestSubPath}/{guestScript}"
            : Path.Combine(
                session.Layout.InputsRoot,
                guestSubPath.Replace('/', Path.DirectorySeparatorChar),
                guestScript.Replace('/', Path.DirectorySeparatorChar));

        var resolvedInterpreter = interpreter.Item2;
        if (session.Layout.Backend == ExecutionBackend.Local && OperatingSystem.IsWindows())
        {
            if (resolvedInterpreter == "sh")
            {
                var bash = FindWindowsBash();
                if (bash is null)
                {
                    return CodexToolResult.Error("Cannot run .sh skill script on Windows: 'bash' (Git Bash or WSL) was not found.");
                }

                resolvedInterpreter = bash;
            }
            else if (resolvedInterpreter == "python3")
            {
                resolvedInterpreter = "python";
            }
        }

        var argv = new List<string> { resolvedInterpreter, resolvedScriptPath };
        argv.AddRange(args);
        var run = await session.RunAsync(new IsolatedCommandRequest(argv, Timeout: TimeSpan.FromMinutes(2)), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (run.Status is not (IsolatedCommandStatus.Completed or IsolatedCommandStatus.Failed))
        {
            return ToolIsolationGate.Refuse(Name, run.Decision);
        }

        if (run.ExitCode != 0)
        {
            return CodexToolResult.Error(string.Join((char)10, $"Skill script failed (exit {run.ExitCode}).", run.Stdout, run.Stderr).Trim());
        }

        return CodexToolResult.Succeeded(
            run.Stdout,
            new { Skill = name, ScriptPath = normalizedScriptPath, Args = args, Isolated = session.Layout.Backend == ExecutionBackend.Docker },
            summary: $"skill script ran: {name}/{normalizedScriptPath}");
    }

    private static string? FindWindowsBash()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = pathEnv };
            if (WindowsCommandResolver.FindExecutableOnPath("bash.exe", env) is { } onPath)
            {
                return onPath;
            }
        }

        var gitBash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        return File.Exists(gitBash) ? gitBash : null;
    }

    private static IReadOnlyList<string> ParseArgs(object? value)
    {
        if (value == null)
        {
            return [];
        }

        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                return jsonElement.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToArray();
            }

            if (jsonElement.ValueKind == JsonValueKind.String)
            {
                return ParseArgs(jsonElement.GetString());
            }
        }

        if (value is JArray jArray)
        {
            return jArray.Select(item => item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
        }

        if (value is IEnumerable<object> objects)
        {
            return objects.Select(item => item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
        }

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (text.TrimStart()[0] == '[')
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(text) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string NormalizeSkillName(string? name)
        => (name ?? string.Empty)
            .Replace("/SKILL.md", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("\\SKILL.md", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim()
            .Trim('/', '\\');

    private static bool TryResolveSkillFile(string root, string name, out string skillFile)
    {
        skillFile = string.Empty;
        if (!TryResolveSkillDirectory(root, name, out var skillDirectory))
        {
            return false;
        }

        var candidate = Path.Combine(skillDirectory, "SKILL.md");
        if (!File.Exists(candidate))
        {
            return false;
        }

        skillFile = candidate;
        return true;
    }

    private static bool TryResolveSkillDirectory(string root, string name, out string skillDirectory)
    {
        skillDirectory = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(root, name));
        if (!ToolPathResolver.IsWithinRoot(candidate, root) || !Directory.Exists(candidate))
        {
            return false;
        }

        skillDirectory = candidate;
        return true;
    }

    private static string? ResolveSkillRoot(string? configuredRoot)
    {
        foreach (var candidate in BuildSkillRootCandidates(configuredRoot))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string?> BuildSkillRootCandidates(string? configuredRoot)
    {
        yield return configuredRoot;
        yield return Environment.GetEnvironmentVariable("CODEX_SKILLS_DIR");
        yield return Path.Combine(AppContext.BaseDirectory, "skills");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "skills");

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            yield return Path.Combine(current.FullName, "CodexFlow", "skills");
            yield return Path.Combine(current.FullName, "skills");
            current = current.Parent;
        }
    }
}
