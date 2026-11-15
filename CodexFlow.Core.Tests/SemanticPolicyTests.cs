// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Services;

public class SemanticPolicyTests : IDisposable
{
    private readonly string _testWorkspace;

    public SemanticPolicyTests()
    {
        _testWorkspace = Path.Combine(Path.GetTempPath(), $"policy_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testWorkspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testWorkspace)) Directory.Delete(_testWorkspace, true);
    }

    [Fact]
    public async Task AsyncMethodNamingRule_ShouldFail_WhenMethodNameIsIncorrect()
    {
        // Arrange
        var rule = new AsyncMethodNamingRule();
        var code = @"
            public class TestService {
                public Task DoWork() => Task.CompletedTask;
            }";
        var filePath = Path.Combine(_testWorkspace, "TestService.cs");
        await File.WriteAllTextAsync(filePath, code);

        // Act
        var result = await rule.EvaluateAsync(new CodexSession(), _testWorkspace);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Async", result.Message);
    }

    [Fact]
    public async Task EntityIntegrityRule_ShouldFail_WhenDtoInDomain()
    {
        // Arrange
        var rule = new EntityIntegrityRule();
        var domainDir = Path.Combine(_testWorkspace, "MyProject.Domain");
        Directory.CreateDirectory(domainDir);
        
        var code = "public class UserDto { }";
        await File.WriteAllTextAsync(Path.Combine(domainDir, "UserDto.cs"), code);

        // Act
        var result = await rule.EvaluateAsync(new CodexSession(), _testWorkspace);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("DTO/VM", result.Message);
    }

    [Fact]
    public async Task DefaultPolicyValidator_ShouldAggregateFailures()
    {
        // Arrange
        var rules = new List<IPolicyRule> { new AsyncMethodNamingRule(), new EntityIntegrityRule() };
        var validator = new DefaultPolicyValidator(rules, NullLogger<DefaultPolicyValidator>.Instance);
        
        // 觸發 Async 命名違規
        await File.WriteAllTextAsync(Path.Combine(_testWorkspace, "Bad.cs"), "public class B { public Task Run() => null; }");

        // Act
        var result = await validator.ValidateAsync(new CodexSession(), new CodexTask { Id = "T1" }, _testWorkspace);

        // Assert
        Assert.False(result.Success);
        Assert.True(result.Details.Count >= 1);
    }
}
