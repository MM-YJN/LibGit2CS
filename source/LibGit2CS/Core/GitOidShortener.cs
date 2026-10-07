// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Core;

/// <summary>
/// Unique OID abbreviation lengthener. Managed port of libgit2's
/// <c>git_oid_shorten</c> (<c>src/libgit2/oid.c:349-534</c>).
/// </summary>
/// <remarks>
/// <para>
/// A 16-ary trie (one child per hex nibble) that stores OID hex prefixes.
/// After each <see cref="Add"/> call, <see cref="MinLength"/> reports the
/// shortest unambiguous prefix length across all inserted OIDs. This is the
/// data structure behind <c>core.abbrev</c> auto-sizing and
/// <c>git rev-list --abbrev-commit</c>.
/// </para>
/// <para>
/// <see cref="Add"/> returns the abbreviation length on success and throws
/// <see cref="GitException"/> for invalid hex or a full trie. C's negative
/// return code and error category are carried by the exception.
/// </para>
/// <para>
/// The trie caps at <see cref="short.MaxValue"/> (32,767) nodes — matching
/// C's <c>SHRT_MAX</c> guard at <c>oid.c:380-383</c>. With uniform OID
/// distribution this supports ~20,000 unique OIDs before the cap is hit.
/// </para>
/// <para>
/// Node layout (matches C's <c>trie_node</c> union):
/// <list type="bullet">
/// <item><description>Internal node: 16 <see cref="short"/> child indexes
/// (one per hex nibble 0–F). 0 = empty, positive = internal child index,
/// negative = leaf index (sign-flipped).</description></item>
/// <item><description>Leaf node: a <c>string</c> tail (the remaining hex
/// chars after the prefix that led here). The first char of the tail is
/// the nibble; the rest is the unprocessed suffix.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class GitOidShortener : IDisposable
{
    // Hex size assumed by the trie. libgit2 hardcodes GIT_OID_SHA1_HEXSIZE (40).
    // The trie walks up to 40 nibbles; longer inputs are truncated by the
    // caller. Matches `for (i = 0; i < GIT_OID_SHA1_HEXSIZE; ++i)` at oid.c:486.
    private const int Sha1HexSize = 40;

    // Each internal node is 16 shorts (one per nibble 0–F). Leaf nodes store
    // a tail string instead. We keep the two arrays in parallel: _children
    // for internal nodes, _tails for leaves. A child slot of 0 means empty,
    // positive means internal child at that index, negative means leaf at
    // index -slot (sign-flipped).
    private short[][] _children;
    private string?[] _tails;
    private int _nodeCount;
    private int _capacity;
    private bool _full;
    private bool _disposed;

    /// <summary>
    /// Creates a new shortener. Matches <c>git_oid_shorten_new</c>
    /// (<c>oid.c:394-413</c>).
    /// </summary>
    /// <param name="minLength">
    /// The minimum abbreviation length to start from. The shortener will
    /// never report a <see cref="MinLength"/> below this. Matches C's
    /// <c>min_length</c> parameter.
    /// </param>
    public GitOidShortener(int minLength = 4)
    {
        MinLength = minLength;
        _capacity = 16;
        _children = new short[_capacity][];
        _tails = new string?[_capacity];
        _children[0] = new short[16];
        _tails[0] = null;
        _nodeCount = 1;
    }

    /// <summary>
    /// The current minimum unambiguous abbreviation length across all
    /// inserted OIDs. Matches the return value of
    /// <c>git_oid_shorten_add</c>.
    /// </summary>
    public int MinLength { get; private set; }

    /// <summary>
    /// True once the trie has hit the <see cref="short.MaxValue"/> node cap
    /// and can no longer accept new OIDs. Subsequent <see cref="Add"/> calls
    /// throw <see cref="GitException"/>. Matches C's <c>os->full</c> flag.
    /// </summary>
    public bool IsFull => _full;

    /// <summary>
    /// Adds an OID hex string to the trie and returns the new minimum
    /// unambiguous abbreviation length. Matches <c>git_oid_shorten_add</c>
    /// (<c>oid.c:469-533</c>).
    /// </summary>
    /// <param name="oidHex">
    /// The full OID as lowercase hex (40 chars for SHA-1). Only the first
    /// 40 chars are walked; longer strings are ignored beyond that.
    /// Unless the trie is full, <c>null</c> returns the current <see cref="MinLength"/>
    /// unchanged (C: <c>text_oid == NULL</c> returns <c>os-&gt;min_length</c>).
    /// </param>
    /// <returns>
    /// The new <see cref="MinLength"/>.
    /// </returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> with <see cref="GitErrorCategory.Invalid"/>
    /// if the trie is full (including when <paramref name="oidHex"/> is null),
    /// or the trie walk encounters a non-hex character or the end of the input.
    /// </exception>
    public int Add(string? oidHex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // C checks os->full BEFORE the NULL check (oid.c:475-481): a full
        // trie returns -1 even for a NULL input.
        if (_full)
        {
            throw new GitException(GitErrorCode.Error, "unable to shorten OID - OID set full", GitErrorCategory.Invalid);
        }

        if (oidHex is null)
        {
            return MinLength;
        }

        short idx = 0; // root node
        bool isLeaf = false;
        int i;

        for (i = 0; i < Sha1HexSize; i++)
        {
            // C reads the NUL terminator when the string ends before 40
            // nibbles and git__fromhex('\0') returns -1 (oid.c:486-493) —
            // a short string descending an existing trie path fails
            // gracefully instead of reading past the end.
            if (i >= oidHex.Length)
            {
                throw new GitException(GitErrorCode.Error, "unable to shorten OID - invalid hex value", GitErrorCategory.Invalid);
            }

            int c = FromHex(oidHex[i]);
            if (c < 0)
            {
                // C (oid.c:490-493): invalid hex returns -1 with
                // GIT_ERROR_INVALID; translate that error into the managed exception.
                throw new GitException(GitErrorCode.Error, "unable to shorten OID - invalid hex value", GitErrorCategory.Invalid);
            }

            // If the current node is a leaf (reached by following a negative
            // child slot last iteration), expand it into an internal node
            // by pushing the stored tail down a level. Matches oid.c:497-509.
            if (isLeaf)
            {
                string tail = _tails[idx]!;
                _tails[idx] = null;
                _children[idx] = new short[16];

                // Re-create the leaf one level deeper at the tail's first nibble.
                int tailNibble = FromHex(tail[0]);
                if (!TryPushLeaf(idx, tailNibble, tail[1..]))
                {
                    throw new GitException(GitErrorCode.Error, "unable to shorten OID - OID set full", GitErrorCategory.Invalid);
                }
            }

            short[] node = _children[idx]
                ?? throw new InvalidOperationException("trie node not initialized");

            short slot = node[c];
            if (slot == 0)
            {
                // Empty slot — push a new leaf holding the rest of this OID.
                if (!TryPushLeaf(idx, c, oidHex[(i + 1)..]))
                {
                    throw new GitException(GitErrorCode.Error, "unable to shorten OID - OID set full", GitErrorCategory.Invalid);
                }

                break;
            }

            if (slot < 0)
            {
                // Leaf slot — flip sign to get the leaf index, and mark so
                // the next iteration expands it (if we descend further).
                _children[idx][c] = slot = (short)(-slot);
                idx = slot;
                isLeaf = true;
            }
            else
            {
                // Internal node — descend.
                idx = slot;
                isLeaf = false;
            }
        }

        // libgit2 (oid.c:529-530): `if (++i > os->min_length) os->min_length = i;`
        // — a full 40-nibble match (duplicate OID) completes the loop with
        // i == 40 and bumps min_length to 41; an early break leaves i at the
        // pushed-leaf position, so ++i covers the divergent nibble.
        i++;
        if (i > MinLength)
        {
            MinLength = i;
        }

        return MinLength;
    }

    /// <summary>
    /// Allocates a new leaf node and stores <paramref name="tail"/> there,
    /// linking it from parent node <paramref name="parentIdx"/> at child
    /// slot <paramref name="pushAt"/>. Matches <c>push_leaf</c>
    /// (<c>oid.c:368-392</c>). Returns false if the trie is full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Like C's <c>push_leaf</c>, the node count is incremented and the
    /// <c>node_count == SHRT_MAX</c> full check runs <b>before</b> the leaf
    /// is linked: the boundary push that fills the trie fails without
    /// mutating the trie.
    /// </para>
    /// </remarks>
    private bool TryPushLeaf(short parentIdx, int pushAt, string tail)
    {
        if (_nodeCount >= _capacity)
        {
            Resize(_capacity * 2);
        }

        short leafIdx = (short)_nodeCount++;

        if (_nodeCount == short.MaxValue)
        {
            _full = true;
            return false;
        }

        _children[parentIdx][pushAt] = (short)(-leafIdx);
        _tails[leafIdx] = tail;

        return true;
    }

    private void Resize(int newCapacity)
    {
        short[][] newChildren = new short[newCapacity][];
        string?[] newTails = new string?[newCapacity];
        Array.Copy(_children, newChildren, _capacity);
        Array.Copy(_tails, newTails, _capacity);
        // New slots above _nodeCount are uninitialized; they're only read
        // after TryPushLeaf assigns them. No explicit init needed.

        _children = newChildren;
        _tails = newTails;
        _capacity = newCapacity;
    }

    private static int FromHex(char c)
    {
        if (c is >= '0' and <= '9')
        {
            return c - '0';
        }

        if (c is >= 'a' and <= 'f')
        {
            return c - 'a' + 10;
        }

        if (c is >= 'A' and <= 'F')
        {
            return c - 'A' + 10;
        }

        return -1;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
