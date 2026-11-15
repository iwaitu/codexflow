// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Text;

namespace CodexFlow.Domain.Chat
{
    public class IvilsonChatFile
    {
        public IvilsonChatFile()
        {
            Id = Guid.NewGuid().ToString();
            UpdatedAt = DateTime.UtcNow;
        }
        public required string Id { get; set; }
        public required string FileName { get; set; }

        public long FileSize { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public FileType Type { get; set; } = FileType.Text;

    }

    public enum FileType
    {
        Text,
        Image,
        Zip
    }
}

