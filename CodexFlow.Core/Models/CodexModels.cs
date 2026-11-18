// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Models;

public enum CodexTaskStatus
{
    Pending,
    Planning,
    Executing,
    Success,
    Failed,
    Skipped,
    CompletedWithWarnings,
    BlockedByDependency
}

public class CodexTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TaskType { get; set; } = CodexTaskClassifier.CodeTaskType;
    public CodexTaskStatus Status { get; set; } = CodexTaskStatus.Pending;
    public int StageId { get; set; }
    public Collection<string> Dependencies { get; } = new();
    public Collection<string> Inputs { get; } = new();
    public Collection<string> Outputs { get; } = new();
    public Collection<TaskChecklistItem> ChecklistItems { get; } = new();

    /// <summary>
    /// 结构化验收契约：任务完成时必须满足的文件状态断言。
    /// 由规划器在生成任务时声明，validator 在 fallback 路径中作为硬约束检查。
    /// </summary>
    public Collection<ArtifactAssertion> RequiredArtifacts { get; } = new();

    /// <summary>
    /// 结构化验收契约：任务完成时绝对不能出现的状态。
    /// 例如迁移类任务声明旧文件 file_not_exists。
    /// </summary>
    public Collection<ArtifactAssertion> ForbiddenStates { get; } = new();

    /// <summary>
    /// Files and directories (relative to the project root) this task may not touch, stated by the plan as data. Null means the plan did not
    /// say (an older plan): the task's prose is then read for prohibitions, as before. A list that is there, even an empty one, is the
    /// authority: a path in it is closed to the task whatever else the plan declares, and the prose no longer takes files away.
    /// Limits on the CONTENT of a file ("do not change other dependency versions") do not belong here, only whole files and directories.
    /// </summary>
    public List<string>? ForbiddenPaths { get; set; }

    /// <summary>
    /// 任务风险等级: Low, Medium, High (Level 3 新增)
    /// </summary>
    public string RiskLevel { get; set; } = "Low";

    /// <summary>
    /// 任务效能分级: 1 (Fast), 2 (Standard), 3 (Strict/AST) (Level 7 新增)
    /// </summary>
    public int ComplexityLevel { get; set; } = 2;

    /// <summary>
    /// 当前置依赖通过 fallback 验证（CompletedWithWarnings + IsFallback=true）时，
    /// 是否阻止本任务启动。默认 true（阻止），除非任务显式声明允许。
    /// </summary>
    public bool UnsafeIfDependencyFallbackPassed { get; set; } = true;

    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int RetryCount { get; set; }
    public string? ResultNotes { get; set; }

    /// <summary>
    /// 结构化执行证据：任务执行完成后由编排器填充。
    /// 包含变更文件列表、断言检查结果、build/test 证据等。
    /// </summary>
    public TaskExecutionEvidenceResult? ExecutionEvidence { get; set; }

    public void ReplaceDependencies(IEnumerable<string>? dependencies) => ReplaceCollection(Dependencies, dependencies);
    public void ReplaceInputs(IEnumerable<string>? inputs) => ReplaceCollection(Inputs, inputs);
    public void ReplaceOutputs(IEnumerable<string>? outputs) => ReplaceCollection(Outputs, outputs);
    public void ReplaceChecklistItems(IEnumerable<TaskChecklistItem>? checklistItems) => ReplaceCollection(ChecklistItems, checklistItems);
    public void ReplaceRequiredArtifacts(IEnumerable<ArtifactAssertion>? artifacts) => ReplaceCollection(RequiredArtifacts, artifacts);
    public void ReplaceForbiddenStates(IEnumerable<ArtifactAssertion>? states) => ReplaceCollection(ForbiddenStates, states);
    public void ReplaceForbiddenPaths(IEnumerable<string>? paths) => ForbiddenPaths = paths?.ToList();

    private static void ReplaceCollection<T>(Collection<T> target, IEnumerable<T>? source)
    {
        target.Clear();
        if (source == null)
        {
            return;
        }

        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}

/// <summary>
/// 单个可验证的文件状态断言，由规划器在任务生成时声明。
/// 用于 validator deterministic fallback 路径中的硬约束检查。
/// </summary>
public sealed record ArtifactAssertion(
    string Type,       // "file_exists", "file_not_exists", "file_contains", "file_not_contains"
    string Path,       // 相对于项目根目录的路径
    string? Text = null // file_contains/file_not_contains 需要的搜索文本
);

/// <summary>
/// 任务执行完成后收集的结构化证据。
/// 由编排器在 Post-Loop Finalization 阶段填充，供后续验证和依赖检查使用。
/// </summary>
public sealed record TaskExecutionEvidenceResult(
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> CreatedFiles,
    IReadOnlyList<string> DeletedFiles,
    bool HasSuccessfulBuildEvidence,
    bool HasSuccessfulTestEvidence,
    IReadOnlyList<string> AssertionResults,
    int TotalToolCalls,
    int WriteToolCalls
);

public static class CodexTaskClassifier
{
    public const string CodeTaskType = "code";
    public const string AnalysisTaskType = "analysis";

    private static readonly string[] AnalysisKeywords =
    [
        "分析", "识别", "梳理", "定位", "调研", "审查", "盘点", "评估", "现状", "阅读",
        "analy", "inspect", "review", "audit", "investigate", "research", "inventory", "discover", "locate"
    ];

    private static readonly string[] CodeKeywords =
    [
        "修改", "重构", "提取", "更新", "实现", "新增", "修复", "配置", "构建", "编译", "测试",
        "注册", "注入", "创建", "编写", "迁移", "合并", "提交", "删除",
        "refactor", "implement", "update", "add ", "fix", "configure", "build", "test",
        "register", "inject", "create", "write", "patch", "commit", "remove", "scaffold"
    ];

    // A task that says outright it changes nothing. This outranks both the declared type and the keyword count below: such a task also talks about
    // "build", "test" and "修改" (as things it will NOT do or only run for a baseline), and counting those words sent a read-only survey to a forge
    // worker, which must produce changes and failed for having made none. "不修改任何测试文件" is a scope limit, not this statement.
    private static readonly System.Text.RegularExpressions.Regex ReadOnlyStatement = new(
        @"仅(做|为|限)?只读|本任务只读|只做(调研|勘察|分析)" +
        @"|不(得|会|要)?(修改|改动|更改|写入)(任何|所有)(已有)?(文件|代码)|无(任何)?代码(改动|变更|修改)|不涉及代码(改动|修改)" +
        @"|do not (modify|change|edit|write) any (file|code)|no code changes?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    // A read-only activity that is only NAMED ("只读探索", "read-only review"). It says the task contains such a step, not that the task changes
    // nothing: a dependency upgrade whose description ended "具体路径由只读探索确定" was sent to an explore worker, changed nothing and was marked
    // completed. So a mention decides only when the planner did not declare a code task; the statement above still outranks the declared type.
    private static readonly System.Text.RegularExpressions.Regex ReadOnlyMention = new(
        @"只读(调研|勘察|分析|任务|探索|检查|盘点)|read-only (task|investigation|analysis|research|review|survey)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static bool Matches(System.Text.RegularExpressions.Regex pattern, string? title, string? description)
    {
        try
        {
            return pattern.IsMatch($"{title}\n{description}");
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsDeclaredCodeType(string? taskType)
        => taskType?.Trim() is { Length: > 0 } declared &&
           (declared.Equals(CodeTaskType, StringComparison.OrdinalIgnoreCase) ||
            declared.Equals("implementation", StringComparison.OrdinalIgnoreCase) ||
            declared.Equals("modify", StringComparison.OrdinalIgnoreCase) ||
            declared.Equals("scaffold", StringComparison.OrdinalIgnoreCase) ||
            declared.Equals("config", StringComparison.OrdinalIgnoreCase));

    public static string NormalizeTaskType(string? taskType, string? title, string? description)
    {
        if (Matches(ReadOnlyStatement, title, description) ||
            (!IsDeclaredCodeType(taskType) && Matches(ReadOnlyMention, title, description)))
        {
            return AnalysisTaskType;
        }

        if (!string.IsNullOrWhiteSpace(taskType))
        {
            var normalized = taskType.Trim();
            if (IsDeclaredCodeType(normalized))
            {
                return CodeTaskType;
            }

            if (string.Equals(normalized, AnalysisTaskType, StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("readonly", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("read-only", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("research", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("investigate", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("discovery", StringComparison.OrdinalIgnoreCase))
            {
                return AnalysisTaskType;
            }
        }

        var combined = $"{title}\n{description}";
        var hasCodeKeyword = CodeKeywords.Any(keyword => combined.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        var hasAnalysisKeyword = AnalysisKeywords.Any(keyword => combined.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        if (hasAnalysisKeyword && !hasCodeKeyword)
        {
            return AnalysisTaskType;
        }

        return CodeTaskType;
    }

    public static bool IsCodeExecutionTask(CodexTask? task)
    {
        if (task == null)
        {
            return false;
        }

        return string.Equals(
            NormalizeTaskType(task.TaskType, task.Title, task.Description),
            CodeTaskType,
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAnalysisTask(CodexTask? task)
    {
        if (task == null)
        {
            return false;
        }

        return string.Equals(
            NormalizeTaskType(task.TaskType, task.Title, task.Description),
            AnalysisTaskType,
            StringComparison.OrdinalIgnoreCase);
    }

    public static void NormalizeTask(CodexTask? task)
    {
        if (task == null)
        {
            return;
        }

        task.TaskType = NormalizeTaskType(task.TaskType, task.Title, task.Description);
        NormalizeArtifactContracts(task);
        StrengthenArtifactContracts(task);
        NormalizeArtifactContracts(task);
        CodexTaskProgressNormalizer.NormalizeTaskChecklist(task);
    }

    public static void NormalizePlan(IList<CodexTask>? tasks)
    {
        if (tasks == null)
        {
            return;
        }

        FoldVerificationOnlyTasks(tasks);

        foreach (var task in tasks.Where(t => t != null))
        {
            NormalizeTask(task);
        }
    }

    // A task that says it changes no code and only runs the tests or the build ("仅执行 dotnet test 命令…不进行代码修改").
    private static readonly System.Text.RegularExpressions.Regex VerificationOnlyStatement = new(
        @"不进行(任何)?代码(修改|改动|变更)|不(做|作)(任何)?代码(修改|改动|变更)|仅验证|只(做|作)验证|verification only|no code (is )?(changed|modified)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static readonly System.Text.RegularExpressions.Regex VerificationCommand = new(
        @"dotnet\s+(test|build)|npm\s+(test|run)|pytest|回归测试|运行测试|执行测试",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    // A title that names running the tests and no change ("运行核心测试验证"). Used only together with a task that declares no output at all.
    private static readonly System.Text.RegularExpressions.Regex VerificationTitle = new(
        "测试验证|回归测试|回归验证|(运行|执行|跑).*测试|run .*tests?|verify",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static readonly string[] ChangeVerbs =
        ["修改", "升级", "更新", "添加", "新增", "固定", "替换", "删除", "创建", "修复", "实现", "update", "upgrade", "add ", "fix", "pin", "bump", "create", "remove"];

    /// <summary>
    /// Says so outright ("不进行代码修改"), or declares nothing it would produce (no output file, no required artifact, no forbidden state) under
    /// a title that only names running the tests. Either way it must also name the command it runs.
    /// </summary>
    public static bool IsVerificationOnlyTask(CodexTask? task)
    {
        if (task == null || !Matches(VerificationCommand, task.Title, task.Description))
        {
            return false;
        }

        if (Matches(VerificationOnlyStatement, task.Title, task.Description))
        {
            return true;
        }

        return task.Outputs.Count == 0 && task.RequiredArtifacts.Count == 0 && task.ForbiddenStates.Count == 0 &&
               Matches(VerificationTitle, task.Title, null) &&
               !ChangeVerbs.Any(verb => task.Title.Contains(verb, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A plan task that only runs the tests has no worker that fits it: a forge worker must leave changes (seen live: asked for a write it had no
    /// reason to make, it created an empty directory and the job failed for having changed nothing) and a read-only worker cannot run commands.
    /// Its steps are appended to the last code task before it, whose worker can run them, and the host's own acceptance repeats the build and
    /// the tests after that task. Left alone when another task depends on it or when no code task precedes it.
    /// </summary>
    internal static void FoldVerificationOnlyTasks(IList<CodexTask> tasks)
    {
        if (tasks.IsReadOnly)
        {
            return;
        }

        for (var index = tasks.Count - 1; index > 0; index--)
        {
            var candidate = tasks[index];
            if (!IsVerificationOnlyTask(candidate) ||
                tasks.Any(other => other != null && !ReferenceEquals(other, candidate) &&
                                   other.Dependencies.Contains(candidate.Id, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            var host = tasks.Take(index).LastOrDefault(earlier =>
                earlier != null && !IsVerificationOnlyTask(earlier) &&
                string.Equals(NormalizeTaskType(earlier.TaskType, earlier.Title, earlier.Description), CodeTaskType, StringComparison.OrdinalIgnoreCase));
            if (host == null)
            {
                continue;
            }

            var steps = candidate.ChecklistItems.Select(static item => item.Text).Where(static text => !string.IsNullOrWhiteSpace(text)).ToList();
            host.Description = (host.Description ?? string.Empty).TrimEnd() +
                $"\n\n### 收尾验证（原任务 {candidate.Id}：{candidate.Title}）\n本任务的修改写完后运行，结果写进最终汇报；这一步不要求额外的文件改动。\n" +
                (steps.Count > 0 ? string.Join("\n", steps.Select(static step => "- " + step)) : "- " + candidate.Title);
            tasks.RemoveAt(index);
        }
    }

    public static IReadOnlyList<string> GetContractLintWarnings(CodexTask? task)
    {
        if (task == null)
        {
            return Array.Empty<string>();
        }

        var warnings = new List<string>();
        foreach (var assertion in task.RequiredArtifacts.Concat(task.ForbiddenStates))
        {
            if (!assertion.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var projectName = Path.GetFileNameWithoutExtension(CanonicalizePath(assertion.Path));
            if (!string.IsNullOrWhiteSpace(projectName) &&
                string.Equals(assertion.Text, projectName, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"任务契约包含可疑的自引用 .csproj 断言：{assertion.Path} -> {assertion.Text}");
            }
        }

        foreach (var assertion in task.RequiredArtifacts.Concat(task.ForbiddenStates))
        {
            if ((string.Equals(assertion.Type, "file_contains", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(assertion.Type, "file_not_contains", StringComparison.OrdinalIgnoreCase)) &&
                IsGatewayServicePath(assertion.Path) &&
                LooksLikeRepositoryToken(assertion.Text) &&
                !ShouldPreserveGatewayRepositoryContract(task))
            {
                warnings.Add($"任务契约对外部网关服务提出了可疑的仓储断言：{assertion.Path} -> {assertion.Text}");
            }
        }

        foreach (Match match in GenericInterfaceRegex.Matches($"{task.Title}\n{task.Description}"))
        {
            var interfaceName = match.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(interfaceName))
            {
                continue;
            }

            if (!task.RequiredArtifacts.Concat(task.ForbiddenStates).Any(a =>
                    string.Equals(a.Type, "file_contains", StringComparison.OrdinalIgnoreCase) &&
                    (a.Text?.Contains($"interface {interfaceName}<", StringComparison.Ordinal) ?? false)))
            {
                warnings.Add($"任务描述声明了泛型接口 {interfaceName}<...>，但契约未覆盖接口签名。");
            }
        }

        if (HasCircularCoreInfrastructureContract(task))
        {
            warnings.Add("任务契约形成了 Core 与 Infrastructure 的双向 .csproj 引用要求，这会导致循环依赖。");
        }

        return warnings;
    }

    public static IReadOnlyList<string> EvaluateExecutionSpecConformance(string? projectRoot, CodexTask? task)
    {
        if (task == null || string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            return Array.Empty<string>();
        }

        NormalizeTask(task);

        var issues = new List<string>();
        foreach (var assertion in task.RequiredArtifacts)
        {
            var normalizedPath = CanonicalizePath(assertion.Path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                continue;
            }

            var absolutePath = Path.Combine(projectRoot, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolutePath))
            {
                continue;
            }

            if (normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                assertion.Text is { Length: > 0 } assertionText &&
                assertionText.Contains("interface ", StringComparison.Ordinal) &&
                assertionText.Contains('<', StringComparison.Ordinal))
            {
                var fileContent = SafeReadAllText(absolutePath);
                if (!string.IsNullOrEmpty(fileContent) &&
                    fileContent.IndexOf(assertionText, StringComparison.Ordinal) < 0)
                {
                    issues.Add($"执行期契约预检失败：文件 {normalizedPath} 未满足接口签名要求，必须包含 \"{assertionText}\"。");
                }

                continue;
            }

            if (normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(assertion.Type, "file_contains", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(assertion.Text))
            {
                var fileContent = SafeReadAllText(absolutePath);
                if (!string.IsNullOrEmpty(fileContent) &&
                    fileContent.IndexOf(assertion.Text, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    issues.Add($"执行期契约预检失败：项目文件 {normalizedPath} 仍缺少关键引用 \"{assertion.Text}\"。");
                }
            }
        }

        return issues;
    }

    private static void NormalizeArtifactContracts(CodexTask task)
    {
        var explicitNotExistsPaths = task.RequiredArtifacts
            .Concat(task.ForbiddenStates)
            .Where(a => string.Equals(a.Type, "file_not_exists", StringComparison.OrdinalIgnoreCase))
            .Select(a => CanonicalizePath(a.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existenceByPath = new Dictionary<string, ArtifactAssertion>(StringComparer.OrdinalIgnoreCase);
        var otherAssertions = new List<ArtifactAssertion>();
        var otherKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assertion in task.RequiredArtifacts.Concat(task.ForbiddenStates))
        {
            var normalizedPath = CanonicalizePath(assertion.Path);
            var normalizedType = assertion.Type?.Trim().ToLowerInvariant() ?? string.Empty;

            if (explicitNotExistsPaths.Contains(normalizedPath) &&
                normalizedType is "file_exists" or "file_contains")
            {
                continue;
            }

            if (normalizedType is "file_exists" or "file_not_exists")
            {
                var inferredShouldExist = InferExpectedExistence(task, normalizedPath);
                var effectiveType = inferredShouldExist switch
                {
                    true => "file_exists",
                    false => "file_not_exists",
                    null => normalizedType
                };

                existenceByPath[normalizedPath] = new ArtifactAssertion(effectiveType, assertion.Path, assertion.Text);
                continue;
            }

            var key = $"{normalizedType}|{normalizedPath}|{assertion.Text ?? string.Empty}";
            if (otherKeys.Add(key))
            {
                otherAssertions.Add(assertion);
            }
        }

        task.RequiredArtifacts.Clear();
        task.ForbiddenStates.Clear();

        foreach (var assertion in existenceByPath.Values.OrderBy(a => CanonicalizePath(a.Path), StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(assertion.Type, "file_not_exists", StringComparison.OrdinalIgnoreCase))
            {
                task.ForbiddenStates.Add(assertion);
            }
            else
            {
                task.RequiredArtifacts.Add(assertion);
            }
        }

        foreach (var assertion in otherAssertions)
        {
            if (string.Equals(assertion.Type, "file_not_exists", StringComparison.OrdinalIgnoreCase))
            {
                task.ForbiddenStates.Add(assertion);
            }
            else
            {
                task.RequiredArtifacts.Add(assertion);
            }
        }
    }

    private static void StrengthenArtifactContracts(CodexTask task)
    {
        EnsureExplicitPathCoverage(task);
        NormalizeLayerMoveContracts(task);
        NormalizeDependencyDirectionContracts(task);
        NormalizeCoreIsolationContracts(task);
        EnsureCsprojReferenceCoverage(task);
        EnsureGenericInterfaceSignatureCoverage(task);
        EnsureInfrastructureReferenceCoverage(task);
        EnsureProgramRegistrationCoverage(task);
        RelaxGatewayServiceRepositoryContracts(task);
    }

    private static void EnsureExplicitPathCoverage(CodexTask task)
    {
        foreach (var line in EnumerateTaskLines(task))
        {
            var pathCandidates = ExtractPathCandidates(line);
            if (pathCandidates.Count == 0)
            {
                continue;
            }

            foreach (var path in pathCandidates)
            {
                if (ContainsAny(line, DeleteIndicators))
                {
                    RemoveConflictingOldPathAssertions(task, path);
                    AddAssertionIfMissing(task.ForbiddenStates, new ArtifactAssertion("file_not_exists", path));
                    continue;
                }

                if (HasAnyAssertionForPath(task, path))
                {
                    continue;
                }

                if (ContainsAny(line, CreateIndicators) || ContainsAny(line, UpdateIndicators))
                {
                    task.RequiredArtifacts.Add(new ArtifactAssertion("file_exists", path));
                }
            }
        }
    }

    private static void NormalizeLayerMoveContracts(CodexTask task)
    {
        var combined = $"{task.Title}\n{task.Description}";
        var moveMatches = LayerMoveRegex.Matches(combined);
        if (moveMatches.Count == 0)
        {
            return;
        }

        foreach (Match match in moveMatches)
        {
            var source = match.Groups["source"].Value.Trim();
            var target = match.Groups["target"].Value.Trim();
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            var sourceAssertions = task.RequiredArtifacts
                .Concat(task.ForbiddenStates)
                .Where(a => PathContainsLayer(a.Path, source))
                .ToList();
            var targetAssertions = task.RequiredArtifacts
                .Concat(task.ForbiddenStates)
                .Where(a => PathContainsLayer(a.Path, target))
                .ToList();

            foreach (var sourceAssertion in sourceAssertions)
            {
                var sourceFileName = Path.GetFileName(CanonicalizePath(sourceAssertion.Path));
                if (string.IsNullOrWhiteSpace(sourceFileName))
                {
                    continue;
                }

                var targetAssertion = targetAssertions.FirstOrDefault(a =>
                    string.Equals(
                        Path.GetFileName(CanonicalizePath(a.Path)),
                        sourceFileName,
                        StringComparison.OrdinalIgnoreCase));

                if (targetAssertion == null)
                {
                    continue;
                }

                RemoveConflictingOldPathAssertions(task, sourceAssertion.Path);
                AddAssertionIfMissing(task.ForbiddenStates, new ArtifactAssertion("file_not_exists", sourceAssertion.Path));
            }
        }
    }

    private static void EnsureCsprojReferenceCoverage(CodexTask task)
    {
        foreach (var line in EnumerateTaskLines(task))
        {
            var pathCandidates = ExtractPathCandidates(line)
                .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (pathCandidates.Count == 0)
            {
                continue;
            }

            var targetPath = pathCandidates[0];
            var referenceToken = ExtractCsprojReferenceToken(line, pathCandidates);
            if (string.IsNullOrWhiteSpace(referenceToken))
            {
                continue;
            }

            if (ContainsAny(line, DeleteIndicators))
            {
                AddAssertionIfMissing(task.RequiredArtifacts, new ArtifactAssertion("file_not_contains", targetPath, referenceToken));
                continue;
            }

            if (ContainsAny(line, CreateIndicators) || ContainsAny(line, UpdateIndicators))
            {
                AddAssertionIfMissing(task.RequiredArtifacts, new ArtifactAssertion("file_contains", targetPath, referenceToken));
            }
        }
    }

    private static void NormalizeDependencyDirectionContracts(CodexTask task)
    {
        if (!ShouldNormalizeCoreInfrastructureDependencyDirection(task))
        {
            return;
        }

        var coreInfrastructureAssertions = task.RequiredArtifacts
            .Concat(task.ForbiddenStates)
            .Where(a =>
                string.Equals(a.Type, "file_contains", StringComparison.OrdinalIgnoreCase) &&
                IsCoreProjectPath(a.Path) &&
                string.Equals(a.Text, "CleanApp.Infrastructure", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var assertion in coreInfrastructureAssertions)
        {
            RemoveMatchingAssertions(task.RequiredArtifacts, assertion.Path, "file_contains", "CleanApp.Infrastructure");
            RemoveMatchingAssertions(task.ForbiddenStates, assertion.Path, "file_contains", "CleanApp.Infrastructure");
            AddAssertionIfMissing(
                task.RequiredArtifacts,
                new ArtifactAssertion("file_not_contains", assertion.Path, "CleanApp.Infrastructure"));
        }
    }

    private static void NormalizeCoreIsolationContracts(CodexTask task)
    {
        if (!ShouldNormalizeCoreIsolationContracts(task))
        {
            return;
        }

        var infrastructureProjectPath =
            EnumerateTaskLines(task)
                .SelectMany(ExtractPathCandidates)
                .FirstOrDefault(path =>
                    path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                    IsInfrastructureProjectPath(path))
            ?? "src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj";

        var coreLeakAssertions = task.RequiredArtifacts
            .Concat(task.ForbiddenStates)
            .Where(a =>
                string.Equals(a.Type, "file_contains", StringComparison.OrdinalIgnoreCase) &&
                IsCoreLayerPath(a.Path) &&
                LooksLikeInfrastructureLeakageToken(a.Text))
            .ToList();

        foreach (var assertion in coreLeakAssertions)
        {
            var token = assertion.Text ?? string.Empty;
            RemoveMatchingAssertions(task.RequiredArtifacts, assertion.Path, "file_contains", token);
            RemoveMatchingAssertions(task.ForbiddenStates, assertion.Path, "file_contains", token);
            AddAssertionIfMissing(
                task.RequiredArtifacts,
                new ArtifactAssertion("file_not_contains", assertion.Path, token));

            if (LooksLikeInfrastructurePackageToken(token) &&
                !string.IsNullOrWhiteSpace(infrastructureProjectPath))
            {
                AddAssertionIfMissing(
                    task.RequiredArtifacts,
                    new ArtifactAssertion("file_contains", infrastructureProjectPath, token));
            }
        }
    }

    private static void EnsureGenericInterfaceSignatureCoverage(CodexTask task)
    {
        foreach (Match match in GenericInterfaceRegex.Matches($"{task.Title}\n{task.Description}"))
        {
            var interfaceName = match.Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(interfaceName))
            {
                continue;
            }

            var interfacePath = ResolveInterfacePath(task, interfaceName);
            if (string.IsNullOrWhiteSpace(interfacePath))
            {
                continue;
            }

            AddAssertionIfMissing(
                task.RequiredArtifacts,
                new ArtifactAssertion("file_contains", interfacePath, $"interface {interfaceName}<"));
        }
    }

    private static void EnsureInfrastructureReferenceCoverage(CodexTask task)
    {
        var interfacePaths = EnumerateTaskLines(task)
            .SelectMany(ExtractPathCandidates)
            .Where(path =>
                path.Contains("/Interfaces/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var implementationPaths = EnumerateTaskLines(task)
            .SelectMany(ExtractPathCandidates)
            .Where(path =>
                PathContainsLayer(path, "Infrastructure") &&
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (interfacePaths.Count == 0 || implementationPaths.Count == 0)
        {
            return;
        }

        foreach (var implementationPath in implementationPaths)
        {
            var infrastructureProjectPath =
                EnumerateTaskLines(task)
                    .SelectMany(ExtractPathCandidates)
                    .FirstOrDefault(path =>
                        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                        PathContainsLayer(path, "Infrastructure"))
                ?? InferOwningCsprojPath(implementationPath);

            if (string.IsNullOrWhiteSpace(infrastructureProjectPath))
            {
                continue;
            }

            foreach (var interfacePath in interfacePaths)
            {
                var referencedProjectName = InferOwningProjectName(interfacePath);
                var infrastructureProjectName = InferOwningProjectName(infrastructureProjectPath);
                if (string.IsNullOrWhiteSpace(referencedProjectName) ||
                    string.IsNullOrWhiteSpace(infrastructureProjectName) ||
                    string.Equals(referencedProjectName, infrastructureProjectName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddAssertionIfMissing(
                    task.RequiredArtifacts,
                    new ArtifactAssertion("file_contains", infrastructureProjectPath, referencedProjectName));
            }
        }
    }

    private static void EnsureProgramRegistrationCoverage(CodexTask task)
    {
        var programLines = EnumerateTaskLines(task)
            .Where(line =>
                line.Contains("Program.cs", StringComparison.OrdinalIgnoreCase) &&
                ContainsAny(line, RegistrationIndicators))
            .ToList();

        if (programLines.Count == 0)
        {
            return;
        }

        var programPath = programLines
            .SelectMany(ExtractPathCandidates)
            .FirstOrDefault(path => path.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase))
            ?? "src/CleanApp/Program.cs";

        if (HasTextAssertionForPath(task, programPath))
        {
            return;
        }

        var registrationTokens = ExtractRegistrationTokens(task).Take(2).ToList();
        if (registrationTokens.Count == 0)
        {
            return;
        }

        foreach (var registrationToken in registrationTokens)
        {
            AddAssertionIfMissing(task.RequiredArtifacts, new ArtifactAssertion("file_contains", programPath, registrationToken));
        }
    }

    private static void RelaxGatewayServiceRepositoryContracts(CodexTask task)
    {
        if (ShouldPreserveGatewayRepositoryContract(task))
        {
            return;
        }

        RemoveGatewayRepositoryAssertions(task.RequiredArtifacts);
        RemoveGatewayRepositoryAssertions(task.ForbiddenStates);
    }

    private static void RemoveGatewayRepositoryAssertions(Collection<ArtifactAssertion> assertions)
    {
        for (var i = assertions.Count - 1; i >= 0; i--)
        {
            var assertion = assertions[i];
            if (!IsGatewayServicePath(assertion.Path) || !LooksLikeRepositoryToken(assertion.Text))
            {
                continue;
            }

            if (string.Equals(assertion.Type, "file_contains", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(assertion.Type, "file_not_contains", StringComparison.OrdinalIgnoreCase))
            {
                assertions.RemoveAt(i);
            }
        }
    }

    private static bool? InferExpectedExistence(CodexTask task, string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        var combined = $"{task.Title}\n{task.Description}".Replace('\\', '/');
        var createScore = 0;
        var deleteScore = 0;

        foreach (var rawLine in combined.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine.Replace('\\', '/');
            if (!line.Contains(normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ContainsAny(line, DeleteIndicators))
            {
                deleteScore++;
            }

            if (ContainsAny(line, CreateIndicators))
            {
                createScore++;
            }
        }

        if (deleteScore > createScore && deleteScore > 0)
        {
            return false;
        }

        if (createScore > deleteScore && createScore > 0)
        {
            return true;
        }

        return null;
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> keywords)
    {
        // "不删除 X" / "保留 X，无需移除" asks for the opposite of the verb; only an affirmed verb synthesizes an assertion that
        // verification will later enforce. Keywords that are already a statement of the end state are not verbs and are taken as written.
        foreach (var keyword in keywords)
        {
            if (EndStateKeywords.Contains(keyword)
                ? text.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                : CodexFlow.Core.Text.AffirmedText.ContainsAffirmed(text, CodexFlow.Core.Text.AffirmedText.Style.Task, keyword))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly HashSet<string> EndStateKeywords = new(StringComparer.OrdinalIgnoreCase) { "旧位置", "新位置", "not exist", "new file" };

    private static IEnumerable<string> EnumerateTaskLines(CodexTask task)
    {
        foreach (var rawLine in $"{task.Title}\n{task.Description}".Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return rawLine.Replace('\\', '/');
        }

        foreach (var item in task.ChecklistItems)
        {
            if (!string.IsNullOrWhiteSpace(item.Text))
            {
                yield return item.Text.Replace('\\', '/');
            }
        }
    }

    private static List<string> ExtractPathCandidates(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return PathRegex.Matches(text)
            .Select(match => CanonicalizePath(match.Value))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasAnyAssertionForPath(CodexTask task, string path)
    {
        return task.RequiredArtifacts.Concat(task.ForbiddenStates).Any(a =>
            PathsMatch(a.Path, path));
    }

    private static bool HasTextAssertionForPath(CodexTask task, string path)
    {
        return task.RequiredArtifacts.Concat(task.ForbiddenStates).Any(a =>
            PathsMatch(a.Path, path) &&
            (string.Equals(a.Type, "file_contains", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(a.Type, "file_not_contains", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool PathsMatch(string? left, string? right)
    {
        var normalizedLeft = CanonicalizePath(left);
        var normalizedRight = CanonicalizePath(right);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase) ||
               normalizedLeft.EndsWith("/" + normalizedRight, StringComparison.OrdinalIgnoreCase) ||
               normalizedRight.EndsWith("/" + normalizedLeft, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathContainsLayer(string? path, string layer)
    {
        var segments = CanonicalizePath(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            if (string.Equals(segment, layer, StringComparison.OrdinalIgnoreCase) ||
                segment.EndsWith("." + layer, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveConflictingOldPathAssertions(CodexTask task, string oldPath)
    {
        RemoveMatchingAssertions(task.RequiredArtifacts, oldPath, "file_exists", "file_contains");
        RemoveMatchingAssertions(task.ForbiddenStates, oldPath, "file_contains");
    }

    private static void RemoveMatchingAssertions(Collection<ArtifactAssertion> assertions, string path, params string[] types)
    {
        for (var i = assertions.Count - 1; i >= 0; i--)
        {
            var assertion = assertions[i];
            if (PathsMatch(assertion.Path, path) &&
                types.Any(type => string.Equals(type, assertion.Type, StringComparison.OrdinalIgnoreCase)))
            {
                assertions.RemoveAt(i);
            }
        }
    }

    private static void RemoveMatchingAssertions(Collection<ArtifactAssertion> assertions, string path, string type, string text)
    {
        for (var i = assertions.Count - 1; i >= 0; i--)
        {
            var assertion = assertions[i];
            if (PathsMatch(assertion.Path, path) &&
                string.Equals(assertion.Type, type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(assertion.Text ?? string.Empty, text, StringComparison.OrdinalIgnoreCase))
            {
                assertions.RemoveAt(i);
            }
        }
    }

    private static bool HasCircularCoreInfrastructureContract(CodexTask task)
    {
        return HasAssertion(
                   task,
                   "file_contains",
                   IsCoreProjectPath,
                   "CleanApp.Infrastructure") &&
               HasAssertion(
                   task,
                   "file_contains",
                   IsInfrastructureProjectPath,
                   "CleanApp.Core");
    }

    private static bool HasAssertion(
        CodexTask task,
        string type,
        Func<string, bool> pathPredicate,
        string text)
    {
        return task.RequiredArtifacts.Concat(task.ForbiddenStates).Any(a =>
            string.Equals(a.Type, type, StringComparison.OrdinalIgnoreCase) &&
            pathPredicate(a.Path) &&
            string.Equals(a.Text ?? string.Empty, text, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddAssertionIfMissing(Collection<ArtifactAssertion> assertions, ArtifactAssertion candidate)
    {
        if (assertions.Any(existing =>
            string.Equals(existing.Type, candidate.Type, StringComparison.OrdinalIgnoreCase) &&
            PathsMatch(existing.Path, candidate.Path) &&
            string.Equals(existing.Text ?? string.Empty, candidate.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        assertions.Add(candidate);
    }

    private static string? ExtractCsprojReferenceToken(string line, List<string> pathCandidates)
    {
        var targetProjectName = pathCandidates.Count > 0
            ? Path.GetFileNameWithoutExtension(CanonicalizePath(pathCandidates[0]))
            : null;

        if (pathCandidates.Count > 1)
        {
            var referencedProjectName = Path.GetFileNameWithoutExtension(CanonicalizePath(pathCandidates[1]));
            if (!string.Equals(referencedProjectName, targetProjectName, StringComparison.OrdinalIgnoreCase))
            {
                return referencedProjectName;
            }
        }

        var packageMatch = QualifiedTokenRegex.Matches(line)
            .Select(match => match.Value)
            .FirstOrDefault(token =>
                !token.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(token, targetProjectName, StringComparison.OrdinalIgnoreCase));

        return packageMatch;
    }

    private static IEnumerable<string> ExtractRegistrationTokens(CodexTask task)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in RegistrationSymbolRegex.Matches($"{task.Title}\n{task.Description}"))
        {
            var token = match.Value;
            if (IgnoredRegistrationTokens.Contains(token))
            {
                continue;
            }

            counts[token] = counts.TryGetValue(token, out var count) ? count + 1 : 1;
        }

        return counts
            .OrderByDescending(kvp => ScoreRegistrationToken(kvp.Key, kvp.Value))
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => kvp.Key);
    }

    private static int ScoreRegistrationToken(string token, int count)
    {
        var score = count * 10;
        if (token.StartsWith('I') &&
            (token.EndsWith("Repository", StringComparison.Ordinal) ||
             token.EndsWith("Service", StringComparison.Ordinal) ||
             token.EndsWith("Settings", StringComparison.Ordinal)))
        {
            score += 50;
        }
        else if (token.EndsWith("Repository", StringComparison.Ordinal) ||
                 token.EndsWith("Service", StringComparison.Ordinal) ||
                 token.EndsWith("Settings", StringComparison.Ordinal))
        {
            score += 25;
        }

        return score;
    }

    private static string CanonicalizePath(string? path)
        => (path ?? string.Empty).Replace('\\', '/').Trim();

    private static string SafeReadAllText(string absolutePath)
    {
        try
        {
            return File.ReadAllText(absolutePath);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string? ResolveInterfacePath(CodexTask task, string interfaceName)
    {
        var expectedFileName = interfaceName + ".cs";
        var explicitPath = EnumerateTaskLines(task)
            .SelectMany(ExtractPathCandidates)
            .FirstOrDefault(path => path.EndsWith(expectedFileName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        return task.RequiredArtifacts
            .Concat(task.ForbiddenStates)
            .Select(assertion => assertion.Path)
            .FirstOrDefault(path => path.EndsWith(expectedFileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? InferOwningProjectName(string path)
    {
        var segments = CanonicalizePath(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length >= 2 &&
            string.Equals(segments[0], "src", StringComparison.OrdinalIgnoreCase))
        {
            return segments[1];
        }

        if (segments.Length >= 1)
        {
            return Path.GetFileNameWithoutExtension(segments[^1]);
        }

        return null;
    }

    private static bool ShouldNormalizeCoreInfrastructureDependencyDirection(CodexTask task)
    {
        var combined = $"{task.Title}\n{task.Description}";
        if (!CoreInfrastructureDependencyInversionIndicators.Any(indicator =>
                combined.Contains(indicator, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return HasAssertion(
                   task,
                   "file_contains",
                   IsCoreProjectPath,
                   "CleanApp.Infrastructure") ||
               HasAssertion(
                   task,
                   "file_contains",
                   IsInfrastructureProjectPath,
                   "CleanApp.Core");
    }

    private static bool ShouldNormalizeCoreIsolationContracts(CodexTask task)
    {
        var combined = $"{task.Title}\n{task.Description}";
        if (!CoreIsolationIndicators.Any(indicator =>
                combined.Contains(indicator, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return task.RequiredArtifacts.Concat(task.ForbiddenStates).Any(a =>
            string.Equals(a.Type, "file_contains", StringComparison.OrdinalIgnoreCase) &&
            IsCoreLayerPath(a.Path) &&
            LooksLikeInfrastructureLeakageToken(a.Text));
    }

    private static bool IsCoreProjectPath(string path)
    {
        var normalized = CanonicalizePath(path);
        return normalized.Contains("/CleanApp.Core/", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("/CleanApp.Core.csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCoreLayerPath(string path)
        => IsCoreProjectPath(path) || PathContainsLayer(path, "Core");

    private static bool IsInfrastructureProjectPath(string path)
    {
        var normalized = CanonicalizePath(path);
        return normalized.Contains("/CleanApp.Infrastructure/", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("/CleanApp.Infrastructure.csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static string? InferOwningCsprojPath(string sourcePath)
    {
        var segments = CanonicalizePath(sourcePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length >= 2 &&
            string.Equals(segments[0], "src", StringComparison.OrdinalIgnoreCase))
        {
            var projectName = segments[1];
            return $"src/{projectName}/{projectName}.csproj";
        }

        return null;
    }

    private static bool LooksLikeRepositoryToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("Repository", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeInfrastructureLeakageToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return InfrastructureLeakageTokens.Any(token =>
            string.Equals(token, text, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeInfrastructurePackageToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return InfrastructurePackageTokens.Any(token =>
            string.Equals(token, text, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGatewayServicePath(string? path)
    {
        var fileName = Path.GetFileName(CanonicalizePath(path));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return GatewayServiceIndicators.Any(indicator =>
            fileName.Contains(indicator, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldPreserveGatewayRepositoryContract(CodexTask task)
    {
        var combined = $"{task.Title}\n{task.Description}";
        return GatewayRepositoryPreserveIndicators.Any(indicator =>
            combined.Contains(indicator, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Regex PathRegex = new(@"(?:[A-Za-z0-9_.-]+[\\/])+[A-Za-z0-9_.-]+\.[A-Za-z0-9_.-]+", RegexOptions.Compiled);
    private static readonly Regex LayerMoveRegex = new(@"从\s*(?<source>[A-Za-z][A-Za-z0-9_.-]+)\s*层?.{0,16}?(?:移至|迁移至|移动到|上移至|下沉到|移到)\s*(?<target>[A-Za-z][A-Za-z0-9_.-]+)\s*层?", RegexOptions.Compiled);
    private static readonly Regex QualifiedTokenRegex = new(@"\b[A-Z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+\b", RegexOptions.Compiled);
    private static readonly Regex RegistrationSymbolRegex = new(@"\b(?:I[A-Z][A-Za-z0-9_]+|[A-Z][A-Za-z0-9_]+(?:Repository|Service|Settings|UnitOfWork))\b", RegexOptions.Compiled);
    private static readonly Regex GenericInterfaceRegex = new(@"\b(?<name>I[A-Z][A-Za-z0-9_]*)\s*<\s*T[A-Za-z0-9_]*\s*>", RegexOptions.Compiled);

    private static readonly string[] DeleteIndicators =
    [
        "删除", "移除", "清理", "去掉", "废弃", "删掉", "remove", "delete", "drop", "cleanup", "clean up", "not exist", "旧位置"
    ];

    private static readonly string[] CreateIndicators =
    [
        "创建", "新增", "添加", "生成", "迁移", "下沉", "新位置", "实现", "注册", "修复", "create", "add", "generate", "migrate", "move to", "new file"
    ];

    private static readonly string[] UpdateIndicators =
    [
        "修改", "更新", "调整", "重构", "配置", "替换", "update", "modify", "refactor", "configure", "register"
    ];

    private static readonly string[] RegistrationIndicators =
    [
        "注册", "注入", "依赖注入", "配置绑定", "AddScoped", "AddTransient", "AddSingleton", "register", "inject"
    ];

    private static readonly HashSet<string> IgnoredRegistrationTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program",
        "Core",
        "Infrastructure",
        "AppDbContext",
        "CleanApp"
    };

    private static readonly string[] GatewayServiceIndicators =
    [
        "Mongo", "GridFS", "GridFs", "Redis", "Blob", "Bucket", "Client", "Gateway", "S3"
    ];

    private static readonly string[] GatewayRepositoryPreserveIndicators =
    [
        "元数据", "metadata", "领域仓储", "domain repository", "repository-backed gateway", "仓储化"
    ];

    private static readonly string[] CoreInfrastructureDependencyInversionIndicators =
    [
        "依赖倒置", "解耦", "移除对", "移除引用", "删除引用", "上移接口", "dependency inversion",
        "decouple", "remove projectreference", "remove reference", "move interface to core"
    ];

    private static readonly string[] CoreIsolationIndicators =
    [
        "依赖倒置", "解耦", "修复依赖", "移除对", "移除引用", "删除引用", "迁移 MongoDB 服务",
        "迁移 Mongo 服务", "GridFS", "MongoDB", "gateway", "service migration",
        "dependency inversion", "decouple", "remove reference", "migrate mongodb service"
    ];

    private static readonly string[] InfrastructureLeakageTokens =
    [
        "AppDbContext",
        "CleanApp.Infrastructure",
        "MongoDB.Driver",
        "IMongoDatabase",
        "GridFSBucket",
        "IGridFSBucket",
        "StackExchange.Redis"
    ];

    private static readonly string[] InfrastructurePackageTokens =
    [
        "MongoDB.Driver",
        "StackExchange.Redis",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.EntityFrameworkCore.Sqlite"
    ];
}

public enum SessionWorkMode
{
    Development = 0,
    Daily = 1
}

public sealed record SessionPolicySnapshot(
    SessionWorkMode WorkMode,
    int ModeVersion,
    bool PlanModeActive,
    int PlanModeVersion,
    DateTime? PlanModeChangedAtUtc = null,
    string? PlanModeChangedBy = null)
{
    public static SessionPolicySnapshot Default { get; } = new(SessionWorkMode.Development, 1, false, 1);

    public static SessionPolicySnapshot FromSession(CodexSession? session)
        => session is null
            ? Default
            : new SessionPolicySnapshot(
                session.WorkMode,
                Math.Max(1, session.ModeVersion),
                session.PlanModeActive,
                Math.Max(1, session.PlanModeVersion),
                session.PlanModeChangedAtUtc,
                session.PlanModeChangedBy);
}

public interface ISessionPolicyResolver
{
    Task<SessionPolicySnapshot> ResolveRequiredAsync(string sessionId, CancellationToken ct = default);
}

public partial class CodexSession
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public Uri? ProjectUrl { get; set; }
    public string ProjectSummary { get; set; } = string.Empty;
    public string WorkspacePath { get; set; } = string.Empty;
    public int CurrentStage { get; set; }
    public string? ActiveTaskId { get; set; } // 当前正在执行的任务 ID
    public SessionWorkMode WorkMode { get; set; } = SessionWorkMode.Development;
    public int ModeVersion { get; set; } = 1;
    public bool PlanModeActive { get; set; }
    public int PlanModeVersion { get; set; } = 1;
    public DateTime? PlanModeChangedAtUtc { get; set; }
    public string? PlanModeChangedBy { get; set; }
    public Collection<CodexTask> Plan { get; } = new();
    public Dictionary<string, string> Metadata { get; } = new();

    // [Bug-01 Fix] Plan lifecycle tracking — prevents silent overwrite of a confirmed plan
    // PlanVersion is set on every successful session-plan creation call; stays non-null until session clear.
    // PlanGeneratedAtUtc is non-null whenever a plan was successfully generated this session.
    public string? PlanVersion { get; set; }
    public DateTime? PlanGeneratedAtUtc { get; set; }
    public string? CurrentPlanArtifactId { get; set; }

    public void ReplacePlan(IEnumerable<CodexTask>? plan) => ReplaceCollection(Plan, plan);

    // [Bug-01 Fix] ClearPlan also resets lifecycle tracking fields to prevent false "plan loss" detection
    // on subsequent legitimate re-planning. This is called when a plan is fully completed or explicitly reset.
    public void ClearPlan()
    {
        Plan.Clear();
        PlanVersion = null;
        PlanGeneratedAtUtc = null;
        CurrentPlanArtifactId = null;
    }

    public void ReplaceMetadata(IEnumerable<KeyValuePair<string, string>>? metadata)
    {
        Metadata.Clear();
        if (metadata == null)
        {
            return;
        }

        foreach (var entry in metadata)
        {
            Metadata[entry.Key] = entry.Value;
        }
    }

    internal static Uri? CreateProjectUri(string? projectUrl)
    {
        return Uri.TryCreate(projectUrl, UriKind.Absolute, out var parsedUri)
            ? parsedUri
            : null;
    }

    private static void ReplaceCollection<T>(Collection<T> target, IEnumerable<T>? source)
    {
        target.Clear();
        if (source == null)
        {
            return;
        }

        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}

public static class CodexPlanStateGuards
{
    /// <summary>The placeholder task that stands for a plan being generated right now.</summary>
    public const string PlanGenerationPlaceholderTaskId = "TASK-PLANNING";

    /// <summary>
    /// A plan the user has NOT approved yet: a draft (or pending-approval) plan artifact whose tasks are only previews (status Planning), nothing
    /// ready to run or running. Such a plan is a proposal, not work in progress, so a new requirement may replace it. An approved plan (tasks
    /// Pending/Executing), a plan being generated right now, or a plan without an artifact is not replaceable. Decided by the artifact's status and
    /// the task statuses, never by wording.
    /// </summary>
    public static bool IsUnapprovedDraft(IEnumerable<CodexTask>? plan, Planning.Artifacts.PlanArtifactStatus? currentArtifactStatus)
    {
        var tasks = plan?.Where(t => t != null).ToList() ?? [];
        if (tasks.Count == 0)
        {
            return false;
        }

        if (tasks.Any(t => t.Status is CodexTaskStatus.Pending or CodexTaskStatus.Executing) ||
            tasks.Any(t => string.Equals(t.Id, PlanGenerationPlaceholderTaskId, StringComparison.Ordinal)))
        {
            return false;
        }

        return currentArtifactStatus is Planning.Artifacts.PlanArtifactStatus.Draft
            or Planning.Artifacts.PlanArtifactStatus.NeedsClarification
            or Planning.Artifacts.PlanArtifactStatus.ReadyForApproval;
    }

    public static bool HasPendingOrExecutingTasks(IEnumerable<CodexTask>? plan)
    {
        if (plan == null)
        {
            return false;
        }

        return plan.Any(t => t != null && t.Status is CodexTaskStatus.Pending
            or CodexTaskStatus.Executing
            or CodexTaskStatus.Planning);
    }

    public static bool IsPlanFullyCompleted(IEnumerable<CodexTask>? plan)
    {
        if (plan == null)
        {
            return false;
        }

        var taskList = plan.Where(t => t != null).ToList();
        if (taskList.Count == 0)
        {
            return false;
        }

        // BUG-002 fix: CompletedWithWarnings is no longer treated as "fully completed".
        // Plans with fallback-passed tasks have residual risk and must be acknowledged.
        return taskList.All(t => t.Status is CodexTaskStatus.Success
            or CodexTaskStatus.Skipped);
    }

    public static bool AreDependenciesSatisfied(CodexTask task, IEnumerable<CodexTask>? plan, out IReadOnlyList<string> unsatisfiedDependencies)
    {
        ArgumentNullException.ThrowIfNull(task);

        var failures = new List<string>();
        if (task.Dependencies.Count == 0)
        {
            unsatisfiedDependencies = failures;
            return true;
        }

        if (plan == null)
        {
            failures.Add("计划为空");
            unsatisfiedDependencies = failures;
            return false;
        }

        var taskList = plan.Where(t => t != null).ToList();
        foreach (var depId in task.Dependencies)
        {
            var depTask = taskList.FirstOrDefault(t => string.Equals(t.Id, depId, StringComparison.OrdinalIgnoreCase));
            if (depTask == null)
            {
                failures.Add($"{depId} (未找到)");
                continue;
            }

            if (depTask.Status == CodexTaskStatus.Success)
            {
                continue;
            }

            if (depTask.Status == CodexTaskStatus.CompletedWithWarnings)
            {
                if (task.UnsafeIfDependencyFallbackPassed)
                {
                    failures.Add($"{depId} (CompletedWithWarnings)");
                    continue;
                }

                var declaredAssertions = depTask.RequiredArtifacts.Count + depTask.ForbiddenStates.Count;
                if (declaredAssertions == 0)
                {
                    failures.Add($"{depId} (CompletedWithWarnings, 无结构化断言)");
                    continue;
                }

                if (depTask.ExecutionEvidence == null)
                {
                    failures.Add($"{depId} (CompletedWithWarnings, 缺少执行证据)");
                    continue;
                }

                if (depTask.ExecutionEvidence.AssertionResults.Any(r => r.Contains("断言失败", StringComparison.Ordinal)))
                {
                    failures.Add($"{depId} (CompletedWithWarnings, 断言失败)");
                    continue;
                }

                continue;
            }

            failures.Add($"{depId} ({depTask.Status})");
        }

        unsatisfiedDependencies = failures;
        return failures.Count == 0;
    }
}
