// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.Agents;
using CodexFlow.Core.BusinessSkills;
using Xunit;

namespace CodexFlow.Core.Tests.BusinessSkills;

public sealed class ToolsBJsonCompatibilityTests
{
    [Fact]
    public void SkillCandidatePreservesInitializersForMissingPropertiesAndKeepsExplicitValues()
    {
        const string json = """
            {
              "name":"技能测试",
              "SUCCESSCRITERIA":{"RequireCompletePagination":false},
              "budgets":{"MaxExternalRequests":"10"},
              "inputs":{"properties":{"MixedCase":{"defaultValue":{ "Raw": [1.20, "\u4E2D"] }}}},
              "steps":[
                {"id":"join","operation":"leftjoin","type":"transform","joinSpec":{"missingMatchPolicy":null}},
                {"type":"foreach"},
                {"id":"page","pagination":{"pageSize":"20"},"type":"tool"}
              ]
            }
            """;
        var definition = Assert.IsType<SkillDefinitionV1>(BusinessSkillDefinitionJsonReader.Read(json));
        Assert.Equal("技能测试", definition.Name);
        Assert.False(definition.SuccessCriteria.RequireCompletePagination);
        Assert.True(definition.SuccessCriteria.FailOnPartialData);
        Assert.True(definition.DataPolicy.RedactLogsAndExports);
        Assert.Equal(10, definition.Budgets.MaxExternalRequests);
        Assert.Equal(BusinessSkillDefaults.MaxReadOnlyRetries, definition.Budgets.MaxReadOnlyRetries);
        Assert.Equal("string", definition.Inputs.Properties["MixedCase"].Type);
        Assert.Equal("""{ "Raw": [1.20, "\u4E2D"] }""", definition.Inputs.Properties["MixedCase"].DefaultValue?.GetRawText());
        var transform = Assert.IsType<SkillTransformStep>(definition.Steps[0]);
        Assert.Equal(SkillTransformOperation.LeftJoin, transform.Operation);
        Assert.Equal(BusinessSkillDefaults.MaxJoinOutputRows, transform.OutputLimit);
        Assert.Null(transform.JoinSpec!.MissingMatchPolicy);
        Assert.True(transform.JoinSpec.RightKeyUniqueExpected);
        var each = Assert.IsType<SkillForeachStep>(definition.Steps[1]);
        Assert.Equal(string.Empty, each.Id);
        Assert.Equal("item", each.ItemVariable);
        Assert.True(each.Sequential);
        var page = Assert.IsType<SkillToolStep>(definition.Steps[2]).Pagination;
        Assert.Equal(20, page!.PageSize);
        Assert.Equal("/items", page.ItemsPointer);
    }

    [Fact]
    public void KnownExternalArgumentsKeepNestedJsonTypesAndKeys()
    {
        using var source = JsonDocument.Parse("""{"KeepCase":[1.20,null]}""");
        var argument = new Dictionary<string, object?>
        {
            ["Values"] = new List<object?> { 4311231234L, null, true, source.RootElement }
        };

        const string expected = """{"Values":[4311231234,null,true,{"KeepCase":[1.20,null]}]}""";
        Assert.True(ExternalToolArgumentJson.TryWriteWeb(argument, out var web));
        Assert.Equal(expected, web);
        Assert.True(ExternalToolArgumentJson.TryWriteElement(argument, out var element));
        Assert.Equal(expected, element.GetRawText());

        argument["Extension"] = new UnregisteredExtension("existing custom property");
        Assert.False(ExternalToolArgumentJson.TryWriteElement(argument, out _));
    }

    private sealed record UnregisteredExtension(string Value);
}
