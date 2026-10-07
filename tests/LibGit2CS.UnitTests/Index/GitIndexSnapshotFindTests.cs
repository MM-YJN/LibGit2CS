using LibGit2CS.Index;
using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.Index;

/// <summary>
/// Tests for <see cref="GitIndex.SnapshotFind"/> — the binary search over a
/// sorted index snapshot that ports <c>git_index_snapshot_find</c> +
/// <c>git_vector_bsearch2</c>.
/// </summary>
public sealed class GitIndexSnapshotFindTests
{
    // SnapshotFind assumes its input is sorted by the same (path, stage)
    // comparator it searches with (mirroring how Snapshot() yields a sorted
    // array). This helper reproduces that sort so tests feed valid snapshots.
    private static GitIndexEntry[] BuildSorted(bool ignoreCase, params (string Path, int Stage)[] items)
    {
        var entries = new GitIndexEntry[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            entries[i] = new GitIndexEntry(items[i].Path, default, default).WithStage(items[i].Stage);
        }

        Array.Sort(entries, (a, b) =>
        {
            int d = GitPath.Compare(a.Path, b.Path, ignoreCase);
            return d != 0 ? d : a.Stage - b.Stage;
        });
        return entries;
    }

    private static int LinearFind(GitIndexEntry[] snapshot, string path, int stage, bool ignoreCase)
        => Array.FindIndex(snapshot,
            e => GitPath.Compare(e.Path, GitPath.FromUtf8String(path), ignoreCase) == 0 && e.Stage == stage);

    [Fact]
    public void CaseSensitive_Hit_ReturnsMatchingIndex()
    {
        GitIndexEntry[] snap = BuildSorted(false,
            ("a.txt", 0), ("foo.txt", 0), ("m.txt", 0), ("z.txt", 0));

        int idx = GitIndex.SnapshotFind(snap, GitPath.FromUtf8String("m.txt"), stage: 0, ignoreCase: false);

        Assert.Equal(LinearFind(snap, "m.txt", 0, false), idx);
        Assert.True(idx >= 0);
        Assert.Equal("m.txt", snap[idx].Path.ToUtf8String());
        Assert.Equal(0, snap[idx].Stage);
    }

    [Fact]
    public void CaseSensitive_Miss_ReturnsMinusOne()
    {
        GitIndexEntry[] snap = BuildSorted(false,
            ("a.txt", 0), ("foo.txt", 0), ("m.txt", 0), ("z.txt", 0));

        int idx = GitIndex.SnapshotFind(snap, GitPath.FromUtf8String("nope.txt"), stage: 0, ignoreCase: false);

        Assert.Equal(-1, idx);
    }

    [Fact]
    public void CaseSensitive_CaseMismatch_IsMiss()
    {
        GitIndexEntry[] snap = BuildSorted(false, ("foo.txt", 0));

        int idx = GitIndex.SnapshotFind(snap, GitPath.FromUtf8String("FOO.TXT"), stage: 0, ignoreCase: false);

        Assert.Equal(-1, idx);
    }

    [Fact]
    public void CaseInsensitive_FoldsCaseForHit()
    {
        GitIndexEntry[] snap = BuildSorted(true, ("bar.txt", 0), ("foo.txt", 0), ("qux.txt", 0));

        int idx = GitIndex.SnapshotFind(snap, GitPath.FromUtf8String("FOO.TXT"), stage: 0, ignoreCase: true);

        Assert.True(idx >= 0);
        Assert.Equal("foo.txt", snap[idx].Path.ToUtf8String());
    }

    [Fact]
    public void StageDisambiguation_ReturnsEntryAtRequestedStage()
    {
        // Same path at stages 0 and 2; sorted order is (path, stage) so stage 0
        // precedes stage 2. Searching stage 0 must not land on stage 2.
        GitIndexEntry[] snap = BuildSorted(false, ("dup", 0), ("dup", 2));

        int idx0 = GitIndex.SnapshotFind(snap, GitPath.FromUtf8String("dup"), stage: 0, ignoreCase: false);
        int idx2 = GitIndex.SnapshotFind(snap, GitPath.FromUtf8String("dup"), stage: 2, ignoreCase: false);
        int idx1 = GitIndex.SnapshotFind(snap, GitPath.FromUtf8String("dup"), stage: 1, ignoreCase: false);

        Assert.True(idx0 >= 0);
        Assert.Equal(0, snap[idx0].Stage);
        Assert.True(idx2 >= 0);
        Assert.Equal(2, snap[idx2].Stage);
        Assert.NotEqual(idx0, idx2);
        Assert.Equal(-1, idx1);
    }

    [Fact]
    public void EmptySnapshot_ReturnsMinusOne()
    {
        GitIndexEntry[] snap = Array.Empty<GitIndexEntry>();

        int idx = GitIndex.SnapshotFind(snap, GitPath.FromUtf8String("anything"), stage: 0, ignoreCase: false);

        Assert.Equal(-1, idx);
    }

    [Fact]
    public void LargeSnapshot_MatchesLinearOracle()
    {
        // Exercises binary-search mid calculations across many entries.
        var items = new (string, int)[200];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = ($"path_{i:D4}.txt", 0);
        }

        GitIndexEntry[] snap = BuildSorted(false, items);

        // Probe a spread of positions, some present and some absent.
        foreach (string probe in new[] { "path_0000.txt", "path_0099.txt", "path_0199.txt", "path_0200.txt", "aaaa.txt" })
        {
            int idx = GitIndex.SnapshotFind(snap, GitPath.FromUtf8String(probe), stage: 0, ignoreCase: false);
            Assert.Equal(LinearFind(snap, probe, 0, false), idx);
        }
    }
}
