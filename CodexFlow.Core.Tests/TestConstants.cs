// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.IO;

namespace CodexFlow.Core.Tests;

public static class TestConstants
{
    public const string SessionId = "stable-test-session";
    
    // Use an isolated temp workspace by default; CI can override the root.
    public static string GetWorkspacePath()
    {
        var configuredBasePath = Environment.GetEnvironmentVariable("CODEXFLOW_TEST_WORKSPACE_ROOT");
        var basePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configuredBasePath)
                ? Path.Combine(Path.GetTempPath(), "codexflow-tests")
                : configuredBasePath);
        
        if (!Directory.Exists(basePath))
        {
            Directory.CreateDirectory(basePath);
        }

        // Give each call a unique physical directory.
        var uniquePath = Path.Combine(basePath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(uniquePath);
        
        return uniquePath;
    }
}
