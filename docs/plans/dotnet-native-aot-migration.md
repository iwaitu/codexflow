# CodexFlow .NET NativeAOT 改造方案

- 日期：2026-11-17
- 文档版本：v1.1（已纳入首轮 Codex 独立评审意见修订，待 Phase 0 探针验证与复审）
- 状态：技术方案待确认；**尚未执行 NativeAOT 编译探针与代码改动**
- 目标范围：`CodexFlow.Local` 宿主及底层共享库（`Contracts`、`Core`、`Orchestration`、`Gateway.Http`、`Storage.Sqlite`）
- 关联文档：[`docs/releasing.md`](../releasing.md)、[`docs/plans/local-execution-and-desktop-client.md`](local-execution-and-desktop-client.md)、[`dotnet-native-aot-migration-review.md`](dotnet-native-aot-migration-review.md)

---

## 1. 背景与改造目标

### 1.1 为什么要做（Why）

目前 Electron 桌面客户端内置自包含发布的 `CodexFlow.Local`（见 [`.github/workflows/client.yml`](../../.github/workflows/client.yml#L169-L179)），在基线提交 `f2e9772` 下：

- 生产代码规模（排除 `bin/`、`obj/`）：**6 个项目、510 个 `.cs` 文件、115,826 行**（均以 `net10.0` 为目标框架；测试项目 `CodexFlow.Core.Tests` 为 190 个文件 / 47,393 行），同时仓库向 NuGet 发布 5 个同版本号共享库包。
- 当前自包含后端输出（`clients/desktop/resources/backend`）：**447 个文件 / 150.74 MiB**（对应 Windows 安装包 `138.4 MB` setup / `173.8 MB` portable），其中 `System.*.dll` 约 62.15 MB、`Microsoft.CodeAnalysis*.dll` 约 14.76 MB、`Microsoft.EntityFrameworkCore*.dll` 约 5.12 MB，另有 `coreclr.dll` / `clrjit.dll` 等运行时文件。

推进 NativeAOT 改造的核心目标：

1. **缩小桌面端分发体积与文件数**：消除完整 BCL、JIT 编译器与未裁剪程序集，收敛为原生可执行文件加必要的原生动态库（如 `e_sqlite3`）。
2. **加快桌面端冷启动并降低常驻内存**：在 Electron 拉起 `CodexFlow.Local` 时省去 JIT 预热开销。
3. **保持契约、权限边界与存量数据兼容**：不破坏 `docs/contracts/openapi.json`、`docs/contracts/sse-events.schema.json`、Electron 握手协议（`LocalHostRuntime.FormatReadyLine`）、鉴权/控制面策略及存量 `.codexflow/state.db` 数据平滑升级。

### 1.2 前提与不纳入 AOT 范围

- **已知前提**：用户确认自有上游包 `CodexFlow.QueryRuntime.Engine`（`0.26.2`）本身支持 NativeAOT。但该包在当前缓存 DLL 中未写入 `IsAotCompatible` 元数据，必须在 **Phase 0** 中通过真实 AOT 发布探针验证其在 `CodexFlow.Local` 中的实际链接与调用行为。
- **已标注 AOT 元数据的依赖**：`CodexFlow.QueryRuntime.Models` (`0.26.2`)、`VllmChatClient` (`2.0.39`)、`ModelContextProtocol.Core` (`2.4.0` `net10.0`) 均标注了 `IsAotCompatible` 与 `IsTrimmable`（仍需结合本项目调用路径做真机 AOT 运行验证）。
- **不纳入 AOT 的范围**：`CodexFlow.Core.Tests`（依赖 `Moq`、`FluentAssertions`、`xunit.v3`）保留普通 JIT 测试；Docker 隔离验证镜像与脚本保持不变。

---

## 2. 需要用户决策的关键事项（User Review Required）

> [!WARNING]
> 以下 3 项架构决策直接决定工期、共享 NuGet 包兼容性以及后续维护模式，请在进入编码前拍板（尤其是 **决策 1 需等待 Phase 0 EF Core 实测结果后最终定案**）：

### 决策 1：数据存储层（`CodexFlow.Storage.Sqlite` + `CodexFlow.Orchestration`）改造路线
当前 [`IJobDbContext`](../../CodexFlow.Orchestration/Jobs/IJobDbContext.cs#L21-L49) 直接暴露 EF Core 类型（`DbSet`、`DatabaseFacade`、`ChangeTracker`、`EntityEntry`），[`JobDbContext`](../../CodexFlow.Orchestration/Jobs/JobDbContext.cs) 映射了 18 个 `DbSet` 实体（另有 3 张模型外原生 SQL 表 `OutboxSeq`、`JobEventLog`、`LocalKv` 建于 [`20261113221012_Initial.cs`](../../CodexFlow.Storage.Sqlite/Migrations/20261113221012_Initial.cs#L300-L324)）。多处业务服务（如 [`ChatService.cs`](../../CodexFlow.Orchestration/Chat/ChatService.cs#L273)、[`BusinessPluginControlService.cs`](../../CodexFlow.Orchestration/BusinessSkills/BusinessPluginControlService.cs#L744)）通过 `IJobDbContext` 动态拼接 `IQueryable`、`AsSplitQuery()` 或 `ExecuteUpdateAsync()`，且 [`SqliteStorage.cs`](../../CodexFlow.Storage.Sqlite/SqliteStorage.cs#L48-L49) 使用了 `ProviderModelCacheKeyFactory` 和 `OutboxSeqInterceptor`。

- **选项 A（候选优先，需 Phase 0 探针确认）：保留 EF Core + Compiled Models + Precompiled Queries + 版本化 SQL 迁移**
  - **关键约束**：仅仅使用 `.UseModel(...)`（Compiled Models）**不足以**支撑 NativeAOT 运行时查询翻译；必须验证 EF Core 10 实验性的 **查询预编译（`--precompile-queries --nativeaot`）** 是否能覆盖 `IJobDbContext` 接口注入及现有拦截器，并将 `ChatService`、`BusinessPluginControlService` 等处的**动态条件拼接 `IQueryable` 改写为固定结构查询（或有限分支的静态查询）**。同时将 `Database.MigrateAsync()` 替换为版本化 SQL 迁移 runner。
  - *优点*：最大化复用现有映射与业务逻辑，与平台 PostgreSQL 上下文共享 `JobModel`。
  - *缺点*：EF Core NativeAOT 目前官方仍标注为实验性能力；如果 Phase 0 探针证明动态条件/拦截器/`IJobDbContext` 接口无法被预编译拦截器稳定覆盖，则应回退到选项 B。
- **选项 B（彻底去 EF Core：抽象仓储边界 + `Microsoft.Data.Sqlite` / Dapper AOT）**
  - 不能只替换 `ISessionDirectory`、`IChatService`、`IJobEventLog`——因为 `BusinessPluginControlService` 等服务在 `CodexFlow.Orchestration` 中直接注入了暴露 EF Core 类型的 `IJobDbContext`。
  - 选择此路线时，必须先将 `Orchestration` 内所有消费 `IJobDbContext` 的服务（如后台任务、PlanArtifact、BusinessSkills 等）抽离为**不泄漏 EF 类型的纯仓储/工作单元接口**，明确事务边界、并发令牌与 `OutboxSeq` 原子递增契约，再在 `Storage.Sqlite` 中实现基于 `Microsoft.Data.Sqlite` 的版本（或者将 EF 实现拆入独立兼容包）。
  - *工作量影响*：需在估算中额外增加 **2.5 ~ 4 人周**。

### 决策 2：网关层（`CodexFlow.Gateway.Http`）对外包兼容性与 RDG 归属
[`CodexFlow.Gateway.Http`](../../CodexFlow.Gateway.Http/CodexFlow.Gateway.Http.csproj) 是对外发布的 NuGet 包之一，当前 [`scripts/ci/package-consumer/Program.cs`](../../scripts/ci/package-consumer/Program.cs#L9) 直接引用了公开类型 `CodexGatewayController`。同时 Minimal APIs 的 **Request Delegate Generator (RDG)** 是针对 `Map*` 调用点的编译期源生成器，只在**包含 `Map*` 调用的程序集编译时触发**；消费端直接引用编译好的 `.nupkg` 并在下游开启 `PublishAot=true` 时，不会重新对包内源码运行 RDG。

- **选项 A（推荐：破坏性主/次版本升级，全面切换为自带 RDG 的 Minimal APIs 包）**
  - 将 8 个 Controller、74 个端点重写为 `CodexFlow.Gateway.Http` 内部的 Minimal APIs 端点映射（确保 RDG 在 `CodexFlow.Gateway.Http` 编译期完成全部端点代码生成），同步更新 `package-consumer` 与外部平台宿主的接入方式。
- **选项 B（共享核心 Handler，同时提供 Minimal APIs 与 MVC Controller 适配层）**
  - 将 8 个 Controller 的业务逻辑抽取为共享的框架无关 Handler/Service，在 `CodexFlow.Gateway.Http`（或新增 `CodexFlow.Gateway.AspNetCore`）保留旧 Controller 入口供非 AOT 平台宿主过渡，Minimal APIs 供 `CodexFlow.Local` 使用。工作量约增加 **1 ~ 1.5 人周**，但避免破坏现有平台宿主或 NuGet 下游调用方。

### 决策 3：Roslyn（`Microsoft.CodeAnalysis`）处理策略
仅 [`RoslynCodeAnalysisService.cs`](../../CodexFlow.Core/Agents/RoslynCodeAnalysisService.cs#L38-L40) 使用了 `CSharpCompilation.Create` 与 `MetadataReference.CreateFromFile(typeof(object).Assembly.Location)`（在 NativeAOT 单文件下 `Assembly.Location` 为空，该类会在第 61 行捕获 `ArgumentException` 并记录错误日志后**静默返回空诊断列表**，导致该工具功能失效）。其余 4 个文件仅使用 `CSharpSyntaxTree.ParseText`。同时 [`CodexFlow.Storage.Sqlite.csproj`](../../CodexFlow.Storage.Sqlite/CodexFlow.Storage.Sqlite.csproj#L21-L22) 引入的 `Microsoft.CodeAnalysis.CSharp.Workspaces` 等将 14.76 MB 的 Roslyn 程序集带入了发布目录。

- **选项 A（推荐：清理无用 Workspaces 运行时资产 + 调整 `RoslynCodeAnalysisService`）**
  - 检查 `Storage.Sqlite` 最终 NuGet 资产图（注：`PrivateAssets="all"` 仅控制依赖向下游包传递，需确认本地打包目录是否还包含 `Workspaces.dll`，必要时将设计时编译对齐包移至独立 Design-Time 工程或通过 `<ExcludeAssets>runtime</ExcludeAssets>` 验证），移除 `Assembly.Location` 引用；`RoslynCodeAnalysisService` 改用纯语法树诊断（权衡：会失去跨符号的语义报错），或受控调用工作区宿主环境中的 `dotnet build`（需配套 SDK 检测、超时控制与不可信工作区安全沙箱边界，不可在未授权下自动构建不可信代码）。
- **选项 B（彻底将 Roslyn 从本地宿主运行时剥离）**
  - 通过可选扩展或纯外置命令替代，可进一步削减打包体积与 ILC 链接耗时，但需重新设计依赖 `ICodeAnalysisService` 的内部规则与测试用例。

---

## 3. 完整基线审计清单（已核实事实）

| 审计维度 | 实测数据与关键位置 | 对 NativeAOT 的影响 |
|---|---|---|
| **HTTP 网关** | 8 个 Controller、74 个 `[Http*]` 端点；多处匿名返回（如 `BackgroundJobController.cs:102` 起及 `CodexGatewayController.cs` 等共 **10+ 处**）；[`CodexGatewayController.cs:469`](../../CodexFlow.Gateway.Http/Controllers/CodexGatewayController.cs#L469) `IFormFile` 上传；[`BusinessPluginsController.cs:28`](../../CodexFlow.Gateway.Http/Controllers/BusinessPluginsController.cs#L28) `IAsyncActionFilter`；[`GatewayOpenApiExtensions.cs:49`](../../CodexFlow.Gateway.Http/Contracts/GatewayOpenApiExtensions.cs#L49) `AddControllers()` | MVC 不支持 NativeAOT；需迁移至 Minimal APIs + `IEndpointFilter`，并在 `Gateway.Http` 编译期完成 RDG 生成。`Microsoft.AspNetCore.OpenApi` 在 .NET 10 本身支持 Trimming/AOT，需验证精简宿主的文档推断兼容性。 |
| **JSON 与契约生成** | **71 个**生产 `.cs` 文件包含 `Newtonsoft.Json` / `JsonConvert` / `JObject` / `JArray` / `JToken`；157 处 `System.Text.Json` 匹配中 **0 处** `JsonSerializerContext`；[`JobFailureWire.cs:13`](../../CodexFlow.Core/Protocols/JobFailureWire.cs#L13) 使用 `StringEnumConverter` + 非驼峰格式；[`KeyValueSessionStore.cs`](../../CodexFlow.Orchestration/Persistence/KeyValueSessionStore.cs) 与 [`QueryRuntimeCheckpoint.cs:170`](../../CodexFlow.Core/Runtime/QueryRuntimeCheckpoint.cs#L170) 读写历史 JSON；[`SseEventContractSchema.cs:248-320`](../../CodexFlow.Gateway.Http/Contracts/SseEventContractSchema.cs#L248-L320) 用 `Dictionary<string, object>` 与匿名对象生成 SSE Schema；多处 `[JsonConverter(typeof(JsonStringEnumConverter))]` 非泛型特性 | AOT 下默认关闭 STJ 反射序列化，`Newtonsoft.Json`、匿名对象及未注册类型均会运行时失败。且**必须冻结历史持久化数据与协议的序列化契约**，避免统一改成 `Web/CamelCase` 后破坏旧 Checkpoint 或旧库读写。 |
| **工具工厂与反射** | [`CodexToolFunctionAdapterFactory.cs:151`](../../CodexFlow.Core/Agents/CodexToolFunctionAdapterFactory.cs#L151) `MarkedResultJsonOptions` 无 `TypeInfoResolver`，50+ 个 `AIFunctionFactory.Create` 未传源生成 `SerializerOptions`（`GatewayMessageProcessor.cs` 同理）；`GatewayMessageProcessor.cs`、`SessionChannel.cs`、`ChatClientAudit.cs`、`ConfiguredSamplingChatClient.cs`、`ToolResultTextFormatter.cs`、`GatewayGuardedPlanTool.cs` 等存在数十处 `GetType().GetProperty(...)` | 工具调用 Schema 生成、参数反序列化与结果序列化会在 AOT 下失败；`GetProperty` 读取匿名对象在裁剪后易静默返回 `null`。 |
| **数据存储** | 18 个 `DbSet` 实体 + 3 张非模型 SQL 表（`OutboxSeq`、`JobEventLog`、`LocalKv`）；3 个历史迁移文件；动态组合查询、`AsSplitQuery`、`ExecuteUpdateAsync`、`ProviderModelCacheKeyFactory`、`OutboxSeqInterceptor` | EF 仅加 `.UseModel()` 无法支持动态查询翻译；`Database.MigrateAsync()` 不可直接用于 AOT；需 Phase 0 实验验证并改写动态查询与迁移器。 |
| **配置绑定与杂项** | 13 处 `.Configure<T>()` / `.Bind()`（含 [`ArtifactAuditServiceCollectionExtensions.cs:25`](../../CodexFlow.Orchestration/Services/ArtifactAudit/ArtifactAuditServiceCollectionExtensions.cs#L25)）；[`YamlConfigurationProvider.cs`](../../CodexFlow.Core/Configuration/YamlConfigurationProvider.cs) 使用 `YamlDotNet` DOM API；`SafeTreeWriter.cs` 与 `LocalDirectoryTarBuilder.cs` 共 15 处 `[DllImport]`；[`Program.cs:125`](../../CodexFlow.Local/Program.cs#L125) 开启 `ValidateOnBuild`、[`Program.cs:204`](../../CodexFlow.Local/Program.cs#L204) 使用 `IOptionsMonitor` 热更新、[`Program.cs:185`](../../CodexFlow.Local/Program.cs#L185) 注册自定义 `LocalTokenAuthenticationHandler` | 需开启 `<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>`；`ValidateOnBuild` 与 `IOptionsMonitor` 需保留并做真实解析验证；`JwtBearer` 实际走自定义 Handler，不构成复杂 JWT 反射链；`YamlDotNet` 与 `e_sqlite3` 需以真实 AOT 运行验证；部分平台存在既有功能边界（见第 6 节）。 |

---

## 4. 分阶段改造路线图（总预估：5 ~ 8.5 人周，按选项 A）

> [!IMPORTANT]
> 修正后的阶段依赖严格遵守：**Phase 0 技术探针先行 → 契约/序列化冻结 → 存储/网关按稳态边界推进 → 真实 AOT 产物与 Electron 握手验收**。在 Phase 1~3 期间仍允许局部保持 JIT 回归，不可要求在尚存 MVC/EF 阻塞时提前跑通全量 AOT 矩阵。

### Phase 0：可行性探针、依赖清理与基线冻结（1 人周）
*本阶段是硬前置门禁：不通过探针不全面铺开后续重构。*
1. **统计与样本冻结**：固化基线二进制体积、启动时间、内存占用，以及**历史样本集**（包含 3 个迁移版本的真实 `state.db` 样本、历史 `QueryRuntimeCheckpoint` JSON、历史 `KeyValueSessionStore` / `JobFailureWire` JSON 样本）。
2. **EF Core NativeAOT 最小 PoC**：在独立实验分支使用 `dotnet ef dbcontext optimize --precompile-queries --nativeaot`，对涵盖 `IJobDbContext` 接口注入、`AsSplitQuery()`、`ExecuteUpdateAsync()`、`OutboxSeqInterceptor`、`ProviderModelCacheKeyFactory` 及 `ChatService`/`BusinessPluginControlService` 查询的代表性路径执行真实 `PublishAot=true` 编译与运行验证。
   - **退出决策**：若探针验证通过且动态查询改写成本可控，锁定**决策 1 选项 A**；否则立即切换为**选项 B**（重新排期并先抽离 `IJobDbContext`）。
3. **Gateway.Http RDG 打包与消费 PoC**：验证将 Minimal APIs 端点放在 `CodexFlow.Gateway.Http` 类库中打包为 `.nupkg`，由外部测试消费端（`PublishAot=true`）引用后，端点、模型绑定与 `AddOpenApi()` 是否能正常生成并响应请求。
4. **QRE 与 Roslyn 资产排查**：在 `PublishAot=true` 探针中核实 `CodexFlow.QueryRuntime.Engine` 是否出现未处理 `IL2026`/`IL3050` 或运行时类型缺失；清理 `Storage.Sqlite` 传递进入发布目录的 `Microsoft.CodeAnalysis.CSharp.Workspaces` runtime 资产并验证资产图。

### Phase 1：序列化、反射、Tools 与基础设施清障（1.5 ~ 2.5 人周）
*(本阶段改动全部保持 JIT 兼容，每完成一组即可跑通全量 1,700+ 单元测试。)*
1. **分域迁移 `Newtonsoft.Json`（71 个生产文件）并冻结序列化契约**：
   - **严禁一刀切使用 `Web/CamelCase` 覆盖全部路径**。按以下 4 个契约域分别建立 `JsonSerializerContext` 与独立的 `JsonSerializerOptions`：
     1. **HTTP API DTO 域**（CamelCase/Web）：Gateway 请求与响应 DTO（含具名化替代后的原匿名返回类型）。
     2. **SSE 事件与 Schema 导出域**：重构 [`SseEventContractSchema.cs`](../../CodexFlow.Gateway.Http/Contracts/SseEventContractSchema.cs)（消除 `Dictionary<string, object>` 和匿名对象，改用 `JsonObject`/`JsonArray` 或完整具名模型 + `GatewaySchemaJsonContext`），保证 AOT 模式运行 `--export-contracts` 与 JIT 输出字节级一致。
     3. **持久化与历史兼容域**（保留原字段大小写、枚举策略与 null 策略）：覆盖 `JobFailureWire`（精确保留字符串枚举与忽略 null 行为）、`KeyValueSessionStore`、`QueryRuntimeCheckpoint`、`EfPlanArtifactStore`、BusinessSkills 数据库 JSON 列等，并用 Phase 0 冻结的样本验证“**旧写新读**”与回滚所需的“**新写旧读**”。
     4. **AI 工具参数与结果域**：覆盖 `CodexToolFunctionAdapterFactory.MarkedResultJsonOptions` 与 50+ 工具输入输出模型。
   - 将所有非泛型 `[JsonConverter(typeof(JsonStringEnumConverter))]` 替换为泛型 `[JsonConverter(typeof(JsonStringEnumConverter<TEnum>))]`。
2. **`AIFunctionFactory` 与显式反射清理**：
   - 为 `CodexToolFunctionAdapterFactory` 与 `GatewayMessageProcessor` 中所有 `AIFunctionFactory.Create` 传入绑定了源生成 `JsonSerializerContext` 的 `AIFunctionFactoryOptions.SerializerOptions`；新增测试比对改造前后全量工具 JSON Schema 完全等价。
   - 将 `GatewayMessageProcessor.cs`、`SessionChannel.cs`、`ChatClientAudit.cs`、`ConfiguredSamplingChatClient.cs`、`ToolResultTextFormatter.cs`、`GatewayGuardedPlanTool.cs` 中的 `GetType().GetProperty(...)` 替换为强类型接口/DTO 或模式匹配。
3. **Roslyn、配置绑定与 P/Invoke**：
   - 按决策 3 改造 `RoslynCodeAnalysisService.cs` 移除 `Assembly.Location` 引用；在所有配置扩展所在项目启用 `<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>`（包括遗漏易漏的 `ArtifactAudit`）；评估将 15 处 `[DllImport]` 迁移至 `[LibraryImport]` 并实测跨平台 `e_sqlite3` 加载。

### Phase 2：数据层与版本化 SQLite 迁移落地（1.5 ~ 2 人周，按选项 A）
1. **静态查询重构与预编译集成**：
   - 将 `ChatService.cs`、`BusinessPluginControlService.cs` 等中的动态条件拼接 `IQueryable` 改写为可被 EF Core 查询预编译支持的静态结构化查询。
   - 在构建流程中集成 `JobDbContext` 编译模型与预编译查询生成步骤，确保实体或查询源码变更时自动重生成并纳入 CI 校验。
2. **版本化 SQLite 迁移 Runner（替代 `Database.MigrateAsync()`）**：
   - 按现有 3 个迁移版本（`20261113221012_Initial`、`20261116134656_AddSessionArchiveAndExternalWorkspace`、`20261117061315_AddBusinessPluginBuilderTables`）分段导出并内嵌确定性 SQL 脚本（**必须完整包含 EF 模型之外的 `OutboxSeq`、`JobEventLog`、`LocalKv` 三张表及其索引与初始种子行**）。
   - 实现版本化迁移 runner：
     - 保持在 [`Program.cs:102`](../../CodexFlow.Local/Program.cs#L102) `LocalInstanceGuard` 实例锁之后执行（并说明其仅覆盖单宿主实例，SQLite 文件级需配合事务锁处理多进程竞争）；
     - 建表/检查完整的 `__EFMigrationsHistory` 结构（`MigrationId`、`ProductVersion`）；
     - 按序判定未应用的版本并在独立事务内执行对应增量 SQL + 写入历史记录；
     - **异常保护**：遇到重复启动（已全部应用）直接跳过；遇到失败事务自动回滚；遇到库内存在高于当前应用认知的未知 `MigrationId` 时明确拒绝启动并给出升级提示，防止破坏数据。

### Phase 3：HTTP 网关 Minimal APIs 迁移与安全等价改造（1.5 ~ 2.5 人周）
1. **逐端点迁移与安全策略逐项核对**：
   - 将 8 个 Controller（74 端点）迁移至 `CodexFlow.Gateway.Http/Endpoints/` 下的 Minimal APIs 路由组，确保开启 `CodexFlow.Gateway.Http` 内的 RDG。
   - 将 `BusinessPluginsController.BusinessSkillsFeatureGateFilter` 重构为 `IEndpointFilter`，并确认 `IOptionsMonitor<BusinessSkillsOptions>.CurrentValue` 运行期热更新行为不变。
   - 对 [`CodexGatewayController.UploadAttachment`](../../CodexFlow.Gateway.Http/Controllers/CodexGatewayController.cs#L469) 等文件上传端点：保留 nullable `IFormFile?` 参数检查、`RequestSizeLimit`、会话归属权校验与业务错误码；显式调用 `.DisableAntiforgery()` 时必须确保受 `Bearer` 认证保护。
   - **安全门控等价性清单（强制）**：逐一核对迁移后 74 个端点的认证 Scheme（`JwtBearerDefaults.AuthenticationScheme`）、授权策略（`ActiveUser` vs `ControlPlaneAuthConstants.ControlPlanePolicy`——例如 [`BusinessPluginsController.cs:62`](../../CodexFlow.Gateway.Http/Controllers/BusinessPluginsController.cs#L62) 与 [`78`](../../CodexFlow.Gateway.Http/Controllers/BusinessPluginsController.cs#L78)、[`GatewayApprovalController.cs:88`](../../CodexFlow.Gateway.Http/Controllers/GatewayApprovalController.cs#L88) 的读写权限区分）、会话/工作区资源归属校验，以及 400/401/403/404/503 错误响应体结构。
2. **精简宿主与契约生成验证**：
   - 将 [`Program.cs`](../../CodexFlow.Local/Program.cs) 切换为 `WebApplication.CreateSlimBuilder(args)`，显式验证 `ValidateOnBuild`、Kestrel loopback 绑定、`LocalTokenAuthenticationHandler`、CORS、`IOptionsMonitor` 变更回调与 `/health`。
   - 改造 [`GatewayOpenApiExtensions.cs`](../../CodexFlow.Gateway.Http/Contracts/GatewayOpenApiExtensions.cs) 支持 Minimal APIs OpenAPI 文档生成，验证生成产物中 OpenAPI 版本、`operationId`、schema 名称、`nullable`/`required` 与错误响应结构完整无漂移。

### Phase 4：全量 `PublishAot=true` 与 CI/E2E 闭环（0.5 ~ 1 人周）
1. **开启项目级 AOT 校验**：各库开启 `<IsAotCompatible>true</IsAotCompatible>` 并在 `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` 下清零告警；[`CodexFlow.Local.csproj`](../../CodexFlow.Local/CodexFlow.Local.csproj) 启用 `<PublishAot>true</PublishAot>`（使用标准 MSBuild 属性 `-p:PublishAot=true/false` 控制切换，不虚构未映射的自定义属性）。
2. **修复 CI 盲区**：
   - 更新 [`.github/workflows/client.yml`](../../.github/workflows/client.yml#L6-L19) 的触发 `paths`（纳入 `Core`、`Orchestration`、`Gateway.Http`、`Storage.Sqlite`、`Contracts` 等共享库）；在 PR 阶段增加**真实 NativeAOT 发布与进程 Smoke Job**（而不仅限于非 PR 才跑的 `package` job）。
   - 确保三平台 Runner（`win-x64` / `linux-x64` / `osx-arm64`）具备原生 C++ 链接工具链并检查 Linux 运行环境 glibc 基线。
   - 升级 [`scripts/ci/package-consumer`](../../scripts/ci/package-consumer/package-consumer.csproj)：从真实打包出的 `.nupkg` 还原，不仅 `dotnet build`，还必须执行 `dotnet publish -p:PublishAot=true` 并真实启动宿主完成至少一组路由、数据库读写与 OpenAPI 导出验证。

---

## 5. 风险矩阵与回滚边界

| 风险项 | 严重程度 | 缓解与回滚约束 |
|---|---|---|
| **EF Core 动态查询或预编译生成覆盖不全** | 高 | Phase 0 即做真实 `PublishAot=true` PoC；若动态路径无法覆盖则果断切向选项 B 仓储抽离，避免后期返工。 |
| **序列化字段变更破坏历史 Checkpoint / 会话恢复** | 高 | 持久化域保留原字段/枚举/null 契约，以 Phase 0 冻结的历史样本执行双向兼容性测试；切回 JIT **不能自动撤销已写入新库/新格式的数据**，必须在发布说明中明确版本升级与 `state.db` 自动备份策略。 |
| **Minimal APIs 迁移导致权限降级或错误码变化** | 高 | 引入逐端点鉴权与越权负向测试矩阵（无令牌、普通令牌访问控制面、跨会话访问、功能开关热切换）。 |
| **NuGet 共享库破坏性变更** | 中-高 | 通过 `package-consumer` 真实消费并 AOT 发布验证，按决策 2 选择主版本升级或提供过渡适配层。 |
| **跨平台既有边界被误判为 AOT 回归** | 中 | 验收基线须承认既有平台边界：[`DpapiBusinessCredentialStore.cs:50`](../../CodexFlow.Core/BusinessSkills/DpapiBusinessCredentialStore.cs#L50) 在非 Windows 拒绝持久化凭据、[`SafeTreeWriter.cs:105-115`](../../CodexFlow.Core/Execution/SafeTreeWriter.cs#L105-L115) 仅支持 Windows/Linux；需分别验证“既有拒绝行为保持一致”与核心通用路径跨平台可用。 |

---

## 6. 最终验收标准（Verification Plan）

**只有当以下 5 类验收全部由真实命令与真实 NativeAOT 二进制产物执行通过后，才可宣称 AOT 改造完成**（严禁仅凭 JIT 单元测试或 Electron `mock-backend.mjs` 宣称通过）：

1. **静态分析与多平台 AOT 编译**：
   - 全库开启 `IsAotCompatible` 与 `TreatWarningsAsErrors=true` 编译 0 警告 0 错误。
   - `dotnet publish CodexFlow.Local -c Release -r <rid> -p:PublishAot=true` 在 `win-x64`、`linux-x64`、`osx-arm64` 均成功产出生效可执行文件，且 `e_sqlite3` 原生库可正常加载。
2. **双模式契约与 NuGet 消费验证**：
   - 同时运行 **JIT 模式** 与 **真实 AOT 二进制** 执行 `--export-contracts`，与 [`openapi.json`](../../docs/contracts/openapi.json)、[`sse-events.schema.json`](../../docs/contracts/sse-events.schema.json) 及 `clients/desktop` `npm run generate:types` 对齐（含 OpenAPI 版本、`operationId`、schema 名称、nullable/required 与错误响应）。
   - `scripts/ci/package-consumer` 从本次 `.nupkg` 还原并完成 AOT 发布与运行冒烟。
3. **全量核心测试与隔离测试**：
   - `dotnet test CodexFlow.Core.Tests -c Release` 通过数 `>= MIN_PASSED_TESTS`（`1700`），且本地通过 `Category=DockerIsolation` 回归。
4. **真实 NativeAOT 进程全链路功能与安全验收**：
   - 直接启动 NativeAOT 产物 `CodexFlow.Local(.exe)` 验证：
     - 迁移矩阵：空库初始化、3 个历史版本 `state.db` 样本升级、重复启动、失败回滚、未知高版本拒绝；
     - 安全矩阵：401/403 负向验证（无 Token / 普通 Token 调控制面接口 / 跨会话附件访问 / `BusinessSkillsFeatureGate` 动态开关）；
     - 核心业务：YAML 分层配置、模型流式对话、50+ 工具 `AIFunction` 调用及审批挂起/恢复（`AllowOnce`/`Deny`）、`IFormFile` 附件上传、SSE 断线重连（`snapshot` + cursor）、后台任务与 OpenAPI/MCP BusinessSkill 运行；
     - 历史 JSON 样本双向兼容验证。
5. **真实 Electron 客户端联调与量化对比**：
   - **不可仅依赖使用 `mock-backend.mjs` 的 `desktop.spec.ts`**：新增/运行连接真实 NativeAOT `CodexFlow.Local` 二进制的 Electron 握手（`LocalHostRuntime.FormatReadyLine`）、Pipe Token 传递与 `ParentProcessWatcher` 退出验收，并记录改造前后体积、冷启动耗时与内存对比数据。
