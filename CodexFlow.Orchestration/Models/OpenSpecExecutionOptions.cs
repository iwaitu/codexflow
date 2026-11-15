// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Models;

/// <summary>
/// Controls whether OpenSpec task execution is dispatched through background jobs
/// or executed inline on the current request thread.
/// </summary>
public sealed class OpenSpecExecutionOptions
{
    /// <summary>
    /// Configuration section name for OpenSpec execution settings.
    /// </summary>
    public const string SectionName = "OpenSpecExecution";

    /// <summary>
    /// When true, <c>start_next_task</c> creates a queued background job.
    /// When false, it still creates a job record but executes it inline immediately.
    /// </summary>
    public bool UseBackgroundJob { get; set; } = true;
}
