// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Core;

namespace LibGit2CS.Refs;

/// <summary>
/// A single entry in <c>.git/FETCH_HEAD</c>. Maps to <c>git_fetchhead_ref</c>
/// in <c>src/libgit2/fetchhead.c</c>.
/// </summary>
/// <param name="Oid">The OID of the fetched object.</param>
/// <param name="IsMerge"><c>true</c> if this is a merge candidate (the HEAD's fetch target); <c>false</c> if <c>not-for-merge</c>.</param>
/// <param name="RefName">The ref name (e.g. <c>refs/heads/master</c>), or null for HEAD.</param>
/// <param name="RemoteUrl">The sanitized remote URL (credentials stripped).</param>
public sealed record GitFetchHeadEntry(
    GitOid Oid,
    bool IsMerge,
    string? RefName,
    string? RemoteUrl) : IComparable<GitFetchHeadEntry>
{
    /// <summary>
    /// Sort order: merge entries first, then by ref name alphabetical.
    /// Matches <c>git_fetchhead_ref_cmp</c> in <c>fetchhead.c:20</c>.
    /// </summary>
    public int CompareTo(GitFetchHeadEntry? other)
    {
        if (other is null)
        {
            return 1;
        }

        // Merge entries sort before non-merge
        if (IsMerge != other.IsMerge)
        {
            return IsMerge ? -1 : 1;
        }

        // Then by ref name. C (fetchhead.c:20-27): a NULL ref name sorts
        // LAST.
        if (RefName is not null && other.RefName is not null)
        {
            return StringComparer.Ordinal.Compare(RefName, other.RefName);
        }

        if (RefName is not null)
        {
            return -1;
        }

        if (other.RefName is not null)
        {
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Format this entry as a line in <c>FETCH_HEAD</c>.
    /// Matches <c>fetchhead_ref_write</c> in <c>fetchhead.c:104</c>.
    /// </summary>
    /// <returns>The formatted line (without trailing newline for caller to add).</returns>
    public string Format()
    {
        string mergeFlag = IsMerge ? "" : "not-for-merge";

        // HEAD special case: <oid>\t\t<remote-url>
        if (RefName is "HEAD" or null)
        {
            return $"{Oid}\t\t{RemoteUrl}";
        }

        // Branch: refs/heads/* → "branch '<short>' of <url>"
        if (RefName.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            string shortName = RefName["refs/heads/".Length..];
            return $"{Oid}\t{mergeFlag}\tbranch '{shortName}' of {RemoteUrl}";
        }

        // Tag: refs/tags/* → "tag '<short>' of <url>"
        if (RefName.StartsWith("refs/tags/", StringComparison.Ordinal))
        {
            string shortName = RefName["refs/tags/".Length..];
            return $"{Oid}\t{mergeFlag}\ttag '{shortName}' of {RemoteUrl}";
        }

        // Other: "'<refname>' of <url>"
        return $"{Oid}\t{mergeFlag}\t'{RefName}' of {RemoteUrl}";
    }

    /// <summary>Tests whether the left value sorts before the right value.</summary>
    public static bool operator <(GitFetchHeadEntry? left, GitFetchHeadEntry? right)
    {
        return left is null ? right is not null : left.CompareTo(right) < 0;
    }

    /// <summary>Tests whether the left value sorts before or equal to the right value.</summary>
    public static bool operator <=(GitFetchHeadEntry? left, GitFetchHeadEntry? right)
    {
        return left is null || left.CompareTo(right) <= 0;
    }

    /// <summary>Tests whether the left value sorts after the right value.</summary>
    public static bool operator >(GitFetchHeadEntry? left, GitFetchHeadEntry? right)
    {
        return left is not null && left.CompareTo(right) > 0;
    }

    /// <summary>Tests whether the left value sorts after or equal to the right value.</summary>
    public static bool operator >=(GitFetchHeadEntry? left, GitFetchHeadEntry? right)
    {
        return left is null ? right is null : left.CompareTo(right) >= 0;
    }
}
