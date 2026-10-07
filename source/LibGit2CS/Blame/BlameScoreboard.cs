// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.Blame;

/// <summary>
/// Internal scoreboard state for the blame algorithm. Managed port of
/// libgit2's <c>struct git_blame</c> (<c>blame.h:67-89</c>) — the fields used
/// during the blame walk (not the public result).
/// </summary>
internal sealed class BlameScoreboard
{
    /// <summary>
    /// The path being blamed (byte-faithful).
    /// </summary>
    public GitPath Path { get; set; }

    /// <summary>
    /// The repository.
    /// </summary>
    public required GitRepository Repository { get; set; }

    /// <summary>
    /// Normalized blame options.
    /// </summary>
    public GitBlameOptions Options { get; set; } = new();

    /// <summary>
    /// The mailmap (loaded if <see cref="GitBlameFlags.UseMailmap"/> is set).
    /// </summary>
    public GitMailmap? Mailmap { get; set; }

    /// <summary> The sorted set of paths being tracked (for rename detection in <c>find_origin</c>). Managed port of libgit2's <c>git_vector paths</c>
    /// (<c>blame.h:76</c>, <c>blame.c:149</c>) sorted by <c>paths_cmp</c> = <c>git__strcmp</c> (<c>blame.c:37</c>). Use <see cref="PathsContains"/> / <see
    /// cref="PathsAdd"/> (BinarySearch + sorted insert) — faithful to C's <c>git_vector_bsearch</c> / <c>git_vector_insert_sorted</c> (<c>blame_git.c:478</c>).
    /// Byte-wise <see cref="LibGit2CS.IO.GitPath.Compare(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/> replaces .NET's Unicode-aware
    /// string hashing. </summary>
    public List<GitPath> Paths { get; } = [];

    /// <summary>
    /// Returns true if <paramref name="path"/> is in <see cref="Paths"/>.
    /// Matches <c>git_vector_bsearch</c> with <c>paths_cmp</c> (byte-wise).
    /// </summary>
    public bool PathsContains(GitPath path)
    {
        int idx = Paths.BinarySearch(path, GitPathComparer.Instance);
        return idx >= 0;
    }

    /// <summary>
    /// Inserts <paramref name="path"/> into <see cref="Paths"/> in sorted
    /// position (byte-wise). Matches <c>git_vector_insert_sorted</c> with
    /// <c>paths_cmp</c>. Duplicates are ignored (faithful to C's
    /// <c>paths_on_dup</c> returning -1 on duplicate, blame.c:39-50).
    /// </summary>
    public void PathsAdd(GitPath path)
    {
        int idx = Paths.BinarySearch(path, GitPathComparer.Instance);
        if (idx >= 0)
        {
            // Duplicate — matches C's paths_on_dup returning -1 (no-op insert).
            return;
        }

        Paths.Insert(~idx, path);
    }

    /// <summary>
    /// Byte-wise <see cref="GitPath"/> comparer wrapping <see cref="LibGit2CS.IO.GitPath.Compare(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/>
    /// (ports <c>paths_cmp</c> = <c>git__strcmp</c>, blame.c:37). Used by
    /// <see cref="PathsContains"/> / <see cref="PathsAdd"/>.
    /// </summary>
    private sealed class GitPathComparer : IComparer<GitPath>
    {
        public static readonly GitPathComparer Instance = new();

        public int Compare(GitPath x, GitPath y) => GitPath.Compare(x, y);
    }

    /// <summary>
    /// The final blob being blamed (the target file's content at newest_commit).
    /// </summary>
    public GitBlob? FinalBlob { get; set; }

    /// <summary>
    /// The final commit (newest_commit resolved to a Commit object).
    /// </summary>
    public Commit? Final { get; set; }

    /// <summary>
    /// Head of the linked list of blame entries (the scoreboard).
    /// </summary>
    public BlameEntry? Ent { get; set; }

    /// <summary>
    /// Total number of lines in the final blob.
    /// </summary>
    public int NumLines { get; set; }

    /// <summary>
    /// The raw content of the final blob, retained as bytes for line indexing.
    /// </summary>
    public ReadOnlyMemory<byte> FinalBuf { get; set; }

    /// <summary>
    /// Byte offset where each line begins (line_index in C). Length = NumLines + 1;
    /// the last entry points past the end.
    /// </summary>
    public int[] LineIndex { get; set; } = [];

    /// <summary>
    /// Line pointers for <c>git_blame_line_byindex</c>. Each entry is (offset, length).
    /// </summary>
    public (int Offset, int Length)[] Lines { get; set; } = [];

    /// <summary>
    /// Sorted list of hunks (mutable during buffer blame). Sorted by
    /// <see cref="GitBlame.BlameScoreboardHunk.FinalStartLineNumber"/>.
    /// </summary>
    public List<GitBlame.BlameScoreboardHunk> Hunks { get; } = [];

    /// <summary>
    /// Current diff line during buffer blame callbacks.
    /// </summary>
    public int CurrentDiffLine { get; set; }

    /// <summary>
    /// Current hunk during buffer blame callbacks.
    /// </summary>
    public GitBlame.BlameScoreboardHunk? CurrentHunk { get; set; }

    /// <summary>
    /// Builds the line index from the final blob content. Matches
    /// <c>index_blob_lines</c> (<c>blame.c:331-380</c>).
    /// </summary>
    /// <remarks>
    /// Scans the blob byte-by-byte, recording the offset of each line start
    /// (where <c>bol</c> is true). The last entry points past the end. Incomplete
    /// last lines (no trailing newline) are counted.
    /// </remarks>
    public int IndexBlobLines()
    {
        ReadOnlySpan<byte> buf = FinalBuf.Span;
        int len = buf.Length;
        var lineIndex = new List<int>(len / 40 + 1);
        var lines = new List<(int Offset, int Length)>(len / 40 + 1);

        int num = 0;
        bool incomplete = false;
        bool bol = true;
        int lineStart = 0;

        if (len > 0 && buf[len - 1] != '\n')
        {
            incomplete = true; // incomplete line at the end
        }

        for (int i = 0; i < len; i++)
        {
            if (bol)
            {
                lineIndex.Add(i);
                lineStart = i;
                bol = false;
            }

            if (buf[i] == '\n')
            {
                lines.Add((lineStart, i - lineStart));
                num++;
                bol = true;
            }
        }

        lineIndex.Add(len);

        if (!bol)
        {
            // Incomplete last line (no trailing newline).
            lines.Add((lineStart, len - lineStart));
        }

        LineIndex = lineIndex.ToArray();
        Lines = lines.ToArray();
        NumLines = num + (incomplete ? 1 : 0);
        return NumLines;
    }
}
