# Codex Tools Test Suite

## 概述
这是一套完整的工具集成测试，用于验证所有 Codex 工具在默认工作空间中的功能。这些测试特别关注用户报告的"Git 克隆后找不到文件"的问题。

## 测试文件

### 1. `ToolsIntegrationTests.cs`
**目的**: 测试所有工具的基本功能

**测试内容**:
- ✅ ListWorkspaceTool - 列出工作空间文件
- ✅ ReadFileTool - 读取文件内容
- ✅ WriteFileTool - 创建/更新文件
- ✅ DeleteFileTool - 删除文件
- ✅ CreateDirectoryTool - 创建目录
- ✅ SearchInFilesTool - 搜索文件内容
- ✅ ExecCodeTool - 执行 Python 代码
- ✅ RunCommandTool - 执行系统命令
- ✅ ZipDirectoryTool - 压缩目录
- ✅ DownloadArtifactTool - 下载产物
- ✅ GitWorkspaceTool - Git 操作（Mock）
- ✅ StructuralLsTool - 结构化列表
- ✅ StructuralReadTool - 结构化读取
- ✅ SmartPatchTool - 智能补丁

**工作流测试**:
- 完整的创建→写入→列表→读取→搜索→删除流程

### 2. `GitToolsRealIntegrationTests.cs`
**目的**: 真实 Git 集成测试（需要网络和 Git 客户端）

**测试内容**:
- ✅ GitService.CloneRepository - 克隆真实仓库
- ✅ GitWorkspaceTool.Clone - 使用工具克隆
- ✅ 克隆后文件可访问性验证
- ✅ 默认工作空间路径验证

**测试仓库**:
- microsoft/calculator (小型公共仓库)
- iwaitu/CleanApp (用户的测试仓库)

### 3. `ToolWorkspaceDiagnosticTests.cs`
**目的**: 诊断工具的工作空间路径行为

**诊断测试**:
- ✅ Git 克隆完整路径验证
- ✅ 克隆后列表工具验证
- ✅ 所有工具路径解析验证
- ✅ 详细的目录树输出

## 运行测试

### 运行所有测试
```bash
# 在解决方案根目录
dotnet test CodexFlow.Core.Tests

# 或运行特定测试项目
dotnet test CodexFlow.Core.Tests/CodexFlow.Core.Tests.csproj
```

### 运行特定测试类
```bash
# 运行工具集成测试
dotnet test --filter FullyQualifiedName~ToolsIntegrationTests

# 运行 Git 真实集成测试（需要网络）
dotnet test --filter FullyQualifiedName~GitToolsRealIntegrationTests

# 运行诊断测试
dotnet test --filter FullyQualifiedName~ToolWorkspaceDiagnosticTests
```

### 运行单个测试
```bash
# 运行特定测试方法
dotnet test --filter "FullyQualifiedName~GitToolsRealIntegrationTests.GitWorkspaceTool_Clone_ShouldCloneToDefaultWorkspace_Successfully"
```

### 查看详细输出
```bash
# 显示详细日志
dotnet test --logger "console;verbosity=detailed"

# 或使用 xUnit 输出
dotnet test -- xUnit.ShowProgress=true
```

## 测试环境要求

### 基础测试（ToolsIntegrationTests）
- ✅ .NET 10 SDK
- ✅ 文件系统权限
- ⚠️ Python 3（可选，ExecCodeTool 测试会跳过如果未安装）

### Git 集成测试（GitToolsRealIntegrationTests）
- ✅ .NET 10 SDK
- ✅ Git 客户端已安装（`git --version` 可用）
- ✅ 网络连接（访问 GitHub）
- ✅ 文件系统权限

### 诊断测试（ToolWorkspaceDiagnosticTests）
- ✅ .NET 10 SDK
- ✅ Git 客户端（可选，会显示详细错误信息）
- ✅ 网络连接（可选）

## 测试输出示例

### 成功的测试输出
```
=== Diagnostic Test Workspace: /tmp/codex_diagnostic_abc123 ===

### TEST: Git Clone Full Path Verification ###

Workspace Path: /tmp/codex_diagnostic_abc123
Repository URL: https://github.com/iwaitu/CleanApp.git
Expected clone path: /tmp/codex_diagnostic_abc123/repo

Executing git_clone tool...

Tool Status: Success
Tool Output:
✅ 仓库已成功拉取至 /tmp/codex_diagnostic_abc123/repo
- 文件数: 42
- 目录数: 8
- 仓库 URL: https://github.com/iwaitu/CleanApp.git
请使用 ivilson_ls 工具查看项目结构。

Workspace Directory Structure:
[DIR] codex_diagnostic_abc123/
  [DIR]  repo/
    [FILE] README.md
    [FILE] Program.cs
    [DIR]  .git/

Verification Results:
  Workspace exists: True
  Repo directory exists: True
  .git directory exists: True
  Files in repo root: 3
  Sample files:
    - README.md
    - Program.cs
    - CleanApp.csproj
  Directories in repo root: 2
  Sample directories:
    - src
    - tests

### TEST RESULT: PASSED ###
```

### 失败的测试输出（Git 未安装）
```
Tool Status: Error
Tool Output:
❌ 仓库克隆失败。请检查：1) Git 是否已安装；2) 仓库 URL 是否正确：https://github.com/iwaitu/CleanApp.git；3) 网络连接是否正常。

Verification Results:
  Workspace exists: True
  Repo directory exists: False

### TEST RESULT: FAILED ###
```

## 已修复的问题

### 问题 1: Git 克隆后找不到文件
**症状**: AI 声称成功克隆仓库，但工作空间中找不到文件

**根本原因**:
1. `GitWorkspaceTool` 没有检查 `CloneRepositoryAsync` 的返回值
2. `GitService.CloneRepositoryAsync` 缺少详细的验证和日志
3. 没有验证克隆结果的完整性

**修复**:
- ✅ `GitWorkspaceTool` 现在检查克隆成功/失败
- ✅ 添加了目录存在性验证
- ✅ 添加了 .git 文件夹验证
- ✅ 添加了文件/目录计数统计
- ✅ `GitService` 增加了详细的日志记录
- ✅ 添加了克隆后的完整性检查

### 问题 2: 缺少错误信息
**症状**: 工具失败时没有提供足够的诊断信息

**修复**:
- ✅ Git 克隆失败现在提供详细的错误提示
- ✅ 包含可能的原因检查清单
- ✅ 所有工具都有详细的日志记录

## 持续改进

### 添加新工具测试
当添加新工具时，请在 `ToolsIntegrationTests.cs` 中添加相应的测试：

```csharp
[Fact]
public async Task YourNewTool_ShouldWork_Successfully()
{
    // Arrange
    var tool = new YourNewTool(NullLogger<YourNewTool>.Instance);
    var args = new Dictionary<string, object?>
    {
        ["workspace_path"] = _testWorkspace,
        // ... other args
    };

    // Act
    var result = await tool.ExecuteAsync(args);

    // Assert
    _output.WriteLine($"Tool output:\n{result.Output}");
    Assert.Equal(ToolResultStatus.Success, result.Status);
    // ... additional assertions
}
```

### 运行测试的最佳实践
1. 在提交代码前运行完整测试套件
2. 使用 `--filter` 快速测试特定功能
3. 检查 `_output.WriteLine` 输出以诊断问题
4. Git 测试需要网络，可能会慢一些

## 故障排除

### 测试失败：Git not found
**解决方案**: 安装 Git 客户端
```bash
# Windows (使用 Chocolatey)
choco install git

# macOS
brew install git

# Linux
sudo apt-get install git
```

### 测试失败：Python not found
**解决方案**: ExecCodeTool 测试会自动跳过。如需运行：
```bash
# Windows
choco install python

# macOS
brew install python

# Linux
sudo apt-get install python3
```

### 测试失败：Permission denied
**解决方案**: 确保临时目录有写权限
```bash
# Linux/macOS
chmod 777 /tmp

# Windows: 以管理员身份运行测试
```

### 测试失败：Network timeout
**解决方案**: 检查网络连接或使用更小的测试仓库

## 联系方式
如有问题或建议，请联系团队或提交 Issue。
