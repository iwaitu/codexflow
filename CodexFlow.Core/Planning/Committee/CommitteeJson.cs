// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.SelfCorrection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Planning.Committee;

internal static class CommitteeJson
{
    internal static ReviewerFeedback? ReadReviewerFeedback(string text) => ReadRoot(text, reader =>
    {
        var feedback = new ReviewerFeedback();
        return ReadProperties(reader, (name, value) =>
        {
            switch (name)
            {
                case "status": feedback.Status = value.ReadAsString()!; break;
                case "summary": feedback.Summary = value.ReadAsString()!; break;
                case "blocking_items": feedback.BlockingItems = ReadStrings(value, feedback.BlockingItems)!; break;
                case "non_blocking_items": feedback.NonBlockingItems = ReadStrings(value, feedback.NonBlockingItems)!; break;
                case "resolved_previous_items": feedback.ResolvedPreviousItems = ReadStrings(value, feedback.ResolvedPreviousItems)!; break;
                case "issue_statuses": feedback.IssueStatuses = ReadObjectArray(value, ReadIssueStatus, feedback.IssueStatuses)!; break;
                default: SkipProperty(value); break;
            }
        }) ? feedback : null;
    });

    internal static ModeratorDecision? ReadModeratorDecision(string text) => ReadRoot(text, reader =>
    {
        var decision = new ModeratorDecision();
        return ReadProperties(reader, (name, value) =>
        {
            switch (name)
            {
                case "decision": decision.Decision = value.ReadAsString()!; break;
                case "reason": decision.Reason = value.ReadAsString()!; break;
                case "gpt_feedback": decision.GptFeedback = value.ReadAsString()!; break;
                case "unresolved_items": decision.UnresolvedItems = ReadStrings(value, decision.UnresolvedItems)!; break;
                case "updated_plan": decision.UpdatedPlan = value.ReadAsString()!; break;
                default: SkipProperty(value); break;
            }
        }) ? decision : null;
    });

    internal static List<ProjectedTask>? ReadProjectedTasks(string text)
        => ReadRoot(text, reader => ReadObjectArray(reader, ReadProjectedTask));

    internal static List<ProjectedTask>? ReadProjectedTasks(JArray array)
    {
        using var reader = array.CreateReader();
        return ReadObjectArray(reader, ReadProjectedTask);
    }

    internal static ComplexityClassification ReadComplexity(string text)
    {
        var result = new ComplexityClassification { Reason = "LLM 未返回判定理由" };
        ReadRoot(text, reader => ReadProperties(reader, (name, value) =>
        {
            switch (name)
            {
                case "is_complex":
                    result.IsComplex = value.ReadAsBoolean()
                        ?? throw new JsonSerializationException("is_complex cannot be null.");
                    break;
                case "reason": result.Reason = value.ReadAsString() ?? "LLM 未返回判定理由"; break;
                case "indicators": result.TriggeredIndicators = ReadStrings(value) ?? []; break;
                default: SkipProperty(value); break;
            }
        }));
        return result;
    }

    internal static JToken ReviewerFeedbackToken(ReviewerFeedback? feedback) => feedback is null
        ? JValue.CreateNull()
        : new JObject
        {
            ["status"] = feedback.Status,
            ["summary"] = feedback.Summary,
            ["blocking_items"] = StringsToken(feedback.BlockingItems),
            ["non_blocking_items"] = StringsToken(feedback.NonBlockingItems),
            ["resolved_previous_items"] = StringsToken(feedback.ResolvedPreviousItems),
            ["issue_statuses"] = feedback.IssueStatuses is null
                ? JValue.CreateNull()
                : new JArray(feedback.IssueStatuses.Select(IssueStatusToken))
        };

    internal static JToken ModeratorDecisionToken(ModeratorDecision? decision) => decision is null
        ? JValue.CreateNull()
        : new JObject
        {
            ["decision"] = decision.Decision,
            ["reason"] = decision.Reason,
            ["gpt_feedback"] = decision.GptFeedback,
            ["unresolved_items"] = StringsToken(decision.UnresolvedItems),
            ["updated_plan"] = decision.UpdatedPlan
        };

    internal static JToken StringsToken(IEnumerable<string>? values)
        => values is null ? JValue.CreateNull() : new JArray(values);

    internal static JArray LintIssuesToken(IEnumerable<PlanContractLintIssue> issues)
        => new(issues.Select(issue => new JObject
        {
            ["Severity"] = (int)issue.Severity,
            ["Code"] = issue.Code,
            ["Message"] = issue.Message,
            ["TaskId"] = issue.TaskId
        }));

    private static JToken IssueStatusToken(IssueStatus? issue) => issue is null
        ? JValue.CreateNull()
        : new JObject
        {
            ["item"] = issue.Item,
            ["status"] = issue.Status,
            ["note"] = issue.Note
        };

    private static IssueStatus? ReadIssueStatus(JsonReader reader)
    {
        var issue = new IssueStatus();
        return ReadProperties(reader, (name, value) =>
        {
            switch (name)
            {
                case "item": issue.Item = value.ReadAsString()!; break;
                case "status": issue.Status = value.ReadAsString()!; break;
                case "note": issue.Note = value.ReadAsString()!; break;
                default: SkipProperty(value); break;
            }
        }) ? issue : null;
    }

    private static ProjectedTask? ReadProjectedTask(JsonReader reader)
    {
        var task = new ProjectedTask();
        return ReadProperties(reader, (name, value) =>
        {
            switch (name)
            {
                case "id": task.Id = value.ReadAsString()!; break;
                case "title": task.Title = value.ReadAsString()!; break;
                case "description": task.Description = value.ReadAsString()!; break;
                case "task_type": task.TaskType = value.ReadAsString()!; break;
                case "dependencies": task.Dependencies = ReadStrings(value, task.Dependencies)!; break;
                case "risk_level": task.RiskLevel = value.ReadAsString()!; break;
                case "complexity_level":
                    task.ComplexityLevel = value.ReadAsInt32()
                        ?? throw new JsonSerializationException("complexity_level cannot be null.");
                    break;
                case "verification_steps": task.VerificationSteps = ReadStrings(value, task.VerificationSteps)!; break;
                case "required_artifacts":
                    task.RequiredArtifacts = ReadObjectArray(value, ReadArtifactAssertion, task.RequiredArtifacts)!;
                    break;
                case "forbidden_states":
                    task.ForbiddenStates = ReadObjectArray(value, ReadArtifactAssertion, task.ForbiddenStates)!;
                    break;
                default: SkipProperty(value); break;
            }
        }) ? task : null;
    }

    private static ArtifactAssertion? ReadArtifactAssertion(JsonReader reader)
    {
        string? type = null;
        string? path = null;
        string? text = null;
        return ReadProperties(reader, (name, value) =>
        {
            switch (name)
            {
                case "type": type = value.ReadAsString(); break;
                case "path": path = value.ReadAsString(); break;
                case "text": text = value.ReadAsString(); break;
                default: SkipProperty(value); break;
            }
        }) ? new ArtifactAssertion(type!, path!, text) : null;
    }

    private static T ReadRoot<T>(string text, Func<JsonReader, T> read)
    {
        using var reader = new JsonTextReader(new StringReader(text));
        var result = read(reader);
        if (ReadContent(reader))
        {
            throw new JsonSerializationException("Additional content after the committee JSON value.");
        }

        return result;
    }

    // Typed reads preserve Json.NET's conversions, including numeric strings and raw scalar text.
    private static bool ReadProperties(JsonReader reader, Action<string, JsonReader> readProperty)
    {
        if (reader.TokenType is JsonToken.None or JsonToken.PropertyName && !ReadContent(reader))
        {
            return false;
        }

        if (IsNullObject(reader))
        {
            return false;
        }

        if (reader.TokenType != JsonToken.StartObject)
        {
            throw new JsonSerializationException("Expected a committee JSON object.");
        }

        while (ReadContent(reader))
        {
            if (reader.TokenType == JsonToken.EndObject)
            {
                return true;
            }

            if (reader.TokenType != JsonToken.PropertyName || reader.Value is not string name)
            {
                throw new JsonSerializationException("Expected a committee JSON property.");
            }

            readProperty(name.ToLowerInvariant(), reader);
        }

        throw new JsonSerializationException("Unexpected end of committee JSON object.");
    }

    private static List<T>? ReadObjectArray<T>(JsonReader reader, Func<JsonReader, T?> readItem, List<T>? values = null)
        where T : class
    {
        if (!ReadContent(reader) || IsNullObject(reader))
        {
            return null;
        }

        RequireArray(reader);
        var items = values ?? [];
        while (ReadContent(reader))
        {
            if (reader.TokenType == JsonToken.EndArray)
            {
                return items;
            }

            items.Add(readItem(reader)!);
        }

        throw new JsonSerializationException("Unexpected end of committee JSON array.");
    }

    private static List<string>? ReadStrings(JsonReader reader, List<string>? values = null)
    {
        if (!ReadContent(reader) || IsNullObject(reader))
        {
            return null;
        }

        RequireArray(reader);
        var items = values ?? [];
        while (true)
        {
            var item = reader.ReadAsString();
            if (reader.TokenType == JsonToken.EndArray)
            {
                return items;
            }

            if (reader.TokenType == JsonToken.None)
            {
                throw new JsonSerializationException("Unexpected end of committee JSON array.");
            }

            items.Add(item!);
        }
    }

    private static bool IsNullObject(JsonReader reader)
        => reader.TokenType is JsonToken.Null or JsonToken.Undefined ||
           reader.TokenType == JsonToken.String && reader.Value is "";

    private static void RequireArray(JsonReader reader)
    {
        if (reader.TokenType != JsonToken.StartArray)
        {
            throw new JsonSerializationException("Expected a committee JSON array.");
        }
    }

    private static void SkipProperty(JsonReader reader) => reader.Skip();

    private static bool ReadContent(JsonReader reader)
    {
        while (reader.Read())
        {
            if (reader.TokenType != JsonToken.Comment)
            {
                return true;
            }
        }

        return false;
    }
}
