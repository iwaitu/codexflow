// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.BusinessSkills;

namespace CodexFlow.Core.Tests.BusinessSkills;

/// <summary>
/// Test-only credential storage for platform-independent workflow tests. Nothing is written to disk.
/// Native protection and unsupported-platform rejection are covered by P1SecurityAndCredentialTests.
/// </summary>
internal sealed class InMemoryBusinessCredentialStore : IBusinessCredentialStore
{
    private readonly ConcurrentDictionary<(string Reference, string FileName), string> _secrets = new();

    // Simulate an available store so workflow tests can exercise credential binding on every OS.
    public bool IsPersistentProtectionAvailable => true;

    public Task<StoredBusinessCredentialResult> StoreSecretAsync(
        string credentialRef, BusinessAuthKind authKind, string secretJson, string revision,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var fileName = Guid.NewGuid().ToString("N") + ".test-only";
        _secrets[(credentialRef, fileName)] = secretJson;
        return Task.FromResult(new StoredBusinessCredentialResult(new CredentialRefSummaryDto
        {
            CredentialRef = credentialRef,
            AuthKind = authKind,
            MaskedHint = BuildMaskedHint(authKind, secretJson),
            Revision = revision,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, fileName));
    }

    public Task<string?> ReadSecretAsync(string credentialRef, string cipherFileName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _secrets.TryGetValue((credentialRef, cipherFileName), out var secret);
        return Task.FromResult(secret);
    }

    public Task DeleteSecretAsync(string credentialRef, string cipherFileName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _secrets.TryRemove((credentialRef, cipherFileName), out _);
        return Task.CompletedTask;
    }

    public string BuildMaskedHint(BusinessAuthKind authKind, string secretJson)
        => authKind == BusinessAuthKind.None ? "(none)" : "****";
}
