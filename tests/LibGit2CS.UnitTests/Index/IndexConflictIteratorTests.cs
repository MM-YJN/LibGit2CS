using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Index;

/// <summary>
/// Tests for <see cref="GitIndex.EnumerateConflicts"/> — the managed port of
/// <c>git_index_conflict_iterator</c>/<c>git_index_conflict_next</c>
/// (index.c:2073-2128) over <c>index_conflict__get_byindex</c>
/// (index.c:1888-1937): stage grouping, stage-0 skipping, missing sides,
/// empty indexes, and case-insensitive path boundaries.
/// </summary>
public sealed class IndexConflictIteratorTests
{
    private static GitIndexEntry Entry(string path, int stage)
        => new GitIndexEntry(path, GitOid.Empty, GitFileMode.Regular).WithStage(stage);

    [Fact]
    public void EnumerateConflicts_TwoPathsInterleavedWithStageZero_YieldsTwoTriples()
    {
        using var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        index.Add(Entry("a.txt", 1));
        index.Add(Entry("a.txt", 2));
        index.Add(Entry("a.txt", 3));
        index.Add(Entry("clean.txt", 0));
        index.Add(Entry("b.txt", 1));
        index.Add(Entry("b.txt", 2));
        index.Add(Entry("b.txt", 3));

        var triples =
            index.EnumerateConflicts().ToList();

        // Stage-0 entry is skipped; the two conflict paths come out in
        // path-sorted order (a.txt before b.txt).
        Assert.Equal(2, triples.Count);
        Assert.Equal("a.txt", triples[0].Ancestor!.Value.Path.ToUtf8String());
        Assert.Equal(1, triples[0].Ancestor!.Value.Stage);
        Assert.Equal(2, triples[0].Ours!.Value.Stage);
        Assert.Equal(3, triples[0].Theirs!.Value.Stage);
        Assert.Equal("b.txt", triples[1].Ancestor!.Value.Path.ToUtf8String());
        Assert.NotNull(triples[1].Ours);
        Assert.NotNull(triples[1].Theirs);
    }

    [Fact]
    public void EnumerateConflicts_MissingSide_YieldsNull()
    {
        using var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        index.Add(Entry("partial.txt", 1));
        index.Add(Entry("partial.txt", 2));

        (GitIndexEntry? Ancestor, GitIndexEntry? Ours, GitIndexEntry? Theirs) triple =
            Assert.Single(index.EnumerateConflicts());

        Assert.NotNull(triple.Ancestor);
        Assert.NotNull(triple.Ours);
        Assert.Null(triple.Theirs);
    }

    [Fact]
    public void EnumerateConflicts_NoConflicts_YieldsEmpty()
    {
        using var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        index.Add(Entry("clean.txt", 0));

        Assert.Empty(index.EnumerateConflicts());
    }

    [Fact]
    public void EnumerateConflicts_IgnoreCase_GroupedByCaseInsensitivePath()
    {
        using var index = GitIndex.New(GitHashAlgorithmKind.Sha1);
        index.IgnoreCase = true;
        index.Add(Entry("MiXeD.txt", 1));
        index.Add(Entry("mixed.txt", 2));
        index.Add(Entry("mixed.txt", 3));

        // entries_cmp_path with the index's case-sensitivity folds ASCII
        // case, so "MiXeD.txt" and "mixed.txt" belong to one conflict group.
        (GitIndexEntry? Ancestor, GitIndexEntry? Ours, GitIndexEntry? Theirs) triple =
            Assert.Single(index.EnumerateConflicts());

        Assert.NotNull(triple.Ancestor);
        Assert.NotNull(triple.Ours);
        Assert.NotNull(triple.Theirs);
    }
}
