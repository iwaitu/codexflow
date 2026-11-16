// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Configuration;

internal static class GlobalSettingsFileStore
{
    public static JObject LoadRoot(string path)
    {
        if (!File.Exists(path))
        {
            return new JObject();
        }

        var text = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
    }

    public static void WriteAtomically(string path, string json)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, json);
            if (!OperatingSystem.IsWindows())
            {
                // Owner read/write only. On Windows the file inherits the user-profile ACL, which is already per user.
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
