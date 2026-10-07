// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Blame;

/// <summary>
/// Each group of lines is described by a <see cref="BlameEntry"/>; it can be
/// split as we pass blame to the parents. They form a doubly-linked list in the
/// scoreboard structure, sorted by the target line number (<see cref="Lno"/>).
/// Managed port of libgit2's <c>git_blame__entry</c> (<c>blame.h:28-65</c>).
/// </summary>
/// <remarks>
/// All line numbers are 0-based internally (matching C).
/// </remarks>
internal sealed class BlameEntry
{
    /// <summary>
    /// Previous entry in the linked list (sorted by <see cref="Lno"/>).
    /// </summary>
    public BlameEntry? Prev { get; set; }

    /// <summary>
    /// Next entry in the linked list.
    /// </summary>
    public BlameEntry? Next { get; set; }

    /// <summary>
    /// The first line of this group in the final image (0-based).
    /// </summary>
    public int Lno { get; set; }

    /// <summary>
    /// How many lines this group has.
    /// </summary>
    public int NumLines { get; set; }

    /// <summary>
    /// The commit that introduced this group into the final image. May be null
    /// transiently while a split is being assembled (matches <c>ent->suspect</c>
    /// being NULL in <c>blame_overlap</c> before <c>split_overlap</c> fills it).
    /// </summary>
    public BlameOrigin? Suspect { get; set; }

    /// <summary>
    /// True if the suspect is truly guilty (blame has been assigned); false while
    /// we have not checked if the group came from one of its parents.
    /// </summary>
    public bool Guilty { get; set; }

    /// <summary>
    /// True if the entry has been scanned for copies in the current parent.
    /// (Reserved for future copy detection — not implemented in libgit2 1.9.4.)
    /// </summary>
    public bool Scanned { get; set; }

    /// <summary>
    /// The line number of the first line of this group in the suspect's file
    /// (0-based).
    /// </summary>
    public int SLno { get; set; }

    /// <summary>
    /// How significant this entry is — cached to avoid scanning lines over and
    /// over. (Used by copy detection, not implemented.)
    /// </summary>
    public uint Score { get; set; }

    /// <summary>
    /// Whether this entry has been tracked to a boundary commit (the root, or
    /// the commit specified in <c>git_blame_options.oldest_commit</c>).
    /// </summary>
    public bool IsBoundary { get; set; }

    public BlameEntry(BlameOrigin? suspect)
    {
        Suspect = suspect;
    }
}
