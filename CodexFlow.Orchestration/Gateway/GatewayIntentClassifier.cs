// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using System.Text.RegularExpressions;

namespace CodexFlow.Gateway;

/// <summary>
/// The only natural-language intent parser in the Gateway. It replaces the scattered keyword checks
/// (approval start, plan execution start, continue intent, direct execution, planning-only, explicit tool
/// intent) with one clause-aware analysis whose result is a <see cref="GatewayIntentDecision"/>.
/// <para>
/// Safety rules, in order of strength:
/// quoted / fenced / blockquoted text has no authority; historical statements ("I already approved") have no
/// authority; negated actions are boundaries; questions never authorize; conditional, double-negated and
/// contradictory approvals return a confirmation request instead of acting; approvals deferred to a later step
/// ("plan first, approve afterwards") do not approve now. Anything unrecognized is <see cref="GatewayIntentKind.Unknown"/>,
/// which never executes.
/// </para>
/// </summary>
public static class GatewayIntentClassifier
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // ---- legacy boundary vocabulary (kept verbatim so existing legitimate behavior is preserved) -------------

    private static readonly string[] ExplicitExecutionStartMarkers =
    [
        "批准", "开始执行", "立即执行", "立即修复", "直接修复", "开始修复", "执行计划", "按计划执行",
        "approve", "start execution", "start executing", "execute the plan", "begin execution"
    ];

    private static readonly string[] ExecutionDeferralMarkers =
    [
        "不要调用 approve_plan", "不要调用approve_plan", "不要调用 start_next_task", "不要调用start_next_task",
        "do not call approve_plan", "do not call start_next_task",
        "不要批准", "不要进入执行", "do not approve", "批准后", "等待批准", "待批准", "审批后", "获批后", "确认后再",
        "先不要执行", "不要开始执行", "先别执行", "再批准",
        "after approval", "wait for approval", "awaiting approval", "before executing", "do not start execution"
    ];

    private static readonly string[] StartOnlyDeferralMarkers =
    [
        "先不要执行", "先别执行", "不要开始执行", "不要进入执行", "do not start execution",
        "不要调用 start_next_task", "不要调用start_next_task", "do not call start_next_task"
    ];

    private static readonly string[] ApprovalPendingMarkers =
    [
        "不要调用 approve_plan", "不要调用approve_plan", "do not call approve_plan", "不要批准", "do not approve",
        "批准后", "等待批准", "待批准", "审批后", "获批后", "确认后再", "再批准",
        "after approval", "wait for approval", "awaiting approval", "before executing"
    ];

    private static readonly string[] ReadOnlyMarkers =
    [
        "不要修改", "不要写入", "不要执行", "不要进入执行", "不修改", "不写入", "只读", "仅读",
        "不要改动", "不得修改", "禁止修改",
        "read-only", "readonly", "do not modify", "don't modify", "without modifying", "no code changes"
    ];

    private static readonly string[] PlanningRequestMarkers =
    [
        "生成开发计划", "生成计划", "生成修复计划", "制定计划", "制定开发计划", "创建计划", "创建开发计划",
        "只做计划", "仅做计划", "仅规划", "只做规划", "create_session_plan",
        "create a plan", "generate a plan", "generate a development plan", "plan only", "planning only"
    ];

    // ---- hit vocabulary ---------------------------------------------------------------------------------------

    private static Regex R(string pattern, RegexOptions extra = RegexOptions.None) => new(pattern, Opts | extra, RegexTimeout);

    private static readonly Regex ApproveRx = R(
        @"批准|\bapprove_plan\b|\bapprove\b|\blgtm\b|同意(?=.{0,8}(?:计划|方案|plan))|通过(?=.{0,4}(?:计划|方案))");

    private static readonly Regex RejectRx = R(@"拒绝|驳回|否决|\breject_plan\b|\breject\b");

    private static readonly Regex RevokeRx = R(
        @"(?:取消|撤销|撤回|收回)(?:对)?(?:该|这个|此)?(?:批准|同意|审批)|\b(?:revoke|withdraw|rescind)\b(?:\s+\w+){0,2}?\s+(?:approval|approve|the plan)");

    // A stop request names the job-like object right after the verb ("停止当前作业", "stop the running job").
    // "停止工具调用并等待后台结果" (the CLI Execute prompt) is NOT a job stop. stop_worker is matched via ToolMentions.
    private static readonly Regex StopRx = R(
        @"(?:停止|终止|中止|取消)(?:当前|正在运行的|正在执行的|这个|该|所有|后台)*(?:作业|任务|worker|job|执行|派发|运行|进程)|\b(?:stop|cancel|abort|halt)\s+(?:the\s+|this\s+|current\s+|running\s+|all\s+)*(?:job|worker|task|run|execution)s?\b");

    private static readonly Regex BareStopRx = R(@"^\s*(?:停止|取消|终止|中止|stop|cancel|abort|halt)\s*[.!。！]*\s*$");

    // "continue"-class markers (the legacy MessageMatchesContinueIntent list).
    private static readonly Regex ContinueRx = R(
        @"继续|下一步|下一项|下一个|开始|执行|启动|派发|派一个|走下一|运行下一|推进|\bgo on\b|\bnext task\b|\bstart next\b|\bcontinue\b|\bproceed\b|\bdispatch\b|\bkick off\b|\brun the next\b|\bmove on\b");

    // English start verbs only count as a start request when the clause is about the plan.
    private static readonly Regex PlanStartEnglishRx = R(@"\b(?:start|execute|begin)");
    private static readonly Regex PlanMentionRx = R(@"计划|\bplan\b");

    // Companion start verbs used next to an approval ("approve and start").
    private static readonly Regex ApproveStartRx = R(@"执行|开始|启动|继续|\bstart\b|\bexecute\b|\bbegin\b|\bproceed\b");

    // "direct execution" (modify/build/test) markers - the legacy MessageMatchesDirectExecutionIntent list.
    private static readonly Regex WorkRx = R(
        @"尝试修复|开始修复|请修复|修复项目|修复问题|修复编译|修复测试|修掉|修正|修改|改成|改为|更新|落盘|写入|补丁|打补丁|应用补丁|创建文件|删除文件|重构|实现|运行测试|跑测试|执行测试|构建|编译|验证|dotnet build|dotnet test|npm run|\bfix|\bmodify|\bedit\b|\bpatch\b|apply patch|write file|create file|delete file|\brefactor|\bimplement|run tests|\bbuild\b|\bcompile|\bverify");

    private static readonly (string Tool, Regex Rx)[] ToolMentions =
    [
        ("start_next_task", R(@"\bstart_next_task\b")),
        ("retry_failed_task", R(@"\bretry_failed_task\b")),
        ("continue_worker", R(@"\bcontinue_worker\b")),
        ("stop_worker", R(@"\bstop_worker\b")),
        ("approve_plan", R(@"\bapprove_plan\b")),
        ("reject_plan", R(@"\breject_plan\b")),
        ("project_plan_to_tasks", R(@"\bproject_plan_to_tasks\b")),
        ("retry_failed_task", R(@"重试失败|重试这个失败任务|重试当前失败任务|retry failed task|retry the failed task")),
        ("approve_plan", R(@"批准计划|同意计划|approve the plan")),
        ("reject_plan", R(@"拒绝计划|驳回计划|reject the plan"))
    ];

    private static readonly Regex NegatorRx = R(
        @"不要|不能|不得|不可以|不可|不许|不准|不用|不必|不需要|不应该|不应|别|勿|请勿|切勿|禁止|无需|暂不|先不|不是|并非|不(?![过同但仅管如错久论是少够妨然会])|\bdo not\b|\bdon'?t\b|\bnever\b|\bnot\b|\bno\b|\bcannot\b|\bcan'?t\b|\bwon'?t\b|\bshouldn'?t\b|\bmustn'?t\b|\bmust not\b|\bwithout\b|\bavoid\b|\brefrain from\b|\bhold off\b");

    // Anything after these starts a new, independent statement for negation scope.
    private static readonly Regex NegationCutRx = R(@"然后|而是|只是|\bbut\b|\bthen\b|\binstead\b|\brather\b|\bhowever\b");

    private static readonly Regex HistoricalRx = R(
        @"之前|以前|先前|上次|上回|昨天|前天|刚才|刚刚|早些时候|已经|已被|已批|已同意|曾经|曾|\bpreviously\b|\bearlier\b|\balready\b|\blast time\b|\byesterday\b|\bjust now\b|\b(?:i|we|you|he|she|they|it)\s+(?:had\s+|have\s+|has\s+)?(?:approved|rejected|started)\b|\b(?:was|were|been|got)\s+(?:approved|rejected)\b");

    private static readonly Regex QuestionRx = R(
        @"吗|嘛|是否|能否|可否|要不要|该不该|应不应该|该如何|怎么|如何|\bwhether\b|^\s*(?:should|can|could|would|is|are|how|what|why)\b");

    private static readonly Regex ConditionalRx = R(
        @"如果|假如|倘若|若是|要是|一旦|只要|除非|万一|如若|\bif\b|\bunless\b|\bprovided that\b|\bin case\b|\bassuming\b|\bwhen\b|\bonce\b");

    private static readonly Regex LaterRx = R(
        @"之后|以后|后再|然后|稍后|晚点|晚些|\bthen\b|(?:等|待).{0,12}?(?:后|之后|再)|\blater\b|\bafterwards?\b|\bafter that\b|\buntil\b|\bonce\b|\bbefore\b");

    private static readonly Regex FirstThenRx = R(
        @"(?:先|首先|\bfirst\b)(?<a>.{0,40}?)(?:之后|然后|后|再|\band then\b|\bthen\b|\bafter(?:wards)?\b)");

    private static readonly Regex PastSuffixRx = R(@"^\s*(?:了|过)");

    // Quote / code stripping.
    private static readonly Regex FencedCodeRx = R(@"```.*?```", RegexOptions.Singleline);
    private static readonly Regex InlineCodeRx = R(@"`[^`\n]*`");
    private static readonly Regex BlockquoteRx = R(@"^[ \t]*>.*$", RegexOptions.Multiline);
    private static readonly Regex QuotedRx = R("“[^”]*”|「[^」]*」|『[^』]*』|\"[^\"\\n]*\"");
    private static readonly Regex QuotedToolRx = R(
        "(?<verb>调用|\\bcall\\b|\\binvoke\\b|\\buse\\b|\\brun\\b|执行)\\s*[`\"“「『]\\s*(?<tool>start_next_task|retry_failed_task|continue_worker|stop_worker|approve_plan|reject_plan|project_plan_to_tasks)\\s*[`\"”」』]");

    private static readonly Regex ShortApprovalRx = R(@"^\s*(?:批准|同意|approve|approved|lgtm|approve it)\s*[.!。！]*\s*$");

    private enum HitKind { Approve, Reject, Revoke, Stop, Continue, ApproveStart, Work }

    private enum HitState { Effective, Negated, DoubleNegated, Question, Conditional, Deferred, Historical }

    private sealed record Hit(HitKind Kind, int Pos, HitState State, string? Tool);

    /// <summary>Classifies a natural-language user message. Never throws; unknown input yields <see cref="GatewayIntentKind.Unknown"/>.</summary>
    public static GatewayIntentDecision Classify(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return GatewayIntentDecision.NoAuthority("empty");
        }

        try
        {
            return ClassifyCore(content);
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input must fail closed: no authority.
            return GatewayIntentDecision.NoAuthority("classifier_timeout");
        }
    }

    /// <summary>True when the message is a planning-only / read-only boundary (shared with the legacy helper).</summary>
    public static bool IsPlanningOnlyOrReadOnly(string? content) => Classify(content).IsBoundary;

    private static GatewayIntentDecision ClassifyCore(string content)
    {
        var text = Sanitize(content);
        if (string.IsNullOrWhiteSpace(text))
        {
            return GatewayIntentDecision.NoAuthority("only_quoted_text");
        }

        var trimmedLength = text.Trim().Length;
        var clauses = SplitClauses(text);
        var hits = CollectHits(clauses);

        bool Has(HitKind kind, HitState state) => hits.Any(h => h.Kind == kind && h.State == state);

        var hasDeferralMarker = ContainsAny(text, ExecutionDeferralMarkers);
        // "approve, but do not start" only forbids starting; every other deferral wording means approval is still pending.
        var startOnlyDeferral = hasDeferralMarker &&
                                ContainsAny(text, StartOnlyDeferralMarkers) &&
                                !ContainsAny(text, ApprovalPendingMarkers);
        var legacyBoundary = LegacyBoundary(text, hasDeferralMarker);

        // The short exact forms ("批准", "approve", "lgtm") are plain approvals.
        if (ShortApprovalRx.IsMatch(text) && !Has(HitKind.Approve, HitState.Negated))
        {
            return new GatewayIntentDecision
            {
                Kind = GatewayIntentKind.Approve,
                Source = GatewayIntentSource.NaturalLanguage,
                ReasonCode = "approve_short_form",
                RequiredTool = "approve_plan"
            };
        }

        var effApprove = Has(HitKind.Approve, HitState.Effective);
        var effReject = Has(HitKind.Reject, HitState.Effective) || Has(HitKind.Revoke, HitState.Effective);
        var effStop = Has(HitKind.Stop, HitState.Effective);

        // 1. Stop is a job-management action. It is never blocked by a read-only marker. Other actions in the same
        //    message are NOT inferred from it: they are deferred and need a fresh message.
        if (effStop)
        {
            var deferred = new List<GatewayIntentKind>();
            if (effApprove || Has(HitKind.Approve, HitState.Deferred)) deferred.Add(GatewayIntentKind.Approve);
            if (effReject) deferred.Add(GatewayIntentKind.Reject);
            return new GatewayIntentDecision
            {
                Kind = GatewayIntentKind.Stop,
                Source = GatewayIntentSource.NaturalLanguage,
                ReasonCode = deferred.Count > 0 ? "stop_first_mixed_request" : "stop_requested",
                RequiredTool = RequiredToolFor(hits, GatewayIntentKind.Stop),
                DeferredKinds = deferred
            };
        }

        // 2. Reject / revoke.
        if (effReject)
        {
            if (effApprove)
            {
                return Confirm(
                    "contradictory_approve_reject",
                    "消息同时包含批准和拒绝/撤销计划的表述，未执行任何操作。请单独回复“批准计划”或“拒绝计划”。");
            }

            return new GatewayIntentDecision
            {
                Kind = GatewayIntentKind.Reject,
                Source = GatewayIntentSource.NaturalLanguage,
                ReasonCode = "reject_requested",
                RequiredTool = RequiredToolFor(hits, GatewayIntentKind.Reject)
            };
        }

        // 3. Approval deferred by wording ("批准后", "wait for approval"): this message does not approve.
        if (hasDeferralMarker && !(startOnlyDeferral && effApprove))
        {
            return Boundary(text, "execution_deferred");
        }

        // 4. Explicit approval.
        if (effApprove)
        {
            var effStart = hits.Any(h => h.Kind == HitKind.ApproveStart && h.State == HitState.Effective);
            var negStart = hits.Any(h => h.Kind == HitKind.ApproveStart && h.State == HitState.Negated);
            var elevates = !legacyBoundary && Has(HitKind.Work, HitState.Effective);
            return new GatewayIntentDecision
            {
                Kind = GatewayIntentKind.Approve,
                Source = GatewayIntentSource.NaturalLanguage,
                ReasonCode = effStart ? "approve_and_start" : "approve_requested",
                StartsExecution = effStart,
                SuppressDispatch = negStart && !effStart,
                ElevatesToExecutionStage = elevates,
                ContinuesPlan = false,
                RequiredTool = RequiredToolFor(hits, GatewayIntentKind.Approve)
            };
        }

        // 5. Ambiguous approvals (conditional / double negation) ask for confirmation instead of acting.
        var ambiguousApprove = Has(HitKind.Approve, HitState.Conditional) || Has(HitKind.Approve, HitState.DoubleNegated);
        if (ambiguousApprove)
        {
            return Confirm(
                Has(HitKind.Approve, HitState.Conditional) ? "approval_conditional" : "approval_double_negation",
                "检测到带条件或含糊的批准表述，未批准计划也未开始执行。如需批准当前计划并开始执行，请直接回复“批准计划并开始执行”；仅批准请回复“批准计划”。");
        }

        // 6. Boundaries: negated approval/start, approvals deferred to a later step, legacy plan-only/read-only wording.
        var negatedApprove = Has(HitKind.Approve, HitState.Negated);
        var deferredApprove = Has(HitKind.Approve, HitState.Deferred);
        var negatedStart = Has(HitKind.Continue, HitState.Negated) || Has(HitKind.ApproveStart, HitState.Negated);
        if (negatedApprove)
        {
            return Boundary(text, "approval_negated");
        }

        if (deferredApprove)
        {
            return Boundary(text, "approval_deferred_after_planning");
        }

        if (legacyBoundary)
        {
            return Boundary(text, "planning_or_read_only_request");
        }

        if (negatedStart && !Has(HitKind.Continue, HitState.Effective) && !Has(HitKind.Work, HitState.Effective))
        {
            return Boundary(text, "execution_negated");
        }

        // 7. Execution / continue.
        var effContinue = Has(HitKind.Continue, HitState.Effective);
        var effWork = Has(HitKind.Work, HitState.Effective);
        var requiredExecute = RequiredToolFor(hits, GatewayIntentKind.Execute);
        if (effContinue || effWork || requiredExecute != null)
        {
            return new GatewayIntentDecision
            {
                Kind = GatewayIntentKind.Execute,
                Source = GatewayIntentSource.NaturalLanguage,
                ReasonCode = effWork ? "direct_execution_requested" : "continue_requested",
                StartsExecution = true,
                ElevatesToExecutionStage = effWork,
                ContinuesPlan = hits.Any(h => h.Kind == HitKind.Continue && h.State == HitState.Effective && h.Tool == null) && trimmedLength <= 600,
                RequiredTool = requiredExecute
            };
        }

        // 8. Inert statements: say why, never act.
        if (hits.Any(h => h.Kind is HitKind.Approve or HitKind.Reject or HitKind.Continue &&
                          h.State is HitState.Historical or HitState.Question))
        {
            var historical = hits.Any(h => h.State == HitState.Historical);
            return new GatewayIntentDecision
            {
                Kind = GatewayIntentKind.Unknown,
                Source = GatewayIntentSource.NaturalLanguage,
                ReasonCode = historical ? "action_is_historical" : "question_is_not_authorization"
            };
        }

        return GatewayIntentDecision.NoAuthority("no_actionable_intent");
    }

    // ---------------------------------------------------------------------------------------------------------

    private static GatewayIntentDecision Confirm(string reasonCode, string prompt)
        => new()
        {
            Kind = GatewayIntentKind.Unknown,
            Source = GatewayIntentSource.NaturalLanguage,
            ReasonCode = reasonCode,
            RequiresConfirmation = true,
            ConfirmationPrompt = prompt
        };

    private static GatewayIntentDecision Boundary(string text, string reasonCode)
    {
        var readOnly = ContainsAny(text, ReadOnlyMarkers) && !ContainsAny(text, PlanningRequestMarkers);
        return new GatewayIntentDecision
        {
            Kind = readOnly ? GatewayIntentKind.ReadOnly : GatewayIntentKind.PlanOnly,
            Source = GatewayIntentSource.NaturalLanguage,
            ReasonCode = reasonCode
        };
    }

    /// <summary>The pre-existing planning-only / read-only rule, with the explicit-execution override preserved.</summary>
    private static bool LegacyBoundary(string text, bool hasDeferralMarker)
    {
        if (hasDeferralMarker)
        {
            return true;
        }

        if (ContainsAny(text, ExplicitExecutionStartMarkers))
        {
            return false;
        }

        return ContainsAny(text, ReadOnlyMarkers) || ContainsAny(text, PlanningRequestMarkers);
    }

    private static bool ContainsAny(string content, string[] markers)
    {
        foreach (var marker in markers)
        {
            if (content.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Strips quoted, fenced, inline-code and blockquoted text: it can describe an action but never authorize it.</summary>
    internal static string Sanitize(string content)
    {
        // `请调用 "approve_plan"` is an instruction that merely quotes a tool name: keep the bare tool name.
        var text = QuotedToolRx.Replace(content, "${verb} ${tool}");
        text = FencedCodeRx.Replace(text, " ");
        text = BlockquoteRx.Replace(text, " ");
        text = InlineCodeRx.Replace(text, " ");
        text = QuotedRx.Replace(text, " ");
        return text;
    }

    private sealed record Clause(string Text, bool EndsWithQuestion, bool ConditionalCarry, int Start);

    private static List<Clause> SplitClauses(string text)
    {
        var clauses = new List<Clause>();
        var sb = new StringBuilder();
        var start = 0;
        var carryConditional = false;

        void Flush(bool question)
        {
            var clauseText = sb.ToString();
            sb.Clear();
            if (string.IsNullOrWhiteSpace(clauseText))
            {
                start += 1;
                return;
            }

            var own = ConditionalRx.IsMatch(clauseText);
            clauses.Add(new Clause(clauseText, question, own || carryConditional, start));
            // The condition scopes the clause that states it and the apodosis that follows.
            carryConditional = own;
            start += clauseText.Length + 1;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '?':
                case '？':
                    Flush(true);
                    break;
                case '。':
                case '！':
                case '!':
                case '；':
                case ';':
                case '\n':
                case '\r':
                case '，':
                case ',':
                    Flush(false);
                    break;
                case '.' when i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]):
                    Flush(false);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        Flush(false);
        return clauses;
    }

    private static List<Hit> CollectHits(List<Clause> clauses)
    {
        var hits = new List<Hit>();
        foreach (var clause in clauses)
        {
            var t = clause.Text;
            var firstThen = FirstThenRx.Match(t);

            // Revocation spans swallow the approve/stop words inside them ("取消批准").
            var revokeSpans = RevokeRx.Matches(t).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
            bool InRevoke(Match m) => revokeSpans.Any(s => m.Index >= s.Index && m.Index < s.End);

            foreach (Match m in RevokeRx.Matches(t))
            {
                hits.Add(MakeHit(HitKind.Revoke, m, clause, firstThen, null));
            }

            foreach (Match m in ApproveRx.Matches(t))
            {
                if (!InRevoke(m))
                {
                    hits.Add(MakeHit(HitKind.Approve, m, clause, firstThen, null));
                }
            }

            foreach (Match m in RejectRx.Matches(t))
            {
                hits.Add(MakeHit(HitKind.Reject, m, clause, firstThen, null));
            }

            foreach (Match m in StopRx.Matches(t))
            {
                if (!InRevoke(m))
                {
                    hits.Add(MakeHit(HitKind.Stop, m, clause, firstThen, null));
                }
            }

            var bareStop = BareStopRx.Match(t);
            if (bareStop.Success)
            {
                hits.Add(MakeHit(HitKind.Stop, bareStop, clause, firstThen, null));
            }

            foreach (Match m in ContinueRx.Matches(t))
            {
                hits.Add(MakeHit(HitKind.Continue, m, clause, firstThen, null));
            }

            if (PlanMentionRx.IsMatch(t))
            {
                foreach (Match m in PlanStartEnglishRx.Matches(t))
                {
                    hits.Add(MakeHit(HitKind.Continue, m, clause, firstThen, null));
                }
            }

            foreach (Match m in ApproveStartRx.Matches(t))
            {
                hits.Add(MakeHit(HitKind.ApproveStart, m, clause, firstThen, null));
            }

            foreach (Match m in WorkRx.Matches(t))
            {
                hits.Add(MakeHit(HitKind.Work, m, clause, firstThen, null));
            }

            foreach (var (tool, rx) in ToolMentions)
            {
                foreach (Match m in rx.Matches(t))
                {
                    var kind = tool switch
                    {
                        "approve_plan" => HitKind.Approve,
                        "reject_plan" => HitKind.Reject,
                        "stop_worker" => HitKind.Stop,
                        _ => HitKind.Continue
                    };
                    hits.Add(MakeHit(kind, m, clause, firstThen, tool));
                }
            }
        }

        return hits;
    }

    private static Hit MakeHit(HitKind kind, Match m, Clause clause, Match firstThen, string? tool)
    {
        var t = clause.Text;
        var before = t[..m.Index];
        var after = t[(m.Index + m.Length)..];
        var pos = clause.Start + m.Index;

        // History: a statement about the past never authorizes now ("批准了", "批准过", "之前已经批准").
        if (HistoricalRx.IsMatch(t) || PastSuffixRx.IsMatch(after))
        {
            return new Hit(kind, pos, HitState.Historical, tool);
        }

        // Negation scope: the clause up to the last contrastive/sequential word before the marker.
        var scope = before;
        var cut = NegationCutRx.Matches(before);
        if (cut.Count > 0)
        {
            var last = cut[^1];
            scope = before[(last.Index + last.Length)..];
        }

        var negations = NegatorRx.Matches(scope).Count;
        if (negations >= 1)
        {
            return new Hit(kind, pos, negations % 2 == 1 ? HitState.Negated : HitState.DoubleNegated, tool);
        }

        // Questions and conditions never authorize an action that changes plan/job state. Modify/build/test wording
        // (HitKind.Work) and approval companions keep their legacy meaning: they only widen the tool surface.
        var authorityKind = kind is HitKind.Approve or HitKind.Reject or HitKind.Revoke or HitKind.Stop or HitKind.Continue;
        if (authorityKind && (clause.EndsWithQuestion || QuestionRx.IsMatch(t)))
        {
            return new Hit(kind, pos, HitState.Question, tool);
        }

        if (authorityKind && clause.ConditionalCarry)
        {
            return new Hit(kind, pos, HitState.Conditional, tool);
        }

        if (IsDeferredByOrder(kind, m, before, firstThen))
        {
            return new Hit(kind, pos, HitState.Deferred, tool);
        }

        return new Hit(kind, pos, HitState.Effective, tool);
    }

    /// <summary>
    /// "先规划，之后批准" / "plan first, then approve": the approval belongs to a later step, not this message.
    /// Start verbs that follow an approval in the same message ("批准计划，然后开始执行") are not deferred.
    /// </summary>
    private static bool IsDeferredByOrder(HitKind kind, Match m, string before, Match firstThen)
    {
        if (kind is not (HitKind.Approve or HitKind.Reject or HitKind.Revoke))
        {
            return false;
        }

        if (firstThen.Success)
        {
            var firstSegmentEnd = firstThen.Index + firstThen.Length;
            if (m.Index >= firstSegmentEnd - 1)
            {
                var first = firstThen.Groups["a"].Value;
                if (!ApproveRx.IsMatch(first))
                {
                    return true;
                }
            }
        }

        return LaterRx.IsMatch(before);
    }

    private static string? RequiredToolFor(List<Hit> hits, GatewayIntentKind kind)
    {
        var kindMatch = kind switch
        {
            GatewayIntentKind.Approve => HitKind.Approve,
            GatewayIntentKind.Reject => HitKind.Reject,
            GatewayIntentKind.Stop => HitKind.Stop,
            _ => HitKind.Continue
        };

        var tools = hits
            .Where(h => h.Tool != null && h.Kind == kindMatch && h.State == HitState.Effective)
            .Select(h => h.Tool!)
            .ToHashSet(StringComparer.Ordinal);
        if (tools.Count == 0)
        {
            return null;
        }

        foreach (var (tool, _) in ToolMentions)
        {
            if (tools.Contains(tool))
            {
                return tool;
            }
        }

        return null;
    }
}
