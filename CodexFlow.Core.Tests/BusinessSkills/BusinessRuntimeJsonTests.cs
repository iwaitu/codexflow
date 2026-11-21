// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodexFlow.Core.BusinessSkills;
using Xunit;

namespace CodexFlow.Core.Tests.BusinessSkills;

public sealed class BusinessRuntimeJsonTests
{
    [Fact]
    public void GeneratedWorkflowPayloadsPreserveLegacyJson()
    {
        var json = BusinessRuntimeJsonContext.Default;
        using var document = JsonDocument.Parse("""{"金额":12345.67890123456789,"token":"\u003C&","items":[null,7]}""");
        var values = new List<JsonElement> { document.RootElement };
        IReadOnlyDictionary<string, JsonElement> inputs = new Dictionary<string, JsonElement>
        {
            ["RawValue"] = document.RootElement,
            ["amount"] = document.RootElement.GetProperty("金额")
        };

        AssertParity(new { matched = true }, new WorkflowBranchPayload(true), json.WorkflowBranchPayload);
        AssertParity(new { items = (IReadOnlyList<JsonElement>)values, count = 1, partialData = false },
            new WorkflowPagePayload(values, 1, false), json.WorkflowPagePayload);
        AssertParity(new { createdId = (string?)null, text = "中文 <\"文本\">" },
            new WorkflowCapabilityFallbackPayload(null, "中文 <\"文本\">"), json.WorkflowCapabilityFallbackPayload);
        AssertParity(new { text = "中文", disclosedFields = new[] { "金额", "RawValue" } },
            new WorkflowGeneratedTextPayload("中文", ["金额", "RawValue"]), json.WorkflowGeneratedTextPayload);
        AssertParity(new { targetCapabilityId = "read&report", inputs, batchCount = 1 },
            new WorkflowApprovalPayload("read&report", inputs, 1), json.WorkflowApprovalPayload);
        AssertParity(new { results = values, partial = true },
            new WorkflowPartialLoopPayload(values, true), json.WorkflowPartialLoopPayload);
        AssertParity(new { results = values, count = 1 },
            new WorkflowLoopPayload(values, 1), json.WorkflowLoopPayload);
        AssertParity(new { rows = values, count = 1 },
            new BusinessTransformRowsPayload(values, 1), json.BusinessTransformRowsPayload);
        AssertParity(new { omitted = true }, new WorkflowOmittedPayload(true), json.Object);
    }

    [Fact]
    public void GeneratedTransformAndProjectionJsonPreserveTheirSupportedValueTypes()
    {
        var json = BusinessRuntimeJsonContext.Default;
        using var document = JsonDocument.Parse("""{"Field":[42,true,null,"中文"]}""");
        var transformRow = new Dictionary<string, object?>
        {
            ["HighPrecision"] = 12345.678901234567890123m,
            ["Maximum"] = long.MaxValue,
            ["count"] = 2,
            ["flag"] = false,
            ["nested"] = document.RootElement,
            ["note"] = "中文\n\\",
            ["missing"] = null
        };
        var projection = new List<Dictionary<string, JsonElement>>
        {
            new() { ["Field"] = document.RootElement.GetProperty("Field") }
        };

        AssertParity(transformRow, transformRow, json.DictionaryStringObject);
        AssertParity(projection, projection, json.Object);
        AssertParity(projection[0], projection[0], json.Object);
    }

    private static void AssertParity(object legacy, object candidate, JsonTypeInfo typeInfo)
        => Assert.Equal(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(candidate, typeInfo));
}
