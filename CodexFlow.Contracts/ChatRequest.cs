// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Text;

namespace CodexFlow.Contracts
{
    public class ChatRequest
    {
        public string Message { get; set; } = string.Empty;
        public string GitRepoUrl { get; set; } = string.Empty;
        public string Branch { get; set; } = "master";
        public bool ApproveGitPush { get; set; }
        public string? ConversationId { get; set; }
        public string? SessionId { get; set; } = string.Empty;
        public string? ReplyId { get; set; }
        public string SystemPrompt { get; set; } = string.Empty;
        public string? TaskId { get; set; }
    }
}
