// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

public sealed class IncompleteVerificationStopRuntimeHookTests
{
    [Theory]
    [InlineData("已修复忽略软删除标记的问题。现有 NU1902 漏洞告警不在本任务范围，未抑制，也没有使用 `NoWarn`。")]
    [InlineData("Fixed ignored soft-deleted rows. Existing NU1902 known moderate severity vulnerability remains outside this task; no suppression was used.")]
    [InlineData("NoWarn was not used for these existing vulnerability warnings.")]
    [InlineData("Used NoWarn for unrelated compiler warnings. The existing vulnerability is outside this task.")]
    public async Task OnBeforeStopAsync_DisclosedExistingWarningsAndNegatedSuppression_DoNotExpandTask(string report)
    {
        var hook = new IncompleteVerificationStopRuntimeHook();
        var context = CreateContext("dotnet restore succeeded, dotnet build succeeded, dotnet test passed.\n" + report);

        var result = await hook.OnBeforeStopAsync(context);

        Assert.False(result.Continue);
    }

    // Real final-report sentences from the cleanapp-bug live run 12: the model DENIED suppression, but the hook read
    // "抑制" + "漏洞" in the same sentence as an admission and forced continuation until the worker failed.
    [Theory]
    [InlineData("### “漏洞被抑制”判定不成立——已用工具实证推翻")]
    [InlineData("**结论：仓库中不存在任何形式的安全漏洞抑制。**")]
    [InlineData("审计显式强制开启仍只有 NU1900，说明**是网络取不到漏洞数据，而不是被抑制**。")]
    [InlineData("The vulnerability warnings were not suppressed; the audit simply could not reach the feed.")]
    [InlineData("There is no suppression of vulnerability warnings anywhere in the repository.")]
    [InlineData("漏洞告警并非被抑制，也未被忽略。")]
    public async Task OnBeforeStopAsync_DenialOfSuppression_IsNotTreatedAsSuppression(string report)
    {
        var hook = new IncompleteVerificationStopRuntimeHook();
        var context = CreateContext("dotnet restore succeeded, dotnet build succeeded, dotnet test passed.\n" + report);

        var result = await hook.OnBeforeStopAsync(context);

        Assert.False(result.Continue);
    }

    [Theory]
    [InlineData("为了通过审计，我在 Directory.Build.props 里抑制了这些漏洞告警。")]
    [InlineData("没有升级包，而是添加 NoWarn 忽略漏洞。")]
    [InlineData("Instead of upgrading I suppressed the vulnerability warnings with NuGetAuditSuppress.")]
    [InlineData("不存在可升级版本，所以我抑制了漏洞告警。")]
    public async Task OnBeforeStopAsync_AffirmativeSuppressionNearDenialWords_StillContinues(string report)
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(CreateContext(report));
        Assert.True(result.Continue);
        Assert.Equal("vulnerability_suppression_used", result.Reason);
    }

    [Fact]
    public async Task OnBeforeStopAsync_Feedback_DoesNotDemandAnImpossibleOfflineVulnerabilityScan()
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext("dotnet build failed with NU1605 package downgrade; I need to also bump the version."));

        Assert.True(result.Continue);
        Assert.Contains("NU1900", result.Message);
        Assert.Contains("如实披露", result.Message);
    }

    [Theory]
    [InlineData("error NU1902: package has a known moderate severity vulnerability")]
    [InlineData("dotnet restore still reports NU1903; vulnerable packages remain")]
    [InlineData("没有升级包，而是添加 NoWarn 忽略漏洞。")]
    public async Task OnBeforeStopAsync_ExplicitAuditFailureOrAffirmativeSuppression_StillContinues(string report)
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(CreateContext(report));
        Assert.True(result.Continue);
    }

    [Fact]
    public async Task OnBeforeStopAsync_ShouldContinue_WhenAssistantMentionsNugetDowngradeAndRemainingWork()
    {
        var hook = new IncompleteVerificationStopRuntimeHook();
        var context = CreateContext(
            "The restore shows a NU1605 error: Detected package downgrade from 10.0.8 to 10.0.2. " +
            "I need to also bump the version in CleanApp.csproj. Let me first read the file.");

        var result = await hook.OnBeforeStopAsync(context);

        Assert.True(result.Continue);
        Assert.True(result.AllowToolCallsOnNextRound);
        Assert.Equal(3, result.MaxContinuationAttempts);
        Assert.Equal(QueryTerminalDetailCodes.RecoveryExhausted, result.ExhaustionDetailCode);
        Assert.Contains("dotnet restore", result.Message);
        Assert.Contains("*.csproj", result.Message);
    }

    [Fact]
    public async Task OnBeforeStopAsync_ShouldNotContinue_WhenLatestVerificationSucceeded()
    {
        var hook = new IncompleteVerificationStopRuntimeHook();
        var context = CreateContext(
            "修复完成。dotnet restore succeeded, dotnet build succeeded, dotnet test passed. " +
            "Earlier NU1605 package downgrade errors are resolved.");

        var result = await hook.OnBeforeStopAsync(context);

        Assert.False(result.Continue);
    }

    [Fact]
    public async Task OnBeforeStopAsync_ShouldContinue_WhenVulnerabilityScanStillReportsPackages()
    {
        var hook = new IncompleteVerificationStopRuntimeHook();
        var context = CreateContext(
            "dotnet restore succeeded, dotnet build succeeded, dotnet test passed. " +
            "dotnet list package --vulnerable still reports NU1902 known moderate severity vulnerability " +
            "and has the following vulnerable packages.");

        var result = await hook.OnBeforeStopAsync(context);

        Assert.True(result.Continue);
        Assert.Equal("remaining_vulnerabilities", result.Reason);
        Assert.Contains("vulnerab", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnBeforeStopAsync_ShouldContinue_WhenVulnerabilitiesAreSuppressedInsteadOfFixed()
    {
        var hook = new IncompleteVerificationStopRuntimeHook();
        var context = CreateContext(
            """
            修复完成。新建 `Directory.Build.props`，通过 `NoWarn` / NuGet audit suppression 抑制 3 个已知漏洞。
            dotnet restore succeeded, dotnet build succeeded, dotnet test passed.
            dotnet list package --vulnerable --include-transitive now reports no vulnerable packages.
            """);

        var result = await hook.OnBeforeStopAsync(context);

        Assert.True(result.Continue);
        Assert.Equal("vulnerability_suppression_used", result.Reason);
        Assert.Contains("NoWarn", result.Message);
        Assert.Contains("NuGetAuditSuppress", result.Message);
        Assert.Contains("真实升级", result.Message);
    }

    [Fact]
    public async Task OnBeforeStopAsync_ShouldNotContinue_WhenToolsAreDisabled()
    {
        var hook = new IncompleteVerificationStopRuntimeHook();
        var context = CreateContext(
            "dotnet build failed and I need to fix the project file.",
            enableTools: false);

        var result = await hook.OnBeforeStopAsync(context);

        Assert.False(result.Continue);
    }

    [Fact]
    public async Task OnBeforeStopAsync_ShouldContinue_WhenFinalReportClaimsUnverifiedChangedFile()
    {
        var hook = new IncompleteVerificationStopRuntimeHook();
        var ledger = new QueryEvidenceLedger();
        ledger.ChangedFiles.Add(new ChangedFileEvidence
        {
            FilePath = "src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj",
            Status = "M",
            SourceToolName = "exec_cmd",
            SourceCommand = "git status --short"
        });
        ledger.Verifications.Add(new RuntimeVerificationEvidence
        {
            Kind = "dotnet_vulnerable_clean",
            Summary = "`dotnet list package --vulnerable --include-transitive` 未报告 vulnerable packages",
            SourceToolName = "exec_cmd",
            SourceCommand = "dotnet list package --vulnerable --include-transitive"
        });

        var context = CreateContext(
            """
            修复完成。

            修改的文件（2个）
            | 文件 | 变更 |
            |---|---|
            | `src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj` | upgraded packages |
            | `src/CleanApp.Core/CleanApp.Core.csproj` | upgraded MongoDB.Driver |

            dotnet restore succeeded, dotnet build succeeded, dotnet test passed.
            """,
            evidenceLedger: ledger);

        var result = await hook.OnBeforeStopAsync(context);

        Assert.True(result.Continue);
        Assert.False(result.AllowToolCallsOnNextRound);
        Assert.Equal(1, result.MaxContinuationAttempts);
        Assert.Equal("final_report_unverified_changed_files", result.Reason);
        Assert.Contains("src/CleanApp.Core/CleanApp.Core.csproj", result.Message);
        Assert.Contains("src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj", result.Message);
        Assert.Contains("修改的文件（1 个）", result.FinalTextOnExhaustion);
        Assert.Contains("src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj", result.FinalTextOnExhaustion);
        Assert.DoesNotContain("src/CleanApp.Core/CleanApp.Core.csproj", result.FinalTextOnExhaustion);
        Assert.Contains("`dotnet restore` 通过", result.FinalTextOnExhaustion);
        Assert.Contains("`dotnet build` 通过", result.FinalTextOnExhaustion);
        Assert.Contains("`dotnet test` 通过", result.FinalTextOnExhaustion);
        Assert.Contains("`dotnet list package --vulnerable --include-transitive` 未报告 vulnerable packages", result.FinalTextOnExhaustion);
    }

    [Fact]
    public async Task OnBeforeStopAsync_ShouldForceGitStatus_WhenWriteHappenedAfterStatusEvidence()
    {
        var hook = new IncompleteVerificationStopRuntimeHook();
        var ledger = new QueryEvidenceLedger();
        var statusAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        ledger.ChangedFiles.Add(new ChangedFileEvidence
        {
            FilePath = "src/CleanApp.Core/CleanApp.Core.csproj",
            Status = "M",
            SourceToolName = "exec_cmd",
            SourceCommand = "git status --short",
            ObservedAt = statusAt
        });
        ledger.ToolResults.Add(new ToolEvidence
        {
            ToolName = "delete_file",
            CallId = "delete-1",
            Success = true,
            Summary = "deleted _check_versions.py",
            ObservedAt = statusAt.AddMinutes(1)
        });

        var context = CreateContext(
            """
            已完成修复。

            修改的文件（1 个）：
            - `src/CleanApp.Core/CleanApp.Core.csproj`

            dotnet restore succeeded, dotnet build succeeded, dotnet test passed.
            """,
            evidenceLedger: ledger);

        var result = await hook.OnBeforeStopAsync(context);

        Assert.True(result.Continue);
        Assert.True(result.AllowToolCallsOnNextRound);
        Assert.Equal("exec_cmd", result.RequiredToolNameForNextRound);
        Assert.Equal("stale_git_status_after_write", result.Reason);
        Assert.Contains("git status --short", result.Message);
        Assert.Null(result.FinalTextOnExhaustion);
    }

    [Fact]
    public async Task OnBeforeStopAsync_ShouldContinue_WhenRepairTableClaimsUnverifiedChangedFile()
    {
        var hook = new IncompleteVerificationStopRuntimeHook();
        var ledger = new QueryEvidenceLedger();
        ledger.ChangedFiles.Add(new ChangedFileEvidence
        {
            FilePath = "src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj",
            Status = "M",
            SourceToolName = "exec_cmd",
            SourceCommand = "git status --short"
        });

        var context = CreateContext(
            """
            ## 修复完成

            ### 修复内容

            | 文件 | 修改 |
            |---|---|
            | `src/CleanApp.Core/CleanApp.Core.csproj` | `MongoDB.Driver` 3.6.0 -> 3.9.0 |
            | `src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj` | OpenTelemetry packages upgraded |

            ### 验证结果
            - `dotnet restore` 通过
            - `dotnet build` 通过
            - `dotnet test` 通过
            """,
            evidenceLedger: ledger);

        var result = await hook.OnBeforeStopAsync(context);

        Assert.True(result.Continue);
        Assert.Equal("final_report_unverified_changed_files", result.Reason);
        Assert.Contains("src/CleanApp.Core/CleanApp.Core.csproj", result.Message);
        Assert.Contains("修改的文件（1 个）", result.FinalTextOnExhaustion);
        Assert.Contains("src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj", result.FinalTextOnExhaustion);
        Assert.DoesNotContain("src/CleanApp.Core/CleanApp.Core.csproj", result.FinalTextOnExhaustion);
    }

    [Fact]
    public async Task OnBeforeStopAsync_ShouldNotContinue_WhenFinalReportMatchesGitStatusEvidence()
    {
        var hook = new IncompleteVerificationStopRuntimeHook();
        var ledger = new QueryEvidenceLedger();
        ledger.ChangedFiles.Add(new ChangedFileEvidence
        {
            FilePath = "src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj",
            Status = "M",
            SourceToolName = "exec_cmd",
            SourceCommand = "git status --short"
        });

        var context = CreateContext(
            """
            修复完成。

            修改的文件（1个）
            - `src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj`

            dotnet restore succeeded, dotnet build succeeded, dotnet test passed.
            """,
            evidenceLedger: ledger);

        var result = await hook.OnBeforeStopAsync(context);

        Assert.False(result.Continue);
    }

    // A coordinator chat answers questions; a worker repairs code. The completion signals are about work, so in the coordinator they only
    // count after the turn actually changed or ran something. Live dev-simulation: "装了会让构建失败" in an upgrade PROPOSAL (a warning from
    // the briefing rules) forced the model to stop proposing and start editing, which nobody asked for.
    [Theory]
    [InlineData("沙箱内无网络，清单里没有的版本无法还原，装了会让构建失败。你回一句我就改。")]
    [InlineData("如果升级后测试失败，我们再回退。我需要你确认 SharpCompress 是否升级。")]
    [InlineData("There are 8 vulnerable packages; the build failed on the last run in CI, I will fix it once you approve.")]
    public async Task OnBeforeStopAsync_AdvisoryAnswerInCoordinatorChat_IsNotForcedIntoRepairWork(string answer)
    {
        var hook = new IncompleteVerificationStopRuntimeHook();

        var advisory = await hook.OnBeforeStopAsync(CreateContext(answer, entryPoint: QueryLoopEntryPoint.GatewayMessageProcessor, executedTools: ["hs_read", "search_in_files", "ivilson_ls"]));
        var untouched = await hook.OnBeforeStopAsync(CreateContext(answer, entryPoint: QueryLoopEntryPoint.GatewayMessageProcessor));

        Assert.False(advisory.Continue);
        Assert.False(untouched.Continue);
    }

    [Theory]
    [InlineData("exec_cmd")]
    [InlineData("hs_write")]
    [InlineData("ivilson_smart_patch")]
    public async Task OnBeforeStopAsync_CoordinatorThatChangedOrRanSomething_IsHeldToItsOwnFailureReport(string tool)
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(CreateContext(
            "dotnet build failed with NU1605 package downgrade; I need to also bump the version.",
            entryPoint: QueryLoopEntryPoint.GatewayMessageProcessor, executedTools: [tool]));

        Assert.True(result.Continue);
    }

    [Theory]
    [InlineData(QueryLoopEntryPoint.ForgeWorker)]
    [InlineData(QueryLoopEntryPoint.DefaultCodexKernel)]
    public async Task OnBeforeStopAsync_WorkersAreStillHeldToTheSignals_EvenBeforeTheirFirstCommand(QueryLoopEntryPoint entryPoint)
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(CreateContext(
            "dotnet build failed with NU1605 package downgrade; I need to also bump the version.", entryPoint: entryPoint));

        Assert.True(result.Continue);
    }

    // Real final report of a worker whose task had fully succeeded (live developer conversation, TASK_DEP_003): "没有 NU1605" -- an explicit
    // statement that the error is ABSENT -- read as the error itself, and the Chinese success wording was not recognised as success evidence, so
    // the hook demanded more repair until the worker was failed with recovery_exhausted.
    private const string RealSuccessfulWorkerReport = """
三个包已固定到目标版本，restore、全量 build 和测试都通过；漏洞扫描没能完成，原因是沙箱访问不了 nuget.org。

**修改**

仓库没有中央包管理，所以我在 5 个 `.csproj` 里直接加了 `PackageReference`，没有其他改动：

| 项目 | 新增引用 |
|---|---|
| `src/CleanApp.Infrastructure` | `SQLitePCLRaw.lib.e_sqlite3` 2.1.12 |
| `src/CleanApp.Core` | `Snappier` 1.3.1、`SQLitePCLRaw.lib.e_sqlite3` 2.1.12 |
| `src/CleanApp` | `MessagePack` 2.5.303、`Snappier` 1.3.1、`SQLitePCLRaw.lib.e_sqlite3` 2.1.12 |
| `src/CleanApp.AppHost` | `MessagePack` 2.5.303 |
| `test/CleanAppCoreTests` | `Snappier` 1.3.1、`SQLitePCLRaw.lib.e_sqlite3` 2.1.12 |

**最新验证结果**
- **`dotnet restore`**：全部 8 个项目成功，没有 NU1605。
- **`dotnet build --no-incremental`**：成功，0 个错误，12 个警告。警告只有 NU1900 和一条原有的 CS0108。
- **测试**：4 个测试全部通过，0 个失败。
- **解析版本**：上一轮已逐项目核对，各项目解析到的 `SQLitePCLRaw.lib.e_sqlite3`、`Snappier` 和 `MessagePack` 都是目标版本。CleanApp 原来的 MessagePack 2.5.187 和 AppHost 原来的 2.5.192 两条路径都已覆盖。
- **抑制检查**：项目、props、targets 和 NuGet 配置里没有 `NoWarn`、`NuGetAudit*` 之类的抑制，也没有改 SharpCompress。
- **`git status --short` 为空的原因**：沙箱的基线 commit `codexflow guest baseline` 是在我的修改同步之后生成的，`git show HEAD:src/CleanApp/CleanApp.csproj` 里已经包含这些 `PackageReference`，所以相对基线没有差异。改动内容已落盘，我用 grep 和包解析结果确认过。

**环境限制和范围外的既有情况**
- **漏洞扫描**：`dotnet list package --vulnerable --include-transitive` 因 `api.nuget.org` 不可达（NU1900）失败，漏洞状态没有联网复核。我没有为此升级依赖或抑制告警。
- **CS0108**：`AppFile.cs` 里有一条原有警告，不在本任务范围内，没有处理。
- **SQLitePCLRaw 其余包**：`bundle_e_sqlite3`、`core`、`provider.e_sqlite3` 仍是 2.1.11，任务只要求固定 `lib.e_sqlite3`，所以没动。
- **SharpCompress**：Core 和 Tests 里仍是 0.30.1，按任务要求没有处理。
""";

    [Fact]
    public async Task OnBeforeStopAsync_RealChineseSuccessReport_MentioningAbsentErrorCodes_IsAccepted()
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext(RealSuccessfulWorkerReport, entryPoint: QueryLoopEntryPoint.ForgeWorker, executedTools: ["exec_cmd", "hs_write"]));

        Assert.False(result.Continue);
    }

    [Theory]
    [InlineData("restore 全部成功，没有 NU1605 降级警告。构建成功。4 个测试全部通过。")]
    [InlineData("dotnet restore succeeded, dotnet build succeeded, dotnet test passed. There is no NU1605 and no package downgrade.")]
    [InlineData("dotnet restore succeeded, dotnet build succeeded, dotnet test passed. NU1605 did not appear; build failed 0 times.")]
    public async Task OnBeforeStopAsync_NegatedFailureWording_WithSuccessEvidence_IsNotAFailure(string report)
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(CreateContext(report));

        Assert.False(result.Continue);
    }

    [Theory]
    [InlineData("dotnet restore failed with NU1605 package downgrade.")]
    [InlineData("构建失败：NU1605 仍然存在。")]
    [InlineData("还有 NU1605，没有修好。")]
    public async Task OnBeforeStopAsync_AffirmedFailure_IsStillCaught(string report)
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(CreateContext(report));

        Assert.True(result.Continue);
    }

    // Real final report of a READ-ONLY explore worker (live developer conversation): its advice mentioned NU1605 as a risk, the hook read it as an
    // unfinished repair and kept the worker going until recovery_exhausted -- a worker that cannot change anything.
    private const string RealExploreWorkerReport = """
Репозиторий не содержит ни одного из 7 целевых пакетов, ни явно, ни в виде заметных транзитивных зависимостей, которые можно проверить без restore. Данных restore/build/test у меня нет: `exec_cmd` и `run_tests` недоступны, а `obj/` и `project.assets.json` отсутствуют. `lsp_get_diagnostics` (Roslyn-baseline) дал 0 диагностик, но это не результат `dotnet build`.

## 1. Конфигурация, влияющая на разрешение версий

| Что искал | Результат |
|---|---|
| `Directory.Packages.props` | Нет. Центрального управления версиями нет. |
| `Directory.Build.props/targets`, любые `*.props`/`*.targets` | Нет. |
| `NuGet.config`, `*.config` | Нет. Используется nuget.org по умолчанию. |
| `global.json` | Нет. SDK не закреплён, CI ставит `10.0.x`. |
| `packages.lock.json` | Нет. |
| `TreatWarningsAsErrors`, `WarningsAsErrors`, `NoWarn`, `NuGetAudit`, `RestoreLockedMode`, `CentralPackage` | Нет ни в одном файле. |
| `obj/`, `project.assets.json` | Нет. В `.gitignore` стоит `obj/`, restore здесь не выполнялся. |

Выводы:
- Предупреждения не превращаются в ошибки.
- `NuGetAudit` остаётся включённым по умолчанию: при restore будут предупреждения NU1901–NU1904, но не ошибки.
- Версию можно закрепить только прямым `PackageReference` в нужном csproj.
- CI (`.github/workflows/ci.yml`): `dotnet restore CleanApp.slnx`, затем `dotnet build ... -c Release --no-restore`, затем `dotnet test ... -c Release --no-build --collect:"XPlat Code Coverage"`.

## 2. Стиль записи PackageReference

Во всех 8 csproj используется одинаковый стиль:
- **Элементный:** `<PackageReference Include="X" Version="Y" />`.
- **Отступы:** 2 пробела на уровень, 4 для элементов внутри `ItemGroup`.
- **Переводы строк:** CRLF, хотя hashline показывает `\n`. Это нужно учитывать при патче.
- **Кодировка:** UTF-8 с BOM у Domain, Infrastructure, AppHost, Contracts, Core и тестового проекта. Без BOM у `CleanApp.csproj` и `ServiceDefaults.csproj`.
- **Порядок:** не алфавитный, элементы идут в порядке добавления.
- **Пакетная `ItemGroup`:** отдельная группа для `PackageReference`, отдельная для `ProjectReference`.
- **Многострочная форма:** только там, где есть `PrivateAssets` и `IncludeAssets`, то есть `EntityFrameworkCore.Tools` и `xunit.runner.visualstudio`.
- **`Sdk`-элемент:** в `AppHost.csproj` есть `<Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0" />`.

Прямые `PackageReference` по проектам (все на net10.0):

| Проект | Пакеты |
|---|---|
| `src/CleanApp` (Web) | `Microsoft.AspNetCore.OpenApi` 10.0.2, `Microsoft.AspNetCore.Diagnostics.EntityFrameworkCore` 10.0.2, `Microsoft.EntityFrameworkCore.Sqlite` 10.0.2, `Swashbuckle.AspNetCore` 10.1.0, `StackExchange.Redis` 2.10.1, `Microsoft.AspNetCore.SignalR.StackExchangeRedis` 10.0.2, `Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.2, `Microsoft.AspNetCore.Identity.EntityFrameworkCore` 10.0.2, `Microsoft.EntityFrameworkCore.Tools` 10.0.2, `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.2, `Microsoft.VisualStudio.Azure.Containers.Tools.Targets` 1.23.0 |
| `CleanApp.ServiceDefaults` | `Microsoft.Extensions.Http.Resilience` 10.2.0, `Microsoft.Extensions.ServiceDiscovery` 10.2.0, `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.15.0, `OpenTelemetry.Extensions.Hosting` 1.15.0, `OpenTelemetry.Instrumentation.AspNetCore` 1.15.0, `OpenTelemetry.Instrumentation.Http` 1.15.0, `OpenTelemetry.Instrumentation.Runtime` 1.15.0 |
| `CleanApp.Infrastructure` | `Microsoft.AspNetCore.Identity.EntityFrameworkCore` 10.0.2, `Microsoft.EntityFrameworkCore.Tools` 10.0.2, `Microsoft.EntityFrameworkCore.Sqlite` 10.0.2 |
| `CleanApp.Domain` | `Microsoft.Extensions.Identity.Stores` 10.0.2, `Ulid` 1.4.1 |
| `CleanApp.Core` | `MongoDB.Driver` 3.6.0 |
| `CleanApp.AppHost` | `Aspire.Hosting.AppHost` 13.1.0 |
| `CleanApp.Contracts` | нет |
| `CleanAppCoreTests` | `coverlet.collector` 6.0.4, `EntityFrameworkCore.InMemory` 10.0.2, `Microsoft.NET.Test.Sdk` 18.0.1, `Moq` 4.20.72, `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.5 |

Граф проектов:
- Test → Core.
- Core → Contracts и Infrastructure.
- Infrastructure → Domain.
- AppHost → CleanApp.
- CleanApp → Core и ServiceDefaults.

## 3. Отображение 7 целевых пакетов на проекты

Прямых `PackageReference` на целевые пакеты нет, поэтому все они попадают в граф транзитивно. Без `project.assets.json` или `dotnet list package --include-transitive` я могу дать только оценку по структуре зависимостей. Она не подтверждена restore:

| Пакет | Ожидаемый источник (гипотеза) | Проекты по плану | Подтверждено? |
|---|---|---|---|
| `Microsoft.OpenApi` | `Swashbuckle.AspNetCore` 10.1.0 и `Microsoft.AspNetCore.OpenApi` 10.0.2 в `CleanApp`. `Program.cs` использует API версии 2 (`OpenApiSecuritySchemeReference`, лямбда в `AddSecurityRequirement`, `using Microsoft.OpenApi;`). | `CleanApp` (1) | Нет |
| `MessagePack` (2.5.187 / 2.5.192) | Вероятно, через Aspire или Redis-стек (`Aspire.Hosting.AppHost`, `Microsoft.Extensions.ServiceDiscovery`). | не определено | Нет |
| `Snappier` (3 проекта) | Вероятно, `MongoDB.Driver` 3.6.0 в Core; через проектные ссылки попадает в Core, Test и `CleanApp`. | Core, CleanAppCoreTests, CleanApp | Нет |
| `SQLitePCLRaw.lib.e_sqlite3` (4 проекта) | `Microsoft.EntityFrameworkCore.Sqlite` в Infrastructure, Core (через ссылку), CleanApp и Test. | Infrastructure, Core, CleanApp, CleanAppCoreTests | Нет |
| `OpenTelemetry.Api` (2 проекта) | `OpenTelemetry.Exporter.OpenTelemetryProtocol` / `Extensions.Hosting` в ServiceDefaults; через ссылку попадает в CleanApp. | ServiceDefaults, CleanApp | Нет |

Согласованность с требованием: цифры по SQLitePCLRaw (4), OpenTelemetry.Api (2) и Snappier (3) совпадают с требованием. `MessagePack` и точная картина по `Microsoft.OpenApi` по структуре зависимостей не определяются.

## 4. Базовые данные restore/build/test

Получить их не удалось:
- Нет `obj/`, нет логов, нет `project.assets.json`.
- Нет доступа к `exec_cmd`, поэтому и `dotnet list package` недоступен.
- Анонс про SharpCompress в репозитории не найден. `SharpCompress` не упоминается ни в одном файле. Скорее всего, он транзитивный через `MongoDB.Driver` или другой пакет, и это увидит только restore/audit.
- Тесты: в тестовом проекте один файл, `FileServiceTests.cs`, он использует Moq и EF InMemory. Ничего внешнего ему не нужно, но результат прогона мне неизвестен.

## 5. Что делать дальше

1. **Получить базу командами** (в Forge/Verify, где есть `exec_cmd`):
   - `dotnet restore CleanApp.slnx`
   - `dotnet list CleanApp.slnx package --include-transitive`
   - `dotnet list CleanApp.slnx package --vulnerable --include-transitive`
   - `dotnet build CleanApp.slnx -c Release --no-restore`
   - `dotnet test CleanApp.slnx -c Release --no-build`
   
   Результат нужно сохранить как эталон и пересчитать таблицу из раздела 3.
2. **Для TASK_NUGET_002** фиксировать версии прямыми элементами `PackageReference` в том же стиле, что и существующие.
   - Они добавляются в `ItemGroup` с пакетами, в конец, с сохранением CRLF и BOM.
   - Предположительно: `Microsoft.OpenApi` в `CleanApp.csproj`; `Snappier` в `CleanApp.Core.csproj`, `CleanAppCoreTests.csproj` и `CleanApp.csproj`; `SQLitePCLRaw.lib.e_sqlite3` в `Infrastructure`, `Core`, `CleanApp` и тестовом проекте; `OpenTelemetry.Api` в `ServiceDefaults` и `CleanApp`; `MessagePack` в проектах, которые покажет `list package`.
   - Список считается окончательным только после шага 1.
3. **Риск:** прямая ссылка на транзитивный пакет с версией выше, чем у родителя, может дать NU1608 или NU1605, если родитель ограничивает диапазон версий (например, у `MessagePack` и `OpenTelemetry.*` на уровне `Aspire` и `ServiceDefaults`).
4. **Риск:** `AppHost.csproj` использует `Aspire.AppHost.Sdk` 9.5.0 вместе с `Aspire.Hosting.AppHost` 13.1.0. Это несоответствие уже есть, на базе его нужно отдельно проверить.
""";

    [Theory]
    [InlineData(QueryLoopEntryPoint.ExploreWorker)]
    [InlineData(QueryLoopEntryPoint.PlanWorker)]
    [InlineData(QueryLoopEntryPoint.VerifyWorker)]
    public async Task OnBeforeStopAsync_ReadOnlyWorkers_AreNeverPushedIntoRepairWork(QueryLoopEntryPoint entryPoint)
    {
        var hook = new IncompleteVerificationStopRuntimeHook();

        var real = await hook.OnBeforeStopAsync(CreateContext(RealExploreWorkerReport, entryPoint: entryPoint, executedTools: ["ivilson_read", "search_in_files"]));
        var blunt = await hook.OnBeforeStopAsync(CreateContext("dotnet build failed with NU1605 package downgrade; I need to also bump the version.", entryPoint: entryPoint));

        Assert.False(real.Continue);
        Assert.False(blunt.Continue);
    }

    // The production runtime now hands the hook verification evidence taken from the real tool results; prose about success is no longer the
    // only way to show that the work was verified.
    private const string ClaimsSuppression = "dotnet restore succeeded, dotnet build succeeded, dotnet test passed.\n为了通过审计，我在 Directory.Build.props 里抑制了这些漏洞告警。";

    [Fact]
    public async Task OnBeforeStopAsync_AClaimedSuppression_TheChangedProjectFilesDoNotShow_IsNarrative()
    {
        var hook = new IncompleteVerificationStopRuntimeHook((_, _) => Task.FromResult(SuppressionEvidence.NoneAdded));
        Assert.False((await hook.OnBeforeStopAsync(CreateContext(ClaimsSuppression))).Continue);
    }

    [Fact]
    public async Task OnBeforeStopAsync_ASuppressionReallyAdded_IsCaughtWhateverTheReportSays()
    {
        var hook = new IncompleteVerificationStopRuntimeHook((_, _) => Task.FromResult(SuppressionEvidence.Added));
        var result = await hook.OnBeforeStopAsync(CreateContext("dotnet restore succeeded, dotnet build succeeded, dotnet test passed.\n已完成，全部通过。"));
        Assert.True(result.Continue);
        Assert.Equal("vulnerability_suppression_used", result.Reason);
    }

    [Fact]
    public async Task OnBeforeStopAsync_UnreadableWorkTree_FallsBackToTheWording()
    {
        var hook = new IncompleteVerificationStopRuntimeHook((_, _) => Task.FromResult(SuppressionEvidence.Unknown));
        Assert.True((await hook.OnBeforeStopAsync(CreateContext(ClaimsSuppression))).Continue);
    }

    [Fact]
    public async Task OnBeforeStopAsync_ReadOnlyWorkers_NeverTriggerTheLookup()
    {
        var called = false;
        var hook = new IncompleteVerificationStopRuntimeHook((_, _) => { called = true; return Task.FromResult(SuppressionEvidence.Added); });
        await hook.OnBeforeStopAsync(CreateContext(ClaimsSuppression, entryPoint: QueryLoopEntryPoint.ExploreWorker));
        Assert.False(called);
    }

    private static QueryEvidenceLedger LedgerWith(params string[] kinds)
    {
        var ledger = new QueryEvidenceLedger();
        foreach (var kind in kinds)
        {
            ledger.Verifications.Add(new RuntimeVerificationEvidence { Kind = kind, Summary = kind + " passed" });
        }

        return ledger;
    }

    private const string FailureWordingWithoutProseEvidence = "旧的 dotnet build failed 日志已经清理；我需要再确认一下版本号。";

    [Fact]
    public async Task OnBeforeStopAsync_PassedBuildAndTestInTheLedger_CountAsSuccessEvidence_WithoutAnyProse()
    {
        var hook = new IncompleteVerificationStopRuntimeHook();

        var without = await hook.OnBeforeStopAsync(CreateContext(FailureWordingWithoutProseEvidence));
        var with = await hook.OnBeforeStopAsync(CreateContext(FailureWordingWithoutProseEvidence, evidenceLedger: LedgerWith("dotnet_build", "dotnet_test")));

        Assert.True(without.Continue);
        Assert.False(with.Continue);
    }

    [Theory]
    [InlineData("dotnet_build")]
    [InlineData("dotnet_test")]
    [InlineData("dotnet_restore")]
    public async Task OnBeforeStopAsync_AnIncompleteLedger_IsNotEnough(string only)
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext(FailureWordingWithoutProseEvidence, evidenceLedger: LedgerWith(only)));

        Assert.True(result.Continue);
    }

    // #6: the verification commands that really ran decide first; the worker's wording is only a fallback when no command spoke.
    private static QueryEvidenceLedger LedgerWithFailed(string failedKind, params string[] passedKinds)
    {
        var ledger = LedgerWith(passedKinds);
        ledger.FailedVerifications.Add(new RuntimeVerificationEvidence { Kind = failedKind, Summary = failedKind + " failed" });
        return ledger;
    }

    [Theory]
    [InlineData("已完成全部修改。")]
    [InlineData("All done. dotnet restore passed, dotnet build passed, dotnet test passed.")]
    public async Task OnBeforeStopAsync_AFailedVerificationInTheLedger_ContinuesWhateverTheWorkerWrites(string report)
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext(report, evidenceLedger: LedgerWithFailed("dotnet_test", "dotnet_build")));

        Assert.True(result.Continue);
        Assert.Equal("failed_verification", result.Reason);
    }

    [Theory]
    [InlineData(QueryLoopEntryPoint.ExploreWorker)]
    [InlineData(QueryLoopEntryPoint.PlanWorker)]
    [InlineData(QueryLoopEntryPoint.VerifyWorker)]
    public async Task OnBeforeStopAsync_AFailedVerificationInTheLedger_DoesNotPushAReadOnlyWorker(QueryLoopEntryPoint entryPoint)
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext("已完成。", entryPoint: entryPoint, evidenceLedger: LedgerWithFailed("dotnet_build")));

        Assert.False(result.Continue);
    }

    [Theory]
    [InlineData("早先 dotnet build failed，已经修复。", "dotnet_build", true)]
    [InlineData("早先 NU1605 package downgrade 已经解决。", "dotnet_build", true)]
    [InlineData("早先 tests failed，已经修复。", "dotnet_test", true)]
    [InlineData("早先 dotnet build failed，已经修复。", "dotnet_test", false)]
    [InlineData("早先 tests failed，已经修复。", "dotnet_build", false)]
    public async Task OnBeforeStopAsync_AFailureMentionIsRetiredByThePassOfItsOwnKind(string report, string passedKind, bool retired)
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext(report, evidenceLedger: LedgerWith(passedKind)));

        Assert.Equal(!retired, result.Continue);
    }

    // A restore/build that fails because the offline source lacks a package (NuGet NU1102 and its relatives) is an environment limit the worker
    // cannot repair. It must not be treated as unfinished work: the host's own restore preflight judges that dependency change, and only gets to
    // when the worker is allowed to stop. Before, the hook kept the worker going until its continuations ran out and the job failed generically.
    private static QueryEvidenceLedger LedgerWithEnvironmentFailure(string kind)
    {
        var ledger = new QueryEvidenceLedger();
        ledger.FailedVerifications.Add(new RuntimeVerificationEvidence { Kind = kind, Summary = kind + " failed", EnvironmentLimit = true });
        return ledger;
    }

    [Theory]
    [InlineData("dotnet_restore")]
    [InlineData("dotnet_build")]
    public async Task OnBeforeStopAsync_AnEnvironmentLimitedFailure_IsReportedNotRepaired(string kind)
    {
        var report = "restore、build 失败：离线源缺少 OpenTelemetry 扩展包的 1.15.3（NU1102）。需要提供这些包或可访问的包源。";

        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext(report, evidenceLedger: LedgerWithEnvironmentFailure(kind)));

        Assert.False(result.Continue);
    }

    [Fact]
    public async Task OnBeforeStopAsync_AFailureThatIsNotAnEnvironmentLimit_StillContinues()
    {
        var ledger = new QueryEvidenceLedger();
        ledger.FailedVerifications.Add(new RuntimeVerificationEvidence { Kind = "dotnet_build", Summary = "dotnet_build failed" });

        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext("构建失败：CS1002 语法错误，我还没有修好。", evidenceLedger: ledger));

        Assert.True(result.Continue);
    }

    [Fact]
    public async Task OnBeforeStopAsync_AnEnvironmentLimit_DoesNotHideARealFailureOfAnotherKind()
    {
        var ledger = LedgerWithEnvironmentFailure("dotnet_restore");
        ledger.FailedVerifications.Add(new RuntimeVerificationEvidence { Kind = "dotnet_test", Summary = "dotnet_test failed" });

        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(CreateContext("完成。", evidenceLedger: ledger));

        Assert.True(result.Continue);
    }

    // A weaker model called the write tool with dryRun=true, saw "validation succeeded", and stopped saying the files were not written. A preview
    // changes nothing. The ledger knows (from the call's arguments) that no real write followed, so a forge worker is sent back to apply it.
    private static QueryEvidenceLedger LedgerWithUnappliedPreview(string tool = "hs_write")
    {
        var ledger = new QueryEvidenceLedger();
        ledger.UnappliedPreviews.Add(new UnappliedPreviewEvidence { ToolName = tool });
        return ledger;
    }

    [Fact]
    public async Task OnBeforeStopAsync_AForgeWorkerThatOnlyPreviewedItsEdit_IsSentBackToApplyIt()
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext("已验证补丁，但未写入工作区。", entryPoint: QueryLoopEntryPoint.ForgeWorker, evidenceLedger: LedgerWithUnappliedPreview()));

        Assert.True(result.Continue);
        Assert.Equal("unapplied_preview", result.Reason);
        Assert.Equal("hs_write", result.RequiredToolNameForNextRound);
        Assert.True(result.AllowToolCallsOnNextRound);
        Assert.Contains("dryRun", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(QueryLoopEntryPoint.ExploreWorker)]
    [InlineData(QueryLoopEntryPoint.VerifyWorker)]
    [InlineData(QueryLoopEntryPoint.GatewayMessageProcessor)]
    public async Task OnBeforeStopAsync_APreviewIsOnlyAProblemForAForgeWorker(QueryLoopEntryPoint entryPoint)
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext("预演完成。", entryPoint: entryPoint, evidenceLedger: LedgerWithUnappliedPreview()));

        Assert.NotEqual("unapplied_preview", result.Reason);
    }

    // A forge worker that changed something and is about to stop is asked, once, to state its own status through the structured tool: a task it
    // reports as blocked must not be counted as completed. Decided by the ledger and the successful tool names, never by the final prose.
    [Fact]
    public async Task OnBeforeStopAsync_AForgeWorkerThatWroteButNeverStatedItsStatus_IsAskedOnce()
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext("T1 部分完成，仍有阻碍。", successfulTools: ["hs_read", "hs_write"]));

        Assert.True(result.Continue);
        Assert.Equal("worker_status_report_missing", result.Reason);
        Assert.Equal(WorkerStatusTool.Name, result.RequiredToolNameForNextRound);
        Assert.True(result.AllowToolCallsOnNextRound);
    }

    [Fact]
    public async Task OnBeforeStopAsync_AWorkerThatAlreadyStatedItsStatus_IsNotAskedAgain()
    {
        var ledger = new QueryEvidenceLedger { WorkerReport = new WorkerStatusReport(WorkerStatusTool.Completed, null, []) };

        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext("完成。", evidenceLedger: ledger, successfulTools: ["hs_write", WorkerStatusTool.Name]));

        Assert.NotEqual("worker_status_report_missing", result.Reason);
    }

    [Fact]
    public async Task OnBeforeStopAsync_AWorkerThatReportedItselfBlocked_IsNotSentBackToRepair()
    {
        // Seen live: a worker reported blocked (four packages missing from the offline source), was pushed to continue twice, re-ran the same
        // restore and reported blocked twice more. The job fails as blocked either way; the extra rounds only cost two minutes.
        const string report = "Detected package downgrade NU1605，dotnet build failed，修复尚未完成。";
        var hook = new IncompleteVerificationStopRuntimeHook();
        var blocked = new QueryEvidenceLedger
        {
            WorkerReport = new WorkerStatusReport(WorkerStatusTool.Blocked, "离线源缺少四个包，还原失败。", ["完成离线还原"])
        };

        var withoutReport = await hook.OnBeforeStopAsync(CreateContext(report, successfulTools: ["hs_write"], continuationCount: 1));
        var afterBlockedReport = await hook.OnBeforeStopAsync(
            CreateContext(report, evidenceLedger: blocked, successfulTools: ["hs_write", WorkerStatusTool.Name], continuationCount: 1));

        Assert.True(withoutReport.Continue);
        Assert.False(afterBlockedReport.Continue);
    }

    [Fact]
    public async Task OnBeforeStopAsync_AWorkerThatReportedCompleted_IsStillHeldToFailedVerification()
    {
        const string report = "Detected package downgrade NU1605，dotnet build failed，修复尚未完成。";
        var completed = new QueryEvidenceLedger { WorkerReport = new WorkerStatusReport(WorkerStatusTool.Completed, null, []) };

        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext(report, evidenceLedger: completed, successfulTools: ["hs_write", WorkerStatusTool.Name], continuationCount: 1));

        Assert.True(result.Continue);
    }

    [Fact]
    public async Task OnBeforeStopAsync_TheStatusIsAskedOnlyOnTheFirstStop_SoItCanNeverExhaustTheBudget()
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext("完成。", successfulTools: ["hs_write"], continuationCount: 1));

        Assert.NotEqual("worker_status_report_missing", result.Reason);
    }

    [Fact]
    public async Task OnBeforeStopAsync_AWorkerThatChangedNothing_IsNotAskedForAStatus()
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext("分析完成。", successfulTools: ["hs_read"]));

        Assert.NotEqual("worker_status_report_missing", result.Reason);
    }

    [Theory]
    [InlineData(QueryLoopEntryPoint.ExploreWorker)]
    [InlineData(QueryLoopEntryPoint.PlanWorker)]
    [InlineData(QueryLoopEntryPoint.VerifyWorker)]
    [InlineData(QueryLoopEntryPoint.GatewayMessageProcessor)]
    public async Task OnBeforeStopAsync_OnlyAForgeWorkerIsAskedForAStatus(QueryLoopEntryPoint entryPoint)
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext("完成。", entryPoint: entryPoint, successfulTools: ["hs_write"]));

        Assert.NotEqual("worker_status_report_missing", result.Reason);
    }

    [Fact]
    public async Task OnBeforeStopAsync_OtherContinuationReasonsComeBeforeTheStatusRequest()
    {
        var result = await new IncompleteVerificationStopRuntimeHook().OnBeforeStopAsync(
            CreateContext("已验证补丁，但未写入工作区。", evidenceLedger: LedgerWithUnappliedPreview(), successfulTools: ["hs_write"]));

        Assert.Equal("unapplied_preview", result.Reason);
    }

    private static BeforeStopContext CreateContext(
        string lastAssistantMessage,
        bool enableTools = true,
        QueryEvidenceLedger? evidenceLedger = null,
        QueryLoopEntryPoint entryPoint = QueryLoopEntryPoint.ForgeWorker,
        string[]? executedTools = null,
        string[]? successfulTools = null,
        int continuationCount = 0)
        => new()
        {
            ExecutedToolNames = new HashSet<string>(executedTools ?? [], StringComparer.OrdinalIgnoreCase),
            SuccessfulToolNames = new HashSet<string>(successfulTools ?? [], StringComparer.OrdinalIgnoreCase),
            Request = new QueryRuntimeRequest
            {
                SessionId = "incomplete-verification-hook",
                EntryPoint = entryPoint,
                InitialMessages = [new ChatMessage(ChatRole.User, "fix cleanapp")],
                EnableTools = enableTools
            },
            Round = 0,
            LastAssistantMessage = lastAssistantMessage,
            ThinkingText = string.Empty,
            StopHookActive = continuationCount > 0,
            ContinuationCount = continuationCount,
            EvidenceLedger = evidenceLedger ?? new QueryEvidenceLedger()
        };
}
