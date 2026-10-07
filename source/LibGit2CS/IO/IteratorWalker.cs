// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Runtime.CompilerServices;

using LibGit2CS.Index;

namespace LibGit2CS.IO;

/// <summary>
/// Lock-step multi-iterator walk. Managed port of <c>git_iterator_walk</c>
/// in <c>src/libgit2/iterator.c</c> (lines 2377-2462).
/// </summary>
/// <remarks>
/// <para>
/// Walks multiple iterators simultaneously in lock-step: at each step, finds
/// the alphabetically-first entry across all iterators, collects all
/// iterators that have an entry at that path, and presents them together to
/// the callback. Iterators that don't have an entry at the current path
/// present <c>null</c> in their slot.
/// </para>
/// <para>
/// This is the core mechanism that powers diff: the diff generator walks a
/// tree iterator and a workdir iterator (or tree+index) in lock-step,
/// comparing entries at each path to detect additions, deletions, and
/// modifications.
/// </para>
/// </remarks>
internal static class IteratorWalker
{
    /// <summary>
    /// Walks multiple iterators in lock-step. Matches <c>git_iterator_walk</c>.
    /// </summary>
    /// <param name="iterators">The iterators to walk (2 or more).</param>
    /// <param name="callback">
    /// Called for each unique path. <c>entries[i]</c> is the current entry
    /// from <c>iterators[i]</c>, or <c>null</c> if that iterator has no
    /// entry at the current path. Return false to stop the walk.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async ValueTask WalkAsync(
        IIterator[] iterators,
        Func<GitIndexEntry?[], CancellationToken, ValueTask<bool>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(iterators);
        ArgumentNullException.ThrowIfNull(callback);

        if (iterators.Length == 0)
        {
            return;
        }

        int count = iterators.Length;
        var items = new GitIndexEntry?[count];
        var curItems = new GitIndexEntry?[count];

        // Initialize: get the current entry from each iterator.
        for (int i = 0; i < count; i++)
        {
            items[i] = await iterators[i].CurrentAsync(cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            // Clear current items.
            Array.Clear(curItems);

            // Find the alphabetically-first entry across all iterators.
            GitPath? firstPath = null;

            for (int i = 0; i < count; i++)
            {
                if (items[i] is null)
                {
                    continue;
                }

                GitPath path = items[i]!.Value.Path;

                if (firstPath is null)
                {
                    firstPath = path;
                    curItems[i] = items[i];
                }
                else
                {
                    int cmp = ComparePaths(path, firstPath.Value);

                    if (cmp < 0)
                    {
                        // This iterator has an earlier entry — reset all.
                        Array.Clear(curItems);
                        firstPath = path;
                        curItems[i] = items[i];
                    }
                    else if (cmp == 0)
                    {
                        // Same path — add to current set.
                        curItems[i] = items[i];
                    }
                    // else: this iterator's entry sorts after firstPath, skip.
                }
            }

            // All iterators exhausted?
            if (firstPath is null)
            {
                break;
            }

            // Callback with the current set.
            if (!await callback(curItems, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            // Advance each iterator that participated.
            for (int i = 0; i < count; i++)
            {
                if (curItems[i] is not null)
                {
                    items[i] = await iterators[i].AdvanceAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Simple foreach wrapper for a single iterator. Matches
    /// <c>git_iterator_foreach</c>.
    /// </summary>
    public static async IAsyncEnumerable<GitIndexEntry> ForEachAsync(
        IIterator iterator,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(iterator);
        while (await iterator.AdvanceAsync(cancellationToken).ConfigureAwait(false) is { } entry)
        {
            yield return entry;
        }
    }

    /// <summary>
    /// Compares two iterator entry paths. Directory entries have a trailing
    /// <c>/</c> which affects sort order. Uses case-sensitive comparison
    /// (matching the C <c>git_index_entry_cmp</c> used by
    /// <c>git_iterator_walk</c>).
    /// </summary>
    private static int ComparePaths(GitPath a, GitPath b)
    {
        return GitPath.Compare(a, b);
    }
}
