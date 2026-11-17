// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.BusinessSkills;
using CodexFlow.Core.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodexFlow.Core.Tests.BusinessSkills;

public sealed class P1SecurityAndCredentialTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "codexflow-p1-sec-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            LocalExecutionService.TryDeleteDirectory(_tempRoot);
        }
    }

    [Fact]
    public void BusinessSkillsOptions_FeatureSwitchDefaultsToDisabled()
    {
        var opts = new BusinessSkillsOptions();
        Assert.False(opts.Enabled);
    }

    [Fact]
    public void TargetGuard_PermitsAuthorizedIntranet_AndBlocksMetadataSchemeTraversalBasicHttpAndControlPorts()
    {
        var guard = new OutboundTargetPolicyGuard();
        guard.RegisterProtectedLocalPort(5206);

        var httpsIntranetPolicy = new EndpointPolicyDto
        {
            AllowedOrigin = "https://10.16.0.25:8443",
            AllowedPathPrefix = "/erp/v1"
        };

        var okIntranet = guard.ValidateUri(
            new Uri("https://10.16.0.25:8443/erp/v1/invoices?page=1"),
            httpsIntranetPolicy,
            BusinessAuthKind.BasicTlsOnly,
            OutboundRequestPurpose.Execution);
        Assert.True(okIntranet.Allowed);

        var plainHttpIntranetPolicy = new EndpointPolicyDto
        {
            AllowedOrigin = "http://192.168.10.20:8080",
            AllowedPathPrefix = "/api",
            AllowPlainHttpForIntranet = true
        };

        Assert.True(guard.ValidateUri(
            new Uri("http://192.168.10.20:8080/api/customers"),
            plainHttpIntranetPolicy,
            BusinessAuthKind.Bearer,
            OutboundRequestPurpose.Discovery).Allowed);

        // Basic 即使在内网 HTTP 开关下也必须强制 TLS
        var basicOverHttp = guard.ValidateUri(
            new Uri("http://192.168.10.20:8080/api/customers"),
            plainHttpIntranetPolicy,
            BusinessAuthKind.BasicTlsOnly,
            OutboundRequestPurpose.Execution);
        Assert.False(basicOverHttp.Allowed);
        Assert.Equal(BusinessSkillErrorCodes.AuthRequired, basicOverHttp.ErrorCode);

        // 非 HTTP(S) 协议
        var fileUri = guard.ValidateUri(new Uri("file:///C:/Windows/System32/drivers/etc/hosts"), httpsIntranetPolicy, BusinessAuthKind.None, OutboundRequestPurpose.Discovery);
        Assert.False(fileUri.Allowed);
        Assert.Equal(BusinessSkillErrorCodes.UnsupportedProtocol, fileUri.ErrorCode);

        // UserInfo 偷渡
        var userInfoUri = guard.ValidateUri(new Uri("https://admin:pwd@10.16.0.25:8443/erp/v1/invoices"), httpsIntranetPolicy, BusinessAuthKind.None, OutboundRequestPurpose.Discovery);
        Assert.False(userInfoUri.Allowed);

        // 路径穿越与前缀逃逸
        var traversalUri = guard.ValidateUri(new Uri("https://10.16.0.25:8443/erp/v1/%2e%2e/admin"), httpsIntranetPolicy, BusinessAuthKind.None, OutboundRequestPurpose.Execution);
        Assert.False(traversalUri.Allowed);
        Assert.Equal(BusinessSkillErrorCodes.SelectionViolation, traversalUri.ErrorCode);

        var outsidePrefix = guard.ValidateUri(new Uri("https://10.16.0.25:8443/other/secret"), httpsIntranetPolicy, BusinessAuthKind.None, OutboundRequestPurpose.Execution);
        Assert.False(outsidePrefix.Allowed);

        // 跨 Origin 与未授权外部 $ref
        var evilOrigin = guard.ValidateUri(new Uri("https://evil.example/erp/v1/invoices"), httpsIntranetPolicy, BusinessAuthKind.None, OutboundRequestPurpose.Execution);
        Assert.False(evilOrigin.Allowed);

        var unapprovedExternalRef = guard.ValidateUri(new Uri("https://schemas.evil.example/common.yaml"), httpsIntranetPolicy, BusinessAuthKind.None, OutboundRequestPurpose.ExternalRef);
        Assert.False(unapprovedExternalRef.Allowed);

        // 授权的外部 $ref Origin 允许，未授权的仍禁止
        var refPolicy = httpsIntranetPolicy with
        {
            AllowExternalOpenApiRefs = true,
            AllowedExternalRefOrigins = ["https://schemas.corp.local"]
        };
        Assert.True(guard.ValidateUri(new Uri("https://schemas.corp.local/shared.yaml"), refPolicy, BusinessAuthKind.None, OutboundRequestPurpose.ExternalRef).Allowed);
        Assert.False(guard.ValidateUri(new Uri("https://schemas.evil.example/shared.yaml"), refPolicy, BusinessAuthKind.None, OutboundRequestPurpose.ExternalRef).Allowed);

        // 云元数据 IP 无论是否写进 AllowedOrigin 都必须拦截
        var metadataPolicy = new EndpointPolicyDto
        {
            AllowedOrigin = "http://169.254.169.254",
            AllowPlainHttpForIntranet = true
        };
        var metadataEval = guard.ValidateUri(new Uri("http://169.254.169.254/latest/meta-data"), metadataPolicy, BusinessAuthKind.None, OutboundRequestPurpose.Discovery);
        Assert.False(metadataEval.Allowed);
        Assert.Equal(BusinessSkillErrorCodes.SelectionViolation, metadataEval.ErrorCode);

        // 宿主控制端口禁止被插件连接
        var selfPortPolicy = new EndpointPolicyDto
        {
            AllowedOrigin = "http://127.0.0.1:5206",
            AllowPlainHttpForIntranet = true
        };
        var selfPortEval = guard.ValidateUri(new Uri("http://127.0.0.1:5206/api/plugins"), selfPortPolicy, BusinessAuthKind.None, OutboundRequestPurpose.Discovery);
        Assert.False(selfPortEval.Allowed);
        Assert.Equal(BusinessSkillErrorCodes.SelectionViolation, selfPortEval.ErrorCode);

        // IPv4-mapped IPv6 云元数据同样封禁
        Assert.False(guard.IsIpAddressAllowed(IPAddress.Parse("::ffff:169.254.169.254"), 80, true, out _));
        Assert.False(guard.IsIpAddressAllowed(IPAddress.Parse("100.100.100.200"), 80, true, out _));
        Assert.False(guard.IsIpAddressAllowed(IPAddress.Parse("fd00:ec2::254"), 80, true, out _));
    }

    [Fact]
    public async Task GuardedHttpClient_ConnectsThroughValidatedConnectCallback_AndBlocksRedirectsOrProtectedPort()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();

        app.MapGet("/api/ping", () => Results.Text("pong"));
        app.MapGet("/api/redirect", () => Results.Redirect("https://other.example/steal-header", permanent: false));
        await app.StartAsync();

        try
        {
            var serverUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            var guard = new OutboundTargetPolicyGuard();
            guard.RegisterProtectedLocalPort(59999);

            var policy = new EndpointPolicyDto
            {
                AllowedOrigin = OutboundTargetPolicyGuard.GetOriginString(serverUri),
                AllowedPathPrefix = "/api",
                AllowPlainHttpForIntranet = true
            };

            using var guarded = guard.CreateGuardedHttpClient(policy, BusinessAuthKind.Bearer, OutboundRequestPurpose.Discovery);
            var okResp = await guarded.GetAsync(new Uri(serverUri, "/api/ping"));
            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
            Assert.Equal("pong", await okResp.Content.ReadAsStringAsync());

            // 重定向不会自动跟随到 cross-origin
            var redirectResp = await guarded.GetAsync(new Uri(serverUri, "/api/redirect"));
            Assert.Equal(HttpStatusCode.Redirect, redirectResp.StatusCode);

            // 若端口被注册为宿主自保护控制面端口，ConnectCallback 直接拒绝 TCP 连接
            guard.RegisterProtectedLocalPort(serverUri.Port);
            await Assert.ThrowsAsync<HttpRequestException>(() => guarded.GetAsync(new Uri(serverUri, "/api/ping")));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task DpapiCredentialStore_EncryptsSecretOnDisk_RejectsCrossRefTransplant_AndMasksHint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var credDir = Path.Combine(_tempRoot, "creds");
        var store = new DpapiBusinessCredentialStore(credDir);
        Assert.True(store.IsPersistentProtectionAvailable);

        const string rawSecret = """{"token":"live-enterprise-token-987654"}""";
        var stored = await store.StoreSecretAsync("cred:plg-1:execution", BusinessAuthKind.Bearer, rawSecret, "rev-1");

        Assert.Equal("token:****7654", stored.Summary.MaskedHint);
        Assert.DoesNotContain("live-enterprise-token-987654", stored.Summary.MaskedHint, StringComparison.Ordinal);

        var cipherPath = Path.Combine(credDir, stored.CipherFileName);
        Assert.True(File.Exists(cipherPath));
        var diskBytes = await File.ReadAllBytesAsync(cipherPath);
        var diskText = Encoding.UTF8.GetString(diskBytes);
        Assert.DoesNotContain("live-enterprise-token-987654", diskText, StringComparison.Ordinal);

        var decrypted = await store.ReadSecretAsync("cred:plg-1:execution", stored.CipherFileName);
        Assert.Equal(rawSecret, decrypted);

        // 密文绑定了 credentialRef 作为 entropy，跨引用重放解密必须失败
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            store.ReadSecretAsync("cred:plg-other:execution", stored.CipherFileName));

        await store.DeleteSecretAsync("cred:plg-1:execution", stored.CipherFileName);
        Assert.False(File.Exists(cipherPath));
    }
}
