// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Diff;

/// <summary>
/// Flags controlling email (<c>git format-patch</c>) output. Maps 1:1 to
/// <c>git_email_create_flags_t</c> (<c>include/git2/email.h:24-39</c>).
/// </summary>
[Flags]
public enum GitEmailCreateFlags
{
    /// <summary>Normal behavior. (<c>GIT_EMAIL_CREATE_DEFAULT</c>)</summary>
    Default = 0,

    /// <summary>Never show patch numbers in Subject prefix. (<c>GIT_EMAIL_CREATE_OMIT_NUMBERS</c>; <c>1u &lt;&lt; 0</c>)</summary>
    OmitNumbers = 1 << 0,

    /// <summary>Show <c>[PATCH 1/1]</c> even for single-commit patches. (<c>GIT_EMAIL_CREATE_ALWAYS_NUMBER</c>; <c>1u &lt;&lt; 1</c>)</summary>
    AlwaysNumber = 1 << 1,

    /// <summary>Skip rename/similarity detection. (<c>GIT_EMAIL_CREATE_NO_RENAMES</c>; <c>1u &lt;&lt; 2</c>)</summary>
    NoRenames = 1 << 2,
}
