// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Constants;

public static class ExecutionGuardMetadataKeys
{
    public const string ExecuteCodeTaskRetryRequired = "ExecuteCodeTaskRetryRequired";
    public const string ExecuteCodeTaskLastFailure = "ExecuteCodeTaskLastFailure";

    // Bug-Fix-F: prefix for per-task outer-loop retry counters.
    // Full key = ExternalRetryCountPrefix + taskId
    public const string ExternalRetryCountPrefix = "ECT_ExternalRetry_";

    public static string ExternalRetryCountKey(string taskId) => ExternalRetryCountPrefix + taskId;
}
