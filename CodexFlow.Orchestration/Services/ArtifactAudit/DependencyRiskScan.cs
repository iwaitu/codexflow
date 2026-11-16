// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma warning disable CS1591 // internal audit surface; documented in docs/dependency-baseline.md
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using CodexFlow.Contracts;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodexFlow.Services.ArtifactAudit;

/// <summary>Result of scanning the dependencies of one commit with the same engine as the artifact audit.</summary>
/// <param name="Available">False when the scan could not produce evidence (no image, no isolation, advisory data unusable, audit error).</param>
public sealed record DependencyScanReport(
    string Commit,
    bool Available,
    ArtifactAuditState State,
    string Message,
    IReadOnlyList<ArtifactAuditFinding> Findings,
    IReadOnlyList<ArtifactAuditFinding> BaselineDebt,
    IReadOnlyList<ArtifactDependencyRemediation> Remediation)
{
    public static DependencyScanReport Unavailable(string commit, string message) =>
        new(commit, false, ArtifactAuditState.Error, message, [], [], []);

    /// <summary>Every vulnerability advisory the scan matched, whether or not a reviewed baseline accepted it.</summary>
    public IEnumerable<ArtifactAuditFinding> Vulnerabilities =>
        Findings.Concat(BaselineDebt).Where(f => f.Layer == "dependencies" && f.Category == "Vulnerability");
}

public interface IDependencyRiskScanService
{
    /// <summary>
    /// Scans the dependencies of <paramref name="commit"/> in a pinned, network-less snapshot taken from the git object store of
    /// <paramref name="repositoryRoot"/>. Never throws for scan problems (the report says <c>Available = false</c>); only caller
    /// cancellation propagates.
    /// </summary>
    Task<DependencyScanReport> ScanCommitAsync(string repositoryRoot, string commit, string userId, string sessionId, string tag, CancellationToken ct);
}

public sealed class DependencyRiskScanService(
    IIsolatedExecutionService execution,
    IOptions<ArtifactAuditOptions> options,
    INuGetAdvisorySnapshotProvider advisories,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<DependencyRiskScanService>? logger = null,
    Ecosystems.IOsvSnapshotProvider? osvAdvisories = null,
    IOptionsMonitor<ExecutionOptions>? executionOptions = null) : IDependencyRiskScanService
{
    public async Task<DependencyScanReport> ScanCommitAsync(string repositoryRoot, string commit, string userId, string sessionId, string tag, CancellationToken ct)
    {
        if (!AuditText.IsFullCommitId(commit)) return DependencyScanReport.Unavailable(commit, "The scan needs a full commit id.");

        var localPolicy = LocalModeVerificationPolicy.Evaluate(configuration, executionOptions);
        if (localPolicy.IsLocalBackend)
        {
            var reason = localPolicy.Policy == LocalVerificationModePolicy.Block
                ? $"{LocalModeVerificationPolicy.BlockedStatus}: 本地执行模式下已配置阻止未隔离依赖扫描；请切换 Execution:Backend=Docker。"
                : $"{LocalModeVerificationPolicy.SkippedReasonCode}: {LocalModeVerificationPolicy.UnverifiedNotice}";
            return DependencyScanReport.Unavailable(commit, reason);
        }

        var image = configuration["ArtifactVerification:Image"];
        if (string.IsNullOrWhiteSpace(image)) return DependencyScanReport.Unavailable(commit, "ArtifactVerification:Image is not configured.");

        try
        {
            var safeTag = AuditText.Sanitize(tag, 40);
            var context = new TrustedExecutionContext
            {
                UserId = userId, SessionId = sessionId, JobId = "dependency-scan:" + safeTag, TaskId = "dependency-scan",
                AttemptId = $"dependency-scan:{safeTag}:{commit[..8]}", WorkspaceRoot = repositoryRoot, BaseCommit = commit,
                ScopeHash = TrustedExecutionContextFactory.ComputeScopeHash(ExportScope.Empty), Scope = ExportScope.Empty,
                Deadline = time.GetUtcNow().AddMinutes(15)
            };
            var opened = await execution.OpenSessionAsync(context, new IsolatedSessionOptions(image, PinToBaseCommit: true), ct).ConfigureAwait(false);
            if (!opened.Decision.IsAllowed || opened.Session is null)
                return DependencyScanReport.Unavailable(commit, $"Isolated snapshot unavailable: {AuditText.Sanitize(opened.Decision.Message, 200)}");

            await using var session = opened.Session;
            if (session.Layout.Backend == ExecutionBackend.Local)
            {
                var reevaluated = LocalModeVerificationPolicy.Evaluate(configuration, executionOptions);
                var reason = reevaluated.Policy == LocalVerificationModePolicy.Block
                    ? $"{LocalModeVerificationPolicy.BlockedStatus}: 本地执行模式下已配置阻止未隔离依赖扫描；请切换 Execution:Backend=Docker。"
                    : $"{LocalModeVerificationPolicy.SkippedReasonCode}: {LocalModeVerificationPolicy.UnverifiedNotice}";
                return DependencyScanReport.Unavailable(commit, reason);
            }

            var find = await session.RunAsync(new(SnapshotInventory.FindArgv(), Timeout: TimeSpan.FromMinutes(2)), ct).ConfigureAwait(false);
            if (!AuditText.CommandSucceeded(find))
                return DependencyScanReport.Unavailable(commit, $"Could not inventory the snapshot ({find.Status}).");

            var outcome = await ScanEcosystemsAsync(session, SnapshotInventory.Parse(find.Stdout), ct).ConfigureAwait(false);
            var usable = outcome.State is ArtifactAuditState.Pass or ArtifactAuditState.Fail or ArtifactAuditState.NeedsReview;
            return new DependencyScanReport(commit, usable, outcome.State, outcome.Message, outcome.Findings, outcome.BaselineDebt, outcome.Remediation);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Dependency scan of {Commit} failed", commit);
            return DependencyScanReport.Unavailable(commit, "The dependency scan failed: " + AuditText.Sanitize(ex.GetType().Name, 80));
        }
    }

    /// <summary>
    /// The same ecosystems the verification gate scans: npm and Python lockfiles against the host's OSV data when it is configured, NuGet
    /// for .NET projects. Without it a Node or Python project was analysed with no dependency facts at all, and a request to upgrade its
    /// vulnerable packages had nothing to go by (the container has no network to ask a registry).
    /// </summary>
    private async Task<AuditLayerOutcome> ScanEcosystemsAsync(IIsolatedExecutionSession session, SnapshotInventory inventory, CancellationToken ct)
    {
        Ecosystems.LockfileDependencyScanner[] lockfileScanners = osvAdvisories is null
            ? []
            : [new Ecosystems.LockfileDependencyScanner(new Ecosystems.NpmEcosystem(), options.Value, osvAdvisories, time),
               new Ecosystems.LockfileDependencyScanner(new Ecosystems.PythonEcosystem(), options.Value, osvAdvisories, time)];
        var applicable = lockfileScanners.Where(s => s.Ecosystem.ClaimedManifests(inventory).Count > 0).ToList();
        if (applicable.Count == 0)
            return await new NuGetDependencyScanner(options.Value, advisories, time).ScanAsync(session, inventory, ct).ConfigureAwait(false);

        var claimed = applicable.SelectMany(s => s.Ecosystem.ClaimedManifests(inventory)).ToHashSet(StringComparer.Ordinal);
        var rest = inventory with { UnsupportedManifests = inventory.UnsupportedManifests.Where(m => !claimed.Contains(m)).ToList() };
        var outcomes = new List<AuditLayerOutcome>();
        if (rest.ProjectFiles.Count > 0 || rest.LegacyPackagesConfigs.Count > 0)
            outcomes.Add(await new NuGetDependencyScanner(options.Value, advisories, time).ScanAsync(session, rest, ct).ConfigureAwait(false));
        foreach (var scanner in applicable)
            outcomes.Add(await scanner.ScanAsync(session, inventory, ct).ConfigureAwait(false));
        return ArtifactAuditService.CombineDependencyOutcomes(outcomes);
    }
}

/// <summary>
/// The text handed to the model when a project is analysed. The facts come from the host scan; the model is told to put them first,
/// not to rediscover them, and not to fold them into the fix tasks (existing debt is recorded separately at verification).
/// </summary>
public static class DependencyRiskBriefing
{
    private const int MaxRows = 15;

    private const string SdkInjectedNote = "SDK 隐式引入，不要为它写引用（见下文）";

    // Packages Aspire.AppHost.Sdk adds at restore, one per platform. They show up in the restore result as direct dependencies, but the
    // project never references them: writing a PackageVersion or a PackageReference for one is refused by MSBuild (seen live).
    private static readonly Regex SdkInjectedPackage = new(
        @"^Aspire\.(?:Dashboard\.Sdk|Hosting\.Orchestration)\.[A-Za-z0-9-]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private static bool IsSdkInjected(ArtifactDependencyRemediation item) => IsNuGet(item) && SdkInjectedPackage.IsMatch(item.Package);

    private const int MaxProjectsListed = 3;

    /// <summary>How many project files hold the package and the first few of them: the model must know where to change it, not only how many.</summary>
    private static string ProjectsCell(ArtifactDependencyRemediation item)
    {
        var listed = string.Join("、", item.Projects.Take(MaxProjectsListed).Select(static project => $"`{project}`"));
        return item.Projects.Count > MaxProjectsListed
            ? $"{item.Projects.Count} 个：{listed} 等"
            : item.Projects.Count == 0 ? "0" : $"{item.Projects.Count} 个：{listed}";
    }

    private static string Note(ArtifactDependencyRemediation item) =>
        string.Join("；", new[] { item.BaselineAccepted ? "已被基线接受" : null, IsSdkInjected(item) ? SdkInjectedNote : null }.Where(static n => n is not null));

    /// <summary>Null when there is nothing to report (clean project or no usable scan).</summary>
    public static string? Build(DependencyScanReport report, DependencyUpgradePlan? plan = null)
    {
        if (!report.Available || report.Remediation.Count == 0) return null;

        var hasOther = report.Remediation.Any(static item => !IsNuGet(item));
        var sb = new StringBuilder();
        sb.AppendLine("# 依赖风险（宿主离线公告库的预扫描结果，确定性事实）");
        sb.AppendLine($"扫描提交 `{report.Commit[..7]}`：{report.Remediation.Count} 个包版本存在已知公告，下表给出每个包的升级方向。");
        sb.AppendLine();
        sb.AppendLine(plan is null
            ? "| 包 | 当前版本 | 依赖方式 | 最高严重度 | 升级方向 | 涉及项目 | 备注 |"
            : "| 包 | 当前版本 | 依赖方式 | 最高严重度 | 升级方向 | 宿主解析的目标 | 涉及项目 | 备注 |");
        sb.AppendLine(plan is null ? "|---|---|---|---|---|---|---|" : "|---|---|---|---|---|---|---|---|");
        foreach (var item in report.Remediation.OrderByDescending(Rank).ThenBy(i => i.Package, StringComparer.OrdinalIgnoreCase).Take(MaxRows))
        {
            var note = Note(item);
            sb.AppendLine(plan is null
                ? $"| {Name(item)} | {item.CurrentVersion} | {(item.Transitive ? "传递依赖" : "直接依赖")} | {item.HighestSeverity} | {Target(item)} | {ProjectsCell(item)} | {note} |"
                : $"| {Name(item)} | {item.CurrentVersion} | {(item.Transitive ? "传递依赖" : "直接依赖")} | {item.HighestSeverity} | {Target(item)} | {(IsNuGet(item) ? Resolved(plan, item.Package) : "—")} | {ProjectsCell(item)} | {note} |");
        }

        if (report.Remediation.Count > MaxRows) sb.AppendLine($"（另有 {report.Remediation.Count - MaxRows} 项未列出）");
        sb.AppendLine();
        sb.AppendLine("对分析报告的要求：");
        sb.AppendLine("1. 在分析报告**最前面**单独给出「依赖安全升级建议」小节，按上表列出每个包的升级方向；传递依赖通过添加直接引用或固定版本来升级。版本号以上表为准，不要自行编造。改版本要落在表中「涉及项目」列出的项目文件里：直接依赖就改那个项目文件里已有的引用（不要换到别的项目文件里新加一条），升级方案和计划里写明要改哪些项目文件。");
        sb.AppendLine("2. 这些结果由宿主确定性扫描得出：不要再运行 `dotnet list package --vulnerable` 或联网查询（沙箱内无网络，只会得到 NU1900），也不要自行推翻或补充结论。");
        sb.AppendLine("3. 制定修复计划时，只处理用户要求的问题。不要把这些依赖升级塞进修复任务，除非用户明确要求；已存在的依赖债由验收流程单独记录，不会掩盖本次修复的验收结果。");
        sb.AppendLine("4. 绝不能通过 `NoWarn`、`NuGetAuditSuppress` 等方式抑制这些告警。");
        if (report.Remediation.Any(IsSdkInjected))
        {
            sb.AppendLine("关于备注为「SDK 隐式引入」的包：");
            sb.AppendLine("- 它们不是项目里的独立引用，而是 `Aspire.AppHost.Sdk` 在还原时按平台自动注入的；表里的「直接依赖」只是还原结果的分类。");
            sb.AppendLine("- 不要为了它们启用中央包管理：不要新建 `Directory.Packages.props`，也不要给它们写 `PackageVersion`（MSBuild 会拒绝：隐式包的版本只能写在 PackageReference 上，验收的离线还原因此失败）。");
            sb.AppendLine("- 普通的 `PackageReference`（带 `Version`）是允许的，但不是必须的：它们的版本随 `Aspire.Hosting.AppHost` 变化，升级 `Aspire.Hosting.AppHost` 通常就够了，是否到位以验收结果为准。");
        }

        if (hasOther)
        {
            sb.AppendLine("关于表中标注 (npm) / (PyPI) 的包：");
            sb.AppendLine("- 同样是宿主离线扫描的结果，不要运行 `npm audit`、`pip-audit` 或联网查询（沙箱内无网络）。包名不含括号里的标注。");
            sb.AppendLine("- 如果用户明确要求升级：目标版本取「升级方向」给出的最低修复版本，不要自行换成更高的版本——验收环境没有网络，只能安装宿主离线缓存里已有的版本，缓存里没有的版本会让验收失败。");
            sb.AppendLine("- npm：把 `package.json` 里的版本改为目标版本，再用 `npm install --package-lock-only --offline --ignore-scripts --no-audit --no-fund --cache /tmp/npm-cache` 重新生成 `package-lock.json`（不要手写锁文件）；这条命令失败说明离线缓存里没有目标版本，如实报告，不要改用别的版本硬凑。");
            sb.AppendLine("- PyPI：把 `requirements*.txt` 里该包的精确版本（`==`）改为目标版本即可。");
        }
        if (plan is not null)
            sb.AppendLine("5. 如果用户明确要求升级依赖：只能升级到上表中已由宿主取包的版本，版本号以「宿主解析的目标」为准；其它版本在验收沙箱（无网络）里无法还原，会让验收失败。需要用户批准的包，在用户批准前不要修改。");
        if (plan is not null)
            sb.AppendLine("6. 如果用户要求升级上表之外的包，或要求某个包升到表中目标版本以外的版本（例如把同一系列的其它包一起对齐），这些版本在验收环境（无网络）里无法还原，会让最终验收失败。不要直接照办：先告知用户这一点，由用户决定是缩小范围、放弃这部分，还是另行处理；在用户确认之前，计划和执行都只包含上表范围内的升级。");
        return sb.ToString().TrimEnd();
    }

    private static string Resolved(DependencyUpgradePlan plan, string package)
    {
        var candidate = plan.Candidates.FirstOrDefault(c => string.Equals(c.Package, package, StringComparison.OrdinalIgnoreCase));
        if (candidate is null) return "—";
        return candidate.Status switch
        {
            DependencyUpgradeStatus.NoCandidate => $"无可用版本：{candidate.Reason}",
            DependencyUpgradeStatus.NeedsApproval => $"{candidate.TargetVersion}（跨主版本，需要用户批准后宿主才会取包）",
            _ => plan.OverlayState switch
            {
                DependencyOverlayState.Ready => $"{candidate.TargetVersion}（宿主已取包，可直接使用）",
                DependencyOverlayState.Pending => $"{candidate.TargetVersion}（预取中，由宿主在后台完成并在验收时检查；修改依赖时不要等待它，直接按此版本修改）",
                DependencyOverlayState.Failed => $"{candidate.TargetVersion}（预取失败：验收沙箱可能无法还原）",
                DependencyOverlayState.NotConfigured => $"{candidate.TargetVersion}（未配置宿主补包：验收沙箱无法还原）",
                _ => candidate.TargetVersion ?? "—"
            }
        };
    }

    internal static bool IsNuGet(ArtifactDependencyRemediation item) =>
        string.IsNullOrEmpty(item.Ecosystem) || string.Equals(item.Ecosystem, "NuGet", StringComparison.OrdinalIgnoreCase);

    private static string Name(ArtifactDependencyRemediation item) => IsNuGet(item) ? item.Package : $"{item.Package} ({item.Ecosystem})";

    private static int Rank(ArtifactDependencyRemediation item) => item.HighestSeverity switch { "Critical" => 3, "High" => 2, "Moderate" => 1, _ => 0 };

    private static string Target(ArtifactDependencyRemediation item) => item.Action switch
    {
        "UpgradeToAtLeast" => $"升级到 ≥ {item.MinimumFixedVersion}",
        "UpgradeBeyond" => $"升级到 > {item.MustExceedVersion} 的版本",
        "NoKnownFix" => "暂无已知修复版本：替换或移除依赖",
        _ => item.Action
    };
}

/// <summary>Reads the HEAD commit of a repository. Exists so singletons do not capture the scoped <c>IGitService</c>.</summary>
public interface IRepositoryHeadReader
{
    Task<string?> GetHeadAsync(string repositoryRoot);
}

/// <summary>Singleton-safe: opens a short-lived scope per call, because the host registers <c>IGitService</c> as scoped and validates scopes.</summary>
public sealed class ScopedRepositoryHeadReader(IServiceScopeFactory scopes) : IRepositoryHeadReader
{
    public async Task<string?> GetHeadAsync(string repositoryRoot)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IGitService>().GetHeadHashAsync(repositoryRoot).ConfigureAwait(false);
    }
}

internal sealed class GitServiceHeadReader(IGitService git) : IRepositoryHeadReader
{
    public Task<string?> GetHeadAsync(string repositoryRoot) => git.GetHeadHashAsync(repositoryRoot);
}

public interface IDependencyRiskBriefingProvider
{
    /// <summary>The briefing for the repository's current commit, or null when there is none (no repository, clean, scan unavailable, still running).</summary>
    Task<string?> GetBriefingAsync(string repositoryRoot, string userId, string sessionId, CancellationToken ct);
}

/// <summary>
/// Scans each commit once (cached), waits only a bounded time so an analysis turn is never held hostage by Docker, and keeps a slow
/// scan running so the next turn finds it finished. Never throws.
/// </summary>
public sealed class DependencyRiskBriefingProvider : IDependencyRiskBriefingProvider
{
    private static readonly TimeSpan MaxScanLifetime = TimeSpan.FromMinutes(15);
    private readonly IDependencyRiskScanService _scan;
    private readonly IRepositoryHeadReader _heads;
    private readonly TimeSpan _budget;
    private readonly ILogger? _logger;
    private readonly IDependencyUpgradeService? _upgrades;
    private readonly ConcurrentDictionary<string, Task<BriefingData>> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record BriefingData(DependencyScanReport Report, DependencyUpgradePlan? Plan);

    public DependencyRiskBriefingProvider(IDependencyRiskScanService scan, IRepositoryHeadReader heads, TimeSpan budget, ILogger? logger = null,
        IDependencyUpgradeService? upgrades = null)
    {
        _scan = scan;
        _heads = heads;
        _budget = budget;
        _logger = logger;
        _upgrades = upgrades;
    }

    public DependencyRiskBriefingProvider(IDependencyRiskScanService scan, IGitService git, TimeSpan budget)
        : this(scan, new GitServiceHeadReader(git), budget)
    {
    }

    private async Task<DependencyUpgradePlan?> ResolveUpgradesAsync(DependencyScanReport report, string sessionId, CancellationToken ct)
    {
        // The host fetches upgrade targets from NuGet only; an npm or PyPI package name must never be looked up there.
        var nuGet = report with { Remediation = report.Remediation.Where(DependencyRiskBriefing.IsNuGet).ToList() };
        if (_upgrades is null || !report.Available || nuGet.Remediation.Count == 0) return null;
        try
        {
            return await _upgrades.ResolveAndPrepareAsync(nuGet, sessionId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Resolving the dependency upgrades failed; the briefing lists the vulnerabilities without targets");
            return null;
        }
    }

    public async Task<string?> GetBriefingAsync(string repositoryRoot, string userId, string sessionId, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(repositoryRoot)) return null;
            var head = await _heads.GetHeadAsync(repositoryRoot).ConfigureAwait(false);
            if (!AuditText.IsFullCommitId(head))
            {
                _logger?.LogDebug("No dependency briefing: {Root} has no readable HEAD commit", repositoryRoot);
                return null;
            }

            var key = repositoryRoot + "|" + head;
            var task = _cache.GetOrAdd(key, _ => Task.Run(async () =>
            {
                _logger?.LogInformation("Dependency scan started for {Commit} in {Root}", head, repositoryRoot);
                using var lifetime = new CancellationTokenSource(MaxScanLifetime);
                var report = await _scan.ScanCommitAsync(repositoryRoot, head!, userId, sessionId, "analysis", lifetime.Token).ConfigureAwait(false);
                return new BriefingData(report, await ResolveUpgradesAsync(report, sessionId, lifetime.Token).ConfigureAwait(false));
            }, CancellationToken.None));

            var finished = await Task.WhenAny(task, Task.Delay(_budget, ct)).ConfigureAwait(false);
            if (finished != task) return null; // still running: kept in the cache for the next turn

            if (task.IsFaulted || task.IsCanceled || !task.Result.Report.Available)
            {
                _cache.TryRemove(key, out _); // a failed or unavailable scan must not stick to this commit
                _logger?.LogInformation("No dependency briefing for {Commit}: the scan produced no evidence ({Reason})",
                    head, task.IsCompletedSuccessfully ? task.Result.Report.Message : task.Exception?.GetBaseException().Message ?? "canceled");
                return null;
            }

            // Built per turn: the upgrade plan's fetch keeps running, so a later turn reports it as ready.
            var briefing = DependencyRiskBriefing.Build(task.Result.Report, task.Result.Plan);
            if (briefing is not null)
                _logger?.LogInformation("Dependency briefing attached for {Commit}: {Count} package version(s), upgrade fetch {State}", head,
                    task.Result.Report.Remediation.Count, task.Result.Plan?.OverlayState.ToString() ?? "unplanned");
            return briefing;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The dependency briefing failed for {Root}", repositoryRoot);
            return null;
        }
    }
}
