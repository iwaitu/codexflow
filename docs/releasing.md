# 发布 NuGet 包

六个库项目作为 NuGet 包发布。`CodexFlow.Contracts`、`CodexFlow.Core`、`CodexFlow.Orchestration`、`CodexFlow.Gateway.Http`、`CodexFlow.Storage.Sqlite` 和 `CodexFlow.Storage.EntityFramework`。
它们**同一个版本号**一起发，版本号只写在一个地方：`Directory.Build.props` 的 `VersionPrefix`（预览版再加 `VersionSuffix`）。

## 发一个版本

1. 改 `Directory.Build.props` 里的版本号（例如 `0.1.2`，预览版把 `<VersionSuffix>preview.1</VersionSuffix>` 加回来）。
2. 提交，合并到 `main`（CI 要全部通过）。
3. 在那个提交上打标签并推送，标签必须等于 `v` 加版本号：

   ```bash
   git tag -a v0.1.2 -m "CodexFlow 0.1.2"
   git push origin v0.1.2
   ```

4. 打开仓库的 Actions，`publish` 工作流会先并行跑完后端检查（`ci.yml`）和跨平台桌面客户端测试/打包（`client.yml`，内置自包含 `CodexFlow.Local` 并统一注入 `Directory.Build.props` 版本号）。若配置了仓库变量 `NUGET_USER`，会在 `nuget` 环境批准后推送六个 NuGet 包；随后 `release` Job 自动将所有平台桌面客户端安装包（`.exe`/`.zip`/`.AppImage`/`.tar.gz`/`.dmg`）、`SHA256SUMS.txt` 与 `.nupkg` 一并创建到 GitHub Release 页面供用户下载（未配置 `NUGET_USER` 时仅跳过 NuGet 推送，仍正常发布 GitHub Release 客户端安装包）。

推送的是 `ci` 工作流里**那一次**打出来、检查过的包文件，不是重新编译的。

## 发布前自动检查什么

| 检查 | 失败说明什么 |
|---|---|
| 标签等于 `Directory.Build.props` 的版本号 | 改了一边忘了另一边 |
| 标签指向的提交在 `main` 上 | 在没合并的分支上打了标签 |
| 构建和 `CodexFlow.Core.Tests` 全部通过，通过数不低于下限 | 有测试失败，或测试悄悄没被跑到（下限在 `ci.yml` 的 `MIN_PASSED_TESTS`，删测试时在同一个提交里有意降低它） |
| 恰好六个包、版本相同、许可证 MPL-2.0、带 README 和库文件、互相依赖同一版本、来自这个提交 | 包不完整或元数据不对 |
| 一个只有这批包的新项目能还原、运行并导出网关契约 | 包能打出来但装不上、用不了 |
| gitleaks 扫当前文件和全部历史，并确认扫描器真的能发现密钥 | 有密钥，或扫描器失效 |
| 所有包（含间接依赖）没有已知漏洞 | 依赖有漏洞 |

## 桌面 Native AOT 后端

在装有 `global.json` 指定的 .NET SDK、Visual Studio C++ x64 构建工具、Git、curl 和 tar 的 Windows 上，从仓库执行严格发布。

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\ci\publish-windows-native-aot.ps1 -OutputDirectory .\artifacts\local-win-x64
```

桌面 CI 为 Windows x64、macOS arm64 和 Linux x64 严格发布 Native AOT 后端。发布目录包含原生 `CodexFlow.Local`（Windows 上为 `CodexFlow.Local.exe`）、SQLite 原生库、`code-analysis/worker` 下的自包含托管 Roslyn Worker 和 `third-party-licenses`。macOS 和 Linux 的发布任务还会验证包内后端启动及 Roslyn Worker 诊断。

首次构建会下载锁定提交的 ExcelDataReader 和 Json.NET 官方源码归档，验证 SHA-256 后应用目录内的最小补丁。以后可复用校验通过的中间产物。补丁程序集仅用于桌面 Native AOT 主进程的链接。托管 Roslyn Worker 和 NuGet 消费者使用官方依赖。上游发布修复版后，更新锁定版本并通过同样的严格发布、网关契约、桌面启动和工具回归，再撤掉对应补丁。[补丁和版本锁](../CodexFlow.Local/NativeAotDependencies/)

若发行出现回归，暂停当前版本分发，恢复上一份已验证安装包，必要时恢复数据库的升级前备份。

## SQLite 迁移

Local 启动时使用 `SqliteSchemaMigrator`，在 SQLite 写事务内读取 `__EFMigrationsHistory`，执行对应的嵌入 SQL 并记录版本。现有 `state.db` 沿用原有结构，升级前停止旧实例并备份数据库。回滚时用旧版程序启动备份库。

历史 EF 迁移和 SQL 生成工具保留在独立设计时项目。新增迁移时，在 `SqliteSchemaMigrator` 登记版本，将对应版本的 SQL 加入 `SchemaMigrations`，并验证旧版库升级。

```powershell
dotnet ef migrations list --project CodexFlow.Storage.Sqlite.Design --startup-project CodexFlow.Storage.Sqlite.Design
dotnet ef migrations script --project CodexFlow.Storage.Sqlite.Design --startup-project CodexFlow.Storage.Sqlite.Design
```

## 托管 EF 宿主迁移

平台宿主添加同版本的 `CodexFlow.Storage.EntityFramework` 引用后重新编译。`IJobDbContext` 和 `JobModel` 的命名空间沿用 `CodexFlow.Orchestration.Jobs`。保持现有 `AddDbContext<PlatformDbContext>`、`JobModel.Apply` 和 PostgreSQL 模型配置，然后调用 `services.AddCodexFlowEfStorage<PlatformDbContext>()`。平台仍提供 `IJobEventLog`、`IKeyValueTextStore`、Outbox 序号和已有的提供方专用租约实现，先注册自定义实现可保留其优先级。

## HTTP 网关宿主迁移

升级同版本的 `CodexFlow.Gateway.Http` 引用后，从 `CodexFlow.Gateway.Http` 命名空间调用 `services.AddCodexFlowGateway()`。在认证和授权中间件后调用 `app.UseCodexFlowGatewayBindingErrors()`，并用 `app.MapCodexFlowGateway()` 注册路由。宿主继续提供 `Bearer` 认证和现有的 `ActiveUser`、控制面授权策略。OpenAPI 继续使用现有 `AddCodexFlowGatewayOpenApi()` 和 `MapCodexFlowGatewayOpenApi()` 注册。

## 第一次要做的设置（只做一次）

发布不在仓库里存任何 NuGet 密钥：nuget.org 用 GitHub 的 OIDC 令牌换一个短期密钥（NuGet 的“受信任发布”）。

1. **nuget.org**：登录后进入你的用户名菜单 → *Trusted Publishing* → 新建策略：
   - Repository Owner：`iwaitu`
   - Repository：`codexflow`
   - Workflow File：`publish.yml`
   - Environment：`nuget`
2. **GitHub 仓库设置**：
   - *Settings → Environments → New environment*，名字 `nuget`。勾选 *Required reviewers* 并选你自己：这样每次发布都要你点批准。可以再把 *Deployment branches and tags* 限制为标签 `v*`。
   - *Settings → Secrets and variables → Actions → Variables*：新建仓库变量 `NUGET_USER`，值是 nuget.org 的用户名（个人资料名，不是邮箱）。
3. **建议**：给 `main` 加分支保护，要求 `ci / ci-required` 通过；给标签 `v*` 加规则，只允许管理员创建。

## 要记住的

- nuget.org 上发出去的版本**不能删除**，只能“取消列出”（unlist）。发错了就修好后发下一个版本号。
- 推送用了 `--skip-duplicate`：同一个版本重跑不会报错，也不会覆盖。
- “等六个包都能查到”这一步最多等 30 分钟；超时只代表 nuget.org 还在验证，推送本身已经成功，去 nuget.org 的包管理页看验证状态。
- 门禁里只跑 `CodexFlow.Core.Tests` 里不需要 Docker 的测试（约 1750 个）。需要 Docker 守护进程和验收镜像的“Docker 隔离”测试（`Category=DockerIsolation`，约 30 个）、宿主 `CodexFlow.Local`、以及需要模型接口的场景脚本（`scripts/e2e/devsim`）都不在发布门禁里，发布前请在本机跑一遍：`dotnet test CodexFlow.Core.Tests --filter "Category=DockerIsolation"`。
