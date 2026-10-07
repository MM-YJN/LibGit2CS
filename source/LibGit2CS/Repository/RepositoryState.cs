// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Repository;

/// <summary>
/// Repository state flags indicating in-progress operations. Matches
/// <c>git_repository_state_t</c> in <c>include/git2/repository.h</c>.
/// </summary>
/// <remarks>
/// Computed by <see cref="LibGit2CS.Repository.GitRepository.State"/> by examining the gitdir for
/// state files and directories (e.g. <c>MERGE_HEAD</c>, <c>rebase-merge/</c>,
/// <c>CHERRY_PICK_HEAD</c>, etc.).
/// </remarks>
public enum RepositoryState
{
    /// <summary>
    /// No operation in progress. (<c>GIT_REPOSITORY_STATE_NONE = 0</c>)
    /// </summary>
    None = 0,

    /// <summary>
    /// A merge is in progress (<c>MERGE_HEAD</c> exists).
    /// (<c>GIT_REPOSITORY_STATE_MERGE = 1</c>)
    /// </summary>
    Merge = 1,

    /// <summary>
    /// A revert is in progress (<c>REVERT_HEAD</c> exists).
    /// (<c>GIT_REPOSITORY_STATE_REVERT = 2</c>)
    /// </summary>
    Revert = 2,

    /// <summary>
    /// A revert sequence is in progress (<c>REVERT_HEAD</c> +
    /// <c>sequencer/todo</c>). (<c>GIT_REPOSITORY_STATE_REVERT_SEQUENCE = 3</c>)
    /// </summary>
    RevertSequence = 3,

    /// <summary>
    /// A cherry-pick is in progress (<c>CHERRY_PICK_HEAD</c> exists).
    /// (<c>GIT_REPOSITORY_STATE_CHERRYPICK = 4</c>)
    /// </summary>
    CherryPick = 4,

    /// <summary>
    /// A cherry-pick sequence is in progress (<c>CHERRY_PICK_HEAD</c> +
    /// <c>sequencer/todo</c>). (<c>GIT_REPOSITORY_STATE_CHERRYPICK_SEQUENCE = 5</c>)
    /// </summary>
    CherryPickSequence = 5,

    /// <summary>
    /// A bisect is in progress (<c>BISECT_LOG</c> exists).
    /// (<c>GIT_REPOSITORY_STATE_BISECT = 6</c>)
    /// </summary>
    Bisect = 6,

    /// <summary>
    /// A patch-application rebase is in progress (<c>rebase-apply/rebasing</c>
    /// exists). (<c>GIT_REPOSITORY_STATE_REBASE = 7</c>)
    /// </summary>
    Rebase = 7,

    /// <summary>
    /// An interactive rebase is in progress (<c>rebase-merge/interactive</c>
    /// exists). (<c>GIT_REPOSITORY_STATE_REBASE_INTERACTIVE = 8</c>)
    /// </summary>
    RebaseInteractive = 8,

    /// <summary>
    /// A rebase merge is in progress (<c>rebase-merge/</c> exists, no
    /// <c>interactive</c> file inside). (<c>GIT_REPOSITORY_STATE_REBASE_MERGE = 9</c>)
    /// </summary>
    RebaseMerge = 9,

    /// <summary>
    /// A mailbox apply is in progress (<c>rebase-apply/applying</c> exists).
    /// (<c>GIT_REPOSITORY_STATE_APPLY_MAILBOX = 10</c>)
    /// </summary>
    ApplyMailbox = 10,

    /// <summary>
    /// An apply-mailbox or rebase is in progress (<c>rebase-apply/</c> exists
    /// without the marker files). (<c>GIT_REPOSITORY_STATE_APPLY_MAILBOX_OR_REBASE = 11</c>)
    /// </summary>
    ApplyMailboxOrRebase = 11,
}
