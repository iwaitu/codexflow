// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
    public Encoding? StandardInputEncoding { get; init; }
    public Encoding? StandardOutputEncoding { get; init; }
    public Encoding? StandardErrorEncoding { get; init; }
    public Action<Stream>? ConsumeStdout { get; init; }
    public Action<string>? OnStdoutLine { get; init; }
    public Action<string>? OnStderrLine { get; init; }
    public TimeSpan? Timeout { get; init; }
    public long MaxCaptureBytes { get; init; } = 1024 * 1024;
    public bool IsolateProcessGroup { get; init; }
}

public sealed record HostProcessResult(
    bool Started,
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    bool Canceled,
    bool Truncated,
    string? StartError = null,
    int OrphanedProcessesCleaned = 0);

/// <summary>
/// Shared argv-based process runner (UseShellExecute=false) with bounded UTF-8 byte stdout/stderr capture,
/// chunked reading (safe against unbounded single lines), binary stdin/stdout streaming, timeout,
/// and whole-process-tree / Windows Job Object termination.
/// Used by both <see cref="ProcessDockerCli"/> and <see cref="LocalExecutionService"/>.
/// </summary>
public static class HostProcessRunner
{
    private const int ChunkChars = 4096;
    private const int MaxLineChars = 16 * 1024;

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
            // Always redirect stdin so child processes never inherit the parent host's Console.In pipe handle
            // (e.g. Local:TokenDelivery=Pipe). When no input is supplied, StandardInput is closed immediately after start.
            RedirectStandardInput = true,
            StandardInputEncoding = invocation.StandardInputEncoding,
            StandardOutputEncoding = invocation.StandardOutputEncoding,
            StandardErrorEncoding = invocation.StandardErrorEncoding
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

        using var jobGuard = invocation.IsolateProcessGroup && OperatingSystem.IsWindows()
            ? WindowsJobObjectGuard.TryAttach(process)
            : null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (invocation.Timeout is { } timeout)
        {
            timeoutCts.CancelAfter(timeout);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var maxCaptureBytes = Math.Max(0L, invocation.MaxCaptureBytes);
        long capturedBytes = 0;
        var truncated = false;
        var sinkLock = new object();

        void AppendCapturedText(StringBuilder sink, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var byteCount = Encoding.UTF8.GetByteCount(text);
            lock (sinkLock)
            {
                if (capturedBytes >= maxCaptureBytes)
                {
                    truncated = true;
                    return;
                }

                var remaining = maxCaptureBytes - capturedBytes;
                if (byteCount <= remaining)
                {
                    sink.Append(text);
                    capturedBytes += byteCount;
                }
                else
                {
                    var slice = TruncateToUtf8Bytes(text, (int)Math.Min(remaining, int.MaxValue));
                    sink.Append(slice);
                    capturedBytes += Encoding.UTF8.GetByteCount(slice);
                    truncated = true;
                }
            }
        }

        async Task PumpTextChunked(StreamReader reader, StringBuilder sink, Action<string>? onLine)
        {
            var buffer = new char[ChunkChars];
            var lineBuffer = new StringBuilder();
            var lineTruncated = false;

            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                var segment = new string(buffer, 0, read);
                AppendCapturedText(sink, segment);

                if (onLine is null)
                {
                    continue;
                }

                for (var i = 0; i < read; i++)
                {
                    var ch = buffer[i];
                    if (ch == '\r')
                    {
                        continue;
                    }

                    if (ch == '\n')
                    {
                        onLine(lineTruncated ? lineBuffer + "...[line truncated]" : lineBuffer.ToString());
                        lineBuffer.Clear();
                        lineTruncated = false;
                        continue;
                    }

                    if (lineBuffer.Length < MaxLineChars)
                    {
                        lineBuffer.Append(ch);
                    }
                    else
                    {
                        lineTruncated = true;
                    }
                }
            }

            if (onLine is not null && lineBuffer.Length > 0)
            {
                onLine(lineTruncated ? lineBuffer + "...[line truncated]" : lineBuffer.ToString());
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
            tasks.Add(PumpTextChunked(process.StandardOutput, stdout, invocation.OnStdoutLine));
        }

        tasks.Add(PumpTextChunked(process.StandardError, stderr, invocation.OnStderrLine));

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

        var orphanedCleaned = 0;
        if (jobGuard is not null)
        {
            orphanedCleaned = jobGuard.TerminateRemainingProcesses();
        }
        else if (invocation.IsolateProcessGroup)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
            }
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
            truncated,
            OrphanedProcessesCleaned: orphanedCleaned);
    }

    private static string TruncateToUtf8Bytes(string text, int maxBytes)
    {
        if (maxBytes <= 0 || string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
        {
            return text;
        }

        var len = maxBytes;
        // Back up from UTF-8 continuation bytes (10xxxxxx) so we never split a multi-byte character.
        while (len > 0 && (bytes[len] & 0b1100_0000) == 0b1000_0000)
        {
            len--;
        }

        return Encoding.UTF8.GetString(bytes, 0, len);
    }

    private sealed class WindowsJobObjectGuard : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectBasicProcessIdList = 3;

        private readonly SafeFileHandle _jobHandle;

        private WindowsJobObjectGuard(SafeFileHandle jobHandle)
        {
            _jobHandle = jobHandle;
        }

        public static WindowsJobObjectGuard? TryAttach(Process process)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var job = CreateJobObjectW(IntPtr.Zero, null);
            try
            {
                if (job.IsInvalid)
                {
                    return null;
                }

                var info = new JobObjectExtendedLimitInfo
                {
                    BasicLimitInformation = new JobObjectBasicLimitInfo
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose
                    }
                };
                var length = Marshal.SizeOf<JobObjectExtendedLimitInfo>();
                var ptr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)length))
                    {
                        return null;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }

                if (!AssignProcessToJobObject(job, process.Handle))
                {
                    return null;
                }

                var guard = new WindowsJobObjectGuard(job);
                job = null;
                return guard;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                job?.Dispose();
            }
        }

        public int TerminateRemainingProcesses()
        {
            if (_jobHandle.IsInvalid || _jobHandle.IsClosed)
            {
                return 0;
            }

            var count = CountAssignedProcesses();
            if (count > 0)
            {
                TerminateJobObject(_jobHandle, 1);
            }

            return count;
        }

        private int CountAssignedProcesses()
        {
            const int bufferSize = 4096;
            var ptr = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (!QueryInformationJobObject(_jobHandle, JobObjectBasicProcessIdList, ptr, bufferSize, out _))
                {
                    return 0;
                }

                var numberOfProcessesInList = Marshal.ReadInt32(ptr, sizeof(int));
                return Math.Max(0, numberOfProcessesInList);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public void Dispose()
        {
            _jobHandle.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInfo
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInfo
        {
            public JobObjectBasicLimitInfo BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern SafeFileHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeFileHandle hJob,
            int jobObjectInfoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(
            SafeFileHandle hJob,
            int jobObjectInfoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength,
            out uint lpReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeFileHandle hJob, uint uExitCode);
    }
}
