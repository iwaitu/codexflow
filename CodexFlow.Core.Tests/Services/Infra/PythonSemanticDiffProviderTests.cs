// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Xunit;
using CodexFlow.Core.Services.Infra;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using CodexFlow.Core.Abstractions;

namespace CodexFlow.Core.Tests.Services.Infra;

public class PythonSemanticDiffProviderTests
{
    private readonly PythonSemanticDiffProvider _provider;

    public PythonSemanticDiffProviderTests()
    {
        _provider = new PythonSemanticDiffProvider(NullLogger<PythonSemanticDiffProvider>.Instance);
    }

    [Fact]
    public void CanHandle_ShouldReturnTrueForPyExtension()
    {
        Assert.True(_provider.CanHandle(".py"));
        Assert.True(_provider.CanHandle(".PY"));
        Assert.False(_provider.CanHandle(".cs"));
    }
    
    // Note: Integration tests requiring external python environment are better skipped in CI if python is not available.
    // For local dev, we assume python is available.
    [Fact]
    public async Task AnalyzeAsync_ShouldDetectChanges()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "CodexTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        
        var mainFile = Path.Combine(tempDir, "main.py");
        var shadowFile = Path.Combine(tempDir, "shadow.py");
        
        // Ensure scripts are available (hack for test environment)
        // We might need to copy scripts to output dir manually or assume build did it.
        // If the provider looks for scripts in BaseDirectory/skills/..., we need to ensure that path exists.
        
        var scriptsDir = Path.Combine(AppContext.BaseDirectory, "skills", "python-inspector", "scripts");
        if (!Directory.Exists(scriptsDir))
        {
             // Repo root from this source file (CallerFilePath), independent of the output directory.
             var dir = CodexFlow.Core.Tests.Support.RepoRoot.Find();

             if (dir != null)
             {
                 var sourceScripts = Path.Combine(dir, "CodexFlow", "skills", "python-inspector", "scripts");
                 if (Directory.Exists(sourceScripts))
                 {
                     Directory.CreateDirectory(scriptsDir);
                     foreach(var file in Directory.GetFiles(sourceScripts))
                     {
                         File.Copy(file, Path.Combine(scriptsDir, Path.GetFileName(file)), true);
                     }
                 }
             }
        }

        // The inspector script lives in the skills folder of the server project; a checkout without it (the standalone edition) cannot run this.
        if (!Directory.Exists(scriptsDir))
        {
            Assert.Skip("skills/python-inspector is not part of this checkout.");
        }

        var codeV1 = @"
def hello():
    print('Hello World')

class User:
    def __init__(self, name):
        self.name = name
";
        var codeV2 = @"
def hello():
    print('Hello New World') # Changed content

class User:
    def __init__(self, name, age): # Changed signature
        self.name = name
        self.age = age

def new_func(): # Added
    pass
";
        await File.WriteAllTextAsync(mainFile, codeV1);
        await File.WriteAllTextAsync(shadowFile, codeV2);

        // Act
        var result = await _provider.AnalyzeAsync(mainFile, shadowFile, CancellationToken.None);

        // Assert
        Assert.True(result.HasChanges);
        Assert.Contains(result.ChangedSymbols, s => s.Contains("Function hello")); // Changed content
        Assert.Contains(result.ChangedSymbols, s => s.Contains("Class User")); // Changed init? Or how analzye_diff detects
        // analyze_diff detects signature changes.
        // hello() signature didn't change, but content did. The script checks content too.
        
        // Cleanup
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
}
