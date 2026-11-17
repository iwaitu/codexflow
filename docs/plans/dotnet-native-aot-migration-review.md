# CodexFlow .NET NativeAOT 改造方案评审记录

- 日期：2026-11-17
- 评审对象：[`CodexFlow .NET NativeAOT 改造方案`](dotnet-native-aot-migration.md)
- 代码基线：`f2e9772`（仅静态只读源码核查，未执行修改工作区文件的 build / publish / 测试）
- 评审方式：Antigravity 方案起草 + Codex (`gpt-6-astra`, `codex exec --sandbox read-only`) 独立深度技术评审
- 当前结论：v1.0 判定为 **需修复**（7 项 Blocking + 6 项 Advisory）；现已完成修订稿 **v1.1** 全部采纳落地，待用户确认架构决策并启动 **Phase 0 AOT 实测探针** 与同版复审。

---

## 1. 评审范围与核心结论

本次独立评审对照了仓库实际 `.csproj`、`CodexFlow.Local/Program.cs`、`CodexFlow.Gateway.Http`、`CodexFlow.Orchestration`、`CodexFlow.Storage.Sqlite`、`CodexFlow.Core`、`scripts/ci/package-consumer` 及 `.github/workflows/ci.yml`、`client.yml` 等全部相关代码与流水线定义。

- **v1.0 结论（需修复）**：方向合理，但原稿存在 EF 查询预编译缺失、SQLite 迁移脚本可执行性不足、共享 NuGet 包 RDG 与 API 兼容性边界未闭合、持久化/SSE Schema 序列化契约遗漏、网关安全等价门槛缺失、CI/E2E 验证存在假象以及基线统计/工期算术误差等阻塞问题。
- **v1.1 修订状态**：已逐条核实上述全部代码证据并修订方案文档；当前 v1.1 **尚未执行实际 `dotnet publish -p:PublishAot=true` 编译实验与同版复审**，不可直接作为已获最终批准的无风险施工单。

---

## 2. Blocking 问题与处理详情

| 编号 | 评审发现（精确代码证据） | v1.1 处理与定位 |
|---|---|---|
| **B1** | **EF 路线把编译模型误当成足够的 AOT 执行基础**：`ChatService.cs:273`、`BusinessPluginControlService.cs:744` 动态拼接 `IQueryable`，`IJobDbContext.cs:21` 通过接口暴露 `DbSet`，仅 `.UseModel(...)` 无法支持动态查询翻译，缺少 `--precompile-queries --nativeaot` 验证。 | **已采纳**；调整第 2 节决策 1 选项 A 与第 4 节 Phase 0 / Phase 2，将 EF Core 预编译查询最小 PoC 前移为 Phase 0 硬前置门禁，要求改写动态拼接 `IQueryable` 并把总预估调高至 `5 ~ 8.5` 人周。 |
| **B2** | **去 EF 选项 B 未切断真实依赖边界**：`IJobDbContext.cs:23-48` 暴露 `DbSet`/`DatabaseFacade`/`ChangeTracker`/`EntityEntry`，`BusinessPluginControlService.cs:68` 直接注入该接口且 `Orchestration.csproj:22` 引用 `EntityFrameworkCore.Relational`，仅替换 3 个 Store 无法去 EF。 | **已采纳**；修正决策 1 选项 B，明确须先抽离 `Orchestration` 内所有消费 `IJobDbContext` 的服务为纯仓储/工作单元接口，并重新评估额外工期为 `2.5 ~ 4` 人周。 |
| **B3** | **SQLite 迁移方案不可执行且遗漏模型外 3 张表**：SQLite 不支持 EF 通用幂等脚本；`20261113221012_Initial.cs:300-324` 还建有 `OutboxSeq`、`JobEventLog`、`LocalKv` 三张模型外原生表；且缺乏重复启动/断电/未知高版本处理及历史表兼容说明。 | **已采纳**；重构 Phase 2 第 2 条与第 6 节验收矩阵，改为“按迁移版本分段生成 SQL + 版本化 runner 维护完整 `__EFMigrationsHistory`”，明确覆盖模型外 3 张表、事务回滚与未知高版本拒绝策略。 |
| **B4** | **Minimal APIs 位于 NuGet 类库包的 RDG 与 API 兼容缺口**：`CodexFlow.Gateway.Http` 是类库包且 `scripts/ci/package-consumer/Program.cs:9` 直接引用 `CodexGatewayController`；RDG 须在包含 `Map*` 调用的程序集编译时生效。 | **已采纳**；重构第 2 节决策 2，明确 RDG 归属 `CodexFlow.Gateway.Http` 编译期生成，给出破坏性升级 vs 共享 Handler 双适配层的选项权衡，并在 Phase 0 / Phase 4 强制通过真实 `.nupkg` 的 AOT 发布消费测试。 |
| **B5** | **序列化改造遗漏历史数据兼容与 SSE Schema 独立路径**：`JobFailureWire.cs:13`（非驼峰/字符串枚举）、`KeyValueSessionStore.cs`、`QueryRuntimeCheckpoint.cs:170` 涉及历史 JSON 读写；`SseEventContractSchema.cs:248-320` 使用 `Dictionary<string, object>` 和匿名对象生成 schema。 | **已采纳**；在 Phase 1 拆分为 4 个独立序列化契约域（禁止一刀切 CamelCase），将 `SseEventContractSchema.cs` 纳入改造，并在第 6 节要求历史样本“旧写新读 / 新写旧读”与双模式 `--export-contracts` 校验。 |
| **B6** | **网关迁移缺少安全等价性门槛**：74 个端点存在 `ActiveUser` 与 `ControlPlanePolicy` 权限分层（如 `BusinessPluginsController.cs:62` vs `78`、`GatewayApprovalController.cs:88`）、`IOptionsMonitor` 动态门控、资源归属与 `IFormFile` 错误响应细节。 | **已采纳**；在 Phase 3 与第 6 节补充逐端点认证/策略/归属/热更新/400-401-403-503 安全等价校验清单与负向测试用例要求。 |
| **B7** | **现有 CI/E2E 会产生“AOT 验证通过”假象**：`ci.yml:78` 的 consumer 仅做 `dotnet build`、`desktop.spec.ts:26-32` 使用 `mock-backend.mjs`、`client.yml` 路径过滤未覆盖共享库且 PR 不跑 `package` job；v1.0 中 `EnableNativeAot=false` 属未定义自定义属性。 | **已采纳**；在 Phase 4 与第 6 节要求增设 PR 真实 AOT 发布 Smoke Job、扩展 `client.yml` paths、升级 `package-consumer` 运行真实 AOT 宿主、要求真实 Electron 与 AOT 后端握手测试，并改用标准 `-p:PublishAot=true/false`。 |

---

## 3. Advisory 建议与处理详情

| 编号 | 评审建议 | v1.1 处理情况 |
|---|---|---|
| **A1** | **修正基线统计与工期算术**：排除 `bin/obj` 后生产文件为 `510` 个（115,826 行）、测试 `190` 个（47,393 行）；包含 `Newtonsoft.Json`（含完全限定名）的生产文件实际为 **71** 个（原仅搜 `using` 漏算为 28）；匿名返回超 10 处；`[DllImport]` 共 15 处；工期总和应校准而非 3.5~5 周。 | **已采纳**；全面更新第 1.1 节、第 3 节、第 4 节全部统计数据，将按选项 A 的总预估工期修正为 `5 ~ 8.5` 人周。 |
| **A2** | **调整阶段依赖**：Phase 0 先完成探针、历史样本冻结与 EF/RDG 验证，不可要求前阶段尚未解除阻塞时就跑通全量 AOT 矩阵。 | **已采纳**；重写 Phase 0 为硬前置门禁，并调整第 4 节与第 6 节验收门槛说明。 |
| **A3** | **准确区分非阻塞核查项**：DI `ValidateOnBuild`、`IOptionsMonitor`、`HttpClientFactory`、`LocalTokenAuthenticationHandler`（并非完整 JWT 反射链）、EF 字符串 JSON 列、`RegexOptions.Compiled`、`InternalsVisibleTo` 不直接定性为独立阻塞；补全 `ArtifactAuditServiceCollectionExtensions.cs:25` 配置绑定。 | **已采纳**；更新第 3 节审计表（配置绑定更新为 13 处并准确标注各机制角色）。 |
| **A4** | **准确表述 OpenAPI**：.NET 10 `Microsoft.AspNetCore.OpenApi` 支持 Trimming/AOT，限制主要来自 MVC 路径与 `GenerateContractsAsync` 精简宿主推断，应固定 `operationId`、schema 名称、nullable 等细节校验。 | **已采纳**；更新第 3 节、Phase 3 与第 6 节验收标准。 |
| **A5** | **精准表述 Roslyn 与 `PrivateAssets=all`**：`RoslynCodeAnalysisService.cs:38` 会在第 61 行捕获 `ArgumentException` 返回空诊断而非进程崩溃；`PrivateAssets=all` 不等同排除当前项目 runtime 资产；外部 `dotnet build` 方案需注意不可信项目安全边界。 | **已采纳**；更新第 2 节决策 3，明确上述精准语义与安全边界要求。 |
| **A6** | **承认跨平台既有能力边界与原生库验证**：`DpapiBusinessCredentialStore.cs:50` 非 Windows 拒绝持久化、`SafeTreeWriter.cs:105-115` 仅支持 Windows/Linux；`e_sqlite3` 原生加载与 Linux 基线须实机验证；勿将 `IsAotCompatible` 笼统视作运行免检保证。 | **已采纳**；补充第 1.2 节与第 5 节跨平台验收边界说明。 |

---

## 4. 尚未验证事项与下一步建议

1. **未执行实际 AOT 编译**：截至本评审完成，未修改工作区代码，也未实际运行 `dotnet publish -p:PublishAot=true`、`dotnet ef dbcontext optimize --precompile-queries --nativeaot` 或任何 AOT 进程测试。
2. **建议下一步动作**：
   - 用户确认第 2 节中的 **决策 1（先做选项 A 探针还是直接走选项 B）**、**决策 2（NuGet 共享库破坏性升级 vs 双适配层）**、**决策 3（Roslyn 处置策略）**；
   - 批准后先启动 **Phase 0（1 人周）** 最小实验分支，拿到真实的 EF Precompiled Queries、RDG、QRE AOT 编译链接与二进制体积对比数据，再锁定后续阶段投入。
