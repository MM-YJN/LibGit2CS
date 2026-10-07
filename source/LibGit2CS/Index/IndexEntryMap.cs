// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.IO;

namespace LibGit2CS.Index;

/// <summary>
/// Equality comparers for the index's path-keyed lookup structures, porting
/// <c>git_index_entrymap</c> (<c>src/libgit2/index_map.c</c>) to the managed
/// <see cref="Dictionary{TKey, TValue}"/>. libgit2 maintains a single hashmap
/// whose hash/equality functions are swapped on
/// <c>git_index__set_ignore_case</c>; this type exposes the two corresponding
/// comparers as case-sensitive / case-insensitive singletons.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hash</b> ports <c>git_index_entrymap_hash</c>
/// (<c>index_map.c:16-25</c>) verbatim: a Bernstein X31 string hash
/// (<c>h = h*31 + c</c>) over the raw path bytes — folding <c>A-Z</c> to
/// <c>a-z</c> via <see cref="char.ToLowerInvariant"/> (the <c>git__tolower</c>
/// ASCII-only fold) — with the conflict stage added at the end
/// (<c>return h + stage</c>). The fold is applied for <em>both</em> maps in
/// libgit2; for the case-sensitive map it is purely a distribution choice
/// (byte-equal paths fold equal, so there are no spurious misses — collisions
/// resolve via byte-exact equality).
/// </para>
/// <para>
/// <b>Equality</b> ports <c>git_index_entrymap_equal_default</c> /
/// <c>_icase</c> (<c>index_map.c:27-28</c>): stage equality combined with a
/// path comparison (<see cref="LibGit2CS.IO.GitPath.Compare(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/> for case-sensitive,
/// <see cref="LibGit2CS.IO.GitPath.CompareIgnoreCase(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPath)"/> for case-insensitive — the latter
/// is the <c>strcasecmp</c> ASCII-only fold, <em>not</em> .NET's
/// Unicode-aware <c>StringComparison.OrdinalIgnoreCase</c>).
/// </para>
/// </remarks>
internal sealed class IndexEntryKeyComparer : IEqualityComparer<(GitPath Path, int Stage)>
{
    /// <summary>Case-sensitive comparer (ports the <c>_default</c> entrymap).</summary>
    public static readonly IndexEntryKeyComparer CaseSensitive = new(ignoreCase: false);

    /// <summary>Case-insensitive comparer (ports the <c>_icase</c> entrymap).</summary>
    public static readonly IndexEntryKeyComparer CaseInsensitive = new(ignoreCase: true);

    private readonly bool _ignoreCase;

    private IndexEntryKeyComparer(bool ignoreCase) => _ignoreCase = ignoreCase;

    /// <summary>
    /// Ports <c>git_index_entrymap_equal_default</c> / <c>_icase</c>
    /// (<c>index_map.c:27-28</c>): same stage AND path equal under the
    /// configured case fold.
    /// </summary>
    public bool Equals((GitPath Path, int Stage) x, (GitPath Path, int Stage) y)
    {
        if (x.Stage != y.Stage)
        {
            return false;
        }

        return _ignoreCase
            ? GitPath.CompareIgnoreCase(x.Path, y.Path) == 0
            : x.Path.Equals(y.Path);
    }

    /// <summary>
    /// Ports <c>git_index_entrymap_hash</c> (<c>index_map.c:16-25</c>): X31
    /// hash over folded path bytes, plus the stage. The fold is applied
    /// unconditionally (matching libgit2, which folds for both maps).
    /// </summary>
    public int GetHashCode((GitPath Path, int Stage) obj)
    {
        ReadOnlySpan<byte> b = obj.Path.Span;
        unchecked
        {
            // h = git__tolower(first byte); then h = h*31 + git__tolower(c).
            // An empty path yields h = 0 (the C `if (h)` guard skips the loop).
            uint h;
            if (b.Length == 0)
            {
                h = 0;
            }
            else
            {
                h = (uint)GitPath.AsciiToLower(b[0]);
                for (int i = 1; i < b.Length; i++)
                {
                    h = (h << 5) - h + (uint)GitPath.AsciiToLower(b[i]);
                }
            }

            // libgit2 returns `h + stage` (unsigned add); wrap unchecked.
            return (int)h + obj.Stage;
        }
    }
}

/// <summary> Path-only (no stage) comparer over <see cref="GitPath"/>, used by transient index lookups that key on a single path regardless of stage (e.g. <see
/// cref="GitIndex.ReadTreeAsync"/>'s stat-preservation cache) and by iterator pathlist sorts/binary searches. Shares the <see cref="IndexEntryKeyComparer"/>
/// fold/hash logic but drops the stage term. </summary> <remarks> <para> Implements both <see cref="IEqualityComparer{GitPath}"/> (for <see
/// cref="Dictionary{TKey, TValue}"/>) and <see cref="IComparer{GitPath}"/>. The <see cref="IComparer{GitPath}"/> comparison ports <c>git__strcmp</c> /
/// <c>git__strcasecmp</c> (ASCII-only fold) — <em>not</em>.NET's Unicode-aware <c>StringComparison.OrdinalIgnoreCase</c>. </para> </remarks>
internal sealed class GitPathComparer : IEqualityComparer<GitPath>, IComparer<GitPath>
{
    /// <summary>Case-sensitive path comparer.</summary>
    public static readonly GitPathComparer CaseSensitive = new(ignoreCase: false);

    /// <summary>Case-insensitive path comparer (ASCII fold).</summary>
    public static readonly GitPathComparer CaseInsensitive = new(ignoreCase: true);

    private readonly bool _ignoreCase;

    private GitPathComparer(bool ignoreCase) => _ignoreCase = ignoreCase;

    /// <inheritdoc/>
    public bool Equals(GitPath x, GitPath y)
        => _ignoreCase ? GitPath.CompareIgnoreCase(x, y) == 0 : x.Equals(y);

    /// <summary>
    /// X31 hash over folded path bytes (the path-only portion of
    /// <c>git_index_entrymap_hash</c>, minus the stage).
    /// </summary>
    public int GetHashCode(GitPath obj)
    {
        ReadOnlySpan<byte> b = obj.Span;
        unchecked
        {
            uint h;
            if (b.Length == 0)
            {
                h = 0;
            }
            else
            {
                h = (uint)GitPath.AsciiToLower(b[0]);
                for (int i = 1; i < b.Length; i++)
                {
                    h = (h << 5) - h + (uint)GitPath.AsciiToLower(b[i]);
                }
            }

            return (int)h;
        }
    }

    /// <summary> Byte-wise comparison for sorted-vector operations. Ports <c>git__strcmp</c> (case-sensitive) / <c>git__strcasecmp</c>
    /// (case-insensitive, ASCII-only fold). Used by <c>IteratorBase</c>'s pathlist
    /// <see cref="System.Collections.Generic.List{T}.BinarySearch(int, int, T, System.Collections.Generic.IComparer{T}?)"/> /
    /// <see cref="System.Collections.Generic.List{T}.Sort()"/>. </summary>
    public int Compare(GitPath x, GitPath y)
        => _ignoreCase ? GitPath.CompareIgnoreCase(x, y) : GitPath.Compare(x, y);
}
