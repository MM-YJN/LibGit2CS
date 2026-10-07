// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Refs;

/// <summary>
/// Flags controlling reference name normalization and validation. Matches
/// libgit2's <c>git_reference_format_t</c> (<c>include/git2/refs.h:663-692</c>).
/// </summary>
[Flags]
public enum GitReferenceFormatFlags
{
    /// <summary>
    /// Default: require at least two slash-separated components (e.g.
    /// <c>refs/heads/master</c>). One-level names like <c>HEAD</c> are rejected.
    /// </summary>
    Normal = 0,

    /// <summary>
    /// Allow one-level names (e.g. <c>HEAD</c>, <c>ORIG_HEAD</c>) — must be all
    /// uppercase and underscores. Matches <c>GIT_REFERENCE_FORMAT_ALLOW_ONELEVEL</c>.
    /// </summary>
    AllowOneLevel = 1,

    /// <summary>
    /// Allow a single <c>*</c> wildcard in the name (refspec pattern).
    /// Matches <c>GIT_REFERENCE_FORMAT_REFSPEC_PATTERN</c>.
    /// </summary>
    RefspecPattern = 2,

    /// <summary>
    /// Allow mixed-case shorthand segments (e.g. <c>HEAD/feature</c>).
    /// Matches <c>GIT_REFERENCE_FORMAT_REFSPEC_SHORTHAND</c>.
    /// </summary>
    RefspecShorthand = 4,

    /// <summary> Apply NFD-&gt;NFC precompose to the ref name before validation. Ports the internal <c>GIT_REFERENCE_FORMAT__PRECOMPOSE_UNICODE</c> flag
    /// (<c>src/libgit2/refs.h:56</c>) which <c>reference_normalize_for_repo</c> (<c>refs.c:201-218</c>) sets when <c>core.precomposeunicode</c> is enabled.
    /// The high bit position (1&lt;&lt;16) matches the libgit2 internal flag range and stays clear of the public bits. </summary>
    PrecomposeUnicode = 1 << 16,
}
