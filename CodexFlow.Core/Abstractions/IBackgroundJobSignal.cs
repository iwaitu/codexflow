// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Abstractions
{
    /// <summary>
    /// 信号接口，用于唤醒后台任务调度器。
    /// 当有新任务入队时调用 Pulse() 唤醒 Supervisor，避免空轮询。
    /// </summary>
    public interface IBackgroundJobSignal
    {
        /// <summary>
        /// 发送唤醒信号
        /// </summary>
        void Pulse();

        /// <summary>
        /// 等待信号或超时
        /// </summary>
        Task WaitAsync(TimeSpan timeout, CancellationToken ct);
    }
}