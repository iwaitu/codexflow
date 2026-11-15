# 发布 NuGet 包

五个库项目是 NuGet 包：`CodexFlow.Contracts`、`CodexFlow.Core`、`CodexFlow.Orchestration`、`CodexFlow.Gateway.Http`、`CodexFlow.Storage.Sqlite`。
它们**同一个版本号**一起发，版本号只写在一个地方：`Directory.Build.props` 的 `VersionPrefix`（预览版再加 `VersionSuffix`）。

## 发一个版本

1. 改 `Directory.Build.props` 里的版本号（例如 `0.1.2`，预览版把 `<VersionSuffix>preview.1</VersionSuffix>` 加回来）。
2. 提交，合并到 `main`（CI 要全部通过）。
3. 在那个提交上打标签并推送，标签必须等于 `v` 加版本号：

   ```bash
   git tag -a v0.1.2 -m "CodexFlow 0.1.2"
   git push origin v0.1.2
   ```

4. 打开仓库的 Actions，`publish` 工作流会先跑完所有检查，然后停在 `nuget` 环境等你批准。批准后推送到 nuget.org，等五个包都能在 nuget.org 查到，再创建 GitHub Release。

推送的是 `ci` 工作流里**那一次**打出来、检查过的包文件，不是重新编译的。

## 发布前自动检查什么

| 检查 | 失败说明什么 |
|---|---|
| 标签等于 `Directory.Build.props` 的版本号 | 改了一边忘了另一边 |
| 标签指向的提交在 `main` 上 | 在没合并的分支上打了标签 |
| 构建和 `CodexFlow.Core.Tests` 全部通过，通过数不低于下限 | 有测试失败，或测试悄悄没被跑到（下限在 `ci.yml` 的 `MIN_PASSED_TESTS`，删测试时在同一个提交里有意降低它） |
| 恰好五个包、版本相同、许可证 MPL-2.0、带 README 和库文件、互相依赖同一版本、来自这个提交 | 包不完整或元数据不对 |
| 一个只有这批包的新项目能还原并编译 | 包能打出来但装不上、用不了 |
| gitleaks 扫当前文件和全部历史，并确认扫描器真的能发现密钥 | 有密钥，或扫描器失效 |
| 所有包（含间接依赖）没有已知漏洞 | 依赖有漏洞 |

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
- “等五个包都能查到”这一步最多等 30 分钟；超时只代表 nuget.org 还在验证，推送本身已经成功，去 nuget.org 的包管理页看验证状态。
- 门禁里只跑 `CodexFlow.Core.Tests` 里不需要 Docker 的测试（约 1750 个）。需要 Docker 守护进程和验收镜像的“Docker 隔离”测试（`Category=DockerIsolation`，约 30 个）、宿主 `CodexFlow.Local`、以及需要模型接口的场景脚本（`scripts/e2e/devsim`）都不在发布门禁里，发布前请在本机跑一遍：`dotnet test CodexFlow.Core.Tests --filter "Category=DockerIsolation"`。
