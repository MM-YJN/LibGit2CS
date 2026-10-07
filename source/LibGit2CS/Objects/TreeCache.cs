// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Globalization;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.Objects;

/// <summary> Per-repo tree cache for canonical tree comparisons. Managed port of libgit2's <c>src/libgit2/tree-cache.c</c> + <c>tree-cache.h</c>. </summary>
/// <remarks> <para> A tree-structured cache stored in the index TREE extension, mirroring the repository's tree hierarchy. Each node records the entry count
/// and OID of the corresponding tree, allowing fast equality checks without reloading the tree from the ODB. </para> <para> Read side (<see
/// cref="ReadTreeAsync"/>, <see cref="Get"/>, <see cref="InvalidatePath"/>, <see cref="Read"/>) and write side (<see cref="Write"/>) are both implemented.
/// </para> <para> The C version uses <c>git_pool</c> for arena allocation and a flex-array <c>name</c> field. The managed port uses normal GC allocation and a
/// byte-faithful <see cref="GitPath"/> name — no pool parameter needed. Paths are compared byte-wise (<c>memcmp</c>-equivalent), matching libgit2's
/// <c>find_child</c> (tree-cache.c:13-26), so non-UTF-8 path bytes survive a TREE-extension read/write round-trip without decode loss. </para> </remarks>
internal sealed class TreeCache
{
    /// <summary>
    /// Creates a new tree cache node with the given name.
    /// Matches <c>git_tree_cache_new</c>.
    /// </summary>
    public TreeCache(GitPath name, GitHashAlgorithmKind oidType)
    {
        Name = name;
        OidType = oidType;
        EntryCount = 0;
        Oid = default;
        Children = [];
    }

    /// <summary>The name of this tree node (empty for the root).</summary>
    public GitPath Name { get; }

    /// <summary>The hash algorithm used for OIDs in this cache.</summary>
    public GitHashAlgorithmKind OidType { get; }

    /// <summary>
    /// Number of entries in this tree, or -1 if invalidated. Matches
    /// <c>git_tree_cache.entry_count</c> (ssize_t).
    /// </summary>
    public int EntryCount { get; set; }

    /// <summary>The OID of the tree object this cache node represents.</summary>
    public GitOid Oid { get; set; }

    /// <summary>Child tree cache nodes (subdirectories only).</summary>
    public List<TreeCache> Children { get; }

    /// <summary>
    /// Builds a tree cache from a live <see cref="GitTree"/> object. Matches
    /// <c>git_tree_cache_read_tree</c>.
    /// </summary>
    /// <param name="tree">The root tree to build the cache from.</param>
    /// <param name="oidType">The OID type (SHA-1 or SHA-256).</param>
    /// <returns>A <see cref="TreeCache"/> rooted at the given tree.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<TreeCache> ReadTreeAsync(GitTree tree, GitHashAlgorithmKind oidType, CancellationToken cancellationToken = default)
    {
        var cache = new TreeCache(default, oidType);
        await ReadTreeRecursiveAsync(cache, tree, cancellationToken).ConfigureAwait(false);
        return cache;
    }

    private static async Task ReadTreeRecursiveAsync(TreeCache cache, GitTree tree, CancellationToken cancellationToken)
    {
        cache.Oid = tree.Id;
        GitRepository? repo = tree.Owner;

        for (int i = 0; i < tree.EntryCount; i++)
        {
            GitTreeEntry? entry = tree.EntryByIndex(i);
            if (entry is null)
            {
                continue;
            }

            if (!entry.Value.IsTree)
            {
                cache.EntryCount++;
                continue;
            }

            var child = new TreeCache(entry.Value.Name, cache.OidType);
            cache.Children.Add(child);

            if (repo is not null)
            {
                GitTree? subtree = await repo.Objects.LookupAsync<GitTree>(entry.Value.Id, cancellationToken).ConfigureAwait(false);
                if (subtree is null)
                {
                    // C (tree-cache.c:216-218): a missing subtree ABORTS the whole cache build with the lookup error.
                    throw new GitException(
                        GitErrorCode.NotFound,
                        $"tree cache: subtree {entry.Value.Id} not found",
                        GitErrorCategory.Object);
                }

                await ReadTreeRecursiveAsync(child, subtree, cancellationToken).ConfigureAwait(false);
                cache.EntryCount += child.EntryCount;
                subtree.Dispose();
            }
        }
    }

    /// <summary>
    /// Looks up a sub-tree cache by path. Matches <c>git_tree_cache_get</c>.
    /// </summary>
    /// <param name="path">Slash-separated path (e.g. <c>"src/Core"</c>).</param>
    /// <returns>The matching cache node, or <c>null</c> if not found.</returns>
    public TreeCache? Get(GitPath path)
    {
        ReadOnlySpan<byte> ptr = path.Span;
        TreeCache? current = this;

        while (true)
        {
            int slash = ptr.IndexOf((byte)'/');
            ReadOnlySpan<byte> segment = slash < 0 ? ptr : ptr[..slash];

            current = FindChild(current, segment);
            if (current is null)
            {
                return null;
            }

            if (slash < 0 || slash + 1 >= ptr.Length)
            {
                return current;
            }

            ptr = ptr[(slash + 1)..];
        }
    }

    /// <summary>
    /// Invalidates a path in the cache (sets entry_count = -1 recursively).
    /// Matches <c>git_tree_cache_invalidate_path</c>.
    /// </summary>
    public static void InvalidatePath(TreeCache? tree, GitPath path)
    {
        if (tree is null)
        {
            return;
        }

        tree.EntryCount = -1;

        ReadOnlySpan<byte> ptr = path.Span;
        while (true)
        {
            int slash = ptr.IndexOf((byte)'/');
            if (slash < 0)
            {
                break;
            }

            tree = FindChild(tree, ptr[..slash]);
            if (tree is null)
            {
                return;
            }

            tree.EntryCount = -1;
            ptr = ptr[(slash + 1)..];
        }
    }

    /// <summary>
    /// Parses a tree cache from the index TREE extension binary format.
    /// Matches <c>git_tree_cache_read</c>.
    /// </summary>
    /// <param name="buffer">The binary data (contents of the TREE extension).</param>
    /// <param name="oidType">The OID type (SHA-1 or SHA-256).</param>
    /// <returns>The parsed tree cache.</returns>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> on corruption — C fails the whole
    /// index parse ("corrupted TREE extension in index", index.c:2710-2712,
    /// tree-cache.c:146-166), it never silently drops the cache.
    /// </exception>
    public static TreeCache Read(ReadOnlySpan<byte> buffer, GitHashAlgorithmKind oidType)
    {
        int pos = 0;
        TreeCache? result = ReadInternal(buffer, ref pos, oidType);
        if (result is null)
        {
            throw new GitException(GitErrorCode.Error, "corrupted TREE extension in index", GitErrorCategory.Index);
        }

        if (pos != buffer.Length)
        {
            throw new GitException(GitErrorCode.Error, "corrupted TREE extension in index (unexpected trailing data)", GitErrorCategory.Index);
        }

        return result;
    }

    private static TreeCache? ReadInternal(ReadOnlySpan<byte> buffer, ref int pos, GitHashAlgorithmKind oidType)
    {
        if (pos >= buffer.Length)
        {
            return null;
        }

        // NUL-terminated name. Materialize a byte[] copy that the GitPath owns,
        // matching C's `memcpy(tree->name, name, name_len)` into the flex array
        // (tree-cache.c:253). The buffer span is stack-local and cannot be
        // retained across the GitPath's ReadOnlyMemory<byte> lifetime.
        int nameEnd = buffer[pos..].IndexOf((byte)'\0');
        if (nameEnd < 0)
        {
            return null;
        }

        var name = GitPath.FromUtf8Bytes(buffer.Slice(pos, nameEnd).ToArray());
        pos += nameEnd + 1;

        if (pos >= buffer.Length)
        {
            return null;
        }

        var cache = new TreeCache(name, oidType);

        // Blank-terminated ASCII decimal entry count.
        (int entryCount, int entryConsumed) = ParseDecimal(buffer[pos..]);
        if (entryConsumed == 0)
        {
            return null;
        }

        cache.EntryCount = entryCount;
        pos += entryConsumed;

        if (pos >= buffer.Length || buffer[pos] != ' ')
        {
            return null;
        }

        pos++;

        // Newline-terminated ASCII decimal children count.
        (int childCount, int childConsumed) = ParseDecimal(buffer[pos..]);
        if (childConsumed == 0 || childCount < 0)
        {
            return null;
        }

        pos += childConsumed;

        if (pos >= buffer.Length || buffer[pos] != '\n')
        {
            return null;
        }

        pos++;

        // OID is only present if entry_count >= 0.
        if (cache.EntryCount >= 0)
        {
            int oidSize = GitOid.SizeFor(oidType);
            if (pos + oidSize > buffer.Length)
            {
                return null;
            }

            cache.Oid = GitOid.FromRaw(buffer.Slice(pos, oidSize), oidType);
            pos += oidSize;
        }

        // Parse children.
        for (int i = 0; i < childCount; i++)
        {
            TreeCache? child = ReadInternal(buffer, ref pos, oidType);
            if (child is null)
            {
                return null;
            }

            cache.Children.Add(child);
        }

        return cache;
    }

    /// <summary> Parses an ASCII decimal number terminated by a non-digit. Returns the value and the number of bytes consumed. Handles optional leading minus
    /// sign (for invalidated entry_count = -1). Matches <c>git__strntol32</c> (util.c:36-124): leading ASCII whitespace is skipped and a leading
    /// <c>+</c>/<c>-</c> sign is accepted. </summary>
    private static (int value, int consumed) ParseDecimal(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
        {
            return (0, 0);
        }

        int i = 0;

        // C (util.c:46-63): skip leading ASCII whitespace.
        while (i < span.Length && span[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r')
        {
            i++;
        }

        bool negative = false;
        if (i < span.Length && span[i] is (byte)'-' or (byte)'+')
        {
            negative = span[i] == '-';
            i++;
        }

        int start = i;
        while (i < span.Length && span[i] is >= (byte)'0' and <= (byte)'9')
        {
            i++;
        }

        if (i == start)
        {
            return (0, 0);
        }

        if (int.TryParse(span[start..i], CultureInfo.InvariantCulture, out int value))
        {
            return (negative ? -value : value, i);
        }

        return (0, 0);
    }

    private static TreeCache? FindChild(TreeCache tree, ReadOnlySpan<byte> name)
    {
        for (int i = 0; i < tree.Children.Count; i++)
        {
            TreeCache child = tree.Children[i];
            if (name.SequenceEqual(child.Name.Span))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>
    /// Serializes this tree cache to the binary format used by the index TREE
    /// extension. Matches <c>git_tree_cache_write</c> (tree-cache.c:282-287) +
    /// <c>write_tree</c> (tree-cache.c:269-280).
    /// </summary>
    /// <remarks>
    /// Format per node: <c>&lt;name&gt;\0&lt;entry_count&gt; &lt;children_count&gt;\n</c>
    /// followed by the raw OID (if <see cref="EntryCount"/> &gt;= 0), then each
    /// child node recursively. A node with <see cref="EntryCount"/> == -1
    /// (invalidated) has no OID written.
    /// </remarks>
    /// <returns>The serialized binary data.</returns>
    public byte[] Write()
    {
        using var buf = new MemoryStream();
        WriteNode(buf);
        return buf.ToArray();
    }

    private void WriteNode(MemoryStream buf)
    {
        // <name>\0
        buf.Write(Name.Span);
        buf.WriteByte(0);

        // <entry_count> <children_count>\n
        string header = $"{EntryCount} {Children.Count}\n";
        byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(header);
        buf.Write(headerBytes);

        // Raw OID (only if entry_count >= 0).
        if (EntryCount >= 0)
        {
            buf.Write(Oid.RawBytes);
        }

        // Children recursively.
        foreach (TreeCache child in Children)
        {
            child.WriteNode(buf);
        }
    }
}
