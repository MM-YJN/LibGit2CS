// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.Config;

/// <summary>
/// Read-only point-in-time copy of another config backend. Managed port of
/// libgit2's <c>config_snapshot.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// On <see cref="OpenAsync"/>, the snapshot iterates its source backend and
/// deep-copies every entry into a private <see cref="ConfigList"/>. After
/// construction, the snapshot is fully independent: subsequent file refreshes
/// or mutations in the source do not affect the snapshot's view.
/// </para>
/// <para>
/// In libgit2 this exists to give callers a stable handle whose entry pointers
/// won't be invalidated by an underlying file refresh. In managed code the GC
/// keeps old strings alive naturally, but <see cref="GitConfiguration.SnapshotAsync"/>
/// still provides the same observable point-in-time consistency.
/// </para>
/// </remarks>
internal sealed class SnapshotConfigBackend(IConfigBackend source) : IConfigBackend
{
    // CA2213: the source backend is NOT owned by the snapshot — disposing the
    // snapshot must not dispose the backend it wraps.
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "the source backend is NOT owned by the snapshot — disposing the snapshot must not dispose the backend it wraps.")]
    private readonly IConfigBackend _source = source;
    private ConfigList? _configList;

    /// <summary>
    /// The snapshot config list, throwing if <see cref="OpenAsync"/> has not
    /// populated it.
    /// </summary>
    private ConfigList OpenedList => _configList ?? throw new InvalidOperationException("snapshot config backend has not been opened");
    private bool _opened;

    /// <inheritdoc/>
    public bool ReadOnly => true;

    /// <inheritdoc/>
    public async Task OpenAsync(GitConfigLevel level, CancellationToken cancellationToken = default)
    {
        if (_opened)
        {
            return;
        }

        _opened = true;
        _configList = ConfigList.New();

        await foreach (GitConfigEntry entry in _source.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            // C (config_snapshot.c:152-154 + config_list.c:70-71): the
            // duplicated entry preserves the source's backend_type
            // ("file"/"in-memory") — it is NOT overridden to "snapshot".
            OpenedList.Append(entry);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<GitConfigEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(key));
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(key, buffer);
            return await GetAsync(buffer.AsMemory(0, bytesWritten), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc/>
    public ValueTask<GitConfigEntry?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        EnsureOpened();
        return ValueTask.FromResult(OpenedList.Get(ConfigNameKey.From(key)));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<GitConfigEntry> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpened();
        return OpenedList.Enumerate().ToAsyncEnumerableAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public IConfigBackend Snapshot() => new SnapshotConfigBackend(this);

    // ── Write side — snapshot is read-only, all throw ───────────────────

    /// <inheritdoc/>
    public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("snapshot config backend is read-only");

    /// <inheritdoc/>
    public Task SetBytesAsync(string key, ReadOnlyMemory<byte>? value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("snapshot config backend is read-only");

    /// <inheritdoc/>
    public Task SetBytesAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte>? value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("snapshot config backend is read-only");

    /// <inheritdoc/>
    public Task SetMultiAsync(string key, Regex regexp, string value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("snapshot config backend is read-only");

    /// <inheritdoc/>
    public Task SetMultiBytesAsync(string key, Regex regexp, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("snapshot config backend is read-only");

    /// <inheritdoc/>
    public Task SetMultiBytesAsync(ReadOnlyMemory<byte> key, Regex regexp, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("snapshot config backend is read-only");

    /// <inheritdoc/>
    public Task DeleteKeyAsync(string key, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("snapshot config backend is read-only");

    /// <inheritdoc/>
    public Task DeleteKeyAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("snapshot config backend is read-only");

    /// <inheritdoc/>
    public Task DeleteMultiAsync(string key, Regex regexp, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("snapshot config backend is read-only");

    /// <inheritdoc/>
    public Task DeleteMultiAsync(ReadOnlyMemory<byte> key, Regex regexp, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("snapshot config backend is read-only");

    /// <inheritdoc/>
    public Task LockAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("snapshot config backend is read-only");

    /// <inheritdoc/>
    public Task UnlockAsync(bool commit, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("snapshot config backend is read-only");

    /// <inheritdoc/>
    public void Dispose()
    {
        // ConfigList is GC-reclaimed; the source backend is NOT owned.
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureOpened()
    {
        if (!_opened)
        {
            throw new InvalidOperationException("Snapshot config backend has not been opened.");
        }
    }
}
