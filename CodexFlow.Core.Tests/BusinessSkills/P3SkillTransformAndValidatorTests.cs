// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.BusinessSkills;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.BusinessSkills;

public sealed class P3SkillTransformAndValidatorTests
{
    [Fact]
    public void TransformEngine_ReceivableCollection_AggregatesRepaymentsFirst_RespectsDecimalAndBoundaries_AndBlocksCartesianOrOversize()
    {
        var budgets = new SkillBudgetPolicyDto();
        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["currency"] = JsonSerializer.SerializeToElement("CNY")
        };

        var customersPayload = JsonSerializer.SerializeToElement(new
        {
            items = new[]
            {
                new { customerId = "C-1", customerName = "华东精密", creditOwner = "张三" },
                new { customerId = "C-2", customerName = "边界达标客户", creditOwner = "李四" },
                new { customerId = "C-3", customerName = "边界恰好30天", creditOwner = "王五" },
                new { customerId = "C-4", customerName = "边界恰好5万元", creditOwner = "赵六" },
                new { customerId = "C-5", customerName = "美元客户", creditOwner = "钱七" }
            }
        });

        var invoicesPayload = JsonSerializer.SerializeToElement(new
        {
            items = new[]
            {
                new { invoiceId = "INV-01", customerId = "C-1", amount = 120_000.00m, currency = "CNY", overdueDays = 38 },
                new { invoiceId = "INV-02", customerId = "C-2", amount = 50_000.01m, currency = "CNY", overdueDays = 31 },
                new { invoiceId = "INV-03", customerId = "C-3", amount = 90_000.00m, currency = "CNY", overdueDays = 30 },
                new { invoiceId = "INV-04", customerId = "C-4", amount = 80_000.00m, currency = "CNY", overdueDays = 40 },
                new { invoiceId = "INV-05", customerId = "C-5", amount = 100_000.00m, currency = "USD", overdueDays = 45 }
            }
        });

        var repaymentsPayload = JsonSerializer.SerializeToElement(new
        {
            items = new[]
            {
                new { invoiceId = "INV-01", amount = 20_000.00m, currency = "CNY" },
                new { invoiceId = "INV-01", amount = 30_000.00m, currency = "CNY" },
                new { invoiceId = "INV-04", amount = 30_000.00m, currency = "CNY" }
            }
        });

        var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["fetch_customers"] = customersPayload,
            ["fetch_invoices"] = invoicesPayload,
            ["fetch_repayments"] = repaymentsPayload
        };

        // 1. 尝试直接用未聚合 1:N 回款流水关联账款，必须因 RequirePreAggregation 被阻断（防金额倍增）
        var unsafeJoin = new SkillTransformStep
        {
            Id = "unsafe_join",
            Operation = SkillTransformOperation.LeftJoin,
            Source = new() { Step = "fetch_invoices", Pointer = "/items" },
            SecondarySource = new() { Step = "fetch_repayments", Pointer = "/items" },
            JoinSpec = new SkillJoinSpec
            {
                LeftKey = "invoiceId",
                RightKey = "invoiceId",
                RightKeyUniqueExpected = true,
                DuplicateKeyPolicy = SkillDuplicateKeyPolicy.RequirePreAggregation,
                MissingMatchPolicy = "zero_or_empty",
                RightFieldsToInclude = ["amount"]
            }
        };
        var unsafeRes = SkillTransformEngine.Execute(unsafeJoin, budgets, inputs, outputs);
        Assert.False(unsafeRes.Success);
        Assert.Equal(BusinessSkillErrorCodes.ValidationFailed, unsafeRes.ErrorCode);

        // 2. 正确流水线：先按 invoiceId + currency 聚合回款，再 LeftJoin + Lookup 客户 + Filter 边界
        var aggStep = new SkillTransformStep
        {
            Id = "agg_repayments",
            Operation = SkillTransformOperation.Aggregate,
            Source = new() { Step = "fetch_repayments", Pointer = "/items" },
            GroupByFields = ["invoiceId", "currency"],
            Aggregations = [new("repaidAmount", "sum", "amount"), new("repaymentCount", "count")]
        };
        var aggRes = SkillTransformEngine.Execute(aggStep, budgets, inputs, outputs);
        Assert.True(aggRes.Success, aggRes.ErrorMessage);
        outputs["agg_repayments"] = aggRes.OutputPayload;

        var joinStep = new SkillTransformStep
        {
            Id = "join_rep",
            Operation = SkillTransformOperation.LeftJoin,
            Source = new() { Step = "fetch_invoices", Pointer = "/items" },
            SecondarySource = new() { Step = "agg_repayments", Pointer = "/rows" },
            JoinSpec = new SkillJoinSpec
            {
                LeftKey = "invoiceId",
                RightKey = "invoiceId",
                RightKeyUniqueExpected = true,
                DuplicateKeyPolicy = SkillDuplicateKeyPolicy.RequirePreAggregation,
                MissingMatchPolicy = "zero_or_empty",
                RightFieldsToInclude = ["repaidAmount", "repaymentCount"]
            }
        };
        var joinRes = SkillTransformEngine.Execute(joinStep, budgets, inputs, outputs);
        Assert.True(joinRes.Success, joinRes.ErrorMessage);
        outputs["join_rep"] = joinRes.OutputPayload;

        // 显式计算精确 decimal 余额（模拟实际账款接口已带 unpaidAmount 或投影字段后过滤）
        var enrichedRows = joinRes.OutputPayload.GetProperty("rows").EnumerateArray().Select(row =>
        {
            var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in row.EnumerateObject()) dict[p.Name] = p.Value.Clone();
            var amount = dict["amount"].GetDecimal();
            var repaid = dict["repaidAmount"].GetDecimal();
            dict["unpaidAmount"] = JsonSerializer.SerializeToElement(amount - repaid);
            return dict;
        }).ToArray();
        outputs["with_balance"] = JsonSerializer.SerializeToElement(new { rows = enrichedRows });

        var lookupStep = new SkillTransformStep
        {
            Id = "lookup_cust",
            Operation = SkillTransformOperation.Lookup,
            Source = new() { Step = "with_balance", Pointer = "/rows" },
            SecondarySource = new() { Step = "fetch_customers", Pointer = "/items" },
            JoinSpec = new SkillJoinSpec
            {
                LeftKey = "customerId",
                RightKey = "customerId",
                RightKeyUniqueExpected = true,
                DuplicateKeyPolicy = SkillDuplicateKeyPolicy.Reject,
                MissingMatchPolicy = "fail",
                RightFieldsToInclude = ["customerName"]
            }
        };
        var lookupRes = SkillTransformEngine.Execute(lookupStep, budgets, inputs, outputs);
        Assert.True(lookupRes.Success, lookupRes.ErrorMessage);
        outputs["lookup_cust"] = lookupRes.OutputPayload;

        var filterStep = new SkillTransformStep
        {
            Id = "filter_overdue",
            Operation = SkillTransformOperation.Filter,
            Source = new() { Step = "lookup_cust", Pointer = "/rows" },
            Predicate = new SkillConditionExpr
            {
                Combinator = "and",
                Clauses =
                [
                    new("overdueDays", "gt", new() { Literal = JsonSerializer.SerializeToElement(30) }),
                    new("unpaidAmount", "gt", new() { Literal = JsonSerializer.SerializeToElement(50_000.00m) }),
                    new("currency", "eq", new() { Input = "currency" })
                ]
            },
            SortBy = [new("unpaidAmount", Descending: true)]
        };
        var filterRes = SkillTransformEngine.Execute(filterStep, budgets, inputs, outputs);
        Assert.True(filterRes.Success, filterRes.ErrorMessage);
        Assert.Equal(2, filterRes.OutputRowCount);

        var rows = filterRes.OutputPayload.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal("INV-01", rows[0].GetProperty("invoiceId").GetString());
        Assert.Equal(70_000.00m, rows[0].GetProperty("unpaidAmount").GetDecimal());
        Assert.Equal("INV-02", rows[1].GetProperty("invoiceId").GetString());
        Assert.Equal(50_000.01m, rows[1].GetProperty("unpaidAmount").GetDecimal());

        // 3. 验证超限 (OutputLimit = 1) 不允许静默截断成功，而是报 BudgetExceeded
        var limitStep = filterStep with { OutputLimit = 1 };
        var limitRes = SkillTransformEngine.Execute(limitStep, budgets, inputs, outputs);
        Assert.False(limitRes.Success);
        Assert.Equal(BusinessSkillErrorCodes.BudgetExceeded, limitRes.ErrorCode);
    }

    [Fact]
    public void Validator_RejectsCycle_UnauthorizedModelField_UnknownInReadOnlyTestRun_AndMissingWriteApproval()
    {
        var baseDef = ReceivableCollectionP0SampleTests.CreateReceivableCollectionSample();

        // 1. generateText 引用不在 DataPolicy.AllowedModelFields 中的字段 -> SelectionViolation
        var badFieldDef = baseDef with
        {
            DataPolicy = baseDef.DataPolicy with { AllowedModelFields = ["customerName"] }
        };
        var badFieldRes = SkillDefinitionV1ValidatorAndCanonicalizer.ValidateStatic(badFieldDef);
        Assert.False(badFieldRes.IsValid);
        Assert.Contains(badFieldRes.Errors, e => e.Code == BusinessSkillErrorCodes.SelectionViolation);

        // 2. Branch 环路检查
        var cyclicDef = baseDef with
        {
            Steps =
            [
                new SkillBranchStep
                {
                    Id = "b1",
                    Condition = new(),
                    ThenStepIds = ["b2"],
                    ElseStepIds = []
                },
                new SkillBranchStep
                {
                    Id = "b2",
                    Condition = new(),
                    ThenStepIds = ["b1"],
                    ElseStepIds = []
                }
            ],
            SuccessCriteria = new SkillSuccessCriteriaDto { RequiredSteps = [] }
        };
        var cyclicRes = SkillDefinitionV1ValidatorAndCanonicalizer.ValidateStatic(cyclicDef);
        Assert.False(cyclicRes.IsValid);
        Assert.Contains(cyclicRes.Errors, e => e.Message.Contains("backward edge", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("Cycle", StringComparison.OrdinalIgnoreCase));

        // 3. ValidateAgainstCatalog：只读试跑禁止 Unknown/Write 风险能力；非只读步骤必须有前置 ApprovalStep
        var capMap = new Dictionary<string, CapabilityDetailDto>(StringComparer.Ordinal);
        foreach (var capId in baseDef.SelectedCapabilityIds)
        {
            var isWrite = capId.Contains("POST", StringComparison.Ordinal);
            capMap[capId] = new CapabilityDetailDto
            {
                Summary = new CapabilitySummaryDto
                {
                    Id = capId,
                    SnapshotId = baseDef.CatalogSnapshotId,
                    Risk = isWrite ? CapabilityRiskLevel.Unknown : CapabilityRiskLevel.Read,
                    Executability = CapabilityExecutabilityStatus.Executable
                },
                Invocation = new CapabilityInvocationSpecDto { SourceType = BusinessPluginSourceType.OpenApi }
            };
        }

        var readOnlyViolation = SkillDefinitionV1ValidatorAndCanonicalizer.ValidateAgainstCatalog(
            baseDef, baseDef.PluginId, baseDef.CatalogSnapshotId, "staging", capMap, isReadOnlyTestRun: true);
        Assert.False(readOnlyViolation.IsValid);
        Assert.Contains(readOnlyViolation.Errors, e => e.Code == BusinessSkillErrorCodes.SelectionViolation);

        var withoutApproval = baseDef with
        {
            Steps = baseDef.Steps.Where(s => s is not SkillApprovalStep).ToArray()
        };
        var missingApproval = SkillDefinitionV1ValidatorAndCanonicalizer.ValidateAgainstCatalog(
            withoutApproval, baseDef.PluginId, baseDef.CatalogSnapshotId, "staging", capMap, isReadOnlyTestRun: false);
        Assert.False(missingApproval.IsValid);
        Assert.Contains(missingApproval.Errors, e => e.Code == BusinessSkillErrorCodes.ApprovalRequired);
    }

    [Fact]
    public async Task StructuredGenerator_FlagsOutOfSelectionCapabilityAndRendersReadOnlyMarkdown()
    {
        var sample = ReceivableCollectionP0SampleTests.CreateReceivableCollectionSample();
        var unauthorized = sample with
        {
            UsedCapabilityIds = [.. sample.UsedCapabilityIds, "erp-demo:DELETE:/api/customers"]
        };

        var fakeChat = new Mock<IChatClient>();
        fakeChat.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(unauthorized))));

        var caps = sample.SelectedCapabilityIds.Select(id => new CapabilityDetailDto
        {
            Summary = new CapabilitySummaryDto
            {
                Id = id,
                SnapshotId = sample.CatalogSnapshotId,
                ModelAlias = id.Replace(':', '_'),
                Title = id,
                Description = "desc",
                Risk = id.Contains("POST") ? CapabilityRiskLevel.Write : CapabilityRiskLevel.Read,
                Executability = CapabilityExecutabilityStatus.Executable
            },
            Invocation = new CapabilityInvocationSpecDto { SourceType = BusinessPluginSourceType.OpenApi }
        }).ToArray();

        var generator = new StructuredBusinessSkillGenerator(fakeChat.Object);
        var outcome = await generator.GenerateAsync(new SkillGenerationContext(
            sample.SkillId, 1, sample.Name, sample.Description, sample.PluginId,
            "staging", "conn-1", 1, "crev-1", sample.CatalogSnapshotId, caps, false));

        Assert.True(outcome.Success);
        Assert.Contains(outcome.Errors, e => e.Code == BusinessSkillErrorCodes.SelectionViolation);
        Assert.NotEmpty(outcome.ClarificationQuestions);

        var md = SkillMarkdownRenderer.RenderReadOnlyMarkdown(sample);
        Assert.Contains(sample.Name, md, StringComparison.Ordinal);
        Assert.Contains("不作为独立执行真相", md, StringComparison.Ordinal);
    }
}
