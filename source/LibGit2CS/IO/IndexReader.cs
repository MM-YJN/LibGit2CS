// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.IO;

/// <summary>
/// Reads files from the git index (staged blobs). Matches
/// <c>index_reader</c> in <c>reader.c</c>. Looks up the path in the index
/// and loads the staged blob from the ODB.
/// </summary>
internal sealed class IndexReader : IObjectReader
{
    private readonly GitIndex _index;
    private readonly GitRepository _repo;

    /// <summary>Creates an index reader.</summary>
    public IndexReader(GitIndex index, GitRepository repo)
    {
        _index = index;
        _repo = repo;
    }

    /// <inheritdoc/>
    public async Task<ReaderReadResult> ReadAsync(GitPath path, CancellationToken cancellationToken = default)
    {
        // Uses the GitPath overload of EntryByPath directly
        // (no string round-trip).
        GitIndexEntry? entry = _index.EntryByPath(path, stage: 0);
        if (entry is null)
        {
            return new ReaderReadResult(ReadStatus.NotFound, null);
        }

        GitIndexEntry e = entry.Value;
        if (e.Id.IsZero)
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
