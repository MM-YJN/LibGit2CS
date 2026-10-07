// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

/*
 * An array-of-pointers implementation of Python's Timsort
 * Based on code by Christopher Swenson under the MIT license
 *
 * Copyright (c) 2010 Christopher Swenson
 * Copyright (c) 2011 Vicent Marti
 */
// The underlying MIT permission text is in LICENSES/tsort-MIT.txt.

namespace LibGit2CS.Core;

/// <summary>
/// Stable adaptive mergesort (Timsort). Managed port of libgit2's
/// <c>src/util/tsort.c</c> (<c>git__tsort_r</c>).
/// </summary>
/// <remarks>
/// <para>
/// An array-of-pointers implementation of Python's Timsort, based on code by
/// Christopher Swenson under the MIT license. This is the stable sort that
/// powers <c>git_vector_sort</c> throughout libgit2 (diff delta ordering, index
/// entry sorting, status pairing, describe match ordering, etc.).
/// </para>
/// <para>
/// <b>Stability</b> is critical for byte-exact output: .NET's
/// <c>List&lt;T&gt;.Sort</c> uses introsort (unstable). Using this port ensures
/// equal-keyed elements preserve their original relative order, matching git's
/// behavior exactly.
/// </para>
/// <para>
/// The algorithm detects natural runs (ascending or descending subsequences),
/// reverses descending runs in-place, extends short runs to <c>minrun</c> via
/// binary insertion sort, then merges adjacent runs on a stack while maintaining
/// the invariant <c>A &gt; B + C</c> and <c>B &gt; C</c>.
/// </para>
/// </remarks>
internal static class TimSort
{
    private const int MaxRunStack = 128;

    /// <summary>
    /// Sorts <paramref name="list"/> in-place using a stable Timsort.
    /// </summary>
    /// <param name="list">The list to sort.</param>
    /// <param name="comparison">The comparison delegate.</param>
    public static void Sort<T>(IList<T> list, Comparison<T> comparison)
    {
        int size = list.Count;
        if (size < 2)
        {
            return;
        }

        if (size < 64)
        {
            BiSort(list, 0, 1, size, comparison);
            return;
        }

        int minrun = ComputeMinrun(size);
        var runStack = new RunEntry[MaxRunStack];
        int stackCurr = 0;
        int curr = 0;
        T[] storage = Array.Empty<T>();
        int storageLen = 0;

        // Helper to push the next run onto the stack.
        int PushNext()
        {
            int len = CountRun(list, curr, size, comparison);
            int run = minrun;
            if (run > size - curr)
            {
                run = size - curr;
            }

            if (run > len)
            {
                BiSort(list, curr, curr + len, curr + run, comparison);
                len = run;
            }

            runStack[stackCurr] = new RunEntry(curr, len);
            stackCurr++;
            curr += len;

            if (curr == size)
            {
                // Finish up: merge all remaining runs.
                while (stackCurr > 1)
                {
                    Merge(list, runStack, stackCurr, comparison, ref storage, ref storageLen);
                    runStack[stackCurr - 2] = runStack[stackCurr - 2] with { Length = runStack[stackCurr - 2].Length + runStack[stackCurr - 1].Length };

                    stackCurr--;
                }

                return -1;
            }

            return 0;
        }

        // Push the first three runs.
        for (int i = 0; i < 3; i++)
        {
            if (PushNext() < 0)
            {
                return;
            }
        }

        while (true)
        {
            if (!CheckInvariant(runStack, stackCurr))
            {
                stackCurr = Collapse(list, runStack, stackCurr, size, comparison, ref storage, ref storageLen);
                continue;
            }

            if (PushNext() < 0)
            {
                return;
            }
        }
    }

    private readonly record struct RunEntry(int Start, int Length);

    /// <summary>
    /// Binary search for the insertion point of <paramref name="x"/> within
    /// <paramref name="list"/>[<paramref name="lo"/>, <paramref name="lo"/> +
    /// <paramref name="size"/>). Matches <c>binsearch</c> operating on the
    /// run subarray base: the C code passes <c>&amp;dst[curr]</c> as the array
    /// base, so the search range is relative to the run start, i.e. absolute
    /// <c>[curr .. curr+size-1]</c>.
    /// </summary>
    private static int BinarySearch<T>(IList<T> list, T x, int lo, int size, Comparison<T> comparison)
    {
        int l = 0;
        int r = size - 1;
        int c = r >> 1;
        T? lx = list[lo + l];

        if (comparison(x, lx) < 0)
        {
            return lo;
        }

        if (comparison(x, lx) == 0)
        {
            int i = 1;
            while (i < size && comparison(x, list[lo + i]) == 0)
            {
                i++;
            }

            return lo + i;
        }

        T? cx = list[lo + c];
        while (true)
        {
            int val = comparison(x, cx);
            if (val < 0)
            {
                if (c - l <= 1)
                {
                    return lo + c;
                }

                r = c;
            }
            else if (val > 0)
            {
                if (r - c <= 1)
                {
                    return lo + c + 1;
                }

                l = c;
            }
            else
            {
                do
                {
                    c++;
                    cx = list[lo + c];
                }
                while (comparison(x, cx) == 0);

                return lo + c;
            }

            c = l + ((r - l) >> 1);
            cx = list[lo + c];
        }
    }

    /// <summary>
    /// Binary insertion sort over <paramref name="list"/>[
    /// <paramref name="start"/>, <paramref name="end"/>), knowing that
    /// <paramref name="list"/>[<paramref name="baseIndex"/>, <paramref name="start"/>)
    /// is already sorted. Matches <c>bisort</c>: C passes the run base
    /// (<c>&amp;dst[curr]</c>) plus relative <c>start</c>/<c>size</c>, so the
    /// binary search inside must be relative to the run base, not to index 0.
    /// </summary>
    private static void BiSort<T>(IList<T> list, int baseIndex, int start, int end, Comparison<T> comparison)
    {
        for (int i = start; i < end; i++)
        {
            // If this entry is already correct, just move along.
            if (comparison(list[i - 1], list[i]) <= 0)
            {
                continue;
            }

            // Find the right place, shift everything over, and squeeze in.
            T? x = list[i];
            int location = BinarySearch(list, x, baseIndex, i - baseIndex, comparison);
            for (int j = i - 1; j >= location; j--)
            {
                list[j + 1] = list[j];
            }

            list[location] = x;
        }
    }

    private static void ReverseElements<T>(IList<T> list, int start, int end)
    {
        while (start < end)
        {
            (list[end], list[start]) = (list[start], list[end]);
            start++;
            end--;
        }
    }

    /// <summary>
    /// Counts the length of a run starting at <paramref name="start"/>.
    /// Descending runs are reversed in-place. Matches <c>count_run</c>.
    /// </summary>
    private static int CountRun<T>(IList<T> list, int start, int size, Comparison<T> comparison)
    {
        int curr = start + 2;

        if (size - start == 1)
        {
            return 1;
        }

        if (start >= size - 2)
        {
            if (comparison(list[size - 2], list[size - 1]) > 0)
            {
                (list[size - 1], list[size - 2]) = (list[size - 2], list[size - 1]);
            }

            return 2;
        }

        if (comparison(list[start], list[start + 1]) <= 0)
        {
            while (curr < size - 1 && comparison(list[curr - 1], list[curr]) <= 0)
            {
                curr++;
            }

            return curr - start;
        }

        while (curr < size - 1 && comparison(list[curr - 1], list[curr]) > 0)
        {
            curr++;
        }

        ReverseElements(list, start, curr - 1);
        return curr - start;
    }

    private static int ComputeMinrun(int n)
    {
        int r = 0;
        while (n >= 64)
        {
            r |= n & 1;
            n >>= 1;
        }

        return n + r;
    }

    private static bool CheckInvariant(RunEntry[] stack, int stackCurr)
    {
        if (stackCurr < 2)
        {
            return true;
        }

        if (stackCurr == 2)
        {
            return stack[0].Length > stack[1].Length;
        }

        int a = stack[stackCurr - 3].Length;
        int b = stack[stackCurr - 2].Length;
        int c = stack[stackCurr - 1].Length;
        return !(a <= b + c || b <= c);
    }

    /// <summary>
    /// Ensures <paramref name="storage"/> has at least <paramref name="needed"/>
    /// elements allocated. Matches <c>resize</c>.
    /// </summary>
    private static void EnsureStorage<T>(ref T[] storage, ref int storageLen, int needed)
    {
        if (storageLen < needed)
        {
            int newSize = Math.Max(needed, storageLen * 2);
            if (storage.Length < newSize)
            {
                Array.Resize(ref storage, newSize);
            }

            storageLen = newSize;
        }
    }

    /// <summary>
    /// Merges the two topmost runs on the stack. Matches <c>merge</c>.
    /// </summary>
    private static void Merge<T>(IList<T> list, RunEntry[] stack, int stackCurr, Comparison<T> comparison, ref T[] storage, ref int storageLen)
    {
        int a = stack[stackCurr - 2].Length;
        int b = stack[stackCurr - 1].Length;
        int curr = stack[stackCurr - 2].Start;

        EnsureStorage(ref storage, ref storageLen, Math.Min(a, b));

        if (a < b)
        {
            // Left merge: copy A to storage, merge from storage and B into list.
            for (int i = 0; i < a; i++)
            {
                storage[i] = list[curr + i];
            }

            int iStor = 0;
            int j = curr + a;

            for (int k = curr; k < curr + a + b; k++)
            {
                if (iStor < a && j < curr + a + b)
                {
                    if (comparison(storage[iStor], list[j]) <= 0)
                    {
                        list[k] = storage[iStor++];
                    }
                    else
                    {
                        list[k] = list[j++];
                    }
                }
                else if (iStor < a)
                {
                    list[k] = storage[iStor++];
                }
                else
                {
                    list[k] = list[j++];
                }
            }
        }
        else
        {
            // Right merge: copy B to storage, merge from right-to-left.
            for (int i = 0; i < b; i++)
            {
                storage[i] = list[curr + a + i];
            }

            int iStor = b - 1;
            int j = curr + a - 1;

            for (int k = curr + a + b - 1; k >= curr; k--)
            {
                if (iStor >= 0 && j >= curr)
                {
                    if (comparison(list[j], storage[iStor]) > 0)
                    {
                        list[k] = list[j--];
                    }
                    else
                    {
                        list[k] = storage[iStor--];
                    }
                }
                else if (iStor >= 0)
                {
                    list[k] = storage[iStor--];
                }
                else
                {
                    list[k] = list[j--];
                }
            }
        }
    }

    /// <summary>
    /// Collapses the run stack, merging runs until the invariant is restored.
    /// Matches <c>collapse</c>.
    /// </summary>
    private static int Collapse<T>(IList<T> list, RunEntry[] stack, int stackCurr, int size, Comparison<T> comparison, ref T[] storage, ref int storageLen)
    {
        while (true)
        {
            if (stackCurr <= 1)
            {
                break;
            }

            // If this is the last merge, just do it.
            if (stackCurr == 2 && stack[0].Length + stack[1].Length == size)
            {
                Merge(list, stack, stackCurr, comparison, ref storage, ref storageLen);
                stack[0] = stack[0] with { Length = stack[0].Length + stack[1].Length };
                stackCurr--;
                break;
            }

            // Check if the invariant is off for a stack of 2 elements.
            if (stackCurr == 2 && stack[0].Length <= stack[1].Length)
            {
                Merge(list, stack, stackCurr, comparison, ref storage, ref storageLen);
                stack[0] = stack[0] with { Length = stack[0].Length + stack[1].Length };
                stackCurr--;
                break;
            }

            if (stackCurr == 2)
            {
                break;
            }

            int a = stack[stackCurr - 3].Length;
            int b = stack[stackCurr - 2].Length;
            int c = stack[stackCurr - 1].Length;

            if (a <= b + c)
            {
                if (a < c)
                {
                    Merge(list, stack, stackCurr - 1, comparison, ref storage, ref storageLen);
                    stack[stackCurr - 3] = stack[stackCurr - 3] with { Length = stack[stackCurr - 3].Length + stack[stackCurr - 2].Length };
                    stack[stackCurr - 2] = stack[stackCurr - 1];
                    stackCurr--;
                }
                else
                {
                    Merge(list, stack, stackCurr, comparison, ref storage, ref storageLen);
                    stack[stackCurr - 2] = stack[stackCurr - 2] with { Length = stack[stackCurr - 2].Length + stack[stackCurr - 1].Length };
                    stackCurr--;
                }
            }
            else if (b <= c)
            {
                Merge(list, stack, stackCurr, comparison, ref storage, ref storageLen);
                stack[stackCurr - 2] = stack[stackCurr - 2] with { Length = stack[stackCurr - 2].Length + stack[stackCurr - 1].Length };
                stackCurr--;
            }
            else
            {
                break;
            }
        }

        return stackCurr;
    }
}
