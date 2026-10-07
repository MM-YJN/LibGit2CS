// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Globalization;

using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Utils;

namespace LibGit2CS.Merge;

/// <summary>
/// Builds the <c>MERGE_MSG</c> commit message from the list of annotated
/// merge heads. Managed port of <c>write_merge_msg</c> (merge.c:2727-2842)
/// and its supporting classifiers/formatters (merge.c:2540-2725).
/// </summary>
/// <remarks>
/// <para>
/// The message format emulates core git's <c>MERGE_MSG</c> formatting:
/// <c>"Merge branch 'feature'"</c>, <c>"Merge remote-tracking branch 'origin/main'"</c>,
/// <c>"Merge tag 'v1.0'"</c>, <c>"Merge commit 'abc123'"</c>, or combinations.
/// </para>
/// <para>
/// The algorithm has 6 phases (merge.c:2759-2826):
/// <list type="number">
/// <item>OID-only commits (until first non-OID head), written as <c>commit 'oid'</c>.</item>
/// <item>Branches (<c>refs/heads/</c>), stripped prefix.</item>
/// <item>Remote-tracking branches (<c>refs/remotes/</c>), full ref path.</item>
/// <item>Tags (<c>refs/tags/</c>), stripped prefix.</item>
/// <item>Remote branches (grouped by remote URL), stripped prefix + <c>" of URL"</c>.</item>
/// <item>Remaining OID commits (not yet written), as <c>"; commit 'oid'"</c>.</item>
/// </list>
/// Each phase uses a separator character: <c>';'</c> initially, then <c>','</c>
/// after the first non-empty phase. For 3+ items, Oxford comma (<c>" and "</c>).
/// </para>
/// </remarks>
internal static class MergeMessage
{
    /// <summary>
    /// Builds the full merge message string from the given heads.
    /// Matches <c>write_merge_msg</c> (merge.c:2727-2842).
    /// </summary>
    /// <param name="heads">The annotated commits being merged (their side).</param>
    /// <returns>The message string, e.g. <c>"Merge branch 'feature'\n"</c>.</returns>
    public static string Build(IReadOnlyList<GitAnnotatedCommit> heads)
    {
        ArgumentNullException.ThrowIfNull(heads);
        if (heads.Count == 0)
        {
            return "Merge \n";
        }

        var entries = new MergeMsgEntry[heads.Count];
        for (int i = 0; i < heads.Count; i++)
        {
            entries[i] = new MergeMsgEntry(heads[i]);
        }

        const int InitialCapacity = 512;
        var sb = new ValueStringBuilder(InitialCapacity); // `using` is not added because we need to pass the builder via ref and ToString also implicitly disposes the builder

        sb.Append("Merge ");

        char sep = '\0';

        // Phase 1: OID-only commits until the first non-OID head.
        // Core git writes all OID-specified commits in order until the first
        // named branch/tag is reached. (merge.c:2770-2780)
        int iPhase1;
        for (iPhase1 = 0; iPhase1 < entries.Length; iPhase1++)
        {
            if (!IsOid(entries[iPhase1]))
            {
                break;
            }

            if (iPhase1 > 0)
            {
                sb.Append("; ");
            }

            sb.Append("commit '");
            sb.AppendSpanFormattable(entries[iPhase1].Head.Id, provider: CultureInfo.InvariantCulture);
            sb.Append('\'');

            entries[iPhase1].Written = true;
        }

        if (iPhase1 > 0)
        {
            sep = ';';
        }

        // Phase 2: Branches (refs/heads/).
        List<MergeMsgEntry> matching = Collect(entries, IsBranch);
        if (matching.Count > 0)
        {
            WriteEntries(ref sb, matching, "branch", "branches", GitReferences.RefsHeadsDir.Length, null, sep);
            sep = ',';
        }

        // Phase 3: Remote-tracking branches (refs/remotes/).
        matching = Collect(entries, IsTracking);
        if (matching.Count > 0)
        {
            WriteEntries(ref sb, matching, "remote-tracking branch", "remote-tracking branches", 0, null, sep);
            sep = ',';
        }

        // Phase 4: Tags (refs/tags/).
        matching = Collect(entries, IsTag);
        if (matching.Count > 0)
        {
            WriteEntries(ref sb, matching, "tag", "tags", GitReferences.RefsTagsDir.Length, null, sep);
            sep = ',';
        }

        // Phase 5: Remote branches (has remote_url + refs/heads/), grouped by remote.
        // We should never be called with multiple remote branches, but handle it.
        // (merge.c:2806-2814 — while loop)
        while (true)
        {
            matching = CollectRemote(entries);
            if (matching.Count == 0)
            {
                break;
            }

            string? source = matching[0].Head.RemoteUrl;
            WriteEntries(ref sb, matching, "branch", "branches", GitReferences.RefsHeadsDir.Length, source, sep);
            sep = ',';
        }

        // Phase 6: Remaining OID commits (not yet written).
        // (merge.c:2819-2826)
        foreach (MergeMsgEntry entry in entries)
        {
            if (entry.Written)
            {
                continue;
            }

            sb.Append("; commit '");
            sb.AppendSpanFormattable(entry.Head.Id, provider: CultureInfo.InvariantCulture);
            sb.Append('\'');
        }

        sb.Append('\n');
        return sb.ToString();
    }

    // ── Phase helper: collect entries matching a predicate ─────────────

    private static List<MergeMsgEntry> Collect(
        MergeMsgEntry[] entries,
        Func<MergeMsgEntry, bool> match)
    {
        var result = new List<MergeMsgEntry>();
        foreach (MergeMsgEntry entry in entries)
        {
            if (match(entry))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    // ── Phase 5 remote grouping ─────────────────────────────────────────

    private static List<MergeMsgEntry> CollectRemote(MergeMsgEntry[] entries)
    {
        var result = new List<MergeMsgEntry>();
        string? remoteUrl = null;

        foreach (MergeMsgEntry entry in entries)
        {
            if (!IsRemote(entry, remoteUrl, result.Count == 0))
            {
                continue;
            }

            if (result.Count == 0)
            {
                remoteUrl = entry.Head.RemoteUrl;
            }

            result.Add(entry);
        }

        return result;
    }

    // ── Write entries for one phase ─────────────────────────────────────

    private static void WriteEntries(
        ref ValueStringBuilder sb,
        List<MergeMsgEntry> entries,
        string itemName,
        string itemPluralName,
        int refNameSkip,
        string? source,
        char sep)
    {
        if (entries.Count == 0)
        {
            return;
        }

        if (sep != '\0')
        {
            sb.Append(sep);
            sb.Append(' ');
        }

        sb.Append(entries.Count == 1 ? itemName : itemPluralName);
        sb.Append(' ');

        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(i == entries.Count - 1 ? " and " : ", ");
            }

            string? refName = entries[i].Head.Ref;
            sb.Append('\'');
            if (refName != null && refName.Length > refNameSkip)
            {
                sb.Append(refName[refNameSkip..]);
            }
            else if (refName is not null)
            {
                sb.Append(refName);
            }
            else
            {
                sb.AppendSpanFormattable(entries[i].Head.Id, provider: CultureInfo.InvariantCulture);
            }
            sb.Append('\'');

            entries[i].Written = true;
        }

        if (source != null)
        {
            sb.Append(" of ");
            sb.Append(source);
        }
    }

    // ── Classifiers (merge.c:2545-2611) ─────────────────────────────────

    private static bool IsBranch(MergeMsgEntry entry)
    {
        return !entry.Written
            && entry.Head.RemoteUrl == null
            && entry.Head.Ref != null
            && entry.Head.Ref.StartsWith(GitReferences.RefsHeadsDir, StringComparison.Ordinal);
    }

    private static bool IsTracking(MergeMsgEntry entry)
    {
        return !entry.Written
            && entry.Head.RemoteUrl == null
            && entry.Head.Ref != null
            && entry.Head.Ref.StartsWith(GitReferences.RefsRemotesDir, StringComparison.Ordinal);
    }

    private static bool IsTag(MergeMsgEntry entry)
    {
        return !entry.Written
            && entry.Head.RemoteUrl == null
            && entry.Head.Ref != null
            && entry.Head.Ref.StartsWith(GitReferences.RefsTagsDir, StringComparison.Ordinal);
    }

    private static bool IsRemote(MergeMsgEntry entry, string? expectedRemoteUrl, bool isFirst)
    {
        if (entry.Written)
        {
            return false;
        }

        if (entry.Head.RemoteUrl == null || entry.Head.Ref == null)
        {
            return false;
        }

        if (!entry.Head.Ref.StartsWith(GitReferences.RefsHeadsDir, StringComparison.Ordinal))
        {
            return false;
        }

        // First entry always matches; subsequent must share the same remote URL.
        // (merge.c:2593-2599)
        if (isFirst)
        {
            return true;
        }

        return string.Equals(expectedRemoteUrl, entry.Head.RemoteUrl, StringComparison.Ordinal);
    }

    private static bool IsOid(MergeMsgEntry entry)
    {
        return !entry.Written
            && entry.Head.Ref == null
            && entry.Head.RemoteUrl == null;
    }

    /// <summary>
    /// Internal helper for merge message classification. Tracks whether an
    /// annotated commit head has already been written to the message.
    /// Matches <c>struct merge_msg_entry</c> (merge.c:2540-2543).
    /// </summary>
    internal sealed class MergeMsgEntry
    {
        public GitAnnotatedCommit Head { get; }
        public bool Written { get; set; }

        public MergeMsgEntry(GitAnnotatedCommit head)
        {
            Head = head;
        }
    }
}

