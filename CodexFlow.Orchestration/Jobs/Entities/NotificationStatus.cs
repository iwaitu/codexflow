// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Domain.Jobs
{
    /// <summary>
    /// 通知状态机 — 后台任务完成后的通知派发状态
    /// </summary>
    public enum NotificationStatus
    {
        /// <summary>后台任务已完成，待通知系统处理</summary>
        Pending = 0,

        /// <summary>某个实例已领取通知租约，正在执行派发</summary>
        Dispatching = 1,

        /// <summary>SignalR 同步已成功发送</summary>
        SyncDelivered = 2,

        /// <summary>任务需主会话总结，等待摘要生成</summary>
        SummaryQueued = 3,

        /// <summary>主会话注入成功，通知闭环完成</summary>
        SummaryDelivered = 4,

        /// <summary>用户离线、预算不足或限流命中，等待后续重试</summary>
        Deferred = 5,

        /// <summary>明确不需要主会话通知，被策略永久抑制</summary>
        Suppressed = 6,

        /// <summary>通知失败，可依据重试策略回退或人工介入</summary>
        Failed = 7
    }
}
