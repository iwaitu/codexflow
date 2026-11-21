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
2. **Isolated World DOM 检查、跨 Frame 隔离与敏感字段截屏遮罩**
   - 元素编号表（`globalThis.__cfRefMap`）仅驻留于 CDP `Page.createIsolatedWorld` 隔离世界，页面脚本不可读取或篡改；支持同源/`srcdoc` `iframe` 内联及 `Target.setAutoAttach` 跨源 OOPIF 融合与独立 Isolated World 路由，跨源交互审批强制绑定子 Frame 真实 `Origin`；
   - `browser_act` 的 `type` / `clear` / `press_key` 在执行前通过 Isolated World 校验目标与实时 `activeElement`（含 Shadow DOM 与受管子 Frame 焦点穿透），禁止向密码、OTP、支付卡字段及不受管跨源 `iframe` 输入字符，并对 `file_input` 禁止普通 `click` 触发原生系统文件对话框（强制走受控 `upload_file`）；
   - 点击前使用 `deepElementFromPoint` 跨层级校验元素未被遮挡（OOPIF 同步复核顶层点击命中标签防点击劫持），并在审批完成后同时复核实时 DOM 属性及 `window.location.href` 防止 TOCTOU 与导航漂移；
   - 所有截屏入口在渲染捕获前自动采集可见密码/OTP/支付输入框视口矩形，通过 Closed ShadowRoot + Constructable Stylesheet + 全视口 `<canvas>` 绘制不透明 `[PROTECTED]` 遮罩后再捕获图像并于 `finally` 清除，从视觉通道上阻止敏感凭据进入多模态模型与前端快照缓存；可选开启 `annotate_screenshot: true` 叠加 `[e1]` 框。
3. **主上下文 JS 求值（`browser_evaluate`）、受控文件上传/下载与人工接管**
   - `browser_evaluate` 在活跃页面主上下文运行并共享同源 DOM/Cookie（非隔离沙箱），在 Plan Mode 与后台 Worker 下禁用，每次调用**强制触发控制面 `allow_once` 单次审批**（禁止 `allow_session`），审批前后复核实时 Origin，结果经防循环引用安全序列化并剥离伪造 `<codexflow_visual` 后由 `UntrustedText` 包裹；
   - `upload_file` 由 `BrowserWorkspacePathGuard` 执行 OS 级规范路径解析（Windows `GetFinalPathNameByHandleW` / Unix 逐级符号链接解析，拒绝系统根目录与硬链接数 > 1 文件，大小上限 50MB），在审批前即拷贝生成会话私有只读暂存副本并计算 SHA-256 摘要，随后触发**强制单次控制面审批**（`allow_once`）；
   - 浏览器下载默认调用 `Browser.setDownloadBehavior(deny)`（fail-closed）并强制限制单文件下载上限（200MB 超限取消）；当通过配置关闭 `AllowControlledDownloads` 时实时向活跃上下文下发 `deny`；仅当显式开启 `AllowControlledDownloads`（且非远端模式）时重定向至规范校验后的 `<WorkspaceRoot>/outputs/browser-downloads/`；
   - 桌面有头模式（`Headless = false`）平时在 Windows 置于屏外负坐标（macOS/Linux 自动回退最小化窗口），支持通过 `browser_request_takeover` 或桌面面板无损唤出窗口进行人工登录/验证码操作；多弹窗经 `Target.setDiscoverTargets` 自动挂载，`RuntimeToolTimeouts` 为人工接管与交互/审批操作分配延长预算（8–16 分钟），接管完成后通过控制面显式交回 Agent。
4. **远端 CDP Grid 驱动安全边界（`RemoteCdpWsUrl`）**
   - 仅接受直连 `ws://` / `wss://` 端点（拒绝 `http://` `/json/version` 二次跳转 SSRF），连接前执行 DNS 解析与已校验 IP 直连（阻断云元数据、受保护本机端口与 DNS 重绑定回环），配置热重载（`BrowserPermissionClamp`）禁止磁盘篡改端点，控制面设置回显自动脱敏 Query/UserInfo 凭据；
   - 远端会话禁用本地 `BrowserOutboundProxy`，改为强制 CDP `Fetch.enable`（`urlPattern: "*"`）逐请求检验域名与目标 IP；强制下载 `deny`，禁止本地持久 Profile、人工窗口接管（`remote_cdp_no_takeover`）与本机文件上传（`remote_cdp_upload_unsupported`），销毁会话仅断开连接而不终止远端浏览器进程。
5. **配置热加载收紧优先（`BrowserPermissionClamp`）**
   - 与 `ExecutionPermissionClamp` 一致，运行期磁盘文件重载只能收紧浏览器权限（禁用浏览器、切回无头/临时隔离、关闭受控下载、收紧审批策略或域名白名单、不可篡改自定义路径或远端 CDP 端点），放宽必须走控制面 API。

### 3.2 已知残余风险

1. **同用户本地进程访问受管 CDP 调试端口**
   - 本机受管浏览器使用 `--remote-debugging-address=127.0.0.1 --remote-debugging-port=0` 监听随机回环端口。虽然该端口已注册进 `ProtectedLocalPortRegistry`（浏览器自身及宿主受控出站无法访问），但在无跨用户/容器隔离的桌面会话中，同一操作系统用户下已运行的其他恶意本地进程理论上可直接连接该本地 CDP 端口。
2. **远端 CDP Grid 网络出站边界取决于远端宿主机环境**
   - 当连接外部企业 CDP Grid 时，若网页通过 WebRTC / 底层传输通道发起非标准 `Fetch` 拦截范围之外的网络请求，其网络隔离能力取决于远端 Grid 容器自身的 VPC/防火墙策略；同时建议生产环境对远端 CDP 端点启用 TLS（`wss://`）。
3. **持久化 Profile（`BrowserProfileMode = Persistent`）的凭据暴露面**
   - 启用持久化模式后，登录态 Cookie/Token 存放在专用目录 `~/.codexflow/browser-profiles/default`（Unix `0700` 权限，通过文件锁与原子所有权锁定保证同一时间仅 1 个会话占用；会话关闭时主动保留守护页并清理非临时标签页）。
   - CodexFlow 在持久化模式下对**首次导航至非 loopback Origin（`persistent_nav` 类别）**及**所有非 loopback 网站写操作（`interact` 类别，单次审批）**分别执行独立桶审批门禁，但已登录站点的页面读取结果仍会进入模型上下文（受 `<untrusted_data>` 包裹），且持久化目录同样面临同用户本地恶意进程读取的操作系统级风险。建议仅在确需复用登录态的受信任工作流中启用。

---

## 4. 内置 Computer Use（Windows x64 / macOS）安全边界、权限模型与残余风险

CodexFlow 在**日常模式（`Daily`）**与**开发模式（`Development`）**主会话用户轮次中提供基于纯视觉截图定位的桌面键鼠操控能力，通过原生 C# P/Invoke（无外部 Python/脚本依赖，完全兼容 .NET 10 Native AOT）支持 Windows x64（Win32/GDI+）与 macOS arm64/x64（CoreGraphics/CGEvent/`/usr/sbin/screencapture`），核心安全防线与平台差异如下：

### 4.1 安全边界

1. **模式、角色与模型多模态硬门禁**
   - **Plan Mode 与后台 Worker 禁用**：`PlanSafeToolPolicy` 屏蔽所有 `ComputerInteract`（`computer_act`、`computer_request_takeover`）；`AmbientComputerCallerContext` 与各工具入口强制拒绝后台 Worker/子代理调用桌面操控工具。
   - **非多模态模型硬拒绝**：`computer_observe` 与 `computer_act`（带自动 350ms 后重抓截屏）在执行前校验当前模型支持多模态图像输入（`TurnVisualAttachmentContext.Current?.IsMultimodalEnabled`），禁止在纯文本大模型下盲点盲输。
   - **滑动视觉窗口**：桌面截屏注册为 `computer-screenshot` 滑动分类附件，仅保留最近 **3 帧** 供模型决策参考，防止长任务图片持续膨胀上下文预算。
2. **宿主 Self-UI 防自批与双层遮罩**
   - 客户端在就绪后经控制面 `POST /api/gateway/computer/protected-windows` 上报主进程 PID 与所有受保护窗口原生句柄（含主窗口与悬浮置顶停止胶囊窗）。
   - `Win32ComputerNative`（`CreateToolhelp32Snapshot` 进程树遍历）与 `MacOsComputerNative` 结合 `ComputerSecurityGuard` 在截屏捕获阶段定位同进程树及客户端保护句柄可见矩形，由 `ComputerVisualProcessor` 使用纯托管像素缓冲涂黑并标记 `[CODEXFLOW PROTECTED]`。
   - 所有桌面点击类操作在落点周边执行 **`±4px` 9 宫格多点探测**；只要任意探测点命中 CodexFlow 自身进程树、客户端保护窗口、敏感窗口列表、或位于当前窗口 Z-Order 之上的高危系统进程，立即硬拒绝，从根源阻断模型点击「允许执行」按钮完成自我审批。
3. **高敏进程屏蔽与高危快捷键冻结机制**
   - `BlockedProcessNames` 默认覆盖密码管理器、系统身份授权窗口与凭据保险箱（`1password`、`keepass`、`bitwarden`、`lastpass`、`dashlane`、`securityagent`、`consent`、`credentialuibroker` 等），命中的窗口既禁止 `pin`，也禁止任何鼠标点击或字符注入；
   - `Win+R` / `Win+L` / `Win+X` / `Ctrl+Alt+Del` / `Alt+F4` / `Cmd+Q` / `Cmd+Space` / `Cmd+Tab` 等高危系统级热键受分类策略管制（高危系统键硬拦截，高风险修改/跨应用热键强制单次人工审批）；
   - **`AlwaysAsk` / `HighRiskOnly` / `AppScopedInSession` 分层审批 + 待批屏幕冻结 + TOCTOU 复核**：当触发审批时，系统截取带红圈靶心的局部预览图并通过 `approval_requested` 呈送控制面，同时记录当前桌面目标句柄与前后像素签名（待批期间若人工或外部弹窗打断导致目标漂移、Z-Order 遮挡变化或画面哈希显著漂移，审批通过后也拒绝执行）。
4. **物理桌面全局排他租约锁与四层紧急停止体系**
   - `ComputerSessionManager` 维护单一本机桌面物理键鼠独占租约锁（同一时刻仅允许 1 个活跃会话持有桌面控制权，空闲超时自动回收）。
   - 四层紧急中断机制并存：
     1. 宿主原生底层非注入按键钩子：Windows `WH_KEYBOARD_LL` 检测 `!LLKHF_INJECTED` 物理 `Esc` 双击（450ms 内连按两次 `Esc`）、macOS `CGEventTap` 检测 `kCGEventSourceUserData != CodexFlowEventTag` 物理 `Esc` 双击；
     2. 操控期 Electron 主进程动态注册 `CommandOrControl+.` 全局热键（非操控期不长期占用系统快捷键）；
     3. 顶部无焦点置顶停止胶囊窗（`focusable: false`，点击 `[■ 停止]` 直接经控制面熔断）；
     4. 应用聚焦时排除中文 IME 合成态（`isComposing` / `keyCode === 229`）与弹层态后的 `Esc` / `Ctrl+.` + 物理鼠标大幅移动让权探测（`>120px` 连续用户位移自动切回 `takeover` 或停止）。

### 4.2 双平台系统权限边界与已知残余风险

1. **Windows x64 边界（UIPI 权限隔离）**
   - 当 CodexFlow 以标准非提权用户（Medium Integrity Level）运行时，Windows UIPI（User Interface Privilege Isolation）原生阻断 `SendInput` 向管理员权限窗口（如提权终端、UAC `consent.exe`）注入键鼠消息；`Win32ComputerNative` 通过 DPI Awareness v2 与无偏 `[0, 65535]` 绝对坐标映射到多显示器虚拟桌面（含主屏左侧/上方负坐标）。
   - **残余风险**：标准用户权限下可操控的其他普通桌面对话窗口（如文件管理器、IDE、邮件客户端）拥有与当前用户同等的桌面交互能力；一旦开启 Computer Use 并授权会话级放行，模型在受控应用内的操作相当于代表当前登录用户执行。
2. **macOS arm64/x64 边界（TCC 隐私授权）**
   - CoreGraphics/CGEvent 在 macOS 下严格受 TCC（Transparency, Consent, and Control）约束：需用户在「系统设置 → 隐私与安全性」显式授予宿主应用及客户端「屏幕录制（Screen Recording）」与「辅助功能（Accessibility）」权限，且全局物理 `Esc×2` `CGEventTap` 也依赖「辅助功能 / 输入监控」权限。
   - 若缺失任一项 TCC 权限，`MacOsComputerDriver` 在状态探测与首调截屏/键鼠操作时主动降级或返回清晰权限指引，不会静默假成功；截屏采用系统原生 `/usr/sbin/screencapture -x -t bmp` 规避 macOS 15+ `CGDisplayCreateImage` 废弃兼容问题。
3. **同会话恶意视觉提示注入（Prompt Injection via Screen Content）**
   - 与 Web 页面类似，屏幕上显示的不受信任应用文本、弹窗提示或文档截图内容进入多模态模型视觉上下文后，可能诱导模型做出非预期点击。
   - 缓解措施：高危操作与跨窗口跳转仍受控制面审批、`BlockedProcessNames`、系统级热键过滤与用户四层随时急停制约；配置热加载同样严格遵循 `ComputerPermissionClamp`（只收紧不放宽）。建议处理高度敏感数据时保持默认 `HighRiskOnly` 或切换为 `AlwaysAsk`。
