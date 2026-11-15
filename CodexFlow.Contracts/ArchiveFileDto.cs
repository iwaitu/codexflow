// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Text;

namespace CodexFlow.Contracts
{
    public class ArchiveFileDto
    {
        public required string Id { get; set; }
        public required string FileName { get; set; }
        public required string FileContent { get; set; }
        public DateTime? CreateAt { get; set; }
    }
}
