// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Globalization;

namespace CodexFlow.Core.Agents
{
    public class RoslynCodeAnalysisService : ICodeAnalysisService
    {
        private const string ReferenceDirectoryVariable = "CODEXFLOW_CSHARP_REFERENCE_DIR";
        private static readonly ConcurrentDictionary<string, MetadataReference[]> ReferenceCache =
            new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        private readonly ILogger _logger;
        private readonly string? _referenceDirectory;

        public RoslynCodeAnalysisService(ILogger<RoslynCodeAnalysisService> logger)
            : this((ILogger)logger)
        {
        }

        internal RoslynCodeAnalysisService(ILogger logger, string? referenceDirectory = null)
        {
            _logger = logger;
            _referenceDirectory = referenceDirectory;
        }

        public Task<List<CodeDiagnostic>> AnalyzeCodeAsync(string code, string language = "C#")
        {
            ArgumentNullException.ThrowIfNull(code);

            var syntaxTree = CSharpSyntaxTree.ParseText(code);
            var outputKind = syntaxTree.GetCompilationUnitRoot().Members.Any(static member => member is GlobalStatementSyntax)
                ? OutputKind.ConsoleApplication
                : OutputKind.DynamicallyLinkedLibrary;
            var compilation = CSharpCompilation.Create(
                "AgentAnalysis",
                syntaxTrees: [syntaxTree],
                references: GetReferences(),
                options: new CSharpCompilationOptions(outputKind));

            var result = new List<CodeDiagnostic>();
            foreach (var diagnostic in compilation.GetDiagnostics())
            {
                if (diagnostic.Id is "CS0009" or "CS0518")
                {
                    throw ReferenceUnavailable();
                }

                if (diagnostic.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
                {
                    continue;
                }

                var span = diagnostic.Location.GetLineSpan();
                result.Add(new CodeDiagnostic(
                    diagnostic.Id,
                    diagnostic.GetMessage(CultureInfo.InvariantCulture),
                    diagnostic.Severity.ToString(),
                    span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1));
            }

            return Task.FromResult(result);
        }

        private MetadataReference[] GetReferences()
        {
            var directory = _referenceDirectory
                ?? Environment.GetEnvironmentVariable(ReferenceDirectoryVariable)
                ?? Path.Combine(AppContext.BaseDirectory, "code-analysis", "net10.0");

            try
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    throw ReferenceUnavailable();
                }

                var fullPath = Path.GetFullPath(directory, AppContext.BaseDirectory);
                return ReferenceCache.GetOrAdd(fullPath, static path => LoadReferences(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or BadImageFormatException)
            {
                throw ReferenceUnavailable(exception);
            }
        }

        private static MetadataReference[] LoadReferences(string directory)
        {
            if (!File.Exists(Path.Combine(directory, "System.Runtime.dll"))
                || !File.Exists(Path.Combine(directory, "System.Linq.dll")))
            {
                throw ReferenceUnavailable();
            }

            var paths = Directory.GetFiles(directory, "*.dll");
            Array.Sort(paths, StringComparer.Ordinal);
            return paths.Select(static path => MetadataReference.CreateFromFile(path)).ToArray();
        }

        private static CodeAnalysisUnavailableException ReferenceUnavailable(Exception? innerException = null)
            => new($"无法加载 .NET 10 引用程序集。请检查应用内的 code-analysis/net10.0 目录，或将 {ReferenceDirectoryVariable} 指向可用的 .NET 10 引用程序集目录。", innerException);

        public Task<List<CodeDiagnostic>> AnalyzeProjectAsync(string projectPath)
        {
            // 这里以后可以扩展为使用 MSBuildWorkspace 加载整个 .sln
            StructuredLog.Warning(_logger, "全项目分析功能待扩展，目前仅支持代码片段。");
            return Task.FromResult(new List<CodeDiagnostic>());
        }

        public Task<DependencyGraph> BuildGraphAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            // 暂不支持通过此服务构建 Graph，请使用 SemanticDependencyScanner
            throw new NotImplementedException("Use SemanticDependencyScanner for graph building.");
        }

        public Task<GuardrailResult> CheckGuardrailAsync(DependencyGraph graph, string targetFilePath, string taskRiskLevel)
            => Task.FromResult(new GuardrailResult(false, null));

        public List<string> GetImpactedFiles(DependencyGraph graph, string changedFilePath)
            => new List<string>();
    }

    internal sealed class CodeAnalysisUnavailableException(string message, Exception? innerException)
        : InvalidOperationException(message, innerException)
    {
        public CodeAnalysisUnavailableException() : this("无法加载 .NET 10 引用程序集。")
        {
        }

        public CodeAnalysisUnavailableException(string message) : this(message, null)
        {
        }
    }
}

