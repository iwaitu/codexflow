// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Gateway;

namespace CodexFlow.Gateway.Contracts;

/// <summary>
/// Canonical JSON Schema generator for Gateway SSE events (P3-9).
/// Clients use <c>sse-events.schema.json</c> alongside <c>openapi.json</c> to generate strict TypeScript unions
/// and validate backward compatibility via <see cref="SchemaVersion"/>.
/// </summary>
public static class SseEventContractSchema
{
    public const string SchemaVersion = "1.1.0";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public static string BuildSchemaJson()
    {
        var eventDefs = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [GatewaySseEventType.Heartbeat] = EventDef(
                "Periodic heartbeat and connection handshake.",
                Props(
                    ("sessionId", Str()),
                    ("status", StrEnum("connected", "alive")),
                    ("subscriberCount", Int()),
                    ("epoch", OptionalStr()),
                    ("intervalSeconds", OptionalInt()),
                    ("pendingApprovals", Int())),
                required: ["sessionId", "status", "subscriberCount", "pendingApprovals"]),
            [GatewaySseEventType.Content] = EventDef(
                "Assistant visible response text delta.",
                Props(("type", Const("content")), ("content", Str()), ("text", Str())),
                required: ["type", "content", "text"]),
            [GatewaySseEventType.ThinkingStart] = EventDef(
                "Marks start of assistant reasoning block.",
                Props(("type", Const("thinking_start"))),
                required: ["type"]),
            [GatewaySseEventType.ThinkingContent] = EventDef(
                "Assistant reasoning text delta.",
                Props(("type", Const("thinking_content")), ("content", Str()), ("text", Str())),
                required: ["type", "content", "text"]),
            [GatewaySseEventType.ThinkingEnd] = EventDef(
                "Marks end of assistant reasoning block.",
                Props(("type", Const("thinking_end")), ("thinking_content", Str())),
                required: ["type", "thinking_content"]),
            [GatewaySseEventType.ToolCall] = EventDef(
                "Model requested a tool invocation (arguments sanitized and bounded).",
                Props(
                    ("type", Const("tool_call")),
                    ("name", Str()),
                    ("callId", OptionalStr()),
                    ("arguments", NullableObj()),
                    ("argumentsTruncated", Bool())),
                required: ["type", "name"]),
            [GatewaySseEventType.ToolStarted] = EventDef(
                "Tool execution started on the host.",
                Props(("type", Const("tool_started")), ("name", Str()), ("callId", OptionalStr())),
                required: ["type", "name"]),
            [GatewaySseEventType.ToolOutput] = EventDef(
                "Throttled stdout/stderr stream chunk from an executing command tool.",
                Props(
                    ("tool", Str()),
                    ("commandTaskId", Str()),
                    ("stdout", OptionalStr()),
                    ("stderr", OptionalStr()),
                    ("throttled", Bool()),
                    ("suppressedLines", OptionalInt())),
                required: ["tool", "commandTaskId", "throttled"]),
            [GatewaySseEventType.ToolApprovalRequest] = EventDef(
                "Interactive approval request before starting untrusted code execution.",
                Props(
                    ("approvalId", Str()),
                    ("requestDigest", Str()),
                    ("sessionId", Str()),
                    ("tool", Str()),
                    ("executable", Str()),
                    ("interpreterChain", StrArray()),
                    ("argv", StrArray()),
                    ("cwd", Str()),
                    ("stdinSha256", NullableStr()),
                    ("codeSha256", NullableStr()),
                    ("scriptSha256", NullableStr()),
                    ("backend", Str()),
                    ("network", Str()),
                    ("filesystem", Str()),
                    ("reason", Str()),
                    ("expiresAt", Str()),
                    ("allowedDecisions", StrArray()),
                    ("executesWorkspaceCode", Bool()),
                    ("trustedOriginVerified", Bool()),
                    ("selfApprovalProtection", Bool()),
                    ("origin", OptionalStr()),
                    ("policyVersion", OptionalInt()),
                    ("wrapperSha256", OptionalStr()),
                    ("exportScopeSha256", OptionalStr()),
                    ("timeoutSeconds", OptionalInt()),
                    ("imageDigest", OptionalStr()),
                    ("shellKind", OptionalStr()),
                    ("shell", OptionalStr()),
                    ("scriptPreview", OptionalStr()),
                    ("scriptLength", OptionalInt()),
                    ("backendNotice", OptionalStr())),
                required:
                [
                    "approvalId", "requestDigest", "sessionId", "tool", "executable", "interpreterChain",
                    "argv", "cwd", "backend", "network", "filesystem", "reason", "expiresAt",
                    "allowedDecisions", "executesWorkspaceCode", "trustedOriginVerified", "selfApprovalProtection"
                ]),
            [GatewaySseEventType.ToolApprovalResolved] = EventDef(
                "Resolution outcome for a pending tool approval request.",
                Props(
                    ("approvalId", Str()),
                    ("requestDigest", Str()),
                    ("sessionId", Str()),
                    ("decision", Str()),
                    ("decidedBy", NullableStr()),
                    ("note", NullableStr()),
                    ("resolvedAt", Str())),
                required: ["approvalId", "requestDigest", "sessionId", "decision", "resolvedAt"]),
            [GatewaySseEventType.TaskStatus] = EventDef(
                "Tool completion or task-status progress update.",
                Props(
                    ("type", Str()),
                    ("name", OptionalStr()),
                    ("callId", OptionalStr()),
                    ("success", OptionalBool()),
                    ("result", OptionalStr())),
                required: ["type"]),
            [GatewaySseEventType.SetConversationId] = EventDef(
                "Binds the active conversation identifier for this turn.",
                Props(("conversationId", Str())),
                required: ["conversationId"]),
            [GatewaySseEventType.UpdateConversationTitle] = EventDef(
                "Updates the conversation display title.",
                Props(("conversationId", Str()), ("title", Str())),
                required: ["conversationId", "title"]),
            [GatewaySseEventType.SystemMessage] = EventDef(
                "Host system notification message.",
                Props(("type", OptionalStr()), ("noticeType", OptionalStr()), ("content", Str()), ("text", OptionalStr())),
                required: ["content"]),
            [GatewaySseEventType.NextTaskStarted] = EventDef(
                "Background plan task dispatch notification.",
                Props(
                    ("taskId", Str()),
                    ("title", Str()),
                    ("jobId", Str()),
                    ("jobType", Str()),
                    ("workerType", OptionalStr()),
                    ("status", NullableStr())),
                required: ["taskId", "title", "jobId", "jobType"]),
            [GatewaySseEventType.RetryNotice] = EventDef(
                "Task failure automatic retry scheduled.",
                Props(("taskId", Str()), ("retryCount", Int()), ("maxRetries", Int()), ("reason", OptionalStr())),
                required: ["taskId", "retryCount", "maxRetries"]),
            [GatewaySseEventType.PlanChangeRequest] = EventDef(
                "Plan requires adjustment after exhausted retries or failure.",
                Props(("taskId", Str()), ("failureCount", Int()), ("maxRetries", Int()), ("canRetry", Bool()), ("lastError", NullableStr())),
                required: ["taskId", "failureCount", "maxRetries", "canRetry"]),
            [GatewaySseEventType.PlanApprovalRequested] = EventDef(
                "Plan artifact transitioned to ReadyForApproval and awaits user confirmation.",
                Props(
                    ("type", Const("plan_approval_requested")),
                    ("planArtifactId", Str()),
                    ("version", Str()),
                    ("markdownHash", Str()),
                    ("goal", OptionalStr()),
                    ("status", Str()),
                    ("taskCount", Int())),
                required: ["type", "planArtifactId", "version", "markdownHash", "status", "taskCount"]),
            [GatewaySseEventType.PlanningSummary] = EventDef(
                "Planning summary notification.",
                Props(("summary", OptionalStr()), ("content", OptionalStr())),
                required: [],
                anyOfRequired: [["summary"], ["content"]]),
            [GatewaySseEventType.WorkerStarted] = EventDef(
                "Background worker started.",
                Props(("type", Const("worker_started")), ("content", OptionalStr()), ("text", OptionalStr())),
                required: ["type"]),
            [GatewaySseEventType.WorkerWaitingUser] = EventDef(
                "Background worker waiting for user input or approval.",
                Props(("type", Const("worker_waiting_user")), ("content", OptionalStr()), ("text", OptionalStr())),
                required: ["type"]),
            [GatewaySseEventType.WorkerCompleted] = EventDef(
                "Background worker completed.",
                Props(("type", Const("worker_completed")), ("content", OptionalStr()), ("text", OptionalStr())),
                required: ["type"]),
            [GatewaySseEventType.WorkerFailed] = EventDef(
                "Background worker failed.",
                Props(("type", Const("worker_failed")), ("content", OptionalStr()), ("text", OptionalStr())),
                required: ["type"]),
            [GatewaySseEventType.FileTreeUpdated] = EventDef(
                "Workspace file tree changed by a tool.",
                Props(("tool", Str())),
                required: ["tool"]),
            [GatewaySseEventType.Error] = EventDef(
                "Turn or runtime error event.",
                Props(("messageId", NullableStr()), ("error", Str()), ("message", OptionalStr()), ("detail", OptionalStr())),
                required: ["error"]),
            [GatewaySseEventType.Done] = EventDef(
                "Turn terminal completion marker (source may be UserChat, cancelled, etc.).",
                Props(("type", OptionalStr()), ("messageId", OptionalStr()), ("source", OptionalStr())),
                required: [],
                anyOfRequired: [["type"], ["messageId", "source"]]),
            [GatewaySseEventType.ReplayGap] = EventDef(
                "Indicates missed events on reconnect or slow-subscriber overflow; client must reconcile via snapshot.",
                Props(
                    ("reason", StrEnum(
                        GatewayReplayGapReason.BufferEvicted,
                        GatewayReplayGapReason.SubscriberOverflow,
                        GatewayReplayGapReason.SequenceReset)),
                    ("epoch", OptionalStr()),
                    ("fromSeq", OptionalInt()),
                    ("toSeq", OptionalInt()),
                    ("previousLastEventId", OptionalInt()),
                    ("oldestAvailableSeq", OptionalInt())),
                required: ["reason"]),
            [GatewaySseEventType.BusinessSkillRunUpdated] = EventDef(
                "Business skill run status transition notification.",
                Props(
                    ("runId", Str()),
                    ("skillId", Str()),
                    ("skillVersion", Int()),
                    ("status", Str()),
                    ("stopRequested", Bool()),
                    ("updatedAtUtc", Str())),
                required: ["runId", "skillId", "skillVersion", "status", "stopRequested", "updatedAtUtc"]),
            [GatewaySseEventType.BusinessSkillApprovalRequested] = EventDef(
                "Interactive approval request before dispatching a business write operation.",
                Props(
                    ("approvalId", Str()),
                    ("requestDigest", Str()),
                    ("sessionId", Str()),
                    ("runId", Str()),
                    ("stepId", Str()),
                    ("kind", Str()),
                    ("allowedDecisions", StrArray()),
                    ("expiresAtUtc", Str())),
                required: ["approvalId", "requestDigest", "sessionId", "runId", "stepId", "kind", "allowedDecisions", "expiresAtUtc"]),
            [GatewaySseEventType.BusinessSkillApprovalResolved] = EventDef(
                "Resolution outcome for a pending business write approval request.",
                Props(
                    ("approvalId", Str()),
                    ("requestDigest", Str()),
                    ("runId", Str()),
                    ("decision", Str()),
                    ("decidedBy", NullableStr()),
                    ("resolvedAt", Str())),
                required: ["approvalId", "requestDigest", "runId", "decision", "resolvedAt"]),
            [GatewaySseEventType.PlanModeChanged] = EventDef(
                "Notification emitted when session Plan Mode is entered or exited.",
                Props(
                    ("type", Const(GatewaySseEventType.PlanModeChanged)),
                    ("sessionId", Str()),
                    ("workMode", OptionalStr()),
                    ("modeVersion", OptionalInt()),
                    ("planModeActive", Bool()),
                    ("planModeVersion", Int()),
                    ("changedBy", NullableStr()),
                    ("changedAtUtc", NullableStr()),
                    ("reason", NullableStr()),
                    ("objective", NullableStr())),
                required: ["sessionId", "planModeActive", "planModeVersion"]),
            [GatewaySseEventType.PlanModeExitRequested] = EventDef(
                "Model requested exit from Plan Mode; requires explicit user confirmation on control plane.",
                Props(
                    ("type", Const(GatewaySseEventType.PlanModeExitRequested)),
                    ("sessionId", Str()),
                    ("planModeVersion", Int()),
                    ("planSummary", Str()),
                    ("reason", NullableStr())),
                required: ["sessionId", "planModeVersion", "planSummary"]),
            [GatewaySseEventType.BrowserStateUpdated] = EventDef(
                "Throttled snapshot of the session's managed browser state (URL, title, tabs, screenshotSeq, recent actions/console).",
                Props(
                    ("sessionId", Str()),
                    ("status", Str()),
                    ("headless", Bool()),
                    ("profileMode", Str()),
                    ("url", NullableStr()),
                    ("title", NullableStr()),
                    ("screenshotSeq", Int()),
                    ("consoleErrorCount", Int()),
                    ("updatedAtUtc", NullableStr())),
                required: ["sessionId", "status", "headless", "profileMode", "screenshotSeq", "consoleErrorCount"]),
            [GatewaySseEventType.BrowserApprovalRequested] = EventDef(
                "Interactive control-plane approval request for a sensitive browser action or non-allowlisted dev loopback port.",
                Props(
                    ("approvalId", Str()),
                    ("requestDigest", Str()),
                    ("sessionId", Str()),
                    ("tool", Str()),
                    ("action", Str()),
                    ("targetUrl", Str()),
                    ("targetOrigin", Str()),
                    ("targetDescription", Str()),
                    ("riskReason", Str()),
                    ("expressionOrPath", NullableStr()),
                    ("expiresAtUtc", Str()),
                    ("allowedDecisions", StrArray()),
                    ("controlPlaneAvailable", Bool()),
                    ("selfApprovalProtection", Bool())),
                required:
                [
                    "approvalId", "requestDigest", "sessionId", "tool", "action",
                    "targetUrl", "targetOrigin", "targetDescription", "riskReason",
                    "expiresAtUtc", "allowedDecisions", "controlPlaneAvailable", "selfApprovalProtection"
                ]),
            [GatewaySseEventType.BrowserApprovalResolved] = EventDef(
                "Resolution outcome for a pending browser approval request.",
                Props(
                    ("approvalId", Str()),
                    ("sessionId", Str()),
                    ("decision", Str()),
                    ("decidedBy", NullableStr()),
                    ("note", NullableStr()),
                    ("resolvedAtUtc", Str())),
                required: ["approvalId", "sessionId", "decision", "resolvedAtUtc"])
        };

        var root = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = "https://codexflow.local/schemas/sse-events.schema.json",
            ["title"] = "CodexFlow Gateway SSE Event Schema",
            ["schemaVersion"] = SchemaVersion,
            ["type"] = "object",
            ["required"] = new[] { "event", "data" },
            ["properties"] = new Dictionary<string, object>
            {
                ["id"] = new { type = "integer", minimum = 0 },
                ["event"] = new { type = "string", @enum = eventDefs.Keys.ToArray() },
                ["data"] = new { type = "object" }
            },
            ["oneOf"] = eventDefs.Keys.Select(name => new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["properties"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["event"] = new { @const = name },
                    ["data"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["$ref"] = $"#/$defs/{name}"
                    }
                },
                ["required"] = new[] { "event", "data" }
            }).ToArray(),
            ["$defs"] = eventDefs
        };

        return JsonSerializer.Serialize(root, SerializerOptions);
    }

    private static Dictionary<string, object> EventDef(
        string description,
        Dictionary<string, object> properties,
        string[] required,
        string[][]? anyOfRequired = null)
    {
        var def = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["description"] = description,
            ["additionalProperties"] = true,
            ["properties"] = properties,
            ["required"] = required
        };

        if (anyOfRequired is { Length: > 0 })
        {
            def["anyOf"] = anyOfRequired
                .Select(group => new Dictionary<string, object>(StringComparer.Ordinal) { ["required"] = group })
                .ToArray();
        }

        return def;
    }

    private static Dictionary<string, object> Props(params (string Name, object Schema)[] entries)
    {
        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, schema) in entries)
        {
            dict[name] = schema;
        }

        return dict;
    }

    private static object Str() => new { type = "string" };
    private static object OptionalStr() => new { type = new[] { "string", "null" } };
    private static object NullableStr() => new { type = new[] { "string", "null" } };
    private static object StrEnum(params string[] values) => new { type = "string", @enum = values };
    private static object Const(string value) => new { type = "string", @const = value };
    private static object Int() => new { type = "integer" };
    private static object OptionalInt() => new { type = new[] { "integer", "null" } };
    private static object Bool() => new { type = "boolean" };
    private static object OptionalBool() => new { type = new[] { "boolean", "null" } };
    private static object StrArray() => new { type = "array", items = new { type = "string" } };
    private static object NullableObj() => new { type = new[] { "object", "null" }, additionalProperties = true };
}
