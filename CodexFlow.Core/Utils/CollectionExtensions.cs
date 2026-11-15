// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Utils;

internal static class CollectionExtensions
{
    public static void AddRange<T>(this ICollection<T> target, IEnumerable<T>? items)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (items == null)
        {
            return;
        }

        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
