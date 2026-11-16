# 开发计划：本地执行为默认 + 桌面客户端

- 状态：草案 v3（第 2 轮评审后修订，待复审）
- 日期：2026-11-16
- 范围：单机版（`CodexFlow.Local` 及其依赖的五个库）
- 已定决策：
  - 容器隔离改为**可选**，默认**本地执行**。
  - 本地执行按**权限模式分级审批**（类似 Claude Code / Codex），默认"每次询问"。
  - 客户端采用 **Electron + React + TypeScript**。
  - 客户端**托管后端进程**（以子进程拉起 `CodexFlow.Local`），同时支持连接已运行的实例。
  - 本地模式下，产物验证和安全扫描不可用时，**跳过并标注**"未经隔离验证"，不阻止作业完成（`ArtifactVerification:LocalModePolicy = Skip`）。

---

## 0. 现状摘要（决定计划形状的事实）

### 执行
- 所有执行模型代码的工具都经过 `ToolIsolationGate.TryAcquire`（`CodexFlow.Core/Execution/ToolIsolationGate.cs:28`），包括 `run_command`、`exec_code`、`run_tests` 和 skill 脚本。唯一的后端是 `DockerIsolatedExecutionService`，没有本机回退。
- 注册是无条件的：`OrchestrationServiceCollectionExtensions.cs:219-220` 注册了 `AddCodexIsolatedExecution`，同时注册了 `IsolationReaperHostedService`。
- `HostPreflight` 会执行 `docker info`，失败时 `CodexFlow.Local/Program.cs:77-89` 直接以 exit 1 退出。也就是说，**没有 Docker，单机版今天根本启动不了**。唯一的绕过办法是现有的布尔开关 `Local:SkipPreflight`。
- `TrustedExecutionContext.Validate()`（`ExecutionContracts.cs:205-207`）只接受 `Network = None`。而 `ToolIsolationGate` 在把请求交给后端**之前**就会调用它（`ToolIsolationGate.cs:52`）。
- `GuestTarAnalyzer` 要求 tar 条目以 `workspace/` 开头（`GuestTarAnalyzer.cs:80`），这是 Docker 的路径约定。
- 安全不变量依赖"先在副本中运行，再导出"：
  - 容器里的 `/workspace` 打成 tar，交给 `GuestTarAnalyzer` 做范围、链接、受保护路径和配额检查。
  - 然后由 `ScopedExportPublisher` 在主机侧重新算哈希，按 CAS 写回工作区。
  - 只有 `RunCommandTool` 会导出改动。
  - 这些保证之所以完整，**前提是容器内进程看不到主机文件系统**。
- `RunCommandTool.AllowedCommands`（`RunCommandTool.cs:103-109`）包含 `dotnet`、`npm`、`npx`、`node`、`python`、`pip`、`make`、`cargo`、`go`、`gradle`、`mvn` 等，这些程序都能执行任意代码。
- 验证与审计链路深度绑定容器：
  - `ForgeArtifactVerificationService`、`DependencyRiskScanService`、`ArtifactAuditService`（含 gitleaks、NuGet 扫描）都使用 `PinToBaseCommit` 快照。
  - 它们硬编码了 guest 路径（`/workspace`、`/tmp/home`、`/restore-cache`、`/npm-cache`、`/pip-wheels` 等）。
  - 它们大量使用 POSIX 命令（`sh -c`、`cat`、`find`、`grep`、`cp -a`、`sha256sum`），而主机是 Windows。
- 可复用的主机侧进程执行器：
  - `ProcessDockerCli`（`Execution/DockerCli.cs:33-186`）：基于 argv，支持 stdin，按行回调，有输出上限和超时，能杀进程树。这是最合适的基础。
  - `GitService.RunGitCommandAsync`。

### 对外接口
- 只绑定 `127.0.0.1:{Local:Port}`（默认 5206），没有 HTTPS。
- 认证：每次启动重新生成 `.codexflow/host.token`，请求带 Bearer。
  - token 以明文落盘。只有非 Windows 平台会把文件权限设为 600（`LocalAccess.cs:44-57`）。
  - 同一用户下运行的任何进程都能读到这个文件。
- 不存在的东西：CORS、OpenAPI、静态文件、SignalR、WebSocket。
- 唯一的推送通道是 SSE（`GET api/gateway/sse`）：
  - 每个会话有 4096 条事件的重放缓冲（`SessionChannel.cs:29`），支持 `Last-Event-ID` 加 `epoch` 重连。
  - 每个订阅者另有一个 4096 条的有界队列，满了就 `DropOldest`（`SessionChannel.cs:411-430`）。客户端慢的时候，事件可能被丢掉。
  - 心跳间隔 15 秒。
  - 事件包括 `content`、`thinking_*`、`tool_call`、`task_status`、`worker_*`、`done`、`error`、`replay_gap` 等。
- 发起对话：`POST api/gateway/chat` 返回 202；同一会话正在执行时返回 409。计划审批通过 `StructuredAction` 提交（需要 id、version、hash）。
- `scripts/e2e/devsim` 用 token 文件（`--token-file`）驱动完整的作业流程，其中会触发执行类工具。
- 对客户端来说的关键缺口：
  - 没有设置 API（`IModelSettingsService` 已注册但未暴露）。
  - 不能打开本地任意文件夹作为工作区（只能用 `workspaces/local/<guid>`，或走 `GitRepoUrl` 加 `Workspace:AllowedLocalRepositoryRoots` 白名单）。
  - 不能取消进行中的回合。
  - 没有读取计划或待审批状态的接口，事件里也不带 version 和 hash。
  - `tool_call` 事件丢掉了参数。
  - 没有文件树和 diff 接口。
  - 不能重命名或删除会话、对话。
  - 事件和 DTO 没有类型化契约。
  - 首次运行没配模型时，preflight 会直接退出。

### 模型客户端（QRE 接入）
- `CodexFlow.Core/Runtime/QreV2BackedQueryRuntimeEngine.cs` 中的 `CodexFlowV2ModelClient` 手工实现了 MEAI ↔ QRE Protocol 转换及 finish reason 校验（与 QRE 的 `MeaiRuntimeModelClient` 重复），但同时承载了记忆注入（经 `DefaultLLMExecutor`）、`DynamicContextProvider`、`ForgeProgressGuard`、`UnexposedToolCallGuard`、必需工具关思考、单次尝试超时及空响应流式重试。
- `CodexFlow.Core.csproj` 当前仅引用了 `CodexFlow.QueryRuntime.Engine`（`0.26.2`），未引用包含 `MeaiRuntimeModelClient` 的 `CodexFlow.QueryRuntime.Models` 包；`OrchestrationServiceCollectionExtensions` 的 `HttpClient` 管道也未挂载 `QreModelHttpErrorCaptureHandler`。

### CI 约束
- `check_packages.py` 要求打出的包**恰好**是那五个库。新项目必须设置 `IsPackable=false`。
- CI 全部跑在 ubuntu 上，并对整个 slnx 做漏洞审计。
- 测试数门槛是 `MIN_PASSED_TESTS=1700`。

---

## 1. 总体路线

| 阶段 | 内容 | 依赖 | 可并行 |
|---|---|---|---|
| PM | 模型客户端收敛（外层装饰器 + 内层 `MeaiRuntimeModelClient`） | 核对 `CodexFlow.QueryRuntime.Models` 包版本 | 可与 P1 并行；建议在 P2 超时暂停与 P3 取消回合联调前合入 |
| P1 | 执行后端可选化 + 本地执行后端 + 本地模式下的验证降级（P1-4 第一期） | — | 与 PM、P2 的契约设计并行 |
| P2 | 权限模式、交互式审批、控制面凭据 | P1-1 | — |
| P3 | 面向客户端的后端 API 补齐 | P2 的审批事件契约 | 与 P1-3、P1-4 并行 |
| P4 | Electron 客户端 MVP | P3 的契约（可先用 mock） | 脚手架可与 P1 同时开始 |
| P5 | 客户端增强与发布 | P4 | — |

原则：
1. **Docker 模式行为不变**。只要配置为 Docker 且 Docker 不可用，就拒绝执行，绝不悄悄回退到本地。这条不变量保留，并补上测试。
2. **如实区分两种模式的安全保证**（见下表）。本地模式仍在副本中运行，并通过范围导出和 CAS 写回。但这只约束"经导出通道写回的改动"，**不约束进程的直接副作用**：本地进程以用户身份运行，可以用绝对路径访问主机上的任何位置。
3. **项目级配置（`codexflow.yml`）不能切换执行后端，也不能放宽权限模式**。`ProjectAllowedSections` 保持只有 `Project`，防止克隆来的仓库自行降低隔离级别。
4. **本地模式真正的安全边界是"批准之前不启动"**。一个命令一旦获准运行，就拥有用户的全部权限。权限模式要守住的是：未经批准的执行不会发生，而且模型不能自己给自己批准。

### 不变量对照表

| 不变量 | Docker | Local |
|---|---|---|
| 进程看不到主机文件系统和真实工作区 | ✔ | ✘（以用户权限运行） |
| 无网络 | ✔ | ✘（`Network=Host`，写入审计） |
| CPU、内存、进程数上限 | ✔ | ✘（第一期只有超时和输出上限，见 P1-6） |
| 经导出通道写回时：范围、链接、受保护路径、配额检查和 CAS | ✔ | ✔（复用同一套代码） |
| 任务文件范围限制 | ✔ 强制 | 只约束导出通道。直接写入不受约束，UI 和审计里标注"范围未强制" |
| 未经批准不启动（`Ask` 模式） | ✔ | ✔ |
| 模型不能自批，也不能放宽权限模式 | ✔ | 托管模式下 ✔。独立运行模式默认不提供控制面，此时 Local 下无法审批；只有用户显式启用不安全兼容模式时才提供，此时 ✘（见 P2 控制面凭据） |
| 后端不可用时不回退到另一个后端 | ✔ | ✔ |

---

## PM. 模型客户端收敛（外层装饰器 + 内层 `MeaiRuntimeModelClient`）（M）

> 状态：**方向待定（三方架构评审已有共识，待用户最终拍板；记忆注入位置未定）**。
> 结论：不能用 `MeaiRuntimeModelClient` 直接替换 `CodexFlowV2ModelClient`，应采用**外层 CodexFlow 装饰器 + 内层 `MeaiRuntimeModelClient`** 的组合方案；QRE 本身不需要先改。

### 职责划分
- **外层装饰器（`CodexFlowRuntimeModelClient : IRuntimeModelAttemptClient`）**：
  - 保留 `DynamicContextProvider` 注入消息与恢复提示，以及对必需工具的动态改写。
  - 保留 `ForgeProgressGuard`（基于 `TurnEvidence`）与 `UnexposedToolCallGuard` 两个护栏。
  - 指定必需工具时强制 `ThinkingEnabled = false`（在 `optionsFactory` 中处理）。
  - 保留单次尝试级超时（`CancelAfter`）与连接失败包装为可重试 `ProviderTransport` 错误。
  - 保留空回答时的**流式**重试一次，并累计/保留首次 `RuntimeUsageEvent`。
  - 向内层透传 `RuntimeModelAttemptContext`。
- **内层 `MeaiRuntimeModelClient`**：
  - 负责 MEAI ↔ QRE Protocol 协议转换、结束原因校验（`conflicting_provider_finish_reason`、`provider_content_after_finish`、`missing_provider_finish_reason`）、`DeclaredOnlyTools` 缓存前缀保持、`ToolStrict`、`Step ResponseFormat` 映射与细分错误分类（`ClassifyProviderErrors`）。
- **待决分支：记忆注入位置**（二选一，待拍板）：
  - **方案 A（推荐 / 多数意见）：`ExecutorChatClientBridge`** — 新建桥接器将流式/非流式请求转交 `ILLMExecutor.StreamAsync` / `ExecuteAsync`，继续走 `DefaultLLMExecutor` 注入记忆并复用 `ReloadableChatClient → ConfiguredSamplingChatClient → VllmChatClient` 管道（桥接器不拥有共享客户端）。
  - **方案 B：装饰器内拼装** — 抽离 `DefaultLLMExecutor.EnrichMessagesAsync` 的记忆拼装逻辑至装饰器前置步骤，直接注入 DI 单例 `IChatClient`。

### 动手前必须处理的 7 项关键风险
1. **包版本缺口**：`CodexFlow.Core` 当前引用 `CodexFlow.QueryRuntime.Engine 0.26.2`，本机缓存中 `CodexFlow.QueryRuntime.Models` 最高仅 `0.25.7`（本地 QRE 源码为 `0.26.1`）。实施前必须先确认 NuGet 包源存在与 `Engine 0.26.2` 匹配的 `Models` 版本。
2. **Dispose 所有权**：`MeaiRuntimeModelClient.Dispose()` 会释放其持有的 `IChatClient`（全局单例 `ReloadableChatClient` 管道）。严禁对每轮新建的 `MeaiRuntimeModelClient` 调用 `Dispose()` / `using`，必须明确底层 `IChatClient` 由 DI 容器拥有。
3. **`ChatOptions` 每次重新克隆**：`ConfiguredSamplingChatClient` 读取 `ChatOptions.AdditionalProperties` 中的采样字段后会将其删除。`optionsFactory` 每次必须调用 `QreV2AdapterUtilities.CloneChatOptions` 生成新实例，保留 `VllmChatOptions` 类型与独立的 `AdditionalProperties`。
4. **Usage 少算**：`MeaiRuntimeModelClient` 在 `StreamingWithNonStreamingFallbackOnEmpty` 模式下遇到空回答会改发非流式请求并丢弃首次缓冲的 Usage。第一阶段 `MeaiStreamingMode` 保持 `Streaming`，仍由外层装饰器做流式重试，避免双重重试与 Usage 丢失。
5. **超时与主动取消区分**：外层 `CancelAfter` 超时必须与调用方主动取消（含 P3-5 取消回合）严格区分，否则"超时可重试（`ProviderTransport`）"语义会丢失；同时需兼容 P2 审批挂起期间暂停超时计时。
6. **动态必需工具写回位置**：`DynamicContextProvider` 改写必需工具时，需先校验其属于当前 Step，再写入传给 Meai 的请求 `Parameters.RequiredToolName`（只改 `ChatOptions` 会被 Meai 覆盖），并同步在 `optionsFactory` 中关闭思考。
7. **错误分类生效前提**：必须显式开启 `ClassifyProviderErrors`，并在 `OrchestrationServiceCollectionExtensions` 的 `HttpClient` 管道挂载 `QreModelHttpErrorCaptureHandler`，否则 429 `RetryAfter` 与认证错误分类不会生效。

### 验收标准
- **新旧差分测试**：`CodexFlowV2ModelClient` 与 `CodexFlowRuntimeModelClient` 跑同一组输入对比结果，覆盖：记忆与恢复提示注入顺序、动态必需工具与 `ThinkingEnabled=false` 联动、两个护栏、无工具/`DeclaredOnlyTools`、超时与主动取消区分、429/认证错误分类、空流重试与首次 `Usage` 保留、异常结束原因顺序、并发 `ChatOptions` 隔离、多轮调用后单例 `IChatClient` 存活。
- **真实服务验证**：对照真实模型服务的实际出站载荷（工具声明元数据、strict schema）、模型配置热更新，以及工具只执行一次（不因声明对象重放）。

---

## P1. 执行后端可选化 + 本地执行后端

### P1-1 配置、路由与执行前置条件（S）
- **硬前置**：改造 `TrustedExecutionContext.Validate()`。
  - 新增 `ExecutionNetworkMode.Host`。
  - 网络模式的校验从 gate 挪到后端：每个后端声明自己支持的网络模式，Docker 仍然只接受 `None`。
  - 不做这一步，Local 模式下任何工具都会在 `ToolIsolationGate.cs:52` 被拒绝。
- 新增 `Execution` 配置节：
  ```json
  "Execution": {
    "Backend": "Local",          // Local（默认）| Docker
    "PermissionMode": "Ask"      // 见 P2
  }
  ```
- 新增 `ExecutionBackendRouter : IIsolatedExecutionService`：
  - 在每次 `OpenSessionAsync` 时通过 `IOptionsMonitor<ExecutionOptions>` 选择后端。
  - 切换后端后，新开的会话立即生效，不需要重启。
  - 已经打开的会话继续用原来的后端。
- `AddCodexIsolatedExecution` 的改动：
  - 两种后端都注册。`DockerIsolatedExecutionService` 的构造函数不接触 Docker，所以没有 Docker 时注册它、做 DI 校验都是安全的。
  - `IsolationReaperHostedService` 在 Local 模式下跳过 reaper 循环。新增 `LocalStagingReaper`，负责清理本地后端的 staging 目录。
- `HostPreflight` 的改动：
  - Docker 检查只在 `Backend=Docker` 时执行。
  - 新增 `Local:PreflightMode = Fail | Report | Off`，**替代** `Local:SkipPreflight`。旧开关继续兼容：`SkipPreflight=true` 等同于 `Off`，同时打一条弃用警告。
  - 客户端托管时使用 `Report`：模型未配置或不可达时不退出，状态通过 API 上报（见 P3-1）。
- **默认值的切换时机**：
  - P1 交付时，Local 后端还只是可选项，默认值仍是 Docker。
  - 默认值切换为 Local + `Ask`，前提是同时满足三项：P2 合入、P1-4 第一期合入、升级迁移规则就绪（见第 7 节）。
- **升级迁移**：
  - 用户级 `~/.codexflow/appsettings.json` 里已经有非默认的 `Isolation` 配置、但没有 `Execution` 节时，升级后视为显式选择 Docker，写回 `Execution:Backend=Docker` 并记录日志。
  - 全新安装才使用新的默认值。
  - 回退方法：设置 `Execution:Backend=Docker`。
- 新增 `ExecutionSettingsService`，仿照 `ModelSettingsService`，原子地读写 `~/.codexflow/appsettings.json` 里的 `Execution` 节。
  - **运行期只能收紧，不能放宽**：从文件热加载时只接受更严格的权限模式。要放宽，只能通过控制面 API（见 P2）。
  - 启动时按文件内容生效（这是用户自己的配置）。
- **验收标准**：
  - 没装 Docker、配置为 `Backend=Local` 时，机器能启动，`run_command` 能在本地执行。
  - 设为 Docker 而 Docker 不可用时，返回 `IsolationNotConfigured` 或 `Unavailable`，不会转去本地执行。
  - 项目 yml 里写 `Execution:` 不生效（补一个测试）。
  - 升级迁移的测试：已有 Isolation 配置时，升级后仍是 Docker；全新安装时为 Local。
  - 运行期把文件里的权限模式改宽，不生效；改严，立即生效。

### P1-2 `LocalExecutionService`（L）
它实现 `IIsolatedExecutionService` / `IIsolatedExecutionSession`，语义如下。

| 选项 | 本地语义 |
|---|---|
| `StageWorkspace=true` | 把工作区复制到 `{StagingRoot}/local/{session}/workspace`，复用快照基线的哈希。大仓库的优化见下文"性能"。 |
| `StageWorkspace=false` | 创建空目录，导出被拒绝（与 Docker 一致）。 |
| `PinToBaseCommit` | 复用 `PinnedCommitSnapshotBuilder`，把快照物化到本地 staging 目录，导出被拒绝。 |
| `Inputs` | 复制到 `{session}/inputs/<GuestSubPath>`，不导出。 |
| `GuestGit` | 在 `{session}/git` 建立独立仓库，设置 `GIT_DIR` 和 `GIT_WORK_TREE`（与 Docker 的 `/tmp/cfgit` 同构）。 |
| `RunAsync` | 在 `ProcessDockerCli` 的基础上抽出通用的 `HostProcessRunner`，基于 argv、不经过 shell，启动策略见 P1-3。初始 cwd 限定在 staging 根目录内（规范化后校验）。**这只决定进程从哪里启动，不限制进程能访问哪里。** 超时、输出上限、杀进程树与 Docker 一致。 |
| `ExportScopedChangesAsync` | 把 staging 目录打成 tar 流，交给现有的 `GuestTarAnalyzer` 和 `ScopedExportPublisher`。只复用代码，不复制粘贴。**子任务 a**：把 `GuestTarAnalyzer` 写死的 `workspace/` 前缀改成参数（Docker 传 `workspace/`，Local 传空或自定义根），`GuestTarAnalyzerTests` 两种前缀都要覆盖。**子任务 b**：新写的"目录 → tar"构建器负责 Windows 规范化，见下文。 |
| `AbortAsync` / `DisposeAsync` | 杀进程树，删除 staging 目录。Windows 上进程被杀后，文件句柄不会立刻释放，所以删除要带退避重试。仍然失败时，把目录重命名为待删目录，交给 `LocalStagingReaper` 稍后清理。 |

- **Windows 目录 → tar 的规范化**（子任务 b）：在构建 tar 时就处理，不依赖 analyzer 兜底。
  - 不跟随、并拒绝所有重解析点（符号链接、目录联接 junction、挂载点）。
  - 拒绝 NTFS 备用数据流（`a.txt:stream`）和保留设备名（`CON`、`NUL`、`COM1` 等，含带扩展名的形式）。
  - 拒绝 8.3 短名别名（如 `GIT~1`），以及以点或空格结尾的名字。
  - 同一目录下出现仅大小写不同的两个条目时，拒绝。
  - 受保护路径（如 `.git`）比较时不区分大小写。
  - 规范化之后，仍然交给 `GuestTarAnalyzer` 做同样的检查。
- **环境变量**：
  - 以白名单方式继承主机环境（`PATH`、`SystemRoot`、`TEMP` 以及语言工具链需要的变量）。
  - 剥离 `GIT_*`、`*_API_KEY`、`VllmAgent__*`，以及所有 CodexFlow 凭据相关的变量。
  - `HOME`/`USERPROFILE` 默认指向会话私有目录，避免污染用户的全局配置。
  - 同时显式保留工具链缓存位置，避免重复下载：`NUGET_PACKAGES` 指向用户的全局包目录，`npm_config_cache` 和 `PIP_CACHE_DIR` 指向用户现有的缓存。
  - 以上行为可以配置。
- **网络**：本地模式无法禁网。
  - 契约里新增 `NetworkMode.Host`，仅本地后端接受。
  - 实际使用的网络模式写进审计记录、工具结果和审批卡片。
- **资源限制**：
  - 第一期只做超时、输出上限和进程树清理。
  - Windows Job Object（内存和进程数上限）、Linux `setrlimit`/cgroup 列为可选的后续项（P1-6）。
- **孤儿清理**：沿用 owner lease 机制，清理已退出实例留下的 staging 目录。
- **性能**：
  - 复制前按 `.gitignore` 和默认排除表（`bin/`、`obj/`、`node_modules/`、`.git/` 等）裁剪。
  - 单会话复制量设上限，超限时拒绝并给出原因。
  - 后续可选：用 `git worktree` 或硬链接加速。
- **验收标准**：
  - 测试分为两类：
    - **导出通道安全测试**：两个后端共用，参数化执行。覆盖越界条目、符号链接、硬链接、受保护路径、配额、CAS 冲突，在 Windows 和 Linux 上都要跑。
    - **容器隔离测试**：只适用于 Docker。保留原有用例，不移植到 Local。
  - Windows CI 上新增攻击用例，下列每一种都必须被拒绝，且不产生工作区外或受保护路径上的写入：
    - 大小写变体的受保护路径（`.GIT/config`）；
    - 8.3 短名；
    - 备用数据流；
    - junction；
    - 保留设备名；
    - 仅大小写不同的两个条目；
    - 以点或空格结尾的名字。
  - 新增"本地模式能力如实性"测试：命令用绝对路径写 staging 目录之外的位置时，写入确实会发生。这个测试的目的是证明文档描述与事实一致，不是去拦截写入。审计里要记录 `filesystem: unrestricted`。

### P1-3 消除 guest 路径与 POSIX 依赖、Windows 启动策略（M）
- 在会话上暴露 `ExecutionLayout`（`WorkspaceRoot`、`HomeDir`、`TempDir`、`CacheMounts`）。消费方不再硬编码 `/workspace`、`/tmp/...`。
- **Windows 启动策略**（`HostProcessRunner`）：
  - 原生 `.exe`：用 `CreateProcess` 和 argv 直接启动。
  - `.cmd`/`.bat` 入口**不直接执行**，也不笼统地交给 `cmd /c` 包装。需要的入口走一张显式的**解释器适配表**：
    - `npm` → `node <node 安装目录>/node_modules/npm/bin/npm-cli.js`
    - `npx` → `node …/npm/bin/npx-cli.js`
    - 其余入口按需加入适配表。
  - 不在适配表里的 `.cmd`/`.bat`，一律拒绝并说明原因。
  - 最终启动的可执行文件、解释器链和参数，都写进审批请求（P2）和审计，保证"批准的命令"就是"实际启动的命令"。
- 改造范围：
  - `RunCommandTool`：可执行文件按后端解析。`OfflineToolchainPreparation` 在本地模式下跳过或改用托管代码。环境说明文字按后端生成。
  - `ExecCodeTool`：语言镜像改为"语言运行时"的抽象。本地模式用主机上的 `python`/`node`/`java`；找不到时给出明确错误，不要静默失败。Java 用托管代码写 `Main.java`，不再用 `sh -c cat`。
  - `SkillTool`：`.sh` 脚本在 Windows 上需要 `bash`（Git Bash 或 WSL）。找不到时拒绝执行并说明原因。
  - `RunTestsTool`：只改镜像选择逻辑。
- 提示词：
  - `CodexPrompts.cs:117-123`、`ForgeOfflinePackagesBriefing.cs:33-36` 中关于容器的描述改为按后端生成。本地模式下要如实说明：没有网络隔离，没有文件系统隔离。
  - `RunCommandEnvironmentNoteTests`、`CodexPromptCompositionTests` 同步更新。
- **验收标准**：
  - 在 Windows 主机的本地模式下，下列端到端用例全部通过：`run_command`（dotnet、npm 版本查询、npm 实际运行脚本、python）、`exec_code`（python、node）、`run_tests`。
  - Windows 参数测试：参数里有空格路径、引号和命令元字符（`&`、`|`、`^`、`%VAR%`）时，原样传给目标程序，不被解释。
  - 审计记录的最终命令与审批请求里的命令逐字节一致。

### P1-4 验证与审计链路在本地模式下的策略（M，第一期是默认值切换的硬前置）
1. **第一期**：
   - 在本地模式下，所有验证和审计入口在打开会话之前就按后端判断，统一由 `LocalModeVerificationPolicy` 处理，**不会启动任何容器专用命令**。涉及的入口：
     - `ForgeArtifactVerificationService`
     - `DependencyRiskScanService`
     - `ArtifactAuditService`（含 gitleaks、NuGet 扫描）
   - 这一步把 `Unavailable(requires_docker_backend)` 转成持久化的结果：
     - `Skip`：验证结果记为 `skipped(requires_docker_backend)`，写进回执和作业结果，作业可以完成。
     - `Block`：验收不通过，作业停在"需要处理"状态，给出切换到 Docker 的指引。
   - 回执里标注"未在隔离环境中验证"。
2. **第二期（P1-5，可选）**：
   - 把各个 Verifier 和 Scanner 的 POSIX 命令改写为托管代码（读文件、遍历目录、算哈希都改用 .NET API）。
   - 只保留真正的工具链调用（`dotnet restore/build/test`、`npm ci`、`pytest`、`gitleaks`）。
   - 这样本地模式也能在 pinned 快照目录中验证。gitleaks 的版本要求不满足时，降级为"未审计"。
- **已定（2026-11-16）**：默认 `Skip`。作业可以在"未经隔离验证"的状态下完成，但必须满足三点：
  - 回执和作业结果里有明确的 `verification: skipped(requires_docker_backend)` 标记。
  - 客户端在作业卡片和计划完成摘要上显示醒目的"未经隔离验证"标识（随 P4 MVP 交付）。
  - 用户可以把 `LocalModePolicy` 改成 `Block`（用户级设置，项目 yml 不能修改）。
- **验收标准**：在没有 Docker 的环境里跑完整作业（用 devsim 场景）：
  - `Skip`：作业完成，回执和作业结果带有跳过标记；
  - `Block`：作业不能通过验收；
  - 两种情况下进程审计里都没有任何 `docker` 调用，也没有容器专用命令。

### P1-5（可选）验证与审计链路与后端无关化（L）
见 P1-4 第二期。

### P1-6（可选）本地资源限制（M）
Windows Job Object、Linux cgroup v2。

### P1 对测试与 CI 的影响
- 需要改写的"无主机回退"测试：
  - `IsolatedToolRoutingTests`
  - `SkillToolTests`
  - `DockerIsolationAttackTests.Unavailable_ShouldRefuseWithoutAnyHostFallback`
- 这些测试的语义改为"**Docker 后端不可用时拒绝，绝不回退到 Local**"，同时新增"Local 后端下的正常执行"用例。
- CI 新增 `windows-latest` 测试作业：运行导出通道安全测试、Windows 规范化攻击用例和工具用例。
- e2e 与无头调用的适配见 P2"非交互调用方"。
- 继续满足 `MIN_PASSED_TESTS` 门槛。

---

## P2. 权限模式、交互式审批与控制面凭据（M–L）

### 模式
| 模式 | 行为 |
|---|---|
| `Ask`（默认） | 每次调用 `ExecuteUntrustedCode` 类工具都要审批。 |
| `AutoInWorkspace` | 只自动放行**已知不执行任意代码、也不写文件**的只读命令，其余都询问。 |
| `FullAuto` | 全部自动放行，仍受命令白名单和导出通道检查约束。UI 和审计明确标注：这等于信任模型能执行的任意代码。 |
| `Deny` | 禁止执行（只读分析模式）。 |

- **`AutoInWorkspace` 的放行规则**：第一期刻意缩小范围，凡是不能证明只读的命令都要询问。
  - 读文件、列目录、搜索这类操作，本来就由不属于 `ExecuteUntrustedCode` 的托管文件工具（`hs_read` 等）完成，不需要放行外部程序。
  - **自动放行只针对两个程序**：
    - `git` 的 `status`、`diff`、`log`、`show`；
    - `rg`（ripgrep）。
  - **可信来源**：可执行文件的解析结果，必须落在一张可信安装位置表里。
    - Windows：注册表登记的 Git for Windows 安装目录，以及用户在设置里显式登记的 `rg` 路径。
    - Unix：`/usr/bin`、`/bin`，以及显式登记的路径。
    - 解析时使用净化过的 PATH：去掉工作区、staging、会话目录和相对路径。
    - 解析结果在工作区或 staging 之内，或者不在可信表里时，一律询问。
  - **参数语法白名单**（按程序分别定义，不在白名单里的参数就询问）：
    - `git`：
      - 固定加上 `--no-pager` 和 `-c core.fsmonitor= -c core.hooksPath=<空目录> -c diff.external= -c core.pager=cat`；
      - `diff`、`show`、`log -p` 固定加 `--no-ext-diff --no-textconv`；
      - 子命令只允许列出的这四个；
      - 拒绝 `-c`、`--exec-path`、`--git-dir`、`--work-tree`、`--output`、`--upload-pack` 等用户参数；
      - 只针对后端自建的 GuestGit 运行（P1-2 中 `.git` 不进 staging，因此不会读到工作区仓库里的配置）。
    - `rg`：
      - 固定加 `--no-config`；
      - 拒绝 `--pre`、`--pre-glob`、`--search-zip`（`-z`）以及任何会启动外部程序的参数；
      - 路径参数必须在 staging 之内。
  - **固定环境**：自动放行的命令使用固定的最小环境。
    - `git` 设置 `GIT_CONFIG_NOSYSTEM=1`、`GIT_CONFIG_GLOBAL` 指向空文件，并清除 `GIT_*`；
    - `rg` 清除 `RIPGREP_CONFIG_PATH`。
  - 除此之外的所有命令都询问，包括：
    - 解释器、包管理器、构建工具（`python`、`node`、`npm`、`npx`、`pip`、`dotnet`、`make`、`cargo`、`go`、`gradle`、`mvn`）；
    - 写文件的命令（`mkdir`、`cp`、`mv`）；
    - 其他 POSIX 工具。
  - 文档和 UI 不再用"不访问网络"来描述这个模式。
- Docker 模式可以单独设置默认值，比如 `AutoInWorkspace`，因为它本身已经隔离。
- 模式只能来自用户级配置或控制面 API，项目 yml 不能放宽。运行期只能通过控制面 API 放宽（见 P1-1）。

### 不可变执行请求与批准绑定
- 每次执行之前，生成不可变的 `ExecutionRequest`，并计算摘要 `requestDigest`（规范化 JSON 的 SHA-256）。请求包含：
  - 工具名、会话 id、回合 id；
  - 解析后的最终可执行文件和解释器链（P1-3）、argv、初始 cwd；
  - stdin 的哈希、`exec_code` 代码的哈希、skill 脚本文件的哈希，以及 `Inputs` 目录清单的哈希；
  - 相对白名单的环境变量差异；
  - 后端、网络模式、文件系统能力（`unrestricted` 或 `container`）。
- **摘要能覆盖什么**：`requestDigest` 覆盖的是**传给工具的输入**，即 argv、stdin、`exec_code` 代码、skill 脚本、`Inputs`、环境差异。
  - 它**不覆盖**被执行程序在运行时自己读取的工作区文件，比如 `python build.py` 里 `build.py` 的内容，或 `npm test` 背后的项目脚本。
  - 因此，批准一个解释器、包管理器或构建工具的命令，本质上是信任它在**执行那一刻**读到的工作区代码。审批卡片要明确写出这一点。
- **防止批准后被替换**：
  - skill 脚本和 `Inputs` 会先复制进 staging；
  - 启动前，对**实际要执行的副本**重新计算哈希，与 `requestDigest` 比对；
  - 不一致就不启动，并重新生成审批请求。
  - 等待审批期间修改原脚本，不会执行未批准的内容。
- **`allow_once`**：只授权这一个 `requestDigest`。
  - 一次性使用，重复提交同一个批准，最多执行一次；
  - 不能跨会话使用；
  - 过期、回合被取消、或者请求被撤销之后，即使再收到批准也不会启动；
  - 等待批准期间，如果权限模式或后端发生变化，这个请求作废并重新生成。
- **`allow_session`**：在当前会话内，复用于 **`requestDigest` 完全相同**的后续请求，会话结束即失效。
  - 它与 `allow_once` 共用同一个摘要定义，不另设按 argv 匹配的规则。因此代码、脚本、stdin、`Inputs`、环境中任何一项有变化，都不能复用。
  - 对 argv0 是解释器、包管理器或构建工具的 `run_command`、`run_tests`，**不提供** `allow_session`，只有 `allow_once`。原因是摘要相同并不代表它们读到的工作区代码相同，提供会话授权会让用户误以为授权范围是固定的。
  - `exec_code` 和 skill 脚本可以使用 `allow_session`，因为它们要执行的代码已经全部包含在摘要里。
- **`allow_always`**：持久规则（`~/.codexflow/permissions.json`），按 `requestDigest` 中与会话无关的部分精确匹配。
  - **只能用于不执行任意代码的精确命令**，比如 `mkdir`、`cp`、`mv` 加固定参数，并且同样要满足可信来源规则（见 `AutoInWorkspace`）。
  - 对解释器、包管理器、构建工具，以及 `exec_code` 和 skill 脚本，**不提供** `allow_always`，也不提供参数前缀匹配。UI 上说明原因："这会信任项目里的任意代码"。
  - 规则可以在 UI 上查看和撤销。

### 控制面凭据（防止模型自批）
- 把 API 分成两个面：
  - **普通面**：对话、历史、SSE、作业查询等。
  - **控制面**：
    - 工具审批（`POST api/gateway/approvals/{id}`）；
    - 计划审批（`StructuredAction` 里的 approve、execute）；
    - 放宽权限模式（`PUT api/settings/execution`）；
    - 权限规则（`api/settings/permissions`）；
    - 新增工作区（P3-3）。
- **托管模式**：
  - 客户端主进程生成两个凭据（普通、控制），通过子进程 stdin 管道传给后端，**不落盘，不写进环境变量**。
  - 后端设置 `Local:TokenDelivery=Pipe`，此时不再写 `host.token`。
  - 本地执行的子进程没有 stdin 管道的继承权，也拿不到这两个凭据。
- **独立运行模式**（devsim、连接已运行实例）：
  - 普通 token 照常写入文件。`Local:DataDirectory` 和其中的 token 文件，在 Windows 上设置只有当前用户可读写的 ACL。
  - **控制 token 默认不写文件**：
    - `Backend=Docker` 时，执行发生在容器里，读不到主机上的文件，因此控制 token 可以写进单独的文件，防自批保证仍然成立。
    - `Backend=Local` 时，默认**不提供控制面**。审批、放宽权限、新增工作区这些操作都无法远程完成。
      - 此时 `Ask` 模式下的执行请求会以 `approval_unavailable` 快速失败，作业里写明"请使用托管客户端，或切换到 Docker 后端"。
      - 这样 Local + `Ask` 的防自批保证依然成立。
  - **不安全兼容模式**：`Local:UnsafeFileControlToken=true`，必须由用户在用户级配置里显式开启，默认关闭。项目 yml 不能开启，客户端也不会自动开启。
    - 开启后，Local 后端下也会写控制 token 文件。
    - 防自批保证**不再成立**：同一用户下的命令能读到这个文件。
    - `GET api/system/status`、客户端状态栏和每条审计记录都标注 `selfApprovalProtection: false`，启动时打警告。
    - 这个模式只用于 CI 和 devsim 这类无人值守的场景。
  - 以后可以考虑配对机制（客户端输入后端控制台上显示的一次性配对码，换取只存在内存里的控制凭据），列为后续项，不在本期范围内。
- 威胁模型写进文档：
  - 托管模式下，模型发起的命令不能自批，也不能放宽模式。
  - 但一个**已获批准**的命令以用户权限运行，可能篡改用户文件（包括 `~/.codexflow`）。由于运行期放宽只能走控制面 API，这类篡改最多在下次启动时生效。
  - 同一用户下的进程，理论上也能读取后端或客户端进程的内存，从中拿到凭据。这超出了操作系统层面不隔离时所能防护的范围。
  - 以上两点列为已知的残余风险。要防范它们，应使用 Docker 后端。

### 机制
- 在 `ToolIsolationGate.TryAcquire` 之后、真正执行之前，插入 `IExecutionApprovalBroker`：
  - 发出 SSE 事件 `tool_approval_request`：
    ```json
    { "approvalId", "requestDigest", "sessionId", "tool", "executable", "interpreterChain", "argv", "cwd",
      "stdinSha256?", "codeSha256?", "scriptSha256?", "backend", "network", "filesystem", "reason", "expiresAt" }
    ```
  - 工具挂起等待，有超时（默认 10 分钟，超时视为拒绝）。
  - 客户端调用 `POST api/gateway/approvals/{approvalId}`，带控制面凭据，请求体为 `{ requestDigest, decision: allow_once | allow_session | allow_always | deny, note? }`。
  - 摘要不一致时返回 409。
  - 结果发出 `tool_approval_resolved` 事件。
- **后台作业**（`BackgroundJobRunner`）在没有前台订阅者时，复用现有的 `worker_waiting_user` 和 `ResumeToken` 机制挂起，审批通过后再 resume。
- 审批的输入、摘要、决定和决定者写入审计（`IWorkflowAuditStore`）。
- **不能只靠 SSE 送达**：订阅者队列满了会 `DropOldest`，大量 `tool_output` 可能把 `tool_approval_request` 挤掉。因此有两条要求：
  - 待审批项以服务端状态为准。客户端在每次重连、收到 `replay_gap`、以及心跳时（用一个轻量的计数字段）都与 `GET api/gateway/session/{id}/approvals` 对账。
  - `tool_output` 在服务端合并和限流，避免淹没控制类事件。
- **与 QRE v2 工具管线的关系**：审批挂起发生在工具执行内部（`CodexFlowV2RuntimeTool`）：
  - 等待审批期间，暂停模型流超时（`_modelStreamTimeout`）和回合预算（`RuntimeBudgetSnapshot`）的计时。
  - 取消回合（P3-5）时，挂起的审批以 `deny` 结束。

### 非交互调用方（e2e、CI、自动化）
- 默认值切换后，无头调用方必须显式选择行为，不会悄悄卡住 10 分钟：
  - 配置 `Execution:Backend=Docker` 加 `PermissionMode=AutoInWorkspace`/`FullAuto`；
  - 或者在本地模式下用 `FullAuto`。
- 当 `Ask` 模式下没有任何能审批的订阅者时，请求在 `Execution:HeadlessApprovalTimeout`（默认 30 秒）内快速失败为 `approval_unavailable`，并在作业结果里说明原因。这个值在 CI 配置里可以调整。
- devsim 的调整：
  - 首选 `--execution-profile docker-auto`：Docker 后端加 `AutoInWorkspace`/`FullAuto`，不需要控制面。
  - `--auto-approve`：通过控制面 API 自动批准，并在报告里记录被批准的 `requestDigest`。用于 Local 后端时，必须同时开启 `Local:UnsafeFileControlToken`，报告里标注"防自批保证不成立"。
  - `scripts/e2e/devsim/README.md` 补充配置说明。
- **验收标准**：默认值切换后，现有 devsim 场景按上述配置能够通过。

### 验收标准
- `Ask` 模式下，没收到批准就不会有任何进程启动（用测试断言 runner 没被调用）。
- 四类执行工具都有审批用例：`run_command`、`exec_code`、`run_tests`、skill 脚本。
  - argv、cwd、解释器链不变的前提下，代码、脚本内容、stdin、`Inputs` 或环境差异中任何一项改变，`allow_once` 和 `allow_session` 都不能复用；
  - 等待审批期间修改脚本，启动前的副本哈希校验会拦截，不会执行未批准的内容；
  - 完全相同的请求可以复用 `allow_session`；`allow_once` 的重复提交只执行一次。两种行为分开测试，互不混淆；
  - 不能跨会话使用；
  - 过期或回合取消后不会启动；
  - 解释器、包管理器、构建工具拿不到 `allow_session` 和 `allow_always`。
- `AutoInWorkspace` 的放行测试：
  - 在工作区里放一个同名程序，或在 PATH 前部放一个同名程序（`git.exe`、`rg.exe`），都拿不到自动放行；
  - 白名单之外的参数（比如 `git -c ...`、`git --output`、`rg --pre`）都要询问；
  - 仓库或用户配置了 `core.fsmonitor`、`diff.external`、`textconv`、`core.pager`、`RIPGREP_CONFIG_PATH` 时，自动放行的命令不会启动外部程序；
  - 解释器、包管理器、构建工具不会因为命令名或前缀而被自动放行。
- 自批防护：
  - 托管模式下，本地执行的命令在环境变量和磁盘上都拿不到控制凭据；
  - 用普通凭据调用控制面接口，返回 403；
  - 端到端：`Ask` 模式下，由命令内部发起的自批请求不会让任何进程启动；
  - 独立运行模式加 Local 后端、且不开不安全兼容模式时：磁盘上没有控制 token 文件，控制面接口不可用，执行请求以 `approval_unavailable` 失败。已获批准的命令也无法通过读文件拿到控制凭据，去批准后续请求或放宽权限；
  - 不安全兼容模式默认关闭。开启后，状态接口、UI 和审计都标注 `selfApprovalProtection: false`。
- 长时间审批（大于模型流超时）不会让回合超时或预算耗尽。
- 后台作业审批挂起后，批准能 resume，拒绝能正确结束。
- 断线重连后，待审批项能通过 `GET api/gateway/session/{id}/approvals` 拿回。

---

## P3. 面向客户端的后端 API 补齐（M–L）

所有新端点都放在 `CodexFlow.Gateway.Http`，DTO 用显式 record，不再返回匿名对象。属于控制面的端点，要求控制面凭据（见 P2）。

| # | 能力 | 端点（草案） |
|---|---|---|
| P3-1 | 系统状态 / 首次运行 | `GET api/system/status`：后端版本、执行后端及其可用性、模型是否已配置且可达、preflight 报告、当前权限模式、token 的传递方式 |
| P3-2 | 设置 | `GET/PUT api/settings/model`（ApiKey 只写不读，只回显后 4 位）、`GET/PUT api/settings/execution`（后端、权限模式；放宽属于控制面）、`GET/DELETE api/settings/permissions`（控制面） |
| P3-3 | 打开本地文件夹作为工作区 | `POST api/gateway/workspaces { path, name? }`（控制面）：准入规则见下文 |
| P3-4 | 会话 / 对话管理 | 重命名、删除、归档；分页列出对话 |
| P3-5 | 取消回合 | `POST api/gateway/session/{id}/cancel`：取消正在运行的主对话回合，挂起的审批以 deny 结束，最后发出 `done{source:"cancelled"}` |
| P3-6 | 计划 | `GET api/gateway/session/{id}/plan`（id、version、hash、status、markdown）；新增 SSE 事件 `plan_approval_requested`，带 id、version、hash |
| P3-7 | 工具事件增强 | `tool_call` 事件带上参数（大字段截断，敏感字段脱敏）；新增 `tool_started`、`tool_output`（流式 stdout/stderr，可截断）、`tool_approval_*` |
| P3-8 | 文件与改动 | `GET .../files?path=`（列目录，限定在工作区内）、`GET .../files/content?path=`（只读，有大小上限）、`GET .../changes`（工作区相对基线的 diff，unified 格式） |
| P3-9 | 类型化契约 | 用 `Microsoft.AspNetCore.OpenApi` 生成 OpenAPI；SSE 事件写一份 JSON Schema 和 `schemaVersion`；客户端据此生成 TypeScript 类型（`openapi-typescript`），CI 校验生成物没有漂移 |
| P3-10 | 托管所需的宿主改动 | 见下文 |
| P3-11 | 状态快照 | `GET api/gateway/session/{id}/snapshot`：会话的权威状态（消息、进行中的回合、待审批项、当前计划、各工具调用的最终结果、作业状态），带 `asOfEventId` |

- **P3-3 工作区准入规则**：
  - 先把路径完全解析（展开符号链接、junction 和 `..`），再做判断。
  - 拒绝的路径：
    - 文件系统根和盘符根；
    - 系统目录：Windows 的 `%SystemRoot%`、`Program Files*`、`ProgramData`；Unix 的 `/etc`、`/usr`、`/bin`、`/var`、`/System` 等；
    - 用户主目录本身；
    - `Local:DataDirectory`、`~/.codexflow`，以及与它们有祖先或后代关系的任何路径。
  - 获准的路径持久化到用户级 `~/.codexflow/appsettings.json` 的 `Workspace:AllowedLocalRepositoryRoots`，通过现有的 reload 机制生效。
  - 获准的工作区记入审计。
- **P3-10 宿主改动**：
  - 支持 `Local:Port=0`。启动后在 stdout 输出一行 `CODEXFLOW_READY {"port":…,"pid":…,"instanceId":…}`。
  - 新增 `Local:DataDirectory`：state.db、token 文件、workspaces 全部放在这里，不再依赖 cwd。
  - 新增 `Local:TokenDelivery = File | Pipe`（见 P2）。
  - 新增 `Local:AllowedOrigins`：CORS 只允许客户端的自定义协议来源。
  - 新增 `Local:ParentPid`：父进程退出后，后端自动退出。
  - **单实例互斥**：在 DataDirectory 上加一个独占锁文件（`instance.lock`）和一个发现文件（`instance.json`，内容为 pid、port、instanceId，不含 token）。第二个实例指向同一个 DataDirectory 时，立即失败并给出提示，不会覆盖 token，也不会和已有实例争用 state.db。"连接已运行实例"模式通过发现文件找到端口。
- **事件恢复契约**：
  - SSE 只保证"在重放缓冲范围内续传"。客户端按 `id` 去重。
  - 碰到下列情况时，客户端改用 P3-11 快照恢复权威状态：`replay_gap`、`epoch` 变化、后端重启、超过重放容量。
  - 流式输出（`content` 增量、`tool_output`）允许截断，快照里用 `truncated: true` 标注。
- 兼容性：现有端点和事件不改名，只新增。`scripts/e2e/devsim/client.py` 在独立运行模式下继续可用，见 P2"非交互调用方"。
- **验收标准**：
  - 每个新端点都有控制器测试，并做归属校验（`userId == session.UserId`）。
  - 控制面端点用普通凭据调用时返回 403。
  - 文件接口有路径穿越测试。
  - P3-3 准入测试：拒绝 DataDirectory、`~/.codexflow`、根目录、系统目录、用户主目录，以及通过符号链接或 junction 指向这些位置的路径。
  - 两个实例共用同一个 DataDirectory 时，第二个启动失败，第一个的 token 和数据库不受影响。

---

## P4. Electron 客户端 MVP（L）

### 工程
- 位置：`clients/desktop/`，独立的 Node 工程，**不进 `CodexFlow.slnx`**，不影响 `check_packages.py` 和 dotnet 漏洞审计。
- 技术栈：
  - Electron + electron-vite + React 18 + TypeScript (strict)
  - Tailwind + Radix/shadcn
  - TanStack Query（REST）+ Zustand（会话状态）
  - `react-markdown` + Shiki（代码高亮）；Monaco diff 或 `react-diff-view`
  - 长消息列表用虚拟滚动
- 测试：Vitest（单元）、Playwright for Electron（端到端，后端用 mock SSE 服务器）。
- CI：新增 `client.yml`，跑 lint、typecheck、test、build，以及 `npm audit --omit=dev`，并校验 OpenAPI/TS 类型没有漂移。

### 进程与安全模型
- **主进程**：
  - 启动时把 `dotnet publish` 产出的自包含 `CodexFlow.Local` 作为 `extraResources` 拉起。
  - 参数为 `Local:Port=0`、`Local:DataDirectory=%APPDATA%/CodexFlow`（mac/linux 用对应目录）、`Local:PreflightMode=Report`、`Local:TokenDelivery=Pipe`、`Local:ParentPid`。
  - 生成普通凭据和控制凭据，经 stdin 管道交给后端。解析 `CODEXFLOW_READY` 拿到端口。
  - 后端崩溃时带退避自动重启，日志写入 `logs/`。
  - 支持"连接到已运行的实例"：通过发现文件或手动填写地址，再加上 token 文件。
    - 连接 Docker 后端的实例：功能完整。
    - 连接 Local 后端的实例：默认没有控制面，可以查看和对话，但不能审批执行。UI 引导用户改用托管模式。如果对方开启了不安全兼容模式，UI 常驻提示"防自批保证不成立"。
- **渲染进程**：
  - 设置 `contextIsolation`、`sandbox`，关闭 `nodeIntegration`。
  - preload 只暴露最小 API：`getBackend()`（端口和普通凭据）、`approve(...)` / `updateControlSetting(...)`（由主进程代为带上控制凭据发请求，控制凭据不进渲染进程）、`pickFolder()`、`openExternal()`、`showNotification()`。
  - CSP 的 `connect-src` 只允许 `http://127.0.0.1:{port}`。
  - 页面通过自定义协议 `app://` 加载，后端 CORS 只放行这个来源。
- **SSE**：`fetch` + `ReadableStream` 自己实现解析，因为需要带 `Authorization` 头，原生 `EventSource` 做不到。
  - 维护 `lastEventId` 和 `epoch`，断线后指数退避重连，按事件 id 去重。
  - 碰到 `replay_gap`、epoch 变化或后端重启时，用 P3-11 快照恢复。
  - 心跳超时视为断线。

### MVP 功能（对标 Codex / Claude 桌面端）
1. **首次运行向导**：检测后端状态，配置模型（ApiUrl、ApiMode、Model、ApiKey），选择执行后端和权限模式，提示 Docker 是否可用，并说明本地执行的能力边界（见第 1 节的不变量对照表）。
2. **侧栏**：工作区（本地文件夹）→ 会话 → 对话，支持新建、重命名、删除，可打开本地文件夹（受 P3-3 准入规则约束）。
3. **对话视图**：
   - Markdown 流式渲染，代码块可复制。
   - 思考过程可折叠。
   - 工具调用卡片（名称、参数、运行状态、流式输出、结果、是否被截断）。
   - Worker 状态条。
   - 系统通知。
4. **审批卡片**：
   - 命令审批：显示最终可执行文件、解释器链、argv、cwd、代码或脚本摘要、后端、网络、文件系统能力。
     - 可选：允许一次 / 本会话允许 / 始终允许（仅符合条件的命令）/ 拒绝。
     - 解释器类命令要说明"会执行项目代码"。
   - 计划审批：渲染 markdown，可批准、驳回、仅计划，提交时带上 version 和 hash。
5. **输入区**：多行输入，回合进行中显示"停止"，409 时给出友好提示，可切换会话模式（continue / new thread）。
6. **设置页**：模型、执行后端、权限模式（放宽时要二次确认），以及"始终允许"规则管理。
7. **状态栏**：后端连接状态、执行后端、权限模式、上下文窗口占用（`context-window` 接口）。
8. **验证状态标识**：作业卡片和计划完成摘要上显示"未经隔离验证"（对应 P1-4）。

### 验收标准
- Windows 上冷启动到可对话：已配置模型时不超过 5 秒（不含模型响应时间）。
- 强杀后端后客户端能自动恢复，状态与 P3-11 快照一致。
- 恢复测试，每种情况都检查会话、审批、计划、工具结果是否正确恢复，以及截断标识是否正确：
  - 短时断线：在重放缓冲内续传，没有重复；
  - 超过重放容量；
  - 订阅者队列丢弃事件；
  - 后端重启或 epoch 变化。
- 关闭客户端后没有残留的后端进程。
- 渲染进程拿不到控制凭据（测试断言 `window` 上没有这个值，preload 也不返回它）。

---

## P5. 客户端增强与发布（M）
- 改动面板（diff，依赖 P3-8）、文件树、后台作业面板（列表、取消、resume、continue）。
- 系统托盘、回合完成或待审批时的系统通知、命令面板和快捷键、深色/浅色主题、中英文切换。
- 打包与发布：
  - 用 electron-builder 出 Windows NSIS、macOS dmg（需要签名和公证）、Linux AppImage。
  - 每个平台内置对应 RID 的自包含后端。
  - 自动更新（electron-updater，带签名校验）。
- 在 `publish.yml` 增加客户端产物，但不影响 NuGet 发布流程。

---

## 6. 风险与待决问题
1. **本地模式没有文件系统、网络和资源隔离**（见第 1 节的不变量对照表）。本地模式的安全只建立在"未经批准不启动"和"模型不能自批"之上。获准的命令以用户权限运行，UI 必须明确展示"本地执行 = 以你的用户权限运行，可以访问你能访问的任何文件"。
2. **已获批准的命令可能篡改用户配置**（P2 威胁模型）。运行期放宽只能走控制面，这类篡改最多在重启后生效。列为残余风险。
3. **独立运行模式 + Local 后端**：默认不提供控制面，所以这种组合下无法审批执行，是有意为之的功能限制。只有显式开启不安全兼容模式才能审批，代价是失去防自批保证。配对机制列为后续项。
4. **验证链路降级**（P1-4）：已定为默认 `Skip` 加标注。剩下的风险是用户忽略标注，所以 UI 的标识必须醒目，P1-5 也值得尽早排期。
5. **大仓库复制开销**：第一期设上限并裁剪，必要时上 `git worktree` 加速。
6. **Windows 工具链差异**：`.cmd` 入口只走适配表，`.sh` skill 依赖 bash，私有 HOME 下的工具链缓存需要显式映射。这些都要在 Windows CI 上覆盖。
7. **事件契约演进**：引入 `schemaVersion` 后，客户端要容忍未知事件和字段。
8. **模型客户端收敛未决项（PM）**：`ExecutorChatClientBridge` vs 装饰器内拼装记忆的最终选择，以及 `CodexFlow.QueryRuntime.Models` 与 `Engine 0.26.2` 匹配包版本的可用性确认。

## 7. 建议的交付顺序
1. **PM（与 P1 并行）**：先核对 `CodexFlow.QueryRuntime.Models` NuGet 包版本并拍板记忆注入方案，完成 `CodexFlowRuntimeModelClient` + 差分测试（建议在 P2 超时暂停与 P3-5 取消回合联调前合入）。
2. P1-1（含 `Validate()` 前置）→ P1-2（先把 tar 前缀改成参数，再做 Windows 规范化）→ P1-3（含 Windows 启动策略）。这一步完成后，Local 后端可以通过配置启用，**默认仍是 Docker**。同时先冻结 P3 和 P2 的契约（OpenAPI 加事件 Schema）。
3. P4 的脚手架和 mock 后端，与第 2 步并行。
4. P1-4 第一期，与 P2（含控制面凭据和非交互调用方适配）并行。
5. **默认值切换**为 Local + `Ask`。硬前置有四项：
   - P2 合入；
   - P1-4 第一期合入，且通过无 Docker 的完整作业验收；
   - 升级迁移规则就绪；
   - devsim 和 CI 的非交互配置已调整，并且通过。
6. P3-1/2/3/5/6/7/10/11 → 客户端接入真实后端，交付 MVP（含"未经隔离验证"标识）。
7. P3-8 + P5。
8. 可选项：P1-5、P1-6。

---

## 第 1 轮评审处理记录（v1 → v2）

| 编号 | 评委 | 级别 | 处理 | 改动位置 |
|---|---|---|---|---|
| CLA-001 | claude | blocking | 采纳 | P2"控制面凭据"、P3 控制面端点、P3-10 `TokenDelivery`、P4 渲染进程不持有控制凭据、风险 2、3 |
| CLA-002 | claude | blocking | 采纳 | 第 1 节原则 2、4 和不变量对照表；P2 的 `AutoInWorkspace` 放行清单、`allow_always` 限制；风险 1 |
| CLA-003 | claude | blocking | 采纳 | P1-2 子任务 b（Windows 目录 → tar 规范化）和 Windows 攻击用例 |
| CDX-001 | codex | blocking | 采纳 | 同 CLA-002；P1-2 验收拆成导出通道测试和容器隔离测试，新增"能力如实性"测试 |
| CDX-002 | codex | blocking | 采纳 | P1-3 Windows 启动策略（解释器适配表，拒绝不在表里的 `.cmd`）和参数测试 |
| CDX-003 | codex | blocking | 采纳 | P2"不可变执行请求与批准绑定"，以及四类工具的审批用例 |
| CDX-004 | codex | blocking | 采纳 | P1-4 第一期改为默认值切换的硬前置，统一策略入口和验收；第 7 节第 3、4 步 |
| AGY-001 | antigravity | blocking | 采纳 | 同 CDX-004 |
| AGY-002 | antigravity | blocking | 采纳 | P3-3 工作区准入规则和测试 |
| AGY-003 | antigravity | blocking | 采纳 | P1-1 升级迁移；P2"非交互调用方"；第 7 节第 4 步 |
| AGY-004 | antigravity | advisory | 采纳 | P3-10 单实例互斥和发现文件 |
| AGY-005 | antigravity | advisory | 采纳 | P2 验收：超时暂停、取消联动、后台作业恢复；`AutoInWorkspace` 改为精确清单 |
| AGY-006 | antigravity | advisory | 采纳 | P1-2 重解析点与大小写规则；环境变量里的工具链缓存映射 |
| CDX-005 | codex | advisory | 采纳 | P3-11 快照接口和事件恢复契约；P4 恢复验收改为按场景检查，删去"不丢失"的笼统表述 |

## 第 2 轮评审处理记录（v2 → v3）

| 编号 | 评委 | 级别 | 处理 | 改动位置 |
|---|---|---|---|---|
| CLA-001 | claude（codex 认为仍阻塞） | blocking | 采纳 | 不变量对照表"自批"一行；P2 独立运行模式：Local 后端默认不提供控制面，新增 `Local:UnsafeFileControlToken` 不安全兼容模式（默认关闭，开启后全程标注 `selfApprovalProtection: false`）；devsim 首选 docker-auto；P4"连接已运行实例"；风险 3；P2 自批验收 |
| CLA-002 | claude（codex 认为仍阻塞） | blocking | 采纳 | P2 `AutoInWorkspace` 缩小为只有 `git`（4 个只读子命令）和 `rg`；增加可信来源表、净化 PATH、按程序定义的参数语法白名单和固定环境；删去 `dir`（它是 cmd 内置命令，不是可执行文件）等无法证明只读的项；补充同名程序和配置触发外部程序的测试 |
| CDX-003 | codex | blocking | 采纳 | P2 写明摘要覆盖范围和启动前副本哈希校验；`allow_session` 改为只复用摘要完全相同的请求；解释器、包管理器、构建工具只有 `allow_once`；补充各类输入变化的验收用例 |
| CLA-004 | claude | advisory | 采纳 | 与 CLA-002 合并处理：给 git 和 rg 固定安全参数和环境，只在 GuestGit 上运行，并补充测试 |
