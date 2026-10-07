// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.IO;

namespace LibGit2CS.Diff;

/// <summary> Notification callback invoked once per delta during delta generation (before the iterator advances). Managed equivalent of
/// <c>git_diff_notify_cb</c> in <c>include/git2/diff.h:289-310</c>. </summary> <param name="delta">The delta about to be added.</param> <param
/// name="matchedPathSpec">The matching pathspec, or null if none. Byte-faithful.</param> <returns> A non-zero return value skips the delta (&gt;0) or aborts
/// the diff (&lt;0), matching the C semantics where a positive <c>git_diff_notify_cb</c> return drops the delta. </returns>
public delegate int GitDiffNotificationCallback(GitDiffDelta delta, GitPath? matchedPathSpec);
