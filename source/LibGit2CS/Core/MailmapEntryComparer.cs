// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary>
/// Comparer for <see cref="MailmapEntry"/> that sorts by replace_email first,
/// then replace_name (null sorts before non-null). Matches
/// <c>mailmap_entry_cmp</c> (mailmap.c:42-60) — byte-wise ordinal compares
/// (C's <c>git__strcmp</c> over the raw <c>char *</c> bytes).
/// </summary>
internal sealed class MailmapEntryComparer : IComparer<MailmapEntry>
{
    public static readonly MailmapEntryComparer Instance = new();

    public int Compare(MailmapEntry? a, MailmapEntry? b)
    {
        if (a is null && b is null)
        {
            return 0;
        }

        if (a is null)
        {
            return -1;
        }

        if (b is null)
        {
            return 1;
        }

        int cmp = a.ReplaceEmailBytes.Span.SequenceCompareTo(b.ReplaceEmailBytes.Span);
        if (cmp != 0)
        {
            return cmp;
        }

        // NULL replace_names are less than non-NULL ones.
        if (a.ReplaceNameBytes is null || b.ReplaceNameBytes is null)
        {
            return (a.ReplaceNameBytes is not null ? 1 : 0) - (b.ReplaceNameBytes is not null ? 1 : 0);
        }

        return a.ReplaceNameBytes.GetValueOrDefault().Span.SequenceCompareTo(b.ReplaceNameBytes.GetValueOrDefault().Span);
    }
}
