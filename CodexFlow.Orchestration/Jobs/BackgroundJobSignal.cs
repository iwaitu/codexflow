// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;

namespace CodexFlow.Infrastructure.Jobs
{
    /// <summary>
    /// 后台任务信号实现，使用 SemaphoreSlim 实现轻量级唤醒机制。
    /// </summary>
    public class BackgroundJobSignal : IBackgroundJobSignal, IDisposable
    {
        private readonly SemaphoreSlim _semaphore = new(0);

        public void Pulse()
        {
            try
            {
                if (_semaphore.CurrentCount == 0)
                {
                    _semaphore.Release();
                }
            }
            catch (ObjectDisposedException) { }
        }

        public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                await _semaphore.WaitAsync(timeout, ct);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _semaphore.Dispose();
        }
    }
}