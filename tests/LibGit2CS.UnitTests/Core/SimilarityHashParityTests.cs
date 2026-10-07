using System.Text;

using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

/// <summary>
/// Regression tests for the hashsig parity behaviors in
/// libgit2 1.9.4. Expected scores were
/// differentially verified against the C reference (libgit2 1.9.4,
/// <c>src/libgit2/hashsig.c</c>) via a C harness linking the built static
/// library (git_hashsig_create + git_hashsig_compare).
///
/// The line contents are chosen so their rolling hashes are strictly
/// increasing ("baz%04d" → 0xbe3df5ab + i) and the tail line "0000" hashes
/// below every "baz" line (0x126a72f0). That makes the 128-line buffers
/// saturate the bounded heaps asymmetrically, which is exactly where the
/// port diverged:
///
/// 1. C's <c>mins</c> heap keeps the 127 *smallest* hashes (root = largest
///    kept) while the port's <c>_mins</c> kept the 127 *largest* — so the
///    small-file branch decision (C: <c>mins.size &lt; 127</c>, port:
///    <c>_mins.Size &lt; 127</c>) and the compared heap contents were
///    mirrored.
/// 2. The merge walk hardcoded an ascending <c>CompareTo</c> instead of the
///    heap's own comparator, mis-advancing on the descending-sorted
///    smallest-kept heap (component computed as 0 where C counts all
///    matches).
/// </summary>
public class SimilarityHashParityTests
{
    private const SimilarityHashOptions Options =
        SimilarityHashOptions.SmartWhitespace | SimilarityHashOptions.AllowSmallFiles;

    /// <summary>Builds "baz%04d\n" lines for i in [from, to).</summary>
    private static byte[] BazRange(int from, int to)
    {
        var sb = new StringBuilder();
        for (int i = from; i < to; i++)
        {
            sb.Append("baz").Append(i.ToString("D4")).Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static int Score(byte[] a, byte[] b)
    {
        var sa = SimilarityHash.Create(a, Options);
        var sb = SimilarityHash.Create(b, Options);
        Assert.NotNull(sa);
        Assert.NotNull(sb);
        return SimilarityHash.Compare(sa, sb);
    }

    [Fact]
    public void HeapRoles_SmallFileBranch_UsesSmallestHeap()
    {
        // A: baz0000..baz0126 + "0000" (128 lines), B: baz0000..baz0125 +
        // baz0127 + "0000" (128 lines).
        // C: mins keeps the 127 smallest; the trailing "0000" evicts the
        // largest kept (baz0126 / baz0127) and shrinks mins to 126 on both
        // sides → small-file branch → compare mins only → 100. Keeping the
        // 127 largest instead would take the large-file branch → 99.
        byte[] a = BazRange(0, 127);
        byte[] b = BazRange(0, 126);
        byte[] tail = Encoding.UTF8.GetBytes("0000\n");
        byte[] fullA = [.. a, .. tail];
        byte[] fullB = [.. b, .. Encoding.UTF8.GetBytes("baz0127\n"), .. tail];

        Assert.Equal(100, Score(fullA, fullB));
    }

    [Fact]
    public void HeapWalk_UsesTheHeapsOwnComparator()
    {
        // A: baz0000..baz0126 + "0000", B: baz0000..baz0124 + baz0126 +
        // baz0127 + "0000" (128 lines each).
        // C: mins shrinks to 126 on both sides → small branch → 99 (125
        // shared of 126+126). The merge walk must use the heap's own
        // comparator: an ascending CompareTo over the descending-sorted
        // _maxs heaps ({baz0125..baz0000} vs
        // {baz0126, baz0124..baz0000}) would advance i past every element
        // below baz0126 and count 0 matches → 49.
        byte[] a = BazRange(0, 127);
        byte[] b = BazRange(0, 125);
        byte[] tail = Encoding.UTF8.GetBytes("0000\n");
        byte[] fullA = [.. a, .. tail];
        byte[] fullB = [.. b, .. BazRange(126, 128), .. tail];

        Assert.Equal(99, Score(fullA, fullB));
    }

    [Fact]
    public void SmallFiles_CompareTheFullSets()
    {
        // Both files under the heap size: every hash is kept in both heaps,
        // so the roles and the walk do not matter — the score is the exact
        // overlap ratio. C: 49 shared of 50+50 → 98.
        byte[] a = BazRange(0, 50);
        byte[] b = [.. BazRange(0, 49), .. BazRange(50, 51)];

        Assert.Equal(98, Score(a, b));
    }

    [Fact]
    public void IdenticalLargeFiles_Score100()
    {
        byte[] a = [.. BazRange(0, 300), .. Encoding.UTF8.GetBytes("0000\n")];

        Assert.Equal(100, Score(a, a));
    }
}
