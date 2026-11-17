// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.BusinessSkills;

namespace CodexFlow.Core.BusinessSkills;

public sealed record StoredBusinessCredentialResult(
    CredentialRefSummaryDto Summary,
    string CipherFileName);

/// <summary>
/// 受操作系统原生安全存储保护的企业插件凭据仓库接口。
/// 数据库与日志中只保留引用标识（CredentialRef）、掩码和 Revision，明文凭据绝不落明文库。
/// </summary>
public interface IBusinessCredentialStore
{
    bool IsPersistentProtectionAvailable { get; }

    Task<StoredBusinessCredentialResult> StoreSecretAsync(
        string credentialRef,
        BusinessAuthKind authKind,
        string secretJson,
        string revision,
        CancellationToken ct = default);

    Task<string?> ReadSecretAsync(
        string credentialRef,
        string cipherFileName,
        CancellationToken ct = default);

    Task DeleteSecretAsync(
        string credentialRef,
        string cipherFileName,
        CancellationToken ct = default);

    string BuildMaskedHint(BusinessAuthKind authKind, string secretJson);
}
