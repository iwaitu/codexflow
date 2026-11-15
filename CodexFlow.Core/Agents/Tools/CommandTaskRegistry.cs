// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;

namespace CodexFlow.Core.Agents.Tools;

public enum CommandTaskStatus
{
    Running,
    Completed,
    Failed,
    TimedOut,
    Stopped
}

public sealed record CommandTaskEvent(
    long Seq,
    string Stream,
    string Text,
    DateTimeOffset OccurredAtUtc);

public sealed record CommandTaskSnapshot(
    string CommandTaskId,
    CommandTaskStatus Status,
    string Command,
    string WorkingDirectory,
    int? ExitCode,
    long LatestSeq,
    IReadOnlyList<CommandTaskEvent> Events,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

/// <summary>Output sink handed to the runner of an isolated command task.</summary>
public sealed class CommandTaskSink
{
    private readonly Action<string, string> _add;

    internal CommandTaskSink(Action<string, string> add) => _add = add;

    public void Add(string stream, string text) => _add(stream, text);
}

public sealed record CommandTaskOutcome(CommandTaskStatus Status, int? ExitCode);

/// <summary>
/// Registry of observable command tasks (task_output / task_stop). It never starts host processes itself: the work is
/// supplied as a runner that executes inside the isolation backend. Stop cancels the runner, which kills the container.
/// </summary>
public static class CommandTaskRegistry
{
    private static readonly ConcurrentDictionary<string, CommandTaskState> Tasks = new(StringComparer.OrdinalIgnoreCase);

    public static string StartIsolated(
        string command,
        string workingDirectory,
        TimeSpan timeout,
        Func<CommandTaskSink, CancellationToken, Task<CommandTaskOutcome>> run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var id = "cmd_" + Guid.NewGuid().ToString("N");
        var state = new CommandTaskState(id, command, workingDirectory, timeout);
        if (!Tasks.TryAdd(id, state))
        {
            throw new InvalidOperationException($"Duplicate command task id: {id}");
        }

        state.AddEvent("system", $"started: {command}");
        var sink = new CommandTaskSink(state.AddEvent);
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await run(sink, state.Cancellation.Token).ConfigureAwait(false);
                state.MarkCompleted(outcome.Status, outcome.ExitCode);
                state.AddEvent("system", $"finished: {outcome.Status} exit={outcome.ExitCode?.ToString() ?? "n/a"}");
            }
            catch (OperationCanceledException)
            {
                state.MarkCompleted(CommandTaskStatus.Stopped, null);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                state.AddEvent("stderr", ex.Message);
                state.MarkCompleted(CommandTaskStatus.Failed, null);
            }
        });
        return id;
    }

    public static bool TryGet(string commandTaskId, long? afterSeq, int maxEvents, out CommandTaskSnapshot snapshot)
    {
        snapshot = null!;
        if (!Tasks.TryGetValue(commandTaskId, out var state))
        {
            return false;
        }

        snapshot = state.Snapshot(afterSeq, maxEvents);
        return true;
    }

    public static async Task<CommandTaskSnapshot?> WaitForCompletionAsync(string commandTaskId, CancellationToken ct = default)
    {
        if (!Tasks.TryGetValue(commandTaskId, out var state))
        {
            return null;
        }

        await state.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
        return state.Snapshot(afterSeq: null, maxEvents: 500);
    }

    public static bool Stop(string commandTaskId, out CommandTaskSnapshot? snapshot)
    {
        snapshot = null;
        if (!Tasks.TryGetValue(commandTaskId, out var state))
        {
            return false;
        }

        state.Stop();
        snapshot = state.Snapshot(afterSeq: null, maxEvents: 200);
        return true;
    }

    private sealed class CommandTaskState
    {
        private readonly object _gate = new();
        private readonly List<CommandTaskEvent> _events = [];
        private long _latestSeq;

        public CommandTaskState(string id, string command, string workingDirectory, TimeSpan timeout)
        {
            Id = id;
            Command = command;
            WorkingDirectory = workingDirectory;
            Timeout = timeout;
        }

        public string Id { get; }
        public string Command { get; }
        public string WorkingDirectory { get; }
        public TimeSpan Timeout { get; }
        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAtUtc { get; private set; }
        public CommandTaskStatus Status { get; private set; } = CommandTaskStatus.Running;
        public int? ExitCode { get; private set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AddEvent(string stream, string text)
        {
            lock (_gate)
            {
                _events.Add(new CommandTaskEvent(++_latestSeq, stream, text, DateTimeOffset.UtcNow));
            }
        }

        public void MarkCompleted(CommandTaskStatus status, int? exitCode)
        {
            lock (_gate)
            {
                if (Status != CommandTaskStatus.Running)
                {
                    return;
                }

                Status = status;
                ExitCode = exitCode;
                CompletedAtUtc = DateTimeOffset.UtcNow;
            }

            Completion.TrySetResult();
        }

        public void Stop()
        {
            // Mark first so a racing completion cannot report success for a stopped task, then cancel the runner.
            MarkCompleted(CommandTaskStatus.Stopped, null);
            AddEvent("system", "stopped by task_stop");
            Cancellation.Cancel();
        }

        public CommandTaskSnapshot Snapshot(long? afterSeq, int maxEvents)
        {
            lock (_gate)
            {
                var events = _events
                    .Where(evt => !afterSeq.HasValue || evt.Seq > afterSeq.Value)
                    .Take(Math.Clamp(maxEvents, 1, 500))
                    .ToArray();

                return new CommandTaskSnapshot(
                    Id,
                    Status,
                    Command,
                    WorkingDirectory,
                    ExitCode,
                    _latestSeq,
                    events,
                    StartedAtUtc,
                    CompletedAtUtc);
            }
        }
    }
}
