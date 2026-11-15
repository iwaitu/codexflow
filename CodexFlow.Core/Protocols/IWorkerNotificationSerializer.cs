// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace CodexFlow.Core.Protocols;

public interface IWorkerNotificationSerializer
{
    string Serialize(WorkerNotificationEnvelope envelope);
    string Serialize(VerificationReportEnvelope envelope);
    string Serialize(WaitingUserEnvelope envelope);
}
