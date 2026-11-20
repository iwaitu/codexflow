// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.BusinessSkills;

/// <summary>
/// 统一业务能力执行器接口：按策略分发 OpenAPI 或 MCP 单步与有界分页调用。
/// </summary>
public interface IBusinessCapabilityExecutor
{
    Task<CapabilityExecutionResult> ExecuteAsync(CapabilityExecutionRequest request, CancellationToken ct = default);
}

public sealed class BusinessCapabilityExecutor : IBusinessCapabilityExecutor
{
    private readonly OpenApiBusinessPluginAdapter _openApiAdapter;
    private readonly McpBusinessPluginAdapter _mcpAdapter;
    private readonly ILoggerFactory? _loggerFactory;

    public BusinessCapabilityExecutor(
        OpenApiBusinessPluginAdapter openApiAdapter,
        McpBusinessPluginAdapter mcpAdapter,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(openApiAdapter);
        ArgumentNullException.ThrowIfNull(mcpAdapter);
        _openApiAdapter = openApiAdapter;
        _mcpAdapter = mcpAdapter;
        _loggerFactory = loggerFactory;
    }

    public Task<CapabilityExecutionResult> ExecuteAsync(CapabilityExecutionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Pagination is null)
        {
            return DispatchSingleAsync(request, ct);
        }

        return ExecutePaginatedAsync(request, request.Pagination, ct);
    }

    private Task<CapabilityExecutionResult> DispatchSingleAsync(CapabilityExecutionRequest request, CancellationToken ct)
        => request.Capability.Invocation.SourceType switch
        {
            BusinessPluginSourceType.OpenApi => _openApiAdapter.InvokeSingleAsync(request, ct),
            BusinessPluginSourceType.Mcp => _mcpAdapter.InvokeSingleAsync(request, _loggerFactory, ct),
            _ => Task.FromResult(new CapabilityExecutionResult
            {
                Success = false,
                ErrorCode = BusinessSkillErrorCodes.UnsupportedProtocol,
                ErrorMessage = $"Unsupported capability source type '{request.Capability.Invocation.SourceType}'."
            })
        };

    private async Task<CapabilityExecutionResult> ExecutePaginatedAsync(
        CapabilityExecutionRequest baseRequest,
        SkillPaginationConfig pagination,
        CancellationToken ct)
    {
        if (pagination.MaxPages <= 0 || pagination.PageSize <= 0)
        {
            return new CapabilityExecutionResult
            {
                Success = false,
                ErrorCode = BusinessSkillErrorCodes.ValidationFailed,
                ErrorMessage = "Pagination MaxPages and PageSize must be greater than zero."
            };
        }

        var collected = new List<JsonElement>();
        var diagnostics = new List<string>();
        var requestsUsed = 0;
        var retriesUsed = 0;
        var pagesFetched = 0;
        var visitedCursors = new HashSet<string>(StringComparer.Ordinal);
        var isCursorMode = string.Equals(pagination.Mode, "cursor", StringComparison.OrdinalIgnoreCase);

        var pageNumber = 1;
        string? cursor = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (pagesFetched >= pagination.MaxPages)
            {
                return BuildPartialResult(
                    $"Pagination reached configured MaxPages ({pagination.MaxPages}) before proving traversal completeness.",
                    collected,
                    requestsUsed,
                    retriesUsed,
                    pagesFetched,
                    diagnostics);
            }

            if (baseRequest.RemainingRequestBudget - requestsUsed <= 0)
            {
                return BuildPartialResult(
                    "External request budget reached during pagination; marking result as PartialData.",
                    collected,
                    requestsUsed,
                    retriesUsed,
                    pagesFetched,
                    diagnostics);
            }

            var pageArgs = new Dictionary<string, JsonElement>(baseRequest.Arguments, StringComparer.Ordinal);
            if (isCursorMode)
            {
                if (!string.IsNullOrEmpty(pagination.CursorParam) && !string.IsNullOrEmpty(cursor))
                {
                    pageArgs[pagination.CursorParam] = JsonSerializer.SerializeToElement(cursor, BusinessRuntimeJsonContext.Default.String);
                }

                if (!string.IsNullOrEmpty(pagination.PageSizeParam))
                {
                    pageArgs[pagination.PageSizeParam] = JsonSerializer.SerializeToElement(pagination.PageSize, BusinessRuntimeJsonContext.Default.Int32);
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(pagination.PageParam))
                {
                    pageArgs[pagination.PageParam] = JsonSerializer.SerializeToElement(pageNumber, BusinessRuntimeJsonContext.Default.Int32);
                }

                if (!string.IsNullOrEmpty(pagination.PageSizeParam))
                {
                    pageArgs[pagination.PageSizeParam] = JsonSerializer.SerializeToElement(pagination.PageSize, BusinessRuntimeJsonContext.Default.Int32);
                }
            }

            var pageRequest = baseRequest with
            {
                Arguments = pageArgs,
                RemainingRequestBudget = baseRequest.RemainingRequestBudget - requestsUsed
            };

            var single = await DispatchSingleAsync(pageRequest, ct).ConfigureAwait(false);
            requestsUsed += single.RequestsConsumed;
            retriesUsed += single.RetriesConsumed;

            if (!single.Success || single.PayloadJson is null)
            {
                return new CapabilityExecutionResult
                {
                    Success = false,
                    PartialData = pagesFetched > 0,
                    OutcomeUnknown = single.OutcomeUnknown,
                    IsToolError = single.IsToolError,
                    HttpStatusCode = single.HttpStatusCode,
                    ErrorCode = pagesFetched > 0 ? BusinessSkillErrorCodes.PartialData : single.ErrorCode,
                    ErrorMessage = single.ErrorMessage,
                    CollectedItems = collected,
                    RequestsConsumed = requestsUsed,
                    RetriesConsumed = retriesUsed,
                    PagesFetched = pagesFetched,
                    Diagnostics = diagnostics
                };
            }

            pagesFetched++;
            var root = single.PayloadJson.Value;

            if (!BusinessCapabilityResponseHelpers.TryEvaluateJsonPointer(root, pagination.ItemsPointer, out var itemsElem) ||
                itemsElem.ValueKind != JsonValueKind.Array)
            {
                return new CapabilityExecutionResult
                {
                    Success = false,
                    PartialData = pagesFetched > 1,
                    ErrorCode = BusinessSkillErrorCodes.UnsupportedSchema,
                    ErrorMessage = $"Paginated response missing array at ItemsPointer '{pagination.ItemsPointer}'.",
                    CollectedItems = collected,
                    RequestsConsumed = requestsUsed,
                    RetriesConsumed = retriesUsed,
                    PagesFetched = pagesFetched,
                    Diagnostics = diagnostics
                };
            }

            var pageItemCount = 0;
            foreach (var item in itemsElem.EnumerateArray())
            {
                if (collected.Count >= baseRequest.RemainingRecordBudget)
                {
                    return BuildPartialResult(
                        string.Format(CultureInfo.InvariantCulture, "Read record budget ({0}) exceeded during pagination.", baseRequest.RemainingRecordBudget),
                        collected,
                        requestsUsed,
                        retriesUsed,
                        pagesFetched,
                        diagnostics);
                }

                collected.Add(item.Clone());
                pageItemCount++;
            }

            // 判定分页是否已完备结束
            if (TryDeterminePaginationFinished(root, pagination, isCursorMode, pageItemCount, collected.Count, visitedCursors, out var nextCursor, out var cursorLoopDetected))
            {
                if (cursorLoopDetected)
                {
                    return BuildPartialResult(
                        $"Pagination cursor loop detected at '{nextCursor}'.",
                        collected,
                        requestsUsed,
                        retriesUsed,
                        pagesFetched,
                        diagnostics);
                }

                break;
            }

            if (isCursorMode)
            {
                cursor = nextCursor;
            }
            else
            {
                pageNumber++;
            }
        }

        var mergedArray = JsonSerializer.SerializeToElement(collected, BusinessRuntimeJsonContext.Default.ListJsonElement);
        return new CapabilityExecutionResult
        {
            Success = true,
            PartialData = false,
            PayloadJson = mergedArray,
            CollectedItems = collected,
            RequestsConsumed = requestsUsed,
            RetriesConsumed = retriesUsed,
            PagesFetched = pagesFetched,
            Diagnostics = diagnostics
        };
    }

    private static bool TryDeterminePaginationFinished(
        JsonElement root,
        SkillPaginationConfig pagination,
        bool isCursorMode,
        int pageItemCount,
        int totalCollectedCount,
        HashSet<string> visitedCursors,
        out string? nextCursor,
        out bool cursorLoopDetected)
    {
        nextCursor = null;
        cursorLoopDetected = false;

        if (!string.IsNullOrWhiteSpace(pagination.HasMorePointer) &&
            BusinessCapabilityResponseHelpers.TryEvaluateJsonPointer(root, pagination.HasMorePointer, out var hasMoreElem))
        {
            if (hasMoreElem.ValueKind == JsonValueKind.True)
            {
                return pageItemCount == 0;
            }

            if (hasMoreElem.ValueKind is JsonValueKind.False or JsonValueKind.Null)
            {
                return true;
            }
        }

        if (isCursorMode)
        {
            if (string.IsNullOrWhiteSpace(pagination.NextCursorPointer) ||
                !BusinessCapabilityResponseHelpers.TryEvaluateJsonPointer(root, pagination.NextCursorPointer, out var cursorElem) ||
                cursorElem.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            nextCursor = BusinessCapabilityResponseHelpers.JsonElementToScalarString(cursorElem);
            if (string.IsNullOrEmpty(nextCursor))
            {
                return true;
            }

            if (!visitedCursors.Add(nextCursor))
            {
                cursorLoopDetected = true;
                return true;
            }

            return false;
        }

        if (!string.IsNullOrWhiteSpace(pagination.TotalCountPointer) &&
            BusinessCapabilityResponseHelpers.TryEvaluateJsonPointer(root, pagination.TotalCountPointer, out var totalElem) &&
            totalElem.TryGetInt32(out var expectedTotal))
        {
            if (totalCollectedCount >= expectedTotal)
            {
                return true;
            }

            if (pageItemCount > 0)
            {
                return false;
            }
        }

        // 无总数且无 hasMore=true 时，空页或小于 PageSize 代表末页
        if (pageItemCount == 0 || pageItemCount < pagination.PageSize)
        {
            return true;
        }

        return false;
    }

    private static CapabilityExecutionResult BuildPartialResult(
        string message,
        IReadOnlyList<JsonElement> collected,
        int requestsUsed,
        int retriesUsed,
        int pagesFetched,
        IReadOnlyList<string> diagnostics) => new()
    {
        Success = false,
        PartialData = true,
        ErrorCode = BusinessSkillErrorCodes.PartialData,
        ErrorMessage = message,
        CollectedItems = collected,
        RequestsConsumed = requestsUsed,
        RetriesConsumed = retriesUsed,
        PagesFetched = pagesFetched,
        Diagnostics = diagnostics
    };
}
