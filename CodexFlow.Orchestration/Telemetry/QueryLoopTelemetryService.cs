// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace CodexFlow.Telemetry;

/// <summary>
/// Records structured runtime telemetry for query loop execution, recovery, and termination.
/// </summary>
public sealed class QueryLoopTelemetryService : IQueryLoopTelemetry
{
    /// <summary>Meter name used for query runtime OpenTelemetry instruments.</summary>
    public const string MeterName = "CodexFlow.QueryRuntime";

    private readonly ILogger<QueryLoopTelemetryService> _logger;

    // Counters
    private readonly Counter<long> _queryRunsTotal;
    private readonly Counter<long> _queryRoundsTotal;
    private readonly Counter<long> _zeroToolCallRoundsTotal;
    private readonly Counter<long> _malformedProtocolTotal;
    private readonly Counter<long> _emptyResponseTotal;
    private readonly Counter<long> _recoveryTriggeredTotal;

    // Histograms
    private readonly Histogram<double> _queryDurationMs;
    private readonly Histogram<int> _queryTotalRounds;
    private readonly Histogram<int> _promptTokens;
    private readonly Histogram<int> _completionTokens;
    private readonly Histogram<int> _cachedPromptTokens;

    /// <summary>
    /// Initializes the query loop telemetry service.
    /// </summary>
    /// <param name="logger">Logger used for structured query loop event logging.</param>
    /// <param name="meterFactory">Meter factory used to create telemetry instruments.</param>
    public QueryLoopTelemetryService(ILogger<QueryLoopTelemetryService> logger, IMeterFactory meterFactory)
    {
        _logger = logger;

        var meter = meterFactory.Create(MeterName);

        _queryRunsTotal = meter.CreateCounter<long>("query_runs_total", description: "Total query loop runs started");
        _queryRoundsTotal = meter.CreateCounter<long>("query_rounds_total", description: "Total query rounds completed");
        _zeroToolCallRoundsTotal = meter.CreateCounter<long>("zero_tool_call_rounds_total", description: "Rounds with zero tool calls");
        _malformedProtocolTotal = meter.CreateCounter<long>("malformed_protocol_total", description: "Malformed tool protocol events");
        _emptyResponseTotal = meter.CreateCounter<long>("empty_response_total", description: "Empty LLM response events");
        _recoveryTriggeredTotal = meter.CreateCounter<long>("recovery_triggered_total", description: "Recovery policy trigger events");

        _queryDurationMs = meter.CreateHistogram<double>("query_duration_ms", unit: "ms", description: "Total query loop duration");
        _queryTotalRounds = meter.CreateHistogram<int>("query_total_rounds", description: "Total rounds per query run");
        _promptTokens = meter.CreateHistogram<int>("prompt_tokens", description: "Prompt tokens per query run");
        _completionTokens = meter.CreateHistogram<int>("completion_tokens", description: "Completion tokens per query run");
        _cachedPromptTokens = meter.CreateHistogram<int>("cached_prompt_tokens", description: "Prompt tokens served from the provider prompt cache per query run");
    }

    /// <summary>
    /// Records the start of a query loop run.
    /// </summary>
    /// <param name="evt">Structured start event payload.</param>
    public void RecordStart(QueryLoopStarted evt)
    {
        var ep = evt.EntryPoint.ToString();
        _queryRunsTotal.Add(1, new TagList { { "entry_point", ep } });

        _logger.LogInformation("QUERYLOOP|{Json}", Serialize(new
        {
            event_type = "query_loop_started",
            query_id = evt.QueryId,
            session_id = evt.SessionId,
            entry_point = ep,
            started_at = evt.StartedAt,
            max_rounds = evt.MaxRounds,
            initial_context_chars = evt.InitialContextChars
        }));
    }

    /// <summary>
    /// Records completion of a single query loop round.
    /// </summary>
    /// <param name="evt">Structured round-completed event payload.</param>
    public void RecordRound(QueryLoopRoundCompleted evt)
    {
        var ep = evt.EntryPoint.ToString();
        _queryRoundsTotal.Add(1, new TagList { { "entry_point", ep } });

        if (evt.ToolCallCount == 0)
            _zeroToolCallRoundsTotal.Add(1, new TagList { { "entry_point", ep } });

        _logger.LogInformation("QUERYLOOP|{Json}", Serialize(new
        {
            event_type = "query_loop_round_completed",
            query_id = evt.QueryId,
            session_id = evt.SessionId,
            entry_point = ep,
            round = evt.Round,
            tool_call_count = evt.ToolCallCount,
            has_text = evt.HasText,
            text_length = evt.TextLength,
            thinking_length = evt.ThinkingLength,
            context_chars = evt.ContextChars,
            duration_ms = evt.DurationMs,
            continue_reason = evt.ContinueReason,
            prompt_tokens = evt.PromptTokens,
            completion_tokens = evt.CompletionTokens
        }));
    }

    /// <summary>
    /// Records termination of a query loop run and updates aggregate histograms.
    /// </summary>
    /// <param name="evt">Structured termination event payload.</param>
    public void RecordTermination(QueryLoopTerminated evt)
    {
        var ep = evt.EntryPoint.ToString();
        var reason = evt.TerminationReason.ToString();
        var tags = new TagList { { "entry_point", ep }, { "termination_reason", reason } };
        if (!string.IsNullOrWhiteSpace(evt.DetailCode))
        {
            tags.Add("detail_code", evt.DetailCode);
        }

        _queryDurationMs.Record(evt.TotalDurationMs, tags);
        _queryTotalRounds.Record(evt.TotalRounds, tags);

        if (evt.TotalPromptTokens.HasValue)
            _promptTokens.Record(evt.TotalPromptTokens.Value, tags);
        if (evt.TotalCompletionTokens.HasValue)
            _completionTokens.Record(evt.TotalCompletionTokens.Value, tags);
        if (evt.TotalCachedPromptTokens.HasValue)
            _cachedPromptTokens.Record(evt.TotalCachedPromptTokens.Value, tags);

        _logger.LogInformation("QUERYLOOP|{Json}", Serialize(new
        {
            event_type = "query_loop_terminated",
            query_id = evt.QueryId,
            session_id = evt.SessionId,
            entry_point = ep,
            termination_reason = reason,
            detail_code = evt.DetailCode,
            total_rounds = evt.TotalRounds,
            total_tool_calls = evt.TotalToolCalls,
            zero_tool_call_rounds = evt.ZeroToolCallRounds,
            malformed_protocol_count = evt.MalformedProtocolCount,
            empty_response_count = evt.EmptyResponseCount,
            recovery_count = evt.RecoveryCount,
            total_duration_ms = evt.TotalDurationMs,
            total_prompt_tokens = evt.TotalPromptTokens,
            total_cached_prompt_tokens = evt.TotalCachedPromptTokens,
            total_completion_tokens = evt.TotalCompletionTokens
        }));
    }

    /// <summary>
    /// Records recovery policy activity for a query loop run.
    /// </summary>
    /// <param name="evt">Structured recovery event payload.</param>
    public void RecordRecovery(QueryLoopRecovery evt)
    {
        var ep = evt.EntryPoint.ToString();
        _recoveryTriggeredTotal.Add(1, new TagList { { "entry_point", ep }, { "recovery_type", evt.RecoveryType } });

        if (evt.RecoveryType == "malformed_protocol")
            _malformedProtocolTotal.Add(1, new TagList { { "entry_point", ep } });
        else if (evt.RecoveryType == "empty_response")
            _emptyResponseTotal.Add(1, new TagList { { "entry_point", ep } });

        _logger.LogInformation("QUERYLOOP|{Json}", Serialize(new
        {
            event_type = "query_loop_recovery",
            query_id = evt.QueryId,
            session_id = evt.SessionId,
            entry_point = ep,
            round = evt.Round,
            recovery_type = evt.RecoveryType,
            attempt = evt.Attempt,
            continued = evt.Continued,
            terminal = evt.Terminal
        }));
    }

    private static string Serialize(object obj) => JsonConvert.SerializeObject(obj);
}
