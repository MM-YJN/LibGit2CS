// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

namespace LibGit2CS.Diff;

/// <summary>
/// One contiguous hunk of a diff. Managed equivalent of
/// <c>git_diff_hunk</c> in <c>include/git2/diff.h:607-617</c>.
/// </summary>
/// <param name="OldStart">
/// 1-based starting line of the hunk in the old file. When
/// <paramref name="OldCount"/> is <c>0</c> the value is the line before the
/// insertion point (matches git).
/// </param>
/// <param name="OldCount">Number of old-file lines covered (context + deletions).</param>
/// <param name="NewStart">
/// 1-based starting line of the hunk in the new file. When
/// <paramref name="NewCount"/> is <c>0</c> the value is the line before the
/// deletion point.
/// </param>
/// <param name="NewCount">Number of new-file lines covered (context + additions).</param>
/// <param name="Header">
/// The raw hunk header bytes (e.g. <c>@@ -1,5 +1,5 @@ funcname\n</c>). Ports
/// <c>git_diff_hunk.header</c> (<c>char[GIT_DIFF_HUNK_HEADER_SIZE]</c>): the
/// header is truncated at the last valid UTF-8 boundary and a trailing
/// <c>\n</c> is spliced back, exactly as <c>git_xdiff_cb</c> does
/// (diff_xdiff.c:126-140). At most 128 bytes.
/// </param>
/// <param name="Lines">The context/addition/deletion lines that make up the hunk.</param>
public sealed record GitDiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    ReadOnlyMemory<byte> Header,
    IReadOnlyList<GitDiffLine> Lines)
{
    /// <summary>
    /// The hunk header decoded as UTF-8 (with replacement fallback) for
    /// display. The <see cref="Header"/> bytes are the parity surface; use
    /// this only when a <see cref="string"/> is required.
    /// </summary>
    public string HeaderText => Encoding.UTF8.GetString(Header.Span);
}
