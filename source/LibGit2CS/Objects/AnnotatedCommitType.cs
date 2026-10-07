// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Objects;

/// <summary>
/// The type of an <see cref="GitAnnotatedCommit"/>. Matches
/// <c>git_annotated_commit_type</c> in libgit2.
/// </summary>
internal enum AnnotatedCommitType
{
    /// <summary>
    /// A real commit resolved from ODB/ref/fetchhead. Carries a <see cref="Commit"/>.
    /// </summary>
    Real,

    /// <summary>
    /// A virtual commit produced by recursive merge base computation
    /// (<c>create_virtual_base</c>). Carries a merged <see cref="LibGit2CS.Index.GitIndex"/>
    /// and accumulated parent OIDs.
    /// </summary>
    Virtual,
}
