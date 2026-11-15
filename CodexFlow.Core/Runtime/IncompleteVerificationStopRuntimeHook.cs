// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;
using CodexFlow.Core.Telemetry;
using CodexFlow.Core.Text;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Prevents the runtime from accepting an assistant response that explicitly
/// describes failed verification or unfinished repair work as the final answer.
/// </summary>
public sealed class IncompleteVerificationStopRuntimeHook : IRuntimeHook
{
    private const int MaxContinuationAttempts = 3;

    private readonly Func<string?, CancellationToken, Task<SuppressionEvidence>> _probeSuppression;

    public IncompleteVerificationStopRuntimeHook()
        : this(WorkspaceSuppressionProbe.ProbeAsync)
    {
    }

    /// <param name="probeSuppression">Reads what the workspace's project files really say about audit suppression (injected so tests need no git).</param>
    public IncompleteVerificationStopRuntimeHook(Func<string?, CancellationToken, Task<SuppressionEvidence>> probeSuppression)
    {
        _probeSuppression = probeSuppression ?? throw new ArgumentNullException(nameof(probeSuppression));
    }

    public ValueTask<AfterModelResponseHookResult> OnAfterModelResponseAsync(
        AfterModelResponseContext context,
        CancellationToken ct = default)
        => ValueTask.FromResult(AfterModelResponseHookResult.None);

    public async ValueTask<BeforeStopHookResult> OnBeforeStopAsync(
        BeforeStopContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Whether audit suppression really happened is read from the project files, not from the worker's account of it. Only a worker that
        // can change files and is not just answering a question needs the lookup.
        var files = SuppressionEvidence.Unknown;
        if (context.Request.EnableTools &&
            !string.IsNullOrWhiteSpace(context.LastAssistantMessage) &&
            context.Request.EntryPoint is not (QueryLoopEntryPoint.ExploreWorker or QueryLoopEntryPoint.PlanWorker or QueryLoopEntryPoint.VerifyWorker) &&
            !IsAdvisoryCoordinatorTurn(context))
        {
            files = await _probeSuppression(context.Request.Session?.WorkspacePath, ct).ConfigureAwait(false);
        }

        return await Evaluate(context, files).ConfigureAwait(false);
    }

    private async ValueTask<BeforeStopHookResult> Evaluate(BeforeStopContext context, SuppressionEvidence files)
    {
        var decision = await EvaluateCore(context, files).ConfigureAwait(false);
        return decision.Continue ? decision : RequestStatusReport(context) ?? decision;
    }

    /// <summary>
    /// A forge worker that really changed something and is about to stop without stating its own status is asked, once, to state it through the
    /// structured status tool: a task it reports as blocked is not a completed task, and the host decides that from the call, not from prose. Asked
    /// only on the turn's first stop (no continuation spent yet), so the extra round can never exhaust the continuation budget.
    /// </summary>
    private static BeforeStopHookResult? RequestStatusReport(BeforeStopContext context)
    {
        if (context.Request.EntryPoint != QueryLoopEntryPoint.ForgeWorker ||
            !context.Request.EnableTools ||
            context.ContinuationCount > 0 ||
            context.EvidenceLedger.WorkerReport != null ||
            !context.SuccessfulToolNames.Any(IsWriteToolEvidence))
        {
            return null;
        }

        return new BeforeStopHookResult
        {
            Continue = true,
            Message = $"结束前请调用 `{WorkerStatusTool.Name}` 报告这个任务的真实状态：任务要求的修改都已写入项目文件，报告 status=completed（没能运行或没做完的验证列进 unverified，验收由宿主独立完成）；还有任务要求的修改没有写入或无法写入（比如缺少任务提到的清单、缺包），报告 status=blocked，并用 reason 说明卡在哪里、用 unfinished 列出没做完的修改。不要把部分完成说成完成。",
            Reason = "worker_status_report_missing",
            AllowToolCallsOnNextRound = true,
            RequiredToolNameForNextRound = WorkerStatusTool.Name,
            MaxContinuationAttempts = 1,
            ExhaustionDetailCode = QueryTerminalDetailCodes.RecoveryExhausted,
            ExhaustionMessage = "Forge worker did not state its task status."
        };
    }

    private ValueTask<BeforeStopHookResult> EvaluateCore(BeforeStopContext context, SuppressionEvidence files)
    {

        if (!context.Request.EnableTools ||
            string.IsNullOrWhiteSpace(context.LastAssistantMessage))
        {
            return ValueTask.FromResult(BeforeStopHookResult.None);
        }

        // Explore / plan / verify workers cannot change anything: "unfinished repair work" is meaningless for them, and pushing them to keep going
        // only burns rounds until recovery is exhausted (a read-only explore report that merely mentioned NU1605 as a risk was failed that way).
        if (context.Request.EntryPoint is QueryLoopEntryPoint.ExploreWorker or QueryLoopEntryPoint.PlanWorker or QueryLoopEntryPoint.VerifyWorker)
        {
            return ValueTask.FromResult(BeforeStopHookResult.None);
        }

        // A forge worker that stated, through the status tool, that its task is blocked has given its final answer: the host fails the job as
        // blocked whatever happens next, so sending it back to "repair" only repeats the same failing commands (seen live: two extra rounds, two
        // more identical blocked reports, two minutes). A worker that reported completed is still held to what its verification really did.
        if (context.Request.EntryPoint == QueryLoopEntryPoint.ForgeWorker && context.EvidenceLedger.WorkerReport is { IsBlocked: true })
        {
            return ValueTask.FromResult(BeforeStopHookResult.None);
        }

        // A forge worker that only previewed its edit (dryRun) changed nothing: send it back to apply that edit. Decided by the calls' arguments.
        if (context.Request.EntryPoint == QueryLoopEntryPoint.ForgeWorker && context.EvidenceLedger.UnappliedPreviews.Count > 0)
        {
            var previewTool = context.EvidenceLedger.UnappliedPreviews[^1].ToolName;
            return ValueTask.FromResult(new BeforeStopHookResult
            {
                Continue = true,
                Message = $"你调用 `{previewTool}` 时带了 `dryRun: true`：那只是验证，不会修改任何文件，项目现在没有任何改动。" +
                          $"请用同样的操作再调用一次 `{previewTool}`，这次不要带 `dryRun`，让修改真正写入；写入之后再还原、构建、测试。",
                Reason = "unapplied_preview",
                AllowToolCallsOnNextRound = true,
                RequiredToolNameForNextRound = previewTool,
                MaxContinuationAttempts = 2,
                ExhaustionDetailCode = QueryTerminalDetailCodes.RecoveryExhausted,
                ExhaustionMessage = "Forge worker stopped with an edit that was only previewed (dryRun) and never applied."
            });
        }

        // What the verification commands of this turn really did decides first. A passed command counts as much as the model saying so, and a
        // command whose latest run failed is a failure whatever the report says; the wording below only speaks when no command did.
        var ledger = context.EvidenceLedger;
        // A failure caused by the environment (a package missing from the offline source) is reported by the worker, not repaired by it: it neither
        // forces another round nor counts as an unexplained failure mention. The host's restore preflight judges that dependency change.
        var failedKinds = ledger.FailedVerifications.Where(static verification => !verification.EnvironmentLimit).Select(static verification => verification.Kind).ToHashSet(StringComparer.Ordinal);
        var limitedKinds = ledger.FailedVerifications.Where(static verification => verification.EnvironmentLimit).Select(static verification => verification.Kind).ToHashSet(StringComparer.Ordinal);
        var verified = new VerifiedState(
            Build: PassedAndNotFailed(ledger, failedKinds, "dotnet_build", "dotnet_restore") ||
                   (limitedKinds.Overlaps(["dotnet_build", "dotnet_restore"]) && !failedKinds.Overlaps(["dotnet_build", "dotnet_restore"])),
            Test: PassedAndNotFailed(ledger, failedKinds, "dotnet_test") ||
                  (limitedKinds.Contains("dotnet_test") && !failedKinds.Contains("dotnet_test")));
        var hasCurrentSuccessEvidence = (verified.Build && verified.Test) || (failedKinds.Count == 0 && HasCurrentSuccessEvidence(context.LastAssistantMessage));
        // The wording signals describe work: failed builds, remaining repairs. A coordinator chat that has changed and run nothing is
        // answering a question (an upgrade proposal warning "that would break the build" is not a failed build), so it is not pushed into
        // doing the repair. Workers repair code and stay subject to the signals from their first round.
        var signal = IsAdvisoryCoordinatorTurn(context)
            ? null
            : DetectIncompleteSignal(context.LastAssistantMessage, hasCurrentSuccessEvidence, files, verified)
              ?? (failedKinds.Count > 0 ? EvidenceFailureSignal(ledger) : null);
        if (signal == null)
        {
            var staleChangeEvidence = DetectStaleChangeEvidence(context);
            if (staleChangeEvidence != null)
            {
                return ValueTask.FromResult(new BeforeStopHookResult
                {
                    Continue = true,
                    Message = BuildStaleChangeEvidenceFeedback(staleChangeEvidence),
                    Reason = staleChangeEvidence.Reason,
                    AllowToolCallsOnNextRound = true,
                    RequiredToolNameForNextRound = "exec_cmd",
                    MaxContinuationAttempts = 2,
                    ExhaustionDetailCode = QueryTerminalDetailCodes.RecoveryExhausted,
                    ExhaustionMessage = "Assistant attempted to finalize with stale git status evidence after file changes."
                });
            }

            var reportMismatch = DetectFinalReportMismatch(context);
            if (reportMismatch == null)
            {
                return ValueTask.FromResult(BeforeStopHookResult.None);
            }

            return ValueTask.FromResult(new BeforeStopHookResult
            {
                Continue = true,
                Message = BuildFinalReportCorrectionFeedback(reportMismatch),
                Reason = reportMismatch.Reason,
                AllowToolCallsOnNextRound = false,
                MaxContinuationAttempts = 1,
                ExhaustionDetailCode = QueryTerminalDetailCodes.RecoveryExhausted,
                ExhaustionMessage = "Assistant final response was inconsistent with git status evidence.",
                FinalTextOnExhaustion = BuildFinalReportFallback(reportMismatch, context)
            });
        }

        return ValueTask.FromResult(new BeforeStopHookResult
        {
            Continue = true,
            Message = BuildFeedback(signal, context.LastAssistantMessage),
            Reason = signal.Reason,
            AllowToolCallsOnNextRound = true,
            MaxContinuationAttempts = MaxContinuationAttempts,
            ExhaustionDetailCode = QueryTerminalDetailCodes.RecoveryExhausted,
            ExhaustionMessage = "Assistant final response still described failed verification or unfinished repair work."
        });
    }

    private readonly record struct VerifiedState(bool Build, bool Test);

    // A kind counts as verified when a clean run is recorded and no later run of that kind (or of the kind it includes) failed.
    private static bool PassedAndNotFailed(QueryEvidenceLedger ledger, HashSet<string> failedKinds, string kind, string? includedKind = null)
        => ledger.Verifications.Any(verification => verification.Kind == kind) &&
           !failedKinds.Contains(kind) &&
           (includedKind is null || !failedKinds.Contains(includedKind));

    private static IncompleteSignal EvidenceFailureSignal(QueryEvidenceLedger ledger)
        => new(
            "failed_verification",
            "the latest run of " + string.Join(", ", ledger.FailedVerifications.Where(static verification => !verification.EnvironmentLimit).Select(static verification => verification.Summary).Distinct()) + " did not exit cleanly");

    private static bool IsAdvisoryCoordinatorTurn(BeforeStopContext context)
        => context.Request.EntryPoint == QueryLoopEntryPoint.GatewayMessageProcessor &&
           !context.ExecutedToolNames.Any(static tool => IsWriteToolEvidence(tool) || ContainsAny(tool, "exec_cmd", "run_command"));

    private static StaleChangeEvidence? DetectStaleChangeEvidence(BeforeStopContext context)
    {
        var latestWrite = context.EvidenceLedger.ToolResults
            .Where(static tool => tool.Success && IsWriteToolEvidence(tool.ToolName))
            .OrderByDescending(static tool => tool.ObservedAt)
            .FirstOrDefault();
        if (latestWrite == null)
        {
            return null;
        }

        var latestGitStatusAt = context.EvidenceLedger.ChangedFiles.Count == 0
            ? (DateTimeOffset?)null
            : context.EvidenceLedger.ChangedFiles.Max(static file => file.ObservedAt);
        if (latestGitStatusAt.HasValue && latestGitStatusAt.Value >= latestWrite.ObservedAt)
        {
            return null;
        }

        return new StaleChangeEvidence(
            "stale_git_status_after_write",
            latestWrite.ToolName,
            latestWrite.CallId,
            latestWrite.ObservedAt,
            latestGitStatusAt);
    }

    private static bool IsWriteToolEvidence(string? toolName)
        => ContainsAny(
            toolName ?? string.Empty,
            "write_file",
            "delete_file",
            "ivilson_smart_patch",
            "hs_write",
            "apply_patch",
            "create_file");

    private static FinalReportMismatch? DetectFinalReportMismatch(BeforeStopContext context)
    {
        var verifiedFiles = context.EvidenceLedger.ChangedFiles
            .Select(static file => NormalizePath(file.FilePath))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (verifiedFiles.Length == 0 || !LooksLikeChangedFilesSection(context.LastAssistantMessage))
        {
            return null;
        }

        var mentionedFiles = ExtractMentionedFilePaths(context.LastAssistantMessage);
        var unsupportedFiles = mentionedFiles
            .Where(path => !verifiedFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        var claimedCount = TryExtractClaimedChangedFileCount(context.LastAssistantMessage);
        if (unsupportedFiles.Length == 0 && (claimedCount == null || claimedCount == verifiedFiles.Length))
        {
            return null;
        }

        var reason = unsupportedFiles.Length > 0
            ? "final_report_unverified_changed_files"
            : "final_report_changed_file_count_mismatch";

        return new FinalReportMismatch(reason, verifiedFiles, mentionedFiles, unsupportedFiles, claimedCount);
    }

    private static bool LooksLikeChangedFilesSection(string text)
        => ContainsAny(text,
            "修改的文件",
            "变更的文件",
            "修改文件",
            "changed files",
            "modified files",
            "files changed",
            "修复内容",
            "变更内容",
            "| 文件 |",
            "文件 | 修改",
            "file | change");

    private static string[] ExtractMentionedFilePaths(string text)
    {
        var matches = Regex.Matches(
            text,
            @"`?(?<path>(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+\.(?:csproj|sln|props|targets|cs|json|xml|md|ts|tsx|js|jsx|css|scss|yml|yaml))`?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return matches
            .Select(match => NormalizePath(match.Groups["path"].Value))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int? TryExtractClaimedChangedFileCount(string text)
    {
        var match = Regex.Match(
            text,
            @"(?:修改的文件|变更的文件|修改文件|changed files|modified files|files changed)[^\d]{0,12}(?<count>\d+)\s*(?:个|files?)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count)
            ? count
            : null;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/').Trim().Trim('`', '\'', '"', ',', ';', ':');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }

    private static IncompleteSignal? DetectIncompleteSignal(string text, bool hasCurrentSuccessEvidence, SuppressionEvidence files, VerifiedState verified)
    {
        // The files decide when they can: a suppression line really added is a suppression whatever the report says, and a report that
        // claims one while the changed project files add none is narrative. Without a readable work tree the wording decides, as before.
        if (files == SuppressionEvidence.Added || (files == SuppressionEvidence.Unknown && ContainsVulnerabilitySuppression(text)))
        {
            return new IncompleteSignal("vulnerability_suppression_used", "vulnerabilities were suppressed instead of actually fixed");
        }

        if (ContainsVulnerabilityReport(text))
        {
            return new IncompleteSignal("remaining_vulnerabilities", "vulnerability scan still reports vulnerable packages");
        }

        // A failure mention is retired by a clean run of its own kind: "build failed ... fixed" next to a passed build is history, not a
        // fresh failure. Without that run the wording stands, as before.
        if ((!verified.Build && ContainsAffirmed(text,
                "NU1605",
                "package downgrade",
                "Detected package downgrade",
                "dotnet restore failed",
                "dotnet build failed",
                "Build FAILED",
                "restore failed",
                "build failed")) ||
            (!verified.Test && ContainsAffirmed(text,
                "test failed",
                "tests failed")))
        {
            return hasCurrentSuccessEvidence
                ? null
                : new IncompleteSignal("failed_verification", "verification failure mentioned");
        }

        if (Regex.IsMatch(
                text,
                @"\b(I|we)\s+(need|must|should)\s+to\s+(also\s+)?(fix|update|bump|read|run|verify)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(
                text,
                @"\b(let me|I'll|I will)\s+(also\s+)?(fix|update|bump|read|run|verify)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return hasCurrentSuccessEvidence
                ? null
                : new IncompleteSignal("unfinished_repair_intent", "assistant described remaining repair work");
        }

        if (ContainsAffirmed(text,
                "还需要",
                "仍需",
                "需要继续",
                "下一步需要",
                "我需要",
                "我要继续",
                "验证失败",
                "构建失败",
                "测试失败",
                "恢复失败"))
        {
            return hasCurrentSuccessEvidence
                ? null
                : new IncompleteSignal("unfinished_repair_intent", "assistant described remaining repair work");
        }

        return null;
    }

    private static bool ContainsVulnerabilityReport(string text)
        => (ContainsAny(text,
               "has the following vulnerable packages",
               "仍报 NU1902",
               "漏洞扫描仍",
               "vulnerability scan still",
               "--vulnerable still reports") ||
            Regex.IsMatch(text, @"\berror\s+NU190[1-4]\b|\bstill\s+(?:reports?|shows?)\s+NU190[1-4]\b|\bvulnerable packages\s+(?:remain|still)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) &&
           !ContainsAny(text, "no vulnerable packages", "没有 vulnerable packages", "无漏洞包");

    private const string SuppressionWord = @"(?:NoWarn|NuGetAuditSuppress(?:Item)?|suppress\w*|抑制|忽略)";

    // "不存在/并无/从未/没有任何 … 抑制", "不是/并非/而不是/未被/没有被 … 抑制", "“漏洞被抑制”判定不成立",
    // "no suppression", "not (been) suppressed", "isn't/wasn't suppressed".
    private const string SuppressionDenialPattern =
        @"(?:不存在|并无|从未|不曾|没有任何|没有被|未被|无任何|不是被?|并非被?|而不是被?)[^。，,;；而]{0,12}?" + SuppressionWord +
        @"|被?" + SuppressionWord + @"[”""』」`']*\s*(?:的)?(?:判定|结论|说法|假设|指控)?\s*(?:不成立|不属实|是错误的|并不成立)" +
        @"|\b(?:there\s+is\s+)?no\s+(?:\w+\s+){0,3}?suppress\w*" +
        @"|\b(?:not|never|isn't|wasn't|weren't|aren't|hasn't\s+been|haven't\s+been)\s+(?:been\s+)?suppress\w*";

    private static bool ContainsVulnerabilitySuppression(string text)
    {
        // Do not join an unrelated "ignored soft-deleted rows" sentence to a disclosed package warning.
        // Explicit negation of suppression is not evidence that suppression was performed.
        return Regex.Split(text, @"[。！？\r\n;]+|(?<=[.!?])\s+")
            .Any(sentence =>
            {
                var affirmative = Regex.Replace(sentence,
                    @"(?:未|没有|禁止|不得|不)(?:使用|添加|启用|采用|进行)?[\s`""']*(?:NoWarn|NuGetAuditSuppress(?:Item)?|抑制|忽略)|\b(?:without|never|not|no|did\s+not|do\s+not)\s+(?:(?:use|using|add|adding)\s+)?[`""']*(?:NoWarn|NuGetAuditSuppress(?:Item)?|suppress\w*|suppression)\b",
                    string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                affirmative = Regex.Replace(affirmative,
                    @"(?:NoWarn|NuGetAuditSuppress(?:Item)?|suppression)[`""']*\s*(?:(?:was|were|is)\s+)?(?:not|never)\s+(?:used|applied|added)|(?:NoWarn|NuGetAuditSuppress(?:Item)?)[`""']*\s*(?:未|没有)(?:使用|添加|应用)",
                    string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                // Denials the narrow patterns above miss. Each is bounded to a single clause (no comma, period, or
                // "而是" between the denial and the word) so "没有升级包，而是添加 NoWarn" stays affirmative.
                affirmative = Regex.Replace(affirmative, SuppressionDenialPattern, string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return ContainsAny(affirmative,
               "NuGetAuditSuppress",
               "NuGetAuditSuppressItem",
               "NoWarn",
               "suppress",
               "suppression",
               "抑制",
               "忽略") &&
           ContainsAny(affirmative,
               "vulnerab",
               "漏洞",
               "NuGet 审计",
               "NuGet audit",
               "audit warning",
               "安全漏洞");
            });
    }

    private static bool HasCurrentSuccessEvidence(string text)
        => Regex.IsMatch(
               text,
               @"dotnet\s+restore[^.\n\r]*(passed|succeeded|success|通过|成功)",
               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
           Regex.IsMatch(
               text,
               @"dotnet\s+build[^.\n\r]*(passed|succeeded|success|通过|成功)",
               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
           Regex.IsMatch(
               text,
               @"dotnet\s+test[^.\n\r]*(passed|succeeded|success|通过|成功)",
               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A needle counts only when the sentence around it does not deny it ("没有 NU1605", "no package downgrade", "build failed 0 times").</summary>
    private static bool ContainsAffirmed(string text, params string[] needles)
        => AffirmedText.ContainsAffirmed(text, AffirmedText.Style.Report, needles);

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string BuildFeedback(IncompleteSignal signal, string lastAssistantMessage)
    {
        var excerpt = lastAssistantMessage.Trim();
        if (excerpt.Length > 1200)
        {
            excerpt = excerpt[^1200..];
        }

        return
            "你的上一条回复仍然显示真实任务没有完成，不能作为最终答复结束。" +
            $" 检测到：{signal.Description}。" +
            "\n必须遵守当前任务的授权范围；范围外既有告警应如实披露，不得自行扩展为依赖升级任务。" +
            "\n\n请继续使用工具直接修复，不要只总结或解释。" +
            "\n必须执行的闭环：" +
            "\n1. 如果是 NuGet/NU1605/package downgrade，扫描所有 `*.csproj`，统一 direct/transitive 包版本。" +
            "\n2. 如果是安全漏洞，禁止通过 `NoWarn`、`NuGetAuditSuppress`、Directory.Build.props 审计抑制来伪装通过；必须真实升级/替换依赖，无法升级时报告阻塞。" +
            "\n3. 修改相关 `.csproj` 或源码，清理临时脚本或无关文件。" +
            "\n4. 重新运行 `dotnet restore`、`dotnet build`、`dotnet test`、`dotnet list package --vulnerable --include-transitive` 和 `git status --short`。" +
            "\n   如果漏洞扫描因网络不可达而无法完成（NU1900 / 无法访问 api.nuget.org），这是环境限制而不是代码缺陷：" +
            "如实披露该限制即可，不要为此升级依赖、不要抑制告警，也不要反复自证；其余验证通过即可结束。" +
            "\n5. 只有最新验证全部通过且没有漏洞抑制时才能最终总结；否则继续修复或明确报告不可自动解决的阻塞。" +
            $"\n\n上一条未完成回复摘录：\n{excerpt}";
    }

    private static string BuildFinalReportCorrectionFeedback(FinalReportMismatch mismatch)
    {
        var verified = mismatch.VerifiedFiles.Length == 0
            ? "(none)"
            : string.Join("\n", mismatch.VerifiedFiles.Select(static file => $"- {file}"));
        var unsupported = mismatch.UnsupportedMentionedFiles.Length == 0
            ? "(none)"
            : string.Join("\n", mismatch.UnsupportedMentionedFiles.Select(static file => $"- {file}"));
        var claimedCount = mismatch.ClaimedCount?.ToString() ?? "(not claimed)";

        return
            "你的上一条最终总结与最新 `git status --short` 证据不一致，不能作为最终答复结束。" +
            "\n\n请不要调用工具，只基于下面的结构化证据重写最终总结。" +
            "\n必须做到：" +
            "\n1. “修改的文件”只能列出 git status 证据中的文件。" +
            "\n2. 不要声称修改了未在 git status 中出现的文件；可以把它们描述为验证涉及/依赖影响，但不能列为已修改。" +
            "\n3. 验证结果只引用已经完成的 restore/build/test/vulnerability scan 结论。" +
            $"\n\n最新 git status 变更文件（count={mismatch.VerifiedFiles.Length}）：\n{verified}" +
            $"\n\n上一条总结声称的修改文件数量：{claimedCount}" +
            $"\n上一条总结中未被 git status 支持的文件：\n{unsupported}";
    }

    private static string BuildStaleChangeEvidenceFeedback(StaleChangeEvidence evidence)
    {
        var latestStatus = evidence.LatestGitStatusAt.HasValue
            ? evidence.LatestGitStatusAt.Value.ToString("O")
            : "(none)";
        return
            "你的上一条回复试图结束任务，但最新写入/删除工具调用晚于最新 `git status --short` 证据。" +
            "\n\n必须继续调用工具，不要只改写总结。" +
            "\n下一轮只做最小验证：" +
            "\n1. 调用 `exec_cmd` 运行 `git status --short`。" +
            "\n2. 如果出现临时脚本、无关删除或未预期文件，清理/恢复后再次运行 `git status --short`。" +
            "\n3. 只有最新 git status 与最终总结完全一致时才结束。" +
            $"\n\n最新写入工具：{evidence.ToolName} callId={evidence.CallId} at={evidence.LatestWriteAt:O}" +
            $"\n最新 git status 证据时间：{latestStatus}";
    }

    private static string BuildFinalReportFallback(FinalReportMismatch mismatch, BeforeStopContext context)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("已完成修复；最终摘要已按最新 `git status --short` 证据校正。");
        builder.AppendLine();
        builder.AppendLine($"修改的文件（{mismatch.VerifiedFiles.Length} 个）：");
        foreach (var file in mismatch.VerifiedFiles)
        {
            builder.AppendLine($"- {file}");
        }

        var verificationLines = ExtractSuccessfulVerificationLines(context.LastAssistantMessage, context.EvidenceLedger);
        if (verificationLines.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("验证：");
            foreach (var line in verificationLines)
            {
                builder.AppendLine($"- {line}");
            }
        }

        if (mismatch.UnsupportedMentionedFiles.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("说明：上一条总结提到但未出现在最新 git status 中的文件没有列为已修改文件。");
        }

        return builder.ToString().TrimEnd();
    }

    private static string[] ExtractSuccessfulVerificationLines(string text, QueryEvidenceLedger evidenceLedger)
    {
        var lines = new List<string>();
        if (Regex.IsMatch(
                text,
                @"dotnet\s+restore[^.\n\r]*(passed|succeeded|success|通过|成功)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            lines.Add("`dotnet restore` 通过");
        }

        if (Regex.IsMatch(
                text,
                @"dotnet\s+build[^.\n\r]*(passed|succeeded|success|通过|成功)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            lines.Add("`dotnet build` 通过");
        }

        if (Regex.IsMatch(
                text,
                @"dotnet\s+test[^.\n\r]*(passed|succeeded|success|通过|成功)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            lines.Add("`dotnet test` 通过");
        }

        if (ContainsAny(
                text,
                "no vulnerable packages",
                "没有 vulnerable packages",
                "无漏洞包",
                "没有发现 vulnerable packages",
                "未报告 vulnerable packages"))
        {
            lines.Add("`dotnet list package --vulnerable --include-transitive` 未报告 vulnerable packages");
        }

        foreach (var verification in evidenceLedger.Verifications.OrderBy(static item => item.ObservedAt))
        {
            if (!string.IsNullOrWhiteSpace(verification.Summary) &&
                !lines.Contains(verification.Summary, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add(verification.Summary);
            }
        }

        return lines
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed record IncompleteSignal(string Reason, string Description);

    private sealed record StaleChangeEvidence(
        string Reason,
        string ToolName,
        string CallId,
        DateTimeOffset LatestWriteAt,
        DateTimeOffset? LatestGitStatusAt);

    private sealed record FinalReportMismatch(
        string Reason,
        string[] VerifiedFiles,
        string[] MentionedFiles,
        string[] UnsupportedMentionedFiles,
        int? ClaimedCount);
}
