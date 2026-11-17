// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;

namespace CodexFlow.Core.BusinessSkills;

public sealed record SkillTransformResult(
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    JsonElement OutputPayload,
    int OutputRowCount);

/// <summary>
/// 确定性内存数据转换引擎（支持 Filter / Sort / Project / Aggregate / InnerJoin / LeftJoin / Lookup）。
/// 金额使用 decimal 精度，关联显式防重防笛卡尔积并严格受 MaxJoinOutputRows 阈值保护。
/// </summary>
public static class SkillTransformEngine
{
    public static SkillTransformResult Execute(
        SkillTransformStep step,
        SkillBudgetPolicyDto budgets,
        IReadOnlyDictionary<string, JsonElement> inputs,
        IReadOnlyDictionary<string, JsonElement> stepOutputs)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(budgets);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(stepOutputs);

        if (!SkillValueBinder.TryResolveBinding(step.Source, inputs, stepOutputs, null, out var primaryRaw, out var srcErr))
        {
            return Failed(BusinessSkillErrorCodes.ValidationFailed, $"Transform '{step.Id}' source failed: {srcErr}");
        }

        if (primaryRaw.ValueKind != JsonValueKind.Array)
        {
            return Failed(BusinessSkillErrorCodes.UnsupportedSchema, $"Transform '{step.Id}' primary source must be a JSON array.");
        }

        var rowLimit = Math.Min(
            step.OutputLimit > 0 ? step.OutputLimit : BusinessSkillDefaults.MaxJoinOutputRows,
            Math.Min(budgets.MaxJoinOutputRows, BusinessSkillDefaults.MaxJoinOutputRows));

        var leftRows = primaryRaw.EnumerateArray().Select(static x => x.Clone()).ToList();

        List<JsonElement> workingRows;
        switch (step.Operation)
        {
            case SkillTransformOperation.Filter:
                if (!TryApplyPredicate(leftRows, step.Predicate, inputs, stepOutputs, out workingRows, out var filterErr))
                {
                    return Failed(BusinessSkillErrorCodes.ValidationFailed, filterErr!);
                }

                break;

            case SkillTransformOperation.Sort:
                workingRows = leftRows;
                break;

            case SkillTransformOperation.Project:
                if (!TryApplyProject(leftRows, step.Projections, out workingRows, out var projErr))
                {
                    return Failed(BusinessSkillErrorCodes.ValidationFailed, projErr!);
                }

                break;

            case SkillTransformOperation.Aggregate:
                if (!TryApplyAggregate(leftRows, step.GroupByFields, step.Aggregations, out workingRows, out var aggErr))
                {
                    return Failed(BusinessSkillErrorCodes.ValidationFailed, aggErr!);
                }

                break;

            case SkillTransformOperation.InnerJoin:
            case SkillTransformOperation.LeftJoin:
            case SkillTransformOperation.Lookup:
                if (step.SecondarySource is null || step.JoinSpec is null)
                {
                    return Failed(BusinessSkillErrorCodes.ValidationFailed, $"Transform '{step.Id}' requires SecondarySource and JoinSpec.");
                }

                if (!SkillValueBinder.TryResolveBinding(step.SecondarySource, inputs, stepOutputs, null, out var secondaryRaw, out var secErr))
                {
                    return Failed(BusinessSkillErrorCodes.ValidationFailed, $"Transform '{step.Id}' secondarySource failed: {secErr}");
                }

                if (secondaryRaw.ValueKind != JsonValueKind.Array)
                {
                    return Failed(BusinessSkillErrorCodes.UnsupportedSchema, $"Transform '{step.Id}' secondary source must be a JSON array.");
                }

                var rightRows = secondaryRaw.EnumerateArray().Select(static x => x.Clone()).ToList();
                var joinOutcome = ExecuteJoin(step.Operation, leftRows, rightRows, step.JoinSpec, rowLimit);
                if (!joinOutcome.Success)
                {
                    return Failed(joinOutcome.ErrorCode!, joinOutcome.ErrorMessage!);
                }

                workingRows = joinOutcome.Rows;
                break;

            default:
                return Failed(BusinessSkillErrorCodes.UnsupportedSchema, $"Unsupported transform operation '{step.Operation}'.");
        }

        // 任何转换步均可附带额外筛选/投影/排序管线，但输出绝不可默默截断超限
        if (step.Operation != SkillTransformOperation.Filter && step.Predicate is not null)
        {
            if (!TryApplyPredicate(workingRows, step.Predicate, inputs, stepOutputs, out workingRows, out var postErr))
            {
                return Failed(BusinessSkillErrorCodes.ValidationFailed, postErr!);
            }
        }

        if (step.Operation != SkillTransformOperation.Project && step.Projections.Count > 0)
        {
            if (!TryApplyProject(workingRows, step.Projections, out workingRows, out var projErr2))
            {
                return Failed(BusinessSkillErrorCodes.ValidationFailed, projErr2!);
            }
        }

        if (workingRows.Count > rowLimit)
        {
            return Failed(
                BusinessSkillErrorCodes.BudgetExceeded,
                $"Transform '{step.Id}' produced {workingRows.Count} rows exceeding safety limit of {rowLimit} rows.");
        }

        if (step.SortBy.Count > 0)
        {
            workingRows = ApplySort(workingRows, step.SortBy);
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            rows = workingRows,
            count = workingRows.Count
        });

        return new SkillTransformResult(true, null, null, payload, workingRows.Count);
    }

    private static bool TryApplyPredicate(
        List<JsonElement> rows,
        SkillConditionExpr? predicate,
        IReadOnlyDictionary<string, JsonElement> inputs,
        IReadOnlyDictionary<string, JsonElement> stepOutputs,
        out List<JsonElement> filtered,
        out string? errorMessage)
    {
        if (predicate is null || predicate.Clauses.Count == 0)
        {
            filtered = rows;
            errorMessage = null;
            return true;
        }

        filtered = [];
        foreach (var row in rows)
        {
            if (!SkillValueBinder.TryEvaluateCondition(predicate, row, inputs, stepOutputs, null, out var matched, out var err))
            {
                errorMessage = err;
                return false;
            }

            if (matched)
            {
                filtered.Add(row);
            }
        }

        errorMessage = null;
        return true;
    }

    private static bool TryApplyProject(
        List<JsonElement> rows,
        IReadOnlyList<SkillProjectionField> projections,
        out List<JsonElement> projected,
        out string? errorMessage)
    {
        if (projections.Count == 0)
        {
            projected = rows;
            errorMessage = null;
            return true;
        }

        projected = new List<JsonElement>(rows.Count);
        foreach (var row in rows)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in projections)
            {
                if (!BusinessCapabilityResponseHelpers.TryEvaluateJsonPointer(row, field.SourcePointer, out var rawVal))
                {
                    dict[field.OutputField] = null;
                    continue;
                }

                if (!TryCoerceProjectedValue(rawVal, field, out var coerced, out var err))
                {
                    errorMessage = $"Projection '{field.OutputField}' failed: {err}";
                    return false;
                }

                dict[field.OutputField] = coerced;

                if (!string.IsNullOrWhiteSpace(field.CurrencyField) &&
                    row.ValueKind == JsonValueKind.Object &&
                    row.TryGetProperty(field.CurrencyField, out var curEl) &&
                    !dict.ContainsKey(field.CurrencyField))
                {
                    dict[field.CurrencyField] = curEl.ValueKind == JsonValueKind.String ? curEl.GetString() : curEl.Clone();
                }
            }

            projected.Add(JsonSerializer.SerializeToElement(dict));
        }

        errorMessage = null;
        return true;
    }

    private static bool TryCoerceProjectedValue(
        JsonElement rawVal,
        SkillProjectionField spec,
        out object? coerced,
        out string? errorMessage)
    {
        errorMessage = null;
        if (rawVal.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            coerced = null;
            return true;
        }

        switch (spec.ValueType.Trim().ToLowerInvariant())
        {
            case "decimal":
                if (SkillValueBinder.TryTryDecimal(rawVal, out var dec))
                {
                    coerced = dec;
                    return true;
                }

                errorMessage = $"Expected decimal value for '{spec.OutputField}'.";
                coerced = null;
                return false;

            case "integer":
                if (rawVal.ValueKind == JsonValueKind.Number && rawVal.TryGetInt64(out var i64))
                {
                    coerced = i64;
                    return true;
                }

                if (rawVal.ValueKind == JsonValueKind.String &&
                    long.TryParse(rawVal.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedI64))
                {
                    coerced = parsedI64;
                    return true;
                }

                errorMessage = $"Expected integer value for '{spec.OutputField}'.";
                coerced = null;
                return false;

            case "boolean":
                if (rawVal.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    coerced = rawVal.GetBoolean();
                    return true;
                }

                if (rawVal.ValueKind == JsonValueKind.String && bool.TryParse(rawVal.GetString(), out var b))
                {
                    coerced = b;
                    return true;
                }

                errorMessage = $"Expected boolean value for '{spec.OutputField}'.";
                coerced = null;
                return false;

            case "date":
                if (!SkillValueBinder.TryTryDateTimeOffset(rawVal, out var dto))
                {
                    errorMessage = $"Expected date value for '{spec.OutputField}'.";
                    coerced = null;
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(spec.DateTimezone))
                {
                    try
                    {
                        var tz = TimeZoneInfo.FindSystemTimeZoneById(spec.DateTimezone);
                        dto = TimeZoneInfo.ConvertTime(dto, tz);
                    }
                    catch (TimeZoneNotFoundException)
                    {
                        errorMessage = $"Unknown DateTimezone '{spec.DateTimezone}'.";
                        coerced = null;
                        return false;
                    }
                }

                coerced = dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return true;

            default:
                coerced = rawVal.ValueKind == JsonValueKind.String ? rawVal.GetString() : rawVal.Clone();
                return true;
        }
    }

    private static bool TryApplyAggregate(
        List<JsonElement> rows,
        IReadOnlyList<string> groupByFields,
        IReadOnlyList<SkillAggregationMetric> metrics,
        out List<JsonElement> aggregated,
        out string? errorMessage)
    {
        errorMessage = null;
        var groups = new Dictionary<string, (Dictionary<string, object?> KeyValues, List<JsonElement> Items)>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                errorMessage = "Aggregate expects array of JSON objects.";
                aggregated = [];
                return false;
            }

            var keyValues = new Dictionary<string, object?>(StringComparer.Ordinal);
            var keyParts = new List<string>(groupByFields.Count);
            foreach (var gf in groupByFields)
            {
                if (row.TryGetProperty(gf, out var propEl))
                {
                    var scalar = BusinessCapabilityResponseHelpers.JsonElementToScalarString(propEl);
                    keyParts.Add($"{gf}={scalar}");
                    keyValues[gf] = propEl.Clone();
                }
                else
                {
                    keyParts.Add($"{gf}=<null>");
                    keyValues[gf] = null;
                }
            }

            var compositeKey = string.Join('|', keyParts);
            if (!groups.TryGetValue(compositeKey, out var bucket))
            {
                bucket = (keyValues, []);
                groups[compositeKey] = bucket;
            }

            bucket.Items.Add(row);
        }

        aggregated = new List<JsonElement>(groups.Count);
        foreach (var bucket in groups.Values)
        {
            var outObj = new Dictionary<string, object?>(bucket.KeyValues, StringComparer.Ordinal);
            foreach (var metric in metrics)
            {
                var fn = metric.Function.Trim().ToLowerInvariant();
                if (fn == "count")
                {
                    outObj[metric.OutputField] = bucket.Items.Count;
                    continue;
                }

                var vals = new List<decimal>();
                foreach (var item in bucket.Items)
                {
                    if (!string.IsNullOrWhiteSpace(metric.SourceField) &&
                        item.TryGetProperty(metric.SourceField, out var valEl) &&
                        valEl.ValueKind != JsonValueKind.Null)
                    {
                        if (!SkillValueBinder.TryTryDecimal(valEl, out var d))
                        {
                            errorMessage = $"Aggregate field '{metric.SourceField}' contains non-numeric value '{valEl}'.";
                            aggregated = [];
                            return false;
                        }

                        vals.Add(d);
                    }
                }

                outObj[metric.OutputField] = fn switch
                {
                    "sum" => vals.Sum(),
                    "min" => vals.Count > 0 ? vals.Min() : 0m,
                    "max" => vals.Count > 0 ? vals.Max() : 0m,
                    _ => 0m
                };
            }

            aggregated.Add(JsonSerializer.SerializeToElement(outObj));
        }

        return true;
    }

    private static (bool Success, string? ErrorCode, string? ErrorMessage, List<JsonElement> Rows) ExecuteJoin(
        SkillTransformOperation operation,
        List<JsonElement> leftRows,
        List<JsonElement> rightRows,
        SkillJoinSpec spec,
        int rowLimit)
    {
        var rightIndex = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        var numericFieldHint = DetectNumericRightFields(rightRows, spec.RightFieldsToInclude);

        foreach (var rRow in rightRows)
        {
            if (rRow.ValueKind != JsonValueKind.Object)
            {
                return (false, BusinessSkillErrorCodes.UnsupportedSchema, "Join right-hand rows must be JSON objects.", []);
            }

            if (!TryExtractJoinKey(rRow, spec.RightKey, spec.KeyType, out var rKey, out var kErr))
            {
                return (false, BusinessSkillErrorCodes.ValidationFailed, kErr, []);
            }

            if (rKey is null)
            {
                continue;
            }

            if (!rightIndex.TryGetValue(rKey, out var list))
            {
                list = [];
                rightIndex[rKey] = list;
            }
            else if (spec.RightKeyUniqueExpected && spec.DuplicateKeyPolicy is SkillDuplicateKeyPolicy.Reject or SkillDuplicateKeyPolicy.RequirePreAggregation)
            {
                var advice = spec.DuplicateKeyPolicy == SkillDuplicateKeyPolicy.RequirePreAggregation
                    ? "Aggregate right-side rows before joining to avoid duplicated totals."
                    : "Duplicate keys are disallowed on right-hand side.";
                return (false, BusinessSkillErrorCodes.ValidationFailed,
                    $"Join duplicate key '{rKey}' on right field '{spec.RightKey}'. {advice}", []);
            }

            list.Add(rRow);
        }

        var output = new List<JsonElement>();
        foreach (var lRow in leftRows)
        {
            if (lRow.ValueKind != JsonValueKind.Object)
            {
                return (false, BusinessSkillErrorCodes.UnsupportedSchema, "Join left-hand rows must be JSON objects.", []);
            }

            if (!TryExtractJoinKey(lRow, spec.LeftKey, spec.KeyType, out var lKey, out var lkErr))
            {
                return (false, BusinessSkillErrorCodes.ValidationFailed, lkErr, []);
            }

            rightIndex.TryGetValue(lKey ?? string.Empty, out var matches);
            if (matches is null || matches.Count == 0)
            {
                if (operation == SkillTransformOperation.InnerJoin ||
                    string.Equals(spec.MissingMatchPolicy, "drop", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(spec.MissingMatchPolicy, "fail", StringComparison.Ordinal))
                {
                    return (false, BusinessSkillErrorCodes.ValidationFailed,
                        $"Join/lookup failed: missing match on left key '{spec.LeftKey}={lKey}'.", []);
                }

                output.Add(MergeJoinRow(lRow, rightRow: null, spec, numericFieldHint));
                if (output.Count > rowLimit)
                {
                    return (false, BusinessSkillErrorCodes.BudgetExceeded,
                        $"Join output exceeded safety limit of {rowLimit} rows.", []);
                }

                continue;
            }

            var selectedMatches = spec.DuplicateKeyPolicy == SkillDuplicateKeyPolicy.FirstMatchOnly || operation == SkillTransformOperation.Lookup
                ? [matches[0]]
                : matches;

            foreach (var rMatch in selectedMatches)
            {
                output.Add(MergeJoinRow(lRow, rMatch, spec, numericFieldHint));
                if (output.Count > rowLimit)
                {
                    return (false, BusinessSkillErrorCodes.BudgetExceeded,
                        $"Join output exceeded safety limit of {rowLimit} rows.", []);
                }
            }
        }

        return (true, null, null, output);
    }

    private static HashSet<string> DetectNumericRightFields(List<JsonElement> rightRows, IReadOnlyList<string> fields)
    {
        var numericFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            foreach (var row in rightRows)
            {
                if (row.ValueKind == JsonValueKind.Object &&
                    row.TryGetProperty(field, out var prop) &&
                    prop.ValueKind == JsonValueKind.Number)
                {
                    numericFields.Add(field);
                    break;
                }
            }
        }

        return numericFields;
    }

    private static bool TryExtractJoinKey(
        JsonElement row,
        string keyField,
        string keyType,
        out string? normalizedKey,
        out string? errorMessage)
    {
        errorMessage = null;
        if (!row.TryGetProperty(keyField, out var keyEl) || keyEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            normalizedKey = null;
            return true;
        }

        if (string.Equals(keyType, "integer", StringComparison.OrdinalIgnoreCase))
        {
            if (keyEl.ValueKind == JsonValueKind.Number && keyEl.TryGetInt64(out var i64))
            {
                normalizedKey = i64.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (keyEl.ValueKind == JsonValueKind.String &&
                long.TryParse(keyEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                normalizedKey = parsed.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            errorMessage = $"Join key '{keyField}' expected integer value, got '{keyEl}'.";
            normalizedKey = null;
            return false;
        }

        normalizedKey = BusinessCapabilityResponseHelpers.JsonElementToScalarString(keyEl);
        return true;
    }

    private static JsonElement MergeJoinRow(
        JsonElement leftRow,
        JsonElement? rightRow,
        SkillJoinSpec spec,
        HashSet<string> numericFields)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in leftRow.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.Clone();
        }

        var fieldsToInclude = spec.RightFieldsToInclude.Count > 0
            ? spec.RightFieldsToInclude
            : (rightRow.HasValue ? rightRow.Value.EnumerateObject().Select(static p => p.Name).ToArray() : []);

        foreach (var field in fieldsToInclude)
        {
            var outName = string.IsNullOrWhiteSpace(spec.TargetPrefix) ? field : $"{spec.TargetPrefix}{field}";
            if (rightRow.HasValue && rightRow.Value.TryGetProperty(field, out var rVal))
            {
                dict[outName] = rVal.Clone();
            }
            else if (string.Equals(spec.MissingMatchPolicy, "null", StringComparison.Ordinal))
            {
                dict[outName] = null;
            }
            else
            {
                dict[outName] = numericFields.Contains(field) ? 0m : string.Empty;
            }
        }

        return JsonSerializer.SerializeToElement(dict);
    }

    private static List<JsonElement> ApplySort(List<JsonElement> rows, IReadOnlyList<SkillSortSpec> sortBy)
    {
        var copy = new List<JsonElement>(rows);
        copy.Sort((a, b) =>
        {
            foreach (var sort in sortBy)
            {
                JsonElement va = default;
                JsonElement vb = default;
                var hasA = a.ValueKind == JsonValueKind.Object && a.TryGetProperty(sort.Field, out va);
                var hasB = b.ValueKind == JsonValueKind.Object && b.TryGetProperty(sort.Field, out vb);

                int cmp;
                if (!hasA && !hasB)
                {
                    cmp = 0;
                }
                else if (!hasA)
                {
                    cmp = -1;
                }
                else if (!hasB)
                {
                    cmp = 1;
                }
                else if (SkillValueBinder.TryTryDecimal(va, out var da) && SkillValueBinder.TryTryDecimal(vb, out var db))
                {
                    cmp = da.CompareTo(db);
                }
                else if (SkillValueBinder.TryTryDateTimeOffset(va, out var ta) && SkillValueBinder.TryTryDateTimeOffset(vb, out var tb))
                {
                    cmp = ta.CompareTo(tb);
                }
                else
                {
                    cmp = string.Compare(
                        BusinessCapabilityResponseHelpers.JsonElementToScalarString(va),
                        BusinessCapabilityResponseHelpers.JsonElementToScalarString(vb),
                        StringComparison.Ordinal);
                }

                if (cmp != 0)
                {
                    return sort.Descending ? -cmp : cmp;
                }
            }

            return 0;
        });

        return copy;
    }

    private static SkillTransformResult Failed(string code, string message)
        => new(false, code, message, default, 0);
}
