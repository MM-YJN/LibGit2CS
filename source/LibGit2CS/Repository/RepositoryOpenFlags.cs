// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Repository;

/// <summary>
/// Flags controlling <see cref="LibGit2CS.Repository.GitRepository.OpenExtAsync"/> behavior. Maps to
/// <c>git_repository_open_flag_t</c>.
/// </summary>
[Flags]
public enum RepositoryOpenFlags
{
    /// <summary>No flags — search upward for <c>.git</c>.</summary>
    None = 0,

    /// <summary>Don't search upward; <c>path</c> must be the gitdir. (<c>GIT_REPOSITORY_OPEN_NO_SEARCH = 1</c>)</summary>
    NoSearch = 1,

    /// <summary>Search across filesystem boundaries. (<c>GIT_REPOSITORY_OPEN_CROSS_FS = 2</c>)</summary>
    CrossFs = 2,

    /// <summary>Open as bare (no working directory). (<c>GIT_REPOSITORY_OPEN_BARE = 4</c>)</summary>
    Bare = 4,

    /// <summary>Don't append <c>.git</c> to the path. (<c>GIT_REPOSITORY_OPEN_NO_DOTGIT = 8</c>)</summary>
    NoDotgit = 8,

    /// <summary>
    /// Honor the <c>GIT_DIR</c>/<c>GIT_COMMON_DIR</c>/<c>GIT_WORK_TREE</c>/
    /// <c>GIT_NAMESPACE</c>/<c>GIT_CEILING_DIRECTORIES</c>/
    /// <c>GIT_DISCOVERY_ACROSS_FILESYSTEM</c>/<c>GIT_OBJECT_DIRECTORY</c>/
    /// <c>GIT_INDEX_FILE</c> environment variables when opening.
    /// (<c>GIT_REPOSITORY_OPEN_FROM_ENV = (1 &lt;&lt; 4) = 16</c>)
    /// </summary>
    FromEnv = 16,
}
