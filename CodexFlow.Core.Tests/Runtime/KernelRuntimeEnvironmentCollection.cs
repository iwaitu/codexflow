// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KernelRuntimeEnvironmentCollection
{
    public const string Name = "KernelRuntimeEnvironment";
}
