[English](prepare-restore-cache.md) | 简体中文

# prepare-restore-cache.py — 为验收沙箱构建可信的离线 NuGet 恢复缓存

本脚本用于创建隔离后端以只读方式挂载到容器内 `/restore-cache`（配置项 `Isolation:RestoreCacheHostPath`）的 NuGet 恢复缓存，使无网络验证容器（`--network none`）可以离线执行 `dotnet restore`。**请勿使用 `prepare-audit-cache.py` 代替本脚本**：后者仅用于测试，会刻意构造含已知漏洞的攻击夹具。

> **适用范围（Docker 隔离后端 vs. Local 本地后端）**：本缓存用于开启容器隔离后端（`Execution:Backend = Docker`）时的离线依赖恢复与容器验收。如果只使用无容器的 `Local` 本地执行后端，宿主不会挂载 `/restore-cache`，无需准备此缓存。

## 安全与完整性保证

* **严格限定输入**：仅接受一份经过人工审核的 JSON 清单，且所有包必须指定精确的 `id` 与 `version`（拒绝版本区间、通配符、浮动版本、重复包名、未知字段以及缺失 `reviewedBy` / `reviewedAtUtc` 的清单）。脚本从不读取、解析或执行目标业务仓库的任何文件。
* **隔离生成与纯净源**：脚本会在本地已固定的验收镜像中（从不自动拉取镜像）自行生成最小化 `.csproj`（全部使用 `[x.y.z]` 精确版本引用）以及仅包含 `<clear/>` 和 `nuget.org` 的 `nuget.config`，并以 `HOME=/tmp/h` 运行恢复。容器仅挂载新建的空输出目录，不挂载任何宿主机的 `NuGet.config`、凭据或全局包缓存。缓存构建阶段容器有网络访问；实际运行任务与验收时容器保持 `--network none`。
* **宿主机二次校验与清单签名**：恢复完成后，宿主机脚本会逐包扫描输出目录，确认每个请求根包均以精确版本存在、每个包的 `.nupkg.metadata` 来源均为 `https://api.nuget.org/v3/index.json`，并写入 `restore-cache-manifest.json`（记录镜像 ID、输入清单 SHA-256、包含传递依赖在内的全部包的 `.nupkg` SHA-256 以及总摘要 `packagesSha256`）。
* **失败即中止**：出现任何恢复错误、缺包、非官方源或输出目录非空时，脚本均以退出码 `1` 失败。缓存先在临时暂存目录构建，全部校验通过后才原子移动到目标路径。

## 输入格式（`schema: 1`）

通用顶层字段：`reviewedBy`（必填）、`reviewedAtUtc`（必填，实际审核时的 ISO-8601 **UTC** 时间戳）、`targetFramework`（可选，默认 `net10.0`）。任何未知字段都会被拒绝。`packages` 与 `groups` **必须且只能二选一**：

* `packages`：`[ {"id": "...", "version": "x.y.z"} ]` —— 生成单个项目统一恢复全部包（适合单项目或单一依赖图，单组上限 500 个包）。
* `groups`：`[ {"name": "<project>", "packages": [ {id, version}, ... ], "sdkPackages": [ {id, version}, ... ]} ]` —— **每个分组生成独立项目**并恢复到同一缓存目录。`name` 需匹配 `[A-Za-z0-9._-]`（长度 1–64，忽略大小写不可重复，最多 64 组）；同一分组内包 `id` 不可重复（不同分组之间允许重复且允许不同版本）。`sdkPackages` 为可选字段。

### 为什么需要按项目分组（闭包规则）

NuGet 按**单个项目**解析传递依赖版本（遵循该项目的最近优先原则，包括通过 `ProjectReference` 传递进来的依赖）。如果把多项目仓库的所有包合并到一个项目中恢复，合并后的依赖图可能会统一选出*更高*的传递依赖版本，导致真实项目在离线容器中恢复时找不到其所需的较低版本（实测案例：`CleanApp.ServiceDefaults` 需要 5 个 `10.0.0` 版本的 `Microsoft.Extensions.*` 包，而合并恢复将其向上统一到了更高版本）。因此请为每个真实项目建一个分组，并在该分组的 `packages` 中列出该项目自身的 `PackageReference` **及其完整 `ProjectReference` 闭包内的所有 `PackageReference`**（保持 `.csproj` 中的精确版本号）。最终请结合真实项目的 `--network none` 副本恢复做端到端验证，而非仅检查清单。

### `sdkPackages`（`PackageDownload`）

对于通过 `<Sdk Name="X" Version="Y"/>` 或 `Sdk="X/Y"` 引用的 MSBuild SDK 包：它们无法作为 `PackageReference` 引用（会触发 `NU1213`，包类型为 `DotnetPlatform`）。本脚本会将其生成为精确版本的 `<PackageDownload Include="X" Version="[Y]"/>`：仅下载包并校验其 `nuget.org` 来源，构建缓存时**不会**求值或执行该 SDK。此外，部分 SDK 会添加由自身决定的**隐式**依赖包（例如 Aspire AppHost 13.1.0 会隐式引入同版本的 `Aspire.Dashboard.Sdk.linux-x64` 和 `Aspire.Hosting.Orchestration.linux-x64`），这些也必须一并列入 `sdkPackages`。在离线容器中，NuGet SDK 解析器只读取*全局*包目录（不会读取 fallback 目录），因此宿主缓存必须作为只读的全局包目录挂载（`NUGET_PACKAGES=/restore-cache`）——详见 `.committee-review/claude-ro-global-cache-experiment.md`。以 `scripts/e2e/fixtures/restore-cache-cleanapp-bug.packages.json` 中的 AppHost 分组为例：

```json
{ "name": "CleanApp.AppHost",
  "packages": [ {"id": "Aspire.Hosting.AppHost", "version": "13.1.0"} ],
  "sdkPackages": [ {"id": "Aspire.AppHost.Sdk", "version": "9.5.0"},
                   {"id": "Aspire.Dashboard.Sdk.linux-x64", "version": "13.1.0"},
                   {"id": "Aspire.Hosting.Orchestration.linux-x64", "version": "13.1.0"} ] }
```

最终生成的清单会记录 `roots`（所有 `packages` 与 `sdkPackages`）、`groups`，以及缓存中每一个已恢复包的 `.nupkg` SHA-256。

## `reviewedAtUtc` 必须填写真实 UTC 审核时间

不要填写未来时间或仅写日期的占位符。（最早的两份示例清单曾写为 `2026-11-08T00:00:00Z`，晚于实际 `2026-11-07T17:18Z` 的构建时间；现已修正为实际 UTC 审核时间 `2026-11-07T17:35:31Z`。这仅调整元数据，包 ID 与版本未变。早期冻结的 cleanapp 缓存 `inputListSha256` `579968cc…` 对应归档原始输入 `.committee-review/restore-cache-input-cleanapp-bug.original.json`。）

## 环境变量说明

| 环境变量 | 是否必填 | 说明 |
| --- | --- | --- |
| `ARTIFACT_AUDIT_IMAGE` | 构建时必填 | 本地已存在的验收镜像名称/标签（脚本从不拉取），例如 `codexflow-verification:10.0.403-gitleaks8.30.1`。 |
| `ARTIFACT_AUDIT_CACHE_PACKAGES` | 构建时必填 | 已审核包清单 JSON 文件路径，示例见 `scripts/e2e/fixtures/restore-cache-clean.packages.json`。 |
| `ARTIFACT_AUDIT_CACHE_OUT` | 始终必填 | 缓存输出目录。构建模式要求是**新建或空目录**；仅校验模式指向已有缓存目录。 |
| `ARTIFACT_AUDIT_DOCKER` | 可选 | 容器 CLI 可执行文件名或路径，默认 `docker`。可设为 `podman` 等兼容 CLI。 |
| `ARTIFACT_AUDIT_CACHE_VERIFY_ONLY` | 可选 | 设为 `1` 时进入离线复核模式：根据 `restore-cache-manifest.json` 校验现有缓存，无需网络也无需容器运行时。 |

## 前置条件与支持的环境

* **Python**：宿主机需安装 Python 3。
* **本地验收镜像**：构建缓存前需已在本地构建固定版本验收镜像（脚本不会自动 `pull`）：
  ```bash
  docker build -t codexflow-verification:10.0.403-gitleaks8.30.1 docker/verification
  ```
* **支持的宿主机与容器运行时**：
  * **Windows**：支持 **Docker Desktop**（WSL2 或 Hyper-V 后端），推荐在 **PowerShell** 中运行；若在 **Git Bash / MSYS2** 中运行，需设置 `MSYS_NO_PATHCONV=1`，防止 MSYS2 自动改写 `-v <staging>:/cache` 的路径参数。确保输出目录位于容器运行时允许挂载的驱动器或路径下。
  * **Linux**：支持 **Docker Engine**（rootful 或 rootless）。由于脚本会在 `os.name == "posix"` 时自动附带 `--user <uid>:<gid>`，生成在宿主机目录的缓存文件会归属当前用户，并在容器末尾执行 `chmod -R a+rX /cache`，确保后续只读挂载的离线验收容器用户可读。若系统启用了 SELinux 且有挂载标签策略限制，请确认宿主输出目录具备对应的容器读写权限。
  * **macOS**：支持 **Docker Desktop for Mac** 及兼容 Docker CLI 的运行时（如 OrbStack、Colima 等），同样遵循 POSIX `--user <uid>:<gid>` 行为；输出目录需位于已共享给容器 VM 的路径下（如用户主目录内）。
  * **其他兼容容器运行时**：可通过 `ARTIFACT_AUDIT_DOCKER` 指定其它兼容 Docker CLI 的工具（如 `ARTIFACT_AUDIT_DOCKER=podman`），前提是支持 `<cli> image inspect --format "{{.Id}}" <image>`、`<cli> run --rm -i -v <dir>:/cache --entrypoint sh` 以及（在 POSIX 上）`--user <uid>:<gid>`。注意：若后端隔离服务本身也要使用非 `docker` 的可执行文件，请同步配置 `Isolation:DockerExecutable`。
  * **纯离线校验（全平台，无需 Docker）**：设置 `ARTIFACT_AUDIT_CACHE_VERIFY_ONLY=1` 时，脚本仅使用 Python 标准库扫描本地目录并与 `restore-cache-manifest.json` 比对校验，不调用容器运行时也不访问网络，可在任何平台上直接运行（如 CI 或无 Docker 的审计节点）。

## 用法示例

### Linux / macOS（bash / zsh）

```bash
export ARTIFACT_AUDIT_IMAGE=codexflow-verification:10.0.403-gitleaks8.30.1
export ARTIFACT_AUDIT_CACHE_PACKAGES=scripts/e2e/fixtures/restore-cache-clean.packages.json
export ARTIFACT_AUDIT_CACHE_OUT=/path/to/NEW-empty-cache-dir
python3 scripts/e2e/prepare-restore-cache.py

# 之后 / 在 CI 中离线校验（检测新增、缺失或被篡改的包，无需 Docker 和网络）：
ARTIFACT_AUDIT_CACHE_OUT=/path/to/cache ARTIFACT_AUDIT_CACHE_VERIFY_ONLY=1 python3 scripts/e2e/prepare-restore-cache.py
```

### Windows（PowerShell）

```powershell
$env:ARTIFACT_AUDIT_IMAGE = 'codexflow-verification:10.0.403-gitleaks8.30.1'
$env:ARTIFACT_AUDIT_CACHE_PACKAGES = 'scripts/e2e/fixtures/restore-cache-clean.packages.json'
$env:ARTIFACT_AUDIT_CACHE_OUT = 'D:\path\to\NEW-empty-cache-dir'
python scripts/e2e/prepare-restore-cache.py

# 之后离线校验（无需 Docker 和网络）：
$env:ARTIFACT_AUDIT_CACHE_OUT = 'D:\path\to\cache'
$env:ARTIFACT_AUDIT_CACHE_VERIFY_ONLY = '1'
python scripts/e2e/prepare-restore-cache.py
```

### Windows（Git Bash / MSYS2）

```bash
export MSYS_NO_PATHCONV=1
export ARTIFACT_AUDIT_IMAGE=codexflow-verification:10.0.403-gitleaks8.30.1
export ARTIFACT_AUDIT_CACHE_PACKAGES=scripts/e2e/fixtures/restore-cache-clean.packages.json
export ARTIFACT_AUDIT_CACHE_OUT=/d/path/to/NEW-empty-cache-dir
python scripts/e2e/prepare-restore-cache.py
```

### 使用自定义容器运行时（如 Podman）

```bash
export ARTIFACT_AUDIT_DOCKER=podman
export ARTIFACT_AUDIT_IMAGE=localhost/codexflow-verification:10.0.403-gitleaks8.30.1
export ARTIFACT_AUDIT_CACHE_PACKAGES=scripts/e2e/fixtures/restore-cache-clean.packages.json
export ARTIFACT_AUDIT_CACHE_OUT=/path/to/NEW-empty-cache-dir
python3 scripts/e2e/prepare-restore-cache.py
```

构建完成后，将 `Isolation:RestoreCacheHostPath` 指向该输出目录。使用前请先审核生成的 `restore-cache-manifest.json`（其中包含所有传递依赖包）；需要更新缓存时请新建目录重新构建，切勿直接修改正在使用中的缓存目录。

## 实测验证记录

* **2026-11-08，Windows + Docker Desktop，镜像 `10.0.403-gitleaks8.30.1`**：
  * 使用干净测试夹具 `restore-cache-clean.packages.json`（`Newtonsoft.Json` 13.0.3、`Serilog` 4.1.0，均不在 NuGet 漏洞公告范围内）构建到外部目录：退出码 `0`，共 2 个包，`packagesSha256=6de38b08…`；`VERIFY_ONLY` 校验退出码 `0`；在 `--network none` 隔离容器中基于该缓存成功恢复同时依赖这两个包的项目（退出码 `0`）。
  * 负向测试用例全部以退出码 `1` 拒绝：浮动版本 `13.*`、版本区间、重复包名、不存在的版本 `999.0.0`、不存在的包名、包名注入尝试、缺失审核字段、未知 JSON 字段、非空输出目录，以及校验阶段被篡改的 `.nupkg`。
* 说明：Linux、macOS 及通过 `ARTIFACT_AUDIT_DOCKER` 接入其他兼容容器运行时的行为以上述脚本源码逻辑为准，尚未单独留存同规格的端到端实测归档。
