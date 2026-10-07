// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Rebase;

/// <summary>
/// The type of a rebase operation. Matches <c>git_rebase_operation_t</c>
/// in <c>include/git2/rebase.h:119-155</c>.
/// </summary>
/// <remarks>
/// Only <see cref="Pick"/> is ever produced by libgit2's rebase implementation.
/// The other values exist for API completeness / future interactive rebase
/// support (which currently returns "not supported").
/// </remarks>
public enum GitRebaseOperationType
{
    /// <summary>
    /// Cherry-pick the commit. (<c>GIT_REBASE_OPERATION_PICK = 0</c>)
    /// </summary>
    Pick = 0,

    /// <summary>
    /// Cherry-pick, but prompt for an updated commit message.
    /// (<c>GIT_REBASE_OPERATION_REWORD = 1</c>)
    /// </summary>
    Reword = 1,

    /// <summary>
    /// Cherry-pick, but stop to allow the user to edit changes before committing.
    /// (<c>GIT_REBASE_OPERATION_EDIT = 2</c>)
    /// </summary>
    Edit = 2,

    /// <summary>
    /// Squash into the previous commit; merge the commit messages.
    /// (<c>GIT_REBASE_OPERATION_SQUASH = 3</c>)
    /// </summary>
    Squash = 3,

    /// <summary>
    /// Squash into the previous commit; discard this commit's message.
    /// (<c>GIT_REBASE_OPERATION_FIXUP = 4</c>)
    /// </summary>
    Fixup = 4,

    /// <summary>
    /// Run the given command (no commit cherry-picked).
    /// (<c>GIT_REBASE_OPERATION_EXEC = 5</c>)
    /// </summary>
    Exec = 5,
}
