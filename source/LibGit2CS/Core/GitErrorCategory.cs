// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary>
/// Error categories reflecting the area of the code where an error occurred.
/// Maps 1:1 to <c>git_error_t</c> in <c>include/git2/errors.h</c>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "'Object' matches libgit2's GIT_ERROR_OBJECT identifier.")]
public enum GitErrorCategory
{
    /// <summary>No error. (<c>GIT_ERROR_NONE = 0</c>)</summary>
    None = 0,

    /// <summary>Out of memory. (<c>GIT_ERROR_NOMEMORY = 1</c>)</summary>
    NoMemory = 1,

    /// <summary>OS-level error (errno). (<c>GIT_ERROR_OS = 2</c>)</summary>
    Os = 2,

    /// <summary>Invalid input or operation. (<c>GIT_ERROR_INVALID = 3</c>)</summary>
    Invalid = 3,

    /// <summary>Reference error. (<c>GIT_ERROR_REFERENCE = 4</c>)</summary>
    Reference = 4,

    /// <summary>Zlib error. (<c>GIT_ERROR_ZLIB = 5</c>)</summary>
    Zlib = 5,

    /// <summary>Repository error. (<c>GIT_ERROR_REPOSITORY = 6</c>)</summary>
    Repository = 6,

    /// <summary>Config error. (<c>GIT_ERROR_CONFIG = 7</c>)</summary>
    Config = 7,

    /// <summary>Regex error. (<c>GIT_ERROR_REGEX = 8</c>)</summary>
    Regex = 8,

    /// <summary>Object database error. (<c>GIT_ERROR_ODB = 9</c>)</summary>
    Odb = 9,

    /// <summary>Index error. (<c>GIT_ERROR_INDEX = 10</c>)</summary>
    Index = 10,

    /// <summary>Object error. (<c>GIT_ERROR_OBJECT = 11</c>)</summary>
    Object = 11,

    /// <summary>Network error. (<c>GIT_ERROR_NET = 12</c>)</summary>
    Net = 12,

    /// <summary>Tag error. (<c>GIT_ERROR_TAG = 13</c>)</summary>
    Tag = 13,

    /// <summary>Tree error. (<c>GIT_ERROR_TREE = 14</c>)</summary>
    Tree = 14,

    /// <summary>Indexer error. (<c>GIT_ERROR_INDEXER = 15</c>)</summary>
    Indexer = 15,

    /// <summary>SSL error. (<c>GIT_ERROR_SSL = 16</c>)</summary>
    Ssl = 16,

    /// <summary>Submodule error. (<c>GIT_ERROR_SUBMODULE = 17</c>)</summary>
    Submodule = 17,

    /// <summary>Thread error. (<c>GIT_ERROR_THREAD = 18</c>)</summary>
    Thread = 18,

    /// <summary>Stash error. (<c>GIT_ERROR_STASH = 19</c>)</summary>
    Stash = 19,

    /// <summary>Checkout error. (<c>GIT_ERROR_CHECKOUT = 20</c>)</summary>
    Checkout = 20,

    /// <summary>Fetch head error. (<c>GIT_ERROR_FETCHHEAD = 21</c>)</summary>
    FetchHead = 21,

    /// <summary>Merge error. (<c>GIT_ERROR_MERGE = 22</c>)</summary>
    Merge = 22,

    /// <summary>SSH error. (<c>GIT_ERROR_SSH = 23</c>)</summary>
    Ssh = 23,

    /// <summary>Filter error. (<c>GIT_ERROR_FILTER = 24</c>)</summary>
    Filter = 24,

    /// <summary>Revert error. (<c>GIT_ERROR_REVERT = 25</c>)</summary>
    Revert = 25,

    /// <summary>Callback error. (<c>GIT_ERROR_CALLBACK = 26</c>)</summary>
    Callback = 26,

    /// <summary>Cherry-pick error. (<c>GIT_ERROR_CHERRYPICK = 27</c>)</summary>
    CherryPick = 27,

    /// <summary>Describe error. (<c>GIT_ERROR_DESCRIBE = 28</c>)</summary>
    Describe = 28,

    /// <summary>Rebase error. (<c>GIT_ERROR_REBASE = 29</c>)</summary>
    Rebase = 29,

    /// <summary>Filesystem error. (<c>GIT_ERROR_FILESYSTEM = 30</c>)</summary>
    Filesystem = 30,

    /// <summary>Patch error. (<c>GIT_ERROR_PATCH = 31</c>)</summary>
    Patch = 31,

    /// <summary>Worktree error. (<c>GIT_ERROR_WORKTREE = 32</c>)</summary>
    Worktree = 32,

    /// <summary>SHA/hash error. (<c>GIT_ERROR_SHA = 33</c>)</summary>
    Sha = 33,

    /// <summary>HTTP error. (<c>GIT_ERROR_HTTP = 34</c>)</summary>
    Http = 34,

    /// <summary>Internal error. (<c>GIT_ERROR_INTERNAL = 35</c>)</summary>
    Internal = 35,

    /// <summary>Grafts error. (<c>GIT_ERROR_GRAFTS = 36</c>)</summary>
    Grafts = 36,
}
