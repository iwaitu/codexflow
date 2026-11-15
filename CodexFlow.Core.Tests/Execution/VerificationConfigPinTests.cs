// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

/// <summary>
/// The host pins the SHA-256 of the gitleaks configuration inside the verification image (<c>ArtifactAudit:ExpectedGitleaksConfigSha256</c>) and the
/// secret scanner refuses to run when the configuration it finds hashes differently. The pin was computed on a working copy whose line endings
/// were mixed, while git stored the file normalised to LF: a fresh clone on any other machine (a Mac, a Linux box, the standalone repository)
/// had a file that hashed differently from the pin, so the host would have rejected its own configuration. The file is protected from line-ending
/// normalisation, so every checkout holds the bytes the pin was computed on.
/// </summary>
public class VerificationConfigPinTests
{
    private const string ConfigPath = "docker/verification/gitleaks.toml";

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ConfigPath.Replace('/', Path.DirectorySeparatorChar))))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The repository root (the folder that holds " + ConfigPath + ") was not found above the test output.");
    }

    [Fact]
    public void TheConfigurationInTheRepository_HashesToWhatTheHostPins()
    {
        var root = RepositoryRoot();
        var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, ConfigPath))));
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "CodexFlow.Local", "appsettings.json")));
        var pinned = settings.RootElement.GetProperty("ArtifactAudit").GetProperty("ExpectedGitleaksConfigSha256").GetString();

        Assert.Equal(pinned, actual);
    }

    [Fact]
    public void TheConfiguration_IsProtectedFromLineEndingNormalisation_SoEveryCheckoutHoldsTheSameBytes()
    {
        var attributes = File.ReadAllLines(Path.Combine(RepositoryRoot(), ".gitattributes"));

        var rule = attributes.FirstOrDefault(line => Regex.IsMatch(line, @"^\s*(docker/verification/gitleaks\.toml|docker/verification/\*)\s"));

        Assert.NotNull(rule);
        Assert.Matches(@"(^|\s)(-text|binary)(\s|$)", rule!);
    }
}
