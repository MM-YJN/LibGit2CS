// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary>
/// Similarity hash for rename detection in diff/blame. Managed port of
/// libgit2's <c>src/libgit2/hashsig.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Uses a <b>custom rolling hash</b> (not SHA-1/SHA-256): seed
/// <c>0x012345678ABCDEF0</c>, polynomial <c>state = (state &lt;&lt; 5) - state +
/// ch</c>. Each line (or run up to 80 bytes) produces one 32-bit hash value.
/// </para>
/// <para>
/// Two heaps of up to 127 hashes each are maintained: <c>mins</c> (smallest
/// hashes) and <c>maxs</c> (largest hashes). For files with &lt; 127 lines,
/// only <c>mins</c> is compared. For larger files, both heaps are compared and
/// the result is averaged.
/// </para>
/// <para>
/// Consumers: <c>diff_tform.c</c> rename detection and <c>blame_git.c</c>.
/// </para>
/// </remarks>
internal sealed class SimilarityHash
{
    private const int HeapSize = 127;
    private const int HeapMinSize = 4;
    private const int MaxRun = 80;
    private const long HashStart = 0x012345678ABCDEF0;
    private const int Scale = 100;
    private readonly HashHeap _mins;
    private readonly HashHeap _maxs;
    private readonly SimilarityHashOptions _options;
    private int _lines;

    private SimilarityHash(SimilarityHashOptions options)
    {
        // C (hashsig.c): mins uses hashsig_cmp_min (largest at the root —
        // "ascending: false" here) and keeps the 127 smallest hashes; maxs
        // uses hashsig_cmp_max ("ascending: true") and keeps the 127
        // largest.
        // the small-file branch decision and the compared heap contents.
        _mins = new HashHeap(ascending: false);
        _maxs = new HashHeap(ascending: true);
        _options = options;
    }

    /// <summary>
    /// Creates a similarity hash from a byte buffer. Matches
    /// <c>git_hashsig_create</c>.
    /// </summary>
    /// <param name="data">The file content.</param>
    /// <param name="options">Hashing options.</param>
    /// <returns>A <see cref="SimilarityHash"/>, or <c>null</c> if the file is
    /// too small and <see cref="SimilarityHashOptions.AllowSmallFiles"/> is not
    /// set.</returns>
    public static SimilarityHash? Create(ReadOnlySpan<byte> data, SimilarityHashOptions options)
    {
        var sig = new SimilarityHash(options);
        sig.AddHashes(data);

        if (sig._mins.Size < HeapMinSize &&
            (options & SimilarityHashOptions.AllowSmallFiles) == 0)
        {
            return null;
        }

        sig._mins.Sort();
        sig._maxs.Sort();
        return sig;
    }

    /// <summary>
    /// Creates a similarity hash from a file. Matches
    /// <c>git_hashsig_create_fromfile</c>.
    /// </summary>
    public static async Task<SimilarityHash?> CreateFromFileAsync(string path, SimilarityHashOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return Create(bytes, options);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Compares two similarity hashes and returns a 0-100 similarity score.
    /// Matches <c>git_hashsig_compare</c>.
    /// </summary>
    public static int Compare(SimilarityHash a, SimilarityHash b)
    {
        // Both empty: similar if ignoring whitespace or both truly empty.
        if (a._mins.Size == 0 && b._mins.Size == 0)
        {
            if ((a._lines == 0 && b._lines == 0) ||
                (a._options & SimilarityHashOptions.IgnoreWhitespace) != 0)
            {
                return Scale;
            }

            return 0;
        }

        // Small files: compare mins only.
        if (a._mins.Size < HeapSize)
        {
            return HeapCompare(a._mins, b._mins);
        }

        // Large files: average mins and maxs.
        int mins = HeapCompare(a._mins, b._mins);
        int maxs = HeapCompare(a._maxs, b._maxs);
        return (mins + maxs) / 2;
    }

    private void AddHashes(ReadOnlySpan<byte> data)
    {
        // C's
        // hashsig_in_progress_init sets prog->use_ignores = 1 for BOTH
        // GIT_HASHSIG_IGNORE_WHITESPACE and GIT_HASHSIG_SMART_WHITESPACE
        // (hashsig.c:149-152), so the FIRST run of a smart-whitespace hash
        // skips leading non-LF whitespace. Initializing useIgnores only for
        // IgnoreWhitespace would hash the first line's leading whitespace
        // verbatim in SmartWhitespace mode.
        bool useIgnores = (_options & (SimilarityHashOptions.IgnoreWhitespace | SimilarityHashOptions.SmartWhitespace)) != 0;
        bool smartWs = (_options & SimilarityHashOptions.SmartWhitespace) != 0;
        bool ignoreAnyWs = (_options & (SimilarityHashOptions.IgnoreWhitespace | SimilarityHashOptions.SmartWhitespace)) != 0;

        int scan = 0;
        while (scan < data.Length)
        {
            long state = HashStart;
            int len = 0;

            while (scan < data.Length && len < MaxRun)
            {
                byte ch;

                if (useIgnores)
                {
                    while (scan < data.Length && IsSpaceNonLf(data[scan]))
                    {
                        scan++;
                    }

                    if (scan >= data.Length)
                    {
                        break;
                    }
                }
                else if (ignoreAnyWs)
                {
                    // Smart whitespace: skip \r chars.
                    while (scan < data.Length && data[scan] == '\r')
                    {
                        scan++;
                    }

                    if (scan >= data.Length)
                    {
                        break;
                    }
                }

                ch = data[scan];

                // Smart whitespace: toggle ignore mode based on newline.
                if (smartWs)
                {
                    useIgnores = ch == '\n';
                }

                scan++;

                // Check run terminator.
                if (ch is (byte)'\n' or (byte)'\0')
                {
                    _lines++;
                    break;
                }

                len++;
                state = unchecked(((state << 5) - state + ch));
            }

            if (len > 0)
            {
                _mins.Insert((uint)state);
                _maxs.Insert((uint)state);

                // Skip trailing newlines / NULs.
                while (scan < data.Length && (data[scan] == '\n' || data[scan] == '\0'))
                {
                    scan++;
                }
            }
            else if (scan < data.Length && data[scan] == '\n')
            {
                // Empty line — skip it.
                scan++;
            }
        }
    }

    private static bool IsSpaceNonLf(byte c)
        => c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\f' or (byte)'\v';

    /// <summary>
    /// Compares two sorted heaps, counting matching elements. Matches
    /// <c>hashsig_heap_compare</c>, which walks with the heap's own
    /// comparator (<c>a-&gt;cmp</c>) — each heap is sorted by that same
    /// comparator in <c>hashsig_heap_sort</c>, so a hardcoded ascending
    /// walk mis-advances on the descending-sorted <c>mins</c> heap.
    /// </summary>
    private static int HeapCompare(HashHeap a, HashHeap b)
    {
        int matches = 0;
        int i = 0;
        int j = 0;

        while (i < a.Size && j < b.Size)
        {
            int cmp = a.CompareValues(a[i], b[j]);
            if (cmp < 0)
            {
                i++;
            }
            else if (cmp > 0)
            {
                j++;
            }
            else
            {
                i++;
                j++;
                matches++;
            }
        }

        return Scale * (matches * 2) / (a.Size + b.Size);
    }

    /// <summary>
    /// Bounded min/max heap of 32-bit hash values. Matches <c>hashsig_heap</c>.
    /// </summary>
    private sealed class HashHeap(bool ascending)
    {
        private readonly uint[] _values = new uint[HeapSize];
        private readonly bool _ascending = ascending;

        public int Size { get; private set; }

        public uint this[int index] => _values[index];

        public void Insert(uint val)
        {
            if (Size < HeapSize)
            {
                _values[Size++] = val;
                SiftUp(Size - 1);
            }
            else if (Compare(val, _values[0]) > 0)
            {
                Size--;
                _values[0] = _values[Size];
                SiftDown(0);
            }
        }

        public void Sort()
        {
            // Simple insertion sort — arrays are small (≤127 elements).
            for (int i = 1; i < Size; i++)
            {
                uint key = _values[i];
                int j = i - 1;
                while (j >= 0 && Compare(_values[j], key) > 0)
                {
                    _values[j + 1] = _values[j];
                    j--;
                }

                _values[j + 1] = key;
            }
        }

        private int Compare(uint a, uint b)
            => _ascending ? a.CompareTo(b) : b.CompareTo(a);

        /// <summary>
        /// Compares two hash values with this heap's comparator (the
        /// counterpart of C's <c>hashsig_heap::cmp</c>, used by both the
        /// heap operations and the merge walk).
        /// </summary>
        public int CompareValues(uint a, uint b) => Compare(a, b);

        private void SiftUp(int el)
        {
            while (el > 0)
            {
                int parent = (el - 1) >> 1;
                if (Compare(_values[parent], _values[el]) <= 0)
                {
                    break;
                }

                (_values[el], _values[parent]) = (_values[parent], _values[el]);
                el = parent;
            }
        }

        private void SiftDown(int el)
        {
            while (el < Size / 2)
            {
                int left = (el << 1) + 1;
                int right = (el << 1) + 2;
                int swapEl = left;

                // C (hashsig.c:89-105): hashsig_heap_down reads the RIGHT child unconditionally — when el reaches the last parent (el = Size/2 - 1) the right
                // child index equals Size and the STALE slot participates in the comparison (the heap is always sifted at Size = 126 after the full-heap
                // replace). A `right < Size` guard would skip the stale slot and yield different heap contents.
                if (Compare(_values[left], _values[right]) > 0)
                {
                    swapEl = right;
                }

                if (Compare(_values[el], _values[swapEl]) <= 0)
                {
                    break;
                }

                (_values[el], _values[swapEl]) = (_values[swapEl], _values[el]);
                el = swapEl;
            }
        }
    }
}

/// <summary>
/// Options for <see cref="SimilarityHash"/>. Matches libgit2's
/// <c>git_hashsig_option_t</c>.
/// </summary>
[Flags]
internal enum SimilarityHashOptions
{
    /// <summary>Default: no whitespace handling.</summary>
    Normal = 0,

    /// <summary>Ignore all whitespace except newlines. Matches <c>GIT_HASHSIG_IGNORE_WHITESPACE</c>.</summary>
    IgnoreWhitespace = 1,

    /// <summary>
    /// Ignore whitespace only in lines that end with whitespace. Matches
    /// <c>GIT_HASHSIG_SMART_WHITESPACE</c>.
    /// </summary>
    SmartWhitespace = 2,

    /// <summary>Allow files with fewer than 4 lines. Matches <c>GIT_HASHSIG_ALLOW_SMALL_FILES</c>.</summary>
    AllowSmallFiles = 4,
}
