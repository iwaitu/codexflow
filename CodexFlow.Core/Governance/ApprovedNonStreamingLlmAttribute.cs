// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Governance;

/// <summary>
/// Marks intentionally approved non-streaming LLM entry points so future analyzers
/// can distinguish governed facades from ad-hoc direct calls.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ApprovedNonStreamingLlmAttribute(string reason) : Attribute
{
    public string Reason { get; } = string.IsNullOrWhiteSpace(reason)
        ? throw new ArgumentException("Reason is required.", nameof(reason))
        : reason;

    public string? Ticket { get; init; }

    public string? ReviewBy { get; init; }

    public ApprovedNonStreamingLlmScope Scope { get; init; } = ApprovedNonStreamingLlmScope.Exception;
}

public enum ApprovedNonStreamingLlmScope
{
    Exception,
    Facade
}
