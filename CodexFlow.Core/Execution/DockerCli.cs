// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CodexFlow.Core.Execution;

public sealed class DockerInvocation(IReadOnlyList<string> args)
{
    public IReadOnlyList<string> Args { get; } = args;
    /// <summary>Binary stdin (e.g. tar for docker cp -).</summary>
    public Stream? StdIn { get; init; }
    public string? StdInText { get; init; }
    /// <summary>Consumes binary stdout (e.g. tar from docker cp x -). When set, stdout text is not captured.</summary>
    public Action<Stream>? ConsumeStdout { get; init; }
    public Action<string>? OnStdoutLine { get; init; }
    public Action<string>? OnStderrLine { get; init; }
    public TimeSpan? Timeout { get; init; }
    public long MaxCaptureBytes { get; init; } = 1024 * 1024;
}

public sealed record DockerRunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, bool Canceled, bool Truncated);

/// <summary>Thin seam over the docker CLI so the security logic is unit-testable without a daemon.</summary>
public interface IDockerCli
{
    Task<DockerRunResult> RunAsync(DockerInvocation invocation, CancellationToken ct);
}

public sealed class ProcessDockerCli(string executable = "docker") : IDockerCli
{
    public async Task<DockerRunResult> RunAsync(DockerInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var result = await HostProcessRunner.RunAsync(
            new HostProcessInvocation(executable, invocation.Args)
            {
                StdIn = invocation.StdIn,
                StdInText = invocation.StdInText,
                ConsumeStdout = invocation.ConsumeStdout,
                OnStdoutLine = invocation.OnStdoutLine,
                OnStderrLine = invocation.OnStderrLine,
                Timeout = invocation.Timeout,
                MaxCaptureBytes = invocation.MaxCaptureBytes
            },
            ct).ConfigureAwait(false);

        if (!result.Started)
        {
            return new DockerRunResult(-1, string.Empty, "docker CLI could not be started: " + result.StartError, false, false, false);
        }

        return new DockerRunResult(result.ExitCode, result.Stdout, result.Stderr, result.TimedOut, result.Canceled, result.Truncated);
    }
}
