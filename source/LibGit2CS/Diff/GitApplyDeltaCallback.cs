// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Per-delta callback. Returns &lt;0 to abort, &gt;0 to skip this delta, 0 to proceed.
/// Maps to <c>git_apply_delta_cb</c> (<c>include/git2/apply.h:41-43</c>).
/// </summary>
public delegate int GitApplyDeltaCallback(GitDiffDelta delta);
