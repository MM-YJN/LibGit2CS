// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.IO;

/// <summary>
/// Reads files from a git <see cref="GitTree"/> object. Matches
/// <c>tree_reader</c> in <c>reader.c</c>.
/// </summary>
internal sealed class TreeReader : IObjectReader
{
    private readonly GitTree _tree;
    private readonly GitRepository _repo;

    /// <summary>Creates a tree reader.</summary>
    public TreeReader(GitTree tree, GitRepository repo)
    {
        _tree = tree;
        _repo = repo;
    }

    /// <inheritdoc/>
    public async Task<ReaderReadResult> ReadAsync(GitPath path, CancellationToken cancellationToken = default)
    {
        // Uses the GitPath overload of EntryByPathAsync
        // directly (no string round-trip).
        GitTreeEntry? entry = await _tree.EntryByPathAsync(path, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return new ReaderReadResult(ReadStatus.NotFound, null);
        }

        GitTreeEntry e = entry.Value;
        if (e.IsTree)
        {
            return new ReaderReadResult(ReadStatus.NotFound, null);
        }

        GitBlob? blob = await _repo.Objects.LookupAsync<GitBlob>(e.Id, cancellationToken).ConfigureAwait(false);
        if (blob is null)
        {
            return new ReaderReadResult(ReadStatus.NotFound, null);
        }

        try
        {
            return new ReaderReadResult(
                ReadStatus.Found,
                new ReaderResult(blob.Content, e.Id, e.Mode));
        }
        finally
        {
            blob.Dispose();
        }
    }
}
