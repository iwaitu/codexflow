// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public class SecurityAuditorTests
{
    private static CodexSession CreateSession() => new()
    {
        Id = "test-session",
        WorkspacePath = "/tmp/test-ws",
        ProjectSummary = "Test project"
    };

    private static Mock<ICodexAgentKernel> CreateKernelMock(string responseText, bool isComplete = true)
    {
        var mock = new Mock<ICodexAgentKernel>();
        mock.Setup(k => k.RunLoopAsync(
                It.IsAny<CodexSession>(),
                It.IsAny<string>(),
                It.IsAny<CodexAgentRole>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>(),
                It.IsAny<TaskFileScopeDescriptor?>()))
            .ReturnsAsync(new CodexResponse(responseText, isComplete));
        return mock;
    }

    private static Mock<ICodexAgentKernel> CreateRetryKernelMock(params string[] responses)
    {
        var mock = new Mock<ICodexAgentKernel>();
        var callIndex = 0;
        mock.Setup(k => k.RunLoopAsync(
                It.IsAny<CodexSession>(),
                It.IsAny<string>(),
                It.IsAny<CodexAgentRole>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>(),
                It.IsAny<TaskFileScopeDescriptor?>()))
            .ReturnsAsync(() =>
            {
                var idx = callIndex;
                callIndex++;
                var text = idx < responses.Length ? responses[idx] : responses[^1];
                return new CodexResponse(text, true);
            });
        return mock;
    }

    [Fact]
    public async Task AuditAsync_WithValidPureJson_ReturnsCorrectResult()
    {
        var json = """{"IsPassed": true, "Summary": "No issues found", "Risks": [], "DeferredRisks": [], "LegacyRisks": []}""";
        var mockKernel = CreateKernelMock(json);
        var auditor = new DefaultCodexSecurityAuditor(mockKernel.Object, NullLogger<DefaultCodexSecurityAuditor>.Instance);
        var session = CreateSession();

        var result = await auditor.AuditAsync(session, "/tmp/test-ws");

        Assert.True(result.IsPassed);
        Assert.Equal("No issues found", result.Summary);
        Assert.Empty(result.Risks);
        Assert.Empty(result.DeferredRisks);
        Assert.Empty(result.LegacyRisks);
    }

    [Fact]
    public async Task AuditAsync_WithJsonInMarkdownCodeBlock_ReturnsCorrectResult()
    {
        var response = """
```json
{"IsPassed": false, "Summary": "Found SQL injection", "Risks": ["Critical: UserService.cs:42 - SQL Injection - String concatenation in query - Use parameterized queries"], "DeferredRisks": [], "LegacyRisks": []}
```
""";
        var mockKernel = CreateKernelMock(response);
        var auditor = new DefaultCodexSecurityAuditor(mockKernel.Object, NullLogger<DefaultCodexSecurityAuditor>.Instance);
        var session = CreateSession();

        var result = await auditor.AuditAsync(session, "/tmp/test-ws");

        Assert.False(result.IsPassed);
        Assert.Equal("Found SQL injection", result.Summary);
        Assert.Single(result.Risks);
        Assert.Contains("SQL Injection", result.Risks[0]);
    }

    [Fact]
    public async Task AuditAsync_WithJsonSurroundedByText_ExtractsCorrectJson()
    {
        var response = """
好的，让我来分析一下安全性...

{"IsPassed": true, "Summary": "Clean", "Risks": [], "DeferredRisks": [], "LegacyRisks": []}

以上就是审计结果。
""";
        var mockKernel = CreateKernelMock(response);
        var auditor = new DefaultCodexSecurityAuditor(mockKernel.Object, NullLogger<DefaultCodexSecurityAuditor>.Instance);
        var session = CreateSession();

        var result = await auditor.AuditAsync(session, "/tmp/test-ws");

        Assert.True(result.IsPassed);
        Assert.Equal("Clean", result.Summary);
    }

    [Fact]
    public async Task AuditAsync_WithJsonContainingNestedBracesInStrings_ExtractsCorrectly()
    {
        var response = """{"IsPassed": false, "Summary": "Found issue", "Risks": ["Critical: file.cs:10 - Unsafe eval({code: x})"], "DeferredRisks": [], "LegacyRisks": []}""";
        var mockKernel = CreateKernelMock(response);
        var auditor = new DefaultCodexSecurityAuditor(mockKernel.Object, NullLogger<DefaultCodexSecurityAuditor>.Instance);
        var session = CreateSession();

        var result = await auditor.AuditAsync(session, "/tmp/test-ws");

        Assert.False(result.IsPassed);
        Assert.Single(result.Risks);
        Assert.Contains("eval({code: x})", result.Risks[0]);
    }

    [Fact]
    public async Task AuditAsync_WithEmptyJsonObject_FailsClosed()
    {
        var mockKernel = CreateKernelMock("{}");
        var auditor = new DefaultCodexSecurityAuditor(mockKernel.Object, NullLogger<DefaultCodexSecurityAuditor>.Instance);
        var session = CreateSession();

        var result = await auditor.AuditAsync(session, "/tmp/test-ws");

        Assert.False(result.IsPassed);
        // After retries exhaust, parse failure returns an explicit infra failure summary.
        Assert.Equal("Security auditor parse failure", result.Summary);
        Assert.Contains("[System] Parse failure", result.Risks);
        // Kernel should be called twice (initial + 1 retry)
        mockKernel.Verify(k => k.RunLoopAsync(
            It.IsAny<CodexSession>(),
            It.IsAny<string>(),
            It.IsAny<CodexAgentRole>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<bool>(),
            It.IsAny<TaskFileScopeDescriptor?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AuditAsync_WithEmptyResponse_FailsClosed()
    {
        var mockKernel = CreateKernelMock("");
        var auditor = new DefaultCodexSecurityAuditor(mockKernel.Object, NullLogger<DefaultCodexSecurityAuditor>.Instance);
        var session = CreateSession();

        var result = await auditor.AuditAsync(session, "/tmp/test-ws");

        Assert.False(result.IsPassed);
    }

    [Fact]
    public async Task AuditAsync_WithIncompleteKernelResponse_FailsClosed()
    {
        var mockKernel = CreateKernelMock("some text", isComplete: false);
        var auditor = new DefaultCodexSecurityAuditor(mockKernel.Object, NullLogger<DefaultCodexSecurityAuditor>.Instance);
        var session = CreateSession();

        var result = await auditor.AuditAsync(session, "/tmp/test-ws");

        Assert.False(result.IsPassed);
        Assert.Contains("execution failed", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditAsync_WithInvalidJson_RetriesOnce_ThenFailsClosed()
    {
        var mockKernel = CreateRetryKernelMock(
            "This is not JSON at all",
            "Still not valid JSON"
        );
        var auditor = new DefaultCodexSecurityAuditor(mockKernel.Object, NullLogger<DefaultCodexSecurityAuditor>.Instance);
        var session = CreateSession();

        var result = await auditor.AuditAsync(session, "/tmp/test-ws");

        Assert.False(result.IsPassed);
        // Verify kernel was called twice (initial + 1 retry)
        mockKernel.Verify(k => k.RunLoopAsync(
            It.IsAny<CodexSession>(),
            It.IsAny<string>(),
            It.IsAny<CodexAgentRole>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<bool>(),
            It.IsAny<TaskFileScopeDescriptor?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AuditAsync_WithInvalidJson_RetriesAndSucceeds()
    {
        var mockKernel = CreateRetryKernelMock(
            "```json\n{\"IsPassed\": true, \"Summary\": \"ok\", \"Risks\": [], \"DeferredRisks\": [], \"LegacyRisks\": []}",  // first: has markdown fence
            "{\"IsPassed\": true, \"Summary\": \"ok\", \"Risks\": [], \"DeferredRisks\": [], \"LegacyRisks\": []}"  // second: clean
        );
        var auditor = new DefaultCodexSecurityAuditor(mockKernel.Object, NullLogger<DefaultCodexSecurityAuditor>.Instance);
        var session = CreateSession();

        var result = await auditor.AuditAsync(session, "/tmp/test-ws");

        // First attempt should actually succeed since markdown fences are now handled
        Assert.True(result.IsPassed);
    }

    [Fact]
    public async Task AuditAsync_WithDeferredAndLegacyRisks_ParsesCorrectly()
    {
        var response = """
{
  "IsPassed": true,
  "Summary": "No blocking risks",
  "Risks": [],
  "DeferredRisks": ["Medium: future_file.cs:10 - Missing input validation"],
  "LegacyRisks": ["High: old_file.cs:5 - Hardcoded password (历史遗留)"]
}
""";
        var mockKernel = CreateKernelMock(response);
        var auditor = new DefaultCodexSecurityAuditor(mockKernel.Object, NullLogger<DefaultCodexSecurityAuditor>.Instance);
        var session = CreateSession();

        var result = await auditor.AuditAsync(session, "/tmp/test-ws");

        Assert.True(result.IsPassed);
        Assert.Empty(result.Risks);
        Assert.Single(result.DeferredRisks);
        Assert.Single(result.LegacyRisks);
        Assert.Contains("Missing input validation", result.DeferredRisks[0]);
        Assert.Contains("Hardcoded password", result.LegacyRisks[0]);
    }

    [Fact]
    public async Task AuditAsync_WithCaseInsensitiveJsonKeys_ParsesCorrectly()
    {
        var response = """
{"ispassed": true, "summary": "Case insensitive test", "risks": [], "deferredrisks": [], "legacyrisks": []}
""";
        var mockKernel = CreateKernelMock(response);
        var auditor = new DefaultCodexSecurityAuditor(mockKernel.Object, NullLogger<DefaultCodexSecurityAuditor>.Instance);
        var session = CreateSession();

        var result = await auditor.AuditAsync(session, "/tmp/test-ws");

        Assert.True(result.IsPassed);
        Assert.Equal("Case insensitive test", result.Summary);
    }

    [Fact]
    public async Task AuditAsync_WithTrailingCommaInJson_ParsesCorrectly()
    {
        var response = """
{"IsPassed": true, "Summary": "Trailing comma test", "Risks": [], "DeferredRisks": [], "LegacyRisks": [],}
""";
        var mockKernel = CreateKernelMock(response);
        var auditor = new DefaultCodexSecurityAuditor(mockKernel.Object, NullLogger<DefaultCodexSecurityAuditor>.Instance);
        var session = CreateSession();

        var result = await auditor.AuditAsync(session, "/tmp/test-ws");

        Assert.True(result.IsPassed);
    }

    [Fact]
    public async Task AuditAsync_WithJsonContainingEscapedQuotes_HandlesCorrectly()
    {
        var response = """{"IsPassed": false, "Summary": "Found issue with \"escaped\" quotes", "Risks": ["Critical: file.cs:1 - test"], "DeferredRisks": [], "LegacyRisks": []}""";
        var mockKernel = CreateKernelMock(response);
        var auditor = new DefaultCodexSecurityAuditor(mockKernel.Object, NullLogger<DefaultCodexSecurityAuditor>.Instance);
        var session = CreateSession();

        var result = await auditor.AuditAsync(session, "/tmp/test-ws");

        Assert.False(result.IsPassed);
        Assert.Equal("Found issue with \"escaped\" quotes", result.Summary);
    }

    [Fact]
    public async Task AuditAsync_WithMultipleCodeFences_StripsAll()
    {
        var response = """
Here's the result:
```json
{"IsPassed": true, "Summary": "Clean", "Risks": [], "DeferredRisks": [], "LegacyRisks": []}
```
Hope this helps!
""";
        var mockKernel = CreateKernelMock(response);
        var auditor = new DefaultCodexSecurityAuditor(mockKernel.Object, NullLogger<DefaultCodexSecurityAuditor>.Instance);
        var session = CreateSession();

        var result = await auditor.AuditAsync(session, "/tmp/test-ws");

        Assert.True(result.IsPassed);
    }
}
