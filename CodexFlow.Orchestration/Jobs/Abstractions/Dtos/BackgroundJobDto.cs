// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Application.Jobs.Dtos
{
    /// <summary>
    /// 单条验证证据。
    /// </summary>
    public sealed class BackgroundJobVerificationEvidenceDto
    {
        /// <summary>验证检查项标识或名称。</summary>
        public string Check { get; set; } = string.Empty;
        /// <summary>该证据对应的检查是否通过。</summary>
        public bool Passed { get; set; }
        /// <summary>生成该证据时执行的命令。</summary>
        public string? Command { get; set; }
        /// <summary>命令退出码。</summary>
        public string? ExitCode { get; set; }
        /// <summary>对命令结果的观察结论。</summary>
        public string? Observation { get; set; }
    }

    /// <summary>
    /// 验证清单中的单项。
    /// </summary>
    public sealed class BackgroundJobVerificationChecklistItemDto
    {
        /// <summary>清单项名称。</summary>
        public string Check { get; set; } = string.Empty;
        /// <summary>该清单项关联的证据行。</summary>
        public string[] Evidence { get; set; } = [];
    }

    /// <summary>
    /// 结构化验证清单。
    /// </summary>
    public sealed class BackgroundJobVerificationChecklistDto
    {
        /// <summary>已完成且有证据支撑的检查项。</summary>
        public BackgroundJobVerificationChecklistItemDto[] CompletedItems { get; set; } = [];
        /// <summary>已失败的检查项。</summary>
        public BackgroundJobVerificationChecklistItemDto[] FailedItems { get; set; } = [];
        /// <summary>仍待补齐证据或继续处理的检查项。</summary>
        public BackgroundJobVerificationChecklistItemDto[] PendingItems { get; set; } = [];
        /// <summary>建议优先处理的 focus items。</summary>
        public string[] FocusItems { get; set; } = [];
    }

    /// <summary>
    /// verify worker 的结构化验证结果。
    /// </summary>
    public sealed class BackgroundJobVerificationDto
    {
        /// <summary>验证 verdict，例如 <c>pass</c>、<c>fail</c>、<c>partial</c>。</summary>
        public string Verdict { get; set; } = string.Empty;
        /// <summary>是否整体通过验证。</summary>
        public bool Passed { get; set; }
        /// <summary>验证摘要。</summary>
        public string? Summary { get; set; }
        /// <summary>验证 issues 列表。</summary>
        public string[] Issues { get; set; } = [];
        /// <summary>结构化证据列表。</summary>
        public BackgroundJobVerificationEvidenceDto[] Evidence { get; set; } = [];
        /// <summary>验证清单及重点项。</summary>
        public BackgroundJobVerificationChecklistDto? Checklist { get; set; }
    }

    /// <summary>
    /// 失败作业的结构化原因：失败码、种类、能改变结果的补救方式，以及守卫拒绝的文件等证据。
    /// </summary>
    public class BackgroundJobFailureDto
    {
        /// <summary>宿主给出的失败码；没有时为 unknown。</summary>
        public string Code { get; set; } = "unknown";
        /// <summary>失败种类：transient / quality / scope_boundary / host_decision / infrastructure / unknown。</summary>
        public string Kind { get; set; } = "unknown";
        /// <summary>怎么才能改变结果：retry_as_is / retry_with_guidance / change_plan_or_scope / ask_user / fix_environment。</summary>
        public string Remedy { get; set; } = "retry_with_guidance";
        /// <summary>重试（原样或带指导）是否可能得到不同的结果。</summary>
        public bool Retryable { get; set; }
        /// <summary>失败原因（有长度上限）。</summary>
        public string Message { get; set; } = string.Empty;
        /// <summary>范围守卫拒绝过的文件。</summary>
        public string[] RefusedPaths { get; set; } = [];
        /// <summary>其他证据：依赖没到位的包、工作者自己的说明等。</summary>
        public string[] Evidence { get; set; } = [];
    }

    /// <summary>
    /// 后台作业当前视图 DTO。
    /// </summary>
    public class BackgroundJobDto
    {
        /// <summary>作业标识。</summary>
        public string JobId { get; set; } = string.Empty;
        /// <summary>所属会话标识。</summary>
        public string? SessionId { get; set; }
        /// <summary>关联的任务标识。</summary>
        public string? TaskId { get; set; }
        /// <summary>当前运行该作业的 runner 标识。</summary>
        public string? RunnerId { get; set; }
        /// <summary>后台作业类型。</summary>
        public string JobType { get; set; } = string.Empty;
        /// <summary>后台作业状态。</summary>
        public string Status { get; set; } = string.Empty;
        /// <summary>最新事件序号。</summary>
        public long LatestSeq { get; set; }
        /// <summary>最新可展示消息。</summary>
        public string? LatestMessage { get; set; }
        /// <summary>归一化后的 worker 类型。</summary>
        public string? WorkerType { get; set; }
        /// <summary>面向产品层的细粒度状态分类。</summary>
        public string? StateKind { get; set; }
        /// <summary>当前作业摘要。</summary>
        public string? Summary { get; set; }
        /// <summary>是否处于等待用户输入状态。</summary>
        public bool WaitingUser { get; set; }
        /// <summary>等待用户输入的原因。</summary>
        public string? WaitingReason { get; set; }
        /// <summary>恢复等待态作业所需的 resume token。</summary>
        public string? ResumeToken { get; set; }
        /// <summary>是否需要恢复后再继续执行。</summary>
        public bool RecoveryNeeded { get; set; }
        /// <summary>恢复原因。</summary>
        public string? RecoveryReason { get; set; }
        /// <summary>建议的恢复策略。</summary>
        public string? ResumeStrategy { get; set; }
        /// <summary>恢复提示或操作建议。</summary>
        public string? ResumeGuidance { get; set; }
        /// <summary>结构化验证结果，仅 verify worker 通常会填充。</summary>
        public BackgroundJobVerificationDto? Verification { get; set; }
        /// <summary>作业失败时的结构化原因；其他状态为 null。</summary>
        public BackgroundJobFailureDto? Failure { get; set; }
        /// <summary>进度值，当前主要为兼容字段。</summary>
        public double Progress { get; set; }
        /// <summary>最后更新时间。</summary>
        public DateTime UpdatedAtUtc { get; set; }
    }
}
