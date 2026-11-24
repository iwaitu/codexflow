// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>Test hook invoked right after pre-staging the skill directory and before waiting for interactive approval.</summary>
    internal Action<string>? OnPreStagedBeforeApproval { get; set; }

    /// <summary>Path to the most recently pre-staged script copy (for security test inspection).</summary>
    internal string? LastPreStagedScriptPath { get; private set; }

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

        return CodexToolResult.Succeeded(output, ToolResultMetadata.FromJson(new JsonObject { ["root"] = root, ["skills"] = new JsonArray(skills.Select(static skill => (JsonNode?)new JsonObject { ["name"] = skill.Name, ["relativePath"] = skill.RelativePath }).ToArray()) }), summary: $"skills listed: {skills.Length}");
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
            ToolResultMetadata.FromJson(new JsonObject { ["skill"] = name, ["path"] = skillFile }),
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
        var canonicalScriptRef = $"{guestSubPath}/{guestScript}";

        if (!TryBuildSkillArgvForBackend(backend.ActiveBackend, interpreter.Item2, canonicalScriptRef, args, out var initialSkillArgv))
        {
            return CodexToolResult.Error("Cannot run .sh skill script on Windows: 'bash' (Git Bash or WSL) was not found.");
        }

        // Pre-stage the skill directory into a private staging copy BEFORE requesting approval so that:
        // 1) No container or host process starts before approval;
        // 2) Modifying the source skill script while waiting for approval never executes unapproved code;
        // 3) Re-hashing both the entry script AND full Inputs manifest before and after session staging prevents TOCTOU tampering.
        var prestageRoot = Path.Combine(_gate.StagingRoot, "prestage", $"skill-{Guid.NewGuid():N}");
        try
        {
            LocalExecutionService.CopyInputDirectories(
                [new IsolatedInputDirectory(skillDirectory, guestSubPath)],
                prestageRoot,
                context.Limits);

            var prestageSkillDir = Path.Combine(prestageRoot, guestSubPath.Replace('/', Path.DirectorySeparatorChar));
            var prestageScriptFile = Path.Combine(prestageSkillDir, guestScript.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(prestageScriptFile))
            {
                return CodexToolResult.Error("script_path must point to an existing regular file under the skill directory.");
            }

            var expectedScriptSha256 = CodexFlow.Core.Execution.Approval.ExecutionRequest.Sha256File(prestageScriptFile);
            var expectedInputsManifestSha256 = CodexFlow.Core.Execution.Approval.ExecutionRequest.ComputeDirectoryManifestSha256(prestageSkillDir);

            LastPreStagedScriptPath = prestageScriptFile;
            OnPreStagedBeforeApproval?.Invoke(prestageScriptFile);

            bool VerifyPrestageCopy()
                => File.Exists(prestageScriptFile) &&
                   string.Equals(
                       CodexFlow.Core.Execution.Approval.ExecutionRequest.Sha256File(prestageScriptFile),
                       expectedScriptSha256,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       CodexFlow.Core.Execution.Approval.ExecutionRequest.ComputeDirectoryManifestSha256(prestageSkillDir),
                       expectedInputsManifestSha256,
                       StringComparison.Ordinal);

            (string? ScriptSha256, string? InputsManifestSha256) RefreshPrestageHashes()
            {
                expectedScriptSha256 = CodexFlow.Core.Execution.Approval.ExecutionRequest.Sha256File(prestageScriptFile);
                expectedInputsManifestSha256 = CodexFlow.Core.Execution.Approval.ExecutionRequest.ComputeDirectoryManifestSha256(prestageSkillDir);
                return (expectedScriptSha256, expectedInputsManifestSha256);
            }

            var (authRefusal, authorization) = await _gate.AuthorizeBeforeExecutionAsync(
                Name,
                context,
                backend,
                initialSkillArgv,
                scriptSha256: expectedScriptSha256,
                inputsManifestSha256: expectedInputsManifestSha256,
                verifyStagedArtifactsUnchanged: VerifyPrestageCopy,
                refreshArtifactHashes: RefreshPrestageHashes,
                resolveArgvForBackend: tb => TryBuildSkillArgvForBackend(tb, interpreter.Item2, canonicalScriptRef, args, out var rebuilt)
                    ? (rebuilt, null, null)
                    : null,
                ct: ct).ConfigureAwait(false);
            if (authRefusal is not null)
            {
                return authRefusal;
            }

            if (!VerifyPrestageCopy())
            {
                return ToolIsolationGate.Refuse(Name, ExecutionDecision.Deny(
                    ExecutionReasonCodes.ApprovalInvalidated,
                    "Pre-staged skill script or Inputs manifest changed prior to session open."));
            }

            var open = await backend.OpenPinnedBackendSessionAsync(
                authorization.ApprovedBackend,
                context,
                new IsolatedSessionOptions(
                    image,
                    Inputs:
                    [
                        new IsolatedInputDirectory(
                            prestageSkillDir,
                            guestSubPath,
                            ExpectedManifestSha256: expectedInputsManifestSha256,
                            ExpectedEntryRelativePath: guestScript,
                            ExpectedEntrySha256: expectedScriptSha256)
                    ]),
                ct).ConfigureAwait(false);
            if (!open.Decision.IsAllowed || open.Session is null)
            {
                return ToolIsolationGate.Refuse(Name, open.Decision);
            }

            StructuredLog.Information(logger, "skill run_script runs isolated: {Skill}/{Script} (host runner present={Present}, never used)", name, normalizedScriptPath, _hostRunnerPresent);
            await using var session = open.Session;
            if (session.Layout.Backend != authorization.ApprovedBackend)
            {
                return ToolIsolationGate.Refuse(Name, ExecutionDecision.Deny(
                    ExecutionReasonCodes.ApprovalInvalidated,
                    "Opened session backend did not match approved backend."));
            }

            var resolvedScriptPath = session.Layout.Backend == ExecutionBackend.Docker
                ? $"{session.Layout.InputsRoot.TrimEnd('/')}/{guestSubPath}/{guestScript}"
                : Path.Combine(
                    session.Layout.InputsRoot,
                    guestSubPath.Replace('/', Path.DirectorySeparatorChar),
                    guestScript.Replace('/', Path.DirectorySeparatorChar));

            // B7: Verify BOTH final entry script hash AND complete final Inputs directory manifest hash!
            if (session.Layout.Backend == ExecutionBackend.Local)
            {
                var sessionSkillDir = Path.Combine(session.Layout.InputsRoot, guestSubPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(resolvedScriptPath) ||
                    !string.Equals(
                        CodexFlow.Core.Execution.Approval.ExecutionRequest.Sha256File(resolvedScriptPath),
                        expectedScriptSha256,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        CodexFlow.Core.Execution.Approval.ExecutionRequest.ComputeDirectoryManifestSha256(sessionSkillDir),
                        expectedInputsManifestSha256,
                        StringComparison.Ordinal))
                {
                    return ToolIsolationGate.Refuse(Name, ExecutionDecision.Deny(
                        ExecutionReasonCodes.ApprovalInvalidated,
                        "Final session staged script or Inputs manifest hash differed from approved digest."));
                }
            }
            else if (!VerifyPrestageCopy())
            {
                return ToolIsolationGate.Refuse(Name, ExecutionDecision.Deny(
                    ExecutionReasonCodes.ApprovalInvalidated,
                    "Skill script or Inputs changed while copying into Docker container; refusing to execute."));
            }

            // B6: Consume frozen EffectiveExecutable + EffectiveArgv (replacing only canonicalScriptRef with resolvedScriptPath)
            var frozenArgv = new string[1 + authorization.EffectiveArgv.Count];
            frozenArgv[0] = authorization.EffectiveExecutable;
            for (var i = 0; i < authorization.EffectiveArgv.Count; i++)
            {
                frozenArgv[i + 1] = string.Equals(authorization.EffectiveArgv[i], canonicalScriptRef, StringComparison.Ordinal)
                    ? resolvedScriptPath
                    : authorization.EffectiveArgv[i];
            }

            await using var outputThrottler = new CodexFlow.Core.Execution.Approval.ToolOutputEventThrottler(
                _gate.Gateway,
                context.SessionId,
                Name,
                $"skill-{Guid.NewGuid():N}"[..13]);
            var run = await session.RunAsync(
                new IsolatedCommandRequest(
                    frozenArgv,
                    Timeout: TimeSpan.FromMinutes(2),
                    OnOutput: line => outputThrottler.Append(line.Stream, line.Text),
                    PreResolvedExecutable: authorization.IsPreResolvedExecutable),
                ct).ConfigureAwait(false);
            await outputThrottler.FlushAsync(ct).ConfigureAwait(false);
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
                ToolResultMetadata.FromJson(new JsonObject { ["skill"] = name, ["scriptPath"] = normalizedScriptPath, ["args"] = ToolResultMetadata.StringArray(args), ["isolated"] = session.Layout.Backend == ExecutionBackend.Docker }),
                summary: $"skill script ran: {name}/{normalizedScriptPath}");
        }
        catch (WorkspaceSnapshotException ex)
        {
            return ToolIsolationGate.Refuse(Name, ExecutionDecision.Deny(ex.ReasonCode, ex.Message));
        }
        finally
        {
            LocalExecutionService.TryDeleteDirectory(prestageRoot);
        }
    }

    private static bool TryBuildSkillArgvForBackend(
        ExecutionBackend targetBackend,
        string baseInterpreter,
        string canonicalScriptRef,
        IReadOnlyList<string> args,
        out List<string> builtArgv)
    {
        var interp = baseInterpreter;
        if (targetBackend == ExecutionBackend.Local && OperatingSystem.IsWindows())
        {
            if (interp == "sh")
            {
                var bash = FindWindowsBash();
                if (bash is null)
                {
                    builtArgv = [];
                    return false;
                }

                interp = bash;
            }
            else if (interp == "python3")
            {
                interp = "python";
            }
        }

        builtArgv = new List<string>(2 + args.Count) { interp, canonicalScriptRef };
        builtArgv.AddRange(args);
        return true;
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
                return JsonSerializer.Deserialize(text, AutomationToolArgumentJsonContext.Default.StringArray) ?? [];
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
