// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Blame;

/// <summary>
/// Flags for indicating option behavior for <see cref="GitBlame"/> APIs.
/// Managed equivalent of libgit2's <c>git_blame_flag_t</c>.
/// </summary>
/// <remarks>
/// Values MUST match C exactly — the <c>TRACK_COPIES</c> implication chain in
/// <c>normalize_options</c> (blame.c:279-284) depends on exact bit positions.
/// </remarks>
[Flags]
public enum GitBlameFlags
{
    /// <summary>
    /// Normal blame, the default. Matches <c>GIT_BLAME_NORMAL</c>.
    /// </summary>
    Normal = 0,

    /// <summary>
    /// Track lines that have moved within a file (like <c>git blame -M</c>).
    /// Reserved for future use — not implemented in libgit2 1.9.4.
    /// Matches <c>GIT_BLAME_TRACK_COPIES_SAME_FILE</c> (1&lt;&lt;0).
    /// </summary>
    TrackCopiesSameFile = 1,

    /// <summary>
    /// Track lines that have moved across files in the same commit
    /// (like <c>git blame -C</c>). Implies <see cref="TrackCopiesSameFile"/>.
    /// Reserved for future use. Matches <c>GIT_BLAME_TRACK_COPIES_SAME_COMMIT_MOVES</c> (1&lt;&lt;1).
    /// </summary>
    TrackCopiesSameCommitMoves = 2,

    /// <summary>
    /// Track lines copied from another file in the same commit (like
    /// <c>git blame -CC</c>). Implies <see cref="TrackCopiesSameCommitMoves"/>.
    /// Reserved for future use. Matches <c>GIT_BLAME_TRACK_COPIES_SAME_COMMIT_COPIES</c> (1&lt;&lt;2).
    /// </summary>
    TrackCopiesSameCommitCopies = 4,

    /// <summary>
    /// Track lines copied from any commit (like <c>git blame -CCC</c>).
    /// Implies <see cref="TrackCopiesSameCommitCopies"/>.
    /// Reserved for future use. Matches <c>GIT_BLAME_TRACK_COPIES_ANY_COMMIT_COPIES</c> (1&lt;&lt;3).
    /// </summary>
    TrackCopiesAnyCommitCopies = 8,

    /// <summary>
    /// Restrict the search of commits to those reachable following only the
    /// first parents. Matches <c>GIT_BLAME_FIRST_PARENT</c> (1&lt;&lt;4).
    /// </summary>
    FirstParent = 16,

    /// <summary>
    /// Use mailmap file to map author and committer names and emails to
    /// canonical real names and email addresses. The mailmap is auto-loaded
    /// from the repository when <see cref="GitBlameOptions.Mailmap"/> is null;
    /// otherwise the supplied mailmap is used.
    /// Matches <c>GIT_BLAME_USE_MAILMAP</c> (1&lt;&lt;5).
    /// </summary>
    UseMailmap = 32,

    /// <summary>
    /// Ignore whitespace differences. Matches
    /// <c>GIT_BLAME_IGNORE_WHITESPACE</c> (1&lt;&lt;6).
    /// </summary>
    IgnoreWhitespace = 64,
}
