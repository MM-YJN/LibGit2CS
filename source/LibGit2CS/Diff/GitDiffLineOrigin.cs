// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Origin of a diff line. Maps 1:1 to <c>git_diff_line_t</c> in
/// <c>include/git2/diff.h:619-636</c>. The numeric value is the ASCII sigil
/// character used to render the line (e.g. <c>'+'</c> for an addition).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1028:Enum Storage should be Int32", Justification = "byte underlying type is intentional — the value IS the ASCII sigil.")]
public enum GitDiffLineOrigin : byte
{
    /// <summary>Unchanged context line (rendered with leading space). (<c>GIT_DIFF_LINE_CONTEXT</c> = <c>' '</c>)</summary>
    Context = (byte)' ',

    /// <summary>Line added in the new file. (<c>GIT_DIFF_LINE_ADDITION</c> = <c>'+'</c>)</summary>
    Addition = (byte)'+',

    /// <summary>Line removed from the old file. (<c>GIT_DIFF_LINE_DELETION</c> = <c>'-'</c>)</summary>
    Deletion = (byte)'-',

    /// <summary>Context line; both files have no trailing newline. (<c>GIT_DIFF_LINE_CONTEXT_EOFNL</c> = <c>'='</c>)</summary>
    ContextEofnl = (byte)'=',

    /// <summary>Old file had no trailing newline, new does. (<c>GIT_DIFF_LINE_ADD_EOFNL</c> = <c>'&gt;'</c>)</summary>
    AddEofnl = (byte)'>',

    /// <summary>Old file had a trailing newline, new does not. (<c>GIT_DIFF_LINE_DEL_EOFNL</c> = <c>'&lt;'</c>)</summary>
    DelEofnl = (byte)'<',

    /// <summary>File header line (<c>diff --git ...</c>). (<c>GIT_DIFF_LINE_FILE_HDR</c> = <c>'F'</c>)</summary>
    FileHeader = (byte)'F',

    /// <summary>Hunk header line (<c>@@ ... @@</c>). (<c>GIT_DIFF_LINE_HUNK_HDR</c> = <c>'H'</c>)</summary>
    HunkHeader = (byte)'H',

    /// <summary>Binary placeholder line. (<c>GIT_DIFF_LINE_BINARY</c> = <c>'B'</c>)</summary>
    Binary = (byte)'B',
}
