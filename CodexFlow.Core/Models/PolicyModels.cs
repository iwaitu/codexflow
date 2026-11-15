// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Models;

public record PolicyResult(bool Success, string Summary, IReadOnlyList<RuleResult> Details);

public record RuleResult(bool Success, string RuleName, string Message, string? Evidence = null);
