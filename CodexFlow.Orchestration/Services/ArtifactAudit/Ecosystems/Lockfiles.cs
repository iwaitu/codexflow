// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexFlow.Services.ArtifactAudit.Ecosystems;

/// <summary>A package version a lockfile or a pinned requirements file fixes, and where that was written.</summary>
public sealed record LockedPackage(string Name, string Version, bool Transitive, string Source);

/// <summary>Reads <c>package-lock.json</c> (lockfileVersion 2 and 3).</summary>
public static class NpmLockfile
{
    public static bool TryParse(string json, string path, out IReadOnlyList<LockedPackage> packages, out string error)
    {
        packages = [];
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "package-lock.json is not an object."; return false; }

            var lockfileVersion = root.TryGetProperty("lockfileVersion", out var lv) && lv.TryGetInt32(out var parsedVersion) ? parsedVersion : 0;
            if (lockfileVersion < 2)
            {
                // Version 1 only has the nested "dependencies" tree; reading that as if it were complete would hide packages.
                error = $"lockfileVersion {lockfileVersion} is not supported (2 or 3 needed).";
                return false;
            }

            if (!root.TryGetProperty("packages", out var entries) || entries.ValueKind != JsonValueKind.Object)
            {
                error = "package-lock.json has no \"packages\" map.";
                return false;
            }

            var direct = new HashSet<string>(StringComparer.Ordinal);
            if (entries.TryGetProperty(string.Empty, out var rootEntry) && rootEntry.ValueKind == JsonValueKind.Object)
            {
                foreach (var section in new[] { "dependencies", "devDependencies", "optionalDependencies", "peerDependencies" })
                {
                    if (rootEntry.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var dep in deps.EnumerateObject()) direct.Add(dep.Name);
                    }
                }
            }

            var found = new Dictionary<string, LockedPackage>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateObject())
            {
                if (entry.Name.Length == 0) continue;
                var body = entry.Value;
                if (body.ValueKind != JsonValueKind.Object) continue;
                // Links and workspace members are the project's own code, not downloaded packages.
                if (body.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.True) continue;
                const string marker = "node_modules/";
                var at = entry.Name.LastIndexOf(marker, StringComparison.Ordinal);
                if (at < 0) continue;
                var name = entry.Name[(at + marker.Length)..];
                if (name.Length == 0) continue;
                if (!body.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String) continue;
                var versionText = version.GetString()!;
                var isDirect = entry.Name == marker + name && direct.Contains(name);
                var key = name + "@" + versionText;
                if (!found.TryGetValue(key, out var existing) || (existing.Transitive && isDirect))
                    found[key] = new LockedPackage(name, versionText, !isDirect, path);
            }

            packages = found.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Version, StringComparer.Ordinal).ToList();
            return true;
        }
        catch (JsonException)
        {
            error = "package-lock.json is not valid JSON.";
            return false;
        }
    }
}

/// <summary>What a requirements file fixes, and what it leaves open.</summary>
public sealed record RequirementsRead(IReadOnlyList<LockedPackage> Pins, IReadOnlyList<string> Open);

/// <summary>Reads <c>requirements*.txt</c>. Only exact pins (<c>==</c>, <c>===</c>) say which version is installed; everything else is "open".</summary>
public static class RequirementsFile
{
    private static readonly Regex Pin = new(
        @"^(?<name>[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?)\s*(?:\[[^\]]*\])?\s*===?\s*(?<version>[A-Za-z0-9][A-Za-z0-9._+!-]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static RequirementsRead Parse(string text, string path)
    {
        var pins = new Dictionary<string, LockedPackage>(StringComparer.Ordinal);
        var open = new List<string>();

        // Continuation lines first: "pkg==1.0 \" followed by "--hash=sha256:..." is one requirement.
        var joined = new StringBuilder();
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.EndsWith('\\')) { joined.Append(raw.AsSpan(0, raw.Length - 1)).Append(' '); continue; }
            joined.Append(raw);
            lines.Add(joined.ToString());
            joined.Clear();
        }

        if (joined.Length > 0) lines.Add(joined.ToString());

        foreach (var line in lines)
        {
            var requirement = StripTrailing(line);
            if (requirement.Length == 0) continue;
            if (requirement.StartsWith('-'))
            {
                // -r other.txt, -c constraints.txt, -e ., --index-url ...: they pull in packages this reading does not see.
                open.Add(Truncate(requirement));
                continue;
            }

            Match m;
            try { m = Pin.Match(requirement); }
            catch (RegexMatchTimeoutException) { open.Add(Truncate(requirement)); continue; }
            if (!m.Success || m.Groups["version"].Value.Contains('*', StringComparison.Ordinal))
            {
                open.Add(Truncate(requirement));
                continue;
            }

            var name = Pep440Comparer.NormalizeName(m.Groups["name"].Value);
            pins[name + "==" + m.Groups["version"].Value] = new LockedPackage(name, m.Groups["version"].Value, Transitive: false, path);
        }

        return new RequirementsRead(pins.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList(), open);
    }

    // Drops comments, environment markers and per-requirement options such as --hash.
    private static string StripTrailing(string line)
    {
        var s = line;
        var hash = s.IndexOf(" #", StringComparison.Ordinal);
        if (s.StartsWith('#')) return string.Empty;
        if (hash >= 0) s = s[..hash];
        var marker = s.IndexOf(';', StringComparison.Ordinal);
        if (marker >= 0) s = s[..marker];
        if (!s.TrimStart().StartsWith('-'))
        {
            var option = s.IndexOf(" --", StringComparison.Ordinal);
            if (option >= 0) s = s[..option];
        }

        return s.Trim();
    }

    private static string Truncate(string text) => text.Length <= 80 ? text : text[..80];
}
