// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.IO;

namespace LibGit2CS.Status;

/// <summary>
/// A single parsed <c>.gitignore</c> rule. Managed equivalent of
/// <c>git_attr_fnmatch</c> in the ignore context (where the rule's only
/// "value" is whether it matches and whether it is negated). Simpler than
/// <c>AttrRule</c> — no assignment dictionary.
/// </summary>
/// <remarks>
/// The C <c>ignore.c</c> stores bare <c>git_attr_fnmatch*</c> pointers in
/// <c>git_attr_file.rules</c> (not <c>git_attr_rule*</c>); the
/// <c>GIT_ATTR_FNMATCH_NEGATIVE</c> flag carries the ignore/unignore
/// distinction. This type wraps an <see cref="FnMatchPattern"/> to make
/// the intent explicit at the C# call site.
/// </remarks>
internal sealed class IgnoreRule
{
    /// <summary>The parsed glob pattern.</summary>
    public FnMatchPattern Pattern { get; }

    /// <summary>True if this is a <c>!</c>-negated (un-ignore) rule.</summary>
    public bool IsNegative => (Pattern.Flags & FnMatchPattern.Flag.Negative) != 0;

    /// <summary>True if the rule is a wildcard pattern (contains <c>*</c>/<c>?</c>/<c>[</c>).</summary>
    public bool HasWild => (Pattern.Flags & FnMatchPattern.Flag.HasWild) != 0;

    public IgnoreRule(FnMatchPattern pattern) => Pattern = pattern;

    /// <summary>
    /// Tests this rule against a path. Equivalent to
    /// <c>git_attr_fnmatch__match</c> with the ignore-context directory
    /// semantics. Returns true if the pattern matches the path.
    /// </summary>
    public bool Match(AttrPath path) => Pattern.Match(path);
}
