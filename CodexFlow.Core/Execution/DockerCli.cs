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
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = invocation.StdIn is not null || invocation.StdInText is not null
        };
        foreach (var a in invocation.Args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new DockerRunResult(-1, string.Empty, "docker CLI could not be started: " + ex.Message, false, false, false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (invocation.Timeout is { } t)
        {
            timeoutCts.CancelAfter(t);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var truncated = false;
        var stdoutPending = new StringBuilder();

        async Task PumpText(StreamReader reader, StringBuilder sink, Action<string>? onLine)
        {
            string? line;
            while ((line = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
            {
                onLine?.Invoke(line);
                lock (sink)
                {
                    if (sink.Length < invocation.MaxCaptureBytes)
                    {
                        sink.AppendLine(line);
                    }
                    else
                    {
                        truncated = true;
                    }
                }
            }
        }

        var tasks = new List<Task>();
        Exception? consumerError = null;
        if (invocation.ConsumeStdout is { } consume)
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    consume(process.StandardOutput.BaseStream);
                    // drain whatever the consumer left, so docker does not block on a full pipe
                    process.StandardOutput.BaseStream.CopyTo(Stream.Null);
                }
                catch (Exception ex)
                {
                    consumerError = ex;
                    try
                    {
                        process.StandardOutput.BaseStream.CopyTo(Stream.Null);
                    }
                    catch (IOException)
                    {
                    }
                }
            }, CancellationToken.None));
        }
        else
        {
            tasks.Add(PumpText(process.StandardOutput, stdout, invocation.OnStdoutLine));
        }

        tasks.Add(PumpText(process.StandardError, stderr, invocation.OnStderrLine));

        if (psi.RedirectStandardInput)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (invocation.StdIn is not null)
                    {
                        await invocation.StdIn.CopyToAsync(process.StandardInput.BaseStream, CancellationToken.None).ConfigureAwait(false);
                    }
                    else if (invocation.StdInText is not null)
                    {
                        await process.StandardInput.WriteAsync(invocation.StdInText).ConfigureAwait(false);
                    }

                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // process exited early
                }
            }, CancellationToken.None));
        }

        var timedOut = false;
        var canceled = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            canceled = ct.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        if (consumerError is not null)
        {
            throw new IOException("docker output consumer failed: " + consumerError.Message, consumerError);
        }

        _ = stdoutPending;
        return new DockerRunResult(timedOut || canceled ? -1 : process.ExitCode, stdout.ToString(), stderr.ToString(), timedOut, canceled, truncated);
    }
}
