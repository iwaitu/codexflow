// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.BusinessSkills;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.Core.Tests.BusinessSkills;

public sealed class ReceivableCollectionP0SampleTests
{
    public static SkillDefinitionV1 CreateReceivableCollectionSample()
    {
        IReadOnlyList<string> selected =
        [
            "erp-demo:GET:/api/customers",
            "erp-demo:GET:/api/invoices",
            "erp-demo:GET:/api/repayments",
            "erp-demo:POST:/api/collection-tasks"
        ];

        return new SkillDefinitionV1
        {
            SchemaVersion = 1,
            SkillId = "receivable-collection",
            Version = 1,
            Name = "逾期应收账款催收",
            Description = "找出截至指定日期逾期超过30天、未回款金额超过5万元的客户，按金额排序并经确认后创建催收任务。",
            PluginId = "erp-demo",
            ConnectionBinding = new SkillConnectionBinding
            {
                ConnectionId = "conn-erp-prod",
                Environment = "staging",
                ConnectionRevision = 1,
                CredentialRevision = "cred-rev-1",
                SelectedServerUrl = "https://erp.example.internal"
            },
            CatalogSnapshotId = "snap-erp-001",
            SelectedCapabilityIds = selected,
            UsedCapabilityIds = selected,
            Inputs = new SkillInputSchemaDto
            {
                Required = ["asOfDate", "currency", "orgScope"],
                Properties = new Dictionary<string, SkillInputPropertyDto>(StringComparer.Ordinal)
                {
                    ["asOfDate"] = new() { Type = "string", Format = "date", Description = "统计截止日期 (YYYY-MM-DD)" },
                    ["currency"] = new() { Type = "string", Description = "核算币种", DefaultValue = JsonSerializer.SerializeToElement("CNY") },
                    ["orgScope"] = new() { Type = "string", Description = "组织范围编码" }
                }
            },
            Outputs = new SkillOutputSchemaDto
            {
                Fields =
                [
                    new("qualifiedCustomers", "array", "符合逾期 > 30 天且未回款 > 5 万元的客户明细"),
                    new("createdTasks", "array", "确认后创建的催收任务回执与 ID")
                ]
            },
            Steps =
            [
                new SkillToolStep
                {
                    Id = "fetch_customers",
                    Title = "分页查询客户档案",
                    CapabilityId = "erp-demo:GET:/api/customers",
                    Arguments = new Dictionary<string, SkillValueBinding>(StringComparer.Ordinal)
                    {
                        ["orgScope"] = new() { Input = "orgScope" }
                    },
                    Pagination = new SkillPaginationConfig
                    {
                        Mode = "page",
                        PageParam = "page",
                        PageSizeParam = "pageSize",
                        PageSize = 200,
                        TotalCountPointer = "/totalCount",
                        ItemsPointer = "/items",
                        MaxPages = 20
                    }
                },
                new SkillToolStep
                {
                    Id = "fetch_invoices",
                    Title = "分页查询应收账款",
                    CapabilityId = "erp-demo:GET:/api/invoices",
                    Arguments = new Dictionary<string, SkillValueBinding>(StringComparer.Ordinal)
                    {
                        ["orgScope"] = new() { Input = "orgScope" },
                        ["currency"] = new() { Input = "currency" }
                    },
                    Pagination = new SkillPaginationConfig
                    {
                        Mode = "cursor",
                        CursorParam = "cursor",
                        NextCursorPointer = "/nextCursor",
                        ItemsPointer = "/items",
                        MaxPages = 30
                    }
                },
                new SkillToolStep
                {
                    Id = "fetch_repayments",
                    Title = "分页查询回款流水",
                    CapabilityId = "erp-demo:GET:/api/repayments",
                    Arguments = new Dictionary<string, SkillValueBinding>(StringComparer.Ordinal)
                    {
                        ["orgScope"] = new() { Input = "orgScope" },
                        ["currency"] = new() { Input = "currency" }
                    },
                    Pagination = new SkillPaginationConfig
                    {
                        Mode = "cursor",
                        CursorParam = "cursor",
                        NextCursorPointer = "/nextCursor",
                        ItemsPointer = "/items",
                        MaxPages = 40
                    }
                },
                new SkillTransformStep
                {
                    Id = "aggregate_repayments_by_invoice",
                    Title = "先按账款 ID 汇总回款，避免 1:N 关联导致金额倍增",
                    Operation = SkillTransformOperation.Aggregate,
                    Source = new() { Step = "fetch_repayments", Pointer = "/items" },
                    GroupByFields = ["invoiceId", "currency"],
                    Aggregations =
                    [
                        new("repaidAmount", "sum", "amount"),
                        new("repaymentCount", "count")
                    ]
                },
                new SkillTransformStep
                {
                    Id = "join_invoice_with_repayments",
                    Title = "按账款 ID 左连接汇总回款并计算未回款余额",
                    Operation = SkillTransformOperation.LeftJoin,
                    Source = new() { Step = "fetch_invoices", Pointer = "/items" },
                    SecondarySource = new() { Step = "aggregate_repayments_by_invoice", Pointer = "/rows" },
                    JoinSpec = new SkillJoinSpec
                    {
                        LeftKey = "invoiceId",
                        RightKey = "invoiceId",
                        KeyType = "string",
                        RightKeyUniqueExpected = true,
                        DuplicateKeyPolicy = SkillDuplicateKeyPolicy.RequirePreAggregation,
                        MissingMatchPolicy = "zero_or_empty",
                        RightFieldsToInclude = ["repaidAmount", "repaymentCount"]
                    }
                },
                new SkillTransformStep
                {
                    Id = "lookup_customer_on_invoice",
                    Title = "查找并关联客户主数据",
                    Operation = SkillTransformOperation.Lookup,
                    Source = new() { Step = "join_invoice_with_repayments", Pointer = "/rows" },
                    SecondarySource = new() { Step = "fetch_customers", Pointer = "/items" },
                    JoinSpec = new SkillJoinSpec
                    {
                        LeftKey = "customerId",
                        RightKey = "customerId",
                        KeyType = "string",
                        RightKeyUniqueExpected = true,
                        DuplicateKeyPolicy = SkillDuplicateKeyPolicy.Reject,
                        MissingMatchPolicy = "fail",
                        RightFieldsToInclude = ["customerName", "creditOwner"]
                    }
                },
                new SkillTransformStep
                {
                    Id = "filter_and_sort_overdue",
                    Title = "严格筛选逾期 > 30 天且未回款 > 50,000（十进制与币种一致）并降序排列",
                    Operation = SkillTransformOperation.Filter,
                    Source = new() { Step = "lookup_customer_on_invoice", Pointer = "/rows" },
                    Predicate = new SkillConditionExpr
                    {
                        Combinator = "and",
                        Clauses =
                        [
                            new("overdueDays", "gt", new() { Literal = JsonSerializer.SerializeToElement(30) }),
                            new("unpaidAmount", "gt", new() { Literal = JsonSerializer.SerializeToElement(50000m) }),
                            new("currency", "eq", new() { Input = "currency" })
                        ]
                    },
                    SortBy = [new("unpaidAmount", Descending: true)]
                },
                new SkillGenerateTextStep
                {
                    Id = "generate_advice",
                    Title = "基于字段白名单生成催收建议",
                    Source = new() { Step = "filter_and_sort_overdue", Pointer = "/rows" },
                    AllowedInputFields = ["customerName", "invoiceId", "unpaidAmount", "currency", "overdueDays"],
                    PromptTemplate = "针对以下逾期应收明细生成礼貌、分级的中文催收沟通建议，不要编造金额或日期。"
                },
                new SkillApprovalStep
                {
                    Id = "approve_collection_write",
                    Title = "展示待创建催收任务清单并等待人工批准",
                    TargetCapabilityId = "erp-demo:POST:/api/collection-tasks",
                    ItemsSource = new() { Step = "filter_and_sort_overdue", Pointer = "/rows" },
                    PlannedMutation = "为筛选出的逾期客户各创建 1 条催收任务",
                    KeyAmountField = "unpaidAmount",
                    CurrencyField = "currency",
                    TargetRecordFields = ["customerId", "customerName", "invoiceId", "unpaidAmount", "overdueDays"]
                },
                new SkillForeachStep
                {
                    Id = "dispatch_collection_tasks",
                    Title = "串行创建已批准的催收任务",
                    Source = new() { Step = "filter_and_sort_overdue", Pointer = "/rows" },
                    ItemVariable = "item",
                    BodyStepIds = ["create_single_collection_task"],
                    MaxItems = BusinessSkillDefaults.MaxWriteBatchItems,
                    Sequential = true
                },
                new SkillToolStep
                {
                    Id = "create_single_collection_task",
                    Title = "调用创建催收任务接口",
                    CapabilityId = "erp-demo:POST:/api/collection-tasks",
                    Arguments = new Dictionary<string, SkillValueBinding>(StringComparer.Ordinal)
                    {
                        ["customerId"] = new() { Item = "item", Pointer = "/customerId" },
                        ["invoiceId"] = new() { Item = "item", Pointer = "/invoiceId" },
                        ["unpaidAmount"] = new() { Item = "item", Pointer = "/unpaidAmount" },
                        ["currency"] = new() { Item = "item", Pointer = "/currency" },
                        ["advice"] = new() { Step = "generate_advice", Pointer = "/text" }
                    },
                    ResponseContract = new SkillResponseContract
                    {
                        BusinessCodePointer = "/code",
                        SuccessCodeValues = ["OK", "CREATED"],
                        CreatedIdPointer = "/data/taskId",
                        IdempotencyKeyTemplate = "{{item.invoiceId}}:{{inputs.asOfDate}}"
                    }
                },
                new SkillOutputStep
                {
                    Id = "final_output",
                    Title = "输出催收结果与创建凭据",
                    Source = new() { Step = "dispatch_collection_tasks", Pointer = "/results" },
                    NamedOutputs = new Dictionary<string, SkillValueBinding>(StringComparer.Ordinal)
                    {
                        ["qualifiedCustomers"] = new() { Step = "filter_and_sort_overdue", Pointer = "/rows" },
                        ["createdTasks"] = new() { Step = "dispatch_collection_tasks", Pointer = "/results" }
                    }
                }
            ],
            SuccessCriteria = new SkillSuccessCriteriaDto
            {
                RequiredSteps = ["fetch_customers", "fetch_invoices", "fetch_repayments", "filter_and_sort_overdue", "final_output"],
                RequireCompletePagination = true,
                FailOnPartialData = true
            },
            DataPolicy = new SkillDataPolicyDto
            {
                AllowedModelFields = ["customerName", "invoiceId", "unpaidAmount", "currency", "overdueDays"],
                IncludeSanitizedSamplesForGeneration = false,
                RedactLogsAndExports = true
            }
        };
    }

    [Fact]
    public void ReceivableCollectionSample_PassesStaticValidation_ContentHashStability_AndStructuredOutputSchemaCompatibility()
    {
        var sample = CreateReceivableCollectionSample();

        var validation = SkillDefinitionV1ValidatorAndCanonicalizer.ValidateStatic(sample);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(e => $"{e.Field}: {e.Message}")));
        Assert.Equal(64, validation.ContentHash.Length);

        // 非行为性证据变化不影响 ContentHash
        var withEvidence = sample with
        {
            ValidationEvidence = new SkillValidationEvidenceDto
            {
                DraftRevision = 5,
                ReadOnlyTestRunId = "run-ro-1",
                VerifiedAtUtc = DateTimeOffset.UtcNow
            }
        };
        Assert.Equal(validation.ContentHash, SkillDefinitionV1ValidatorAndCanonicalizer.ComputeContentHash(withEvidence));

        // 行为参数变化（如预算或步骤变动）必须改变 ContentHash
        var modified = sample with { Budgets = sample.Budgets with { MaxWriteBatchItems = 50 } };
        Assert.NotEqual(validation.ContentHash, SkillDefinitionV1ValidatorAndCanonicalizer.ComputeContentHash(modified));

        // 校验 StructuredOutputSchema 可直接传给 Microsoft.Extensions.AI ChatResponseFormat.ForJsonSchema
        var llmSchema = SkillSchemaProvider.StructuredOutputSchema;
        Assert.False(llmSchema.TryGetProperty("$schema", out _));
        Assert.Equal("object", llmSchema.GetProperty("type").GetString());
        Assert.False(llmSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(llmSchema.GetProperty("properties").GetProperty("steps").TryGetProperty("maxItems", out _));

        var responseFormat = ChatResponseFormat.ForJsonSchema(
            llmSchema,
            "business_skill_v1",
            "Declarative Business Skill Definition v1");
        Assert.NotNull(responseFormat);

        // 验证越权能力引用会被静态校验拦截为 SelectionViolation
        var unauthorized = sample with
        {
            UsedCapabilityIds = [.. sample.UsedCapabilityIds, "erp-demo:DELETE:/api/customers"]
        };
        var invalidResult = SkillDefinitionV1ValidatorAndCanonicalizer.ValidateStatic(unauthorized);
        Assert.False(invalidResult.IsValid);
        Assert.Contains(invalidResult.Errors, e => e.Code == BusinessSkillErrorCodes.SelectionViolation);
    }

    [Fact]
    public void ReceivableCollectionJoinBenchmark_AggregatesRepaymentsFirst_RespectsDecimalAndStrictGreaterThanBoundaries()
    {
        var asOfDate = new DateOnly(2026, 11, 17);
        const string targetCurrency = "CNY";

        var customers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["C-1"] = "华东精密",
            ["C-2"] = "边界达标客户",
            ["C-3"] = "边界恰好30天",
            ["C-4"] = "边界恰好5万元",
            ["C-5"] = "美元客户"
        };

        var invoices = new[]
        {
            // INV-01: 账款 120,000，有 2 笔回款 (20,000 + 30,000 = 50,000)，未回款应为 70,000（而非 120,000*2）
            new TestInvoice("INV-01", "C-1", 120_000.00m, "CNY", new DateOnly(2026, 10, 10)), // overdue 38d
            // INV-02: 账款 50,000.01，无回款，逾期 31 天 -> 应命中！
            new TestInvoice("INV-02", "C-2", 50_000.01m, "CNY", new DateOnly(2026, 10, 17)), // overdue 31d
            // INV-03: 逾期恰好 30 天 -> “超过 30 天” 不命中！
            new TestInvoice("INV-03", "C-3", 90_000.00m, "CNY", new DateOnly(2026, 10, 18)), // overdue 30d
            // INV-04: 未回款恰好 50,000.00 -> “超过 5 万元” 不命中！
            new TestInvoice("INV-04", "C-4", 80_000.00m, "CNY", new DateOnly(2026, 10, 1)),  // 30,000 repaid -> 50,000 unpaid
            // INV-05: 币种为 USD -> 跨币种不得混算进 CNY 总额！
            new TestInvoice("INV-05", "C-5", 100_000.00m, "USD", new DateOnly(2026, 10, 1))
        };

        var repayments = new[]
        {
            new TestRepayment("INV-01", 20_000.00m, "CNY"),
            new TestRepayment("INV-01", 30_000.00m, "CNY"),
            new TestRepayment("INV-04", 30_000.00m, "CNY")
        };

        // 步骤 4：先按账款 ID + 币种聚合回款
        var aggregatedRepayments = repayments
            .GroupBy(r => (r.InvoiceId, r.Currency))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        // 步骤 5~7：Left Join 回款汇总 + Lookup 客户 + 严格筛选与降序排列
        var currencyMismatches = new List<string>();
        var results = new List<(string InvoiceId, string CustomerName, int OverdueDays, decimal UnpaidAmount)>();

        foreach (var inv in invoices)
        {
            if (!string.Equals(inv.Currency, targetCurrency, StringComparison.Ordinal))
            {
                currencyMismatches.Add(inv.InvoiceId);
                continue;
            }

            aggregatedRepayments.TryGetValue((inv.InvoiceId, inv.Currency), out var repaid);
            var unpaid = inv.Amount - repaid;
            var overdueDays = asOfDate.DayNumber - inv.DueDate.DayNumber;

            if (overdueDays > 30 && unpaid > 50_000m)
            {
                var customerName = customers[inv.CustomerId];
                results.Add((inv.InvoiceId, customerName, overdueDays, unpaid));
            }
        }

        results = results.OrderByDescending(r => r.UnpaidAmount).ToList();

        Assert.Equal(["INV-05"], currencyMismatches);
        Assert.Equal(2, results.Count);
        Assert.Equal(("INV-01", "华东精密", 38, 70_000.00m), results[0]);
        Assert.Equal(("INV-02", "边界达标客户", 31, 50_000.01m), results[1]);
        Assert.DoesNotContain(results, r => r.InvoiceId is "INV-03" or "INV-04");
        Assert.Equal("70000.00", results[0].UnpaidAmount.ToString("F2", CultureInfo.InvariantCulture));
    }

    private sealed record TestInvoice(string InvoiceId, string CustomerId, decimal Amount, string Currency, DateOnly DueDate);
    private sealed record TestRepayment(string InvoiceId, decimal Amount, string Currency);
}
