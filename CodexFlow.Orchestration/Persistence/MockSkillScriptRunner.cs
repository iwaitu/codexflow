// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;

namespace CodexFlow.Infrastructure.Scripting;

public class MockSkillScriptRunner : ISkillScriptRunner
{
    public Task<object?> RunScriptAsync(string scriptName, object? context = null)
    {
        // Mock implementation
        return Task.FromResult<object?>("Mock script execution result");
    }

    public Task<string> RunAsync(string skillName, string scriptRelativePath, IReadOnlyList<string>? args, CancellationToken cancellationToken)
    {
        return Task.FromResult($"Mock executed: {skillName}/{scriptRelativePath}");
    }
}
