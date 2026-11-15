// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Agents;

/// <summary>One thing the scope guard refused: a write to <see cref="Path"/>, or a project-structure <see cref="Command"/>.</summary>
public sealed record ScopeDenial(string Tool, string? Path, string? Command, string Reason);

/// <summary>
/// Pre-execution scope guard: blocks write/edit tool calls targeting files
/// outside the current task's file scope before they reach the filesystem.
/// </summary>
public sealed class TaskScopeInterventionHook : IQueryRuntimeInterventionHook
{
    private readonly TaskFileScopeDescriptor _scope;
    private readonly ILogger _logger;
    private int _blockCount;
    private readonly List<ScopeDenial> _denials = new();
    private readonly object _denialLock = new();

    /// <summary>The most distinct refusals kept; further ones are counted in <see cref="BlockCount"/> only.</summary>
    public const int MaxRecordedDenials = 20;

    private static readonly HashSet<string> ScopeCheckedToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file",
        "edit_file",
        "delete_file",
        "ivilson_smart_patch",
        "apply_patch",
        "hs_write",
        "ApplyPatchTool",
        "create_directory",
        "run_command",
        "exec_cmd",
        "notebook_edit"
    };

    private static readonly HashSet<string> IsolatedExecutionToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "run_command",
        "exec_cmd",
        "exec_code"
    };

    private static readonly string[] PathArgumentKeys = ["path", "file_path", "filePath", "filepath", "target_file", "target_path", "notebook_path"];
    private static readonly string[] PatchArgumentKeys = ["patch", "input", "content", "request"];
    private static readonly Regex PatchTargetLineRegex = new(
        @"^\*\*\* (?:(?<op>Add|Update|Delete) File|Move to): (?<path>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private readonly bool _enforceCapabilities;
    private readonly bool _requireScopeForWrites;

    /// <param name="enforceCapabilities">Deny tools without a trusted capability declaration.</param>
    /// <param name="requireScopeForWrites">
    /// Default true: a workspace-writing tool call is denied when the task has no file scope, when its target path cannot
    /// be determined, or when the path shape is unsafe. Execution tools (exec_cmd/run_command/exec_code) are exempt
    /// because they run in the isolation backend whose export gate enforces the scope.
    /// </param>
    public TaskScopeInterventionHook(TaskFileScopeDescriptor scope, ILogger logger, bool enforceCapabilities = true, bool requireScopeForWrites = true)
    {
        _enforceCapabilities = enforceCapabilities;
        _requireScopeForWrites = requireScopeForWrites;
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Number of tool calls blocked by this hook since construction.</summary>
    public int BlockCount => _blockCount;

    /// <summary>
    /// What this hook refused, once per tool and target, in the order it happened. The worker is told at the time; this is how the host can say
    /// afterwards which files a task needed and was not allowed to touch.
    /// </summary>
    public IReadOnlyList<ScopeDenial> Denials
    {
        get
        {
            lock (_denialLock)
            {
                return _denials.ToArray();
            }
        }
    }

    private void Record(string tool, string? path, string? command, string reason)
    {
        lock (_denialLock)
        {
            if (_denials.Count >= MaxRecordedDenials ||
                _denials.Any(d => string.Equals(d.Tool, tool, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(d.Command, command, StringComparison.Ordinal)))
            {
                return;
            }

            _denials.Add(new ScopeDenial(tool, path, command, reason.Length <= 300 ? reason : reason[..300]));
        }
    }

    public ValueTask<QueryRuntimeIntervention> OnToolCallRequestedAsync(
        string toolName,
        IDictionary<string, object?> arguments,
        object? session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(arguments);

        // 0. Unknown capability => deny, independent of scope constraints (default deny for undeclared/new tools).
        if (_enforceCapabilities && ToolCapabilityCatalog.Resolve(toolName) == ToolCapability.Unknown)
        {
            _blockCount++;
            var denied = ToolCapabilityCatalog.Evaluate(toolName);
            _logger.LogWarning("Capability guard blocked undeclared tool {ToolName}", toolName);
            return ValueTask.FromResult(QueryRuntimeIntervention.BlockWithMessage(
                new ChatMessage(ChatRole.User, $"Tool `{toolName}` has no trusted capability declaration and is denied. [{denied.ReasonCode}]"),
                $"{denied.ReasonCode}: tool `{toolName}` is not declared."));
        }

        // 1. Only check scope for write/edit tools; pass read-only tools through.
        if (!ScopeCheckedToolNames.Contains(toolName))
        {
            return ValueTask.FromResult(QueryRuntimeIntervention.None);
        }

        var isIsolatedExecution = IsolatedExecutionToolNames.Contains(toolName);

        // 2. A write tool without any task scope is denied (default deny); isolated execution is checked by the export gate.
        if (!_scope.HasConstraints)
        {
            if (_requireScopeForWrites && !isIsolatedExecution)
            {
                return ValueTask.FromResult(BlockWrite(toolName, "ScopeUnavailable", "the task has no file scope, so workspace writes are not authorized"));
            }

            return ValueTask.FromResult(QueryRuntimeIntervention.None);
        }

        if ((toolName.Equals("run_command", StringComparison.OrdinalIgnoreCase) || toolName.Equals("exec_cmd", StringComparison.OrdinalIgnoreCase)) &&
            TryBuildBlockedCommandIntervention(toolName, arguments, out var commandIntervention))
        {
            return ValueTask.FromResult(commandIntervention);
        }

        if (isIsolatedExecution)
        {
            return ValueTask.FromResult(QueryRuntimeIntervention.None);
        }

        // 3. Extract target file paths from tool arguments. apply_patch-style tools
        // can carry the path only inside the patch body, so inspect those too.
        var targetPaths = ExtractPathTargetsFromArguments(arguments).ToArray();
        if (targetPaths.Length == 0)
        {
            if (_requireScopeForWrites)
            {
                return ValueTask.FromResult(BlockWrite(toolName, "PathUnresolved", "no target path could be determined from the arguments"));
            }

            _logger.LogDebug("Scope check skipped for tool {ToolName}: could not extract target path from arguments", toolName);
            return ValueTask.FromResult(QueryRuntimeIntervention.None);
        }

        if (_requireScopeForWrites)
        {
            foreach (var t in targetPaths)
            {
                if (TryFindUnsafeShape(t.Path, out var shapeReason))
                {
                    return ValueTask.FromResult(BlockWrite(toolName, "UnsafePath", $"`{t.Path}`: {shapeReason}"));
                }
            }
        }

        // write_file and create_directory can create new files; apply_patch Add File
        // targets are marked individually by the patch parser.
        var defaultIsNewFile = toolName is "write_file" or "create_directory";
        foreach (var target in targetPaths)
        {
            var normalized = TaskFileScopeGuard.NormalizePathLike(target.Path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (TaskFileScopeGuard.IsPathInScope(normalized, _scope, target.IsNewFile || defaultIsNewFile))
            {
                continue;
            }

            // 5. Block the call with a clear reason message.
            _blockCount++;
            var reason = BuildBlockReason(normalized, toolName);
            Record(toolName, normalized, null, "outside allowed scope");

            _logger.LogWarning(
                "Scope guard blocked tool {ToolName} targeting out-of-scope file {Path}. Block count: {BlockCount}",
                toolName, normalized, _blockCount);

            var feedbackMessage = new ChatMessage(
                ChatRole.User,
                $"Task scope violation: file `{normalized}` is not in the allowed scope for this task.\n\n" +
                $"Allowed files: {string.Join(", ", _scope.AllowedFiles.Take(12))}" +
                (_scope.AllowedFiles.Count > 12 ? " ..." : "") + "\n" +
                (_scope.AllowedFileNames.Count > 0
                    ? $"Allowed named source files: {string.Join(", ", _scope.AllowedFileNames.Take(12))}" +
                      (_scope.AllowedFileNames.Count > 12 ? " ..." : "") + "\n"
                    : "") +
                (_scope.AllowedDirectories.Count > 0
                    ? $"Allowed directories for new files: {string.Join(", ", _scope.AllowedDirectories.Take(8))}\n"
                    : "") +
                (_scope.DisallowedDirectories.Count > 0
                    ? $"Disallowed directories: {string.Join(", ", _scope.DisallowedDirectories.Take(8))}\n"
                    : "") +
                "\nPlease stay within the defined scope. If you genuinely need to modify a file outside scope, " +
                "explain the reason in your response and stop attempting modifications beyond the allowed files.");

            return ValueTask.FromResult(QueryRuntimeIntervention.BlockWithMessage(feedbackMessage, reason));
        }

        return ValueTask.FromResult(QueryRuntimeIntervention.None);
    }

    public ValueTask<QueryRuntimeIntervention> OnToolExecutionCompletedAsync(
        string toolName,
        string result,
        bool success,
        object? session,
        CancellationToken ct = default)
    {
        // Scope guard only acts pre-execution.
        return ValueTask.FromResult(QueryRuntimeIntervention.None);
    }

    private static IEnumerable<PathTarget> ExtractPathTargetsFromArguments(IDictionary<string, object?> arguments)
    {
        foreach (var key in PathArgumentKeys)
        {
            if (arguments.TryGetValue(key, out var value) &&
                TryConvertPathValue(value, out var pathStr))
            {
                yield return new PathTarget(pathStr.TrimStart('/', '\\'), IsNewFile: false);
            }
        }

        foreach (var key in PatchArgumentKeys)
        {
            if (!arguments.TryGetValue(key, out var value))
            {
                continue;
            }

            foreach (var target in ExtractPatchTargets(value))
            {
                yield return target;
            }
        }

        // Special handling for tools which nest path or patch inside a "request" object.
        if (arguments.TryGetValue("request", out var requestObj))
        {
            if (requestObj is IDictionary<string, object?> requestDict)
            {
                foreach (var key in PathArgumentKeys)
                {
                    if (requestDict.TryGetValue(key, out var nestedValue) &&
                        TryConvertPathValue(nestedValue, out var nestedPath))
                    {
                        yield return new PathTarget(nestedPath.TrimStart('/', '\\'), IsNewFile: false);
                    }
                }

                foreach (var key in PatchArgumentKeys)
                {
                    if (requestDict.TryGetValue(key, out var nestedValue))
                    {
                        foreach (var target in ExtractPatchTargets(nestedValue))
                        {
                            yield return target;
                        }
                    }
                }
            }

            // request might be a JSON string; try to extract path or patch from it.
            if (requestObj is string requestJson && !string.IsNullOrWhiteSpace(requestJson))
            {
                List<PathTarget> requestTargets = [];
                try
                {
                    var jsonObj = Newtonsoft.Json.Linq.JObject.Parse(requestJson);
                    foreach (var key in PathArgumentKeys)
                    {
                        var token = jsonObj[key];
                        if (token != null && token.Type == Newtonsoft.Json.Linq.JTokenType.String)
                        {
                            var extracted = token.ToString();
                            if (!string.IsNullOrWhiteSpace(extracted))
                            {
                                requestTargets.Add(new PathTarget(extracted.TrimStart('/', '\\'), IsNewFile: false));
                            }
                        }
                    }

                    foreach (var key in PatchArgumentKeys)
                    {
                        var token = jsonObj[key];
                        if (token != null)
                        {
                            foreach (var target in ExtractPatchTargets(token))
                            {
                                requestTargets.Add(target);
                            }
                        }
                    }
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    // Not valid JSON; skip
                }

                foreach (var target in requestTargets)
                {
                    yield return target;
                }
            }
        }
    }

    private static IEnumerable<PathTarget> ExtractPatchTargets(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case string text:
                foreach (var target in ExtractPatchTargetsFromText(text))
                {
                    yield return target;
                }

                yield break;
            case JsonElement { ValueKind: JsonValueKind.String } jsonString:
                foreach (var target in ExtractPatchTargetsFromText(jsonString.GetString() ?? string.Empty))
                {
                    yield return target;
                }

                yield break;
            case JsonElement { ValueKind: JsonValueKind.Object } jsonObject:
                foreach (var property in jsonObject.EnumerateObject())
                {
                    if (!PatchArgumentKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var target in ExtractPatchTargets(property.Value))
                    {
                        yield return target;
                    }
                }

                yield break;
            case Newtonsoft.Json.Linq.JValue jValue:
                foreach (var target in ExtractPatchTargetsFromText(jValue.Value?.ToString() ?? string.Empty))
                {
                    yield return target;
                }

                yield break;
            case Newtonsoft.Json.Linq.JObject jObject:
                foreach (var key in PatchArgumentKeys)
                {
                    var token = jObject[key];
                    if (token == null)
                    {
                        continue;
                    }

                    foreach (var target in ExtractPatchTargets(token))
                    {
                        yield return target;
                    }
                }

                yield break;
            case Newtonsoft.Json.Linq.JToken jToken:
                foreach (var target in ExtractPatchTargetsFromText(jToken.ToString()))
                {
                    yield return target;
                }

                yield break;
            default:
                foreach (var target in ExtractPatchTargetsFromText(value.ToString() ?? string.Empty))
                {
                    yield return target;
                }

                yield break;
        }
    }

    private static IEnumerable<PathTarget> ExtractPatchTargetsFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in PatchTargetLineRegex.Matches(text))
        {
            var path = match.Groups["path"].Value.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var op = match.Groups["op"].Value;
            yield return new PathTarget(path.TrimStart('/', '\\'), IsNewFile: string.Equals(op, "Add", StringComparison.OrdinalIgnoreCase));
        }
    }

    private bool TryBuildBlockedCommandIntervention(
        string commandToolName,
        IDictionary<string, object?> arguments,
        out QueryRuntimeIntervention intervention)
    {
        intervention = QueryRuntimeIntervention.None;

        if (!arguments.TryGetValue("command", out var commandValue) ||
            !TryConvertCommandValue(commandValue, out var command))
        {
            return false;
        }

        if (!TryGetBlockedCommandReason(command, out var policyReason))
        {
            return false;
        }

        _blockCount++;
        Record(commandToolName, null, command.Length <= 200 ? command : command[..200], policyReason);
        _logger.LogWarning(
            "Scope guard blocked command {Command}. Reason: {Reason}. Block count: {BlockCount}",
            command,
            policyReason,
            _blockCount);

        var feedbackMessage = new ChatMessage(
            ChatRole.User,
            $"Task scope violation: command `{command}` is not allowed for the current file scope.\n\n" +
            policyReason + "\n" +
            "Use read-only validation commands such as `dotnet build`, `dotnet test`, `dotnet list package --vulnerable`, " +
            "or edit only the explicitly allowed files. If the task truly needs project or migration changes, stop and report that it requires a separate scoped task.");

        intervention = QueryRuntimeIntervention.BlockWithMessage(
            feedbackMessage,
            $"Task scope violation: command `{command}` is blocked. {policyReason} Block #{_blockCount}.");
        return true;
    }

    private bool TryGetBlockedCommandReason(string command, out string reason)
    {
        reason = string.Empty;
        // Judged on the parsed command (program + subcommand), not on words anywhere in the line: a commit message or an echo that mentions
        // "dotnet add" runs nothing, while dotnet.exe, a chained command or bash -c "dotnet add ..." does.
        var invocations = ShellCommandParser.DotnetInvocations(command);
        if (invocations.Count == 0)
        {
            return false;
        }

        bool Runs(Func<IReadOnlyList<string>, bool> match) => invocations.Any(match);

        if (!AllowsMigrationCommand() &&
            Runs(args => args.Count >= 2 && args[0] == "ef" && args[1] is "migrations" or "database"))
        {
            reason = "EF migration/database commands can create or rewrite migration files outside the current task scope.";
            return true;
        }

        if (!AllowsProjectStructureCommand() &&
            Runs(args => args.Count > 0 &&
                         ((args[0] is "add" or "remove" && args.Skip(1).Any(arg => arg is "package" or "reference")) ||
                          (args[0] == "sln" && args.Skip(1).Any(arg => arg is "add" or "remove")) ||
                          args[0] == "new")))
        {
            reason = "Project/package/reference commands can modify .csproj, solution, or generated project files outside the allowed file list.";
            return true;
        }

        if (!AllowsBroadFormattingCommand() &&
            Runs(args => args.Count > 0 && args[0] == "format"))
        {
            reason = "Broad formatting commands can rewrite files beyond the explicitly allowed task scope.";
            return true;
        }

        return false;
    }

    private bool AllowsMigrationCommand()
        => _scope.AllowProjectFileEdits ||
           _scope.AllowedFiles.Any(IsMigrationPath) ||
           _scope.AllowedDirectories.Any(IsMigrationPath);

    private bool AllowsProjectStructureCommand()
        => _scope.AllowProjectFileEdits ||
           _scope.AllowedFiles.Any(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

    private bool AllowsBroadFormattingCommand()
        => _scope.AllowProjectFileEdits && _scope.AllowedDirectories.Count > 0;

    private static bool IsMigrationPath(string path)
        => NormalizePathLikeForCommand(path).Contains("/migrations", StringComparison.Ordinal);

    private static string NormalizePathLikeForCommand(string path)
        => path.Replace('\\', '/').Trim().ToLowerInvariant();

    private static bool TryConvertPathValue(object? value, out string path)
    {
        path = string.Empty;
        switch (value)
        {
            case null:
                return false;
            case string stringValue:
                path = stringValue;
                return !string.IsNullOrWhiteSpace(path);
            case JsonElement { ValueKind: JsonValueKind.String } jsonString:
                path = jsonString.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(path);
            case JsonElement jsonElement when jsonElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined:
                path = jsonElement.ToString();
                return !string.IsNullOrWhiteSpace(path);
            case Newtonsoft.Json.Linq.JValue jValue:
                path = jValue.Value?.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(path);
            case Newtonsoft.Json.Linq.JToken jToken:
                path = jToken.ToString();
                return !string.IsNullOrWhiteSpace(path);
            default:
                path = value.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(path);
        }
    }

    private static bool TryConvertCommandValue(object? value, out string command)
    {
        command = string.Empty;
        switch (value)
        {
            case null:
                return false;
            case string stringValue:
                command = stringValue;
                return !string.IsNullOrWhiteSpace(command);
            case JsonElement { ValueKind: JsonValueKind.String } jsonString:
                command = jsonString.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(command);
            case JsonElement { ValueKind: JsonValueKind.Array } jsonArray:
                command = string.Join(" ", jsonArray.EnumerateArray().Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()));
                return !string.IsNullOrWhiteSpace(command);
            case Newtonsoft.Json.Linq.JArray jArray:
                command = string.Join(" ", jArray.Select(token => token.ToString()));
                return !string.IsNullOrWhiteSpace(command);
            case Newtonsoft.Json.Linq.JValue jValue:
                command = jValue.Value?.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(command);
            case IEnumerable enumerable:
                command = string.Join(" ", enumerable.Cast<object?>().Select(part => part?.ToString()).Where(part => !string.IsNullOrWhiteSpace(part)));
                return !string.IsNullOrWhiteSpace(command);
            default:
                command = value.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(command);
        }
    }

    private QueryRuntimeIntervention BlockWrite(string toolName, string reasonCode, string detail)
    {
        _blockCount++;
        Record(toolName, UnsafeOrUnresolvedPath(reasonCode, detail), null, $"{reasonCode}: {detail}");
        _logger.LogWarning("Scope guard denied {ToolName}: [{Reason}] {Detail}", toolName, reasonCode, detail);
        return QueryRuntimeIntervention.BlockWithMessage(
            new ChatMessage(ChatRole.User, $"Write denied for `{toolName}` [{reasonCode}]: {detail}."),
            $"{reasonCode}: `{toolName}` write denied: {detail}. Block #{_blockCount}.");
    }

    /// <summary>
    /// Shape check on the model-supplied path text (the real write points additionally re-validate against the filesystem,
    /// including links, in ToolPathResolver.IsSafeWriteTarget). A single leading slash is tolerated because the write
    /// tools treat it as workspace-relative.
    /// </summary>
    internal static bool TryFindUnsafeShape(string path, out string reason)
    {
        reason = string.Empty;
        var p = path.Trim();
        if (p.Length == 0)
        {
            reason = "empty path";
            return true;
        }

        if (p.StartsWith("\\\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal))
        {
            reason = "UNC or network path";
            return true;
        }

        if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':')
        {
            reason = "drive-qualified absolute path";
            return true;
        }

        if (p.Any(ch => ch < 0x20))
        {
            reason = "control character in path";
            return true;
        }

        var segments = p.Replace((char)92, '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (seg == "..")
            {
                reason = "parent traversal";
                return true;
            }

            if (seg.Contains(':', StringComparison.Ordinal))
            {
                reason = "alternate data stream or drive syntax";
                return true;
            }

            if (seg.Equals(".git", StringComparison.OrdinalIgnoreCase) || seg.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase))
            {
                reason = "git metadata is protected";
                return true;
            }
        }

        return false;
    }

    private string BuildBlockReason(string normalizedPath, string toolName)
    {
        return $"Task scope violation: tool `{toolName}` targeting `{normalizedPath}` is outside allowed scope. " +
               $"Allowed: [{string.Join(", ", _scope.AllowedFiles.Take(6))}]. " +
               $"Allowed names: [{string.Join(", ", _scope.AllowedFileNames.Take(6))}]. Block #{_blockCount}.";
    }

    // The detail of an unsafe-path denial starts with the path in backticks; the other denials have no path to name.
    private static string? UnsafeOrUnresolvedPath(string reasonCode, string detail)
    {
        if (reasonCode != "UnsafePath" || detail.Length < 2 || detail[0] != '`')
        {
            return null;
        }

        var end = detail.IndexOf('`', 1);
        return end > 1 ? detail[1..end] : null;
    }

    private readonly record struct PathTarget(string Path, bool IsNewFile);
}
