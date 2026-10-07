// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS;

/// <summary>
/// Flags controlling <see cref="LibGit2CS.Repository.GitRepository.InitExtAsync"/> behavior. Maps 1:1 to
/// libgit2's <c>git_repository_init_flag_t</c>.
/// </summary>
[Flags]
public enum GitRepositoryInitFlags
{
    /// <summary>No flags — create a standard non-bare repo.</summary>
    None = 0,

    /// <summary>Create a bare repository (no working directory). (<c>GIT_REPOSITORY_INIT_BARE = 1</c>)</summary>
    Bare = 1 << 0,

    /// <summary>Error if the repository already exists. (<c>GIT_REPOSITORY_INIT_NO_REINIT = 2</c>)</summary>
    NoReinit = 1 << 1,

    /// <summary>Don't append <c>.git</c> to the path (the path IS the gitdir). (<c>GIT_REPOSITORY_INIT_NO_DOTGIT_DIR = 4</c>)</summary>
    NoDotgitDir = 1 << 2,

    /// <summary>Create the parent directory if it doesn't exist. (<c>GIT_REPOSITORY_INIT_MKDIR = 8</c>)</summary>
    Mkdir = 1 << 3,

    /// <summary>Create all ancestor directories (like <c>mkdir -p</c>). (<c>GIT_REPOSITORY_INIT_MKPATH = 16</c>)</summary>
    Mkpath = 1 << 4,

    /// <summary>Use the external template path. (<c>GIT_REPOSITORY_INIT_EXTERNAL_TEMPLATE = 32</c>)</summary>
    ExternalTemplate = 1 << 5,

    /// <summary>Use a relative gitlink for the worktree path. (<c>GIT_REPOSITORY_INIT_RELATIVE_GITLINK = 64</c>)</summary>
    RelativeGitlink = 1 << 6,
}
