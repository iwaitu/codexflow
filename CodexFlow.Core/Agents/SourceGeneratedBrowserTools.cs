// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Agents;

internal sealed partial class SourceGeneratedToolFunction
{
    private static readonly JsonElement BrowserNavigateSchema = ParseElement("""
        {"type":"object","properties":{
            "url":{"type":["string","null"],"default":null},
            "action":{"type":["string","null"],"default":null}
        }}
        """);

    private static readonly JsonElement BrowserSnapshotSchema = ParseElement("""
        {"type":"object","properties":{
            "max_chars":{"type":["integer","null"],"default":null},
            "include_console":{"type":["boolean","null"],"default":null},
            "include_network_errors":{"type":["boolean","null"],"default":null},
            "include_screenshot":{"type":["boolean","null"],"default":null},
            "annotate_screenshot":{"type":["boolean","null"],"default":null}
        }}
        """);

    private static readonly JsonElement BrowserActSchema = ParseElement("""
        {"type":"object","properties":{
            "action":{"type":"string"},
            "ref":{"type":["string","null"],"default":null},
            "text":{"type":["string","null"],"default":null},
            "key":{"type":["string","null"],"default":null},
            "file_path":{"type":["string","null"],"default":null},
            "delta_y":{"type":["integer","null"],"default":null},
            "timeout_ms":{"type":["integer","null"],"default":null}
        },"required":["action"]}
        """);

    private static readonly JsonElement BrowserTabsSchema = ParseElement("""
        {"type":"object","properties":{
            "action":{"type":"string"},
            "tab_id":{"type":["string","null"],"default":null},
            "url":{"type":["string","null"],"default":null}
        },"required":["action"]}
        """);

    private static readonly JsonElement BrowserRequestTakeoverSchema = ParseElement("""
        {"type":"object","properties":{
            "reason":{"type":"string"},
            "timeout_seconds":{"type":["integer","null"],"default":null}
        },"required":["reason"]}
        """);

    private static readonly JsonElement BrowserEvaluateSchema = ParseElement("""
        {"type":"object","properties":{
            "expression":{"type":"string"},
            "await_promise":{"type":["boolean","null"],"default":null},
            "max_result_chars":{"type":["integer","null"],"default":null}
        },"required":["expression"]}
        """);

    internal static AIFunction Browser(ICodexTool tool, string name, string description) => name switch
    {
        BrowserToolNames.Navigate => new SourceGeneratedToolFunction(tool, name, description, BrowserNavigateSchema, BindBrowserNavigate),
        BrowserToolNames.Snapshot => new SourceGeneratedToolFunction(tool, name, description, BrowserSnapshotSchema, BindBrowserSnapshot),
        BrowserToolNames.Act => new SourceGeneratedToolFunction(tool, name, description, BrowserActSchema, BindBrowserAct),
        BrowserToolNames.Tabs => new SourceGeneratedToolFunction(tool, name, description, BrowserTabsSchema, BindBrowserTabs),
        BrowserToolNames.RequestTakeover => new SourceGeneratedToolFunction(tool, name, description, BrowserRequestTakeoverSchema, BindBrowserRequestTakeover),
        BrowserToolNames.Evaluate => new SourceGeneratedToolFunction(tool, name, description, BrowserEvaluateSchema, BindBrowserEvaluate),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown browser tool.")
    };

    private static Dictionary<string, object?> BindBrowserNavigate(AIFunctionArguments input)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddWhenPresent(args, "url", ReadArgument(input, "url", JsonContext.String));
        AddWhenPresent(args, "action", ReadArgument(input, "action", JsonContext.String));
        return args;
    }

    private static Dictionary<string, object?> BindBrowserSnapshot(AIFunctionArguments input)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddWhenPresent(args, "max_chars", ReadArgument(input, "max_chars", JsonContext.NullableInt32));
        AddWhenPresent(args, "include_console", ReadArgument(input, "include_console", JsonContext.NullableBoolean));
        AddWhenPresent(args, "include_network_errors", ReadArgument(input, "include_network_errors", JsonContext.NullableBoolean));
        AddWhenPresent(args, "include_screenshot", ReadArgument(input, "include_screenshot", JsonContext.NullableBoolean));
        AddWhenPresent(args, "annotate_screenshot", ReadArgument(input, "annotate_screenshot", JsonContext.NullableBoolean));
        return args;
    }

    private static Dictionary<string, object?> BindBrowserAct(AIFunctionArguments input)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["action"] = RequireString(input, "action") };
        AddWhenPresent(args, "ref", ReadArgument(input, "ref", JsonContext.String));
        AddWhenPresent(args, "text", ReadArgument(input, "text", JsonContext.String));
        AddWhenPresent(args, "key", ReadArgument(input, "key", JsonContext.String));
        AddWhenPresent(args, "file_path", ReadArgument(input, "file_path", JsonContext.String));
        AddWhenPresent(args, "delta_y", ReadArgument(input, "delta_y", JsonContext.NullableInt32));
        AddWhenPresent(args, "timeout_ms", ReadArgument(input, "timeout_ms", JsonContext.NullableInt32));
        return args;
    }

    private static Dictionary<string, object?> BindBrowserTabs(AIFunctionArguments input)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["action"] = RequireString(input, "action") };
        AddWhenPresent(args, "tab_id", ReadArgument(input, "tab_id", JsonContext.String));
        AddWhenPresent(args, "url", ReadArgument(input, "url", JsonContext.String));
        return args;
    }

    private static Dictionary<string, object?> BindBrowserRequestTakeover(AIFunctionArguments input)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["reason"] = RequireString(input, "reason") };
        AddWhenPresent(args, "timeout_seconds", ReadArgument(input, "timeout_seconds", JsonContext.NullableInt32));
        return args;
    }

    private static Dictionary<string, object?> BindBrowserEvaluate(AIFunctionArguments input)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["expression"] = RequireString(input, "expression") };
        AddWhenPresent(args, "await_promise", ReadArgument(input, "await_promise", JsonContext.NullableBoolean));
        AddWhenPresent(args, "max_result_chars", ReadArgument(input, "max_result_chars", JsonContext.NullableInt32));
        return args;
    }
}
