// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using CodexFlow.Core.Runtime;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

public sealed class WorkspaceSuppressionProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "probe-" + Guid.NewGuid().ToString("N"));

    public WorkspaceSuppressionProbeTests()
    {
        Directory.CreateDirectory(_dir);
        Git("init", "-q");
        Git("config", "user.email", "t@t");
        Git("config", "user.name", "t");
        File.WriteAllText(Path.Combine(_dir, "App.csproj"), "<Project>\n  <PropertyGroup>\n    <NoWarn>CS1591</NoWarn>\n  </PropertyGroup>\n</Project>\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "base");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in new DirectoryInfo(_dir).EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal;
            Directory.Delete(_dir, true);
        }
        catch (IOException) { }
    }

    private void Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        p.WaitForExit();
    }

    [Fact]
    public async Task ACleanWorkTreeIsUnknown_NotEvidenceOfNoSuppression() =>
        Assert.Equal(SuppressionEvidence.Unknown, await WorkspaceSuppressionProbe.ProbeAsync(_dir, default));

    [Fact]
    public async Task ChangesThatAddNoSuppressionAreNoneAdded()
    {
        File.WriteAllText(Path.Combine(_dir, "Code.cs"), "class C {}");
        File.WriteAllText(Path.Combine(_dir, "App.csproj"), File.ReadAllText(Path.Combine(_dir, "App.csproj")).Replace("CS1591", "CS1591;CS1573"));
        Assert.Equal(SuppressionEvidence.NoneAdded, await WorkspaceSuppressionProbe.ProbeAsync(_dir, default));
    }

    [Fact]
    public async Task ANoWarnForAVulnerabilityCodeThatWasAddedIsFound()
    {
        File.WriteAllText(Path.Combine(_dir, "App.csproj"), File.ReadAllText(Path.Combine(_dir, "App.csproj")).Replace("CS1591", "CS1591;NU1902"));
        Assert.Equal(SuppressionEvidence.Added, await WorkspaceSuppressionProbe.ProbeAsync(_dir, default));
    }

    [Fact]
    public async Task ANewPropsFileWithAuditSuppressionIsFound()
    {
        File.WriteAllText(Path.Combine(_dir, "Directory.Build.props"), "<Project><ItemGroup><NuGetAuditSuppress Include=\"https://x\" /></ItemGroup></Project>");
        Assert.Equal(SuppressionEvidence.Added, await WorkspaceSuppressionProbe.ProbeAsync(_dir, default));
    }

    [Fact]
    public async Task ASuppressionThatWasAlreadyInTheRepositoryIsNotBlamedOnTheWorker()
    {
        File.WriteAllText(Path.Combine(_dir, "App.csproj"), "<Project><PropertyGroup><NoWarn>NU1902</NoWarn></PropertyGroup></Project>");
        Git("add", "-A");
        Git("commit", "-q", "-m", "pre-existing");
        File.WriteAllText(Path.Combine(_dir, "Code.cs"), "class C {}");
        Assert.Equal(SuppressionEvidence.NoneAdded, await WorkspaceSuppressionProbe.ProbeAsync(_dir, default));
    }

    [Fact]
    public async Task NotAWorkTreeIsUnknown() =>
        Assert.Equal(SuppressionEvidence.Unknown, await WorkspaceSuppressionProbe.ProbeAsync(Path.GetTempPath(), default));
}
