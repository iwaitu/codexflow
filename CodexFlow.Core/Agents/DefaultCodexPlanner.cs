// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.VllmChatClient.Kimi;
using Microsoft.Extensions.Logging;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Core.Planning.SelfCorrection;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Linq;
using SystemTextJsonDocument = System.Text.Json.JsonDocument;
using SystemTextJsonElement = System.Text.Json.JsonElement;

namespace CodexFlow.Core.Agents;

public class DefaultCodexPlanner : ICodexPlanner
{
    private static readonly Lazy<SystemTextJsonElement> PlanResponseJsonSchema = new(CreatePlanResponseJsonSchema);
    private static readonly Lazy<SystemTextJsonElement> PlanStructuredOutputJsonSchema = new(CreatePlanStructuredOutputJsonSchema);

    private readonly IChatClient _chatClient;
    private readonly ILogger<DefaultCodexPlanner> _logger;
    private readonly ILLMExecutor? _llmExecutor;
    private readonly IQueryRuntimeEngine? _queryRuntimeEngine;
    private readonly IPlannerSummaryPublisher? _summaryPublisher;
    private readonly IPlanContractLinter? _planContractLinter;

    public DefaultCodexPlanner(
        IChatClient chatClient,
        ILogger<DefaultCodexPlanner> logger,
        ILLMExecutor? llmExecutor = null,
        IQueryRuntimeEngine? queryRuntimeEngine = null,
        IPlannerSummaryPublisher? summaryPublisher = null,
        IPlanContractLinter? planContractLinter = null)
    {
        _chatClient = chatClient;
        _logger = logger;
        _llmExecutor = llmExecutor;
        _queryRuntimeEngine = queryRuntimeEngine;
        _summaryPublisher = summaryPublisher;
        _planContractLinter = planContractLinter;
    }

    public async Task<List<CodexTask>> GeneratePlanAsync(CodexSession session, string goal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(goal);

        await PublishSummaryAsync(
            session,
            new PlannerSummaryUpdate
            {
                Kind = PlannerSummaryKind.Started,
                Message = "规划已启动，正在结合项目上下文生成任务清单。",
                Phase = "planning_start"
            },
            ct).ConfigureAwait(false);

        var activeFacts = session.ActiveFacts;

        var dependencySummary = activeFacts.FirstOrDefault(f => f.Key == "DependencyGraphSummary")?.Value
                                ?? "未进行语义依赖扫描。";

        var archAudit = activeFacts.FirstOrDefault(f => f.Key == "ArchitectureAudit")?.Value
                                ?? "未检测到显著架构债务。";

        var projectMode = session.ProjectUrl == null ? "新建项目 (Greenfield)" : "已有项目 (Brownfield)";

        var draftSystemPrompt = $$"""
你是一个专业的软件架构师。请先进行开放式任务规划，不需要输出 JSON。
目标是在最终结构化任务清单生成前，先充分分析项目背景、风险、依赖顺序和任务合并策略。

输出要求：
- 使用简体中文。
- 输出自然语言规划草稿，可以包含分点、风险说明、任务合并理由和执行顺序建议。
- 不要输出 JSON，不要使用 markdown 代码块，不要调用工具。
- 重点说明哪些工作应合并成一个任务，哪些必须拆分，哪些文件/模块属于高风险范围。
- 需求里已经写明的事实（宿主已预取哪些包、目标版本、用户已做的决定）直接当作已知条件使用：不要为“确认”这些事实单独安排任务，也不要写“事实未确认则不得修改”；任务内部也不要写“缺包/不可用就停止、等待”这类终止条件（缺包时仍然修改并如实报告）。执行者无法查询宿主状态；这些事实是否成立，由宿主随后的确定性检查（如离线还原预检）判定，不需要计划里再设一道关卡。
- 每个任务的描述必须自包含：执行者看不到规划时的对话，所以不要写“按前述清单”“上表中的包”这类指向任务之外的引用，要把具体的包名、版本、文件直接写进任务本身。

[项目模式]
{{projectMode}}

[项目背景]
{{session.ProjectSummary}}

[工程依赖风险预警 (Criticality Map)]
{{dependencySummary}}

[架构审计发现 (Level 5 Architecture Audit)]
{{archAudit}}

[当前阶段]
{{session.CurrentStage}}

[最终目标]
{{goal}}
""";

        var systemPrompt = $$"""
你是一个专业的软件架构师，擅长以最少的步骤高效解决问题。
请根据项目背景和用户目标，生成一份精简高效的任务规划。
你的输出必须是且仅是一个纯 JSON 对象，形如 {"tasks":[...]}（任务数组放在 tasks 字段里），不包含任何解释性文字、代码块包装或工具调用。
禁止输出任何非 JSON 内容。第一个字符必须是 {，最后一个字符必须是 }。
**重要规则：无论用户输入何种语言，生成的任务 Title 和 Description 必须强制使用中文（简体）。**

[项目模式]
{{projectMode}}

[项目背景]
{{session.ProjectSummary}}

[工程依赖风险预警 (Criticality Map)]
{{dependencySummary}}

[架构审计发现 (Level 5 Architecture Audit)]
{{archAudit}}

[当前阶段]
{{session.CurrentStage}}

[最终目标]
{{goal}}

━━━ 任务精简原则（最高优先级） ━━━
⚠️ 用最少的任务完成目标。每个任务应尽可能多地包含相关工作，而非拆成多个小步骤。
- **合并同类项**：如果多个文件需要类似的修改（如多个类需要提取接口、多个配置文件需要统一格式），合并为一个任务。
- **读改合一**：禁止为"读取文件"单独创建任务。读取是修改任务的内置步骤，在 Description 中注明"先读取 X 了解现有结构，然后修改 Y"即可。
- **禁止冗余任务**：不得生成仅做"检查"、"审查"、"验证结构"的独立任务，这些由系统的 Sentry/Guard 自动完成。
- **数量动态裁定**：
  - 简单修复/重构（如提取接口、调整依赖方向、修复命名）：1-3 个任务
  - 中等功能开发（如新增 CRUD、API 端点）：3-6 个任务
  - 大型功能或跨模块重构：6-10 个任务
  - **绝对上限 10 个**，超过则必须合并

项目模式专属规则：
- 新建项目 (Greenfield)：
  1. **脚手架先行**：第一个任务必须是执行脚手架命令。
     - **C#**: `dotnet new webapi/console -n [Name]`
     - **Python**: `mkdir [Name] && cd [Name] && python -m venv .venv`
     - **Node/TS**: `mkdir [Name] && cd [Name] && npm init -y`
  2. **结构化创建**：第二个任务应使用 `create_directory` 和 `write_file` 建立标准目录结构（如 Controllers/, Models/, Services/）。
  3. **严禁空查**：禁止生成任何仅为了"查看空目录"的任务。

- 已有项目 (Brownfield)：
  1. **模式对齐**：修改任务必须遵循项目中已有的命名和架构模式，在 Description 中说明需先读取哪些文件以了解现有模式。
  2. **高风险标注**：修改 Top Critical Files 的任务必须标记 RiskLevel: High。

通用规则：
1. 每个任务必须包含：Id, Title, Description, Dependencies, StageId, RiskLevel (Low/Medium/High), ComplexityLevel (1/2/3)，以及 ChecklistItems。
1.1. 每个任务必须额外包含 TaskType，且只能为 `code` 或 `analysis`。
1.2. 只有真正需要修改代码/配置/测试/脚手架的任务才能标记为 `code`；纯分析、盘点、识别、阅读现状类任务必须合并进代码任务描述中。只有在确实无法合并时，才允许保留 `analysis` 任务。
1.3. **Description 必须包含完整的执行蓝图**，格式如下：
     ```
     ## 阶段目标
     [本任务要达成的具体目标，一句话描述]

     ## 阶段范围
     [明确界定本任务的边界，哪些做、哪些不做]

     ## 关键任务
     - [具体的子任务列表，按执行顺序排列]
     - [每个子任务用动词开头，如：创建、修改、配置、验证]

     ## 任务执行顺序
     1. [第一步：具体操作 + 预期产出]
     2. [第二步：具体操作 + 预期产出]
     ...

     ## 涉及模块或服务
     - [列出本任务会涉及的具体模块/服务名称]

     ## 主要技术改动点
     - [列出关键的技术变更，如：新增接口、修改数据模型、调整配置等]

     ## 影响范围
     - 代码目录：[预计修改的文件路径或目录]
     - 服务：[受影响的服务名称]
     - 数据表：[受影响的数据库表（如适用）]
     注意：如果无法精确确定，请基于项目背景写出合理假设。
     ```
1.4. **ChecklistItems 必须是结构化子步骤清单**，用于后续增量验证与增量修复：
   - 每个 ChecklistItem 必须包含 `Id`、`Text`、`Status`
   - `Status` 在规划阶段统一填 `Pending`
   - ChecklistItems 应与 `## 任务执行顺序` 对齐，但必须更原子、可追踪、可单独判定完成/失败
   - 若任务涉及 build/test，必须把 `执行 dotnet build` / `执行 dotnet test` 作为独立 checklist item
   - 若任务涉及迁移/移动/删除，必须显式包含“创建新位置 / 更新引用 / 删除旧位置 / 构建验证 / 测试验证”这类 checklist item
2. 效能分级规则 (Adaptive Efficiency)：
   - Level 1 (微创/文档/脚手架): 仅创建模板、文档或配置。跳过 TDD/AST。
   - Level 2 (常规功能): 标准业务逻辑开发。并行 TDD + 影子执行。
   - Level 3 (核心重构): 修改核心架构、公共接口或高 RiskLevel 任务。强制开启 AST 强校验与架构准入。
3. 依赖一致性：脚手架/基础设施任务排在功能开发任务之前。
4. 结构化验收契约（BUG-002 fix）：每个任务必须声明 RequiredArtifacts 和 ForbiddenStates：
   - RequiredArtifacts: 列出任务完成时必须满足的文件状态断言。每个断言包含 Type（file_exists/file_not_exists/file_contains/file_not_contains）、Path（相对于项目根目录的路径）、Text（仅 file_contains/file_not_contains 需要）。
   - ForbiddenStates: 列出任务完成时绝对不能出现的状态（同格式）。
   - 迁移/移动类任务至少声明：新位置 file_exists + 旧位置 file_not_exists + 引用迁移 file_not_contains
   - 新增/创建类任务至少声明：目标文件 file_exists
   - 修改/更新类任务至少声明：目标文件 file_contains（包含新增/修改的关键内容）
   - 覆盖性规则：凡是在 `## 阶段范围`、`## 关键任务`、`## 任务执行顺序`、`## 影响范围` 中明确提到会被修改的文件路径，至少要在 RequiredArtifacts 或 ForbiddenStates 中出现一次，不能遗漏 `Program.cs`、`*.csproj`、旧位置路径、新位置路径。
   - 若任务修改 `Program.cs` 且涉及依赖注入/配置注册，必须为 `Program.cs` 声明至少一条 `file_contains` 断言，`Text` 必须是要注册的关键接口名、实现类名或配置节名称。
   - 若任务修改 `*.csproj` 且涉及添加/移除 `ProjectReference` 或 `PackageReference`，必须为对应 `.csproj` 声明 `file_contains` 或 `file_not_contains` 断言，`Text` 必须是被添加/移除的项目名或包名。
   - 若任务描述包含“从 A 层移至 B 层 / 迁移至 / 上移至 / 下沉到”等迁移表达，旧位置不能再保留 `file_exists` 或 `file_contains` 断言；旧位置应转为 `file_not_exists` 或至少 `file_not_contains`。
   - 若任务明确声明泛型接口（例如 `IFileRepository<TEntity>`、`IRepository<T>`），必须为对应接口文件声明至少一条 `file_contains` 断言，`Text` 至少包含 `interface Xxx<`，禁止只校验文件存在。
   - 若任务在 Core 层新增接口、在 Infrastructure 层新增实现，则必须为对应的 Infrastructure `*.csproj` 声明 `file_contains` 断言，`Text` 必须是 Core 项目名（例如 `CleanApp.Core`）。
   - 对外部网关/存储专用服务（如名称或职责明显属于 `Mongo`、`Redis`、`GridFS`、`Blob`、`Bucket`、`Client`、`Gateway`），除非任务明确要求其承担元数据/领域仓储职责，否则不得仅因“统一抽象”而生成“该服务应包含 `I*Repository`”之类断言。
5. UnsafeIfDependencyFallbackPassed: 高风险（RiskLevel=High）或核心重构任务必须设为 true，低风险脚手架任务可设为 false。

### 工具调用规范 (严格执行)
所有参数必须放在顶级 JSON 中。**禁止**使用 `{ "args": { ... } }`。

### JSON 模型示例
[
  {
    "Id": "TASK_INIT_001",
    "TaskType": "code",
    "Title": "[Scaffold] 项目脚手架初始化",
    "Description": "## 阶段目标\n创建 ASP.NET Core WebAPI 项目骨架，建立标准目录结构。\n\n## 阶段范围\n仅负责项目初始化与目录创建，不涉及业务代码编写。\n\n## 关键任务\n- 执行 dotnet new webapi 创建项目\n- 创建 Controllers/Models/Services 目录\n- 添加基础配置文件\n\n## 任务执行顺序\n1. 执行 `dotnet new webapi -n MyApi` → 生成项目骨架\n2. 创建 Controllers、Models、Services 目录 → 完成分层结构\n3. 添加 appsettings.Development.json → 完成环境配置\n\n## 涉及模块或服务\n- 项目根目录\n- 配置系统\n\n## 主要技术改动点\n- 创建 .csproj 项目文件\n- 初始化 Program.cs 入口\n- 配置 Kestrel 服务器\n\n## 影响范围\n- 代码目录：/MyApi/*, /MyApi/Controllers, /MyApi/Models, /MyApi/Services\n- 服务：无（仅脚手架）\n- 数据表：无",
    "Dependencies": [],
    "RequiredArtifacts": [
      { "Type": "file_exists", "Path": "MyApi/Program.cs" },
      { "Type": "file_exists", "Path": "MyApi/MyApi.csproj" }
    ],
    "ForbiddenStates": [],
    "ForbiddenPaths": [],
    "ChecklistItems": [
      { "Id": "CHK_001", "Text": "执行 `dotnet new webapi -n MyApi` 创建项目骨架", "Status": "Pending" },
      { "Id": "CHK_002", "Text": "创建 Controllers、Models、Services 目录", "Status": "Pending" },
      { "Id": "CHK_003", "Text": "添加 appsettings.Development.json", "Status": "Pending" }
    ],
    "StageId": 3,
    "RiskLevel": "Low",
    "ComplexityLevel": 1,
    "UnsafeIfDependencyFallbackPassed": false
  },
  {
    "Id": "TASK_ARCH_002",
    "TaskType": "code",
    "Title": "[Core] 上移接口并修复依赖方向",
    "Description": "## 阶段目标\n将 IUnitOfWork 接口从 Infrastructure 层上移到 Core 层，并修复 Program.cs 与项目引用。\n\n## 阶段范围\n仅修改接口位置、项目依赖与依赖注入配置，不迁移业务逻辑。\n\n## 关键任务\n- 创建 src/CleanApp.Core/Interfaces/IUnitOfWork.cs\n- 删除 src/CleanApp.Infrastructure/IUnitOfWork.cs 旧接口定义\n- 更新 src/CleanApp.Core/CleanApp.Core.csproj 移除对 CleanApp.Infrastructure 的引用\n- 更新 src/CleanApp/Program.cs 修复依赖注入注册\n- 执行 dotnet build 与 dotnet test 验证\n\n## 任务执行顺序\n1. 创建 src/CleanApp.Core/Interfaces/IUnitOfWork.cs → 完成接口上移\n2. 删除 src/CleanApp.Infrastructure/IUnitOfWork.cs → 移除旧位置定义\n3. 修改 src/CleanApp.Core/CleanApp.Core.csproj → 移除 CleanApp.Infrastructure 项目引用\n4. 修改 src/CleanApp/Program.cs → 注册新的接口依赖\n5. 执行 dotnet build → 验证编译通过\n6. 执行 dotnet test → 验证测试通过\n\n## 涉及模块或服务\n- CleanApp.Core\n- CleanApp.Infrastructure\n- CleanApp\n\n## 主要技术改动点\n- 接口上移\n- 项目引用解耦\n- 依赖注入配置更新\n\n## 影响范围\n- 代码目录：src/CleanApp.Core/Interfaces/, src/CleanApp.Infrastructure/, src/CleanApp/Program.cs\n- 服务：依赖注入配置\n- 数据表：无",
    "Dependencies": [],
    "RequiredArtifacts": [
      { "Type": "file_exists", "Path": "src/CleanApp.Core/Interfaces/IUnitOfWork.cs" },
      { "Type": "file_not_contains", "Path": "src/CleanApp.Core/CleanApp.Core.csproj", "Text": "CleanApp.Infrastructure" },
      { "Type": "file_contains", "Path": "src/CleanApp/Program.cs", "Text": "IUnitOfWork" }
    ],
    "ForbiddenStates": [
      { "Type": "file_not_exists", "Path": "src/CleanApp.Infrastructure/IUnitOfWork.cs" }
    ],
    "ForbiddenPaths": [],
    "ChecklistItems": [
      { "Id": "CHK_101", "Text": "创建 src/CleanApp.Core/Interfaces/IUnitOfWork.cs 完成接口上移", "Status": "Pending" },
      { "Id": "CHK_102", "Text": "删除 src/CleanApp.Infrastructure/IUnitOfWork.cs 旧接口定义", "Status": "Pending" },
      { "Id": "CHK_103", "Text": "修改 src/CleanApp.Core/CleanApp.Core.csproj 移除 CleanApp.Infrastructure 引用", "Status": "Pending" },
      { "Id": "CHK_104", "Text": "修改 src/CleanApp/Program.cs 更新依赖注入注册", "Status": "Pending" },
      { "Id": "CHK_105", "Text": "执行 dotnet build 验证编译通过", "Status": "Pending" },
      { "Id": "CHK_106", "Text": "执行 dotnet test 验证测试通过", "Status": "Pending" }
    ],
    "StageId": 3,
    "RiskLevel": "High",
    "ComplexityLevel": 3,
    "UnsafeIfDependencyFallbackPassed": true
  },
  {
    "Id": "TASK_ARCH_003",
    "TaskType": "code",
    "Title": "[Core] 引入泛型仓储抽象并保持 GridFS 网关职责单一",
    "Description": "## 阶段目标\n在 Core 层引入 `IFileRepository<TEntity>` 泛型仓储抽象，由 Infrastructure 层实现，并保持 MongoFileService 继续作为 GridFS 网关服务，不强制其依赖仓储抽象。\n\n## 阶段范围\n仅处理仓储抽象、项目引用与依赖注入配置，不重写 MongoFileService 的 GridFS 读写逻辑。\n\n## 关键任务\n- 创建 src/CleanApp.Core/Interfaces/IFileRepository.cs 泛型接口\n- 创建 src/CleanApp.Infrastructure/Repositories/FileRepository.cs 实现\n- 更新 src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj 添加对 CleanApp.Core 的引用\n- 更新 src/CleanApp/Program.cs 注册 IFileRepository 与 FileRepository\n- 执行 dotnet build 与 dotnet test 验证\n\n## 任务执行顺序\n1. 创建 src/CleanApp.Core/Interfaces/IFileRepository.cs → 定义 `interface IFileRepository<TEntity>`\n2. 创建 src/CleanApp.Infrastructure/Repositories/FileRepository.cs → 实现泛型仓储接口\n3. 修改 src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj → 添加 CleanApp.Core 的 ProjectReference\n4. 修改 src/CleanApp/Program.cs → 注册 IFileRepository 与 FileRepository\n5. 保持 src/CleanApp.Core/Services/MongoFileService.cs 作为 GridFS 网关，不强制引入 IFileRepository\n6. 执行 dotnet build → 验证编译通过\n7. 执行 dotnet test → 验证测试通过\n\n## 涉及模块或服务\n- CleanApp.Core\n- CleanApp.Infrastructure\n- CleanApp\n- MongoFileService\n\n## 主要技术改动点\n- 新增泛型仓储接口\n- 新增 Infrastructure 仓储实现\n- 补齐 Infrastructure 对 Core 的项目引用\n- 更新 Program.cs 依赖注入\n- 保持 MongoFileService 的外部网关职责不变\n\n## 影响范围\n- 代码目录：src/CleanApp.Core/Interfaces/, src/CleanApp.Infrastructure/Repositories/, src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj, src/CleanApp/Program.cs\n- 服务：依赖注入容器、GridFS 网关服务\n- 数据表：无",
    "Dependencies": [],
    "RequiredArtifacts": [
      { "Type": "file_exists", "Path": "src/CleanApp.Core/Interfaces/IFileRepository.cs" },
      { "Type": "file_contains", "Path": "src/CleanApp.Core/Interfaces/IFileRepository.cs", "Text": "interface IFileRepository<" },
      { "Type": "file_exists", "Path": "src/CleanApp.Infrastructure/Repositories/FileRepository.cs" },
      { "Type": "file_contains", "Path": "src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj", "Text": "CleanApp.Core" },
      { "Type": "file_contains", "Path": "src/CleanApp/Program.cs", "Text": "IFileRepository" },
      { "Type": "file_contains", "Path": "src/CleanApp/Program.cs", "Text": "FileRepository" }
    ],
    "ForbiddenStates": [],
    "ForbiddenPaths": [],
    "ChecklistItems": [
      { "Id": "CHK_201", "Text": "创建 src/CleanApp.Core/Interfaces/IFileRepository.cs 并定义泛型接口签名", "Status": "Pending" },
      { "Id": "CHK_202", "Text": "创建 src/CleanApp.Infrastructure/Repositories/FileRepository.cs 实现仓储接口", "Status": "Pending" },
      { "Id": "CHK_203", "Text": "修改 src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj 添加 CleanApp.Core 引用", "Status": "Pending" },
      { "Id": "CHK_204", "Text": "修改 src/CleanApp/Program.cs 注册 IFileRepository 与 FileRepository", "Status": "Pending" },
      { "Id": "CHK_205", "Text": "保持 MongoFileService 作为 GridFS 网关，不强制引入 IFileRepository", "Status": "Pending" },
      { "Id": "CHK_206", "Text": "执行 dotnet build 验证编译通过", "Status": "Pending" },
      { "Id": "CHK_207", "Text": "执行 dotnet test 验证测试通过", "Status": "Pending" }
    ],
    "StageId": 3,
    "RiskLevel": "High",
    "ComplexityLevel": 3,
    "UnsafeIfDependencyFallbackPassed": true
  }
]

━━━ 禁止输出的错误格式（反例） ━━━
以下格式会导致解析失败，绝对禁止：
- 包含注释：`[{ "Id": "T1" // 任务一 }]` 或 `/* 块注释 */`
- 包含尾随逗号：`[1, 2,]` 或 `{"Title": "test",}`
- 包含代码块标记：` ```json [...] ``` `
- 包含解释性文字：`以下是任务规划：[...]`
只输出纯 JSON 对象 {"tasks":[...]}，第一个字符 {，最后一个字符 }，中间没有任何非 JSON 内容。
""";

        var draftMessages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, draftSystemPrompt),
            new ChatMessage(ChatRole.User, $"请先生成开放式规划草稿，用于后续结构化任务投影：{goal}")
        };

        var draftOptions = new VllmChatOptions
        {
            Temperature = 0.4f,
            ThinkingEnabled = true
        };

        var (planningDraft, draftThinking) = await GeneratePlanTextWithFallbackAsync(
            session,
            draftMessages,
            draftOptions,
            "planning_draft",
            "规划草稿运行时异常，已回退兼容模式继续生成草稿。",
            ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(planningDraft))
        {
            await PublishSummaryAsync(
                session,
                new PlannerSummaryUpdate
                {
                    Kind = PlannerSummaryKind.InProgress,
                    Message = "规划草稿已生成，正在投影为结构化任务清单。",
                    Phase = "planning_projection",
                    ThinkingLength = draftThinking?.Length,
                    TextLength = planningDraft.Length
                },
                ct).ConfigureAwait(false);
        }

        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, BuildPlanProjectionPrompt(goal, planningDraft, RepositoryManifestIndex.Describe(
                Tools.ToolPathResolver.ResolveProjectRoot(session.WorkspacePath, null, session.ProjectUrl, session.Metadata))))
        };

        var options = new VllmChatOptions
        {
            Temperature = 0.2f,
            ThinkingEnabled = false,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                PlanStructuredOutputJsonSchema.Value,
                "codex_task_plan",
                "Codex task plan: a JSON object whose tasks field is the array of executable planning tasks")
        };

        var (json, projectionThinking) = await GeneratePlanTextWithFallbackAsync(
            session,
            messages,
            options,
            "planning_projection",
            "规划结构化投影运行时异常，已回退兼容模式继续生成任务清单。",
            ct).ConfigureAwait(false);
        var plannerThinking = CombinePlannerThinking(draftThinking, projectionThinking);

        if (!string.IsNullOrWhiteSpace(plannerThinking))
        {
            _logger.LogInformation("Planner thinking: {Thinking}", TruncateForLog(plannerThinking, 2000));
        }

        json = json?.Trim() ?? "[]";

        try
        {
            // Clean markdown blocks if present
            if (json.StartsWith("```", StringComparison.Ordinal))
            {
                var idx = json.IndexOf('\n', StringComparison.Ordinal);
                if (idx > 0) json = json[(idx + 1)..];
                if (json.EndsWith("```", StringComparison.Ordinal)) json = json[..^3];
                json = json.Trim();
            }

            // Fallback: extract JSON array from response if surrounded by non-JSON text (an object payload is parsed as it is)
            if (!json.StartsWith('[') && !json.StartsWith('{'))
            {
                var arrayStart = json.IndexOf('[');
                var arrayEnd = json.LastIndexOf(']');
                if (arrayStart >= 0 && arrayEnd > arrayStart)
                {
                    _logger.LogWarning("Planner response contained non-JSON preamble, extracting embedded JSON array");
                    json = json[arrayStart..(arrayEnd + 1)];
                }
            }

            var tasks = TryDeserializePlan(json, out _, out var repaired);
            if (repaired)
            {
                StructuredLog.Warning(_logger, "Planner JSON required sanitization before parsing.");
            }
            CodexTaskClassifier.NormalizePlan(tasks);
            LogPlanContractLintIssues(tasks);

            // Ensure status is Pending
            foreach (var task in tasks)
            {
                task.Status = CodexTaskStatus.Pending;
            }

            await PublishSummaryAsync(
                session,
                new PlannerSummaryUpdate
                {
                    Kind = PlannerSummaryKind.Completed,
                    Message = $"任务规划已生成，共 {tasks.Count} 个任务。",
                    Phase = "planning_completed",
                    TaskCount = tasks.Count,
                    ThinkingLength = plannerThinking?.Length,
                    TextLength = json.Length
                },
                ct).ConfigureAwait(false);

            return tasks;
        }
        catch (JsonReaderException ex)
        {
            return await HandlePlanParseFailureWithRetryAsync(session, goal, messages, options, json, ex, ct).ConfigureAwait(false);
        }
        catch (JsonSerializationException ex)
        {
            return await HandlePlanParseFailureWithRetryAsync(session, goal, messages, options, json, ex, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return await HandlePlanParseFailureWithRetryAsync(session, goal, messages, options, json, ex, ct).ConfigureAwait(false);
        }
    }

    private async Task<(string Text, string? Thinking)> GeneratePlanTextWithFallbackAsync(
        CodexSession session,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        string phase,
        string fallbackMessage,
        CancellationToken ct)
    {
        if (ShouldUseRuntimeForPlanning())
        {
            try
            {
                return await GeneratePlanWithRuntimeAsync(session, messages, options, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not PlannerContextLimitException && ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Planner runtime execution failed in phase {Phase}. Falling back to direct streaming implementation.", phase);
                await PublishSummaryAsync(
                    session,
                    new PlannerSummaryUpdate
                    {
                        Kind = PlannerSummaryKind.Fallback,
                        Message = fallbackMessage,
                        Phase = $"{phase}_runtime_fallback"
                    },
                    ct).ConfigureAwait(false);
            }
        }

        return await GeneratePlanWithDirectStreamingAsync(messages, options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Worked example for the projection round. Weaker models copy the constraints of the planning turn itself ("this round only plans, no code is
    /// changed") into the task and project the act of planning as the one task; the example shows what the tasks are instead: the work that runs
    /// AFTER approval. The example is parsed by the same parser and checked against the required schema fields in a test, so it cannot rot.
    /// </summary>
    internal const string PlanProjectionFewShot = """
[示例：把计划投影成任务（只学习结构，不要照抄内容）]
输入计划摘要：
  需求：修复登录的两个缺陷：用户名区分大小写；用户名首尾带空白时登录失败。本轮只写计划、不执行、不修改代码；批准后再执行。
  计划：1) 增加大小写不同的登录回归用例，并在 src/UserService.cs 比较用户名时忽略大小写；2) 增加首尾带空白的登录回归用例，并在比较前去掉首尾空白；每一步都运行 dotnet test，确认新增与现有用例全部通过。
正确输出（任务是"批准之后要做的工作"，涉及改代码的任务 TaskType 必须是 code）：
{"tasks":[
 {"Id":"T1","Title":"登录比较用户名时忽略大小写","Description":"## 阶段目标\n修复用户名大小写敏感的缺陷，并用回归用例固定下来。\n\n## 阶段范围\n只改 tests/UserServiceTests.cs 和 src/UserService.cs 的比较处；不改其他行为。\n\n## 关键任务\n- 读取 UserServiceTests.cs 了解现有风格\n- 新增用大写用户名登录的回归用例\n- 修改比较为忽略大小写\n\n## 任务执行顺序\n1. 读取测试文件 → 了解断言风格\n2. 新增回归用例 → 复现缺陷\n3. 修改 UserService.cs → 比较忽略大小写\n4. 运行 dotnet test → 全部通过\n\n## 涉及模块或服务\n- UserService 及其测试\n\n## 主要技术改动点\n- 新增 1 个回归测试\n- 用户名比较改为 OrdinalIgnoreCase","TaskType":"code","Status":"Pending","StageId":3,"Dependencies":[],"Inputs":["tests/UserServiceTests.cs","src/UserService.cs"],"Outputs":["tests/UserServiceTests.cs","src/UserService.cs"],"ChecklistItems":[{"Id":"T1-C1","Text":"新增大小写不同的登录用例","Status":"Pending"},{"Id":"T1-C2","Text":"修改比较为忽略大小写","Status":"Pending"},{"Id":"T1-C3","Text":"dotnet test 全部通过","Status":"Pending"}],"RequiredArtifacts":[{"Type":"file_contains","Path":"src/UserService.cs","Text":"OrdinalIgnoreCase"}],"ForbiddenStates":[],"ForbiddenPaths":[],"RiskLevel":"Medium","ComplexityLevel":2,"UnsafeIfDependencyFallbackPassed":false},
 {"Id":"T2","Title":"登录前去掉用户名首尾空白","Description":"## 阶段目标\n修复用户名首尾带空白时登录失败的缺陷，并用回归用例固定下来。\n\n## 阶段范围\n只改 tests/UserServiceTests.cs 和 src/UserService.cs 的比较处；不改其他行为。\n\n## 关键任务\n- 新增用户名首尾带空白的登录回归用例\n- 比较前对用户名调用 Trim\n\n## 任务执行顺序\n1. 新增回归用例 → 复现缺陷\n2. 修改 UserService.cs → 比较前去掉首尾空白\n3. 运行 dotnet test → 全部通过\n\n## 涉及模块或服务\n- UserService 及其测试\n\n## 主要技术改动点\n- 新增 1 个回归测试\n- 比较前 Trim 用户名","TaskType":"code","Status":"Pending","StageId":3,"Dependencies":["T1"],"Inputs":["tests/UserServiceTests.cs","src/UserService.cs"],"Outputs":["tests/UserServiceTests.cs","src/UserService.cs"],"ChecklistItems":[{"Id":"T2-C1","Text":"新增首尾带空白的登录用例","Status":"Pending"},{"Id":"T2-C2","Text":"比较前去掉首尾空白","Status":"Pending"},{"Id":"T2-C3","Text":"dotnet test 全部通过","Status":"Pending"}],"RequiredArtifacts":[{"Type":"file_contains","Path":"src/UserService.cs","Text":"Trim"}],"ForbiddenStates":[],"ForbiddenPaths":[],"RiskLevel":"Medium","ComplexityLevel":2,"UnsafeIfDependencyFallbackPassed":false}
]}
错误输出（禁止）：只生成 1 个任务，标题是"制定修复计划"，Description 写"仅制定计划，不批准、不修改项目代码、不运行测试"。
这是错的：那是"本轮"的约束，不是要执行的工作；它会让后续没有任何代码任务可执行。任务必须描述批准之后要做的修改与验证，
不要把"制定/提交/批准计划""本轮不修改代码""不运行测试"之类当前回合的限制写进任务的标题或 Description。
错误输出（禁止）：先安排一个任务去“确认宿主是否已预取目标包”，再规定后续任务“预取未确认时不得修改依赖”。
这是错的：执行者没有任何办法查询宿主的预取状态，第一个任务只能回答“无法确认”，第二个任务就被它挡住，什么都不会改。
需求里写明的事实直接使用；它们是否成立，由宿主随后的离线还原预检判定。若不成立，任务以明确的原因失败，而不是被一道没有数据来源的关卡拦住。
错误输出（禁止）：任务里写“离线预检缺包则停止并等待，不要修改依赖”。
这是错的：执行者读到缓存缺包就会照办，只改一两处就收工并自称“受阻”，其余目标包一个没改；正确写法是“按目标版本修改全部依赖声明，运行还原，把还原失败的原因如实写进报告”。
错误输出（禁止）：在 T1、T2 之外再加一个 T3，标题“运行测试验证”，Description 只写“执行 dotnet test，确认全部用例通过”。
这是错的：T3 不改动任何文件，执行者跑完测试也会被宿主判为“没有产生工作区改动”而失败；测试写在 T1、T2 各自的最后一步里，宿主还会在任务完成后独立验收。
错误输出（禁止）：把一个缺陷拆成两个任务：T1 只新增“修复前会失败”的回归用例、不改业务代码，T2 再去修复。
这是错的：T1 结束时测试是失败的，宿主把最近一次测试运行失败判为任务未完成，T1 无法通过，T2 也就不会执行；
回归用例和让它通过的修改写在同一个任务里，每个任务结束时构建和测试都必须通过。一个缺陷一个任务，不要按“写测试 / 改代码 / 跑测试”拆分。
""";

    /// <summary>
    /// What a task may ask its executor to verify. Seen live: plans asked for a before/after baseline of every resolved package and a separate
    /// "--no-restore" run; the executor (one fresh container per command, no network) could not do either, and reported a finished upgrade as
    /// blocked. Acceptance is the host's job, so a task asks only for what its executor can run.
    /// </summary>
    internal const string PlanVerificationScopeRule =
        "- 任务里的验证步骤只写执行者做得到的：对改动涉及的项目运行还原、构建和相关测试，并如实报告结果。" +
        "不要要求记录或比对变更前后的基线、逐项目逐目标框架核对全部解析版本、核对任务范围之外的包有没有变化、重新做漏洞扫描，" +
        "也不要要求“先还原、再单独用 --no-restore 构建或测试”这类跨命令的步骤（执行者的每条命令都在全新的容器里运行，上一条命令的还原结果不会保留）。" +
        "这些核对由宿主在任务完成后的验收中独立完成（构建、测试、依赖与密钥扫描），不属于任务。";

    /// <summary>
    /// A task is executed by a worker that cannot ask the user anything. Seen live: the user said "the other 7 vulnerable packages", the scan
    /// listed 7 records for 6 packages, and the plan told the task to "report the difference and ask the user to name the seventh"; the worker
    /// upgraded all six, could not ask anyone, and reported the task blocked. A doubt about the request is not work for the executor.
    /// </summary>
    internal const string PlanOpenQuestionRule =
        "- 任务由执行者独立完成，执行者无法向用户提问。需求里对不上或不清楚的地方（数量与清单不一致、指代不明等）不要写进任务：" +
        "不要写“请用户明确/确认”“报告差异并等待用户决定”“向用户询问”这类步骤，也不要把它列为任务的未完成项。" +
        "按需求里能确定的内容写任务（例如明确列出了包名和版本，就以列出的为准，数量说法与之不符时以清单为准）；这类疑问由规划之后给用户的计划说明去指出。";

    /// <summary>
    /// A task that only runs the tests changes nothing, and a code task that leaves the workspace unchanged is failed by the host. Seen live
    /// (local Qwen, bugfix-paging): the plan was split in three and the last task was "run dotnet test and confirm all cases pass"; the worker
    /// ran the tests, they passed, and the job was still failed with "without any persisted workspace changes". The host verifies every
    /// finished task on its own (build, tests, dependency and secret scans), so verification belongs inside the task that made the change.
    /// </summary>
    internal const string PlanNoVerificationOnlyTaskRule =
        "- 不要把“运行测试 / 构建 / 验证修复”单独写成一个任务：只做验证的任务不会改动工作区，会被宿主判为失败，而且宿主会在每个任务完成后独立验收（构建、测试、依赖与密钥扫描）。" +
        "验证写成产生改动的那个任务自己的最后一步（ChecklistItems 里的验证项）；如果没有别的要改的东西，就不要为验证新增任务。";

    /// <summary>
    /// What a task may not touch is data in the plan. The host used to read it out of the task's sentences with regular expressions, and six live
    /// runs ended with a file the task had to change taken away by a sentence that only limited what to change IN it.
    /// </summary>
    internal const string PlanForbiddenPathsRule =
        "- ForbiddenPaths：只写这个任务绝对不能碰的整个文件或目录（相对项目根目录，例如 \"src/Domain/\"、\"package-lock.json\"）；没有就写 []，但字段必须存在。" +
        "“不要改文件里的其他内容 / 其他依赖版本”这类对文件里的内容的限制，写在 Description 里，不要写进 ForbiddenPaths——写进去会让整个文件被锁死，" +
        "任务就无法修改它。任务的 Outputs 里的文件不要同时出现在 ForbiddenPaths 里。" +
        "不要把“其他任务负责的目录”或“当前任务范围之外的所有目录”都列进 ForbiddenPaths：范围之外的文件本来就不可写，这个字段只写用户明确说了不要碰的东西；" +
        "依赖升级任务要改哪些项目文件，以宿主的依赖表（简报里的“受影响项目”）为准，不要自己猜。";

    /// <summary>
    /// The projection step never sees the repository, so a task's Outputs were whatever the model imagined ("OtherProject1/*.csproj"), and the
    /// files the task really had to change were refused as out of scope. Outputs come from the list of files that exist, or from the draft.
    /// </summary>
    internal const string PlanRealPathsRule =
        "- Outputs 只写上面“仓库里实际存在的清单文件”里有的、或第一阶段草稿里明确出现过的真实路径；不要写通配符、占位名（例如 \"OtherProject1/*.csproj\"）" +
        "或你没有见过的路径。拿不准就少写，写不出具体路径就写 []——宿主会按它知道的事实补上依赖任务要改的清单文件。";

    /// <summary>
    /// Run by hand with the same model, opencode kept the one request the host could not guarantee (versions the acceptance environment has not
    /// fetched) apart from the safe ones, so a failure of that part could be undone alone. A plan that mixes them fails as a whole.
    /// </summary>
    internal const string PlanRiskIsolationRule =
        "- 用户明确要求做一件验收环境可能做不成的事（例如升级宿主没有取包的版本、对齐没有公告的包）时，把它放进单独的任务，" +
        "在描述里写明“风险由用户承担，失败只影响这个任务”；有把握的升级放在别的任务里，不要和它混在同一个任务。";

    internal static string BuildPlanProjectionPrompt(string goal, string planningDraft, string? repositoryManifests = null)
    {
        var draft = string.IsNullOrWhiteSpace(planningDraft)
            ? "（第一阶段未生成有效草稿，请直接根据系统上下文和最终目标生成结构化任务清单。）"
            : planningDraft.Trim();
        // The step that turns the draft into tasks does not see the repository: the files that exist are listed for it, so that outputs are chosen
        // from them and not imagined.
        var manifestBlock = string.IsNullOrWhiteSpace(repositoryManifests)
            ? string.Empty
            : "\n[仓库里实际存在的清单文件]\n" + repositoryManifests.Trim() + "\n";

        return $$"""
请根据最终目标和第一阶段规划草稿，生成最终结构化任务规划。

[最终目标]
{{goal}}

[第一阶段规划草稿]
{{draft}}
{{manifestBlock}}

严格要求：
- 只输出纯 JSON 对象，形如 {"tasks":[...]}，第一个字符必须是 {，最后一个字符必须是 }。
- 不要输出草稿、解释、markdown 代码块或任何非 JSON 内容。
- 若草稿与系统规则冲突，以系统规则、JSON schema、任务精简原则和 RequiredArtifacts/ForbiddenStates 规则为准。
- 严格保留最终目标中的范围限制，不得用通用架构治理任务替换已指定的缺陷修复。
- 每个代码任务必须具有非空 ChecklistItems，包含具体修改与验证步骤。禁止仅输出 title/description/priority/estimated_effort 摘要。
- 需求里已经写明的事实（宿主已预取哪些包、目标版本、用户已做的决定）直接使用：不要为“确认”这些事实单独安排任务，也不要让后续任务以“事实未确认”为由不修改；任务内部也不要写“缺包/不可用就停止、等待”。
- 每个任务的描述必须自包含：执行者看不到规划时的对话，所以不要写“按前述清单”“上表中的包”这类指向任务之外的引用，要把具体的包名、版本、文件直接写进任务本身。
- 任务是计划里"批准之后要执行的工作"。计划文字里写的"本轮只规划、不执行、不修改代码"是当前回合的限制，不是任务内容，不得写进任务。
{{PlanVerificationScopeRule}}
{{PlanNoVerificationOnlyTaskRule}}
{{PlanForbiddenPathsRule}}
{{PlanRealPathsRule}}
{{PlanRiskIsolationRule}}
{{PlanOpenQuestionRule}}

{{PlanProjectionFewShot}}

[必须满足的输出 JSON Schema（即使服务端不强制 schema 也必须遵守）]
{{PlanResponseJsonSchema.Value.GetRawText()}}
""";
    }

    private static string? CombinePlannerThinking(string? draftThinking, string? projectionThinking)
    {
        if (string.IsNullOrWhiteSpace(draftThinking))
        {
            return string.IsNullOrWhiteSpace(projectionThinking) ? null : projectionThinking;
        }

        if (string.IsNullOrWhiteSpace(projectionThinking))
        {
            return draftThinking;
        }

        return $"{draftThinking}\n\n{projectionThinking}";
    }

    private async Task<List<CodexTask>> HandlePlanParseFailureWithRetryAsync(
        CodexSession session,
        string goal,
        IReadOnlyList<ChatMessage> originalMessages,
        ChatOptions options,
        string failedJson,
        Exception originalException,
        CancellationToken ct)
    {
        StructuredLog.Warning(_logger, originalException, "Planner JSON parse failed, attempting one retry with correction prompt. Failed JSON length={Length}", failedJson.Length);

        await PublishSummaryAsync(
            session,
            new PlannerSummaryUpdate
            {
                Kind = PlannerSummaryKind.Fallback,
                Message = "规划输出格式异常，正在尝试纠偏重试。",
                Phase = "planning_parse_retry"
            },
            ct).ConfigureAwait(false);

        // Build correction messages: original context + explicit correction request
        var correctionMessages = new List<ChatMessage>(originalMessages)
        {
            new ChatMessage(ChatRole.Assistant, failedJson),
            new ChatMessage(ChatRole.User,
                "你上一次的输出不是合法的 JSON 对象（{\"tasks\":[...]}），解析失败。\n" +
                "请严格遵守以下规则重新输出：\n" +
                "1. 只输出一个纯 JSON 对象 {\"tasks\":[...]}，第一个字符必须是 {，最后一个必须是 }\n" +
                "2. 禁止包含任何注释（// 或 /* */）\n" +
                "3. 禁止包含尾随逗号（,] 或 ,}）\n" +
                "4. 禁止包含 markdown 代码块标记\n" +
                "5. 禁止包含任何解释性文字\n" +
                "请重新生成任务规划：" + goal)
        };

        try
        {
            var (retryJson, retryThinking) = await GeneratePlanWithDirectStreamingAsync(correctionMessages, options, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(retryThinking))
            {
                _logger.LogInformation("Planner retry thinking: {Thinking}", TruncateForLog(retryThinking, 500));
            }

            retryJson = retryJson?.Trim() ?? "[]";

            if (retryJson.StartsWith("```", StringComparison.Ordinal))
            {
                var idx = retryJson.IndexOf('\n', StringComparison.Ordinal);
                if (idx > 0) retryJson = retryJson[(idx + 1)..];
                if (retryJson.EndsWith("```", StringComparison.Ordinal)) retryJson = retryJson[..^3];
                retryJson = retryJson.Trim();
            }

            if (!retryJson.StartsWith('['))
            {
                var arrayStart = retryJson.IndexOf('[');
                var arrayEnd = retryJson.LastIndexOf(']');
                if (arrayStart >= 0 && arrayEnd > arrayStart)
                {
                    retryJson = retryJson[arrayStart..(arrayEnd + 1)];
                }
            }

            var tasks = TryDeserializePlan(retryJson, out _, out var repaired);
            if (repaired)
            {
                StructuredLog.Warning(_logger, "Planner retry JSON required sanitization before parsing.");
            }

            CodexTaskClassifier.NormalizePlan(tasks);
            LogPlanContractLintIssues(tasks);
            foreach (var task in tasks)
            {
                task.Status = CodexTaskStatus.Pending;
            }

            await PublishSummaryAsync(
                session,
                new PlannerSummaryUpdate
                {
                    Kind = PlannerSummaryKind.Completed,
                    Message = $"纠偏重试成功，任务规划已生成，共 {tasks.Count} 个任务。",
                    Phase = "planning_retry_completed",
                    TaskCount = tasks.Count
                },
                ct).ConfigureAwait(false);

            return tasks;
        }
        catch (Exception retryEx)
        {
            StructuredLog.Error(_logger, retryEx, "Planner retry also failed. Original error: {OriginalError}", originalException.Message);
            await PublishSummaryAsync(
                session,
                new PlannerSummaryUpdate
                {
                    Kind = PlannerSummaryKind.Failed,
                    Message = "规划纠偏重试也失败，未能生成有效任务清单。",
                    Phase = "planning_retry_failed"
                },
                ct).ConfigureAwait(false);
            return new List<CodexTask>();
        }
    }

    private void LogPlanContractLintIssues(IEnumerable<CodexTask> tasks)
    {
        if (_planContractLinter == null)
        {
            return;
        }

        var issues = _planContractLinter.LintTasks(tasks);
        foreach (var issue in issues)
        {
            if (issue.Severity == PlanContractLintSeverity.Error)
            {
                _logger.LogWarning(
                    "Plan contract lint error. TaskId={TaskId} Code={Code} Message={Message}",
                    issue.TaskId ?? "<plan>",
                    issue.Code,
                    issue.Message);
            }
            else
            {
                _logger.LogInformation(
                    "Plan contract lint finding. TaskId={TaskId} Severity={Severity} Code={Code} Message={Message}",
                    issue.TaskId ?? "<plan>",
                    issue.Severity,
                    issue.Code,
                    issue.Message);
            }
        }
    }

    private static void CollectPlannerStreamingContent(Microsoft.Extensions.AI.ChatResponseUpdate update, System.Text.StringBuilder textSb, System.Text.StringBuilder thinkingSb)
    {
        var isThinking = update is Microsoft.Extensions.AI.ReasoningChatResponseUpdate ru && ru.Thinking;

        foreach (var part in update.Contents ?? Array.Empty<Microsoft.Extensions.AI.AIContent>())
        {
            if (part is Microsoft.Extensions.AI.TextContent tc && !string.IsNullOrEmpty(tc.Text))
            {
                if (isThinking) thinkingSb.Append(tc.Text);
                else textSb.Append(tc.Text);
            }
        }

        // Fallback for providers that send text via update.Text instead of Contents.
        // This also covers ReasoningChatResponseUpdate on providers that set update.Text
        // directly while marking Thinking=true on the update itself.
        if (string.IsNullOrEmpty(update.Text) == false && (update.Contents == null || update.Contents.All(c => c is not Microsoft.Extensions.AI.TextContent)))
        {
            if (isThinking) thinkingSb.Append(update.Text);
            else textSb.Append(update.Text);
        }
    }

    private bool ShouldUseRuntimeForPlanning()
    {
        return _queryRuntimeEngine != null &&
               !string.Equals(Environment.GetEnvironmentVariable("PLANNER_DISABLE_RUNTIME"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task PublishSummaryAsync(CodexSession session, PlannerSummaryUpdate update, CancellationToken ct)
    {
        if (_summaryPublisher == null)
        {
            return;
        }

        try
        {
            await _summaryPublisher.PublishAsync(session, update, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Planner summary publish failed. SessionId={SessionId} Kind={Kind}",
                session.Id,
                update.Kind);
        }
    }

    private async Task<(string Text, string? Thinking)> GeneratePlanWithRuntimeAsync(
        CodexSession session,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        CancellationToken ct)
    {
        if (_queryRuntimeEngine == null)
        {
            throw new InvalidOperationException("Planner runtime engine is not available.");
        }

        var request = new QueryRuntimeRequest
        {
            SessionId = session.Id ?? "planner-session",
            EntryPoint = QueryLoopEntryPoint.PlanWorker,
            InitialMessages = messages,
            Options = options,
            Scenario = MemoryInjectionScenario.Planning,
            Session = null,
            MaxRounds = 1,
            EnableTools = false,
            AllowStreaming = true,
            PromptMetadata = new PromptMetadata(
                RolePrompt: "DefaultCodexPlanner",
                WorkspacePath: session.WorkspacePath,
                PlanSize: session.Plan.Count,
                InitialStage: session.CurrentStage)
        };

        var eventSink = new CompositeQueryRuntimeEventSink(
            new PlannerDiagnosticEventSink(_logger),
            new PlannerSummaryEventSink(session, _summaryPublisher, _logger));
        var result = await _queryRuntimeEngine.ExecuteAsync(request, eventSink, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Planner runtime completed. SessionId={SessionId} Termination={TerminationReason} Rounds={Rounds} DurationMs={DurationMs} ThinkingLength={ThinkingLength} TextLength={TextLength}",
            session.Id,
            result.TerminationReason,
            result.TotalRounds,
            result.TotalDurationMs,
            result.FinalThinking?.Length ?? 0,
            result.FinalText?.Length ?? 0);

        if (result.TerminationReason == QueryTerminationReason.ContextHardLimit)
        {
            throw new PlannerContextLimitException();
        }

        if (result.TerminationReason == QueryTerminationReason.Exception ||
            result.TerminationReason == QueryTerminationReason.RecoveryExhausted)
        {
            // The engine knows why (model call failed, cancelled, ...); without it every planning failure reads just "Exception".
            var detail = result.TerminalDetailMessage?.Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(detail)
                ? $"Planner runtime terminated unsuccessfully: {result.TerminationReason}"
                : $"Planner runtime terminated unsuccessfully: {result.TerminationReason} ({(detail.Length > 400 ? detail[..400] : detail)})");
        }

        return (result.FinalText ?? "[]", result.FinalThinking);
    }

    private sealed class PlannerContextLimitException : InvalidOperationException
    {
        public PlannerContextLimitException()
            : base("Planner context exceeds the runtime budget; refusing to bypass the context limit through direct streaming.") { }
        public PlannerContextLimitException(string message) : base(message) { }
        public PlannerContextLimitException(string message, Exception innerException) : base(message, innerException) { }
    }

    private async Task<(string Text, string? Thinking)> GeneratePlanWithDirectStreamingAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        CancellationToken ct)
    {
        var plannerThinkingSb = new StringBuilder();
        var plannerTextSb = new StringBuilder();

        if (_llmExecutor != null)
        {
            await foreach (var update in _llmExecutor.StreamAsync(
                new LLMExecutionRequest(messages, options, MemoryInjectionScenario.Planning), ct).ConfigureAwait(false))
            {
                CollectPlannerStreamingContent(update, plannerTextSb, plannerThinkingSb);
            }
        }
        else
        {
            await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
            {
                CollectPlannerStreamingContent(update, plannerTextSb, plannerThinkingSb);
            }
        }

        return (plannerTextSb.ToString(), plannerThinkingSb.Length > 0 ? plannerThinkingSb.ToString() : null);
    }

    private static string TruncateForLog(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength];
    }

    /// <summary>
    /// Parses the plan. <paramref name="repaired"/> is true only when the text had to be fixed to parse (comments, trailing commas, bad
    /// escapes, missing braces). Unwrapping <c>{"tasks":[...]}</c> is not a repair: that object is the shape the schema asks for, and
    /// reporting it as one put a warning on every plan and hid the real repairs.
    /// </summary>
    private static List<CodexTask> TryDeserializePlan(string json, out string sanitizedJson, out bool repaired)
    {
        // Pre-sanitize: strip comments and trailing commas before any parsing
        var original = json;
        json = StripJsonComments(json);
        json = StripTrailingCommas(json);
        repaired = !string.Equals(original, json, StringComparison.Ordinal);

        sanitizedJson = NormalizePlanJsonPayload(json);

        try
        {
            return PlanToolJson.Read<List<CodexTask>>(sanitizedJson) ?? [];
        }
        catch (JsonReaderException ex) when (TrySanitizeJsonStringEscapes(sanitizedJson, ex, out sanitizedJson))
        {
            repaired = true;
            return PlanToolJson.Read<List<CodexTask>>(sanitizedJson) ?? [];
        }
        catch (JsonException) when (TryRepairMissingObjectOpenBraces(sanitizedJson, out sanitizedJson))
        {
            repaired = true;
            try
            {
                return PlanToolJson.Read<List<CodexTask>>(sanitizedJson) ?? [];
            }
            catch (JsonReaderException ex) when (TrySanitizeJsonStringEscapes(sanitizedJson, ex, out var escapeSanitizedJson))
            {
                sanitizedJson = escapeSanitizedJson;
                return PlanToolJson.Read<List<CodexTask>>(sanitizedJson) ?? [];
            }
        }
    }

    private static string NormalizePlanJsonPayload(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        var trimmed = json.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        try
        {
            var payload = JObject.Parse(trimmed);
            if (payload.TryGetValue("tasks", StringComparison.OrdinalIgnoreCase, out var tasks) &&
                tasks is JArray taskArray)
            {
                return taskArray.ToString(Formatting.None);
            }
        }
        catch (JsonException)
        {
            return trimmed;
        }

        return trimmed;
    }

    /// <summary>
    /// The schema sent as the provider's structured-output constraint. Providers accept different JSON-schema subsets: Anthropic rejects array
    /// length bounds ("For 'array' type, property 'maxItems' is not supported"), which failed the projection round and left a placeholder plan.
    /// The bounds still reach the model as text, because the prompt carries <see cref="PlanResponseJsonSchema"/> unchanged.
    /// </summary>
    private static SystemTextJsonElement CreatePlanStructuredOutputJsonSchema()
    {
        var schema = JObject.Parse(PlanResponseJsonSchema.Value.GetRawText());
        foreach (var node in new[] { schema }.Concat(schema.Descendants().OfType<JObject>()).ToList())
        {
            node.Remove("minItems");
            node.Remove("maxItems");
        }

        using var document = SystemTextJsonDocument.Parse(schema.ToString(Formatting.None));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The plan schema has an object root, <c>{"tasks":[...]}</c>: strict structured-output providers (OpenAI, Anthropic) reject an array
    /// root ("schema must be a JSON Schema of type object"). The parser also still accepts a bare array.
    /// </summary>
    internal static SystemTextJsonElement CreatePlanResponseJsonSchema()
    {
        var wrapper = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["tasks"] = JObject.Parse(CreatePlanTasksArraySchema().GetRawText()) },
            ["required"] = new JArray("tasks"),
            ["additionalProperties"] = false
        };

        using var wrapped = SystemTextJsonDocument.Parse(wrapper.ToString(Formatting.None));
        return wrapped.RootElement.Clone();
    }

    private static SystemTextJsonElement CreatePlanTasksArraySchema()
    {
        using var document = SystemTextJsonDocument.Parse("""
        {
          "type": "array",
          "minItems": 0,
          "maxItems": 10,
          "items": {
            "type": "object",
            "properties": {
              "Id": { "type": "string" },
              "Title": { "type": "string" },
              "Description": { "type": "string" },
              "TaskType": { "type": "string", "enum": ["code", "analysis"] },
              "Status": { "type": "string", "enum": ["Pending"] },
              "StageId": { "type": "integer" },
              "Dependencies": {
                "type": "array",
                "items": { "type": "string" }
              },
              "Inputs": {
                "type": "array",
                "items": { "type": "string" }
              },
              "Outputs": {
                "type": "array",
                "items": { "type": "string" }
              },
              "ChecklistItems": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {
                    "Id": { "type": "string" },
                    "Text": { "type": "string" },
                    "Status": { "type": "string", "enum": ["Pending"] }
                  },
                  "required": ["Id", "Text", "Status"],
                  "additionalProperties": false
                }
              },
              "RequiredArtifacts": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {
                    "Type": { "type": "string", "enum": ["file_exists", "file_not_exists", "file_contains", "file_not_contains"] },
                    "Path": { "type": "string" },
                    "Text": { "type": "string" }
                  },
                  "required": ["Type", "Path", "Text"],
                  "additionalProperties": false
                }
              },
              "ForbiddenStates": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {
                    "Type": { "type": "string", "enum": ["file_exists", "file_not_exists", "file_contains", "file_not_contains"] },
                    "Path": { "type": "string" },
                    "Text": { "type": "string" }
                  },
                  "required": ["Type", "Path", "Text"],
                  "additionalProperties": false
                }
              },
              "ForbiddenPaths": {
                "type": "array",
                "items": { "type": "string" }
              },
              "RiskLevel": { "type": "string", "enum": ["Low", "Medium", "High"] },
              "ComplexityLevel": { "type": "integer", "enum": [1, 2, 3] },
              "UnsafeIfDependencyFallbackPassed": { "type": "boolean" }
            },
            "required": [
              "Id",
              "Title",
              "Description",
              "TaskType",
              "Status",
              "StageId",
              "Dependencies",
              "Inputs",
              "Outputs",
              "ChecklistItems",
              "RequiredArtifacts",
              "ForbiddenStates",
              "ForbiddenPaths",
              "RiskLevel",
              "ComplexityLevel",
              "UnsafeIfDependencyFallbackPassed"
            ],
            "additionalProperties": false
          }
        }
        """);

        return document.RootElement.Clone();
    }

    private static bool TryRepairMissingObjectOpenBraces(string json, out string repairedJson)
    {
        repairedJson = json;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        var sb = new StringBuilder(json.Length + 32);
        var stack = new Stack<char>();
        var inserted = false;
        var inString = false;

        for (var i = 0; i < json.Length; i++)
        {
            var ch = json[i];

            if (ch == '"' && !IsEscapedQuote(json, i))
            {
                inString = !inString;
                sb.Append(ch);
                continue;
            }

            if (inString)
            {
                sb.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '[':
                    stack.Push('[');
                    sb.Append(ch);
                    if (ShouldInsertObjectStartAfter(json, i))
                    {
                        sb.Append('{');
                        stack.Push('{');
                        inserted = true;
                    }
                    break;

                case '{':
                    stack.Push('{');
                    sb.Append(ch);
                    break;

                case '}':
                    if (stack.Count > 0 && stack.Peek() == '{')
                    {
                        stack.Pop();
                    }
                    sb.Append(ch);
                    break;

                case ']':
                    if (stack.Count > 1 && stack.Peek() == '{' && IsNextStackFrameArray(stack))
                    {
                        stack.Pop();
                        sb.Append('}');
                        inserted = true;
                    }

                    if (stack.Count > 0 && stack.Peek() == '[')
                    {
                        stack.Pop();
                    }
                    sb.Append(ch);
                    break;

                case ',':
                    sb.Append(ch);
                    if (stack.Count > 0 && stack.Peek() == '[' && ShouldInsertObjectStartAfter(json, i))
                    {
                        sb.Append('{');
                        stack.Push('{');
                        inserted = true;
                    }
                    break;

                default:
                    sb.Append(ch);
                    break;
            }
        }

        if (!inserted)
        {
            return false;
        }

        repairedJson = sb.ToString();
        return true;
    }

    private static bool IsNextStackFrameArray(Stack<char> stack)
    {
        var index = 0;
        foreach (var frame in stack)
        {
            if (index == 1)
            {
                return frame == '[';
            }

            index++;
        }

        return false;
    }

    private static bool ShouldInsertObjectStartAfter(string json, int structuralIndex)
    {
        var i = structuralIndex + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i]))
        {
            i++;
        }

        if (i >= json.Length || json[i] != '"')
        {
            return false;
        }

        i++;
        while (i < json.Length)
        {
            if (json[i] == '"' && !IsEscapedQuote(json, i))
            {
                i++;
                break;
            }

            i++;
        }

        while (i < json.Length && char.IsWhiteSpace(json[i]))
        {
            i++;
        }

        return i < json.Length && json[i] == ':';
    }

    private static bool TrySanitizeJsonStringEscapes(string json, JsonReaderException ex, out string sanitizedJson)
    {
        sanitizedJson = json;

        if (!ex.Message.Contains("Bad JSON escape sequence", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sb = new StringBuilder(json.Length);
        var inString = false;

        for (var i = 0; i < json.Length; i++)
        {
            var ch = json[i];

            if (ch == '"' && !IsEscapedQuote(json, i))
            {
                inString = !inString;
                sb.Append(ch);
                continue;
            }

            if (inString && ch == '\\' && i + 1 < json.Length)
            {
                var next = json[i + 1];
                if (IsValidJsonEscape(json, i + 1, next))
                {
                    sb.Append(ch);
                    continue;
                }

                sb.Append(next);
                i++;
                continue;
            }

            sb.Append(ch);
        }

        sanitizedJson = sb.ToString();
        return !string.Equals(json, sanitizedJson, StringComparison.Ordinal);
    }

    private static bool IsEscapedQuote(string text, int quoteIndex)
    {
        var backslashCount = 0;
        for (var i = quoteIndex - 1; i >= 0 && text[i] == '\\'; i--)
        {
            backslashCount++;
        }

        return backslashCount % 2 == 1;
    }

    private static bool IsValidJsonEscape(string text, int nextIndex, char next)
    {
        if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
        {
            return true;
        }

        if (next != 'u')
        {
            return false;
        }

        if (nextIndex + 4 >= text.Length)
        {
            return false;
        }

        for (var j = nextIndex + 1; j <= nextIndex + 4; j++)
        {
            if (!Uri.IsHexDigit(text[j]))
            {
                return false;
            }
        }

        return true;
    }

    private static string StripJsonComments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        var sb = new StringBuilder(json.Length);
        var i = 0;
        var inString = false;

        while (i < json.Length)
        {
            var ch = json[i];

            if (ch == '"' && !IsEscapedQuote(json, i))
            {
                inString = !inString;
                sb.Append(ch);
                i++;
                continue;
            }

            if (inString)
            {
                sb.Append(ch);
                i++;
                continue;
            }

            // Strip single-line comments: // ...
            if (ch == '/' && i + 1 < json.Length && json[i + 1] == '/')
            {
                while (i < json.Length && json[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            // Strip block comments: /* ... */
            if (ch == '/' && i + 1 < json.Length && json[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < json.Length && (json[i] != '*' || json[i + 1] != '/'))
                {
                    i++;
                }

                i += 2; // skip closing */
                continue;
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString();
    }

    private static string StripTrailingCommas(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        return System.Text.RegularExpressions.Regex.Replace(
            json,
            @",\s*([\]\}])",
            "$1");
    }

    private sealed class PlannerDiagnosticEventSink(ILogger logger) : IQueryRuntimeEventSink
    {
        public bool IsEnabled(QueryRuntimeEventType eventType)
            => eventType is QueryRuntimeEventType.RoundStarted
            or QueryRuntimeEventType.ThinkingStarted
            or QueryRuntimeEventType.ThinkingDelta
            or QueryRuntimeEventType.ThinkingEnded
            or QueryRuntimeEventType.RoundCompleted
            or QueryRuntimeEventType.Terminated
            or QueryRuntimeEventType.Error;

        public ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent)
        {
            switch (runtimeEvent)
            {
                case RoundStartedEvent e:
                    logger.LogInformation(
                        "Planner runtime round started. Round={Round} MaxRounds={MaxRounds} ContextChars={ContextChars}",
                        e.Round + 1,
                        e.MaxRounds,
                        e.ContextChars);
                    break;
                case ThinkingStartedEvent e:
                    logger.LogInformation("Planner runtime thinking started. Round={Round}", e.Round + 1);
                    break;
                case ThinkingDeltaEvent e:
                    logger.LogDebug(
                        "Planner runtime thinking delta. Round={Round} Delta={Delta}",
                        e.Round + 1,
                        TruncateForLog(e.Delta, 240));
                    break;
                case ThinkingEndedEvent e:
                    logger.LogInformation(
                        "Planner runtime thinking ended. Round={Round} ThinkingLength={ThinkingLength}",
                        e.Round + 1,
                        e.FullThinking?.Length ?? 0);
                    logger.LogDebug(
                        "Planner runtime full thinking. Round={Round} Thinking={Thinking}",
                        e.Round + 1,
                        e.FullThinking);
                    break;
                case RoundCompletedEvent e:
                    logger.LogInformation(
                        "Planner runtime round completed. Round={Round} HasText={HasText} TextLength={TextLength} ThinkingLength={ThinkingLength}",
                        e.Round + 1,
                        e.HasText,
                        e.TextLength,
                        e.ThinkingLength);
                    break;
                case TerminatedEvent e:
                    logger.LogInformation(
                        "Planner runtime terminated. Reason={Reason} TotalRounds={TotalRounds} TotalDurationMs={TotalDurationMs}",
                        e.Reason,
                        e.TotalRounds,
                        e.TotalDurationMs);
                    break;
                case ErrorEvent e:
                    logger.LogWarning(
                        e.Exception,
                        "Planner runtime error. Type={ErrorType} Message={Message}",
                        e.ErrorType,
                        e.Message);
                    break;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class PlannerSummaryEventSink(
        CodexSession session,
        IPlannerSummaryPublisher? summaryPublisher,
        ILogger logger) : IQueryRuntimeEventSink
    {
        private bool _publishedInProgress;
        private bool _publishedRoundComplete;

        public bool IsEnabled(QueryRuntimeEventType eventType)
            => summaryPublisher != null &&
               eventType is
                   QueryRuntimeEventType.RoundStarted or
                   QueryRuntimeEventType.ThinkingStarted or
                   QueryRuntimeEventType.RoundCompleted;

        public async ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent)
        {
            if (summaryPublisher == null)
            {
                return;
            }

            PlannerSummaryUpdate? update = runtimeEvent switch
            {
                RoundStartedEvent e => new PlannerSummaryUpdate
                {
                    Kind = PlannerSummaryKind.InProgress,
                    Message = "规划模型已接收请求，正在读取上下文。",
                    Phase = "runtime_round_started",
                    Round = e.Round + 1,
                    MaxRounds = e.MaxRounds
                },
                ThinkingStartedEvent e when !_publishedInProgress => new PlannerSummaryUpdate
                {
                    Kind = PlannerSummaryKind.InProgress,
                    Message = "正在整合项目上下文并拆分任务。",
                    Phase = "runtime_thinking",
                    Round = e.Round + 1
                },
                RoundCompletedEvent e when !_publishedRoundComplete => new PlannerSummaryUpdate
                {
                    Kind = PlannerSummaryKind.InProgress,
                    Message = "规划模型已完成响应，正在整理任务清单。",
                    Phase = "runtime_round_completed",
                    Round = e.Round + 1,
                    ThinkingLength = e.ThinkingLength,
                    TextLength = e.TextLength
                },
                _ => null
            };

            if (update == null)
            {
                return;
            }

            if (runtimeEvent is ThinkingStartedEvent)
            {
                _publishedInProgress = true;
            }
            else if (runtimeEvent is RoundCompletedEvent)
            {
                _publishedRoundComplete = true;
            }

            try
            {
                await summaryPublisher.PublishAsync(session, update, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Planner summary event publish failed. SessionId={SessionId} Phase={Phase}",
                    session.Id,
                    update.Phase);
            }
        }
    }


}
