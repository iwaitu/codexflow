// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Abstractions;

public interface ISkillScriptRunner
{
    Task<object?> RunScriptAsync(string scriptName, object? context = null);
    Task<string> RunAsync(string skillName, string scriptRelativePath, IReadOnlyList<string>? args, CancellationToken cancellationToken);
}
