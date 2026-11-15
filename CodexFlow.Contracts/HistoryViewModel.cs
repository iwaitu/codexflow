// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace CodexFlow.Contracts
{
    public class HistoryViewModel
    {
        public string SessionId { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public int CurrentStage { get; set; }
        public string LastActive { get; set; } = string.Empty;
        public string WorkspacePath { get; set; } = string.Empty;
        public List<string> LanguageTags { get; set; } = new List<string>();
        public List<RecentConversationDto> RecentConversations { get; set; } = new List<RecentConversationDto>();
    }

    public class RecentConversationDto
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int MsgCount { get; set; }
    }
}
