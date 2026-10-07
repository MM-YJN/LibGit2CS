// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Objects;

namespace LibGit2CS.Notes;

/// <summary>
/// A git note attached to an object. Matches <c>git_note</c> in
/// <c>include/git2/notes.h</c>.
/// </summary>
public sealed class GitNote : IDisposable
{
    private readonly Commit _commit;
    private readonly GitBlob _blob;

    internal GitNote(GitOid id, Commit commit, GitBlob blob)
    {
        Id = id;
        _commit = commit;
        _blob = blob;
    }

    /// <summary>The OID of the note blob.</summary>
    public GitOid Id { get; }

    /// <summary>The author signature (from the notes commit).</summary>
    public GitSignature Author => _commit.Author;

    /// <summary>The committer signature (from the notes commit).</summary>
    public GitSignature Committer => _commit.Committer;

    /// <summary>
    /// The note message. C (notes.c:334-338): git__strndup is NUL-terminated
    /// — the message ends at the FIRST NUL byte.
    /// </summary>
    public string Message
    {
        get
        {
            ReadOnlySpan<byte> content = _blob.Content.Span;
            int nul = content.IndexOf((byte)0);
            return System.Text.Encoding.UTF8.GetString(nul < 0 ? content : content[..nul]);
        }
    }

    /// <summary>Disposes the note and its underlying objects.</summary>
    public void Dispose()
    {
        _commit.Dispose();
        _blob.Dispose();
    }
}
