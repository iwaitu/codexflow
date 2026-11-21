// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core;

/// <summary>Owns a resource until startup completes and the caller transfers ownership.</summary>
internal sealed class AsyncResourceOwner<T> : IAsyncDisposable where T : class, IAsyncDisposable
{
    private T? _resource;

    public T Acquire(Func<T> create) => _resource = create();

    public async Task<T> AcquireAsync(Func<Task<T>> create)
        => _resource = await create().ConfigureAwait(false);

    public void RelinquishOwnership() => _resource = null;

    public ValueTask DisposeAsync()
    {
        var resource = _resource;
        _resource = null;
        return resource?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
