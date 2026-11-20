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

---

## 3. 内置 Web Browser Use 安全边界与残余风险

CodexFlow 通过原生 .NET CDP 引擎管理本机 Chromium 族浏览器（Chrome / Edge / Chromium / Brave），其安全防线与已知边界如下：

### 3.1 安全边界

1. **宿主内置出站 HTTP/CONNECT 代理（`BrowserOutboundProxy` + `BrowserTargetPolicyGuard`）**
   - 浏览器进程与每个会话的独立 `BrowserContext` 均强制通过本机代理发起网络请求（`--proxy-bypass-list=<-loopback>`、`--disable-quic`、`--force-webrtc-ip-handling-policy=disable_non_proxied_udp`）。
   - 代理在每次建连前执行 DNS 解析、剥离 IPv6 zone-id、解包 IPv4-mapped / NAT64 / 6to4 / 八进制及十六进制 IPv4，并对已解析 IP 逐一校验后直连已校验 IP：
     - 永久拦截云元数据（`169.254.169.254`、`100.100.100.200`、`168.63.129.16`、`fd00:ec2::254` 等）、CodexFlow 受保护本地端口（`ProtectedLocalPortRegistry` 中的宿主 API 端口与活跃 CDP 端口）以及非回环外部域名 DNS 重绑定到 `127.0.0.1`/`::1`；
     - 日常模式（`Daily`）额外拦截 RFC1918 私网、CGNAT、ULA 与默认 `localhost`；开发模式（`Development`）默认仅放行前端开发端口白名单，其余本机端口需经控制面审批；
     - 拒绝双 `Content-Length`、`Transfer-Encoding + Content-Length` 冲突与裸 CR/LF 请求走私。
2. **Isolated World DOM 检查与防密码/点击劫持绕过**
   - 元素编号表（`globalThis.__cfRefMap`）仅驻留于 CDP `Page.createIsolatedWorld` 隔离世界，页面脚本不可读取或篡改；
   - `browser_act` 的 `type` / `clear` / `press_key` 在执行前通过 Isolated World 校验目标与实时 `activeElement`（含 Shadow DOM 穿透），禁止向密码、OTP、支付卡字段及跨源 `iframe` 输入字符，并对 `file_input` 禁止普通 `click` 触发原生系统文件对话框（强制走受控 `upload_file`）；
   - 点击前使用 `deepElementFromPoint` 校验元素未被遮挡，并在审批完成后同时复核实时 DOM 属性及 `window.location.href` 防止 TOCTOU 与导航漂移。
3. **受控文件上传、下载与人工接管**
   - `upload_file` 由 `BrowserWorkspacePathGuard` 执行 OS 级规范路径解析（Windows `GetFinalPathNameByHandleW` / Unix 逐级符号链接解析，拒绝系统根目录与硬链接数 > 1 文件，大小上限 50MB），在审批前即拷贝生成会话私有只读暂存副本并计算 SHA-256 摘要，随后触发**强制单次控制面审批**（`allow_once`）；
   - 浏览器下载默认调用 `Browser.setDownloadBehavior(deny)`（fail-closed）并强制限制单文件下载上限（200MB 超限取消）；当通过配置关闭 `AllowControlledDownloads` 时实时向活跃上下文下发 `deny`；仅当显式开启 `AllowControlledDownloads` 时重定向至规范校验后的 `<WorkspaceRoot>/outputs/browser-downloads/`；
   - 桌面有头模式（`Headless = false`）平时在 Windows 置于屏外负坐标（macOS/Linux 自动回退最小化窗口），支持通过 `browser_request_takeover` 或桌面面板无损唤出窗口进行人工登录/验证码操作；多弹窗经 `Target.setDiscoverTargets` 自动挂载，`RuntimeToolTimeouts` 为人工接管与审批操作分配延长预算（8–16 分钟），接管完成后通过控制面显式交回 Agent。
4. **配置热加载收紧优先（`BrowserPermissionClamp`）**
   - 与 `ExecutionPermissionClamp` 一致，运行期磁盘文件重载只能收紧浏览器权限（禁用浏览器、切回无头/临时隔离、关闭受控下载、收紧审批策略或域名白名单），放宽必须走控制面 API。

### 3.2 已知残余风险

1. **同用户本地进程访问受管 CDP 调试端口**
   - 受管浏览器使用 `--remote-debugging-address=127.0.0.1 --remote-debugging-port=0` 监听随机回环端口。虽然该端口已注册进 `ProtectedLocalPortRegistry`（浏览器自身及宿主受控出站无法访问），但在无跨用户/容器隔离的桌面会话中，同一操作系统用户下已运行的其他恶意本地进程理论上可直接连接该本地 CDP 端口。
2. **持久化 Profile（`BrowserProfileMode = Persistent`）的凭据暴露面**
   - 启用持久化模式后，登录态 Cookie/Token 存放在专用目录 `~/.codexflow/browser-profiles/default`（Unix `0700` 权限，通过文件锁与原子所有权锁定保证同一时间仅 1 个会话占用；会话关闭时主动保留守护页并清理非临时标签页）。
   - CodexFlow 在持久化模式下对**首次导航至非 loopback Origin（`persistent_nav` 类别）**及**所有非 loopback 网站写操作（`interact` 类别，单次审批）**分别执行独立桶审批门禁，但已登录站点的页面读取结果仍会进入模型上下文（受 `<untrusted_data>` 包裹），且持久化目录同样面临同用户本地恶意进程读取的操作系统级风险。建议仅在确需复用登录态的受信任工作流中启用。
