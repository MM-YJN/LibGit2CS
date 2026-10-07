// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.Notes;

/// <summary>
/// Iterator over notes in a notes tree. Matches <c>git_note_iterator</c>
/// (which wraps a tree iterator). Yields <see cref="GitNoteEntry"/> pairs.
/// </summary>
public sealed class GitNoteIterator : IAsyncEnumerable<GitNoteEntry>, IDisposable
{
    private readonly IIterator _treeIterator;
    private readonly GitRepository _repo;

    internal GitNoteIterator(IIterator treeIterator, GitRepository repo)
    {
        _treeIterator = treeIterator;
        _repo = repo;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerator<GitNoteEntry> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            GitIndexEntry? entry = await _treeIterator.AdvanceAsync(cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                break;
            }

            // Process the entry path to extract the annotated OID.
            // The path may contain / fanout separators (e.g. "ab/cdef...").
            GitOid? annotatedId = ProcessEntryPath(entry.Value.Path, _repo);
            if (annotatedId.HasValue)
            {
                yield return new GitNoteEntry(entry.Value.Id, annotatedId.Value);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _treeIterator.Dispose();
    }

    /// <summary>
    /// Strips / separators from the entry path and parses the remaining
    /// hex as an OID. Matches <c>process_entry_path</c> (notes.c:667-712).
    /// </summary>
    private static GitOid? ProcessEntryPath(GitPath path, GitRepository repo)
    {
        // Strip / separators and validate hex.
        using var sb = new ValueStringBuilder(path.Length);
        foreach (byte b in path.Span)
        {
            if (b == (byte)'/')
            {
                continue;
            }

            char c = (char)b;
            if (!IsHexDigit(c))
            {
                return null;
            }

            sb.Append(c);
        }

        ReadOnlySpan<char> hex = sb.AsSpan();
        if (hex.Length != GitOid.HexSizeFor(repo.ObjectFormat))
        {
            return null;
        }

        if (GitOid.TryParse(hex, repo.ObjectFormat, out GitOid oid))
        {
            return oid;
        }

        return null;
    }

    private static bool IsHexDigit(char c)
        => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
