// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Models.Jobs
{
    /// <summary>
    /// MongoDB document for persistent job event history
    /// </summary>
    public class JobEventLogEntry
    {
        public string EventId { get; set; } = string.Empty;

        public string? JobId { get; set; }
        
        public string? SessionId { get; set; }
        
        public string? TaskId { get; set; }
        
        public long Seq { get; set; }
        
        public string EventType { get; set; } = string.Empty;
        
        public string PayloadJson { get; set; } = "{}";
        
        public DateTime OccurredAtUtc { get; set; }
        
        public DateTime LoggedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
