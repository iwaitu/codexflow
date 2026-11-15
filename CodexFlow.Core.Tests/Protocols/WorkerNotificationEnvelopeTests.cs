// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Linq;
using System.Xml.Linq;
using CodexFlow.Core.Protocols;
using Xunit;

namespace CodexFlow.Core.Tests.Protocols;

public class WorkerNotificationEnvelopeTests
{
    private readonly XmlWorkerNotificationSerializer _serializer = new();

    [Fact]
    public void T1_NormalComplete_ShortSummary()
    {
        var env = CreateTestEnv();
        var xml = _serializer.Serialize(env);
        
        Assert.Contains("<task-notification>", xml);
        Assert.Contains("<status>completed</status>", xml);
        Assert.Contains("<summary>Job done</summary>", xml);
        Assert.DoesNotContain("<result>", xml);
    }

    [Fact]
    public void T2_NormalComplete_LongSummary()
    {
        var env = CreateTestEnv(summary: new string('a', 600));
        var xml = _serializer.Serialize(env);
        
        Assert.Contains("<![CDATA[", xml);
    }

    [Fact]
    public void T3_ResultContainsSpecialChars()
    {
        var env = CreateTestEnv(result: "Error: a < b & c > d");
        var xml = _serializer.Serialize(env);
        
        Assert.Contains("<result><![CDATA[Error: a < b & c > d]]></result>", xml);
    }

    [Fact]
    public void T4_ResultIsNull()
    {
        var env = CreateTestEnv(result: null);
        var xml = _serializer.Serialize(env);
        
        Assert.DoesNotContain("<result>", xml);
    }

    [Fact]
    public void T5_WaitingUserStatus_HasResumeToken()
    {
        var env = CreateTestEnv(status: WorkerStatus.WaitingUser, resumeToken: "token-123");
        var xml = _serializer.Serialize(env);
        
        Assert.Contains("<status>waiting</status>", xml);
        Assert.Contains("<resume-token>token-123</resume-token>", xml);
    }

    [Fact]
    public void T6_CompletedStatus_NoResumeToken()
    {
        var env = CreateTestEnv(status: WorkerStatus.Completed, resumeToken: null);
        var xml = _serializer.Serialize(env);
        
        Assert.DoesNotContain("<resume-token>", xml);
    }

    [Fact]
    public void T7_VerificationReportPassed()
    {
        var env = CreateVerificationReport(passed: true);
        var xml = _serializer.Serialize(env);
        
        Assert.Contains("<verification-report>", xml);
        Assert.Contains("<passed>true</passed>", xml);
    }

    [Fact]
    public void T8_VerificationReportFailed_HasIssues()
    {
        var env = CreateVerificationReport(passed: false);
        env.Issues.Add("Test failed");
        var xml = _serializer.Serialize(env);
        
        Assert.Contains("<passed>false</passed>", xml);
        Assert.Contains("<issue>Test failed</issue>", xml);
    }

    [Fact]
    public void T9_VerificationReportNoEvidence_Omitted()
    {
        var env = CreateVerificationReport(passed: true);
        env.Evidence.Clear();
        var xml = _serializer.Serialize(env);
        
        Assert.DoesNotContain("<evidence-list>", xml);
    }

    [Fact]
    public void T10_Over10KBResult_CDataUsed()
    {
        var env = CreateTestEnv(result: new string('x', 10240));
        var xml = _serializer.Serialize(env);
        
        Assert.Contains("<![CDATA[", xml);
        Assert.True(xml.Contains("]]>") && xml.Contains("<result>"));
    }

    [Fact]
    public void T11_UsageIsNull_Omitted()
    {
        var env = CreateTestEnv();
        env = env with { Usage = null };
        var xml = _serializer.Serialize(env);
        
        Assert.DoesNotContain("<usage>", xml);
    }

    [Fact]
    public void T12_EmptySummary_NoException()
    {
        var env = CreateTestEnv(summary: "");
        var xml = _serializer.Serialize(env);
        
        Assert.Contains("<task-notification>", xml);
        Assert.DoesNotContain("<summary></summary>", xml); // Empty strings are omitted
    }

    [Fact]
    public void T13_MultiLineResult_CDataUsed()
    {
        var env = CreateTestEnv(result: "Line1\nLine2\n<xml>");
        var xml = _serializer.Serialize(env);
        
        Assert.Contains("<![CDATA[Line1\nLine2\n<xml>]]></result>", xml);
    }

    [Fact]
    public void T14_WaitingUserWithLongContext_CDataUsed()
    {
        var waitEnv = new WaitingUserEnvelope
        {
            TaskId = "T1",
            JobId = "J1",
            ResumeToken = "RT",
            Reason = "Need Input",
            Context = new string('b', 600)
        };
        var xml = _serializer.Serialize(waitEnv);
        Assert.Contains("<![CDATA[", xml);
    }

    [Fact]
    public void T15_AllFieldsPresent_CompleteStructure()
    {
        var env = CreateTestEnv();
        env = env with 
        { 
            Usage = new WorkerUsageInfo { DurationMs = 100, ToolCalls = 5, WriteToolCalls = 1, InputTokens = 10, OutputTokens = 5 },
            Result = "Result",
            ResumeToken = "Token" // Usually for waiting, but testing structure here
        };
        var xml = _serializer.Serialize(env);
        
        XDocument doc = XDocument.Parse(xml);
        Assert.NotNull(doc.Element("task-notification"));
        Assert.NotNull(doc.Root.Element("usage"));
        Assert.NotNull(doc.Root.Element("result"));
    }

    [Fact]
    public void T16_ResultContainsCDataTerminator_SerializedAsWellFormedXml()
    {
        var env = CreateTestEnv(result: "some text ]]> more text");
        var xml = _serializer.Serialize(env);

        var doc = XDocument.Parse(xml);
        Assert.Equal("some text ]]> more text", doc.Root?.Element("result")?.Value);
    }

    [Fact]
    public void T17_WorktreeMetadata_SerializesNestedStructure()
    {
        var env = CreateTestEnv() with
        {
            Worktree = new WorkerWorktreeInfo
            {
                Path = "/tmp/shadows/TASK_001",
                Retained = true,
                CommitHash = "abc123",
                ChangedFiles = ["src/Foo.cs", "tests/FooTests.cs"]
            }
        };

        var xml = _serializer.Serialize(env);
        var doc = XDocument.Parse(xml);

        Assert.Equal("/tmp/shadows/TASK_001", doc.Root?.Element("worktree")?.Element("path")?.Value);
        Assert.Equal("true", doc.Root?.Element("worktree")?.Element("retained")?.Value);
        Assert.Equal("abc123", doc.Root?.Element("worktree")?.Element("commit-hash")?.Value);
        Assert.Equal(
            ["src/Foo.cs", "tests/FooTests.cs"],
            doc.Root?.Element("worktree")?.Element("changed-files")?.Elements("file").Select(x => x.Value).ToArray());
    }

    private WorkerNotificationEnvelope CreateTestEnv(
        string summary = "Job done", 
        string? result = null,
        WorkerStatus status = WorkerStatus.Completed,
        string? resumeToken = null)
    {
        return new WorkerNotificationEnvelope
        {
            TaskId = "TASK_001",
            JobId = "JOB_001",
            WorkerType = WorkerType.Forge,
            Status = status,
            Summary = summary,
            Result = result,
            ResumeToken = resumeToken,
            Usage = new WorkerUsageInfo { DurationMs = 5000 },
            CompletedAtUtc = DateTime.UtcNow
        };
    }

    private VerificationReportEnvelope CreateVerificationReport(bool passed)
    {
        var env = new VerificationReportEnvelope
        {
            TaskId = "TASK_001",
            JobId = "JOB_001",
            Verdict = passed ? "pass" : "fail",
            Passed = passed,
            Summary = passed ? "All good" : "Some failed"
        };
        env.Evidence.Add(new VerificationEvidence { Check = "Build", Passed = true });
        return env;
    }
}
