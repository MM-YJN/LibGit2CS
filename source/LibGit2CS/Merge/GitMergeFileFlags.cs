// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Merge;

/// <summary>
/// Flags controlling file-level merge behavior. Matches
/// <c>git_merge_file_flag_t</c> in <c>include/git2/merge.h</c>.
/// </summary>
[Flags]
public enum GitMergeFileFlags
{
    /// <summary>Defaults: standard merge, no special handling.</summary>
    None = 0,

    /// <summary>
    /// Use standard conflict markers (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> /
    /// <c>=======</c> / <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>). Matches
    /// <c>GIT_MERGE_FILE_STYLE_MERGE = 1&lt;&lt;0</c>.
    /// </summary>
    StyleMerge = 1 << 0,

    /// <summary>
    /// Use diff3-style conflict markers (include ancestor between
    /// <c>|||||||</c> and <c>=======</c>). Matches
    /// <c>GIT_MERGE_FILE_STYLE_DIFF3 = 1&lt;&lt;1</c>.
    /// </summary>
    StyleDiff3 = 1 << 1,

    /// <summary>
    /// Condense non-alphanumeric lines for similarity comparison. Matches
    /// <c>GIT_MERGE_FILE_SIMPLIFY_ALNUM = 1&lt;&lt;2</c>.
    /// </summary>
    SimplifyAlnum = 1 << 2,

    /// <summary>
    /// Ignore all whitespace when comparing lines. Matches
    /// <c>GIT_MERGE_FILE_IGNORE_WHITESPACE = 1&lt;&lt;3</c>.
    /// </summary>
    IgnoreWhitespace = 1 << 3,

    /// <summary>
    /// Ignore changes in whitespace amount when comparing lines. Matches
    /// <c>GIT_MERGE_FILE_IGNORE_WHITESPACE_CHANGE = 1&lt;&lt;4</c>.
    /// </summary>
    IgnoreWhitespaceChange = 1 << 4,

    /// <summary>
    /// Ignore whitespace at end of line when comparing. Matches
    /// <c>GIT_MERGE_FILE_IGNORE_WHITESPACE_EOL = 1&lt;&lt;5</c>.
    /// </summary>
    IgnoreWhitespaceEol = 1 << 5,

    /// <summary>
    /// Use the patience diff algorithm. Matches
    /// <c>GIT_MERGE_FILE_DIFF_PATIENCE = 1&lt;&lt;6</c>.
    /// </summary>
    DiffPatience = 1 << 6,

    /// <summary>
    /// Use the minimal diff algorithm (slower, more minimal). Matches
    /// <c>GIT_MERGE_FILE_DIFF_MINIMAL = 1&lt;&lt;7</c>.
    /// </summary>
    DiffMinimal = 1 << 7,

    /// <summary>
    /// Use zealous diff3-style conflict markers. Matches
    /// <c>GIT_MERGE_FILE_STYLE_ZDIFF3 = 1&lt;&lt;8</c>.
    /// </summary>
    StyleZdiff3 = 1 << 8,

    /// <summary>
    /// Allow conflicts to be recorded in the result (rather than returning an
    /// error). Matches <c>GIT_MERGE_FILE_ACCEPT_CONFLICTS = 1&lt;&lt;9</c>.
    /// Checked by the merge driver, not by the file merger itself.
    /// </summary>
    AcceptConflicts = 1 << 9,
}
