// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Application.Notifications
{
    /// <summary>
    /// 通知派发编排器 — 协调扫描、租约、策略、摘要、投递的核心入口
    /// </summary>
    public interface INotificationDispatcher
    {
        Task DispatchAsync(string jobId, CancellationToken ct, string dispatchSource = "unknown");
    }
}
