// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.Revwalk;

/// <summary>
/// Revision specification parser. Managed port of libgit2's
/// <c>src/libgit2/revparse.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Resolves revision strings (<c>HEAD~3</c>, <c>master@{1}</c>, <c>:path</c>,
/// <c>HEAD^{commit}</c>, <c>A..B</c>) into <see cref="GitObject"/>s. See
/// <c>git2/revparse.h</c>.
/// </para>
/// </remarks>
public static partial class GitRevParser
{
    /// <summary>
    /// Parses a range expression <c>A..B</c> or <c>A...B</c> (or a single spec).
    /// Matches <c>git_revparse</c> (revparse.c:912-968).
    /// </summary>
    internal static async Task<(GitObject? From, GitObject? To, GitRevSpecFlags Flags)> ParseRangeAsync(GitRepository repo, string spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(spec);

        int dotdot = FindRangeSeparator(spec);

        if (dotdot < 0)
        {
            // Single spec.
            GitObject? obj = await ParseSingleAsync(repo, spec, cancellationToken).ConfigureAwait(false);
            return (obj, null, GitRevSpecFlags.Single);
        }

        // C (revparse.c:926-940): only the bare ".." is rejected — "The empty
        // range '...' is still allowed" and resolves to HEAD...HEAD.
        if (spec == "..")
        {
            throw new GitException(
                GitErrorCode.InvalidSpec,
                "invalid pattern '..'",
                GitErrorCategory.Invalid);
        }

        string lstr = dotdot > 0 ? spec[..dotdot] : "HEAD";

        int afterDots = dotdot + 2;
        GitRevSpecFlags flags = GitRevSpecFlags.Range;
        string rstr;
        if (afterDots < spec.Length && spec[afterDots] == '.')
        {
            // Triple-dot: symmetric difference.
            flags |= GitRevSpecFlags.MergeBase;
            rstr = spec[(afterDots + 1)..];
        }
        else
        {
            rstr = spec[afterDots..];
        }

        if (rstr.Length == 0)
        {
            rstr = "HEAD";
        }

        GitObject? from = await ParseSingleAsync(repo, lstr, cancellationToken).ConfigureAwait(false);
        GitObject? to = await ParseSingleAsync(repo, rstr, cancellationToken).ConfigureAwait(false);

        return (from, to, flags);
    }

    /// <summary>
    /// Finds the index of the <c>..</c> range separator, or -1 if none.
    /// A <c>...</c> triple-dot still returns the index of the first dot.
    /// </summary>
    private static int FindRangeSeparator(string spec)
    {
        // Look for ".." but not at position 0 (need a left operand context).
        // A simple scan is sufficient; revparse uses strstr.
        for (int i = 0; i < spec.Length - 1; i++)
        {
            if (spec[i] == '.' && spec[i + 1] == '.')
            {
                return i;
            }
        }

        return -1;
    }
}
