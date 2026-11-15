// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Text;

namespace CodexFlow.Contracts
{
    public class ChatHistoryDetailDto
    {
        public List<ChatMessageDto> Messages { get; set; } = new List<ChatMessageDto>();
        public List<ArchiveFileDto> ArchiveFiles { get; set; } = new List<ArchiveFileDto>();
    }
}
