// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Protocols;
using CodexFlow.Domain.Jobs;

namespace CodexFlow.Services.Notifications;

/// <summary>
/// Carries a failed job's structured failure through the notification pipeline: the dispatcher writes it into the envelope's string metadata,
/// the injection step reads it back into the message the coordinator gets. A job that did not fail adds nothing.
/// </summary>
public static class JobFailureNotificationMeta
{
    /// <summary>The metadata key.</summary>
    public const string Key = "failure";

    /// <summary>Adds the failure of <paramref name="job"/> to <paramref name="meta"/> when the job failed.</summary>
    public static void Apply(IDictionary<string, string> meta, BackgroundJob job)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(job);

        if (job.Status is not (BackgroundJobStatus.Failed or BackgroundJobStatus.FailedRecoveryNeeded))
        {
            return;
        }

        meta[Key] = JobFailureWire.ToJson(JobFailureClassifier.Classify(job.ErrorMessage, job.ResultPayloadJson));
    }

    /// <summary>The failure in <paramref name="meta"/>, or null when there is none.</summary>
    public static JobFailure? Read(IReadOnlyDictionary<string, string> meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return meta.TryGetValue(Key, out var json) ? JobFailureWire.FromJson(json) : null;
    }
}
