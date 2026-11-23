// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodexFlow.Application;
using CodexFlow.Application.Jobs;
using CodexFlow.Application.Jobs.Dtos;
using CodexFlow.Contracts.Browser;
using CodexFlow.Controllers;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.ComputerUse;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Core.Services;
using CodexFlow.Gateway.Contracts;
using CodexFlow.Local;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using CodexFlow.Orchestration.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Gateway;

public sealed class P3FilesSnapshotContractsAndHostingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-p3-files-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            LocalExecutionService.TryDeleteDirectory(_root);
        }
    }

    [Fact]
    public async Task WorkspaceFilesAndChanges_EnforceOwnershipPathTraversalLimitsBinaryDetectionAndGitDiff()
    {
        var globalSettings = Path.Combine(_root, "global", "appsettings.json");
        var gitService = new GitService(NullLogger<GitService>.Instance);

        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            gitService: gitService);

        using var client = host.CreateNormalClient();
        var sessionResp = await client.PostAsync("/api/gateway/session?name=FilesTest", null);
        var sessionId = (await sessionResp.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];

        string workspacePath, foreignSessionId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            workspacePath = (await chat.GetSession(sessionId))!.WorkspacePath;
            foreignSessionId = await chat.CreateSession("someone-else", "Foreign");
        }

        // Prepare git repo + tracked files (including nested .codexflow & .gitattributes) BEFORE initial commit
        Assert.True(await gitService.InitRepositoryAsync(workspacePath));
        var trackedFile = Path.Combine(workspacePath, "src", "hello.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(trackedFile)!);
        await File.WriteAllTextAsync(trackedFile, "line1\n");
        var nestedInternal = Path.Combine(workspacePath, "src", ".codexflow", "private.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedInternal)!);
        await File.WriteAllTextAsync(nestedInternal, "v1");
        var nestedBinInternal = Path.Combine(workspacePath, "src", ".codexflow", "private.bin");
        await File.WriteAllBytesAsync(nestedBinInternal, [0x00, 0x01, 0x02, 0x03]);
        await File.WriteAllTextAsync(Path.Combine(workspacePath, ".gitattributes"), "*.txt filter=Evil diff=Evil\n*.bin binary\n");
        Assert.True(await gitService.CommitAsync(workspacePath, "add hello and baseline"));

        await File.WriteAllTextAsync(trackedFile, "line1\nline2\n");
        await File.WriteAllTextAsync(nestedInternal, "nested-internal-secret-body");
        await File.WriteAllBytesAsync(nestedBinInternal, [0x00, 0x09, 0x0A, 0x0B]);
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "untracked.md"), "# New doc\n");
        await File.WriteAllBytesAsync(Path.Combine(workspacePath, "blob.bin"), [0x00, 0x01, 0x02, 0xFF]);

        // Ownership enforcement (404)
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/gateway/session/{foreignSessionId}/files")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/gateway/session/{foreignSessionId}/changes")).StatusCode);

        // Path traversal & hidden internal dirs (.git, .codexflow, Windows trailing dot bypass, .., absolute)
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/gateway/session/{sessionId}/files?path=../outside")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/gateway/session/{sessionId}/files?path=.git")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/gateway/session/{sessionId}/files/content?path=.git./HEAD")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/gateway/session/{sessionId}/files/content?path=.codexflow%20/state.db")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/gateway/session/{sessionId}/files/content?path=../../secret.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/gateway/session/{sessionId}/files/content?path={Uri.EscapeDataString(globalSettings)}")).StatusCode);

        // Internal .codexflow files are never exposed in changes or diff
        Directory.CreateDirectory(Path.Combine(workspacePath, ".codexflow"));
        await File.WriteAllTextAsync(Path.Combine(workspacePath, ".codexflow", "internal.secret"), "secret-internal-token");

        // R3-B1 & R3-B2: Case-colliding drivers (filter.evil + filter.Evil), diff.noprefix, diff.mnemonicPrefix
        var filterMarker = Path.Combine(_root, "filter-executed.marker");
        var filterCmd = OperatingSystem.IsWindows()
            ? $"powershell -NoProfile -Command \"Set-Content -Path '{filterMarker}' -Value pwned\""
            : $"sh -c 'touch \"{filterMarker}\"'";
        foreach (var cfgArgs in new[]
        {
            $"config filter.evil.clean \"echo safe\"",
            $"config filter.Evil.clean \"{filterCmd.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
            $"config filter.Evil.process \"{filterCmd.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
            "config filter.Evil.required true",
            $"config diff.Evil.command \"{filterCmd.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
            "config diff.noprefix true",
            "config diff.mnemonicPrefix true"
        })
        {
            using var p = Process.Start(new ProcessStartInfo("git", cfgArgs)
            {
                WorkingDirectory = workspacePath,
                CreateNoWindow = true,
                UseShellExecute = false
            })!;
            Assert.True(p.WaitForExit(5000));
            Assert.Equal(0, p.ExitCode);
        }

        // List root hides .git and lists src, untracked.md, blob.bin
        var rootListing = await client.GetFromJsonAsync<WorkspaceDirectoryListingDto>($"/api/gateway/session/{sessionId}/files");
        Assert.NotNull(rootListing);
        Assert.DoesNotContain(rootListing!.Entries, e => e.Name == ".git");
        Assert.Contains(rootListing.Entries, e => e.Name == "src" && e.IsDirectory);
        Assert.Contains(rootListing.Entries, e => e.Name == "untracked.md" && !e.IsDirectory);

        // Read text file content
        var txtContent = await client.GetFromJsonAsync<WorkspaceFileContentDto>($"/api/gateway/session/{sessionId}/files/content?path=src/hello.txt");
        Assert.False(txtContent!.IsBinary);
        Assert.False(txtContent.Truncated);
        Assert.Contains("line2", txtContent.Content, StringComparison.Ordinal);

        // Binary file detection
        var binContent = await client.GetFromJsonAsync<WorkspaceFileContentDto>($"/api/gateway/session/{sessionId}/files/content?path=blob.bin");
        Assert.True(binContent!.IsBinary);
        Assert.Equal(string.Empty, binContent.Content);

        // Truncation limit check via WorkspaceFileExplorerService
        var bigPath = Path.Combine(workspacePath, "big.txt");
        await File.WriteAllTextAsync(bigPath, new string('a', 4096));
        var truncatedRead = await WorkspaceFileExplorerService.ReadFileContentAsync(workspacePath, "big.txt", maxBytes: 1024);
        Assert.True(truncatedRead.Success);
        Assert.True(truncatedRead.Truncated);
        Assert.Equal(1024, truncatedRead.Content.Length);

        // Changes unified diff
        var changes = await client.GetFromJsonAsync<WorkspaceChangesDto>($"/api/gateway/session/{sessionId}/changes");
        Assert.NotNull(changes);
        Assert.True(changes!.HasGitRepository);
        Assert.Contains(changes.Files, f => f.FilePath == "src/hello.txt");
        Assert.Contains(changes.Files, f => f.FilePath == "untracked.md");
        Assert.Contains("+line2", changes.UnifiedDiff, StringComparison.Ordinal);
        Assert.Contains("+# New doc", changes.UnifiedDiff, StringComparison.Ordinal);
        Assert.DoesNotContain(changes.Files, f => f.FilePath.Contains(".codexflow", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("secret-internal-token", changes.UnifiedDiff, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-internal-secret-body", changes.UnifiedDiff, StringComparison.Ordinal);
        Assert.DoesNotContain("src/.codexflow/private.txt", changes.UnifiedDiff, StringComparison.Ordinal);
        Assert.DoesNotContain("src/.codexflow/private.bin", changes.UnifiedDiff, StringComparison.Ordinal);
        Assert.False(File.Exists(filterMarker), "Git clean/process/diff filters must never execute during read-only /changes.");

        // Confirm raw git diff actually emitted a binary patch when run directly with binary attributes, and separate parser prefix modes filter it
        using (var rawBinDiff = Process.Start(new ProcessStartInfo("git", "diff --binary HEAD -- src/.codexflow/private.bin")
        {
            WorkingDirectory = workspacePath,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            UseShellExecute = false
        })!)
        {
            var rawOut = await rawBinDiff.StandardOutput.ReadToEndAsync();
            Assert.True(rawBinDiff.WaitForExit(5000));
            Assert.Contains("GIT binary patch", rawOut, StringComparison.Ordinal);
        }

        const string binBody = "index 111..222 100644\nGIT binary patch\nliteral 4\nLcmZQzU|<0R002M$\n";
        var filteredNoPrefix = WorkspaceFileExplorerService.FilterInternalMetadataDiffSections($"diff --git src/.codexflow/private.bin src/.codexflow/private.bin\n{binBody}");
        var filteredMnemonic = WorkspaceFileExplorerService.FilterInternalMetadataDiffSections($"diff --git i/src/.codexflow/private.bin w/src/.codexflow/private.bin\n{binBody}");
        var filteredStandard = WorkspaceFileExplorerService.FilterInternalMetadataDiffSections($"diff --git a/src/.codexflow/private.bin b/src/.codexflow/private.bin\n{binBody}");
        Assert.DoesNotContain("GIT binary patch", filteredNoPrefix, StringComparison.Ordinal);
        Assert.DoesNotContain("GIT binary patch", filteredMnemonic, StringComparison.Ordinal);
        Assert.DoesNotContain("GIT binary patch", filteredStandard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotEndpoint_ReturnsAuthoritativeStateWithAsOfEventIdMessagesPlanApprovalsAndJobs()
    {
        var globalSettings = Path.Combine(_root, "global", "appsettings.json");
        var jobMock = new Mock<IJobViewService>();
        jobMock.Setup(j => j.GetSessionJobsAsync(It.IsAny<string>()))
            .ReturnsAsync(
            [
                new BackgroundJobDto
                {
                    JobId = "job-1",
                    JobType = "ForgeWorker",
                    Status = "Succeeded",
                    Summary = "Completed without isolated verification"
                }
            ]);

        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            jobViewService: jobMock.Object);

        using var client = host.CreateNormalClient();
        var sessionResp = await client.PostAsync("/api/gateway/session?name=SnapTest", null);
        var sessionId = (await sessionResp.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];

        string convId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            convId = await chat.CreateConversationInSession(LocalUser.Id, sessionId, name: "Main Thread", type: CodexFlow.Domain.Chat.ConversationThreadType.Master);
            await chat.AddMessage(LocalUser.Id, convId, "hello", "world", 10, "codex-v2", thinkingContent: "thoughts");
        }

        // Publish an event to advance channel seq & epoch
        await host.Gateway.PublishEventAsync(sessionId, new GatewaySseEvent
        {
            Type = GatewaySseEventType.SystemMessage,
            Payload = new { content = "ready" }
        });

        // Save a plan
        await host.PlanStore.SaveAsync(new PlanArtifact
        {
            Id = "snap-plan",
            SessionId = sessionId,
            Version = "2",
            Goal = "Snapshot goal",
            Markdown = "# Plan\n1. Step 1\n",
            Status = PlanArtifactStatus.ReadyForApproval
        });
        await host.PlanStore.SetCurrentAsync(sessionId, "snap-plan");

        var snapshot = await client.GetFromJsonAsync<SessionSnapshotDto>($"/api/gateway/session/{sessionId}/snapshot");
        Assert.NotNull(snapshot);
        Assert.Equal(sessionId, snapshot!.SessionId);
        Assert.True(snapshot.AsOfEventId >= 1);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.ChannelEpoch));
        Assert.Equal(convId, snapshot.ActiveConversationId);
        Assert.Equal(2, snapshot.Messages.Count); // user + assistant
        Assert.Equal("snap-plan", snapshot.CurrentPlan!.PlanArtifactId);
        Assert.Equal("2", snapshot.CurrentPlan.Version);
        var job = Assert.Single(snapshot.Jobs);
        Assert.Equal("job-1", job.JobId);
    }

    [Fact]
    public async Task BrowserSse_PublishesPreviewAndApprovalWithDesktopFieldNames()
    {
        await using var host = await P3TestHost.StartAsync(
            _root, Path.Combine(_root, "global", "appsettings.json"),
            ExecutionBackend.Local, ExecutionPermissionMode.Ask);
        using var client = host.CreateNormalClient();
        using var sessionResponse = await client.PostAsync("/api/gateway/session?name=BrowserSse", null);
        sessionResponse.EnsureSuccessStatusCode();
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync($"/api/gateway/sse?sessionId={sessionId}",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));

        const string url = "https://example.com/";
        var now = DateTimeOffset.UtcNow;
        await host.Gateway.PublishEventAsync(sessionId, new GatewaySseEvent
        {
            Type = GatewaySseEventType.BrowserStateUpdated,
            Payload = new BrowserSessionStateDto(sessionId, "running", true, "Ephemeral", url,
                "Example", 7, 0, [], [new(1, "browser_navigate", "goto", url, url, true, null, 10, now)], [])
        }, timeout.Token);
        await host.Gateway.PublishEventAsync(sessionId, new GatewaySseEvent
        {
            Type = GatewaySseEventType.BrowserApprovalRequested,
            Payload = new BrowserApprovalRequestDto("approval-1", "digest", sessionId, "browser_act",
                "click", url, "https://example.com", "Submit", "Sensitive submission", null,
                now.AddMinutes(1), ["allow_once", "deny"], true, true)
        }, timeout.Token);

        using var state = await ReadEventAsync(GatewaySseEventType.BrowserStateUpdated);
        Assert.Equal(sessionId, state.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("running", state.RootElement.GetProperty("status").GetString());
        Assert.Equal(url, state.RootElement.GetProperty("url").GetString());
        Assert.Equal(7, state.RootElement.GetProperty("screenshotSeq").GetInt64());
        var action = Assert.Single(state.RootElement.GetProperty("recentActions").EnumerateArray());
        Assert.Equal("browser_navigate", action.GetProperty("tool").GetString());
        Assert.True(action.GetProperty("succeeded").GetBoolean());

        using var approval = await ReadEventAsync(GatewaySseEventType.BrowserApprovalRequested);
        Assert.Equal(sessionId, approval.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("approval-1", approval.RootElement.GetProperty("approvalId").GetString());
        Assert.Equal("deny", approval.RootElement.GetProperty("allowedDecisions")[1].GetString());

        async Task<JsonDocument> ReadEventAsync(string eventType)
        {
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line == $"event: {eventType}")
                {
                    var data = await reader.ReadLineAsync(timeout.Token);
                    Assert.StartsWith("data: ", data, StringComparison.Ordinal);
                    return JsonDocument.Parse(data!["data: ".Length..]);
                }
            }
            throw new InvalidOperationException($"Missing SSE event {eventType}.");
        }
    }

    [Fact]
    public async Task ContractGeneration_MatchesCommittedOpenApiAndSseSchemaWithoutDrift()
    {
        var repoRoot = FindRepoRoot();
        var openApiPath = Path.Combine(repoRoot, "docs", "contracts", "openapi.json");
        var sseSchemaPath = Path.Combine(repoRoot, "docs", "contracts", "sse-events.schema.json");

        Assert.True(File.Exists(openApiPath), $"Missing committed OpenAPI contract: {openApiPath}");
        Assert.True(File.Exists(sseSchemaPath), $"Missing committed SSE schema contract: {sseSchemaPath}");

        var (generatedOpenApi, generatedSseSchema) = await GatewayOpenApiExtensions.GenerateContractsAsync();
        var committedOpenApi = (await File.ReadAllTextAsync(openApiPath)).ReplaceLineEndings("\n").TrimEnd('\n');
        var committedSseSchema = (await File.ReadAllTextAsync(sseSchemaPath)).ReplaceLineEndings("\n").TrimEnd('\n');

        Assert.Equal(committedOpenApi, generatedOpenApi.TrimEnd('\n'));
        Assert.Equal(committedSseSchema, generatedSseSchema.TrimEnd('\n'));

        using var sseDoc = JsonDocument.Parse(generatedSseSchema);
        Assert.Equal(SseEventContractSchema.SchemaVersion, sseDoc.RootElement.GetProperty("schemaVersion").GetString());
        var defs = sseDoc.RootElement.GetProperty("$defs");
        Assert.True(defs.TryGetProperty(GatewaySseEventType.PlanApprovalRequested, out _));
        Assert.True(defs.TryGetProperty(GatewaySseEventType.ToolStarted, out _));
        Assert.True(defs.TryGetProperty(GatewaySseEventType.ToolOutput, out _));

        // B9: Every $def declares required constraints and root oneOf binds event -> data.$ref
        Assert.True(sseDoc.RootElement.TryGetProperty("oneOf", out var oneOfArray));
        var boundEvents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in oneOfArray.EnumerateArray())
        {
            var props = branch.GetProperty("properties");
            var evtConst = props.GetProperty("event").GetProperty("const").GetString()!;
            var dataRef = props.GetProperty("data").GetProperty("$ref").GetString()!;
            Assert.Equal($"#/$defs/{evtConst}", dataRef);
            boundEvents.Add(evtConst);
        }

        foreach (var def in defs.EnumerateObject())
        {
            Assert.Contains(def.Name, boundEvents);
            Assert.True(def.Value.TryGetProperty("required", out _), $"$defs/{def.Name} missing required");
        }

        // B9: plan_approval_requested explicitly requires version and markdownHash
        var planApprovalDef = defs.GetProperty(GatewaySseEventType.PlanApprovalRequested);
        var planRequired = planApprovalDef.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("version", planRequired);
        Assert.Contains("markdownHash", planRequired);
        Assert.Equal("string", planApprovalDef.GetProperty("properties").GetProperty("version").GetProperty("type").GetString());
        Assert.Equal("string", planApprovalDef.GetProperty("properties").GetProperty("markdownHash").GetProperty("type").GetString());
    }

    [Fact]
    public async Task SnapshotEndpoint_ExposesInFlightPartialContentAndCursor_And404ForForeignOrUnknownConversation()
    {
        var globalSettings = Path.Combine(_root, "global-b6", "appsettings.json");
        var midTurnReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowTurnFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = new DelegateSnapshotProcessor(async (msg, emit, ct) =>
        {
            await emit(new GatewaySseEvent { Type = GatewaySseEventType.ThinkingContent, Payload = new { type = "thinking_content", content = "thinking-part ", text = "thinking-part " } });
            await emit(new GatewaySseEvent { Type = GatewaySseEventType.Content, Payload = new { type = "content", content = "partial-answer", text = "partial-answer" } });
            await emit(new GatewaySseEvent
            {
                Type = GatewaySseEventType.ToolOutput,
                Payload = new { tool = "exec_cmd", commandTaskId = "cmd-1", stdout = "chunk-1", stderr = (string?)null, throttled = false, suppressedLines = 0 }
            });
            await emit(new GatewaySseEvent
            {
                Type = GatewaySseEventType.TaskStatus,
                Payload = new { type = "tool_result", name = "exec_cmd", callId = "c1", success = true, result = new string('r', 5000) }
            });
            midTurnReached.TrySetResult();
            await allowTurnFinish.Task.WaitAsync(ct);
        });

        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            customProcessor: processor);

        using var client = host.CreateNormalClient();
        var sessionResp = await client.PostAsync("/api/gateway/session?name=InFlightSnap", null);
        var sessionId = (await sessionResp.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];

        var chatResp = await client.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest { SessionId = sessionId, Message = "go" });
        Assert.Equal(HttpStatusCode.Accepted, chatResp.StatusCode);
        await midTurnReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Publish concurrent external event while turn is active -> monotonic cursor must preserve prior events
        await host.Gateway.PublishEventAsync(sessionId, new GatewaySseEvent { Type = GatewaySseEventType.SystemMessage, Payload = new { content = "ext" } });

        // R3-B3: Production external path ToolOutputEventThrottler -> ICodexGateway.PublishEventAsync -> SessionChannel
        await using (var throttler = new CodexFlow.Core.Execution.Approval.ToolOutputEventThrottler(
            host.Gateway,
            sessionId,
            "exec_cmd",
            "throttler-task-1"))
        {
            throttler.Append("stdout", "external-throttler-line");
            await throttler.FlushAsync(CancellationToken.None);
        }

        var snapshot = await client.GetFromJsonAsync<SessionSnapshotDto>($"/api/gateway/session/{sessionId}/snapshot");
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.ActiveTurn.IsRunning);
        Assert.Equal("partial-answer", snapshot.ActiveTurn.PartialContent);
        Assert.Equal("thinking-part ", snapshot.ActiveTurn.PartialThinking);
        Assert.True(snapshot.AsOfEventId >= 6);
        Assert.Single(snapshot.ActiveToolResults);
        Assert.True(snapshot.ToolResultsTruncated);
        Assert.Equal(2, snapshot.ActiveToolOutputs!.Count);
        Assert.Contains(snapshot.ActiveToolOutputs, o => o.Stdout == "external-throttler-line");

        // Requesting an unowned or missing conversationId returns 404 instead of silently falling back
        var missingConvResp = await client.GetAsync($"/api/gateway/session/{sessionId}/snapshot?conversationId=does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missingConvResp.StatusCode);

        // Complete turn and verify completed tool outputs & results remain recoverable on snapshot
        allowTurnFinish.TrySetResult();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        SessionSnapshotDto? postTurn = null;
        while (DateTime.UtcNow < deadline)
        {
            postTurn = await client.GetFromJsonAsync<SessionSnapshotDto>($"/api/gateway/session/{sessionId}/snapshot");
            if (postTurn is { ActiveTurn.IsRunning: false })
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.NotNull(postTurn);
        Assert.False(postTurn!.ActiveTurn.IsRunning);
        Assert.Single(postTurn.ActiveToolResults);
        Assert.True(postTurn.ToolResultsTruncated);
        Assert.Equal(2, postTurn.ActiveToolOutputs!.Count);

        // R3-B3: Simulated host restart (channel closed/evicted) recovers persisted tool results & truncation flags from SQLite
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            var convId = await chat.CreateConversationInSession(LocalUser.Id, sessionId, name: "Restart Recovery");
            var persistedEnvelope = JsonSerializer.Serialize(new
            {
                results = postTurn.ActiveToolResults,
                resultsTruncated = true,
                outputs = postTurn.ActiveToolOutputs,
                outputsTruncated = true
            });
            await chat.AddMessageWithToolcall(LocalUser.Id, convId, "u", "a", 1, persistedEnvelope, "codex-v2", "", "", "t");
        }

        await host.Gateway.CloseSessionAsync(sessionId);
        var postRestart = await client.GetFromJsonAsync<SessionSnapshotDto>($"/api/gateway/session/{sessionId}/snapshot");
        Assert.NotNull(postRestart);
        Assert.Null(postRestart!.ChannelEpoch);
        Assert.Single(postRestart.ActiveToolResults);
        Assert.True(postRestart.ToolResultsTruncated);
        Assert.Equal(2, postRestart.ActiveToolOutputs!.Count);
        Assert.True(postRestart.ToolOutputsTruncated);
    }

    [Fact]
    public async Task SnapshotEndpoint_KeepsLongToolHistoryStructuredAndRetainsFailures()
    {
        await using var host = await P3TestHost.StartAsync(
            _root, Path.Combine(_root, "snapshot-tool-history", "appsettings.json"),
            ExecutionBackend.Local, ExecutionPermissionMode.Ask);
        using var client = host.CreateNormalClient();
        var created = await client.PostAsync("/api/gateway/session?name=ToolHistory", null);
        var sessionId = (await created.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];

        await using var scope = host.Services.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
        var conversationId = await chat.CreateConversationInSession(LocalUser.Id, sessionId, name: "Tool history");
        var largeHistory = JsonSerializer.Serialize(new
        {
            results = Enumerable.Range(0, 15).Select(i => new GatewayChannelToolResultSnapshot(
                "computer_act", $"c{i}", i != 14, i == 14 ? "window drift" : new string('x', 4200), false)),
            resultsTruncated = false,
            outputs = Array.Empty<GatewayChannelToolOutputSnapshot>(),
            outputsTruncated = false
        });
        await chat.AddMessageWithToolcall(LocalUser.Id, conversationId, "u", "", 0, largeHistory, "codex-v2", "", "", null);
        await chat.AddMessage(LocalUser.Id, conversationId, "u2", "newer answer", 0, "codex-v2", "", "", null);

        var snapshot = await client.GetFromJsonAsync<SessionSnapshotDto>(
            $"/api/gateway/session/{sessionId}/snapshot?conversationId={conversationId}");
        var stored = Assert.Single(snapshot!.Messages, message => message.ToolCallResult is not null);
        Assert.True(stored.Truncated);
        Assert.True(stored.ToolCallResult!.Length <= 32_768);
        using var history = JsonDocument.Parse(stored.ToolCallResult);
        Assert.True(history.RootElement.GetProperty("resultsTruncated").GetBoolean());
        var results = history.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(15, results.Length);
        Assert.Equal("window drift", results[^1].GetProperty("Result").GetString());
        Assert.False(results[^1].GetProperty("Success").GetBoolean());
    }

    [Fact]
    public async Task RealGatewayMessageProcessor_PersistsToolOutputsResultsAndLateOutputsAcrossRealRestartAndSseReconnect()
    {
        var globalSettings = Path.Combine(_root, "global-real-proc", "appsettings.json");
        var turnCompletedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = 1; // 1 = capacity overflow (105 results, 55 outputs), 2 = single result + late output
        var computer = new Mock<IComputerSessionManager>();
        computer.Setup(x => x.EndTurnAsync(It.IsAny<string>(), CancellationToken.None)).Returns(Task.CompletedTask);

        string sessionId;
        string overflowSessionId;
        await using (var host1 = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            configureServices: (services, cfg) =>
            {
                services.AddSingleton(Mock.Of<IChatClient>());
                services.AddCodexFlowRuntime(cfg);
                services.AddSingleton(computer.Object);
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ILLMExecutor>();
                services.AddScoped<ILLMExecutor>(_ => new SimpleStubLlmExecutor());
                services.RemoveAll<IQueryRuntimeEngine>();
                services.AddScoped<IQueryRuntimeEngine>(sp => new StubRuntimeEngine(
                    sp.GetRequiredService<ICodexGateway>(),
                    () => Volatile.Read(ref scenario),
                    () => turnCompletedTcs.TrySetResult()));
            }))
        {
            using var client1 = host1.CreateNormalClient();
            var overflowResp = await client1.PostAsync("/api/gateway/session?name=RealProcOverflow", null);
            overflowSessionId = (await overflowResp.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];

            // Session A (overflowSessionId): 105 tool results & 55 outputs persisted through real GatewayMessageProcessor -> SQLite
            var chat1 = await client1.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest { SessionId = overflowSessionId, Message = "turn-overflow" });
            Assert.Equal(HttpStatusCode.Accepted, chat1.StatusCode);
            await turnCompletedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var deadline = DateTime.UtcNow.AddSeconds(10);
            SessionSnapshotDto? snap1 = null;
            while (DateTime.UtcNow < deadline)
            {
                snap1 = await client1.GetFromJsonAsync<SessionSnapshotDto>($"/api/gateway/session/{overflowSessionId}/snapshot");
                if (snap1 is { ActiveTurn.IsRunning: false } && snap1.Messages.Any(m => m.Role == "assistant"))
                {
                    break;
                }

                await Task.Delay(30);
            }

            Assert.NotNull(snap1);
            computer.Verify(x => x.EndTurnAsync(overflowSessionId, CancellationToken.None), Times.Once);
            Assert.True(snap1!.ToolResultsTruncated);
            Assert.Equal(100, snap1.ActiveToolResults.Count);

            // Session B (sessionId): Non-overflow turn (1 tool_result + 1 in-turn throttler output), then notification + LATE oversized output
            var sessionResp = await client1.PostAsync("/api/gateway/session?name=RealProcRestart", null);
            sessionId = (await sessionResp.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];
            turnCompletedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref scenario, 2);
            var chat2 = await client1.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest { SessionId = sessionId, Message = "turn-late" });
            Assert.Equal(HttpStatusCode.Accepted, chat2.StatusCode);
            await turnCompletedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

            deadline = DateTime.UtcNow.AddSeconds(10);
            SessionSnapshotDto? snap2 = null;
            while (DateTime.UtcNow < deadline)
            {
                snap2 = await client1.GetFromJsonAsync<SessionSnapshotDto>($"/api/gateway/session/{sessionId}/snapshot");
                if (snap2 is { ActiveTurn.IsRunning: false } && snap2.Messages.Any(m => m.Role == "assistant"))
                {
                    break;
                }

                await Task.Delay(30);
            }

            Assert.NotNull(snap2);
            Assert.Single(snap2!.ActiveToolResults);
            Assert.False(snap2.ToolResultsTruncated);

            // Emit a notification message AFTER Turn 2 completes (must never hijack late tool output merging),
            // then emit an OVERSIZED (>4096 char) LATE post-turn output after completion.
            await using (var scope = host1.Services.CreateAsyncScope())
            {
                var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
                await chat.AddNotificationMessage(LocalUser.Id, snap2.ActiveConversationId!, "post-turn system note", "info", null);
            }

            await using (var lateThrottler = new CodexFlow.Core.Execution.Approval.ToolOutputEventThrottler(
                host1.Gateway,
                sessionId,
                "run_command",
                "late-bg-cmd",
                maxChunkChars: 8192))
            {
                lateThrottler.Append("stdout", "late-post-turn-output-" + new string('z', 4500));
                await lateThrottler.FlushAsync(CancellationToken.None);
            }
        } // Host 1 disposed completely (true process-restart boundary for in-memory gateway & DB contexts)

        // Genuine restart: Host 2 opens the SAME SQLite state.db with empty in-memory gateway
        await using var host2 = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask);
        using var client2 = host2.CreateNormalClient();

        // Reconnect SSE FIRST -> creates a fresh empty SessionChannel in Host 2 before snapshot is queried!
        using var sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (var sseResp = await client2.GetAsync($"/api/gateway/sse?sessionId={Uri.EscapeDataString(sessionId)}", HttpCompletionOption.ResponseHeadersRead, sseCts.Token))
        {
            Assert.Equal(HttpStatusCode.OK, sseResp.StatusCode);
            var dl = DateTime.UtcNow.AddSeconds(3);
            while (!host2.Gateway.HasActiveSubscribers(sessionId) && DateTime.UtcNow < dl)
            {
                await Task.Delay(20);
            }

            // Snapshot MUST NOT be shadowed by fresh empty SessionChannel or system notification row;
            // recovers Turn 2 result + in-turn & late oversized outputs + OutputsTruncated=true permanently from SQLite!
            var restartedSnap = await client2.GetFromJsonAsync<SessionSnapshotDto>($"/api/gateway/session/{sessionId}/snapshot");
            Assert.NotNull(restartedSnap);
            var recoveredResult = Assert.Single(restartedSnap!.ActiveToolResults);
            Assert.Equal("ok-turn2", recoveredResult.Result);
            Assert.Contains(restartedSnap.ActiveToolOutputs!, o => o.Stdout == "in-turn-throttler-output");
            Assert.Contains(restartedSnap.ActiveToolOutputs!, o => o.Stdout is { } s && s.StartsWith("late-post-turn-output-", StringComparison.Ordinal));
            Assert.True(restartedSnap.ToolOutputsTruncated);

            // Also confirm overflowSessionId recovers ToolResultsTruncated=true & ToolOutputsTruncated=true across genuine restart
            var snapOverflow = await client2.GetFromJsonAsync<SessionSnapshotDto>($"/api/gateway/session/{overflowSessionId}/snapshot");
            Assert.NotNull(snapOverflow);
            Assert.True(snapOverflow!.ToolResultsTruncated);
            Assert.True(snapOverflow.ToolOutputsTruncated);
            Assert.Equal(100, snapOverflow.ActiveToolResults.Count);

            sseCts.Cancel();
        }
    }

    [Fact]
    public async Task RealGatewayMessageProcessor_LegacyToolLoop_PropagatesCancellationAndPersistsCancelledState()
    {
        var globalSettings = Path.Combine(_root, "global-legacy-cancel", "appsettings.json");
        var streamingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var computer = new Mock<IComputerSessionManager>();
        computer.Setup(x => x.EndTurnAsync(It.IsAny<string>(), CancellationToken.None)).Returns(Task.CompletedTask);

        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            configureServices: (services, cfg) =>
            {
                services.AddSingleton(Mock.Of<IChatClient>());
                services.AddCodexFlowRuntime(cfg);
                services.AddSingleton(computer.Object);
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IQueryRuntimeEngine>(); // Force legacy StreamWithToolLoopAsync path
                services.RemoveAll<ILLMExecutor>();
                services.AddScoped<ILLMExecutor>(_ => new CancellingStreamExecutor(streamingEntered));
            });

        using var client = host.CreateNormalClient();
        var sessionResp = await client.PostAsync("/api/gateway/session?name=LegacyCancel", null);
        var sessionId = (await sessionResp.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];

        var chatResp = await client.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest { SessionId = sessionId, Message = "cancel me" });
        Assert.Equal(HttpStatusCode.Accepted, chatResp.StatusCode);
        await streamingEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var cancelResp = await client.PostAsync($"/api/gateway/session/{sessionId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);
        Assert.True((await cancelResp.Content.ReadFromJsonAsync<CancelTurnResponse>())!.Cancelled);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        SessionSnapshotDto? snap = null;
        while (DateTime.UtcNow < deadline)
        {
            snap = await client.GetFromJsonAsync<SessionSnapshotDto>($"/api/gateway/session/{sessionId}/snapshot");
            if (snap is { ActiveTurn.IsRunning: false } && snap.Messages.Any(m => m.Content.Contains("[已取消]", StringComparison.Ordinal)))
            {
                break;
            }

            await Task.Delay(30);
        }

        Assert.NotNull(snap);
        Assert.Contains(snap!.Messages, m => m.Role == "assistant" && m.Content.Contains("[已取消]", StringComparison.Ordinal));
        computer.Verify(x => x.EndTurnAsync(sessionId, CancellationToken.None), Times.Once);
    }

    [Theory]
    [InlineData("  ", true)]
    [InlineData("Archived summary", false)]
    public async Task DailyGateway_ReplaysSelectedHistoryWhenLegacyCutoffHasNoSummary(string summary, bool recoverHistory)
    {
        var captured = new TaskCompletionSource<QueryRuntimeRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Mock<IQueryRuntimeEngine>();
        runtime.Setup(engine => engine.ExecuteAsync(
                It.IsAny<QueryRuntimeRequest>(), It.IsAny<IQueryRuntimeEventSink>(), It.IsAny<CancellationToken>()))
            .Callback<QueryRuntimeRequest, IQueryRuntimeEventSink, CancellationToken>((request, _, _) => captured.TrySetResult(request))
            .ReturnsAsync(new QueryRuntimeResult
            {
                TerminationReason = QueryTerminationReason.NoToolCalls,
                TotalRounds = 1,
                TotalToolCalls = 0,
                ZeroToolCallRounds = 1,
                EmptyResponseCount = 0,
                RecoveryCount = 0,
                MalformedProtocolCount = 0,
                FinalText = "Follow-up complete",
                TotalDurationMs = 0
            });
        await using var host = await P3TestHost.StartAsync(
            _root, Path.Combine(_root, "daily-history", "appsettings.json"), ExecutionBackend.Local, ExecutionPermissionMode.Ask,
            configureServices: (services, cfg) =>
            {
                services.AddSingleton(Mock.Of<IChatClient>());
                services.AddCodexFlowRuntime(cfg);
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IQueryRuntimeEngine>();
                services.AddSingleton(runtime.Object);
                services.Configure<CodexFlow.Models.ContextCompressionOptions>(options => options.PromptHistoryHardLimit = 2);
            });
        using var client = host.CreateNormalClient();
        using var created = await client.PostAsync("/api/gateway/session?name=ContextRecovery&workMode=daily", null);
        created.EnsureSuccessStatusCode();
        var sessionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString()!;

        const string outsideLimit = "older than configured prompt limit";
        const string recovered = "synthetic handoff marker";
        const string recent = "next selected-chat message";
        const string otherChat = "different conversation marker";
        string conversationId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            conversationId = await chat.CreateConversationInSession(LocalUser.Id, sessionId, name: "Selected chat");
            var otherConversation = await chat.CreateConversationInSession(LocalUser.Id, sessionId, name: "Different chat");
            await chat.AddMessage(LocalUser.Id, conversationId, outsideLimit, "Old reply", 0, "test");
            await chat.AddMessage(LocalUser.Id, conversationId, recovered, "First selected reply", 0, "test");
            await chat.AddMessage(LocalUser.Id, conversationId, recent, "Second selected reply", 0, "test");
            await chat.AddMessage(LocalUser.Id, otherConversation, otherChat, "Different chat reply", 0, "test");

            var manager = scope.ServiceProvider.GetRequiredService<CodexSessionManager>();
            var session = await manager.GetOrCreateSessionAsync(sessionId);
            session.HistorySummary = summary;
            session.LastCompressedAt = DateTime.UtcNow.AddSeconds(1);
            await manager.UpdateSessionAsync(session);
        }

        var usage = await client.GetFromJsonAsync<SessionContextWindowResponse>(
            $"/api/gateway/session/{sessionId}/context-window?conversationId={conversationId}");
        Assert.Equal(recoverHistory ? 4 : 0, usage!.RecentTurns);
        Assert.Equal(recoverHistory ? 0 : summary.Length, usage.SummaryChars);

        using var sent = await client.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest
        {
            SessionId = sessionId,
            ConversationId = conversationId,
            ConversationMode = "continue-current",
            Message = "Continue the selected conversation"
        });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var request = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var historicalMessages = request.InitialMessages.Select(message => message.Text)
            .Where(text => text is recovered or recent or otherChat or outsideLimit)
            .ToArray();

        Assert.Equal(recoverHistory ? [recovered, recent] : Array.Empty<string>(), historicalMessages);
        if (!recoverHistory)
        {
            Assert.Contains(request.InitialMessages, message =>
                message.Role == ChatRole.System && message.Text.Contains(summary, StringComparison.Ordinal));
        }
    }

    private sealed class SimpleStubLlmExecutor : ILLMExecutor
    {
        public Task<ChatResponse> ExecuteAsync(LLMExecutionRequest request, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Title")]));

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            LLMExecutionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }
    }

    private sealed class CancellingStreamExecutor(TaskCompletionSource entered) : ILLMExecutor
    {
        public Task<ChatResponse> ExecuteAsync(LLMExecutionRequest request, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Title")]));

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            LLMExecutionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial-before-cancel");
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    private sealed class StubRuntimeEngine(
        ICodexGateway gateway,
        Func<int> getScenario,
        Action onFinished) : IQueryRuntimeEngine
    {
        public async Task<QueryRuntimeResult> ExecuteAsync(
            QueryRuntimeRequest request,
            IQueryRuntimeEventSink eventSink,
            CancellationToken ct = default)
        {
            var mode = getScenario();
            var qid = Guid.NewGuid();
            var sessionId = request.Session.Id;

            if (mode == 1)
            {
                for (var i = 0; i < 105; i++)
                {
                    await eventSink.OnEventAsync(new ToolExecutionCompletedEvent(
                        i + 1, qid, sessionId, QueryLoopEntryPoint.GatewayMessageProcessor, 1, "exec_cmd", $"c-{i}", $"res-{i}", true));
                }

                await using var throttler = new CodexFlow.Core.Execution.Approval.ToolOutputEventThrottler(
                    gateway,
                    sessionId,
                    "run_command",
                    "overflow-cmd",
                    minFlushInterval: TimeSpan.Zero);
                for (var j = 0; j < 55; j++)
                {
                    throttler.Append("stdout", $"overflow-line-{j}");
                    await throttler.FlushAsync(ct);
                }
            }
            else
            {
                await eventSink.OnEventAsync(new ToolExecutionCompletedEvent(
                    1, qid, sessionId, QueryLoopEntryPoint.GatewayMessageProcessor, 1, "exec_cmd", "c-t2", "ok-turn2", true));

                await using var throttler = new CodexFlow.Core.Execution.Approval.ToolOutputEventThrottler(
                    gateway,
                    sessionId,
                    "run_command",
                    "in-turn-cmd");
                throttler.Append("stdout", "in-turn-throttler-output");
                await throttler.FlushAsync(ct);
            }

            await eventSink.OnEventAsync(new ThinkingDeltaEvent(200, qid, sessionId, QueryLoopEntryPoint.GatewayMessageProcessor, 1, "real-thinking"));
            await eventSink.OnEventAsync(new AssistantDeltaEvent(201, qid, sessionId, QueryLoopEntryPoint.GatewayMessageProcessor, 1, "real-answer"));
            onFinished();

            return new QueryRuntimeResult
            {
                TerminationReason = QueryTerminationReason.NoToolCalls,
                TotalRounds = 1,
                TotalToolCalls = mode == 1 ? 105 : 1,
                ZeroToolCallRounds = 0,
                EmptyResponseCount = 0,
                RecoveryCount = 0,
                MalformedProtocolCount = 0,
                FinalText = "real-answer",
                FinalThinking = "real-thinking",
                TotalDurationMs = 5
            };
        }
    }

    [Fact]
    public async Task LocalHostRealAppsettings_RespectsDataDirectoryIsolation_AndHotReloadsGlobalModelSettings()
    {
        var repoRoot = FindRepoRoot();
        var realAppSettings = Path.Combine(repoRoot, "CodexFlow.Local", "appsettings.json");
        var dataDirA = Path.Combine(_root, "data-a");
        var dataDirB = Path.Combine(_root, "data-b");
        var globalDir = Path.Combine(_root, "global-b5");
        var globalSettingsPath = Path.Combine(globalDir, "appsettings.json");
        Directory.CreateDirectory(globalDir);

        var configA = new ConfigurationBuilder()
            .AddJsonFile(realAppSettings, optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Local:DataDirectory"] = dataDirA })
            .Build();
        var configB = new ConfigurationBuilder()
            .AddJsonFile(realAppSettings, optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Local:DataDirectory"] = dataDirB })
            .Build();

        var layoutA = LocalHostRuntime.ResolveLayout(configA, globalDir);
        var layoutB = LocalHostRuntime.ResolveLayout(configB, globalDir);
        Assert.Equal(Path.Combine(Path.GetFullPath(dataDirA), "state.db"), layoutA.SqlitePath);
        Assert.Equal(Path.Combine(Path.GetFullPath(dataDirB), "state.db"), layoutB.SqlitePath);
        Assert.NotEqual(layoutA.SqlitePath, layoutB.SqlitePath);

        // Standalone File-mode configuration chain with reloadOnChange sees ModelSettingsService updates
        var modelSvc = new CodexFlow.Core.Configuration.ModelSettingsService(globalSettingsPath);
        modelSvc.Update(new CodexFlow.Core.Configuration.ModelSettingsUpdate(Model: "initial-model"));
        var liveConfig = new ConfigurationBuilder()
            .AddJsonFile(realAppSettings, optional: false)
            .AddJsonFile(globalSettingsPath, optional: true, reloadOnChange: true)
            .Build();
        Assert.Equal("initial-model", liveConfig["VllmAgent:Model"]);

        modelSvc.Update(new CodexFlow.Core.Configuration.ModelSettingsUpdate(Model: "reloaded-model"));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!string.Equals(liveConfig["VllmAgent:Model"], "reloaded-model", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        Assert.Equal("reloaded-model", liveConfig["VllmAgent:Model"]);
    }

    private sealed class DelegateSnapshotProcessor(
        Func<GatewayMessage, Func<GatewaySseEvent, ValueTask>, CancellationToken, Task> handler) : IGatewayMessageProcessor
    {
        public Task ProcessAsync(GatewayMessage message, Func<GatewaySseEvent, ValueTask> emit, CancellationToken ct)
            => handler(message, emit, ct);
    }

    [Fact]
    public async Task LocalHostRuntimeAndInstanceGuard_EnforceSingleInstanceLock_WriteTokenFreeDiscovery_AndWatchParentPid()
    {
        var dataDir = Path.Combine(_root, "managed-data");
        var globalDir = Path.Combine(_root, "global-dir");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(globalDir);

        // 1. DataDirectory layout decouples sqlite, credentials, workspaces, staging from cwd
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Local:DataDirectory"] = dataDir,
                ["Local:AllowedOrigins:0"] = "app://codexflow",
                ["Local:ParentPid"] = "4242"
            })
            .Build();

        var layout = LocalHostRuntime.ResolveLayout(config, globalDir);
        Assert.Equal(Path.GetFullPath(dataDir), layout.DataDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(dataDir), "state.db"), layout.SqlitePath);
        Assert.Equal(Path.Combine(Path.GetFullPath(dataDir), "workspaces"), layout.WorkspacesRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(dataDir), "credentials"), layout.CredentialDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(dataDir), "staging"), layout.StagingRoot);
        Assert.Equal(["app://codexflow"], layout.AllowedOrigins);
        Assert.Equal(4242, layout.ParentPid);

        // 2. Ready line format check
        var readyLine = LocalHostRuntime.FormatReadyLine(54321, 999, "inst-abc");
        Assert.StartsWith("CODEXFLOW_READY ", readyLine, StringComparison.Ordinal);
        using var readyJson = JsonDocument.Parse(readyLine["CODEXFLOW_READY ".Length..]);
        Assert.Equal(54321, readyJson.RootElement.GetProperty("port").GetInt32());
        Assert.Equal(999, readyJson.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal("inst-abc", readyJson.RootElement.GetProperty("instanceId").GetString());

        // 3. Single instance lock + token-free discovery file
        var dbMarker = Path.Combine(dataDir, "state.db");
        var tokenMarker = Path.Combine(dataDir, "credentials", LocalHostCredentials.AccessTokenFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(tokenMarker)!);
        await File.WriteAllTextAsync(dbMarker, "db-intact");
        await File.WriteAllTextAsync(tokenMarker, "token-intact");

        using (var guard1 = LocalInstanceGuard.Acquire(dataDir, "inst-1"))
        {
            var info = guard1.WriteDiscovery(51234);
            Assert.Equal(51234, info.Port);
            var discoveryText = await File.ReadAllTextAsync(guard1.DiscoveryFilePath);
            Assert.Contains("51234", discoveryText, StringComparison.Ordinal);
            Assert.DoesNotContain("token", discoveryText, StringComparison.OrdinalIgnoreCase);

            // Second instance targeting same DataDirectory fails immediately without touching token or DB
            Assert.Throws<LocalInstanceAlreadyRunningException>(() => LocalInstanceGuard.Acquire(dataDir, "inst-2"));
            Assert.Equal("db-intact", await File.ReadAllTextAsync(dbMarker));
            Assert.Equal("token-intact", await File.ReadAllTextAsync(tokenMarker));
        }

        // After first instance disposes, discovery file is removed and lock can be re-acquired
        Assert.False(File.Exists(Path.Combine(dataDir, LocalInstanceGuard.DiscoveryFileName)));
        using var guardReacquired = LocalInstanceGuard.Acquire(dataDir, "inst-3");

        // 4. ParentProcessWatcher triggers shutdown callback when parent process exits
        var exitTriggered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helperStart = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0")
            : new ProcessStartInfo("/bin/sh", "-c \"exit 0\"");
        helperStart.CreateNoWindow = true;
        helperStart.UseShellExecute = false;

        using var shortLivedParent = Process.Start(helperStart)!;
        using var watcher = new ParentProcessWatcher(
            shortLivedParent.Id,
            () => exitTriggered.TrySetResult(),
            pollInterval: TimeSpan.FromMilliseconds(50));
        await exitTriggered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodexFlow.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing CodexFlow.slnx.");
    }
}
