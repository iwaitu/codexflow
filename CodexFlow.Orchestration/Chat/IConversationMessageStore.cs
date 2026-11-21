// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Chat;

namespace CodexFlow.Application;

/// <summary>Appends messages and reads the history used to resume a conversation.</summary>
public interface IConversationMessageStore
{
    Task<List<IvilsonChatMessage>> ReadAfterAsync(string conversationId, DateTime? createdAfterUtc, int? hardLimit);

    /// <summary>Appends a message and creates or updates its conversation in one storage operation.</summary>
    Task AppendAsync(string userId, string? model, IvilsonChatMessage message);
}
