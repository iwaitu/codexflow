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
    private static readonly JsonElement ComputerScreenInfoSchema = ParseElement("""
        {"type":"object","properties":{
            "pin_window_handle":{"type":["string","null"],"default":null},
            "pin_process_name":{"type":["string","null"],"default":null},
            "unpin":{"type":["boolean","null"],"default":null}
        }}
        """);

    private static readonly JsonElement ComputerScreenshotSchema = ParseElement("""
        {"type":"object","properties":{
            "full_desktop":{"type":["boolean","null"],"default":null},
            "display_index":{"type":["integer","null"],"default":null},
            "include_grid":{"type":["boolean","null"],"default":null}
        }}
        """);

    private static readonly JsonElement ComputerActSchema = ParseElement("""
        {"type":"object","properties":{
            "action":{"type":"string"},
            "x":{"type":["number","null"],"default":null},
            "y":{"type":["number","null"],"default":null},
            "end_x":{"type":["number","null"],"default":null},
            "end_y":{"type":["number","null"],"default":null},
            "button":{"type":["string","null"],"default":null},
            "text":{"type":["string","null"],"default":null},
            "keys":{"type":["string","null"],"default":null},
            "delta_x":{"type":["integer","null"],"default":null},
            "delta_y":{"type":["integer","null"],"default":null},
            "duration_ms":{"type":["integer","null"],"default":null},
            "coordinate_space":{"type":["string","null"],"default":null}
        },"required":["action"]}
        """);

    private static readonly JsonElement ComputerRequestTakeoverSchema = ParseElement("""
        {"type":"object","properties":{
            "reason":{"type":"string"},
            "timeout_seconds":{"type":["integer","null"],"default":null}
        },"required":["reason"]}
        """);

    internal static AIFunction Computer(ICodexTool tool, string name, string description) => name switch
    {
        ComputerToolNames.ScreenInfo => new SourceGeneratedToolFunction(tool, name, description, ComputerScreenInfoSchema, BindComputerScreenInfo),
        ComputerToolNames.Screenshot => new SourceGeneratedToolFunction(tool, name, description, ComputerScreenshotSchema, BindComputerScreenshot),
        ComputerToolNames.Act => new SourceGeneratedToolFunction(tool, name, description, ComputerActSchema, BindComputerAct),
        ComputerToolNames.RequestTakeover => new SourceGeneratedToolFunction(tool, name, description, ComputerRequestTakeoverSchema, BindComputerRequestTakeover),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown computer use tool.")
    };

    private static Dictionary<string, object?> BindComputerScreenInfo(AIFunctionArguments input)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddWhenPresent(args, "pin_window_handle", ReadArgument(input, "pin_window_handle", JsonContext.String));
        AddWhenPresent(args, "pin_process_name", ReadArgument(input, "pin_process_name", JsonContext.String));
        AddWhenPresent(args, "unpin", ReadArgument(input, "unpin", JsonContext.NullableBoolean));
        return args;
    }

    private static Dictionary<string, object?> BindComputerScreenshot(AIFunctionArguments input)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddWhenPresent(args, "full_desktop", ReadArgument(input, "full_desktop", JsonContext.NullableBoolean));
        AddWhenPresent(args, "display_index", ReadArgument(input, "display_index", JsonContext.NullableInt32));
        AddWhenPresent(args, "include_grid", ReadArgument(input, "include_grid", JsonContext.NullableBoolean));
        return args;
    }

    private static Dictionary<string, object?> BindComputerAct(AIFunctionArguments input)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["action"] = RequireString(input, "action") };
        AddWhenPresent(args, "x", ReadArgument(input, "x", JsonContext.NullableDouble));
        AddWhenPresent(args, "y", ReadArgument(input, "y", JsonContext.NullableDouble));
        AddWhenPresent(args, "end_x", ReadArgument(input, "end_x", JsonContext.NullableDouble));
        AddWhenPresent(args, "end_y", ReadArgument(input, "end_y", JsonContext.NullableDouble));
        AddWhenPresent(args, "button", ReadArgument(input, "button", JsonContext.String));
        AddWhenPresent(args, "text", ReadArgument(input, "text", JsonContext.String));
        AddWhenPresent(args, "keys", ReadArgument(input, "keys", JsonContext.String));
        AddWhenPresent(args, "delta_x", ReadArgument(input, "delta_x", JsonContext.NullableInt32));
        AddWhenPresent(args, "delta_y", ReadArgument(input, "delta_y", JsonContext.NullableInt32));
        AddWhenPresent(args, "duration_ms", ReadArgument(input, "duration_ms", JsonContext.NullableInt32));
        AddWhenPresent(args, "coordinate_space", ReadArgument(input, "coordinate_space", JsonContext.String));
        return args;
    }

    private static Dictionary<string, object?> BindComputerRequestTakeover(AIFunctionArguments input)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["reason"] = RequireString(input, "reason") };
        AddWhenPresent(args, "timeout_seconds", ReadArgument(input, "timeout_seconds", JsonContext.NullableInt32));
        return args;
    }
}
