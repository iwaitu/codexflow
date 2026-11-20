// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexFlow.Core.Execution.Approval;

internal sealed record ExecutionApprovalDigest
{
    public string? SessionId { get; init; }
    public string? WorkspaceRoot { get; init; }
    public required string Tool { get; init; }
    public required string Executable { get; init; }
    public bool TrustedOrigin { get; init; }
    public required IReadOnlyList<string> InterpreterChain { get; init; }
    public required IReadOnlyList<string> Argv { get; init; }
    public required string Cwd { get; init; }
    public string? StdinSha256 { get; init; }
    public string? CodeSha256 { get; init; }
    public string? ScriptSha256 { get; init; }
    public string? InputsManifestSha256 { get; init; }
    public required string[][] EnvDelta { get; init; }
    public required string Backend { get; init; }
    public required string Network { get; init; }
    public required string Filesystem { get; init; }
    public required string Origin { get; init; }
    public int PolicyVersion { get; init; }
    public string? WrapperSha256 { get; init; }
    public string? ExportScopeSha256 { get; init; }
    public int TimeoutSeconds { get; init; }
    public string? ImageDigest { get; init; }
    public string? ShellKind { get; init; }
}

internal sealed record ExecutionApprovalAudit(
    string RequestDigest,
    string Tool,
    string Executable,
    IReadOnlyList<string> InterpreterChain,
    IReadOnlyList<string> Argv,
    string Cwd,
    string Backend,
    string Network,
    string Filesystem,
    string Outcome,
    string DecidedBy,
    bool SelfApprovalProtection,
    string Origin,
    int PolicyVersion,
    string? Shell,
    string? Note);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ExecutionApprovalDigest))]
[JsonSerializable(typeof(ExecutionApprovalAudit))]
internal sealed partial class ExecutionApprovalJsonContext : JsonSerializerContext
{
    // The digest is persisted in permission rules. Keep its encoder and property order
    // independent of the audit log's default escaping.
    internal static ExecutionApprovalJsonContext Canonical { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}
