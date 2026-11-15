// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CodexFlow.Local;

/// <summary>The one user of a local host.</summary>
public static class LocalUser
{
    /// <summary>Owner of every session, conversation and job in the local database.</summary>
    public const string Id = "local";
}

/// <summary>
/// The secret a caller of the local interface has to present. It is generated when the host starts and written to a file next to
/// the database, so only someone who can read the project's state directory can talk to the host.
/// </summary>
public sealed class LocalAccessToken
{
    public const string FileName = "host.token";

    private readonly byte[] _valueBytes;

    private LocalAccessToken(string value, string path)
    {
        Value = value;
        Path = path;
        _valueBytes = Encoding.UTF8.GetBytes(value);
    }

    public string Value { get; }

    /// <summary>The file the token was written to.</summary>
    public string Path { get; }

    /// <summary>Generates a token and writes it to <paramref name="stateDirectory"/>, replacing the previous host's.</summary>
    public static LocalAccessToken Create(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var path = System.IO.Path.Combine(stateDirectory, FileName);
        File.WriteAllText(path, value);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return new LocalAccessToken(value, path);
    }

    /// <summary>Compares in constant time.</summary>
    public bool Matches(string? candidate)
        => !string.IsNullOrEmpty(candidate)
           && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _valueBytes);
}

/// <summary>
/// What "Bearer" means on a local host: the caller presents the host's access token and is the local user. The controllers ask
/// for this scheme by name; on the platform the same name is a signed user token.
/// </summary>
public sealed class LocalTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string Prefix = "Bearer ";
    private readonly LocalAccessToken _token;

    public LocalTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        LocalAccessToken token) : base(options, logger, encoder)
    {
        _token = token;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || !_token.Matches(header[Prefix.Length..].Trim()))
        {
            return Task.FromResult(AuthenticateResult.Fail("The access token is not this host's."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, LocalUser.Id), new Claim(ClaimTypes.Name, LocalUser.Id)],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
