// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Gateway.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace CodexFlow.Core.Tests.BusinessSkills;

public sealed class BusinessApprovalAndContractReconciliationTests
{
    [Fact]
    public void ApprovalDigest_BindsAllImmutableExecutionContextFields_AndRestrictsDecisions()
    {
        Assert.Equal(["deny", "allow_once"], BusinessApprovalConstants.AllowedDecisions);

        var expires = new DateTimeOffset(2026, 11, 17, 14, 0, 0, TimeSpan.Zero);
        var baseline = new BusinessApprovalDigestInput(
            RunId: "run-001",
            SkillId: "receivable-collection",
            SkillVersion: 1,
            SkillContentHash: "skillhash-1",
            ConnectionId: "conn-erp",
            ConnectionRevision: 1,
            CredentialRevision: "cred-1",
            CapabilityId: "erp-demo:POST:/api/collection-tasks",
            CatalogSnapshotHash: "snap-hash-1",
            NormalizedArgumentsJson: """{"items":["INV-01","INV-02"]}""",
            BatchItemIds: ["INV-01", "INV-02"],
            ExpiresAtUtc: expires);

        var d1 = baseline.ComputeRequestDigest();
        var d1Repeat = (baseline with { }).ComputeRequestDigest();
        Assert.Equal(d1, d1Repeat);

        // 任一绑定要素变动都必须使批准失效
        Assert.NotEqual(d1, (baseline with { RunId = "run-002" }).ComputeRequestDigest());
        Assert.NotEqual(d1, (baseline with { SkillContentHash = "skillhash-2" }).ComputeRequestDigest());
        Assert.NotEqual(d1, (baseline with { ConnectionRevision = 2 }).ComputeRequestDigest());
        Assert.NotEqual(d1, (baseline with { CredentialRevision = "cred-2" }).ComputeRequestDigest());
        Assert.NotEqual(d1, (baseline with { CatalogSnapshotHash = "snap-hash-2" }).ComputeRequestDigest());
        Assert.NotEqual(d1, (baseline with { BatchItemIds = ["INV-01", "INV-02", "INV-03"] }).ComputeRequestDigest());
        Assert.NotEqual(d1, (baseline with { ExpiresAtUtc = expires.AddMinutes(1) }).ComputeRequestDigest());
    }

    [Fact]
    public void ApprovalDigest_PreservesLegacyHashes()
    {
        // Expected hashes were captured from the reflection serializer before the AOT change.
        var expires = new DateTimeOffset(2026, 11, 17, 14, 12, 35, TimeSpan.FromMinutes(330)).AddTicks(1_234_567);
        var approval = new BusinessApprovalDigestInput(
            "run-审批/<>&\\\"", "skill-汉字", -3, "hash-🦊",
            "conn-\n\t", 17, "cred-'\r", "cap-\u2028",
            "snapshot-\"/", """{"客户":"甲<&>","ids":[1,2],"quoted":"\n"}""",
            ["项-2", "item-1", "项-2", "", null!], expires);

        const string originalDigest = "7c769afd5d673e2fa3ebdb654a2bcf31ccd3ba341c78195e4cd196bc07ec950c";
        Assert.Equal(originalDigest, approval.ComputeRequestDigest());
        Assert.Equal(originalDigest, (approval with { ExpiresAtUtc = expires.ToUniversalTime() }).ComputeRequestDigest());
        Assert.Equal("c3fec449890cc0e5a17ab4b006a0358e69da754e5bf63abd8bbdc61a29fdc37b",
            (approval with { BatchItemIds = ["item-1", "项-2", "项-2", "", null!] }).ComputeRequestDigest());
        Assert.Equal("a6ab6b85640cbfbfc3154681f8bf66f79d5532288d750a4a65c0c717e7d2472d",
            (approval with { BatchItemIds = [] }).ComputeRequestDigest());
        Assert.Equal("2b13234eb7df200e878bec2f28ba9ad9bec7289bc7cb6300b70ee41c25060b27",
            (approval with { BatchItemIds = null! }).ComputeRequestDigest());
        Assert.Equal("07c66b8a91a98891824cd10760fa82ccf178cb1bb2d944b962267859ed81a1fe",
            new BusinessApprovalDigestInput(null!, null!, 0, null!, null!, 0, null!, null!, null!, null!, [], default).ComputeRequestDigest());
    }

    [Fact]
    public void ErrorCodes_AndSupportMatrix_MatchDesignSpecMinimumRequirements()
    {
        string[] expectedCodes =
        [
            "AuthRequired",
            "ConnectionUnavailable",
            "UnsupportedProtocol",
            "UnsupportedSchema",
            "CatalogIncomplete",
            "CatalogChanged",
            "SelectionViolation",
            "ValidationFailed",
            "ApprovalRequired",
            "ApprovalExpired",
            "BudgetExceeded",
            "PartialData",
            "OutcomeUnknown"
        ];

        foreach (var code in expectedCodes)
        {
            Assert.Contains(code, BusinessSkillErrorCodes.All);
        }

        var matrix = BusinessSkillDefaults.CreateFrozenMatrix();
        Assert.Equal(BusinessSkillDefaults.LockedMcpProtocolVersion, matrix.LockedMcpProtocolVersion);
        Assert.Contains("2.0", matrix.SupportedOpenApiSpecFamilies);
        Assert.Contains("3.0", matrix.SupportedOpenApiSpecFamilies);
        Assert.Contains("3.1", matrix.SupportedOpenApiSpecFamilies);
        Assert.Contains("3.2", matrix.UnsupportedOpenApiSpecFamilies);
        Assert.Contains("2026-07-28", matrix.UnsupportedMcpProtocolVersions);
    }

    [Fact]
    public async Task Section11ProposedRoutes_DoNotCollideWithExistingGatewayHttpRoutes()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddCodexFlowGateway();
        await using var app = builder.Build();
        app.MapCodexFlowGateway();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>();
        var routes = endpoints
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Select(method =>
                (Signature: $"{method} /{endpoint.RoutePattern.RawText?.Trim('/')}", Endpoint: endpoint)) ?? [])
            .ToLookup(route => route.Signature, route => route.Endpoint, StringComparer.OrdinalIgnoreCase);

        Assert.Single(routes["POST /api/gateway/approvals/{approvalId}"]);
        Assert.All(routes, group => Assert.Single(group));
        foreach (var route in BusinessPluginApiRoutes.CanonicalRoutes)
        {
            var endpoint = Assert.Single(routes[$"{route.Method} {route.Template}"]);
            var expectedPolicy = route.AuthPlane == BusinessApiAuthPlane.ControlPlane
                ? ControlPlaneAuthConstants.ControlPlanePolicy
                : "ActiveUser";
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), authorization =>
                string.Equals(authorization.Policy, expectedPolicy, StringComparison.Ordinal));
        }
    }
}
