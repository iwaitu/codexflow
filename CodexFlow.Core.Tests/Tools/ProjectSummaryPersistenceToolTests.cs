// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public class ProjectSummaryPersistenceToolTests
{
    private readonly ITestOutputHelper _output;

    public ProjectSummaryPersistenceToolTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task SaveProjectSummaryTool_WritesProjectSummaryToWorkspaceRoot()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "codex-summary-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var sessionManager = new CodexSessionManager(
                Mock.Of<ISessionStore>(),
                Mock.Of<IMemoryService>(),
                NullLogger<CodexSessionManager>.Instance);
            var projectMemoryService = new ProjectMemoryService(
                sessionManager,
                NullLogger<ProjectMemoryService>.Instance);

            var tool = new SaveProjectSummaryTool(
                NullLogger<SaveProjectSummaryTool>.Instance,
                sessionManager,
                projectMemoryService);
            const string summary = "test summary";

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath,
                ["summary"] = summary
            });

            _output.WriteLine($"Status: {result.Status}");
            _output.WriteLine($"Output: {result.Output}");

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("PROJECT_SUMMARY.md", result.Output ?? string.Empty);

            var summaryPath = Path.Combine(workspacePath, "PROJECT_SUMMARY.md");
            Assert.True(File.Exists(summaryPath));
            var content = await File.ReadAllTextAsync(summaryPath);
            Assert.Contains("项目记忆摘要", content);
            Assert.Contains("已确认工程决策", content);
            Assert.Contains(summary, content);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeProjectTool_AutoPersistsProjectSummaryFile()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "codex-summary-analyze-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var scanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
            scanner.Setup(s => s.ScanAndSummarizeAsync(workspacePath)).ReturnsAsync("# Fingerprint");

            var semantic = new Mock<ICodeAnalysisService>();
            semantic.Setup(s => s.BuildGraphAsync(workspacePath, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DependencyGraph());

            var architecture = new Mock<IArchitectureService>();
            architecture.Setup(a => a.AnalyzeGraph(It.IsAny<DependencyGraph>()))
                .Returns(new List<ArchitectureMetrics>());

            var sessionManager = new CodexSessionManager(
                Mock.Of<ISessionStore>(),
                Mock.Of<IMemoryService>(),
                NullLogger<CodexSessionManager>.Instance);
            var projectMemoryService = new ProjectMemoryService(
                sessionManager,
                NullLogger<ProjectMemoryService>.Instance);

            var tool = new AnalyzeProjectTool(
                scanner.Object,
                semantic.Object,
                architecture.Object,
                sessionManager,
                projectMemoryService,
                NullLogger<AnalyzeProjectTool>.Instance);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath
            });

            _output.WriteLine($"Status: {result.Status}");
            _output.WriteLine($"Output: {result.Output}");

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("PROJECT_SUMMARY.md", result.Output ?? string.Empty);

            var summaryPath = Path.Combine(workspacePath, "PROJECT_SUMMARY.md");
            Assert.True(File.Exists(summaryPath), $"Expected file at {summaryPath}");

            var content = await File.ReadAllTextAsync(summaryPath);
            Assert.Contains("项目记忆摘要", content);
            Assert.Contains("项目深度分析报告", content);
            Assert.Contains("技术栈与架构约束", content);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public void AnalyzeProjectTool_AllowedStages_IncludesStageZero()
    {
        var scanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var semantic = new Mock<ICodeAnalysisService>();
        var architecture = new Mock<IArchitectureService>();
        var sessionManager = new CodexSessionManager(
            Mock.Of<ISessionStore>(),
            Mock.Of<IMemoryService>(),
            NullLogger<CodexSessionManager>.Instance);
        var projectMemoryService = new ProjectMemoryService(
            sessionManager,
            NullLogger<ProjectMemoryService>.Instance);

        var tool = new AnalyzeProjectTool(
            scanner.Object, semantic.Object, architecture.Object,
            sessionManager, projectMemoryService, NullLogger<AnalyzeProjectTool>.Instance);

        _output.WriteLine($"AllowedStages: [{string.Join(", ", tool.AllowedStages)}]");
        Assert.Contains(0, tool.AllowedStages);
        Assert.Contains(1, tool.AllowedStages);
        Assert.Contains(2, tool.AllowedStages);
    }

    [Fact]
    public async Task AnalyzeProjectTool_OutputContainsAnalysisFirstFollowUpGuidance()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "codex-continuation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var scanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
            scanner.Setup(s => s.ScanAndSummarizeAsync(workspacePath)).ReturnsAsync("# Fingerprint");

            var semantic = new Mock<ICodeAnalysisService>();
            semantic.Setup(s => s.BuildGraphAsync(workspacePath, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DependencyGraph());

            var architecture = new Mock<IArchitectureService>();
            architecture.Setup(a => a.AnalyzeGraph(It.IsAny<DependencyGraph>()))
                .Returns(new List<ArchitectureMetrics>());

            var sessionManager = new CodexSessionManager(
                Mock.Of<ISessionStore>(),
                Mock.Of<IMemoryService>(),
                NullLogger<CodexSessionManager>.Instance);
            var projectMemoryService = new ProjectMemoryService(
                sessionManager,
                NullLogger<ProjectMemoryService>.Instance);

            var tool = new AnalyzeProjectTool(
                scanner.Object, semantic.Object, architecture.Object,
                sessionManager, projectMemoryService, NullLogger<AnalyzeProjectTool>.Instance);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath
            });

            _output.WriteLine($"Output: {result.Output}");

            Assert.DoesNotContain("openspec_set_stage", result.Output ?? string.Empty);
            Assert.DoesNotContain("generate_dev_plan", result.Output ?? string.Empty);
            Assert.Contains("[SYSTEM]", result.Output ?? string.Empty);
            Assert.Contains("向用户输出结论", result.Output ?? string.Empty);
            Assert.Contains("只有当用户明确要求制定计划", result.Output ?? string.Empty);
            // 无 README 时应提示未发现
            Assert.Contains("未发现 README", result.Output ?? string.Empty);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeProjectTool_IncludesReadmeWhenPresent()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "codex-readme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            // 创建一个 README.md
            var readmeText = "# My Project\n\nThis is a sample project for testing README integration.";
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "README.md"), readmeText);

            var scanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
            scanner.Setup(s => s.ScanAndSummarizeAsync(workspacePath)).ReturnsAsync("# Fingerprint");

            var semantic = new Mock<ICodeAnalysisService>();
            semantic.Setup(s => s.BuildGraphAsync(workspacePath, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DependencyGraph());

            var architecture = new Mock<IArchitectureService>();
            architecture.Setup(a => a.AnalyzeGraph(It.IsAny<DependencyGraph>()))
                .Returns(new List<ArchitectureMetrics>());

            var sessionManager = new CodexSessionManager(
                Mock.Of<ISessionStore>(),
                Mock.Of<IMemoryService>(),
                NullLogger<CodexSessionManager>.Instance);
            var projectMemoryService = new ProjectMemoryService(
                sessionManager,
                NullLogger<ProjectMemoryService>.Instance);

            var tool = new AnalyzeProjectTool(
                scanner.Object, semantic.Object, architecture.Object,
                sessionManager, projectMemoryService, NullLogger<AnalyzeProjectTool>.Instance);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath
            });

            _output.WriteLine($"Output: {result.Output}");

            // README 内容应包含在输出中
            Assert.Contains("My Project", result.Output ?? string.Empty);
            Assert.Contains("README 摘要", result.Output ?? string.Empty);
            // 有 README 时 directive 应提示结合 README 输出结论或继续定位
            Assert.Contains("已自动读取并包含了项目 README", result.Output ?? string.Empty);
            Assert.Contains("向用户输出简明结论", result.Output ?? string.Empty);
            Assert.Contains("继续定位具体问题", result.Output ?? string.Empty);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task SaveExecutionResultAsync_PreservesNestedHeadingsInLegacyAnalysisSnapshot()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "codex-summary-execution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var sessionManager = new CodexSessionManager(
                Mock.Of<ISessionStore>(),
                Mock.Of<IMemoryService>(),
                NullLogger<CodexSessionManager>.Instance);
            var projectMemoryService = new ProjectMemoryService(
                sessionManager,
                NullLogger<ProjectMemoryService>.Instance);

            const string rawAnalysis = """
### 项目深度分析报告

#### 1. 工程指纹
# 项目技术指纹报告 (Project Fingerprint)
生成时间: 2026-03-11 15:01:33

## 1. 物理结构概览
- src/CleanApp/Program.cs
- src/CleanApp.Core/Services/FileService.cs

## 2. 技术栈构成
- C#
- .NET 10

#### 2. 核心组件依赖
- CleanApp.Core -> CleanApp.Infrastructure
""";

            await projectMemoryService.SaveAnalysisAsync(
                new ProjectAnalysisMemoryInput(
                    workspacePath,
                    workspacePath,
                    null,
                    null,
                    null,
                    "csharp",
                    "# 项目技术指纹报告 (Project Fingerprint)\n生成时间: 2026-03-11 15:01:33",
                    ["[Arch] src/CleanApp.Core/Services/FileService.cs - Unstable Foundation"],
                    null,
                    rawAnalysis));

            await projectMemoryService.SaveExecutionResultAsync(
                new ProjectExecutionMemoryInput(
                    workspacePath,
                    workspacePath,
                    null,
                    null,
                    null,
                    [
                        new CodexTask
                        {
                            Id = "TASK_001",
                            Title = "验证项目摘要更新",
                            Status = CodexTaskStatus.Success
                        }
                    ],
                    "任务执行汇总：成功 1，跳过 0，失败 0，总计 1。"));

            var summaryPath = Path.Combine(workspacePath, "PROJECT_SUMMARY.md");
            var content = await File.ReadAllTextAsync(summaryPath);

            Assert.Contains("## 历史分析快照", content);
            Assert.Contains("## 1. 物理结构概览", content);
            Assert.Contains("## 2. 技术栈构成", content);
            Assert.Contains("#### 2. 核心组件依赖", content);
            Assert.Contains("## 最近一次任务执行结果", content);
            Assert.Contains("TASK_001", content);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task SaveExecutionResultAsync_RehydratesManagedSections_ForMalformedSummaryFile()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "codex-summary-malformed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        try
        {
            var sessionManager = new CodexSessionManager(
                Mock.Of<ISessionStore>(),
                Mock.Of<IMemoryService>(),
                NullLogger<CodexSessionManager>.Instance);
            var projectMemoryService = new ProjectMemoryService(
                sessionManager,
                NullLogger<ProjectMemoryService>.Instance);

            var summaryPath = Path.Combine(workspacePath, "PROJECT_SUMMARY.md");
            await File.WriteAllTextAsync(summaryPath, "# 项目记忆摘要\n\n旧的摘要内容，没有受控 section。\n");

            var result = await projectMemoryService.SaveExecutionResultAsync(
                new ProjectExecutionMemoryInput(
                    workspacePath,
                    null,
                    "session-malformed",
                    null,
                    null,
                    [new CodexTask { Id = "TASK-1", Title = "fix issue", Status = CodexTaskStatus.Success }],
                    "dotnet test passed"),
                CancellationToken.None);

            Assert.Equal(summaryPath, result.FilePath);

            var content = await File.ReadAllTextAsync(summaryPath);
            Assert.Contains("## 项目概况", content);
            Assert.Contains("## 当前验证基线", content);
            Assert.Contains("## 最近一次任务执行结果", content);
            Assert.Contains("## 历史分析快照", content);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }
}
