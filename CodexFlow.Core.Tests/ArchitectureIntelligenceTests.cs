// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public class ArchitectureIntelligenceTests
{
    [Fact]
    public void AnalyzeGraph_ShouldDetectGodObject_WhenCouplingIsHigh()
    {
        // Arrange
        var service = new DefaultArchitectureService(NullLogger<DefaultArchitectureService>.Instance);
        var graph = new DependencyGraph();

        // 创建一个上帝类 (God Object): 5个依赖，5个被依赖
        var godNode = new CodeNode { FilePath = "Core/GodService.cs", Language = "C#" };
        for (int i = 0; i < 5; i++)
        {
            godNode.References.Add($"Ref{i}.cs");
            godNode.ReferencedBy.Add($"Client{i}.cs");
        }
        graph.AddNode(godNode);

        // Act
        var results = service.AnalyzeGraph(graph);

        // Assert
        var godMetrics = results.FirstOrDefault(r => r.FilePath == "Core/GodService.cs");
        Assert.NotNull(godMetrics);
        Assert.Contains("Architecture Hub", godMetrics.DetectedSmells.First());
        Assert.True(godMetrics.RefactorPriority >= 40);
    }

    [Fact]
    public void AnalyzeGraph_ShouldDetectUnstableFoundation_InCoreFolder()
    {
        // Arrange
        var service = new DefaultArchitectureService(NullLogger<DefaultArchitectureService>.Instance);
        var graph = new DependencyGraph();

        // 创建一个不稳定的基础类: 在 Core 文件夹，但依赖了 8 个外部组件，只有 2 个入度
        var unstableNode = new CodeNode { FilePath = "CodexFlow.Core/BaseRepository.cs", Language = "C#" };
        for (int i = 0; i < 8; i++) unstableNode.References.Add($"Ext{i}.cs");
        unstableNode.ReferencedBy.Add("App1.cs");
        unstableNode.ReferencedBy.Add("App2.cs");
        graph.AddNode(unstableNode);

        // Act
        var results = service.AnalyzeGraph(graph);

        // Assert
        var metrics = results.FirstOrDefault(r => r.FilePath == "CodexFlow.Core/BaseRepository.cs");
        Assert.NotNull(metrics);
        Assert.Contains("Unstable Foundation", metrics.DetectedSmells);
        Assert.True(metrics.Instability > 0.7);
    }
}
