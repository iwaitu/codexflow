# 本地执行与控制面威胁模型

本文说明 `Execution:Backend = Local` 模式下的安全边界、防自批架构以及已知残余风险。

## 1. 安全边界

本地模式不在容器或操作系统级沙箱中运行外部命令；进程一旦被允许启动，即拥有当前操作系统用户的网络与文件系统权限（执行回执中诚实标注 `network = Host`、`filesystem = unrestricted`）。因此，本地模式的安全边界建立在**启动前门禁**与**写回工作区门禁**之上：

1. **权限模式与交互式审批（`ExecutionApprovalBroker`）**
   - **`Ask`（默认）**：每次调用 `ExecuteUntrustedCode` 工具（`exec_cmd`、`exec_code`、`run_tests`、`skill` 脚本）在打开会话和启动任何进程前均挂起请求用户批准。
   - **`AutoInWorkspace`**：仅自动放行来自可信安装位置、通过净化 `PATH` 与符号链接校验、符合只读语法白名单并注入强制安全参数/固定最小环境的 `git`（`status` / `diff` / `log` / `show`，针对独立 `GuestGit`）与 `rg`；其余命令全部转为 `Ask`。
   - **`FullAuto`**：自动放行所有白名单命令（仍受 Windows `.cmd`/`.bat` 禁用策略与导出通道检查约束）。启用 `FullAuto` 等于信任模型能执行的任意代码。
   - **`Deny`**：只读分析模式，禁止启动任何命令。
2. **不可变 `ExecutionRequest` 与 `requestDigest` 绑定**
   - 批准操作强绑定规范化请求 JSON 的 SHA-256 摘要（覆盖最终可执行文件、解释器链、argv、cwd、stdin/代码/skill 脚本/`Inputs` 清单哈希、环境变量差异、后端、网络与文件系统能力）。
   - `skill` 脚本与 `Inputs` 先复制到私有暂存目录计算摘要，并在真正启动进程前二次重算哈希比对，防止等待审批期间替换内容。
   - 注意：`requestDigest` 不覆盖解释器、包管理器或构建工具（如 `dotnet`、`npm`、`python`）在运行时自行从工作区读取的项目源码文件。因此这类命令在 `exec_cmd` / `run_tests` 中仅提供单次授权 `allow_once`，不提供 `allow_session` 或 `allow_always`。
3. **双平面凭据与防模型自批**
   - **普通面**（`ActiveUser`）：仅能发起对话、订阅 SSE、读取历史与待审批列表。
   - **控制面**（`ControlPlane`）：工具执行审批、计划结构化审批（`approve` / `execute`）、放宽权限模式或切换执行后端、管理持久权限规则。普通凭据调用控制面一律返回 `403 Forbidden`。
   - **托管模式（`Local:TokenDelivery = Pipe`）**：桌面客户端通过子进程 `stdin` 管道一次性传入普通凭据与控制凭据，不落盘、不写入环境变量。
   - **独立运行模式（`Local:TokenDelivery = File`）**：`Backend = Local` 且未开启 `Local:UnsafeFileControlToken` 时，宿主默认不生成 `control.token`，`Ask` 请求以 `approval_unavailable` 快速失败，确保本地命令无法从磁盘读取控制凭据完成自批。

## 2. 已知残余风险

当用户在本地模式下**批准**某条命令运行后，由于操作系统层面不存在用户态隔离，存在以下两项超出本地无沙箱模式防护范围的已知残余风险：

1. **已获批准进程修改用户级配置文件**
   - 已获批准运行的进程拥有当前用户权限，可能修改用户主目录下的文件（包括 `~/.codexflow/appsettings.json` 或 `~/.codexflow/permissions.json`）。
   - 缓解措施：运行期配置热加载受到 `ExecutionPermissionClamp` 约束，文件改动**只能收紧权限模式、绝不能在当前运行期内放宽权限模式**（放宽只能走控制面 API），因此篡改配置文件最多在下次重启进程时生效。
2. **同用户进程内存读取**
   - 在未启用跨用户或容器隔离的同一操作系统会话中，已获批准运行的进程理论上可通过操作系统调试接口读取后端或客户端进程内存以窃取内存中的控制凭据。

**建议**：处理不受信任的仓库、第三方脚本或需要防范上述残余风险的场景时，请将执行后端切换为 **`Docker`**（无网络、只读根文件系统、受限用户与资源配额）。
