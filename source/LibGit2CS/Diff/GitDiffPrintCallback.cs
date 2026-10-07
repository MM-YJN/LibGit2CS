// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Callback for line-by-line diff output. Managed equivalent of
/// <c>git_diff_line_cb</c> in <c>include/git2/diff.h:667</c>.
/// </summary>
/// <param name="delta">The delta being printed, or null for synthetic lines
/// that are not tied to a specific delta (e.g. stat/summary output).</param>
/// <param name="hunk">The current hunk, or null for file-level lines.</param>
/// <param name="line">The line to output.</param>
public delegate void GitDiffPrintCallback(GitDiffDelta? delta, GitDiffHunk? hunk, GitDiffLine line);
