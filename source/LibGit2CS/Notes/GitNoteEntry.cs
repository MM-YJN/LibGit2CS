// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Notes;

/// <summary>
/// A single entry from the notes iterator. Matches the output of
/// <c>git_note_next</c>.
/// </summary>
public readonly record struct GitNoteEntry
{
    /// <summary>The OID of the note blob.</summary>
    public GitOid NoteId { get; init; }

    /// <summary>The OID of the annotated object.</summary>
    public GitOid AnnotatedId { get; init; }

    /// <summary>Creates a note entry identifying the note blob and the annotated object.</summary>
    public GitNoteEntry(GitOid noteId, GitOid annotatedId)
    {
        NoteId = noteId;
        AnnotatedId = annotatedId;
    }
}
