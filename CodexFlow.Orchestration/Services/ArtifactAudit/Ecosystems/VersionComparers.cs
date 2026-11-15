// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexFlow.Services.ArtifactAudit.Ecosystems;

/// <summary>Orders the versions of one package ecosystem. A version it cannot parse is reported as such, never guessed.</summary>
public interface IVersionComparer
{
    /// <summary>Compares two versions; <see langword="null"/> when either cannot be parsed.</summary>
    int? Compare(string left, string right);
}

/// <summary>
/// Semantic Versioning 2.0 as npm uses it: numeric core, pre-release before release, build metadata ignored. A core with fewer than
/// three parts ("13.0", found as a range boundary in real advisories) means the missing parts are 0.
/// </summary>
public sealed class SemVerComparer : IVersionComparer
{
    private static readonly Regex Pattern = new(
        @"^v?(?<major>0|[1-9]\d*)(?:\.(?<minor>0|[1-9]\d*))?(?:\.(?<patch>0|[1-9]\d*))?(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static SemVerComparer Instance { get; } = new();

    public int? Compare(string left, string right)
    {
        if (!TryParse(left, out var a) || !TryParse(right, out var b)) return null;
        for (var i = 0; i < 3; i++)
        {
            var c = a.Core[i].CompareTo(b.Core[i]);
            if (c != 0) return Math.Sign(c);
        }

        // A version without a pre-release is greater than one with.
        if (a.Pre.Length == 0 && b.Pre.Length == 0) return 0;
        if (a.Pre.Length == 0) return 1;
        if (b.Pre.Length == 0) return -1;

        for (var i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
        {
            var x = a.Pre[i];
            var y = b.Pre[i];
            var xNumeric = System.Numerics.BigInteger.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var xn);
            var yNumeric = System.Numerics.BigInteger.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var yn);
            int c;
            if (xNumeric && yNumeric) c = xn.CompareTo(yn);
            else if (xNumeric) c = -1;          // numeric identifiers sort before alphanumeric ones
            else if (yNumeric) c = 1;
            else c = string.CompareOrdinal(x, y);
            if (c != 0) return Math.Sign(c);
        }

        return Math.Sign(a.Pre.Length.CompareTo(b.Pre.Length));
    }

    private static bool TryParse(string text, out (System.Numerics.BigInteger[] Core, string[] Pre) version)
    {
        version = default;
        try
        {
            var m = Pattern.Match(text.Trim());
            if (!m.Success) return false;
            version = (
                [System.Numerics.BigInteger.Parse(m.Groups["major"].Value, CultureInfo.InvariantCulture),
                 m.Groups["minor"].Success ? System.Numerics.BigInteger.Parse(m.Groups["minor"].Value, CultureInfo.InvariantCulture) : System.Numerics.BigInteger.Zero,
                 m.Groups["patch"].Success ? System.Numerics.BigInteger.Parse(m.Groups["patch"].Value, CultureInfo.InvariantCulture) : System.Numerics.BigInteger.Zero],
                m.Groups["pre"].Success ? m.Groups["pre"].Value.Split('.') : []);
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

/// <summary>PEP 440 ordering as pip uses it: epoch, release, dev &lt; pre-release &lt; final &lt; post-release; the local part only breaks ties.</summary>
public sealed class Pep440Comparer : IVersionComparer
{
    private static readonly Regex Pattern = new(
        @"^\s*v?(?:(?<epoch>\d+)!)?(?<release>\d+(?:\.\d+)*)" +
        @"(?:[-_.]?(?<pre>alpha|a|beta|b|preview|pre|c|rc)[-_.]?(?<prenum>\d*))?" +
        @"(?:(?:-(?<postnum1>\d+))|(?:[-_.]?(?<post>post|rev|r)[-_.]?(?<postnum2>\d*)))?" +
        @"(?:[-_.]?(?<dev>dev)[-_.]?(?<devnum>\d*))?" +
        @"(?:\+(?<local>[a-z0-9]+(?:[-_.][a-z0-9]+)*))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    public static Pep440Comparer Instance { get; } = new();

    public int? Compare(string left, string right)
    {
        if (!TryParse(left, out var a) || !TryParse(right, out var b)) return null;

        var c = a.Epoch.CompareTo(b.Epoch);
        if (c != 0) return Math.Sign(c);

        var length = Math.Max(a.Release.Length, b.Release.Length);
        for (var i = 0; i < length; i++)
        {
            var x = i < a.Release.Length ? a.Release[i] : System.Numerics.BigInteger.Zero;
            var y = i < b.Release.Length ? b.Release[i] : System.Numerics.BigInteger.Zero;
            c = x.CompareTo(y);
            if (c != 0) return Math.Sign(c);
        }

        c = a.Pre.CompareTo(b.Pre);
        if (c != 0) return Math.Sign(c);
        c = a.Post.CompareTo(b.Post);
        if (c != 0) return Math.Sign(c);
        c = a.Dev.CompareTo(b.Dev);
        if (c != 0) return Math.Sign(c);

        // Same public version: a local version sorts after the plain one.
        return string.IsNullOrEmpty(a.Local) == string.IsNullOrEmpty(b.Local)
            ? Math.Sign(string.CompareOrdinal(a.Local ?? string.Empty, b.Local ?? string.Empty))
            : string.IsNullOrEmpty(a.Local) ? -1 : 1;
    }

    /// <summary>The normalized name PyPI uses: lower case with runs of <c>-</c>, <c>_</c> and <c>.</c> collapsed to <c>-</c>.</summary>
    public static string NormalizeName(string name) => Regex.Replace(name.Trim().ToLowerInvariant(), @"[-_.]+", "-");

    private readonly record struct Key(
        System.Numerics.BigInteger Epoch, System.Numerics.BigInteger[] Release, (int Rank, System.Numerics.BigInteger Number) Pre,
        (int Rank, System.Numerics.BigInteger Number) Post, (int Rank, System.Numerics.BigInteger Number) Dev, string? Local);

    private static bool TryParse(string text, out Key key)
    {
        key = default;
        try
        {
            var m = Pattern.Match(text);
            if (!m.Success) return false;

            static System.Numerics.BigInteger Number(string value)
                => value.Length == 0 ? System.Numerics.BigInteger.Zero : System.Numerics.BigInteger.Parse(value, CultureInfo.InvariantCulture);

            var hasPre = m.Groups["pre"].Success;
            var hasPost = m.Groups["postnum1"].Success || m.Groups["post"].Success;
            var hasDev = m.Groups["dev"].Success;

            // Ranks follow PEP 440's sort key: a dev release of a plain version sorts before its pre-releases, a plain version after them.
            (int, System.Numerics.BigInteger) pre;
            if (!hasPre && !hasPost && hasDev) pre = (-1, 0);
            else if (!hasPre) pre = (4, 0);
            else
            {
                var letter = m.Groups["pre"].Value.ToLowerInvariant();
                var rank = letter switch { "a" or "alpha" => 0, "b" or "beta" => 1, _ => 2 }; // c, rc, pre, preview
                pre = (rank, Number(m.Groups["prenum"].Value));
            }

            (int, System.Numerics.BigInteger) post = hasPost
                ? (1, Number(m.Groups["postnum1"].Success ? m.Groups["postnum1"].Value : m.Groups["postnum2"].Value))
                : (0, 0);
            (int, System.Numerics.BigInteger) dev = hasDev ? (0, Number(m.Groups["devnum"].Value)) : (1, 0);

            key = new Key(
                m.Groups["epoch"].Success ? Number(m.Groups["epoch"].Value) : System.Numerics.BigInteger.Zero,
                m.Groups["release"].Value.Split('.').Select(Number).ToArray(),
                pre, post, dev,
                m.Groups["local"].Success ? m.Groups["local"].Value.ToLowerInvariant() : null);
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

internal static class RankExtensions
{
    public static int CompareTo(this (int Rank, System.Numerics.BigInteger Number) a, (int Rank, System.Numerics.BigInteger Number) b)
    {
        var c = a.Rank.CompareTo(b.Rank);
        return c != 0 ? c : a.Number.CompareTo(b.Number);
    }
}
