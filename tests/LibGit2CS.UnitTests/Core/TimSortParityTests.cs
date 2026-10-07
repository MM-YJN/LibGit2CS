using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Core;

/// <summary> Regression tests for the shared stable timsort that backs <c>git_vector_sort</c> (C <c>src/util/tsort.c</c>). </summary> <remarks> the
/// run-extension bisort searched the absolute range <c>[0..i-1]</c> instead of the run-relative range <c>[curr..i-1]</c>, corrupting any sort of ≥64 elements
/// whose natural run was shorter than <c>minrun</c>. Verified first-party: the repro below emitted <c>0,137,138,139,0,1,…</c>. </remarks>
public sealed class TimSortParityTests
{
    private static List<int> Run1ShortRun2Repro()
    {
        // 100..139 (40 ascending) + 0,1,2,3 (4 ascending) + 0..19 (20) = 64 elements.
        var list = new List<int>();
        for (int i = 100; i < 140; i++)
        {
            list.Add(i);
        }

        list.AddRange(new[] { 0, 1, 2, 3 });
        for (int i = 0; i < 20; i++)
        {
            list.Add(i);
        }

        return list;
    }

    private static void AssertSorted(List<int> list)
    {
        for (int i = 1; i < list.Count; i++)
        {
            Assert.True(list[i - 1] <= list[i], $"unsorted at {i}: {list[i - 1]} > {list[i]}");
        }
    }

    [Fact]
    public void Sort_Run1ShortRun2_64Elements_SortsCorrectly()
    {
        // Repro: the shipped TimSort emitted "0,137,138,139,0,1,…" for this input (not sorted).
        List<int> list = Run1ShortRun2Repro();
        TimSort.Sort(list, (a, b) => a.CompareTo(b));
        AssertSorted(list);
        Assert.Equal(64, list.Count);
        Assert.Equal(0, list[0]);
        Assert.Equal(139, list[^1]);
    }

    [Fact]
    public void Sort_64Elements_AllAscending_IsSorted()
    {
        var list = new List<int>();
        for (int i = 0; i < 64; i++)
        {
            list.Add(i);
        }

        TimSort.Sort(list, (a, b) => a.CompareTo(b));
        AssertSorted(list);
    }

    [Fact]
    public void Sort_64Elements_AllDescending_IsSorted()
    {
        var list = new List<int>();
        for (int i = 63; i >= 0; i--)
        {
            list.Add(i);
        }

        TimSort.Sort(list, (a, b) => a.CompareTo(b));
        AssertSorted(list);
    }

    [Fact]
    public void Sort_Stable_EqualKeysKeepInsertionOrder()
    {
        var list = new List<(int Key, int Seq)>();
        for (int i = 0; i < 200; i++)
        {
            list.Add((i % 7, i));
        }

        TimSort.Sort(list, (a, b) => a.Key.CompareTo(b.Key));
        int lastKey = -1;
        int lastSeq = -1;
        foreach ((int key, int seq) in list)
        {
            Assert.True(key >= lastKey);
            if (key == lastKey)
            {
                Assert.True(seq > lastSeq, "equal keys reordered (unstable sort)");
            }

            lastKey = key;
            lastSeq = seq;
        }
    }

    [Fact]
    public void Sort_MatchesReferenceStableSort_AcrossRandomInputs()
    {
        // Differential check against LINQ OrderBy (stable) over many shapes: sizes straddling the 64 threshold, random contents, and injected short runs (the
        // trigger).
        var rng = new Random(12345);
        for (int trial = 0; trial < 500; trial++)
        {
            int size = rng.Next(0, 300);
            var input = new List<int>(size);
            for (int i = 0; i < size; i++)
            {
                input.Add(rng.Next(-50, 50));
            }

            if (trial % 3 == 0 && size >= 64)
            {
                // Inject a short natural run in the middle, as the repro does.
                int pos = rng.Next(10, size - 20);
                input.RemoveRange(pos, 4);
                input.InsertRange(pos, new[] { -100 + trial, -99 + trial, -98 + trial, -97 + trial });
            }

            var expected = input.OrderBy(x => x).ToList();
            TimSort.Sort(input, (a, b) => a.CompareTo(b));
            Assert.Equal(expected, input);
        }
    }
}
