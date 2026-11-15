// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;

namespace CodexFlow.Core.Execution;

/// <summary>
/// Finds the overlay of packages the host fetched for a session. Layout under the overlay root:
/// <c>&lt;session&gt;/current</c> (a pointer file containing the name of the active set) and <c>&lt;session&gt;/&lt;set&gt;/</c> (the NuGet package folder).
/// A new set is built beside the old one and the pointer is switched atomically, so a running guest never sees a cache change under its mount.
/// Only host-trusted identifiers are accepted; anything malformed or outside the root yields null (no overlay), never an exception.
/// </summary>
public static class RestoreOverlayLocator
{
    public const string PointerFileName = "current";
    private static readonly Regex SessionId = new(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex SetName = new(@"^[0-9a-f]{16,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsValidSessionId(string? sessionId) => sessionId is not null && SessionId.IsMatch(sessionId);

    public static bool IsValidSetName(string? name) => name is not null && SetName.IsMatch(name);

    /// <returns>The full path of the session's active overlay directory, or null when there is none.</returns>
    public static string? Resolve(string? overlayRoot, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(overlayRoot) || !IsValidSessionId(sessionId)) return null;
        try
        {
            var root = Path.GetFullPath(overlayRoot);
            var sessionDirectory = Path.GetFullPath(Path.Combine(root, sessionId!));
            if (!sessionDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;

            var pointer = Path.Combine(sessionDirectory, PointerFileName);
            if (!File.Exists(pointer)) return null;

            var name = File.ReadAllText(pointer).Trim();
            if (!IsValidSetName(name)) return null;

            var overlay = Path.GetFullPath(Path.Combine(sessionDirectory, name));
            return overlay.StartsWith(sessionDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Directory.Exists(overlay) ? overlay : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
