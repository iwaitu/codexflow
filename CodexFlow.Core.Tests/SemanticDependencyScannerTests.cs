// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Xunit;
using CodexFlow.Core.Services;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexFlow.Core.Tests;

public class SemanticDependencyScannerTests
{
    private readonly SemanticDependencyScanner _scanner;
    private readonly string _testWorkspace;

    public SemanticDependencyScannerTests()
    {
        _scanner = new SemanticDependencyScanner(NullLogger<SemanticDependencyScanner>.Instance);
        _testWorkspace = Path.Combine(Path.GetTempPath(), "CodexTest_" + Guid.NewGuid());
        Directory.CreateDirectory(_testWorkspace);
    }

    [Fact]
    public async Task BuildGraphAsync_ShouldDetectCSharpInheritance()
    {
        // Arrange
        var baseClassCode = "namespace Test; public class BaseService {}";
        var childClassCode = "using Test; namespace Test; public class AuthService : BaseService {}";

        File.WriteAllText(Path.Combine(_testWorkspace, "BaseService.cs"), baseClassCode);
        File.WriteAllText(Path.Combine(_testWorkspace, "AuthService.cs"), childClassCode);

        // Act
        var graph = await _scanner.BuildGraphAsync(_testWorkspace);

        // Assert
        Assert.True(graph.Nodes.ContainsKey("BaseService.cs"), "BaseService node should exist");
        Assert.True(graph.Nodes.ContainsKey("AuthService.cs"), "AuthService node should exist");

        var baseNode = graph.Nodes["BaseService.cs"];
        var childNode = graph.Nodes["AuthService.cs"];

        Assert.Contains("BaseService.cs", childNode.References); 
        Assert.Contains("AuthService.cs", baseNode.ReferencedBy); 
    }

    [Fact]
    public async Task BuildGraphAsync_ShouldDetectTypescriptImports()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_testWorkspace, "src"));
        File.WriteAllText(Path.Combine(_testWorkspace, "src", "Button.tsx"), "export const Button = () => {}");
        File.WriteAllText(Path.Combine(_testWorkspace, "src", "App.tsx"), "import { Button } from './Button'; export const App = () => {}");

        // Act
        var graph = await _scanner.BuildGraphAsync(_testWorkspace);

        // Assert
        string appPath = Path.Combine("src", "App.tsx");
        string btnPath = Path.Combine("src", "Button.tsx");
        
        Assert.True(graph.Nodes.ContainsKey(appPath), "App.tsx node should exist");
        Assert.True(graph.Nodes.ContainsKey(btnPath), "Button.tsx node should exist");

        var appNode = graph.Nodes[appPath];
        var btnNode = graph.Nodes[btnPath];

        // Since our linker is simplistic (based on filename), it should find 'Button'
        // Note: Linking logic might need to be robust to relative paths, but for 'thumbnail' we check basic name match
        // Let's verify that App.tsx at least recorded the import string
        Assert.Contains("./Button", appNode.ImportedNamespaces); 
    }

    [Fact]
    public async Task BuildGraphAsync_ShouldIgnoreObjDirectories()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_testWorkspace, "obj"));
        File.WriteAllText(Path.Combine(_testWorkspace, "obj", "Garbage.cs"), "public class Garbage {}");
        File.WriteAllText(Path.Combine(_testWorkspace, "Real.cs"), "public class Real {}");

        // Act
        var graph = await _scanner.BuildGraphAsync(_testWorkspace);

        // Assert
        Assert.True(graph.Nodes.ContainsKey("Real.cs"));
        Assert.False(graph.Nodes.ContainsKey(Path.Combine("obj", "Garbage.cs")));
    }

    [Fact]
    public async Task BuildGraphAsync_ShouldDetectJavaInheritanceAndImports()
    {
        // Arrange
        var javaBase = "package com.test; public class BaseEntity {}";
        var javaChild = "package com.test; import com.test.BaseEntity; public class User extends BaseEntity implements Serializable {}";

        File.WriteAllText(Path.Combine(_testWorkspace, "BaseEntity.java"), javaBase);
        File.WriteAllText(Path.Combine(_testWorkspace, "User.java"), javaChild);

        // Act
        var graph = await _scanner.BuildGraphAsync(_testWorkspace);

        // Assert
        Assert.True(graph.Nodes.ContainsKey("BaseEntity.java"));
        Assert.True(graph.Nodes.ContainsKey("User.java"));

        var baseNode = graph.Nodes["BaseEntity.java"];
        var childNode = graph.Nodes["User.java"];

        Assert.Equal("com.test.BaseEntity", baseNode.TypeName);
        Assert.Equal("com.test.User", childNode.TypeName);
        
        // 验证继承和导入
        Assert.Contains("BaseEntity", childNode.InheritedBaseClasses);
        Assert.Contains("com.test.BaseEntity", childNode.ImportedNamespaces);
        
        // 验证链接
        Assert.Contains("BaseEntity.java", childNode.References);
        Assert.Contains("User.java", baseNode.ReferencedBy);
    }

    [Fact]
    public async Task BuildGraphAsync_ShouldDetectPythonImports()
    {
        // Arrange
        var pyUtils = "def helper(): pass";
        var pyMain = "import utils\nfrom services import AuthService\n\ndef run(): utils.helper()";

        File.WriteAllText(Path.Combine(_testWorkspace, "utils.py"), pyUtils);
        File.WriteAllText(Path.Combine(_testWorkspace, "main.py"), pyMain);

        // Act
        var graph = await _scanner.BuildGraphAsync(_testWorkspace);

        // Assert
        Assert.True(graph.Nodes.ContainsKey("utils.py"));
        Assert.True(graph.Nodes.ContainsKey("main.py"));

        var mainNode = graph.Nodes["main.py"];
        Assert.Contains("utils", mainNode.ImportedNamespaces);
        Assert.Contains("services", mainNode.ImportedNamespaces);
        
        // 验证链接 (基于文件名的模糊匹配)
        Assert.Contains("utils.py", mainNode.References);
    }

    [Fact]
    public async Task BuildGraphAsync_ShouldHandleFullPolyglotProject()
    {
        // Arrange: 创建一个包含 4 种语言的混合项目结构
        File.WriteAllText(Path.Combine(_testWorkspace, "Service.cs"), "public class Service {}");
        File.WriteAllText(Path.Combine(_testWorkspace, "App.tsx"), "import { Service } from './Service';");
        File.WriteAllText(Path.Combine(_testWorkspace, "script.py"), "import logic");
        File.WriteAllText(Path.Combine(_testWorkspace, "logic.py"), "print('hello')");
        File.WriteAllText(Path.Combine(_testWorkspace, "Base.java"), "public class Base {}");

        // Act
        var graph = await _scanner.BuildGraphAsync(_testWorkspace);

        // Assert
        Assert.Equal(5, graph.Nodes.Count);
        
        var extensions = graph.Nodes.Keys.Select(Path.GetExtension).Distinct().ToList();
        Assert.Contains(".cs", extensions);
        Assert.Contains(".tsx", extensions);
        Assert.Contains(".py", extensions);
        Assert.Contains(".java", extensions);

        // 验证 Python 内部链接
        Assert.Contains("logic.py", graph.Nodes["script.py"].References);
    }
}
