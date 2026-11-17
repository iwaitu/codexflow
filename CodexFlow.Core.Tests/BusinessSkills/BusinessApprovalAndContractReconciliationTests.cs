// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
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
    public void Section11ProposedRoutes_DoNotCollideWithExistingGatewayHttpRoutes()
    {
        var legacyControllers = typeof(GatewayApprovalController).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract &&
                        typeof(ControllerBase).IsAssignableFrom(t) &&
                        t != typeof(BusinessPluginsController) &&
                        t != typeof(BusinessSkillsController))
            .ToArray();

        var existingLegacyRoutes = ExtractRouteSignatures(legacyControllers);
        var businessControllerRoutes = ExtractRouteSignatures([typeof(BusinessPluginsController), typeof(BusinessSkillsController)]);

        // 现有非 BusinessSkills 控制器的审批路径必须存在
        Assert.Contains("POST /api/gateway/approvals/{approvalId}", existingLegacyRoutes);

        var newRoutes = BusinessPluginApiRoutes.CanonicalRoutes
            .Where(r => !string.Equals(r.Template, "/api/gateway/approvals/{approvalId}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // 新增业务路由不得与原有控制器路由冲突
        foreach (var route in newRoutes)
        {
            var signature = $"{route.Method} {route.Template}";
            Assert.DoesNotContain(signature, existingLegacyRoutes);
        }

        // P1 + P3 已实现全部第 11 节非网关审批业务路由
        foreach (var route in newRoutes)
        {
            var sig = $"{route.Method} {route.Template}";
            Assert.Contains(sig, businessControllerRoutes);
        }
    }

    private static HashSet<string> ExtractRouteSignatures(IEnumerable<Type> controllerTypes)
    {
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var controller in controllerTypes)
        {
            var routeAttr = controller.GetCustomAttributes(typeof(RouteAttribute), inherit: false)
                .OfType<RouteAttribute>()
                .FirstOrDefault();
            var prefix = routeAttr?.Template?.Trim('/') ?? string.Empty;

            foreach (var method in controller.GetMethods())
            {
                foreach (var httpAttr in method.GetCustomAttributes(typeof(HttpMethodAttribute), inherit: false)
                             .OfType<HttpMethodAttribute>())
                {
                    var template = (httpAttr.Template ?? string.Empty).Trim('/');
                    var fullPath = string.IsNullOrEmpty(prefix)
                        ? "/" + template
                        : (string.IsNullOrEmpty(template) ? "/" + prefix : $"/{prefix}/{template}");

                    foreach (var httpMethod in httpAttr.HttpMethods)
                    {
                        routes.Add($"{httpMethod} {fullPath}");
                    }
                }
            }
        }

        return routes;
    }
}
