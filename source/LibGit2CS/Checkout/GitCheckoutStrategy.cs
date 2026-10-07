// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics.CodeAnalysis;

namespace LibGit2CS.Checkout;

/// <summary>
/// Checkout strategy flags. Maps 1:1 to <c>git_checkout_strategy_t</c> in
/// <c>include/git2/checkout.h:14-66</c>.
/// </summary>
/// <remarks>
/// <b>Default is <see cref="Safe"/></b> (value 0), matching libgit2. A default
/// checkout refuses to overwrite modified or untracked files. Use
/// <see cref="Force"/> to allow all updates (potentially data-losing).
/// </remarks>
[SuppressMessage("Design", "CA1008:Enums should have zero value", Justification = "Flag enum with None=0 and Safe=0 mirrors libgit2's git_checkout_strategy_t where GIT_CHECKOUT_SAFE is the zero default. This is a faithful port.")]
[Flags]
public enum GitCheckoutStrategy
{
    /// <summary>
    /// Default safe checkout — refuse to overwrite uncommitted data.
    /// (<c>GIT_CHECKOUT_SAFE = 0</c>)
    /// </summary>
    Safe = 0,

    /// <summary>Allow all updates, even if data-losing. (<c>GIT_CHECKOUT_FORCE = 1u &lt;&lt; 1</c>)</summary>
    Force = 1 << 1,

    /// <summary>Recreate missing files that were removed from the workdir. (<c>GIT_CHECKOUT_RECREATE_MISSING = 1u &lt;&lt; 2</c>)</summary>
    RecreateMissing = 1 << 2,

    /// <summary>Allow safe updates even if conflicts are present. (<c>GIT_CHECKOUT_ALLOW_CONFLICTS = 1u &lt;&lt; 4</c>)</summary>
    AllowConflicts = 1 << 4,

    /// <summary>Remove untracked files from the workdir. (<c>GIT_CHECKOUT_REMOVE_UNTRACKED = 1u &lt;&lt; 5</c>)</summary>
    RemoveUntracked = 1 << 5,

    /// <summary>Remove ignored files from the workdir. (<c>GIT_CHECKOUT_REMOVE_IGNORED = 1u &lt;&lt; 6</c>)</summary>
    RemoveIgnored = 1 << 6,

    /// <summary>Only update existing files; don't create or delete. (<c>GIT_CHECKOUT_UPDATE_ONLY = 1u &lt;&lt; 7</c>)</summary>
    UpdateOnly = 1 << 7,

    /// <summary>Don't write updated entries to the index. (<c>GIT_CHECKOUT_DONT_UPDATE_INDEX = 1u &lt;&lt; 8</c>)</summary>
    DontUpdateIndex = 1 << 8,

    /// <summary>Don't refresh index/config before checkout. (<c>GIT_CHECKOUT_NO_REFRESH = 1u &lt;&lt; 9</c>)</summary>
    NoRefresh = 1 << 9,

    /// <summary>Skip files with unmerged index entries. (<c>GIT_CHECKOUT_SKIP_UNMERGED = 1u &lt;&lt; 10</c>)</summary>
    SkipUnmerged = 1 << 10,

    /// <summary>For unmerged files, use stage 2 (ours). (<c>GIT_CHECKOUT_USE_OURS = 1u &lt;&lt; 11</c>)</summary>
    UseOurs = 1 << 11,

    /// <summary>For unmerged files, use stage 3 (theirs). (<c>GIT_CHECKOUT_USE_THEIRS = 1u &lt;&lt; 12</c>)</summary>
    UseTheirs = 1 << 12,

    /// <summary>Treat the pathspec as an exact file list (no glob matching). (<c>GIT_CHECKOUT_DISABLE_PATHSPEC_MATCH = 1u &lt;&lt; 13</c>)</summary>
    DisablePathSpecMatch = 1 << 13,

    /// <summary>Ignore directories that are in use (locked). (<c>GIT_CHECKOUT_SKIP_LOCKED_DIRECTORIES = 1u &lt;&lt; 18</c>)</summary>
    SkipLockedDirectories = 1 << 18,

    /// <summary>Don't overwrite ignored files. (<c>GIT_CHECKOUT_DONT_OVERWRITE_IGNORED = 1u &lt;&lt; 19</c>)</summary>
    DontOverwriteIgnored = 1 << 19,

    /// <summary>Write <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> merge conflict markers. (<c>GIT_CHECKOUT_CONFLICT_STYLE_MERGE = 1u &lt;&lt; 20</c>)</summary>
    ConflictStyleMerge = 1 << 20,

    /// <summary>Write diff3-style conflict markers (includes ancestor). (<c>GIT_CHECKOUT_CONFLICT_STYLE_DIFF3 = 1u &lt;&lt; 21</c>)</summary>
    ConflictStyleDiff3 = 1 << 21,

    /// <summary>Don't remove files/folders on case-insensitive filesystems. (<c>GIT_CHECKOUT_DONT_REMOVE_EXISTING = 1u &lt;&lt; 22</c>)</summary>
    DontRemoveExisting = 1 << 22,

    /// <summary>Don't write the index on checkout completion. (<c>GIT_CHECKOUT_DONT_WRITE_INDEX = 1u &lt;&lt; 23</c>)</summary>
    DontWriteIndex = 1 << 23,

    /// <summary>Dry-run: determine actions but don't execute. (<c>GIT_CHECKOUT_DRY_RUN = 1u &lt;&lt; 24</c>)</summary>
    DryRun = 1 << 24,

    /// <summary>Write zdiff3-style conflict markers. (<c>GIT_CHECKOUT_CONFLICT_STYLE_ZDIFF3 = 1u &lt;&lt; 25</c>)</summary>
    ConflictStyleZdiff3 = 1 << 25,

    /// <summary>
    /// Skip checkout entirely — don't write any files. (<c>GIT_CHECKOUT_NONE = 1u &lt;&lt; 30</c>).
    /// Used by clone to disable the post-fetch checkout.
    /// </summary>
    None = 1 << 30,
}
