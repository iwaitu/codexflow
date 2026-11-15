// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Constants;

public static class PlanningToolNames
{
    public const string Primary = "create_session_plan";
    public const string LegacyAlias = "generate_dev_plan";

    public static bool IsPlanCreationTool(string? toolName)
        => string.Equals(toolName, Primary, StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolName, LegacyAlias, StringComparison.OrdinalIgnoreCase);
}
