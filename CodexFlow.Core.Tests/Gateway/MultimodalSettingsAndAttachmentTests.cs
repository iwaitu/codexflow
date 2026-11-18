// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CodexFlow.Controllers;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodexFlow.Core.Tests.Gateway;

public sealed class MultimodalSettingsAndAttachmentTests : IDisposable
{
    private readonly string _root;

    public MultimodalSettingsAndAttachmentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "codexflow-mm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup race on Windows.
        }
    }

    [Fact]
    public async Task SaveModelSettings_AlwaysProbesWithBuiltInImage_AndAutoEnablesMultimodalWhenSupported()
    {
        var globalSettings = Path.Combine(_root, "global-auto", "appsettings.json");
        var probe = new StubMultimodalProbe(new MultimodalProbeResult(
            IsSupported: true,
            Status: "Supported",
            Message: "内置测试图片识别成功，已自动启用多模态能力。"));

        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            configureServices: (services, _) => services.AddSingleton<IMultimodalCapabilityProbe>(probe));

        using var controlClient = host.CreateControlClient();
        using var normalClient = host.CreateNormalClient();

        // Even when SupportsMultimodal is false/unchecked, passing probe automatically enables it!
        var putResp = await controlClient.PutAsJsonAsync("/api/settings/model", new UpdateModelSettingsRequest(
            ApiUrl: "https://vision.example.local/v1",
            ApiMode: "ChatCompletions",
            Model: "qwen-vl-test",
            ApiKey: "test-key-vision-1234",
            SupportsMultimodal: false));

        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        var updated = await putResp.Content.ReadFromJsonAsync<ModelSettingsDto>();
        Assert.NotNull(updated);
        Assert.True(updated!.SupportsMultimodal);
        Assert.Equal("Supported", updated.MultimodalProbeStatus);
        Assert.Equal(1, probe.CallCount);

        var fetched = await normalClient.GetFromJsonAsync<ModelSettingsDto>("/api/settings/model");
        Assert.NotNull(fetched);
        Assert.True(fetched!.SupportsMultimodal);
    }

    [Fact]
    public async Task SaveModelSettings_DisablesMultimodalAndExplains_WhenImageProbeFails()
    {
        var globalSettings = Path.Combine(_root, "global-fail", "appsettings.json");
        var probe = new StubMultimodalProbe(new MultimodalProbeResult(
            IsSupported: false,
            Status: "Unsupported",
            Message: "模型接口拒绝了图片输入"));

        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            configureServices: (services, _) => services.AddSingleton<IMultimodalCapabilityProbe>(probe));

        using var controlClient = host.CreateControlClient();

        var putResp = await controlClient.PutAsJsonAsync("/api/settings/model", new UpdateModelSettingsRequest(
            ApiUrl: "https://text-only.example.local/v1",
            ApiMode: "ChatCompletions",
            Model: "deepseek-text",
            ApiKey: "test-key-text-1234",
            SupportsMultimodal: true));

        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        var updated = await putResp.Content.ReadFromJsonAsync<ModelSettingsDto>();
        Assert.NotNull(updated);
        Assert.False(updated!.SupportsMultimodal);
        Assert.Equal("Unsupported", updated.MultimodalProbeStatus);
        Assert.Contains("拒绝了图片输入", updated.MultimodalProbeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveModelSettings_PreservesManualSupportsMultimodal_WhenProbeIsInconclusive()
    {
        var globalSettings = Path.Combine(_root, "global-inconclusive", "appsettings.json");
        var probe = new StubMultimodalProbe(new MultimodalProbeResult(
            IsSupported: false,
            Status: "ProbeFailed",
            Message: "模型未返回有效文本响应，暂未开启多模态。"));

        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            configureServices: (services, _) => services.AddSingleton<IMultimodalCapabilityProbe>(probe));

        using var controlClient = host.CreateControlClient();
        using var normalClient = host.CreateNormalClient();

        var putResp = await controlClient.PutAsJsonAsync("/api/settings/model", new UpdateModelSettingsRequest(
            ApiUrl: "https://vision.example.local/v1",
            ApiMode: "ChatCompletions",
            Model: "qwen3.8-27b-fp8",
            ApiKey: "test-key-vision-1234",
            SupportsMultimodal: true));

        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        var updated = await putResp.Content.ReadFromJsonAsync<ModelSettingsDto>();
        Assert.NotNull(updated);
        Assert.True(updated!.SupportsMultimodal);
        Assert.Equal("ProbeFailed", updated.MultimodalProbeStatus);
        Assert.Contains("已保留你手动开启的多模态设置", updated.MultimodalProbeMessage, StringComparison.Ordinal);

        var fetched = await normalClient.GetFromJsonAsync<ModelSettingsDto>("/api/settings/model");
        Assert.NotNull(fetched);
        Assert.True(fetched!.SupportsMultimodal);
    }

    [Fact]
    public async Task GatewayChat_RejectsImageAttachmentWhenNonMultimodal_ButAcceptsOfficeDocument()
    {
        var globalSettings = Path.Combine(_root, "global-chat", "appsettings.json");
        var probe = new StubMultimodalProbe(new MultimodalProbeResult(false, "Unsupported", "不支持多模态"));

        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            configureServices: (services, _) => services.AddSingleton<IMultimodalCapabilityProbe>(probe));

        using var normalClient = host.CreateNormalClient();

        var boot = await normalClient.PostAsJsonAsync("/api/gateway/session/bootstrap", new { });
        boot.EnsureSuccessStatusCode();
        var session = await boot.Content.ReadFromJsonAsync<SessionBootstrapResponse>();
        Assert.NotNull(session);

        // Upload PNG attachment
        using var imgForm = new MultipartFormDataContent();
        using var imgContent = new ByteArrayContent(DefaultMultimodalCapabilityProbe.BuiltInTestPngBytes.ToArray());
        imgContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        imgForm.Add(imgContent, "file", "shot.png");

        var uploadImg = await normalClient.PostAsync($"/api/gateway/session/{session!.SessionId}/attachments", imgForm);
        uploadImg.EnsureSuccessStatusCode();
        var uploadedImg = await uploadImg.Content.ReadFromJsonAsync<UploadedAttachmentDto>();

        var chatImgResp = await normalClient.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest
        {
            SessionId = session.SessionId,
            Message = "看下这张图",
            Attachments =
            [
                new ChatAttachmentDto(uploadedImg!.Name, uploadedImg.RelativePath, uploadedImg.SizeBytes, uploadedImg.ContentType)
            ]
        });

        Assert.Equal(HttpStatusCode.BadRequest, chatImgResp.StatusCode);
        var errBody = await chatImgResp.Content.ReadAsStringAsync();
        Assert.Contains("multimodal_not_supported", errBody, StringComparison.Ordinal);

        // Office document attachment (.docx) succeeds even on non-multimodal model
        using var docForm = new MultipartFormDataContent();
        using var docContent = new ByteArrayContent([1, 2, 3, 4]);
        docContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        docForm.Add(docContent, "file", "notes.docx");

        var uploadDoc = await normalClient.PostAsync($"/api/gateway/session/{session.SessionId}/attachments", docForm);
        uploadDoc.EnsureSuccessStatusCode();
        var uploadedDoc = await uploadDoc.Content.ReadFromJsonAsync<UploadedAttachmentDto>();

        var chatDocResp = await normalClient.PostAsJsonAsync("/api/gateway/chat", new GatewayChatRequest
        {
            SessionId = session.SessionId,
            Message = "总结这个文档",
            Attachments =
            [
                new ChatAttachmentDto(uploadedDoc!.Name, uploadedDoc.RelativePath, uploadedDoc.SizeBytes, uploadedDoc.ContentType)
            ]
        });
        Assert.Equal(HttpStatusCode.Accepted, chatDocResp.StatusCode);
    }

    private sealed class StubMultimodalProbe(MultimodalProbeResult result) : IMultimodalCapabilityProbe
    {
        public int CallCount { get; private set; }

        public Task<MultimodalProbeResult> ProbeAsync(
            string apiUrl,
            string? apiMode,
            string model,
            string apiKey,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
