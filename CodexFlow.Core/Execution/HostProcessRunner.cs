// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CodexFlow.Core.Execution;

public sealed class HostProcessInvocation(string executable, IReadOnlyList<string> args)
{
    public string Executable { get; } = executable;
    public IReadOnlyList<string> Args { get; } = args;
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    public bool ClearEnvironment { get; init; }
    public Stream? StdIn { get; init; }
    public string? StdInText { get; init; }
    public Action<Stream>? ConsumeStdout { get; init; }
    public Action<string>? OnStdoutLine { get; init; }
    public Action<string>? OnStderrLine { get; init; }
    public TimeSpan? Timeout { get; init; }
    public long MaxCaptureBytes { get; init; } = 1024 * 1024;
}

public sealed record HostProcessResult(
    bool Started,
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    bool Canceled,
    bool Truncated,
    string? StartError = null);

/// <summary>
/// Shared argv-based process runner (UseShellExecute=false) with bounded stdout/stderr capture,
/// line callbacks, binary stdin/stdout streaming, timeout, and whole-process-tree termination.
/// Used by both <see cref="ProcessDockerCli"/> and <see cref="LocalExecutionService"/>.
/// </summary>
public static class HostProcessRunner
{
    public static async Task<HostProcessResult> RunAsync(HostProcessInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var psi = new ProcessStartInfo
        {
            FileName = invocation.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = invocation.StdIn is not null || invocation.StdInText is not null
        };

        if (!string.IsNullOrWhiteSpace(invocation.WorkingDirectory))
        {
            psi.WorkingDirectory = invocation.WorkingDirectory;
        }

        if (invocation.ClearEnvironment)
        {
            psi.Environment.Clear();
        }

        if (invocation.Environment is not null)
        {
            foreach (var (key, value) in invocation.Environment)
            {
                if (value is null)
                {
                    psi.Environment.Remove(key);
                }
                else
                {
                    psi.Environment[key] = value;
                }
            }
        }

        foreach (var arg in invocation.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new HostProcessResult(false, -1, string.Empty, ex.Message, false, false, false, ex.Message);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (invocation.Timeout is { } timeout)
        {
            timeoutCts.CancelAfter(timeout);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var truncated = false;

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
            throw new IOException("process output consumer failed: " + consumerError.Message, consumerError);
        }

        return new HostProcessResult(
            true,
            timedOut || canceled ? -1 : process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            timedOut,
            canceled,
            truncated);
    }
}
