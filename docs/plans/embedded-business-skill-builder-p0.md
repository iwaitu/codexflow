# 内置企业插件与业务 Skill 构建器 —— P0 契约与技术验证冻结基线

- 日期：2026-11-17
- 上游设计：[embedded-business-skill-builder.md](embedded-business-skill-builder.md)（v1.1 候选稿 §1.1、§3、§6、§7、§8.2、§11、§13）
- 适用阶段：作为后续 P1（公共基础与存储）、P2（OpenAPI/MCP 适配器）、P3（Skill 生成与受限执行）、P4（桌面端界面）的共同契约基线

---

## 1. 锁定的 SDK、协议与兼容矩阵

### 1.1 NuGet 依赖与运行时版本（已实测还原并全解决方案编译通过）

| 能力域 | 锁定包 / 传递解析版本 | 关键兼容结论与约束 |
|---|---|---|
| OpenAPI JSON/YAML 解析 | `Microsoft.OpenApi 2.13.0`<br>`Microsoft.OpenApi.YamlReader 2.13.0` | 与 [CodexFlow.Gateway.Http.csproj](../CodexFlow.Gateway.Http/CodexFlow.Gateway.Http.csproj) 使用的 `Microsoft.AspNetCore.OpenApi 10.0.15`（约束 `[2.13.0, 3.0.0)`）完全同系锁定，避免升级 `3.x` 引起的钻石依赖冲突。禁用外部 `$ref`（`LoadExternalRefs = false`），YAML 限制 `MaxDepth = 64`、`MaxInputByteCount = 10 MiB`。 |
| 远程 MCP 客户端 | `ModelContextProtocol.Core 2.4.0` | 传递解析 `Microsoft.Extensions.AI.Abstractions 10.11.0`，已通过解决方案构建验证与 `VllmChatClient 2.0.39`、`CodexFlow.QueryRuntime.* 0.26.2` 兼容。测试工程使用 `ModelContextProtocol.AspNetCore 2.4.0` 构建真实 Kestrel Streamable HTTP 测试服务端。 |
| MCP 协议握手锁定 | `McpClientOptions.ProtocolVersion = "2025-11-25"`<br>`HttpTransportMode.StreamableHttp` | `ModelContextProtocol.Core 2.4.0` 默认偏好 `2026-07-28`（无 `initialize` 握手与 `Mcp-Session-Id`）；P0 强制锁定 `2025-11-25` 且禁止静默降级至 legacy HTTP+SSE；服务端返回不匹配协议版本时抛出协商失败并统一归类为 `UnsupportedProtocol`。 |
| 模型结构化输出 | `Microsoft.Extensions.AI 10.10.0` / `10.11.0` (`ChatResponseFormat.ForJsonSchema`) | 对齐 `DefaultCodexPlanner` 已验证约束：向模型请求结构化 `SkillDefinitionV1` 候选时使用 `SkillSchemaProvider.StructuredOutputSchema`（保持 `object` 根、`additionalProperties: false`，移除 `$schema` 与 `minItems`/`maxItems`），完整校验使用 `SkillSchemaProvider.CanonicalValidationSchema`。 |

### 1.2 冻结协议兼容矩阵与默认不变量

见代码契约 [BusinessSkillProtocolMatrix.cs](../../CodexFlow.Contracts/BusinessSkills/BusinessSkillProtocolMatrix.cs)：

- **OpenAPI 支持**：Swagger `2.0`、OpenAPI `3.0.x`、OpenAPI `3.1.x`（JSON 与 YAML）。
- **OpenAPI 不支持项**：OpenAPI `3.2.x`（前置版本探针拦截返回 `UnsupportedProtocol`）；HTML 登录页（识别返回 `AuthRequired`）；`multipart/form-data`、`application/octet-stream`、`application/x-www-form-urlencoded`、Callback/Webhook、顶层 `oneOf`/`anyOf` Schema 标记单条 `Unsupported` 而保留同文档其余可用操作。
- **MCP 支持**：远程 `StreamableHttp`（`2025-11-25`），支持 `tools/list` 游标分页（带循环游标检测和重复工具名检测）、`tools/call`（区分传输错误、`isError=true`、`StructuredContent` 与文本内容）。
- **MCP 不支持项**：`2026-07-28` 原生模式、`LegacyHttpSse`、`Stdio` 本地拉起，以及 `sampling`/`roots`/`elicitation` 客户端回调能力。
- **能力风险默认规则**：OpenAPI HTTP 方法与 MCP ToolAnnotations 仅记录为 `SuggestedSourceRiskHint`，新发现能力 `Risk` 一律默认 `Unknown`，经控制面审核确认后才能标记 `Read`。

---

## 2. 冻结契约模型（`CodexFlow.Contracts/BusinessSkills`）

`CodexFlow.Contracts` 继续保持 **零外部 NuGet 依赖**，已冻结以下契约文件：

1. [BusinessSkillProtocolMatrix.cs](../../CodexFlow.Contracts/BusinessSkills/BusinessSkillProtocolMatrix.cs)：枚举（`BusinessPluginSourceType`、`BusinessAuthKind`、`CapabilityRiskLevel`、`CapabilityExecutabilityStatus`、`SkillRunStatus`、`SkillRunStepState`、`PluginOperationStatus`）、第 12 节限额常量（`BusinessSkillDefaults`）及兼容矩阵（`ProtocolSupportMatrixDto`）。
2. [BusinessPluginContracts.cs](../../CodexFlow.Contracts/BusinessSkills/BusinessPluginContracts.cs)：
   - 13 个规范错误码 `BusinessSkillErrorCodes`（`AuthRequired`, `ConnectionUnavailable`, `UnsupportedProtocol`, `UnsupportedSchema`, `CatalogIncomplete`, `CatalogChanged`, `SelectionViolation`, `ValidationFailed`, `ApprovalRequired`, `ApprovalExpired`, `BudgetExceeded`, `PartialData`, `OutcomeUnknown`）与统一错误结构 `BusinessSkillErrorDto`；
   - `BusinessPluginDto`、`PluginConnectionDto`、`EndpointPolicyDto`、`CredentialRefSummaryDto`（仅引用与掩码，不含明文密钥）；
   - `CatalogSnapshotDto`、`CatalogDiffDto`、`CapabilitySummaryDto`、`CapabilityDetailDto`、`CapabilitySelectionDto`；
   - 异步操作受理/查询模型 `PluginOperationAcceptedDto`、`PluginOperationStatusDto` 与 §11 规范路由表 `BusinessPluginApiRoutes`。
3. [BusinessSkillDefinitionContracts.cs](../../CodexFlow.Contracts/BusinessSkills/BusinessSkillDefinitionContracts.cs)：
   - `SkillDefinitionV1` 顶层契约、`SkillValueBinding` 结构化值绑定、以及严格限定的 7 类多态步骤：`tool`、`transform`（含 `Filter`, `Sort`, `Project`, `Aggregate`, `InnerJoin`, `LeftJoin`, `Lookup`）、`branch`、`foreach`、`generateText`、`approval`、`output`。
4. [BusinessApprovalContracts.cs](../../CodexFlow.Contracts/BusinessSkills/BusinessApprovalContracts.cs)：
   - `BusinessApprovalSummaryDto` 与 `BusinessApprovalDigestInput.ComputeRequestDigest()`；
   - 草稿 `SkillDraftDto`、发布版本 `PublishedSkillDto` 与持久化运行 `SkillRunDto` / `SkillRunStepDto`。

---

## 3. §11 本地 API 路由、审批与桌面 IPC 核对结果

| 区域 | 核对对象 | 核对结论 |
|---|---|---|
| 控制面与 Gateway 路由 | [GatewayApprovalController.cs](../../CodexFlow.Gateway.Http/Controllers/GatewayApprovalController.cs)<br>[ExecutionSettingsController.cs](../../CodexFlow.Gateway.Http/Controllers/ExecutionSettingsController.cs)<br>[BackgroundJobController.cs](../../CodexFlow.Gateway.Http/Controllers/BackgroundJobController.cs)<br>[CodexGatewayController.cs](../../CodexFlow.Gateway.Http/Controllers/CodexGatewayController.cs) | `BusinessPluginApiRoutes.CanonicalRoutes`（21 条端点，含 §11 拆分的 `GET /api/skills` 与 `GET /api/skills/{id}`）除明确复用的 `POST /api/gateway/approvals/{approvalId}` 外，均与现有 Gateway 路由零冲突。 |
| 写入审批扩展点 | [ExecutionApprovalContracts.cs](../../CodexFlow.Core/Execution/Approval/ExecutionApprovalContracts.cs)<br>[ExecutionApprovalBroker.cs](../../CodexFlow.Core/Execution/Approval/ExecutionApprovalBroker.cs) | 现有 `PendingApprovalItem` 面向本地命令行执行；企业写入采用 `Kind = "business_skill_write"` 与 `BusinessApprovalSummaryDto`，通过 `BusinessApprovalDigestInput.ComputeRequestDigest()` 绑定 `runId`/`skillContentHash`/`connectionRevision`/`credentialRevision`/`capabilityId`/`catalogSnapshotHash`/`normalizedArgumentsJson`/`batchItemIds`/`expiresAtUtc`，首版 `AllowedDecisions` 固定为 `["deny", "allow_once"]`，严禁 `allow_session`/`allow_always`。 |
| 桌面端安全 IPC | [ipc.ts](../../clients/desktop/src/shared/ipc.ts) | `DesktopBridge` 已有受控 `ControlPlaneCallResult<T>` 代理与 `approveToolExecution` 等方法；P1/P4 仅按类型化能力追加插件连接凭据设置、插件授权/停用、能力选择冻结、草稿生成/发布及 Skill 正式运行启动 IPC，不向渲染进程暴露通用 HTTP 代理。 |

---

## 4. 应收账款催收多结果集关联 P0 样例

完整样例位于 [ReceivableCollectionP0SampleTests.cs](../../CodexFlow.Core.Tests/BusinessSkills/ReceivableCollectionP0SampleTests.cs)（`CreateReceivableCollectionSample()`），遵循设计文档第 2.1 节与第 7.2 节规范：

1. **批量分页拉取**：依次通过 `fetch_customers`（page 模式）、`fetch_invoices`（cursor 模式）、`fetch_repayments`（cursor 模式）拉取数据；
2. **先聚合再关联**：使用 `aggregate_repayments_by_invoice`（`GroupBy: ["invoiceId", "currency"]`，`sum(amount)`），再经 `join_invoice_with_repayments`（`LeftJoin`，`DuplicateKeyPolicy = RequirePreAggregation`，`MissingMatchPolicy = "zero_or_empty"`）与 `lookup_customer_on_invoice`（`Lookup` 客户名）合并，从结构上杜绝 1:N 回款明细直接 Join 导致的账款金额倍增与逐客户 N+1 请求；
3. **十进制与严格边界**：严格按 `overdueDays > 30`、`unpaidAmount > 50000` 与币种一致性过滤，按 `unpaidAmount` 降序排序；
4. **字段白名单与批量审批串行写入**：`generateText` 仅允许传入 `["customerName", "invoiceId", "unpaidAmount", "currency", "overdueDays"]`；`approval` 节点生成批次摘要，经人工批准后由 `foreach`（`Sequential = true`，`MaxItems <= 100`）逐条写入并校验创建 ID 证据。

---

## 5. P0 实测验证结果

- **解决方案编译**：`dotnet build CodexFlow.slnx --no-restore` 编译通过（0 错误），全新增 `BusinessSkills` 模块与 `CodexFlow.Gateway.Http`、`CodexFlow.Orchestration`、`CodexFlow.Local` 无依赖或分析器冲突。
- **P0 专项测试**：`dotnet test CodexFlow.Core.Tests/CodexFlow.Core.Tests.csproj --no-build --filter "FullyQualifiedName~BusinessSkills"` 通过 **13 / 13**（失败 0，跳过 0）：
  - [OpenApiP0ValidationTests.cs](../../CodexFlow.Core.Tests/BusinessSkills/OpenApiP0ValidationTests.cs)：Swagger 2.0 JSON、OpenAPI 3.0.3 YAML（参数继承与不支持项隔离）、OpenAPI 3.1.0 JSON、`oneOf`/`multipart` 单条拦截、OpenAPI 3.2.0 与 HTML 登录页拒绝、本地 Kestrel 样例 ERP 真实 GET/POST 发现与调用全部通过；
  - [McpP0ValidationTests.cs](../../CodexFlow.Core.Tests/BusinessSkills/McpP0ValidationTests.cs)：`ModelContextProtocol.AspNetCore 2.4.0` 真实 Kestrel Streamable HTTP (`2025-11-25`) 握手协商、跨游标分页、结构化输出、`isError=true` 归类、循环游标标记不完整、服务端协议降级至 `2024-11-05` 时拒收并归类为 `UnsupportedProtocol` 全部通过；
  - [ReceivableCollectionP0SampleTests.cs](../../CodexFlow.Core.Tests/BusinessSkills/ReceivableCollectionP0SampleTests.cs)：应收账款催收先聚合回款再关联账款与客户的完整定义通过静态校验与规范化哈希稳定性检验，`StructuredOutputSchema` 成功通过 `ChatResponseFormat.ForJsonSchema` 构造检查，边界数据基准测试验证 30 天 / 5 万元边界与多笔回款无倍增；
  - [BusinessApprovalAndContractReconciliationTests.cs](../../CodexFlow.Core.Tests/BusinessSkills/BusinessApprovalAndContractReconciliationTests.cs)：审批摘要防篡改摘要验证、13 项错误码与兼容矩阵完整性检查、以及 §11 新增路由与现有 `CodexFlow.Gateway.Http` 控制器零冲突验证全部通过。
- **既有回归范围**：联合执行 `PlannerTests`、`ExecutionApprovalAndPermissionTests`、`ControlPlaneAndSelfApprovalTests` 与 `BusinessSkills` 共计 **70 / 70** 通过（失败 0，跳过 0）。
- **明确未验证范围**：尚未接入真实客户企业系统（P2/P5 联调验证）；尚未在真实外部 LLM 服务上发出在线生成调用；P1 数据库迁移与 P4 客户端 UI 尚未开始实施。
