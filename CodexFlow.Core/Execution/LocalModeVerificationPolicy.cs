// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.Execution;

public enum LocalVerificationModePolicy
{
    Skip = 0,
    Block = 1
}

public sealed record LocalVerificationPolicyEvaluation(
    bool IsLocalBackend,
    LocalVerificationModePolicy Policy);

/// <summary>
/// Central policy entry for verification and security-audit pipelines when the active
/// execution backend is <see cref="ExecutionBackend.Local"/>.
/// Evaluated BEFORE opening any execution session so no container-only command is launched.
/// </summary>
public static class LocalModeVerificationPolicy
{
    public const string SkippedReasonCode = "skipped(requires_docker_backend)";
    public const string BlockedStatus = "blocked(requires_docker_backend)";
    public const string BlockedReasonCode = "RequiresDockerBackend";
    public const string UnverifiedNotice = "未在隔离环境中验证";

    public static LocalVerificationPolicyEvaluation Evaluate(
        IConfiguration configuration,
        IOptionsMonitor<ExecutionOptions>? executionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var backend = executionOptions?.CurrentValue.Backend;
        if (backend is null)
        {
            var rawBackend = configuration[$"{ExecutionOptions.SectionName}:Backend"];
            backend = !string.IsNullOrWhiteSpace(rawBackend) &&
                      Enum.TryParse<ExecutionBackend>(rawBackend, ignoreCase: true, out var parsedBackend)
                ? parsedBackend
                : ExecutionBackend.Local;
        }

        var rawPolicy = configuration["ArtifactVerification:LocalModePolicy"];
        var policy = !string.IsNullOrWhiteSpace(rawPolicy) &&
                     Enum.TryParse<LocalVerificationModePolicy>(rawPolicy, ignoreCase: true, out var parsedPolicy)
            ? parsedPolicy
            : LocalVerificationModePolicy.Skip;

        return new LocalVerificationPolicyEvaluation(backend == ExecutionBackend.Local, policy);
    }
}
