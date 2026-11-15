// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;

namespace CodexFlow.Services.Background;

/// <summary>
/// One field of a job's hot view, as text. Numbers are written invariant and booleans as <c>1</c>/<c>0</c>, which is how these
/// fields have always been stored and what the readers of the hot view parse.
/// </summary>
public readonly record struct HotViewField
{
    public HotViewField(string name, string? value)
    {
        Name = name;
        Value = value ?? string.Empty;
    }

    public HotViewField(string name, long value) : this(name, value.ToString(CultureInfo.InvariantCulture))
    {
    }

    public HotViewField(string name, bool value) : this(name, value ? "1" : "0")
    {
    }

    public string Name { get; }

    public string Value { get; }
}
