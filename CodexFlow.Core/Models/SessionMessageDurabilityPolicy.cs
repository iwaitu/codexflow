// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Models;

/// <summary>
/// Maps runtime/orchestrator events to session-message durability semantics.
/// </summary>
public static class SessionMessageDurabilityPolicy
{
    public static SessionTurnWrite FromEvent(CodexEventType type, string message)
    {
        return type switch
        {
            CodexEventType.StageStarted or
            CodexEventType.StageCompleted or
            CodexEventType.TaskStarted or
            CodexEventType.TaskCompleted or
            CodexEventType.TaskFailed or
            CodexEventType.GuardrailBlocked or
            CodexEventType.ValidationResult or
            CodexEventType.TaskListUpdated or
            CodexEventType.PlanningSummary or
            CodexEventType.RecoveryTriggered or
            CodexEventType.Error
                => SessionTurnWrite.SystemBoundary(message),

            _ => SessionTurnWrite.Progress(message)
        };
    }
}
