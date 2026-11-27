// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Services;
using CodexFlow.Domain.Jobs;
using CodexFlow.Orchestration.Jobs;
using CodexFlow.Services.Background;
using CodexFlow.Services.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

public sealed class ForgeRepositoryOwnershipRetryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retry_RequiresAnAuthorizedReadableRepository_ThenRequeuesTheSameJob(bool ownershipFailure)
    {
        var workspace = IsolationTestSupport.NewTempDir("ownership-retry");
        try
        {
            var shadowPath = GitService.BuildShadowPath(workspace, "T1");
            var failureCode = ownershipFailure ? JobFailureClassifier.RepositoryOwnershipCode : $"WorkspaceUnavailable: {shadowPath}";
            var job = new BackgroundJob
            {
                JobId = "ownership-job",
                JobType = "ForgeWorker",
                TaskId = "T1",
                SessionId = "session",
                Status = BackgroundJobStatus.Failed,
                PayloadJson = new JObject
                {
                    ["sessionId"] = "session",
                    ["userId"] = "user",
                    ["mainWorkspacePath"] = workspace,
                    ["workspacePath"] = ownershipFailure ? workspace : shadowPath,
                    ["lastShadowWorktreePath"] = ownershipFailure ? null : shadowPath,
                    ["worktreeRetained"] = false,
                    ["worktreeCleanedAtUtc"] = DateTime.UtcNow,
                    ["prompt"] = "Original task instructions."
                }.ToString(),
                ResultPayloadJson = new JObject
                {
                    ["detailCode"] = failureCode,
                    ["message"] = ownershipFailure ? "Git rejected the repository ownership." : "Worker workspace was unavailable."
                }.ToString()
            };

            var store = new Mock<IJobStore>();
            store.Setup(s => s.FindAsync(job.JobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
            JobTransition? queued = null;
            store.Setup(s => s.ApplyTransitionsAsync(It.IsAny<IReadOnlyList<JobTransition>>(), It.IsAny<CancellationToken>()))
                .Callback<IReadOnlyList<JobTransition>, CancellationToken>((transitions, _) => queued = Assert.Single(transitions))
                .ReturnsAsync(1);

            var sessions = new Mock<ISessionDirectory>();
            var sessionOwner = "user";
            sessions.Setup(s => s.FindAsync("session", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new SessionRecord("session", sessionOwner, workspace));
            var service = new BackgroundJobService(
                store.Object, Mock.Of<IOutboxSignal>(), Mock.Of<IBackgroundJobSignal>(), Mock.Of<IJobHotView>(),
                Mock.Of<IKeyValueTextStore>(), Mock.Of<IJobEventLog>(), NullLogger<BackgroundJobService>.Instance,
                sessions.Object, new ConfigurationBuilder().Build());

            var projectedFailure = WorkerProjectionHelpers.ResolveFailure("Failed", null, job.ResultPayloadJson);
            Assert.True(projectedFailure!.Retryable);
            Assert.Equal("fix_environment", projectedFailure.Remedy);
            var notification = new Dictionary<string, string>();
            JobFailureNotificationMeta.Apply(notification, job);
            var notifiedFailure = JobFailureNotificationMeta.Read(notification);
            Assert.Equal(failureCode, notifiedFailure?.Code);
            Assert.Equal(JobFailureRemedy.FixEnvironment, notifiedFailure?.Remedy);

            var expectedConflict = ownershipFailure ? "repository_ownership_not_recovered" : "workspace_not_recovered";
            var unavailable = await service.RetryJobDetailedAsync(job.JobId);
            Assert.Equal(expectedConflict, unavailable.ReasonCode);
            Assert.Null(queued);
            Assert.Equal(0, job.Attempt);

            var git = new GitService(NullLogger<GitService>.Instance);
            Assert.True(await git.InitRepositoryAsync(workspace));
            sessionOwner = "different-user";
            var unauthorized = await service.RetryJobDetailedAsync(job.JobId);
            Assert.Equal(expectedConflict, unauthorized.ReasonCode);
            Assert.Null(queued);

            sessionOwner = "user";
            var recovered = await service.RetryJobDetailedAsync(job.JobId);
            Assert.Equal(BackgroundJobCommandResult.Success, recovered.Result);
            Assert.NotNull(queued);
            Assert.Equal(JobTransitionKind.Retry, queued.Kind);
            Assert.Equal("ownership-job", queued.UpdatedJob.JobId);
            Assert.Equal(BackgroundJobStatus.Failed, queued.ExpectedStatus);
            Assert.Equal(BackgroundJobStatus.Queued, queued.UpdatedJob.Status);
            Assert.Equal(1, queued.UpdatedJob.Attempt);
            var payload = JObject.Parse(queued.UpdatedJob.PayloadJson);
            Assert.StartsWith("Original task instructions.", payload.Value<string>("prompt"));
            Assert.Contains("工作区可被 Git 读取", payload.Value<string>("prompt"));
            Assert.Equal(failureCode, payload["failureHistory"]?[0]?.Value<string>("detailCode"));
            Assert.Equal(workspace, payload.Value<string>("workspacePath"));

            try
            {
                var shadow = await git.CreateShadowWorktreeAsync(workspace, "T1", "main");
                Assert.False(string.IsNullOrWhiteSpace(shadow));
                Assert.True(File.Exists(Path.Combine(shadow, ".gitignore")));
            }
            finally
            {
                await git.RemoveShadowWorktreeAsync(workspace, "T1");
            }
        }
        finally
        {
            IsolationTestSupport.Cleanup(workspace);
        }
    }
}
