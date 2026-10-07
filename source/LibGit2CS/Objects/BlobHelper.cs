// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.Objects;

/// <summary>
/// Blob creation helpers. Managed port of <c>git_blob__create_from_paths</c>
/// (blob.c:185-270) and related functions.
/// </summary>
internal static class BlobHelper
{
    /// <summary> Creates a blob from a workdir file, applying clean filters. Returns the blob OID and stat-derived entry. Matches
    /// <c>git_blob__create_from_paths</c> (blob.c:185-270) as used by <c>stash_to_index</c> (stash.c:171-195). Byte-faithful: the FS-boundary <c>Path.Join</c>
    /// routes through <see cref="GitPath.ToFileSystemString"/>; the <c>GitFilterList.LoadAsync</c> call uses the <see cref="GitPath"/> overload (no UTF-8
    /// round-trip on the attribute path). </summary> <param name="repo">The repository.</param> <param name="path">The path relative to the repo
    /// workdir.</param> <returns>The blob OID and the stat-derived index entry (without OID set in the returned entry's Id field — caller must set
    /// it).</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<(GitOid oid, GitIndexEntry entry)> CreateFromWorkdirAsync(
        GitRepository repo, GitPath path, CancellationToken cancellationToken = default)
    {
        string workdir = repo.Workdir
            ?? throw new GitException(GitErrorCode.BareRepo, "cannot create blob on a bare repository", GitErrorCategory.Odb);

        // FS boundary: single transcode point.
        string fullPath = Path.Join(workdir, path.ToFileSystemString());
        var fi = new FileInfo(fullPath);

        if (!fi.Exists)
        {
            if (Directory.Exists(fullPath))
            {
                throw new GitException(
                    GitErrorCode.Directory,
                    $"cannot create blob from '{path.ToUtf8String()}' — it is a directory",
                    GitErrorCategory.Odb);
            }

            throw new GitException(
                GitErrorCode.NotFound,
                $"cannot create blob from '{path.ToUtf8String()}' — file does not exist",
                GitErrorCategory.Odb);
        }

        GitFileMode mode = StatUtil.GetFileMode(fi);
        (IndexTime ctime, IndexTime mtime, uint dev, uint ino, uint uid, uint gid, uint size) = StatUtil.GetStatInfoForIndex(fi); // C index-write path stores st_rdev (index.c:909)

        ReadOnlyMemory<byte> content;
        if (mode == GitFileMode.Symlink)
        {
            // read the
            // raw link bytes via readlink — FileInfo.LinkTarget decodes the
            // OS bytes as UTF-8, corrupting non-UTF-8 targets (C's
            // write_symlink hashes the raw p_readlink bytes, blob.c:163-180).
            if (!NativeStat.TryReadLinkTarget(fullPath, out byte[] linkTarget))
            {
                string? text = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
                linkTarget = Encoding.UTF8.GetBytes(text);
            }

            content = linkTarget;
        }
        else
        {
            content = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        // Apply clean filters. GitPath overload (no UTF-8 round-trip).
        GitFilterList? filters = await GitFilterList.LoadAsync(repo, path, null, GitFilterMode.ToOdb, GitFilterListFlags.None, attrCommitId: null, cancellationToken).ConfigureAwait(false);
        if (filters is not null)
        {
            content = await filters.ApplyToBufferAsync(content, cancellationToken).ConfigureAwait(false);
        }

        GitOid oid = await repo.Objects.WriteAsync(GitObjectType.Blob, content, cancellationToken).ConfigureAwait(false);

        // Build the entry with the byte-faithful path (GitPath ctor overload).
        var entry = new GitIndexEntry
        {
            Path = path,
            Mode = mode,
            Ctime = ctime,
            Mtime = mtime,
            Dev = dev,
            Ino = ino,
            Uid = uid,
            Gid = gid,
            FileSize = size,
        };

        return (oid, entry);
    }

    /// <summary> String overload — delegates via <see cref="GitPath.FromUtf8String"/>. </summary>
    internal static Task<(GitOid oid, GitIndexEntry entry)> CreateFromWorkdirAsync(
        GitRepository repo, string path, CancellationToken cancellationToken = default)
        => CreateFromWorkdirAsync(repo, GitPath.FromUtf8String(path), cancellationToken);

    /// <summary>
    /// Creates a blob from a byte buffer. Matches
    /// <c>git_blob_create_from_buffer</c> (blob.c:484-488).
    /// </summary>
    internal static async Task<GitOid> CreateFromBufferAsync(GitRepository repo, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        return await repo.Objects.WriteAsync(GitObjectType.Blob, content, cancellationToken).ConfigureAwait(false);
    }
}
