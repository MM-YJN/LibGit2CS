// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Globalization;

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.Objects;

/// <summary>
/// A builder for constructing git tree objects. Managed port of libgit2's
/// <c>git_treebuilder</c> (<c>src/libgit2/tree.c</c> write side).
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="GitTreeBuilder"/> maintains an in-memory set of tree entries keyed
/// by filename. Entries can be inserted, removed, and filtered. On
/// <see cref="WriteAsync"/>, entries are sorted in git's canonical tree order (see
/// <see cref="GitTreeEntry.CompareTo"/>) and serialized as
/// <c>&lt;octal-mode&gt; &lt;name&gt;\0&lt;raw-oid&gt;</c>, then written to the
/// ODB as a tree object.
/// </para>
/// <para>
/// <b>Entry validation</b> (matches <c>check_entry</c>, tree.c:488-504):
/// filemode must be one of the 5 canonical modes; filename must be non-empty
/// and not contain <c>/</c>, <c>.</c>/<c>..</c> traversal, or <c>.git</c>;
/// OID must be non-zero. For non-gitlink entries, the object is verified to
/// exist in the repo's ODB (unless <see cref="GitSettings.StrictObjectCreation"/>
/// is false on the owning context's settings, read via
/// <c>repo.Context.Settings</c>).
/// </para>
/// <para>
/// C's <c>git_treebuilder_filter_cb</c> + <c>void *payload</c> →
/// <c>Func&lt;TreeEntry, bool&gt;</c> (true = remove).
/// </para>
/// </remarks>
public sealed class GitTreeBuilder : IDisposable
{
    private readonly GitRepository _repo;
    private readonly Dictionary<GitPath, GitTreeEntry> _entries;
    private bool _disposed;

    /// <summary>
    /// Creates a new <see cref="GitTreeBuilder"/> for <paramref name="repo"/>,
    /// optionally seeded from an existing <paramref name="source"/> tree.
    /// Matches <c>git_treebuilder_new</c> (tree.c:744-779).
    /// </summary>
    /// <param name="repo">The repository (for ODB write and object validation).</param>
    /// <param name="source">Optional source tree to copy entries from. Entries are copied without validation.</param>
    internal GitTreeBuilder(GitRepository repo, GitTree? source = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        _repo = repo;
        _entries = new Dictionary<GitPath, GitTreeEntry>();

        if (source is not null)
        {
            for (int i = 0; i < source.EntryCount; i++)
            {
                GitTreeEntry? entry = source.EntryByIndex(i);
                if (entry is null)
                {
                    continue;
                }

                // Copy without validation (matches append_entry with validate=false).
                _entries[entry.Value.Name] = entry.Value;
            }
        }
    }

    /// <summary>
    /// The number of entries in this builder.
    /// </summary>
    public int EntryCount => _entries.Count;

    /// <summary>
    /// Inserts or updates an entry. Matches <c>git_treebuilder_insert</c>
    /// (tree.c:781-817). If an entry with the same filename already exists,
    /// its OID is updated in place; otherwise a new entry is created.
    /// </summary>
    /// <param name="filename">The entry name (no <c>/</c>, no <c>.</c>/<c>..</c>, no <c>.git</c>).</param>
    /// <param name="id">The target OID (non-zero, must exist in ODB for non-gitlink).</param>
    /// <param name="filemode">The canonical file mode.</param>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Invalid"/> if the entry fails validation.
    /// </exception>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask InsertAsync(GitPath filename, GitOid id, GitFileMode filemode, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await CheckEntryAsync(_repo, filename, id, filemode, cancellationToken).ConfigureAwait(false);

        var entry = new GitTreeEntry(filemode, id, filename, GitFileModeExtensions.TypeFromMode((ushort)filemode));
        _entries[filename] = entry;
    }

    /// <summary> <c>string</c> convenience overload of <see cref="InsertAsync(GitPath, GitOid, GitFileMode, CancellationToken)"/>. Encodes via <see
    /// cref="GitPath.FromUtf8String"/>. </summary>
    public ValueTask InsertAsync(string filename, GitOid id, GitFileMode filemode, CancellationToken cancellationToken = default)
        => InsertAsync(GitPath.FromUtf8String(filename), id, filemode, cancellationToken);

    /// <summary>
    /// Gets the entry for <paramref name="filename"/>, or null if not found.
    /// Matches <c>git_treebuilder_get</c> (tree.c:832-835).
    /// </summary>
    public GitTreeEntry? Get(GitPath filename)
    {
        ThrowIfDisposed();

        return _entries.TryGetValue(filename, out GitTreeEntry entry) ? entry : null;
    }

    /// <summary> <c>string</c> convenience overload of <see cref="Get(GitPath)"/>. </summary>
    public GitTreeEntry? Get(string filename)
        => Get(GitPath.FromUtf8String(filename));

    /// <summary>
    /// Removes the entry for <paramref name="filename"/>. Matches
    /// <c>git_treebuilder_remove</c> (tree.c:837-848).
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.NotFound"/> if the entry doesn't exist.
    /// </exception>
    public void Remove(GitPath filename)
    {
        ThrowIfDisposed();

        if (!_entries.Remove(filename))
        {
            // C (tree.c:842): tree_error returns -1 (GIT_ERROR).
            throw new GitException(
                GitErrorCode.Error,
                $"failed to remove entry: file isn't in the tree - {filename.ToUtf8String()}",
                GitErrorCategory.Tree);
        }
    }

    /// <summary> <c>string</c> convenience overload of <see cref="Remove(GitPath)"/>. </summary>
    public void Remove(string filename)
        => Remove(GitPath.FromUtf8String(filename));

    /// <summary>
    /// Removes all entries for which <paramref name="filter"/> returns true.
    /// Matches <c>git_treebuilder_filter</c> (tree.c:858-878).
    /// </summary>
    /// <param name="filter">Returns true to remove the entry, false to keep it.</param>
    public void Filter(Func<GitTreeEntry, bool> filter)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(filter);

        var toRemove = new List<GitPath>();
        foreach (KeyValuePair<GitPath, GitTreeEntry> kvp in _entries)
        {
            if (filter(kvp.Value))
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (GitPath name in toRemove)
        {
            _entries.Remove(name);
        }
    }

    /// <summary>
    /// Removes all entries. Matches <c>git_treebuilder_clear</c> (tree.c:880-893).
    /// </summary>
    public void Clear()
    {
        ThrowIfDisposed();
        _entries.Clear();
    }

    /// <summary>
    /// Serializes and writes the tree to the ODB. Matches
    /// <c>git_treebuilder_write</c> (tree.c:850-856) +
    /// <c>git_treebuilder__write_with_buffer</c> (tree.c:506-557). Entries are
    /// sorted in canonical tree order and serialized as
    /// <c>&lt;octal-mode&gt; &lt;name&gt;\0&lt;raw-oid-bytes&gt;</c>.
    /// </summary>
    /// <returns>The OID of the written tree object.</returns>
    public async Task<GitOid> WriteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_entries.Count == 0)
        {
            return await _repo.Objects.WriteAsync(GitObjectType.Tree, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        }

        // Sort entries in canonical tree order.
        var sorted = new List<GitTreeEntry>(_entries.Values);
        TimSort.Sort(sorted, CompareEntries);

        // Serialize: <octal-mode> <name>\0<raw-oid>
        // Use a pooled buffer (rented from ArrayPool) instead of a MemoryStream.
        using var buf = new PooledByteBufferWriter(sorted.Count * 72);

        foreach (GitTreeEntry entry in sorted)
        {
            // Octal mode + space. C writes the RAW attr (tree.c:540-542) —
            // a non-canonical parsed mode round-trips byte-exact.
            buf.WriteSpanFormattable(new OctalPadLeftFormatter(entry.RawMode, 0), provider: CultureInfo.InvariantCulture);
            buf.Write((byte)' ');

            // Filename bytes + NUL. Write the raw stored bytes (GitPath) directly — no UTF-8 encode/decode round-trip, byte-faithful.
            ReadOnlySpan<byte> nameBytes = entry.Name.Span;
            Span<byte> nameSpan = buf.GetSpan(nameBytes.Length + 1);
            nameBytes.CopyTo(nameSpan);
            nameSpan[nameBytes.Length] = 0;
            buf.Advance(nameBytes.Length + 1);

            // Raw OID bytes.
            buf.Write(entry.Id.RawBytes);
        }

        return await _repo.Objects.WriteAsync(GitObjectType.Tree, buf.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Compares two entries using git's canonical tree sort order. Matches
    /// <c>entry_sort_cmp</c> (tree.c:64-73) + <c>git_fs_path_cmp</c>. Delegates
    /// to <see cref="GitPath.CompareTreeOrder"/> which handles the trailing-<c>/</c>
    /// subtlety for directory entries.
    /// </summary>
    private static int CompareEntries(GitTreeEntry a, GitTreeEntry b)
        => GitPath.CompareTreeOrder(a.Name, a.IsTree, b.Name, b.IsTree);

    /// <summary> Validates a tree entry. Matches <c>check_entry</c> (tree.c:488-504). </summary> <remarks> The name-validity check still routes through the
    /// <c>string</c>-based <see cref="GitPathValidator"/> (decoded lossy for non-UTF-8); the structural rules (no <c>/</c>, no <c>.</c>/<c>..</c>, no
    /// <c>.git</c>) are all ASCII and survive the decode. </remarks>
    private static async ValueTask CheckEntryAsync(GitRepository repo, GitPath filename, GitOid id, GitFileMode filemode, CancellationToken cancellationToken)
    {
        // All C tree_error failures return -1 (GIT_ERROR) with class
        // GIT_ERROR_TREE (tree.c:355-373, 488-504).
        if (!IsValidFilemode(filemode))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"failed to insert entry: invalid filemode for file - {filename.ToUtf8String()}",
                GitErrorCategory.Tree);
        }

        if (!await IsValidEntryNameAsync(repo, filename, cancellationToken).ConfigureAwait(false))
        {
            throw new GitException(
                GitErrorCode.Error,
                $"failed to insert entry: invalid name for a tree entry - {filename.ToUtf8String()}",
                GitErrorCategory.Tree);
        }

        if (id.IsZero)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"failed to insert entry: invalid null OID - {filename.ToUtf8String()}",
                GitErrorCategory.Tree);
        }

        // For non-gitlink entries, verify the object TYPE via a read
        // (git_object__is_valid, tree.c:501-502) — an existing blob in the
        // tree slot is rejected.
        if (filemode != GitFileMode.GitLink)
        {
            if (repo.Context.Settings.StrictObjectCreation
                && !await repo.Objects.IsValidAsync(id, GitFileModeExtensions.TypeFromMode((ushort)filemode), cancellationToken).ConfigureAwait(false))
            {
                throw new GitException(
                    GitErrorCode.Error,
                    $"failed to insert entry: invalid object specified - {filename.ToUtf8String()}",
                    GitErrorCategory.Tree);
            }
        }
    }

    private static bool IsValidFilemode(GitFileMode filemode)
        => filemode is GitFileMode.Tree
            or GitFileMode.Regular
            or GitFileMode.Executable
            or GitFileMode.Symlink
            or GitFileMode.GitLink;

    /// <summary>
    /// Validates an entry name. Matches <c>valid_entry_name</c> (tree.c:57-62):
    /// non-empty, no traversal (<c>.</c>/<c>..</c>), no <c>.git</c>, no
    /// embedded slash.
    /// </summary>
    private static async Task<bool> IsValidEntryNameAsync(GitRepository? repo, GitPath filename, CancellationToken cancellationToken)
    {
        if (filename.Length == 0)
        {
            return false;
        }

        return await GitPathValidator.IsValidAsync(
            filename.ToUtf8String(),
            GitPathRejectFlags.Traversal | GitPathRejectFlags.DotGit | GitPathRejectFlags.Slash,
            repo,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _entries.Clear();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
