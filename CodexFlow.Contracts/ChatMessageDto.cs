// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Text;

namespace CodexFlow.Contracts
{
    public class ChatMessageDto
    {
        public required string Id { get; set; }
        public required string Role { get; set; }
        public required string Content { get; set; }
        public string? Name { get; set; }
        public string? Picture { get; set; }
        public DateTime Created { get; set; }
        public List<ArchiveFileDto>? ArchiveFiles { get; set; }
        /// <summary>消息类型：null=普通, "notification"=系统通知</summary>
        public string? MessageType { get; set; }
        /// <summary>通知元数据 JSON</summary>
        public string? NotificationMeta { get; set; }
    }
}
