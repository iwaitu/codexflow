// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public class ToolArgumentNormalizerTests
{
    // ═══════════════════════════════════════════════════════════════
    // NormalizeInPlace — container flattening
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NormalizeInPlace_FlattensSingleContainerKey()
    {
        var args = new Dictionary<string, object?>
        {
            ["arguments"] = new Dictionary<string, object?>
            {
                ["path"] = "src/main.cs",
                ["recursive"] = true
            }
        };

        ToolArgumentNormalizer.NormalizeInPlace(args);

        Assert.Equal(2, args.Count);
        Assert.Equal("src/main.cs", args["path"]);
        Assert.Equal(true, args["recursive"]);
    }

    [Fact]
    public void NormalizeInPlace_DoesNotFlattenNonContainerKey()
    {
        var args = new Dictionary<string, object?>
        {
            ["path"] = "src/main.cs",
            ["recursive"] = true
        };

        ToolArgumentNormalizer.NormalizeInPlace(args);

        Assert.Equal(2, args.Count);
        Assert.Equal("src/main.cs", args["path"]);
    }

    // ═══════════════════════════════════════════════════════════════
    // NormalizeInPlace — key trimming & blank removal
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NormalizeInPlace_TrimsKeys()
    {
        var args = new Dictionary<string, object?>
        {
            ["  path  "] = "src/main.cs"
        };

        ToolArgumentNormalizer.NormalizeInPlace(args);

        Assert.True(args.ContainsKey("path"));
        Assert.Equal("src/main.cs", args["path"]);
    }

    [Fact]
    public void NormalizeInPlace_RemovesBlankKeys()
    {
        var args = new Dictionary<string, object?>
        {
            ["   "] = "value",
            ["path"] = "src/main.cs"
        };

        ToolArgumentNormalizer.NormalizeInPlace(args);

        Assert.Single(args);
        Assert.Equal("src/main.cs", args["path"]);
    }

    // ═══════════════════════════════════════════════════════════════
    // NormalizeToolArgumentValue — string trim & empty→null
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NormalizeToolArgumentValue_TrimsStrings()
    {
        var result = ToolArgumentNormalizer.NormalizeToolArgumentValue("path", "  src/main.cs  ");
        Assert.Equal("src/main.cs", result);
    }

    [Fact]
    public void NormalizeToolArgumentValue_EmptyStringBecomesNull()
    {
        var result = ToolArgumentNormalizer.NormalizeToolArgumentValue("path", "   ");
        Assert.Null(result);
    }

    // ═══════════════════════════════════════════════════════════════
    // NormalizeToolArgumentValue — line-number string→int conversion
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NormalizeToolArgumentValue_ConvertsStartLineStringToInt()
    {
        var result = ToolArgumentNormalizer.NormalizeToolArgumentValue("start_line", "10");
        Assert.Equal(10, result);
    }

    [Fact]
    public void NormalizeToolArgumentValue_ConvertsEndLineStringToInt()
    {
        var result = ToolArgumentNormalizer.NormalizeToolArgumentValue("end_line", "50");
        Assert.Equal(50, result);
    }

    // ═══════════════════════════════════════════════════════════════
    // BUG-001 regression: window_start_line / window_line_count
    // ivilson_read declares these as int? but LLM may send "10" as string.
    // The old Controller logic used key.Contains("line") which covered these.
    // The initial shared version only matched start_line/end_line exactly,
    // causing a regression where string values wouldn't be converted to int.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NormalizeToolArgumentValue_ConvertsWindowStartLineStringToInt()
    {
        var result = ToolArgumentNormalizer.NormalizeToolArgumentValue("window_start_line", "42");
        Assert.Equal(42, result);
        Assert.IsType<int>(result);
    }

    [Fact]
    public void NormalizeToolArgumentValue_ConvertsWindowLineCountStringToInt()
    {
        var result = ToolArgumentNormalizer.NormalizeToolArgumentValue("window_line_count", "100");
        Assert.Equal(100, result);
        Assert.IsType<int>(result);
    }

    [Fact]
    public void NormalizeToolArgumentValue_ConvertsMaxResultsStringToInt()
    {
        var result = ToolArgumentNormalizer.NormalizeToolArgumentValue("max_results", "50");
        Assert.Equal(50, result);
        Assert.IsType<int>(result);
    }

    // Broad heuristic: keys containing "line", "start", "end", "count"
    [Fact]
    public void NormalizeToolArgumentValue_ConvertsLineNumbersStringToInt_ViaBroadHeuristic()
    {
        var result = ToolArgumentNormalizer.NormalizeToolArgumentValue("line_number", "25");
        Assert.Equal(25, result);
    }

    [Fact]
    public void NormalizeToolArgumentValue_ConvertsStartOffsetStringToInt_ViaBroadHeuristic()
    {
        var result = ToolArgumentNormalizer.NormalizeToolArgumentValue("start_offset", "5");
        Assert.Equal(5, result);
    }

    [Fact]
    public void NormalizeToolArgumentValue_DoesNotConvertNonLineNumberStringToInt()
    {
        var result = ToolArgumentNormalizer.NormalizeToolArgumentValue("path", "10");
        Assert.Equal("10", result);
    }

    // ═══════════════════════════════════════════════════════════════
    // NormalizeInPlace — end-to-end: ivilson_read arguments
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NormalizeInPlace_ConvertsAllReadToolLineNumberArgs_FromStrings()
    {
        // Simulates LLM sending all line-number params as strings
        var args = new Dictionary<string, object?>
        {
            ["path"] = "src/main.cs",
            ["start_line"] = "10",
            ["end_line"] = "50",
            ["window_start_line"] = "30",
            ["window_line_count"] = "20"
        };

        ToolArgumentNormalizer.NormalizeInPlace(args);

        Assert.Equal("src/main.cs", args["path"]);
        Assert.Equal(10, args["start_line"]);
        Assert.Equal(50, args["end_line"]);
        Assert.Equal(30, args["window_start_line"]);
        Assert.Equal(20, args["window_line_count"]);
    }

    [Fact]
    public void NormalizeInPlace_AliasesFilePathToPath_WhenCanonicalPathMissing()
    {
        var args = new Dictionary<string, object?>
        {
            ["file_path"] = "src/main.cs"
        };

        ToolArgumentNormalizer.NormalizeInPlace(args);

        Assert.Equal("src/main.cs", args["path"]);
    }

    // ═══════════════════════════════════════════════════════════════
    // NormalizeCopy
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NormalizeCopy_DoesNotMutateOriginal()
    {
        var original = new Dictionary<string, object?>
        {
            ["  path  "] = "  src/main.cs  ",
            ["start_line"] = "10"
        };

        var copy = ToolArgumentNormalizer.NormalizeCopy(original);

        Assert.True(original.ContainsKey("  path  "));
        Assert.Equal("  src/main.cs  ", original["  path  "]);
        Assert.Equal("10", original["start_line"]);

        Assert.True(copy.ContainsKey("path"));
        Assert.Equal("src/main.cs", copy["path"]);
        Assert.Equal(10, copy["start_line"]);
    }

    [Fact]
    public void NormalizeCopy_HandlesNullInput()
    {
        var result = ToolArgumentNormalizer.NormalizeCopy(null);
        Assert.Empty(result);
    }

    [Fact]
    public void NormalizeToolArgumentValue_PathArray_CollapsesToFirstString()
    {
        var result = ToolArgumentNormalizer.NormalizeToolArgumentValue("path", new object?[] { "src/CleanApp.Core/Services", "ignored" });

        Assert.Equal("src/CleanApp.Core/Services", result);
    }

    [Fact]
    public void NormalizeToolArgumentValue_PathWrappedInSingleNestedArray_CollapsesToString()
    {
        var result = ToolArgumentNormalizer.NormalizeToolArgumentValue("path", new object?[] { new object?[] { "src/CleanApp" } });

        Assert.Equal("src/CleanApp", result);
    }

    [Fact]
    public void NormalizeToolArgumentValue_PathDictionary_BecomesNull()
    {
        var result = ToolArgumentNormalizer.NormalizeToolArgumentValue("path", new Dictionary<string, object?> { ["unexpected"] = "value" });

        Assert.Null(result);
    }

    [Fact]
    public void NormalizeInPlace_PreservesSingleHashlineOperationArray()
    {
        var args = new Dictionary<string, object?>
        {
            ["operations"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace_range",
                    ["newLines"] = new object?[] { "updated" }
                }
            }
        };

        ToolArgumentNormalizer.NormalizeInPlace(args);

        var operations = Assert.IsAssignableFrom<IList<object?>>(args["operations"]);
        var operation = Assert.IsType<Dictionary<string, object?>>(Assert.Single(operations));
        var newLines = Assert.IsAssignableFrom<IList<object?>>(operation["newLines"]);
        Assert.Equal("updated", Assert.Single(newLines));
    }
}
