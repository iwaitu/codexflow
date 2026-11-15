// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Claims;
using CodexFlow.Application.Jobs;
using CodexFlow.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodexFlow.Controllers;

/// <summary>Authenticated Gateway entry for verification of a stored Forge artifact.</summary>
[ApiController]
[Route("api/gateway/artifacts")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "ActiveUser")]
public sealed class GatewayArtifactController(IForgeArtifactVerificationService verification) : ControllerBase
{
    /// <summary>Verify a server-owned artifact; the caller cannot select a path or command.</summary>
    [HttpPost("verify")]
    public async Task<IActionResult> Verify([FromBody] VerifyForgeArtifactRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.JobId))
            return BadRequest("SessionId and JobId are required.");
        var receipt = await verification.VerifyAsync(userId, request, ct);
        return receipt.FailureCode == "ArtifactNotFound" ? NotFound() : Ok(receipt);
    }
}
