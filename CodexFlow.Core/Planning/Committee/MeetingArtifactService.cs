// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Planning.Committee;

/// <summary>
/// 会议工件落盘服务。
/// 负责创建会议目录、写入各阶段文件、更新 meeting.json。
/// </summary>
public class MeetingArtifactService
{
    private readonly ILogger _logger;

    public MeetingArtifactService(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 创建会议目录并初始化基础工件。
    /// </summary>
    public string InitializeMeetingDirectory(string workspacePath, string logRoot, CommitteeMeetingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var logDir = Path.Combine(workspacePath, logRoot);
        Directory.CreateDirectory(logDir);

        var meetingDirName = $"meeting-{state.StartedAt:yyyyMMdd}-{state.StartedAt:HHmmss}-{Environment.ProcessId}";
        var meetingDir = Path.Combine(logDir, meetingDirName);
        Directory.CreateDirectory(meetingDir);

        var runtimeDir = Path.Combine(meetingDir, CommitteeConstants.RuntimeDir);
        Directory.CreateDirectory(runtimeDir);

        state.MeetingDirectory = meetingDir;

        // 初始化 meeting.json
        WriteMeetingJson(state);

        // 初始化 transcript.md
        var transcriptPath = Path.Combine(meetingDir, CommitteeConstants.TranscriptFileName);
        File.WriteAllText(transcriptPath, $"# 委员会会议记录\n\n- 会议 ID: {state.MeetingId}\n- 目标: {state.Goal}\n- 开始时间: {state.StartedAt:O}\n\n");

        // 初始化 review_history.json
        var historyPath = Path.Combine(meetingDir, CommitteeConstants.RuntimeDir, CommitteeConstants.ReviewHistoryFileName);
        File.WriteAllText(historyPath, "[]");

        _logger.LogInformation("会议目录已创建: {MeetingDir}", meetingDir);
        return meetingDir;
    }

    /// <summary>
    /// 写入初始蓝图。
    /// </summary>
    public void WriteInitialBlueprint(CommitteeMeetingState state, string blueprint)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.InitialBlueprintFileName);
        File.WriteAllText(path, blueprint);

        WriteCurrentPlan(state, blueprint);
    }

    /// <summary>
    /// 写入当前方案到 runtime/current_plan.md。
    /// </summary>
    public void WriteCurrentPlan(CommitteeMeetingState state, string plan)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.RuntimeDir, CommitteeConstants.CurrentPlanFileName);
        File.WriteAllText(path, plan);
        state.CurrentPlan = plan;
    }

    /// <summary>
    /// 写入某一轮评审前的方案快照。
    /// </summary>
    public void WriteRoundPlanSnapshot(CommitteeMeetingState state, int round)
    {
        ArgumentNullException.ThrowIfNull(state);

        var fileName = $"round-{round:D2}-plan-before-review.md";
        var path = Path.Combine(state.MeetingDirectory, fileName);
        File.WriteAllText(path, state.CurrentPlan);
    }

    /// <summary>
    /// 写入分析师评审反馈。
    /// </summary>
    public void WriteAnalystFeedback(CommitteeMeetingState state, int round, ReviewerFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(state);

        var fileName = $"round-{round:D2}-analyst-feedback.md";
        var path = Path.Combine(state.MeetingDirectory, fileName);
        File.WriteAllText(path, CommitteeJson.ReviewerFeedbackToken(feedback).ToString(Formatting.Indented));
    }

    /// <summary>
    /// 写入架构师评审反馈。
    /// </summary>
    public void WriteArchitectFeedback(CommitteeMeetingState state, int round, ReviewerFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(state);

        var fileName = $"round-{round:D2}-architect-feedback.md";
        var path = Path.Combine(state.MeetingDirectory, fileName);
        File.WriteAllText(path, CommitteeJson.ReviewerFeedbackToken(feedback).ToString(Formatting.Indented));
    }

    /// <summary>
    /// 写入项目经理裁决。
    /// </summary>
    public void WriteModeratorDecision(CommitteeMeetingState state, int round, ModeratorDecision decision)
    {
        ArgumentNullException.ThrowIfNull(state);

        var fileName = $"round-{round:D2}-project-manager-feedback.md";
        var path = Path.Combine(state.MeetingDirectory, fileName);
        File.WriteAllText(path, CommitteeJson.ModeratorDecisionToken(decision).ToString(Formatting.Indented));
    }

    /// <summary>
    /// 写入最终蓝图。
    /// </summary>
    public void WriteFinalPlan(CommitteeMeetingState state, string finalPlan)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.FinalPlanFileName);
        File.WriteAllText(path, finalPlan);
    }

    /// <summary>
    /// 更新 review_history.json。
    /// </summary>
    public void UpdateReviewHistory(CommitteeMeetingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.RuntimeDir, CommitteeConstants.ReviewHistoryFileName);
        var history = state.Rounds is null
            ? (JToken)JValue.CreateNull()
            : new JArray(state.Rounds.Select(round => round is null
                ? (JToken)JValue.CreateNull()
                : new JObject
                {
                    ["RoundNumber"] = round.RoundNumber,
                    ["AnalystFeedback"] = CommitteeJson.ReviewerFeedbackToken(round.AnalystFeedback),
                    ["ArchitectFeedback"] = CommitteeJson.ReviewerFeedbackToken(round.ArchitectFeedback),
                    ["ModeratorDecision"] = CommitteeJson.ModeratorDecisionToken(round.ModeratorDecision)
                }));
        File.WriteAllText(path, history.ToString(Formatting.Indented));
    }

    /// <summary>
    /// 追加 transcript。
    /// </summary>
    public void AppendTranscript(CommitteeMeetingState state, string entry)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.TranscriptFileName);
        File.AppendAllText(path, entry + "\n\n");
    }

    /// <summary>
    /// 更新 meeting.json 状态，包含轮次详情、事件和工件索引。
    /// </summary>
    public void WriteMeetingJson(CommitteeMeetingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var meetingData = new JObject
        {
            ["meeting_id"] = state.MeetingId,
            ["goal"] = state.Goal,
            ["target_dir"] = state.TargetDir,
            ["source_file"] = state.SourceFile,
            ["started_at"] = state.StartedAt,
            ["ended_at"] = state.EndedAt,
            ["status"] = state.Status.ToString().ToLowerInvariant(),
            ["current_round"] = state.CurrentRound,
            ["max_rounds"] = state.MaxRounds,
            ["total_rounds"] = state.Rounds.Count,
            ["rounds"] = new JArray(state.Rounds.Select(round => new JObject
            {
                ["round"] = round.RoundNumber,
                ["analyst"] = ReviewerSummaryToken(round.AnalystFeedback),
                ["architect"] = ReviewerSummaryToken(round.ArchitectFeedback),
                ["moderator"] = round.ModeratorDecision is { } decision ? new JObject
                {
                    ["Decision"] = decision.Decision,
                    ["Reason"] = decision.Reason,
                    ["unresolved_count"] = decision.UnresolvedItems.Count
                } : null
            })),
            ["events"] = BuildEventList(state),
            ["artifacts"] = BuildArtifactIndex(state)
        };

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.MeetingJsonFileName);
        File.WriteAllText(path, meetingData.ToString(Formatting.Indented));
    }

    private static JObject? ReviewerSummaryToken(ReviewerFeedback? feedback) => feedback is null
        ? null
        : new JObject
        {
            ["Status"] = feedback.Status,
            ["Summary"] = feedback.Summary,
            ["blocking_count"] = feedback.BlockingItems.Count
        };

    /// <summary>
    /// 构建结构化事件列表。
    /// </summary>
    private static JArray BuildEventList(CommitteeMeetingState state)
    {
        var events = new JArray
        {
            EventToken("meeting_started", state.StartedAt, state.Goal)
        };

        foreach (var round in state.Rounds)
        {
            events.Add(EventToken("round_started", null, $"Round {round.RoundNumber}"));

            if (round.AnalystFeedback != null)
                events.Add(EventToken("analyst_review", null, round.AnalystFeedback.Status));
            if (round.ArchitectFeedback != null)
                events.Add(EventToken("architect_review", null, round.ArchitectFeedback.Status));
            if (round.ModeratorDecision != null)
                events.Add(EventToken("moderator_decision", null, round.ModeratorDecision.Decision));
        }

        if (state.EndedAt.HasValue)
        {
            events.Add(EventToken("meeting_ended", state.EndedAt.Value, state.Status.ToString().ToLowerInvariant()));
        }

        return events;
    }

    private static JObject EventToken(string type, DateTime? timestamp, string? detail) => new()
    {
        ["type"] = type,
        ["timestamp"] = timestamp,
        ["detail"] = detail
    };

    /// <summary>
    /// 构建会议工件索引。
    /// </summary>
    private static JArray BuildArtifactIndex(CommitteeMeetingState state)
    {
        var artifacts = new JArray();

        if (!string.IsNullOrEmpty(state.MeetingDirectory) && Directory.Exists(state.MeetingDirectory))
        {
            // 索引会议目录中的所有文件
            foreach (var file in Directory.GetFiles(state.MeetingDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(state.MeetingDirectory, file);
                artifacts.Add(new JObject
                {
                    ["path"] = relativePath,
                    ["type"] = ClassifyArtifact(relativePath)
                });
            }
        }

        return artifacts;
    }

    private static string ClassifyArtifact(string relativePath)
    {
        if (relativePath.Contains("analyst", StringComparison.OrdinalIgnoreCase)) return "analyst_feedback";
        if (relativePath.Contains("architect", StringComparison.OrdinalIgnoreCase)) return "architect_feedback";
        if (relativePath.Contains("project-manager", StringComparison.OrdinalIgnoreCase)) return "moderator_decision";
        if (relativePath.Contains("plan-before-review", StringComparison.OrdinalIgnoreCase)) return "round_plan_snapshot";
        if (relativePath.Contains("initial_blueprint", StringComparison.OrdinalIgnoreCase)) return "initial_blueprint";
        if (relativePath.Contains("final_plan", StringComparison.OrdinalIgnoreCase)) return "final_plan";
        if (relativePath.Contains("transcript", StringComparison.OrdinalIgnoreCase)) return "transcript";
        if (relativePath.Contains("review_history", StringComparison.OrdinalIgnoreCase)) return "review_history";
        if (relativePath.Contains("shadow-plan-diff", StringComparison.OrdinalIgnoreCase)) return "shadow_diff";
        if (relativePath.Contains("shadow-metrics", StringComparison.OrdinalIgnoreCase)) return "shadow_metrics";
        if (relativePath.Contains("meeting.json", StringComparison.OrdinalIgnoreCase)) return "meeting_metadata";
        return "other";
    }

    /// <summary>
    /// 写入 shadow-plan-diff.json。
    /// </summary>
    public void WriteShadowPlanDiff(CommitteeMeetingState state, JObject? diffData)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.ShadowPlanDiffFileName);
        File.WriteAllText(path, diffData?.ToString(Formatting.Indented) ?? "null");
    }

    /// <summary>
    /// 写入 shadow-metrics.json，记录灰度决策所需的对比指标。
    /// </summary>
    public void WriteShadowMetrics(
        CommitteeMeetingState state,
        List<CodexTask> baselinePlan,
        List<CodexTask>? committeePlan,
        CommitteePlanningResult committeeResult)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(baselinePlan);
        ArgumentNullException.ThrowIfNull(committeeResult);

        var metrics = new JObject
        {
            ["generated_at"] = DateTime.UtcNow,
            ["meeting_id"] = !string.IsNullOrWhiteSpace(committeeResult.MeetingId)
                ? committeeResult.MeetingId
                : state.MeetingId,
            ["committee_status"] = committeeResult.Status.ToString(),
            ["committee_total_rounds"] = committeeResult.TotalRounds,
            ["committee_unresolved_count"] = committeeResult.UnresolvedItems.Count,
            ["baseline"] = PlanMetricsToken(baselinePlan),
            ["committee"] = committeePlan is null ? null : PlanMetricsToken(committeePlan),
            ["projection_succeeded"] = committeePlan is { Count: > 0 }
        };

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.ShadowMetricsFileName);
        File.WriteAllText(path, metrics.ToString(Formatting.Indented));

        _logger.LogInformation("Shadow 指标已写入: {Path}", path);
    }

    private static JObject PlanMetricsToken(List<CodexTask> plan) => new()
    {
        ["task_count"] = plan.Count,
        ["high_risk_count"] = plan.Count(task => string.Equals(task.RiskLevel, "High", StringComparison.OrdinalIgnoreCase)),
        ["avg_complexity"] = plan.Count > 0 ? plan.Average(task => task.ComplexityLevel) : 0
    };
}
