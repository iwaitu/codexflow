// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Runtime;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Services.Background;

/// <summary>Persisted host diagnostics. Tool receipts refer to an attempt and cannot attest to a candidate.</summary>
internal static class ForgeFailureContext
{
    public static JObject Preflight(int attempt) => Create(attempt, "preflight", "none");

    public static bool IsVerificationFailure(JObject result)
        => result["failureContext"]?["phase"]?.Value<string>() == "verify" ||
           result["detailMessage"]?.Value<string>() == "failed_verification";

    public static JObject FromAttempt(
        int attempt, QueryRuntimeResult result, int changedFiles,
        bool usedShadow, bool retained, bool restorePreflightFailed = false)
    {
        var verificationFailed = result.TerminalDetailMessage == "failed_verification";
        var phase = restorePreflightFailed ? "prepare_verification" : verificationFailed ? "verify" : "execute";
        var artifact = usedShadow
            ? retained ? "retained_unverified" : "cleaned"
            : changedFiles > 0 ? "unverified" : "none";
        var context = Create(attempt, phase, artifact);
        context["observedChangeCount"] = changedFiles;
        if (verificationFailed) context["stopReason"] = "failed_verification";
        context["checks"] = new JArray(result.VerificationReceipts.Select(static receipt => new JObject
        {
            ["kind"] = receipt.Kind,
            ["outcome"] = receipt.Outcome,
            ["exitCode"] = receipt.ExitCode,
            ["reason"] = receipt.Reason,
            ["passed"] = receipt.Passed,
            ["failed"] = receipt.Failed
        }));
        return context;
    }

    private static JObject Create(int attempt, string phase, string artifact) => new()
    {
        ["version"] = 1,
        ["attempt"] = attempt,
        ["phase"] = phase,
        ["artifact"] = artifact
    };
}
