// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Utils;

namespace LibGit2CS.Index;

/// <summary>
/// Lock-for-write scope for the git index. Managed port of
/// <c>git_indexwriter</c> (index.c:3847-3939).
/// </summary>
/// <remarks>
/// <para>
/// Creates <c>{indexPath}.lock</c> via <c>FileMode.CreateNew</c> (fails if
/// already locked → <see cref="GitErrorCode.Locked"/>), serializes the index to
/// the lock file, then atomically renames to the final path on
/// <see cref="CommitAsync"/>. <see cref="Dispose"/> deletes the lock file
/// if not committed. Matches the <c>FileRefBackend.LooseLock</c> pattern from
/// and the <c>FileConfigBackend.LockAsync</c> pattern.
/// </para>
/// <para>
/// No <c>git_filebuf</c> port — temp + <c>File.Move</c> is sufficient.
/// </para>
/// </remarks>
internal sealed class IndexWriter : IDisposable
{
    private readonly GitIndex _index;
    private readonly string _lockPath;
    private readonly string _finalPath;
    private bool _committed;
    private bool _disposed;

    private IndexWriter(GitIndex index, string lockPath, string finalPath)
    {
        _index = index;
        _lockPath = lockPath;
        _finalPath = finalPath;
    }

    /// <summary>
    /// Initializes an <see cref="IndexWriter"/> for the given index: creates
    /// the <c>.lock</c> temp file. Matches <c>git_indexwriter_init</c>
    /// (index.c:3847-3877).
    /// </summary>
    /// <remarks>
    /// The <c>FileMode.CreateNew</c> open is an O_EXCL metadata probe (matches
    /// the <c>FileConfigBackend.Lock</c> / <c>FileRefBackend.Lock</c>
    /// precedent — a metadata exemption); the body is
    /// fully synchronous.
    /// </remarks>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Locked"/> if the index is already locked.
    /// <see cref="GitErrorCode.Invalid"/> if the index has no owning repository.
    /// </exception>
    public static IndexWriter Init(GitIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        if (index.Owner is null || index.IndexPath is null)
        {
            throw new GitException(
                GitErrorCode.Invalid,
                "index has no owning repository (cannot write an in-memory index to disk)",
                GitErrorCategory.Index);
        }

        string finalPath = index.IndexPath;
        string lockPath = finalPath + ".lock";

        // Create the lock file with CreateNew — fails if it already exists.
        // This is a metadata-only O_EXCL probe (not content IO); the stream is
        // closed immediately. Stays sync-completing (the metadata exemption),
        // matching the LockAsync precedents in FileConfigBackend / FileRefBackend.
        FileStream stream;
        try
        {
            stream = new FileStream(
                lockPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
        }
        catch (IOException) when (File.Exists(lockPath))
        {
            // C (index.c:3870-3872): GIT_ELOCKED with the index-specific
            // message (the filebuf error is replaced when the path is the
            // index lock).
            throw new GitException(
                GitErrorCode.Locked,
                "the index is locked; this might be due to a concurrent or crashed process",
                GitErrorCategory.Index);
        }
        catch (DirectoryNotFoundException)
        {
            // C (filebuf.c:54-59, futils.c:80-81): the index writer opens its
            // filebuf WITHOUT GIT_FILEBUF_CREATE_LEADING_DIRS, so a missing
            // parent directory fails with GIT_ENOTFOUND — not a silent
            // directory creation.
            throw new GitException(
                GitErrorCode.NotFound,
                $"failed to create locked file '{lockPath}'",
                GitErrorCategory.Os);
        }

        stream.Dispose();

        var writer = new IndexWriter(index, lockPath, finalPath);
        return writer;
    }

    /// <summary>
    /// Commits the index writer: serializes the index to the lock file, then
    /// atomically renames <c>.lock</c> → <c>index</c>. Matches
    /// <c>git_indexwriter_commit</c> (index.c:3897-3931).
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
        {
            return;
        }

        // Sort entries before writing (case-sensitive sort is the on-disk order).
        _index.EnsureSortedForWrite();

        using (var writer = new PooledByteBufferWriter())
        {
            // Serialize the index into the pooled writer (no intermediate
            // byte[] allocation).
            _index.SerializeForWrite(writer);

            // Write to the lock file (the one real content-IO site).
            await File.WriteAllBytesAsync(_lockPath, writer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }

        // Atomically rename .lock → index: C's
        // git_filebuf_commit uses p_rename (filebuf.c:447) — atomic
        // REPLACE_EXISTING — no crash window with a missing index file.
        // File.Delete/File.Move are metadata (the stat exemption) and stay
        // sync.
        File.Move(_lockPath, _finalPath, overwrite: true);

        _committed = true;
        _index.ClearDirty();

        // Refresh the index stamp to the new disk mtime, matching
        // git_indexwriter_commit (index.c:3919-3924) which re-stats the index
        // file after the rename. This is used by the racy-git check in
        // DiffGenerator.EntryNewerThanIndex. Stat-only → stays sync.
        _index.CaptureStamp(_finalPath);
    }

    /// <summary>
    /// Cleans up the index writer: deletes the lock file if not committed.
    /// Matches <c>git_indexwriter_cleanup</c> (index.c:3933-3939). The body
    /// is metadata-only (<c>File.Delete</c>), so cleanup is synchronous.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_committed && File.Exists(_lockPath))
        {
            try
            {
                File.Delete(_lockPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
