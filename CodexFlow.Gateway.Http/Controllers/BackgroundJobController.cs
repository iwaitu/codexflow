// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Gateway.Http;
using CodexFlow.Application.Jobs;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Workers;
using CodexFlow.Domain.Jobs;
using CodexFlow.Services.Background;
using Newtonsoft.Json.Linq;
using System.Security.Claims;

namespace CodexFlow.Controllers
{
    internal sealed record JobReasonResponse(string Reason);
    internal sealed record JobCreatedResponse(string JobId, string Status);
    internal sealed record JobCommandResponse(bool Success);
    internal sealed record JobNoOpResponse(bool Success, bool NoOp);
    internal sealed record JobRetryResponse(bool Success, string? FailureSummary);
    internal sealed record JobCommandFailureResponse(bool Success, string? Reason);
    internal sealed record JobRetryFailureResponse(bool Success, string Reason, string? ReasonCode);
    internal sealed record JobContinueResponse(bool Success, string Mode, string? JobId, string? NewJobId);
    internal sealed record CancelJobsResponse(bool Success, int CancelledCount);

    /// <summary>
    /// 后台任务控制 API — 提供状态查询、生命周期操作、历史追溯
    /// </summary>

    public class BackgroundJobController : GatewayEndpointHandler
    {
        private readonly IBackgroundJobService _jobService;
        private readonly IJobViewService _jobViewService;
        private readonly WorkerCoordinatorService _workerCoordinatorService;
        private readonly IJobStore _store;

        /// <summary>
        /// 初始化后台作业控制器。
        /// </summary>
        /// <param name="jobService">负责创建、取消、重试和恢复后台作业的应用服务。</param>
        /// <param name="jobViewService">负责读取作业当前视图与历史事件的查询服务。</param>
        /// <param name="workerCoordinatorService">负责向 worker coordinator 转发恢复和控制请求的服务。</param>
        /// <param name="store">用于校验会话归属关系的数据库上下文。</param>
        public BackgroundJobController(
            IBackgroundJobService jobService,
            IJobViewService jobViewService,
            WorkerCoordinatorService workerCoordinatorService,
            IJobStore store)
        {
            _jobService = jobService;
            _jobViewService = jobViewService;
            _workerCoordinatorService = workerCoordinatorService;
            _store = store;
        }

        /// <summary>
        /// Job types a client may create through this public endpoint (exact, case-sensitive match).
        /// Worker jobs (Explore/Plan/Forge/Verify and every alias <see cref="WorkerJobTypes"/> resolves) carry host paths,
        /// scope and artifact identity and may only be created by trusted Gateway dispatch, never from a raw client payload.
        /// </summary>
        private static readonly HashSet<string> ClientCreatableJobTypes = new(StringComparer.Ordinal) { "MockSimpleTask" };

        private const int MaxClientPayloadChars = 16 * 1024;
        private const int MaxClientTaskIdChars = 128;

        private string? CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier);

        private async Task<bool> OwnsSessionAsync(string? sessionId, string userId)
            => !string.IsNullOrWhiteSpace(sessionId) &&
               await _store.FindOwnedSessionAsync(sessionId, userId, HttpContext.RequestAborted) != null;

        /// <summary>Loads a job only when its session belongs to the user; unauthorized and missing jobs are indistinguishable.</summary>
        private Task<BackgroundJob?> LoadOwnedJobAsync(string jobId, string userId)
            => string.IsNullOrWhiteSpace(jobId)
                ? Task.FromResult<BackgroundJob?>(null)
                : _store.FindOwnedJobAsync(jobId, userId, HttpContext.RequestAborted);

        private static bool IsValidClientPayload(string? payloadJson)
        {
            if (payloadJson is null) return true;
            if (payloadJson.Length > MaxClientPayloadChars) return false;
            try { return JToken.Parse(payloadJson) is JObject; }
            catch (Newtonsoft.Json.JsonException) { return false; }
        }

        /// <summary>
        /// 创建一个新的后台作业，并返回队列中的作业标识。
        /// 仅限当前用户自己的会话；worker 类作业（含别名/大小写变体）一律拒绝，必须经 Gateway 可信派发。
        /// </summary>
        /// <param name="request">作业类型、所属会话以及初始负载。</param>
        /// <returns>返回新建作业的标识与初始状态。</returns>

        public async Task<IResult> CreateJob(CreateBackgroundJobRequest request)
        {
            var userId = CurrentUserId;
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            if (request is null || string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.JobType))
            {
                return ValidationProblem("sessionId and jobType are required.");
            }

            if (!await OwnsSessionAsync(request.SessionId, userId))
            {
                return NotFound(new JobReasonResponse("Session not found or not owned by current user."));
            }

            // Resolve with the same lenient parser the runner uses elsewhere (trim/case/aliases such as "forge" or
            // "ExecuteCodeTask"), then also require an exact allow-list hit: unknown strings are not "safe by default".
            if (WorkerJobTypes.IsWorkerJobType(request.JobType))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new JobReasonResponse("worker_jobs_require_gateway_dispatch"));
            }

            if (!ClientCreatableJobTypes.Contains(request.JobType))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new JobReasonResponse("job_type_not_client_creatable"));
            }

            if (!IsValidClientPayload(request.PayloadJson) ||
                (request.TaskId is { Length: > MaxClientTaskIdChars }))
            {
                return BadRequest(new JobReasonResponse("invalid_payload"));
            }

            var jobId = await _jobService.CreateJobAsync(
                request.SessionId,
                request.TaskId,
                request.JobType,
                request.PayloadJson ?? "{}");

            return Accepted(new JobCreatedResponse(jobId, "Queued"));
        }

        /// <summary>
        /// 获取单个作业的当前状态视图，优先从热视图读取，不足时回退到数据库。
        /// </summary>
        /// <param name="jobId">作业标识。</param>
        /// <returns>返回作业的当前状态、摘要与恢复信息。</returns>

        public async Task<IResult> GetStatus(string jobId)
        {
            var userId = CurrentUserId;
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
            if (await LoadOwnedJobAsync(jobId, userId) is null) return NotFound();

            var view = await _jobViewService.GetViewAsync(jobId);
            if (view == null) return NotFound();
            return Ok(view);
        }

        /// <summary>
        /// 兼容旧调用约定的作业状态查询别名。
        /// </summary>
        /// <param name="jobId">作业标识。</param>

        public Task<IResult> GetStatusAlias(string jobId) => GetStatus(jobId);

        /// <summary>
        /// 获取指定会话下的全部后台作业视图。
        /// </summary>
        /// <param name="sessionId">会话标识。</param>
        /// <returns>返回该会话下按更新时间排序的后台作业列表。</returns>

        public async Task<IResult> GetSessionJobs(string sessionId)
        {
            var userId = CurrentUserId;
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
            if (!await OwnsSessionAsync(sessionId, userId)) return NotFound();

            var jobs = await _jobViewService.GetSessionJobsAsync(sessionId);
            return Ok(jobs.ToArray());
        }

        /// <summary>
        /// 获取作业的完整事件历史，用于回放或补齐 SignalR 丢失事件。
        /// </summary>
        /// <param name="jobId">作业标识。</param>
        /// <param name="afterSeq">可选的事件序号下界，仅返回此序号之后的历史事件。</param>
        /// <returns>返回按序号升序排列的作业事件流。</returns>

        public async Task<IResult> GetHistory(string jobId, long? afterSeq = null)
        {
            var userId = CurrentUserId;
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
            if (await LoadOwnedJobAsync(jobId, userId) is null) return NotFound();

            var history = await _jobViewService.GetHistoryAsync(jobId, afterSeq);
            return Ok(history.ToArray());
        }

        /// <summary>
        /// 取消指定作业。
        /// </summary>
        /// <param name="jobId">作业标识。</param>
        /// <returns>返回取消是否成功，以及当前状态是否允许取消。</returns>

        public async Task<IResult> CancelJob(string jobId)
        {
            var userId = CurrentUserId;
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
            if (await LoadOwnedJobAsync(jobId, userId) is null) return NotFound();

            var result = await _jobService.CancelJobAsync(jobId);
            return result switch
            {
                BackgroundJobCommandResult.Success => Ok(new JobCommandResponse(true)),
                BackgroundJobCommandResult.NoOp => Ok(new JobNoOpResponse(true, true)),
                BackgroundJobCommandResult.NotFound => NotFound(),
                _ => Conflict(new JobCommandFailureResponse(false, "Job cannot be cancelled in its current state."))
            };
        }

        /// <summary>
        /// 兼容旧调用约定的作业取消别名。
        /// </summary>
        /// <param name="jobId">作业标识。</param>

        public Task<IResult> CancelJobAlias(string jobId) => CancelJob(jobId);

        /// <summary>
        /// 重试一个已经失败或可重试的作业。
        /// </summary>
        /// <param name="jobId">作业标识。</param>
        /// <returns>返回重试是否已排队，或当前状态是否允许重试。</returns>

        public async Task<IResult> RetryJob(string jobId)
        {
            var userId = CurrentUserId;
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
            if (await LoadOwnedJobAsync(jobId, userId) is null) return NotFound();

            // The retry puts the reason the job failed in front of the worker and refuses a retry that cannot change the outcome: the
            // refusal says why, in words and as a code, instead of one sentence for every case.
            var outcome = await _jobService.RetryJobDetailedAsync(jobId);
            return outcome.Result switch
            {
                BackgroundJobCommandResult.Success => Ok(new JobRetryResponse(true, outcome.FailureSummary)),
                BackgroundJobCommandResult.NotFound => NotFound(),
                BackgroundJobCommandResult.NoOp => Ok(new JobNoOpResponse(true, true)),
                _ => Conflict(new JobRetryFailureResponse(
                    false,
                    outcome.Reason ?? "Job cannot be retried in its current state.",
                    outcome.ReasonCode))
            };
        }

        /// <summary>
        /// 恢复一个等待用户输入或等待恢复指令的作业。
        /// </summary>
        /// <param name="jobId">作业标识。</param>
        /// <param name="request">恢复令牌与可选的恢复负载。</param>
        /// <returns>返回恢复是否已成功排队执行。</returns>

        public async Task<IResult> ResumeJob(string jobId, ResumeJobRequest? request)
        {
            var userId = CurrentUserId;
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
            var owned = await LoadOwnedJobAsync(jobId, userId);
            if (owned is null) return NotFound();

            // ResumeJobAsync overwrites the stored payload with whatever is passed. Replacing it would let a client rewrite
            // host paths / scope of a worker job, so only client-creatable job types may take a replacement payload.
            var payloadJson = request?.PayloadJson;
            if (payloadJson != null &&
                (WorkerJobTypes.IsWorkerJobType(owned.JobType) ||
                 !ClientCreatableJobTypes.Contains(owned.JobType) ||
                 !IsValidClientPayload(payloadJson)))
            {
                return BadRequest(new JobReasonResponse("payload_not_accepted_for_this_job"));
            }

            var result = await _jobService.ResumeJobAsync(jobId, request?.ResumeToken, payloadJson);
            return result switch
            {
                BackgroundJobCommandResult.Success => Ok(new JobCommandResponse(true)),
                BackgroundJobCommandResult.NotFound => NotFound(),
                BackgroundJobCommandResult.NoOp => Ok(new JobNoOpResponse(true, true)),
                _ => Conflict(new JobCommandFailureResponse(false, "Job cannot be resumed in its current state."))
            };
        }

        /// <summary>
        /// 继续一个已有 worker。对于 recovery-needed job 会优先原地恢复；
        /// 对于普通 failed/completed job 会按 follow-up 语义派生新 job。
        /// </summary>
        /// <param name="jobId">目标 worker 作业标识。</param>
        /// <param name="request">可选的追问内容与恢复令牌。</param>

        public async Task<IResult> ContinueWorker(string jobId, ContinueWorkerHttpRequest? request)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized();
            }

            // A job with no (or a deleted) session is not owned by anyone: deny instead of falling through.
            var job = await LoadOwnedJobAsync(jobId, userId);
            if (job == null)
            {
                return NotFound(new JobCommandFailureResponse(false, "Job not found."));
            }

            var session = job.SessionId is { } sessionId
                ? await _store.FindOwnedSessionAsync(sessionId, userId, HttpContext.RequestAborted)
                : null;
            if (session == null)
            {
                return NotFound(new JobCommandFailureResponse(false, "Job not found."));
            }

            var prompt = string.IsNullOrWhiteSpace(request?.Prompt)
                ? BuildDefaultContinuePrompt(job.ResultPayloadJson)
                : request!.Prompt!.Trim();

            var result = await _workerCoordinatorService.ContinueWorkerAsync(
                new ContinueWorkerRequest
                {
                    JobId = jobId,
                    Prompt = prompt,
                    ResumeToken = request?.ResumeToken,
                    SessionIdFallback = session.Id,
                    UserIdFallback = userId,
                    WorkspacePathFallback = session.WorkspacePath
                },
                HttpContext.RequestAborted);

            if (!result.Success)
            {
                return result.Message switch
                {
                    "worker 不存在。" => NotFound(new JobCommandFailureResponse(false, result.Message)),
                    _ => Conflict(new JobCommandFailureResponse(false, result.Message ?? "Worker cannot be continued in its current state."))
                };
            }

            return Ok(new JobContinueResponse(true, result.Mode.ToString(), result.JobId, result.NewJobId));
        }

        /// <summary>
        /// 取消指定会话下所有活跃的后台作业（需属于当前用户）
        /// </summary>

        public async Task<IResult> CancelSessionJobs(string sessionId)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            // Verify the session belongs to the current user
            if (!await OwnsSessionAsync(sessionId, userId))
                return NotFound(new JobReasonResponse("Session not found or not owned by current user."));

            var cancelled = await _jobService.CancelSessionJobsAsync(sessionId);
            return Ok(new CancelJobsResponse(true, cancelled));
        }

        /// <summary>
        /// 取消当前用户所有会话下的全部活跃后台作业
        /// </summary>

        public async Task<IResult> CancelAllMyJobs()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            var sessionIds = await _store.ListSessionIdsAsync(userId, HttpContext.RequestAborted);

            if (sessionIds.Count == 0) return Ok(new CancelJobsResponse(true, 0));

            var totalCancelled = 0;
            foreach (var sid in sessionIds)
            {
                totalCancelled += await _jobService.CancelSessionJobsAsync(sid);
            }

            return Ok(new CancelJobsResponse(true, totalCancelled));
        }

        private static string BuildDefaultContinuePrompt(string? resultPayloadJson)
        {
            if (!string.IsNullOrWhiteSpace(resultPayloadJson))
            {
                try
                {
                    var payload = JObject.Parse(resultPayloadJson);
                    var suggestedPrompt = payload["resumePlaybook"]?["suggestedPrompt"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(suggestedPrompt))
                    {
                        return suggestedPrompt.Trim();
                    }

                    var guidance = payload["resumeGuidance"]?.Value<string>()
                        ?? payload["resumePlaybook"]?["guidance"]?.Value<string>()
                        ?? payload["resumePlaybook"]?["nextAction"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(guidance))
                    {
                        return $"{guidance.Trim()}\n请不要重复上轮相同步骤，只执行一个新的最小有效步骤并返回明确结果。";
                    }

                    var recoveryReason = payload["recoveryReason"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(recoveryReason))
                    {
                        return $"请根据恢复原因 {recoveryReason.Trim()} 继续，从一个新的最小有效步骤开始，避免重复上轮动作，并返回明确结果。";
                    }
                }
                catch
                {
                    // Ignore malformed recovery payload and fall back to a generic continue prompt.
                }
            }

            return "请依据已有上下文继续任务，不要重复上轮相同步骤，只执行一个新的最小有效步骤并返回明确结果。";
        }
    }

    /// <summary>创建后台作业的请求体契约</summary>
    public class CreateBackgroundJobRequest
    {
        /// <summary>所属会话标识。</summary>
        public string SessionId { get; set; } = string.Empty;
        /// <summary>可选的任务标识，用于把后台作业关联到任务列表项。</summary>
        public string? TaskId { get; set; }
        /// <summary>后台作业类型，例如 <c>ExploreWorker</c>、<c>VerifyWorker</c>。</summary>
        public string JobType { get; set; } = string.Empty;
        /// <summary>传递给后台作业执行器的 JSON 负载。</summary>
        public string? PayloadJson { get; set; }
    }

    /// <summary>恢复作业的请求体契约</summary>
    public class ResumeJobRequest
    {
        /// <summary>WaitingUser 场景下必须提供的恢复令牌</summary>
        public string? ResumeToken { get; set; }

        /// <summary>恢复时传入的 JSON 负载（可选）</summary>
        public string? PayloadJson { get; set; }
    }

    /// <summary>继续 worker 的请求体契约</summary>
    public class ContinueWorkerHttpRequest
    {
        /// <summary>可选的追问内容；缺省时会自动从 recovery playbook 推导。</summary>
        public string? Prompt { get; set; }

        /// <summary>WaitingUser 场景下可选传入的恢复令牌。</summary>
        public string? ResumeToken { get; set; }
    }
}
