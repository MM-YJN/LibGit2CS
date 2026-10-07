// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using LibGit2CS.Core;

namespace LibGit2CS.Config;

/// <summary>
/// Read-side contract for a configuration backend. Managed equivalent of the
/// read methods on libgit2's <c>git_config_backend</c> vtable.
/// </summary>
/// <remarks>
/// <para>
/// libgit2's <c>git_config_backend</c> (defined in <c>git2/sys/config.h</c>) is a
/// vtable with 11 function pointers: <c>open</c>, <c>get</c>, <c>set</c>,
/// <c>set_multivar</c>, <c>del</c>, <c>del_multivar</c>, <c>iterator</c>,
/// <c>snapshot</c>, <c>lock</c>, <c>unlock</c>, <c>free</c>. This interface unifies
/// the read and write halves (single-vtable model, matching C). Read-only
/// backends (<see cref="ReadOnly"/> = true) throw <see cref="NotSupportedException"/>
/// from write methods.
/// </para>
/// <para>
/// The C <c>version</c> field and <c>readonly</c> flag are preserved as
/// <see cref="ReadOnly"/>. The <c>cfg</c> back-pointer to the owning
/// <c>git_config</c> is dropped (the managed <see cref="GitConfiguration"/> facade
/// tracks backend-to-level mapping itself).
/// </para>
/// <para>
/// <b>Async:</b> every IO-doing method returns
/// <see cref="ValueTask"/> / <see cref="ValueTask{TResult}"/> and threads a
/// <see cref="CancellationToken"/>. <see cref="EnumerateAsync"/> returns
/// <see cref="IAsyncEnumerable{T}"/>. In-memory backends
/// (<see cref="MemoryConfigBackend"/>, <see cref="SnapshotConfigBackend"/>)
/// complete synchronously (<see cref="ValueTask.CompletedTask"/> /
/// <see cref="ValueTask.FromResult"/>).
/// </para>
/// </remarks>
internal interface IConfigBackend : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Whether this backend rejects writes. Snapshots and in-memory backends
    /// are always read-only; the file backend is writable.
    /// </summary>
    bool ReadOnly { get; }

    /// <summary>
    /// Initializes the backend at the given priority level. For file backends,
    /// this reads and parses the on-disk file. For memory backends, this parses
    /// the in-memory buffer. Matches libgit2's <c>git_config_backend.open</c>.
    /// </summary>
    /// <param name="level">The config level to assign to parsed entries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task OpenAsync(GitConfigLevel level, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up a single key. Returns the matching entry or <c>null</c> if not
    /// found. Matches libgit2's <c>git_config_backend.get</c>.
    /// </summary>
    /// <param name="key">The fully-qualified normalized key (e.g. <c>core.autocrlf</c>).</param>
    /// <returns>The entry, or <c>null</c> if the key is not present in this backend.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<GitConfigEntry?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary> Byte-key variant of <see cref="LibGit2CS.Config.IConfigBackend.GetAsync(string, System.Threading.CancellationToken)"/>: the key is the raw normalized name bytes — C's <c>git_config_list_get</c> strcmps the <c>char
    /// *</c> key bytes (config_list.c:193-202), so non-UTF-8 subsection bytes round-trip byte-exact. The string overload is the UTF-8 convenience tier on top.
    /// </summary> <param name="key">The fully-qualified normalized key bytes.</param> <returns>The entry, or <c>null</c> if the key is not present in this
    /// backend.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<GitConfigEntry?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Iterates all entries in this backend in file/insertion order. Matches
    /// libgit2's <c>git_config_backend.iterator</c>.
    /// </summary>
    IAsyncEnumerable<GitConfigEntry> EnumerateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a read-only point-in-time snapshot of this backend's current
    /// state. Subsequent changes to this backend (e.g. file refresh) do not
    /// affect the returned snapshot. Matches libgit2's
    /// <c>git_config_backend.snapshot</c>.
    /// </summary>
    IConfigBackend Snapshot();

    // ── Write side ──────────────────────────────────────────────────────

    /// <summary>
    /// Sets a single config key to <paramref name="value"/> (or removes it if
    /// <paramref name="value"/> is null). Matches <c>git_config_backend.set</c>
    /// (<c>config_file_set</c>).
    /// </summary>
    /// <param name="key">The normalized fully-qualified key.</param>
    /// <param name="value">The new value, or null to delete the key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);

    /// <summary> Sets a single config key to a raw byte value (or removes it if <paramref name="value"/> is null). Byte-primary surface: the value bytes are
    /// written verbatim (ASCII-escaped), so non-UTF-8 values round-trip byte-exact. The string <see cref="SetAsync"/> is the UTF-8 convenience tier on top.
    /// </summary> <param name="key">The normalized fully-qualified key.</param> <param name="value">The raw value bytes, or null to delete the key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetBytesAsync(string key, ReadOnlyMemory<byte>? value, CancellationToken cancellationToken = default);

    /// <summary> Byte-key variant of <see cref="LibGit2CS.Config.IConfigBackend.SetBytesAsync(string, System.ReadOnlyMemory{byte}?, System.Threading.CancellationToken)"/>: the key is the raw normalized name bytes — C's <c>config_file_set</c> passes the normalized
    /// <c>char *</c> key bytes to <c>config_file_write</c> (config_file.c:294-334), so non-UTF-8 subsection bytes write byte-exact. </summary> <param
    /// name="key">The normalized fully-qualified key bytes.</param> <param name="value">The raw value bytes, or null to delete the key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetBytesAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte>? value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a multivar config key: updates all entries matching
    /// <paramref name="regexp"/> to <paramref name="value"/>, or appends a new
    /// entry if none match. Matches <c>git_config_backend.set_multivar</c>
    /// (<c>config_file_set_multivar</c>).
    /// </summary>
    /// <param name="key">The normalized fully-qualified key.</param>
    /// <param name="regexp">A regex applied to existing values; matching values are replaced.</param>
    /// <param name="value">The new value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetMultiAsync(string key, Regex regexp, string value, CancellationToken cancellationToken = default);

    /// <summary> Byte-primary variant of <see cref="SetMultiAsync"/>: the value bytes are written verbatim (C passes the multivar value to
    /// <c>config_file_write</c> unescaped). </summary> <param name="key">The normalized fully-qualified key.</param> <param name="regexp">A regex applied to
    /// existing values; matching values are replaced.</param> <param name="value">The raw new value bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetMultiBytesAsync(string key, Regex regexp, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default);

    /// <summary> Byte-key variant of <see cref="LibGit2CS.Config.IConfigBackend.SetMultiBytesAsync(string, System.Text.RegularExpressions.Regex, System.ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>: the key is the raw normalized name bytes (C's <c>config_file_set_multivar</c> passes the
    /// normalized <c>char *</c> key bytes to <c>config_file_write</c>). </summary> <param name="key">The normalized fully-qualified key bytes.</param> <param
    /// name="regexp">A regex applied to existing values; matching values are replaced.</param> <param name="value">The raw new value bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetMultiBytesAsync(ReadOnlyMemory<byte> key, Regex regexp, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a single config key. Matches <c>git_config_backend.del</c>
    /// (<c>config_file_delete</c>).
    /// </summary>
    Task DeleteKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <summary> Byte-key variant of <see cref="LibGit2CS.Config.IConfigBackend.DeleteKeyAsync(string, System.Threading.CancellationToken)"/>: the key is the raw normalized name bytes (C's <c>config_file_delete</c> passes the
    /// normalized <c>char *</c> key bytes to <c>config_file_write</c>). </summary>
    Task DeleteKeyAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all entries for <paramref name="key"/> whose value matches
    /// <paramref name="regexp"/>. Matches <c>git_config_backend.del_multivar</c>
    /// (<c>config_file_delete_multivar</c>).
    /// </summary>
    Task DeleteMultiAsync(string key, Regex regexp, CancellationToken cancellationToken = default);

    /// <summary> Byte-key variant of <see cref="LibGit2CS.Config.IConfigBackend.DeleteMultiAsync(string, System.Text.RegularExpressions.Regex, System.Threading.CancellationToken)"/>: the key is the raw normalized name bytes (C's <c>config_file_delete_multivar</c> passes
    /// the normalized <c>char *</c> key bytes to <c>config_file_write</c>). </summary>
    Task DeleteMultiAsync(ReadOnlyMemory<byte> key, Regex regexp, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks this backend for an atomic read-modify-write transaction.
    /// Creates a <c>&lt;path&gt;.lock</c> temp file and freezes the parsed content.
    /// Matches <c>git_config_backend.lock</c> (<c>config_file_lock</c>).
    /// </summary>
    Task LockAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Unlocks the backend, either committing pending changes (writing the
    /// temp file over the real config) or rolling back. Matches
    /// <c>git_config_backend.unlock</c> (<c>config_file_unlock</c>).
    /// </summary>
    /// <param name="commit">If true, commit the locked content; if false, discard.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UnlockAsync(bool commit, CancellationToken cancellationToken = default);
}
