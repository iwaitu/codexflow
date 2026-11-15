// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.LanguageServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.LanguageServices;

public class LanguageServiceRegistryTests
{
    [Fact]
    public void Registry_ShouldExposeFourLanguages_WithExpectedCapabilities()
    {
        var registry = CreateRegistry(new FixedProbe(new Dictionary<string, LanguageServiceRuntimeProbeResult>()));

        var providers = registry.GetRegisteredProviders();

        Assert.Equal(4, providers.Count);
        Assert.Contains(providers, provider => provider.Language == SupportedLanguage.CSharp);
        Assert.Contains(providers, provider => provider.Language == SupportedLanguage.Python);
        Assert.Contains(providers, provider => provider.Language == SupportedLanguage.TypeScript);
        Assert.Contains(providers, provider => provider.Language == SupportedLanguage.Java);
        Assert.All(providers, provider =>
        {
            Assert.Contains(LanguageServiceCapability.GetDiagnostics, provider.Capabilities);
            Assert.Contains(LanguageServiceCapability.DocumentSymbols, provider.Capabilities);
            Assert.Contains(LanguageServiceCapability.GoToDefinition, provider.Capabilities);
        });
    }

    [Fact]
    public async Task SessionFactory_ShouldAllowPartialInitialization_InMultilanguageWorkspace()
    {
        var probe = new FixedProbe(new Dictionary<string, LanguageServiceRuntimeProbeResult>(StringComparer.OrdinalIgnoreCase)
        {
            ["pylsp"] = new() { Available = false, DegradedReason = LanguageServiceDegradedReason.BinaryMissing },
            ["typescript-language-server"] = new() { Available = true, Command = "typescript-language-server" },
            ["jdtls"] = new() { Available = false, DegradedReason = LanguageServiceDegradedReason.BinaryMissing }
        });
        var registry = CreateRegistry(probe);
        var factory = new DefaultLanguageServiceSessionFactory(registry, NullLogger<DefaultLanguageServiceSessionFactory>.Instance);
        var workspacePath = CreateWorkspace();

        try
        {
            var csharp = await factory.GetOrCreateAsync(new LanguageServiceSessionKey
            {
                WorkspacePath = workspacePath,
                WorkerId = "explore",
                Language = SupportedLanguage.CSharp
            });
            var python = await factory.GetOrCreateAsync(new LanguageServiceSessionKey
            {
                WorkspacePath = workspacePath,
                WorkerId = "explore",
                Language = SupportedLanguage.Python
            });
            var typeScript = await factory.GetOrCreateAsync(new LanguageServiceSessionKey
            {
                WorkspacePath = workspacePath,
                WorkerId = "explore",
                Language = SupportedLanguage.TypeScript
            });
            var java = await factory.GetOrCreateAsync(new LanguageServiceSessionKey
            {
                WorkspacePath = workspacePath,
                WorkerId = "explore",
                Language = SupportedLanguage.Java
            });

            Assert.Equal(LanguageServiceStatus.Active, csharp.Descriptor.Status);
            Assert.Equal(LanguageServiceStatus.Degraded, python.Descriptor.Status);
            Assert.Equal(LanguageServiceDegradedReason.BinaryMissing, python.Descriptor.DegradedReason);
            Assert.Equal(LanguageServiceStatus.Active, typeScript.Descriptor.Status);
            Assert.Equal(LanguageServiceStatus.Degraded, java.Descriptor.Status);
            Assert.Equal(LanguageServiceDegradedReason.BinaryMissing, java.Descriptor.DegradedReason);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Theory]
    [InlineData("Program.cs", SupportedLanguage.CSharp)]
    [InlineData("script.py", SupportedLanguage.Python)]
    [InlineData("app.ts", SupportedLanguage.TypeScript)]
    [InlineData("Main.java", SupportedLanguage.Java)]
    [InlineData("typescript", SupportedLanguage.TypeScript)]
    public void DetectLanguage_ShouldResolveCommonExtensions(string input, SupportedLanguage expected)
    {
        var registry = CreateRegistry(new FixedProbe(new Dictionary<string, LanguageServiceRuntimeProbeResult>()));
        Assert.Equal(expected, registry.DetectLanguage(input));
    }

    private static DefaultLanguageServiceRegistry CreateRegistry(ILanguageServiceRuntimeProbe probe)
        => new(
        [
            new CSharpLanguageServiceProvider(NullLogger<CSharpLanguageServiceProvider>.Instance),
            new PythonLanguageServiceProvider(probe, NullLogger<PythonLanguageServiceProvider>.Instance),
            new TypeScriptLanguageServiceProvider(probe, NullLogger<TypeScriptLanguageServiceProvider>.Instance),
            new JavaLanguageServiceProvider(probe, NullLogger<JavaLanguageServiceProvider>.Instance)
        ]);

    private static string CreateWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexflow-lsp-registry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Program.cs"), "public class Program {}");
        File.WriteAllText(Path.Combine(root, "script.py"), "def run():\n    return 1\n");
        File.WriteAllText(Path.Combine(root, "app.ts"), "export function run() { return 1; }\n");
        File.WriteAllText(Path.Combine(root, "Main.java"), "public class Main {}\n");
        return root;
    }

    private sealed class FixedProbe(IReadOnlyDictionary<string, LanguageServiceRuntimeProbeResult> responses) : ILanguageServiceRuntimeProbe
    {
        public ValueTask<LanguageServiceRuntimeProbeResult> ProbeAsync(
            IReadOnlyList<string> commandCandidates,
            CancellationToken ct = default)
        {
            foreach (var candidate in commandCandidates)
            {
                if (responses.TryGetValue(candidate, out var result))
                {
                    return ValueTask.FromResult(result);
                }
            }

            return ValueTask.FromResult(new LanguageServiceRuntimeProbeResult
            {
                Available = true,
                Command = commandCandidates.FirstOrDefault(),
                DegradedReason = LanguageServiceDegradedReason.None
            });
        }
    }
}
