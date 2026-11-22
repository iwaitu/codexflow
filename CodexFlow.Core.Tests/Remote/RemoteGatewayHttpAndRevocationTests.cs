// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexFlow.Application;
using CodexFlow.Application.Jobs;
using CodexFlow.Contracts.ComputerUse;
using CodexFlow.Controllers;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using CodexFlow.Core.Remote;
using CodexFlow.Core.Services;
using CodexFlow.Gateway.Http;
using CodexFlow.Gateway.Http.Remote;
using CodexFlow.Local;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Services;
using CodexFlow.Storage.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Remote;

public sealed class RemoteGatewayHttpAndRevocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cf-remote-http-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            LocalExecutionService.TryDeleteDirectory(_root);
        }
    }

    [Fact]
    public async Task RemoteRequestContextMiddleware_EnforcesBridgeToken_PrincipalHeaders_AndRevocation()
    {
        await using var host = await RemoteTestHost.StartAsync(_root);
        var p1 = new RemotePrincipalStamp(Guid.NewGuid(), 1, 1, 1);

        // 1. Remote header without bridge token or with wrong bridge token -> 403
        using (var noBridge = host.CreateLocalClient())
        {
            noBridge.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.ControlChannelHeader, "Remote");
            noBridge.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.PairingId, p1.PairingId.ToString("D"));
            noBridge.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.BindingVersion, "1");
            noBridge.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.AccountRevocationSeq, "1");
            noBridge.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.DeviceRevocationSeq, "1");

            Assert.Equal(HttpStatusCode.Forbidden, (await noBridge.GetAsync("/api/gateway/workspaces")).StatusCode);

            noBridge.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.BridgeToken, "wrong-bridge-token-0123456789abcdef");
            Assert.Equal(HttpStatusCode.Forbidden, (await noBridge.GetAsync("/api/gateway/workspaces")).StatusCode);
        }

        // 2. Missing X-CF-Pairing-Id -> 400
        using (var missingPairing = host.CreateLocalClient())
        {
            missingPairing.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.ControlChannelHeader, "Remote");
            missingPairing.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.BridgeToken, host.BridgeToken);
            missingPairing.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.BindingVersion, "1");
            missingPairing.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.AccountRevocationSeq, "1");
            missingPairing.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.DeviceRevocationSeq, "1");
            Assert.Equal(HttpStatusCode.BadRequest, (await missingPairing.GetAsync("/api/gateway/workspaces")).StatusCode);
        }

        // 3. Internal remote endpoints require both ControlPlane token and bridge token
        using (var controlOnly = host.CreateControlClient())
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await controlOnly.GetAsync("/api/internal/remote/workspace-authorizations")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await controlOnly.GetAsync("/api/internal/remote/host-events")).StatusCode);
        }

        using (var internalClient = host.CreateInternalRemoteClient())
        {
            Assert.Equal(HttpStatusCode.OK, (await internalClient.GetAsync("/api/internal/remote/workspace-authorizations")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await internalClient.GetAsync("/api/internal/remote/host-events")).StatusCode);
        }

        // 4. Revoking P1 via internal endpoint immediately rejects subsequent remote requests from P1
        using (var p1Client = host.CreateRemoteClient(p1))
        {
            Assert.Equal(HttpStatusCode.OK, (await p1Client.GetAsync("/api/gateway/workspaces")).StatusCode);

            using var internalClient = host.CreateInternalRemoteClient();
            var revResp = await internalClient.PostAsJsonAsync(
                "/api/internal/remote/revocations",
                new RemoteRevocationApplyRequest(null, null, [p1.PairingId]));
            revResp.EnsureSuccessStatusCode();

            Assert.Equal(HttpStatusCode.Forbidden, (await p1Client.GetAsync("/api/gateway/workspaces")).StatusCode);
        }
    }

    [Fact]
    public async Task RemoteWorkspaceVisibility_OpenLocalWorkspaceForbidden_AndInternalGrantFlow()
    {
        await using var host = await RemoteTestHost.StartAsync(_root);
        using var localClient = host.CreateLocalClient();

        var bootstrapResp = await localClient.PostAsync("/api/gateway/session/bootstrap", null);
        bootstrapResp.EnsureSuccessStatusCode();
        var defaultSessionId = (await bootstrapResp.Content.ReadFromJsonAsync<SessionBootstrapResponse>())!.SessionId;

        var extDir = Path.Combine(_root, "outside-workspace");
        Directory.CreateDirectory(extDir);
        string extSessionId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            (extSessionId, _, _) = await chat.CreateOrOpenExternalWorkspaceSession(LocalUser.Id, extDir, "ExternalProject");
        }

        var p1 = new RemotePrincipalStamp(Guid.NewGuid(), 1, 0, 0);
        using var remoteClient = host.CreateRemoteClient(p1);
        using var remoteControlClient = host.CreateRemoteClient(p1, useControlToken: true);

        // Unauthorized external workspace is hidden from remote list and blocked on direct access
        var listBefore = await remoteClient.GetFromJsonAsync<CodexFlow.Contracts.HistoryViewModel[]>("/api/gateway/workspaces");
        Assert.NotNull(listBefore);
        Assert.Contains(listBefore!, w => w.SessionId == defaultSessionId);
        Assert.DoesNotContain(listBefore!, w => w.SessionId == extSessionId);

        Assert.Equal(HttpStatusCode.Forbidden, (await remoteClient.GetAsync($"/api/gateway/session/{extSessionId}/tasklist")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await remoteClient.GetAsync($"/api/gateway/session/{extSessionId}/browser")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await remoteClient.GetAsync($"/api/gateway/session/{extSessionId}/computer")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await remoteControlClient.PostAsJsonAsync("/api/gateway/computer/emergency-stop", new ComputerEmergencyStopRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await remoteControlClient.PostAsJsonAsync("/api/gateway/computer/emergency-stop", new ComputerEmergencyStopRequest(extSessionId))).StatusCode);

        // OpenLocalWorkspace (POST /api/gateway/workspaces) is strictly forbidden in remote channel even with control token
        var openLocalResp = await remoteControlClient.PostAsJsonAsync(
            "/api/gateway/workspaces",
            new OpenLocalWorkspaceRequest(extDir, "Hack"));
        Assert.Equal(HttpStatusCode.Forbidden, openLocalResp.StatusCode);

        // Explicit desktop grant via internal endpoint exposes the external session and lifts 403
        using (var internalClient = host.CreateInternalRemoteClient())
        {
            var grantResp = await internalClient.PutAsJsonAsync(
                "/api/internal/remote/workspace-authorizations",
                new RemoteWorkspaceAuthorizationUpdateRequest(extDir, extSessionId, true));
            grantResp.EnsureSuccessStatusCode();
        }

        var listAfter = await remoteClient.GetFromJsonAsync<CodexFlow.Contracts.HistoryViewModel[]>("/api/gateway/workspaces");
        Assert.Contains(listAfter!, w => w.SessionId == extSessionId);

        var tasklistAfter = await remoteClient.GetAsync($"/api/gateway/session/{extSessionId}/tasklist");
        Assert.NotEqual(HttpStatusCode.Forbidden, tasklistAfter.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await remoteClient.GetAsync($"/api/gateway/session/{extSessionId}/browser")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await remoteClient.GetAsync($"/api/gateway/session/{extSessionId}/computer")).StatusCode);
    }

    [Fact]
    public async Task RemoteUploadAndChatIdempotency_RestrictsFields_WorkspaceReference_AndEnvelopeCheck()
    {
        await using var host = await RemoteTestHost.StartAsync(_root);
        using var localClient = host.CreateLocalClient();
        var bootstrapResp = await localClient.PostAsync("/api/gateway/session/bootstrap", null);
        bootstrapResp.EnsureSuccessStatusCode();
        var sessionId = (await bootstrapResp.Content.ReadFromJsonAsync<SessionBootstrapResponse>())!.SessionId;

        var p1 = new RemotePrincipalStamp(Guid.NewGuid(), 1, 0, 0);
        var p2 = new RemotePrincipalStamp(Guid.NewGuid(), 1, 0, 0);

        var fileBytes = Encoding.UTF8.GetBytes("remote uploaded plain text content");
        var fileSha256 = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
        const string uploadId = "up-http-1";

        UploadedAttachmentDto uploadedDto;
        using (var uploadClient = host.CreateRemoteClient(p1, uploadId: uploadId, fileSha256: fileSha256))
        {
            using var form = new MultipartFormDataContent();
            var byteContent = new ByteArrayContent(fileBytes);
            byteContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(byteContent, "file", "spec.txt");

            var uploadResp = await uploadClient.PostAsync($"/api/gateway/session/{sessionId}/attachments", form);
            Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);
            uploadedDto = (await uploadResp.Content.ReadFromJsonAsync<UploadedAttachmentDto>())!;
            Assert.StartsWith(".codexflow-attachments/", uploadedDto.RelativePath, StringComparison.Ordinal);

            var statusResp = await uploadClient.GetFromJsonAsync<RemoteAttachmentUploadStatusResponse>(
                $"/api/gateway/session/{sessionId}/attachments/uploads/{uploadId}?fileSha256={fileSha256}");
            Assert.NotNull(statusResp);
            Assert.Equal("completed", statusResp!.Status);
            Assert.Equal(uploadedDto.RelativePath, statusResp.Attachment!.RelativePath);

            var conflictStatusResp = await uploadClient.GetAsync(
                $"/api/gateway/session/{sessionId}/attachments/uploads/{uploadId}?fileSha256={new string('0', 64)}");
            Assert.Equal(HttpStatusCode.Conflict, conflictStatusResp.StatusCode);
        }

        // Missing X-CF-Client-Op-Id -> 400
        using (var noOpClient = host.CreateRemoteClient(p1))
        {
            var resp = await noOpClient.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest
            {
                SessionId = sessionId,
                Message = "hello"
            });
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        // Restricted fields -> 400
        using (var restrictedClient = host.CreateRemoteClient(p1, clientOpId: "op-restricted-1"))
        {
            var r1 = await restrictedClient.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest
            {
                SessionId = sessionId,
                Message = "hello",
                GitRepoUrl = "https://github.com/org/repo"
            });
            Assert.Equal(HttpStatusCode.BadRequest, r1.StatusCode);

            var r2 = await restrictedClient.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest
            {
                SessionId = sessionId,
                Message = "hello",
                ApproveGitPush = true
            });
            Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
        }

        // isWorkspaceReference: true -> 400
        using (var wsRefClient = host.CreateRemoteClient(p1, clientOpId: "op-wsref-1"))
        {
            var resp = await wsRefClient.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest
            {
                SessionId = sessionId,
                Message = "read file",
                Attachments = [new ChatAttachmentDto("secret.txt", "secret.txt", 10, "text/plain", IsWorkspaceReference: true)]
            });
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        // Pairing P2 referencing P1's attachment -> 403
        using (var p2Client = host.CreateRemoteClient(p2, clientOpId: "op-cross-pairing-1"))
        {
            var resp = await p2Client.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest
            {
                SessionId = sessionId,
                Message = "steal attachment",
                Attachments = [new ChatAttachmentDto(uploadedDto.Name, uploadedDto.RelativePath, uploadedDto.SizeBytes, uploadedDto.ContentType)]
            });
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // P1 idempotent retry returns identical messageId (202); same opId with different body -> 409
        using (var p1ChatClient = host.CreateRemoteClient(p1, clientOpId: "op-1"))
        {
            var req1 = new GatewayChatRequest
            {
                SessionId = sessionId,
                Message = "Analyze attached spec",
                Attachments = [new ChatAttachmentDto(uploadedDto.Name, uploadedDto.RelativePath, uploadedDto.SizeBytes, uploadedDto.ContentType)]
            };
            var firstResp = await p1ChatClient.PostAsJsonAsync("/api/gateway/chat", req1);
            Assert.Equal(HttpStatusCode.Accepted, firstResp.StatusCode);
            var firstBody = (await firstResp.Content.ReadFromJsonAsync<GatewayChatAcceptedResponse>())!;

            var replayResp = await p1ChatClient.PostAsJsonAsync("/api/gateway/chat", req1);
            Assert.Equal(HttpStatusCode.Accepted, replayResp.StatusCode);
            var replayBody = (await replayResp.Content.ReadFromJsonAsync<GatewayChatAcceptedResponse>())!;
            Assert.Equal(firstBody.MessageId, replayBody.MessageId);

            var conflictResp = await p1ChatClient.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest
            {
                SessionId = sessionId,
                Message = "Different message with same op-1"
            });
            Assert.Equal(HttpStatusCode.Conflict, conflictResp.StatusCode);

            var opStatus = await p1ChatClient.GetFromJsonAsync<RemoteChatOpStatusResponse>(
                $"/api/gateway/session/{sessionId}/ops/op-1");
            Assert.NotNull(opStatus);
            Assert.Equal(firstBody.MessageId, opStatus!.MessageId);

            var opConflict = await p1ChatClient.GetAsync(
                $"/api/gateway/session/{sessionId}/ops/op-1?requestDigest={new string('0', 64)}");
            Assert.Equal(HttpStatusCode.Conflict, opConflict.StatusCode);
        }

        // Structured Approve without X-CF-Verified-Envelope-* headers -> 403
        using (var unverifiedEnvClient = host.CreateRemoteClient(p1, useControlToken: true, clientOpId: "op-plan-no-env"))
        {
            var resp = await unverifiedEnvClient.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest
            {
                SessionId = sessionId,
                Message = "approve",
                StructuredAction = new GatewayStructuredActionRequest
                {
                    Action = "approve",
                    PlanArtifactId = "plan-1",
                    PlanVersion = "v1",
                    PlanHash = new string('a', 64)
                }
            });
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
    }

    [Fact]
    public async Task RemoteBackgroundJobEndpoints_BlockRetryResumeContinue_With403()
    {
        await using var host = await RemoteTestHost.StartAsync(_root);
        var p1 = new RemotePrincipalStamp(Guid.NewGuid(), 1, 0, 0);
        using var remoteClient = host.CreateRemoteClient(p1);

        var retryResp = await remoteClient.PostAsync("/api/jobs/any-job/retry", null);
        Assert.Equal(HttpStatusCode.Forbidden, retryResp.StatusCode);

        var resumeResp = await remoteClient.PostAsJsonAsync("/api/jobs/any-job/resume", new { resumeToken = "tok" });
        Assert.Equal(HttpStatusCode.Forbidden, resumeResp.StatusCode);

        var continueResp = await remoteClient.PostAsJsonAsync("/api/jobs/any-job/continue", new { prompt = "continue" });
        Assert.Equal(HttpStatusCode.Forbidden, continueResp.StatusCode);
    }

    [Fact]
    public async Task RemoteSse_IgnoresLastEventId_AndSanitizesPreviewImageBase64()
    {
        await using var host = await RemoteTestHost.StartAsync(_root);
        using var localClient = host.CreateLocalClient();
        var bootstrapResp = await localClient.PostAsync("/api/gateway/session/bootstrap", null);
        bootstrapResp.EnsureSuccessStatusCode();
        var sessionId = (await bootstrapResp.Content.ReadFromJsonAsync<SessionBootstrapResponse>())!.SessionId;

        await host.Gateway.PublishEventAsync(sessionId, new GatewaySseEvent
        {
            Type = GatewaySseEventType.ComputerApprovalRequested,
            Payload = new ComputerApprovalRequestDto(
                "cappr-sse-1",
                "digest-1",
                sessionId,
                "computer_act",
                "click",
                "notepad",
                "Notes",
                "0x1",
                "click",
                "Ask",
                10,
                20,
                DateTimeOffset.UtcNow.AddMinutes(5),
                ["allow_once", "deny"],
                true,
                true,
                PreviewImageBase64: "SECRET_BASE64_DATA")
        });

        var p1 = new RemotePrincipalStamp(Guid.NewGuid(), 1, 0, 0);
        using var remoteClient = host.CreateRemoteClient(p1);
        long firstEventId = 0;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            // Unauthenticated Last-Event-ID header must be ignored when query lastEventId is absent
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/gateway/sse?sessionId={Uri.EscapeDataString(sessionId)}");
            req.Headers.TryAddWithoutValidation("Last-Event-ID", "999999");

            using var resp = await remoteClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            var collected = new StringBuilder();
            while (!cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }

                collected.AppendLine(line);
                if (line.StartsWith("id:", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(3).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
                {
                    firstEventId = parsedId;
                }
                if (line.StartsWith("data:", StringComparison.Ordinal) &&
                    line.Contains("cappr-sse-1", StringComparison.Ordinal))
                {
                    break;
                }
            }

            var sseText = collected.ToString();
            Assert.Contains("cappr-sse-1", sseText, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET_BASE64_DATA", sseText, StringComparison.Ordinal);
            Assert.DoesNotContain("previewImageBase64", sseText, StringComparison.OrdinalIgnoreCase);
            Assert.True(firstEventId > 0);
        }

        // Signed query lastEventId resumes from subsequent events without replaying event 1
        await host.Gateway.PublishEventAsync(sessionId, new GatewaySseEvent
        {
            Type = GatewaySseEventType.ComputerApprovalRequested,
            Payload = new ComputerApprovalRequestDto(
                "cappr-sse-2",
                "digest-2",
                sessionId,
                "computer_act",
                "click",
                "notepad",
                "Notes",
                "0x1",
                "click",
                "Ask",
                10,
                20,
                DateTimeOffset.UtcNow.AddMinutes(5),
                ["allow_once", "deny"],
                true,
                true,
                PreviewImageBase64: "SECRET_2")
        });

        using (var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        using (var req2 = new HttpRequestMessage(HttpMethod.Get, $"/api/gateway/sse?sessionId={Uri.EscapeDataString(sessionId)}&lastEventId={firstEventId}"))
        using (var resp2 = await remoteClient.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, cts2.Token))
        {
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
            await using var stream2 = await resp2.Content.ReadAsStreamAsync(cts2.Token);
            using var reader2 = new StreamReader(stream2);
            var collected2 = new StringBuilder();
            while (!cts2.IsCancellationRequested)
            {
                var line = await reader2.ReadLineAsync(cts2.Token);
                if (line is null)
                {
                    break;
                }

                collected2.AppendLine(line);
                if (line.StartsWith("data:", StringComparison.Ordinal) &&
                    line.Contains("cappr-sse-2", StringComparison.Ordinal))
                {
                    break;
                }
            }

            var text2 = collected2.ToString();
            Assert.Contains("cappr-sse-2", text2, StringComparison.Ordinal);
            Assert.DoesNotContain("cappr-sse-1", text2, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET_2", text2, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ComputeRemoteChatDigest_MatchesFrozenChatOpV1Spec()
    {
        var req = new GatewayChatRequest
        {
            SessionId = "sess-001",
            ConversationId = "conv-001",
            ConversationMode = "default",
            Message = "Line 1\nLine 2"
        };
        var uploads = new List<VerifiedRemoteAttachment>
        {
            new("up-2", ".codexflow-attachments/b.txt", "b.txt", "text/plain", new string('b', 64), 10),
            new("up-1", ".codexflow-attachments/a.txt", "a.txt", "text/plain", new string('a', 64), 10)
        };
        var actual = CodexGatewayController.ComputeRemoteChatDigest(req, uploads);
        var expectedPayload = "CF-CHAT-OP-V1\nsess-001\nconv-001\ndefault\nup-1,up-2\n\nLine 1\nLine 2";
        var expectedDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expectedPayload))).ToLowerInvariant();
        Assert.Equal(expectedDigest, actual);

        var actionReq = new GatewayChatRequest
        {
            SessionId = "sess-001",
            ConversationMode = "PLAN",
            Message = "",
            StructuredAction = new GatewayStructuredActionRequest
            {
                Action = " Approve ",
                PlanArtifactId = "plan-1",
                PlanVersion = "a1b2c3d4",
                PlanHash = new string('f', 64),
                IdempotencyKey = "idem-1",
                StartExecution = false
            }
        };
        var actionActual = CodexGatewayController.ComputeRemoteChatDigest(actionReq, null);
        var actionExpected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"CF-CHAT-OP-V1\nsess-001\n\nplan\n\napprove|plan-1|a1b2c3d4|{new string('f', 64)}|idem-1|false\n"))).ToLowerInvariant();
        Assert.Equal(actionExpected, actionActual);
    }

    [Fact]
    public async Task SessionChannelRevocation_CancelsRevokedRemoteTurn_MarksQueuedOpCancelled_AndNeverCancelsLocalTurn()
    {
        var processor = new ControlledGatewayProcessor();
        await using var host = await RemoteTestHost.StartAsync(_root, processor);

        using var localClient = host.CreateLocalClient();
        var bootstrapResp = await localClient.PostAsync("/api/gateway/session/bootstrap", null);
        bootstrapResp.EnsureSuccessStatusCode();
        var sessionId = (await bootstrapResp.Content.ReadFromJsonAsync<SessionBootstrapResponse>())!.SessionId;

        var p1 = new RemotePrincipalStamp(Guid.NewGuid(), 1, 0, 0);

        // Scenario A: Active local turn stays uncancelled across remote revocation
        var localTurnResp = await localClient.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest
        {
            SessionId = sessionId,
            Message = "local-turn"
        });
        Assert.Equal(HttpStatusCode.Accepted, localTurnResp.StatusCode);
        var localRun = await processor.WaitForInvocationAsync("local-turn");

        using (var internalClient = host.CreateInternalRemoteClient())
        {
            var revResp = await internalClient.PostAsJsonAsync(
                "/api/internal/remote/revocations",
                new RemoteRevocationApplyRequest(null, null, [p1.PairingId]));
            revResp.EnsureSuccessStatusCode();
            var revBody = (await revResp.Content.ReadFromJsonAsync<RemoteRevocationApplyResponse>())!;
            Assert.Equal(0, revBody.CancelledActiveRemoteTurns);
        }

        Assert.False(localRun.Token.IsCancellationRequested);
        localRun.Release();
        await localRun.Completed.WaitAsync(TimeSpan.FromSeconds(5));

        // Wait briefly for SessionChannel to release UserChat exclusive slot
        await Task.Delay(50);

        // Scenario B: Active P2 remote turn + queued P2 message in _inbox; revoking P2 cancels active turn and skips queued message
        var p2 = new RemotePrincipalStamp(Guid.NewGuid(), 1, 0, 0);
        using (var p2Client = host.CreateRemoteClient(p2, clientOpId: "op-active-p2"))
        {
            var remoteActiveResp = await p2Client.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest
            {
                SessionId = sessionId,
                Message = "remote-active-turn"
            });
            Assert.Equal(HttpStatusCode.Accepted, remoteActiveResp.StatusCode);
        }

        var remoteActiveRun = await processor.WaitForInvocationAsync("remote-active-turn");

        // Reserve queued op in store & enqueue behind active turn via non-exclusive SystemEvent source
        var opStore = host.Services.GetRequiredService<IRemoteChatOpStore>();
        var p2IdStr = p2.PairingId.ToString("D");
        await opStore.TryReserveAsync(new RemoteChatOpRecord(
            p2IdStr, sessionId, "op-queued-1", new string('e', 64), "queued", null, "msg-queued-1",
            p2.BindingVersion, p2.AccountRevocationSeq, p2.DeviceRevocationSeq, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        await host.Gateway.EnqueueAsync(new GatewayMessage
        {
            MessageId = "msg-queued-1",
            Source = GatewayMessageSource.SystemEvent,
            SessionId = sessionId,
            UserId = LocalUser.Id,
            Content = "remote-queued-turn",
            ControlChannel = ControlChannel.Remote,
            RemoteStamp = p2,
            RemoteClientOpId = "op-queued-1"
        });

        using (var internalClient = host.CreateInternalRemoteClient())
        {
            var revResp = await internalClient.PostAsJsonAsync(
                "/api/internal/remote/revocations",
                new RemoteRevocationApplyRequest(null, null, [p2.PairingId]));
            revResp.EnsureSuccessStatusCode();
            var revBody = (await revResp.Content.ReadFromJsonAsync<RemoteRevocationApplyResponse>())!;
            Assert.Equal(1, revBody.CancelledActiveRemoteTurns);
        }

        await remoteActiveRun.Cancelled.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(remoteActiveRun.Token.IsCancellationRequested);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        RemoteChatOpRecord? queuedOp = null;
        while (DateTime.UtcNow < deadline)
        {
            queuedOp = await opStore.FindAsync(p2IdStr, sessionId, "op-queued-1");
            if (queuedOp is not null && string.Equals(queuedOp.Status, "cancelled_revoked", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.NotNull(queuedOp);
        Assert.Equal("cancelled_revoked", queuedOp!.Status);
        Assert.DoesNotContain("remote-queued-turn", processor.InvokedContents);
    }

    private sealed class ControlledGatewayProcessor : IGatewayMessageProcessor
    {
        private readonly ConcurrentQueue<string> _invoked = new();
        private readonly ConcurrentDictionary<string, TurnHandle> _handles = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<TurnHandle>> _waiters = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> InvokedContents => _invoked.ToArray();

        public Task<TurnHandle> WaitForInvocationAsync(string contentMarker)
        {
            if (_handles.TryGetValue(contentMarker, out var existing))
            {
                return Task.FromResult(existing);
            }

            var tcs = _waiters.GetOrAdd(contentMarker, _ => new TaskCompletionSource<TurnHandle>(TaskCreationOptions.RunContinuationsAsynchronously));
            if (_handles.TryGetValue(contentMarker, out existing))
            {
                tcs.TrySetResult(existing);
            }

            return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task ProcessAsync(GatewayMessage message, Func<GatewaySseEvent, ValueTask> emit, CancellationToken ct)
        {
            _invoked.Enqueue(message.Content);
            var handle = new TurnHandle(ct);
            _handles[message.Content] = handle;
            if (_waiters.TryGetValue(message.Content, out var waiter))
            {
                waiter.TrySetResult(handle);
            }

            await handle.WaitForReleaseOrCancelAsync().ConfigureAwait(false);
        }

        public sealed class TurnHandle
        {
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenRegistration _reg;

            public TurnHandle(CancellationToken token)
            {
                Token = token;
                _reg = token.Register(() => _cancelled.TrySetResult());
            }

            public CancellationToken Token { get; }
            public Task Cancelled => _cancelled.Task;
            public Task Completed => _completed.Task;
            public void Release() => _release.TrySetResult();

            public async Task WaitForReleaseOrCancelAsync()
            {
                try
                {
                    await _release.Task.WaitAsync(Token).ConfigureAwait(false);
                }
                finally
                {
                    if (Token.IsCancellationRequested)
                    {
                        _cancelled.TrySetResult();
                    }

                    _reg.Dispose();
                    _completed.TrySetResult();
                }
            }
        }
    }

    private sealed class RemoteTestHost : IAsyncDisposable
    {
        private const string NormalToken = "remote-test-normal-token-111111111111";
        private const string ControlToken = "remote-test-control-token-222222222222";
        private const string BridgeTokenValue = "remote-test-bridge-token-333333333333";

        private readonly WebApplication _app;

        private RemoteTestHost(WebApplication app, Uri baseUri)
        {
            _app = app;
            BaseUri = baseUri;
        }

        public Uri BaseUri { get; }
        public string BridgeToken => BridgeTokenValue;
        public IServiceProvider Services => _app.Services;
        public ICodexGateway Gateway => _app.Services.GetRequiredService<ICodexGateway>();

        public HttpClient CreateLocalClient()
        {
            var client = new HttpClient { BaseAddress = BaseUri };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", NormalToken);
            return client;
        }

        public HttpClient CreateControlClient()
        {
            var client = new HttpClient { BaseAddress = BaseUri };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);
            return client;
        }

        public HttpClient CreateInternalRemoteClient()
        {
            var client = CreateControlClient();
            client.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.BridgeToken, BridgeTokenValue);
            return client;
        }

        public HttpClient CreateRemoteClient(
            RemotePrincipalStamp stamp,
            bool useControlToken = false,
            string? clientOpId = null,
            string? uploadId = null,
            string? fileSha256 = null,
            string? verifiedEnvelopeKind = null,
            string? verifiedEnvelopeHash = null)
        {
            var client = useControlToken ? CreateControlClient() : CreateLocalClient();
            client.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.ControlChannelHeader, "Remote");
            client.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.BridgeToken, BridgeTokenValue);
            client.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.PairingId, stamp.PairingId.ToString("D"));
            client.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.BindingVersion, stamp.BindingVersion.ToString(CultureInfo.InvariantCulture));
            client.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.AccountRevocationSeq, stamp.AccountRevocationSeq.ToString(CultureInfo.InvariantCulture));
            client.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.DeviceRevocationSeq, stamp.DeviceRevocationSeq.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(clientOpId))
            {
                client.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.ClientOpId, clientOpId);
            }

            if (!string.IsNullOrWhiteSpace(uploadId))
            {
                client.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.UploadId, uploadId);
            }

            if (!string.IsNullOrWhiteSpace(fileSha256))
            {
                client.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.FileSha256, fileSha256);
            }

            if (!string.IsNullOrWhiteSpace(verifiedEnvelopeKind))
            {
                client.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.VerifiedEnvelopeKind, verifiedEnvelopeKind);
            }

            if (!string.IsNullOrWhiteSpace(verifiedEnvelopeHash))
            {
                client.DefaultRequestHeaders.Add(RemoteBridgeHeaderNames.VerifiedEnvelopeHash, verifiedEnvelopeHash);
            }

            return client;
        }

        public static async Task<RemoteTestHost> StartAsync(string root, IGatewayMessageProcessor? customProcessor = null)
        {
            var dataDir = Path.Combine(root, "host-data");
            var credDir = Path.Combine(dataDir, "creds");
            var workspacesRoot = Path.Combine(dataDir, "workspaces");
            var globalSettings = Path.Combine(root, "global", "appsettings.json");
            var dbPath = Path.Combine(dataDir, "state.db");
            Directory.CreateDirectory(workspacesRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(globalSettings)!);

            using var pipe = new StringReader(JsonSerializer.Serialize(new
            {
                accessToken = NormalToken,
                controlToken = ControlToken,
                remoteBridgeToken = BridgeTokenValue
            }) + "\n");

            var creds = LocalHostCredentials.Initialize(
                credDir,
                LocalTokenDeliveryMode.Pipe,
                ExecutionBackend.Local,
                unsafeFileControlToken: false,
                pipeReader: pipe);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            var optionsMonitor = new CodexFlow.Core.Tests.Execution.ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(
                new ExecutionOptions { Backend = ExecutionBackend.Local, PermissionMode = ExecutionPermissionMode.Ask });
            var clamp = new ExecutionPermissionClamp();
            var permStore = new ExecutionPermissionStore(Path.Combine(Path.GetDirectoryName(globalSettings)!, "permissions.json"));
            var execSettings = new ExecutionSettingsService(globalSettings);
            var modelSettings = new ModelSettingsService(globalSettings);
            var workspaceSettings = new WorkspaceSettingsService(globalSettings);

            builder.Services.AddCodexFlowGateway();
            builder.Services.AddSingleton(creds);
            builder.Services.AddSingleton<IControlPlaneStateProvider>(creds);
            builder.Services.AddSingleton<IRemoteBridgeAuthenticator>(creds);
            builder.Services.AddSingleton<IOptionsMonitor<ExecutionOptions>>(optionsMonitor);
            builder.Services.AddSingleton(clamp);
            builder.Services.AddSingleton<IExecutionPermissionStore>(permStore);
            builder.Services.AddSingleton<IExecutionSettingsService>(execSettings);
            builder.Services.AddSingleton<IModelSettingsService>(modelSettings);
            builder.Services.AddSingleton<IWorkspaceSettingsService>(workspaceSettings);
            builder.Services.AddSingleton<IWorkspaceRootProvider>(new WorkspaceRootProvider(workspacesRoot, dataDir));
            builder.Services.AddSingleton(Mock.Of<IChatClient>());
            builder.Services.AddCodexFlowRuntime(builder.Configuration);
            builder.Services.RemoveAll<IHostedService>();
            builder.Services.AddCodexFlowSqliteStorage(dbPath);
            builder.Services.AddCodexFlowLocalDefaults();

            builder.Services.RemoveAll<IGatewayMessageProcessor>();
            if (customProcessor is not null)
            {
                builder.Services.AddScoped(_ => customProcessor);
            }
            else
            {
                builder.Services.AddScoped<IGatewayMessageProcessor>(_ => Mock.Of<IGatewayMessageProcessor>());
            }

            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, LocalTokenAuthenticationHandler>(JwtBearerDefaults.AuthenticationScheme, null);
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("ActiveUser", p => p.RequireAuthenticatedUser());
                options.AddPolicy(
                    ControlPlaneAuthConstants.ControlPlanePolicy,
                    p => p.RequireAuthenticatedUser().RequireClaim(ControlPlaneAuthConstants.PlaneClaimType, ControlPlaneAuthConstants.PlaneControl));
            });

            var app = builder.Build();
            await app.Services.GetRequiredService<SqliteSchemaMigrator>().MigrateAsync();

            app.UseAuthentication();
            app.UseAuthorization();
            app.UseCodexFlowGatewayBindingErrors();
            app.MapCodexFlowGateway();
            await app.StartAsync();

            var baseUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            return new RemoteTestHost(app, baseUri);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
        }
    }
}
