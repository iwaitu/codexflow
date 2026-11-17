// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexFlow.Contracts.BusinessSkills;

namespace CodexFlow.Core.BusinessSkills;

/// <summary>
/// 确定性结构化值绑定、条件求值与受限模板解析器（严禁 eval 或任意脚本执行）。
/// </summary>
public static partial class SkillValueBinder
{
    [GeneratedRegex(@"\{\{([a-zA-Z0-9_.\-]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex BoundedPlaceholderRegex();

    public static bool TryValidateRunInputs(
        SkillInputSchemaDto schema,
        IReadOnlyDictionary<string, JsonElement> suppliedInputs,
        out IReadOnlyDictionary<string, JsonElement> resolvedInputs,
        out List<BusinessSkillFieldError> errors)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(suppliedInputs);

        errors = [];
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var kv in suppliedInputs)
        {
            merged[kv.Key] = kv.Value.Clone();
        }

        foreach (var prop in schema.Properties)
        {
            if (!merged.ContainsKey(prop.Key) && prop.Value.DefaultValue.HasValue)
            {
                merged[prop.Key] = prop.Value.DefaultValue.Value.Clone();
            }
        }

        foreach (var req in schema.Required)
        {
            if (!merged.TryGetValue(req, out var val) ||
                val.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                (val.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(val.GetString())))
            {
                errors.Add(new($"inputs.{req}", BusinessSkillErrorCodes.ValidationFailed,
                    $"Required input '{req}' is missing or empty."));
            }
        }

        foreach (var prop in schema.Properties)
        {
            if (!merged.TryGetValue(prop.Key, out var val) || val.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (!MatchesDeclaredType(val, prop.Value, out var reason))
            {
                errors.Add(new($"inputs.{prop.Key}", BusinessSkillErrorCodes.ValidationFailed, reason));
            }
        }

        resolvedInputs = merged;
        return errors.Count == 0;
    }

    public static bool TryResolveBinding(
        SkillValueBinding? binding,
        IReadOnlyDictionary<string, JsonElement> inputs,
        IReadOnlyDictionary<string, JsonElement> stepOutputs,
        JsonElement? loopItem,
        out JsonElement value,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(stepOutputs);

        value = default;
        errorMessage = null;

        if (binding is null)
        {
            errorMessage = "Value binding cannot be null.";
            return false;
        }

        if (binding.Literal.HasValue)
        {
            value = binding.Literal.Value.Clone();
            return true;
        }

        if (!string.IsNullOrWhiteSpace(binding.Input))
        {
            if (!inputs.TryGetValue(binding.Input, out var inputVal))
            {
                errorMessage = $"Input '{binding.Input}' is not provided.";
                return false;
            }

            if (!string.IsNullOrEmpty(binding.Pointer))
            {
                if (!BusinessCapabilityResponseHelpers.TryEvaluateJsonPointer(inputVal, binding.Pointer, out var inner))
                {
                    errorMessage = $"Input '{binding.Input}' does not contain pointer '{binding.Pointer}'.";
                    return false;
                }

                value = inner.Clone();
                return true;
            }

            value = inputVal.Clone();
            return true;
        }

        if (!string.IsNullOrWhiteSpace(binding.Item))
        {
            if (!loopItem.HasValue)
            {
                errorMessage = $"Item binding '{binding.Item}' used outside an active loop iteration.";
                return false;
            }

            if (!BusinessCapabilityResponseHelpers.TryEvaluateJsonPointer(loopItem.Value, binding.Pointer, out var itemProp))
            {
                errorMessage = $"Loop item missing pointer '{binding.Pointer}'.";
                return false;
            }

            value = itemProp.Clone();
            return true;
        }

        if (!string.IsNullOrWhiteSpace(binding.Step))
        {
            if (!stepOutputs.TryGetValue(binding.Step, out var stepRoot))
            {
                errorMessage = $"Referenced step '{binding.Step}' has not produced an output.";
                return false;
            }

            if (!BusinessCapabilityResponseHelpers.TryEvaluateJsonPointer(stepRoot, binding.Pointer, out var stepTarget))
            {
                errorMessage = $"Step '{binding.Step}' output missing pointer '{binding.Pointer}'.";
                return false;
            }

            value = stepTarget.Clone();
            return true;
        }

        errorMessage = "Binding must specify literal, input, item, or step.";
        return false;
    }

    public static bool TryEvaluateCondition(
        SkillConditionExpr expr,
        JsonElement? rowElement,
        IReadOnlyDictionary<string, JsonElement> inputs,
        IReadOnlyDictionary<string, JsonElement> stepOutputs,
        JsonElement? loopItem,
        out bool result,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(expr);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(stepOutputs);
        result = false;
        errorMessage = null;

        if (expr.Clauses.Count == 0)
        {
            result = true;
            return true;
        }

        var isOr = string.Equals(expr.Combinator, "or", StringComparison.OrdinalIgnoreCase);
        var combined = !isOr;

        foreach (var clause in expr.Clauses)
        {
            if (!TryResolveFieldFromScope(clause.Field, rowElement, stepOutputs, inputs, out var leftVal))
            {
                // 行中缺失字段时，仅 eq/ne 允许明确按 missing 处理，其余比较拒绝隐式强转为 true
                if (clause.Operator.Equals("eq", StringComparison.OrdinalIgnoreCase) &&
                    clause.Value.Literal?.ValueKind == JsonValueKind.Null)
                {
                    if (isOr)
                    {
                        result = true;
                        return true;
                    }

                    continue;
                }

                if (isOr)
                {
                    continue;
                }

                result = false;
                return true;
            }

            if (!TryResolveBinding(clause.Value, inputs, stepOutputs, loopItem, out var rightVal, out var bindErr))
            {
                errorMessage = $"Condition right-hand operand failed for field '{clause.Field}': {bindErr}";
                return false;
            }

            if (!TryCompare(leftVal, rightVal, clause.Operator, out var matched, out var cmpErr))
            {
                errorMessage = $"Condition evaluation failed for field '{clause.Field}': {cmpErr}";
                return false;
            }

            if (isOr && matched)
            {
                result = true;
                return true;
            }

            if (!isOr && !matched)
            {
                result = false;
                return true;
            }

            combined = isOr ? (combined || matched) : (combined && matched);
        }

        result = combined;
        return true;
    }

    public static string RenderBoundedTemplate(
        string template,
        IReadOnlyDictionary<string, JsonElement> inputs,
        IReadOnlyDictionary<string, JsonElement> stepOutputs,
        JsonElement? loopItem)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(stepOutputs);

        return BoundedPlaceholderRegex().Replace(template, match =>
        {
            var token = match.Groups[1].Value;
            var dotIdx = token.IndexOf('.', StringComparison.Ordinal);
            if (dotIdx <= 0 || dotIdx >= token.Length - 1)
            {
                return string.Empty;
            }

            var rootKey = token[..dotIdx];
            var propPath = token[(dotIdx + 1)..];
            var pointer = "/" + propPath.Replace('.', '/');

            if (string.Equals(rootKey, "inputs", StringComparison.Ordinal) ||
                string.Equals(rootKey, "input", StringComparison.Ordinal))
            {
                var inputParts = propPath.Split('.', 2, StringSplitOptions.RemoveEmptyEntries);
                if (inputs.TryGetValue(inputParts[0], out var inputEl))
                {
                    if (inputParts.Length == 1)
                    {
                        return BusinessCapabilityResponseHelpers.JsonElementToScalarString(inputEl);
                    }

                    if (BusinessCapabilityResponseHelpers.TryEvaluateJsonPointer(inputEl, "/" + inputParts[1].Replace('.', '/'), out var sub))
                    {
                        return BusinessCapabilityResponseHelpers.JsonElementToScalarString(sub);
                    }
                }

                return string.Empty;
            }

            if (string.Equals(rootKey, "item", StringComparison.Ordinal) && loopItem.HasValue)
            {
                return BusinessCapabilityResponseHelpers.TryEvaluateJsonPointer(loopItem.Value, pointer, out var itemVal)
                    ? BusinessCapabilityResponseHelpers.JsonElementToScalarString(itemVal)
                    : string.Empty;
            }

            if (stepOutputs.TryGetValue(rootKey, out var stepVal) &&
                BusinessCapabilityResponseHelpers.TryEvaluateJsonPointer(stepVal, pointer, out var stepProp))
            {
                return BusinessCapabilityResponseHelpers.JsonElementToScalarString(stepProp);
            }

            return string.Empty;
        });
    }

    private static bool TryResolveFieldFromScope(
        string field,
        JsonElement? rowElement,
        IReadOnlyDictionary<string, JsonElement> stepOutputs,
        IReadOnlyDictionary<string, JsonElement> inputs,
        out JsonElement value)
    {
        value = default;

        if (rowElement.HasValue)
        {
            if (field.StartsWith('/'))
            {
                if (BusinessCapabilityResponseHelpers.TryEvaluateJsonPointer(rowElement.Value, field, out var found))
                {
                    value = found;
                    return true;
                }
            }
            else if (rowElement.Value.ValueKind == JsonValueKind.Object &&
                     rowElement.Value.TryGetProperty(field, out var direct))
            {
                value = direct;
                return true;
            }
        }

        // Branch 步允许使用 stepId.pointer 或 input.name
        if (field.StartsWith("input.", StringComparison.Ordinal) && inputs.TryGetValue(field["input.".Length..], out var inputVal))
        {
            value = inputVal;
            return true;
        }

        var dotIdx = field.IndexOf('.', StringComparison.Ordinal);
        if (dotIdx > 0 && dotIdx < field.Length - 1)
        {
            var stepId = field[..dotIdx];
            var ptr = "/" + field[(dotIdx + 1)..].Replace('.', '/');
            if (stepOutputs.TryGetValue(stepId, out var stepRoot) &&
                BusinessCapabilityResponseHelpers.TryEvaluateJsonPointer(stepRoot, ptr, out var found))
            {
                value = found;
                return true;
            }
        }

        return false;
    }

    private static bool TryCompare(
        JsonElement left,
        JsonElement right,
        string op,
        out bool matched,
        out string? errorMessage)
    {
        matched = false;
        errorMessage = null;
        var normOp = op.Trim().ToLowerInvariant();

        if (normOp == "in")
        {
            if (right.ValueKind != JsonValueKind.Array)
            {
                errorMessage = "Operator 'in' requires an array right-hand value.";
                return false;
            }

            var leftStr = BusinessCapabilityResponseHelpers.JsonElementToScalarString(left);
            foreach (var candidate in right.EnumerateArray())
            {
                if (TryTryDecimal(left, out var ld) && TryTryDecimal(candidate, out var rd))
                {
                    if (ld == rd)
                    {
                        matched = true;
                        return true;
                    }
                }
                else if (string.Equals(leftStr, BusinessCapabilityResponseHelpers.JsonElementToScalarString(candidate), StringComparison.Ordinal))
                {
                    matched = true;
                    return true;
                }
            }

            return true;
        }

        if (normOp == "contains")
        {
            if (left.ValueKind == JsonValueKind.Array)
            {
                var rightScalar = BusinessCapabilityResponseHelpers.JsonElementToScalarString(right);
                foreach (var item in left.EnumerateArray())
                {
                    if (string.Equals(BusinessCapabilityResponseHelpers.JsonElementToScalarString(item), rightScalar, StringComparison.Ordinal))
                    {
                        matched = true;
                        return true;
                    }
                }

                return true;
            }

            var ls = BusinessCapabilityResponseHelpers.JsonElementToScalarString(left);
            var rs = BusinessCapabilityResponseHelpers.JsonElementToScalarString(right);
            matched = ls.Contains(rs, StringComparison.Ordinal);
            return true;
        }

        if (normOp is "eq" or "ne")
        {
            var equal = AreEqual(left, right);
            matched = normOp == "eq" ? equal : !equal;
            return true;
        }

        if (normOp is "gt" or "gte" or "lt" or "lte")
        {
            if (TryTryDecimal(left, out var lDec) && TryTryDecimal(right, out var rDec))
            {
                var cmp = lDec.CompareTo(rDec);
                matched = normOp switch
                {
                    "gt" => cmp > 0,
                    "gte" => cmp >= 0,
                    "lt" => cmp < 0,
                    _ => cmp <= 0
                };
                return true;
            }

            if (TryTryDateTimeOffset(left, out var lDt) && TryTryDateTimeOffset(right, out var rDt))
            {
                var cmp = lDt.CompareTo(rDt);
                matched = normOp switch
                {
                    "gt" => cmp > 0,
                    "gte" => cmp >= 0,
                    "lt" => cmp < 0,
                    _ => cmp <= 0
                };
                return true;
            }

            errorMessage = $"Incompatible or non-numeric/non-date operands for '{op}' ({left.ValueKind} vs {right.ValueKind}).";
            return false;
        }

        errorMessage = $"Unsupported operator '{op}'.";
        return false;
    }

    internal static bool AreEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Null && right.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (left.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            right.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return left.GetBoolean() == right.GetBoolean();
        }

        if (TryTryDecimal(left, out var ld) && TryTryDecimal(right, out var rd))
        {
            return ld == rd;
        }

        if (left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String)
        {
            return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
        }

        return string.Equals(
            BusinessCapabilityResponseHelpers.JsonElementToScalarString(left),
            BusinessCapabilityResponseHelpers.JsonElementToScalarString(right),
            StringComparison.Ordinal);
    }

    internal static bool TryTryDecimal(JsonElement el, out decimal result)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out result))
        {
            return true;
        }

        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out result))
        {
            return true;
        }

        result = 0m;
        return false;
    }

    internal static bool TryTryDateTimeOffset(JsonElement el, out DateTimeOffset result)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var raw = el.GetString();
            if (!string.IsNullOrWhiteSpace(raw) &&
                DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result))
            {
                return true;
            }
        }

        result = default;
        return false;
    }

    private static bool MatchesDeclaredType(JsonElement value, SkillInputPropertyDto spec, out string message)
    {
        message = string.Empty;
        switch (spec.Type.Trim().ToLowerInvariant())
        {
            case "string":
                if (value.ValueKind != JsonValueKind.String)
                {
                    message = $"Expected string value, got {value.ValueKind}.";
                    return false;
                }

                var s = value.GetString() ?? string.Empty;
                if (spec.EnumValues is { Count: > 0 } && !spec.EnumValues.Contains(s, StringComparer.Ordinal))
                {
                    message = $"Value '{s}' is not in allowed enum values.";
                    return false;
                }

                if (string.Equals(spec.Format, "date", StringComparison.OrdinalIgnoreCase) &&
                    !DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    message = $"Date value '{s}' must use YYYY-MM-DD format.";
                    return false;
                }

                if (string.Equals(spec.Format, "date-time", StringComparison.OrdinalIgnoreCase) &&
                    !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                {
                    message = $"DateTime value '{s}' is not a valid ISO-8601 timestamp.";
                    return false;
                }

                return true;
            case "integer":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _))
                {
                    message = $"Expected integer value, got {value.ValueKind}.";
                    return false;
                }

                return true;
            case "number":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out _))
                {
                    message = $"Expected decimal/number value, got {value.ValueKind}.";
                    return false;
                }

                return true;
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    message = $"Expected boolean value, got {value.ValueKind}.";
                    return false;
                }

                return true;
            case "array":
                if (value.ValueKind != JsonValueKind.Array)
                {
                    message = $"Expected array value, got {value.ValueKind}.";
                    return false;
                }

                return true;
            case "object":
                if (value.ValueKind != JsonValueKind.Object)
                {
                    message = $"Expected object value, got {value.ValueKind}.";
                    return false;
                }

                return true;
            default:
                return true;
        }
    }
}
