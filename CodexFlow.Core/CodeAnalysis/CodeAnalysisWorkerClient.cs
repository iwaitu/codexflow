// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Runtime;

namespace CodexFlow.Core.CodeAnalysis;

internal sealed class CodeAnalysisWorkerClient : ICancellableSnippetAnalysis, IDisposable
{
    private const string WorkerName = "CodexFlow.CodeAnalysis.Worker";
    private readonly SemaphoreSlim _requests = new(1);
    private readonly object _lifecycle = new();
    private Process? _worker;
    private long _nextId;
    private int _activeCalls;
    private bool _disposed;

    internal static CodeAnalysisWorkerClient Shared { get; } = new();

    public Task<List<CodeDiagnostic>> AnalyzeSnippetAsync(string code, CancellationToken cancellationToken)
        => AnalyzeAsync(code, RoslynCodeAnalysisService.GetReferenceDirectory(), cancellationToken);

    internal async Task<List<CodeDiagnostic>> AnalyzeAsync(
        string code, string referenceDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeCalls++;
        }

        try
        {
            return await SendAsync(code, referenceDirectory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lifecycle)
            {
                _activeCalls--;
                if (_disposed && _activeCalls == 0)
                {
                    _requests.Dispose();
                }
            }
        }
    }

    private async Task<List<CodeDiagnostic>> SendAsync(
        string code, string referenceDirectory, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RuntimeToolTimeouts.For("analyze_code"));
        var token = deadline.Token;

        try
        {
            await _requests.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodeAnalysisUnavailableException("等待 C# 语义分析进程超时。", ex);
        }

        try
        {
            var worker = GetWorker();
            var id = ++_nextId;
            var request = new CodeAnalysisRequest(CodeAnalysisWorkerJson.ProtocolVersion, id, code,
                Path.IsPathFullyQualified(referenceDirectory) || string.IsNullOrWhiteSpace(referenceDirectory)
                    ? referenceDirectory
                    : Path.GetFullPath(referenceDirectory, AppContext.BaseDirectory));
            var requestJson = JsonSerializer.Serialize(request, CodeAnalysisWorkerJson.Default.CodeAnalysisRequest);
            await worker.StandardInput.WriteLineAsync(requestJson.AsMemory(), token).ConfigureAwait(false);
            await worker.StandardInput.FlushAsync(token).ConfigureAwait(false);
            var line = await worker.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null)
            {
                StopWorker();
                throw new CodeAnalysisUnavailableException("C# 语义分析进程提前退出。");
            }

            var response = JsonSerializer.Deserialize(line, CodeAnalysisWorkerJson.Default.CodeAnalysisResponse);
            if (response is null || response.Version != CodeAnalysisWorkerJson.ProtocolVersion
                || response.Id != id || (response.Diagnostics is null) == (response.Error is null))
            {
                throw new JsonException("Invalid C# analysis response.");
            }

            return response.Diagnostics ?? throw new CodeAnalysisUnavailableException(response.Error!);
        }
        catch (OperationCanceledException ex)
        {
            StopWorker();
            cancellationToken.ThrowIfCancellationRequested();
            throw new CodeAnalysisUnavailableException("C# 语义分析超时。", ex);
        }
        catch (JsonException ex)
        {
            StopWorker();
            throw new CodeAnalysisUnavailableException("C# 语义分析进程返回了无效响应。", ex);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException and not CodeAnalysisUnavailableException
            or Win32Exception or ArgumentException or NotSupportedException)
        {
            StopWorker();
            throw new CodeAnalysisUnavailableException("C# 语义分析进程未能完成请求。", ex);
        }
        finally
        {
            _requests.Release();
        }
    }

    private Process GetWorker()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is { HasExited: false } running)
            {
                return running;
            }

            StopWorkerLocked();
            var path = Path.Combine(AppContext.BaseDirectory, "code-analysis", "worker",
                OperatingSystem.IsWindows() ? WorkerName + ".exe" : WorkerName);
            if (!File.Exists(path))
            {
                throw new CodeAnalysisUnavailableException("未安装 C# 语义分析进程。请检查应用的 code-analysis/worker 目录。");
            }

            var startInfo = new ProcessStartInfo(path)
            {
                WorkingDirectory = Path.GetDirectoryName(path)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = Encoding.UTF8
            };
            return _worker = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
    }

    private void StopWorker()
    {
        lock (_lifecycle)
        {
            StopWorkerLocked();
        }
    }

    private void StopWorkerLocked()
    {
        using var process = _worker;
        _worker = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Closing the process pipes also tells the worker to exit.
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopWorkerLocked();
            if (_activeCalls == 0)
            {
                _requests.Dispose();
            }
        }
    }
}
