[English](README.md) | 简体中文

# CodexFlow

在本机运行的 AI 编码助手宿主：对话 → 出计划 → 你批准 → 默认在本地按权限分级审批执行（也可选切换到隔离的 Docker 容器）→ 验收（Docker 模式下执行构建、测试、密钥扫描、依赖漏洞扫描、模型审查；本地模式默认跳过容器验收并明确标注）。
状态全部保存在一个 SQLite 文件里，只依赖 .NET 10 SDK 和一个模型接口（Docker 为可选项）。

**状态：早期。** 还没有 `codexflow` 命令行；现在通过本地宿主的 HTTP 接口使用，并用 `scripts/e2e/devsim` 里的场景脚本驱动和检查。

## 需要什么

- .NET 10 SDK
- 一个 OpenAI 兼容的聊天接口（`ChatCompletions` 模式）
- Docker *（可选）*：仅当配置 `Execution:Backend=Docker` 启用容器隔离执行和完整容器验收门禁时需要

## 运行

模型接口通过 `VllmAgent` 配置节或环境变量提供。**密钥只放环境变量，不要写进文件。**

```bash
export VllmAgent__ApiUrl='https://your-model-host/v1/{1}'
export VllmAgent__ApiMode='ChatCompletions'
export VllmAgent__Model='your-model'
export VllmAgent__ApiKey='...'
dotnet run --project CodexFlow.Local
```

- 只监听 `127.0.0.1`，端口由 `Local:Port` 配置（默认 5206）。
- 启动时生成访问令牌，写在数据库旁边的 `host.token`（默认 `.codexflow/host.token`）；请求不带令牌或令牌不对返回 401。
- 启动检查：模型设置齐全且接口能答；仅当 `Execution:Backend=Docker` 时才要求 Docker 守护进程可用。有问题会一次全报，然后以退出码 1 退出。可用 `Local:PreflightMode=Off`（或 `Report`）调整启动检查行为。
- 默认执行后端为 `Local`、默认权限模式为 `Ask`（每次调用代码执行工具都需审批；本地模式下默认跳过容器验证并在回执中标注“未在隔离环境中验证”）。已有自定义 `Isolation` 节的旧配置会在启动时自动迁移保留 `Execution:Backend=Docker`。
- 状态数据库默认 `.codexflow/state.db`，由 `Storage:SqlitePath` 配置，启动时自动应用迁移。

### 验收用的容器镜像（可选，仅 Docker 后端需要）

当配置 `Execution:Backend=Docker` 时，验收和用户代码在 `codexflow-verification` 镜像里执行，需要先构建：

```bash
docker build -t codexflow-verification:10.0.403-gitleaks8.30.1 docker/verification
```

`CodexFlow.Local/appsettings.json` 里固定了镜像名和密钥扫描配置的 SHA-256。`docker/verification/gitleaks.toml` 的字节不能改动（`.gitattributes` 已禁止换行符转换），改了就要同步改这个哈希。

### 漏洞公告数据

- NuGet 的公告由宿主自己刷新。
- npm 和 PyPI 的公告是离线准备的，超过 48 小时网关就拒绝使用（依赖层返回 Error，不会放行）。需要手工刷新，会从 osv.dev 下载几百 MB：

```bash
python scripts/e2e/prepare-osv-advisories.py --out ./osv-advisories
```

并把 `ArtifactAudit:OsvAdvisoryDirectory` 指向这个目录。

## 测试

```bash
dotnet test CodexFlow.Core.Tests
```

这里目前只有 `CodexFlow.Core.Tests`。网关、作业执行器和验收的集成测试尚未补充（需要先实现基于 SQLite 的测试宿主）。部分测试需要 Docker 或真实模型接口，没有时会跳过。

## 开发者模拟（devsim）

`scripts/e2e/devsim` 用自然语言和运行中的宿主对话（拉仓库、提问、要计划、批准、等作业、验收），并按结构化条件检查系统有没有做对，找的是系统的问题，不是模型措辞的差别。

```bash
python scripts/e2e/devsim list
python scripts/e2e/devsim run bugfix-python-pager --api http://127.0.0.1:5206 --token-file .codexflow/host.token
```

详见 `scripts/e2e/devsim/README.md`。其中 `faultproxy.py` 是个夹在宿主和模型之间的代理，用来让“重试”这类很少碰到的路径必然发生。

## 许可

[MPL-2.0](LICENSE)。版权：iwaitu。
