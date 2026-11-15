// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

/// <summary>
/// git_clone (tool and GitService) clones remote https/ssh sources only. File URIs, local paths, ext::/fd:: transports and every
/// other scheme are refused; nothing in the tool arguments - including a made-up "authorized_local_source" - can authorize one.
/// </summary>
public sealed class GitCloneSourcePolicyTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "gcsp-" + Guid.NewGuid().ToString("N"));

    public GitCloneSourcePolicyTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private (GitWorkspaceTool Tool, Mock<IGitService> Git) Create()
    {
        var git = new Mock<IGitService>();
        git.Setup(x => x.CloneRepositoryAsync(It.IsAny<Uri>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync(false);
        var tool = new GitWorkspaceTool("git_clone", "clone", [1], git.Object, NullLogger<GitWorkspaceTool>.Instance);
        return (tool, git);
    }

    private static void NeverCloned(Mock<IGitService> git)
        => git.Verify(x => x.CloneRepositoryAsync(It.IsAny<Uri>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);

    private Dictionary<string, object?> Args(string url, params (string Key, object? Value)[] extra)
    {
        var args = new Dictionary<string, object?> { ["workspace_path"] = _workspace, ["url"] = url };
        foreach (var (key, value) in extra) args[key] = value;
        return args;
    }

    private static string LocalPath() => Path.Combine(Path.GetTempPath(), "someone-elses-repo");

    // ---- the tool ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LocalFileUri_IsRefused_AndNeverCloned()
    {
        var (tool, git) = Create();
        var result = await tool.ExecuteAsync(Args(new Uri(LocalPath()).AbsoluteUri));
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("clone_source_not_supported", result.Output);
        NeverCloned(git);
    }

    [Fact]
    public async Task LocalAbsolutePath_IsRefused()
    {
        var (tool, git) = Create();
        Assert.Equal(ToolResultStatus.Failed, (await tool.ExecuteAsync(Args(LocalPath()))).Status);
        NeverCloned(git);
    }

    [Theory]
    [InlineData("authorized_local_source")]
    [InlineData("authorized_local_source_path")]
    [InlineData("allow_local")]
    public async Task ModelSuppliedAuthorizationArguments_AreIgnored(string key)
    {
        var (tool, git) = Create();
        var local = LocalPath();
        var result = await tool.ExecuteAsync(Args(new Uri(local).AbsoluteUri, (key, local), ("authorized", true)));
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        NeverCloned(git);
    }

    [Theory]
    [InlineData("ftp://example.com/repo.git")]
    [InlineData("http://example.com/repo.git")]
    [InlineData("git://example.com/repo.git")]
    [InlineData("gopher://example.com/repo")]
    [InlineData("ext::sh -c touch /tmp/pwned")]
    [InlineData("fd::17/repo")]
    public async Task EverythingButHttpsAndSsh_IsRefused(string url)
    {
        var (tool, git) = Create();
        Assert.Equal(ToolResultStatus.Failed, (await tool.ExecuteAsync(Args(url))).Status);
        NeverCloned(git);
    }

    [Theory]
    [InlineData("https://github.com/example/repo.git")]
    [InlineData("ssh://git@example.com/repo.git")]
    public async Task HttpsAndSshSources_AreStillCloned(string url)
    {
        var (tool, git) = Create();
        await tool.ExecuteAsync(Args(url));
        git.Verify(x => x.CloneRepositoryAsync(It.IsAny<Uri>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    // ---- GitService (the second line of defense, independent of the tool) ---------------------------------------------

    [Theory]
    [InlineData("https://github.com/example/repo.git")]
    [InlineData("ssh://git@github.com/example/repo.git")]
    [InlineData("git@github.com:example/repo.git")]
    public void RemoteSources_AreAcceptedByTheService(string url) => Assert.True(GitService.IsRemoteCloneSource(url));

    [Theory]
    [InlineData("file:///C:/repos/other")]
    [InlineData("file:///home/other/repo")]
    [InlineData("C:\\repos\\other")]
    [InlineData("/home/other/repo")]
    [InlineData("../other/repo")]
    [InlineData("\\\\server\\share\\repo")]
    [InlineData("ext::sh -c id")]
    [InlineData("fd::17/x")]
    [InlineData("--upload-pack=touch /tmp/pwned")]
    [InlineData("-c core.sshCommand=evil")]
    [InlineData("http://example.com/repo.git")]
    [InlineData("git://example.com/repo.git")]
    [InlineData("ftp://example.com/repo.git")]
    [InlineData("")]
    [InlineData("   ")]
    public void NonRemoteSources_AreRefusedByTheService(string url) => Assert.False(GitService.IsRemoteCloneSource(url));

    [Fact]
    public async Task GitService_RefusesALocalSource_WithoutStartingGit_AndWithoutCreatingTheTarget()
    {
        var service = new GitService(NullLogger<GitService>.Instance);
        var target = Path.Combine(_workspace, "should-not-exist");
        var cloned = await service.CloneRepositoryAsync(new Uri(LocalPath()).AbsoluteUri, target);
        Assert.False(cloned);
        Assert.False(Directory.Exists(target));
        Assert.False(await service.CloneRepositoryAsync("ext::sh -c id", target));
        Assert.False(Directory.Exists(target));
    }
}
