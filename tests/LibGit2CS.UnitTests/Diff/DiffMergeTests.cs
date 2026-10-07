using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitDiff = LibGit2CS.Diff.GitDiff;
using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Regression tests for <see cref="GitDiff.Merge"/> and
/// <see cref="GitDiff.TreeToWorkdirWithIndexAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these guard against.</b> <see cref="GitDiff.Merge"/> delegates to
/// <c>DiffTransform.Merge</c>, which mutates the generator's
/// <c>DeltaList</c> in place (sorted merge by path changes the count +
/// ordering). <see cref="GitDiff.Merge"/> must refresh
/// the <see cref="GitDiff"/> wrapper's <c>_deltas</c> snapshot afterwards, or
/// <see cref="GitDiff.DeltaCount"/>, <see cref="GitDiff.Deltas"/>, and
/// <see cref="GitDiff.GetDelta"/> return the stale pre-merge list. The
/// <see cref="GitDiff.TreeToWorkdirWithIndexAsync"/> facade — which builds a
/// tree-to-index diff, an index-to-workdir diff, and merges them — would
/// otherwise silently drop deltas that were only present in the
/// second (index-to-workdir) sub-diff.
/// </para>
/// <para>
/// <b>Test 1</b> is a 1:1 port of libgit2's
/// <c>test_diff_tree__merge</c> (<c>tests/libgit2/diff/tree.c:212</c>): two
/// tree-to-tree diffs (a→b and c→b) are merged, and the merged result must
/// contain 6 files (2 added, 1 deleted, 3 modified), 6 hunks, 59 lines.
/// This is the exact scenario libgit2's suite uses to validate
/// <c>git_diff_merge</c>.
/// </para>
/// <para>
/// <b>Test 2</b> reproduces the integration path: a staged change to
/// <c>fileA.txt</c> (visible only in tree-to-index) and an unstaged change
/// to <c>fileB.txt</c> (visible only in index-to-workdir) must both appear
/// in the <see cref="GitDiff.TreeToWorkdirWithIndexAsync"/> result; a stale
/// snapshot would report only <c>fileA.txt</c>.
/// </para>
/// </remarks>
public sealed class DiffMergeTests : DiffGoldenBase
{
    /// <summary>
    /// <see cref="GitDiff.Merge"/> combines two tree-to-tree diffs and the
    /// merged <see cref="GitDiff"/> exposes the union of both deltas via
    /// <see cref="GitDiff.DeltaCount"/>/<see cref="GitDiff.Deltas"/>/<see cref="GitDiff.GetDelta"/>.
    /// </summary>
    /// <remarks>
    /// Ported from libgit2's <c>test_diff_tree__merge</c>
    /// (<c>tests/libgit2/diff/tree.c:212-253</c>). The attr fixture has
    /// three commits used here: <c>a=605812a</c>, <c>b=370fe9e</c>,
    /// <c>c=f5b0af1</c>. Diff1 = a→b, Diff2 = c→b; merging them yields
    /// 6 deltas (2 added, 1 deleted, 3 modified), 6 hunks, 59 lines
    /// (1 context, 36 additions, 22 deletions).
    /// <para>
    /// The public API must report the merged count (6): a stale snapshot
    /// would keep <see cref="GitDiff.DeltaCount"/> at Diff1's count (5) and
    /// hide the merged-in deltas from Diff2.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Merge_TwoTreeToTreeDiffs_ExposesCombinedDeltasViaPublicApi()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("attr");
        CancellationToken ct = TestContext.Current.CancellationToken;

        GitTree a = await ResolveTreeAsync(repo, "605812ab7fe421fdd325a935d35cb06a9234a7d7");
        GitTree b = await ResolveTreeAsync(repo, "370fe9ec224ce33e71f9e5ec2bd1142ce9937a6a");
        GitTree c = await ResolveTreeAsync(repo, "f5b0af1fb4f5c0cd7aad880711d368a07333c307");

        using GitDiff diff1 = await repo.DiffTreeToTreeAsync(a, b, cancellationToken: ct);
        using GitDiff diff2 = await repo.DiffTreeToTreeAsync(c, b, cancellationToken: ct);

        // Pre-merge invariants: each diff is non-empty.
        Assert.True(diff1.DeltaCount > 0);
        Assert.True(diff2.DeltaCount > 0);
        int preMergeCount = diff1.DeltaCount;

        diff1.Merge(diff2);

        // Post-merge: the public API must reflect the merged result, not the
        // stale pre-merge snapshot. This is the regression assertion: a stale
        // snapshot would keep DeltaCount at preMergeCount.
        Assert.NotEqual(preMergeCount, diff1.DeltaCount);

        // Exact counts from libgit2's test_diff_tree__merge (tree.c:240-250).
        DiffCounter counter = await DiffCounter.CountAsync(diff1);
        Assert.Equal(6, counter.Files);
        Assert.Equal(2, counter[GitDeltaStatus.Added]);
        Assert.Equal(1, counter[GitDeltaStatus.Deleted]);
        Assert.Equal(3, counter[GitDeltaStatus.Modified]);
        Assert.Equal(6, counter.Hunks);
        Assert.Equal(59, counter.Lines);
        Assert.Equal(1, counter.LineContext);
        Assert.Equal(36, counter.LineAdds);
        Assert.Equal(22, counter.LineDels);
    }

    /// <summary>
    /// <see cref="GitDiff.TreeToWorkdirWithIndexAsync"/> merges a
    /// tree-to-index diff (staged changes) with an index-to-workdir diff
    /// (unstaged changes). A delta present only in the index-to-workdir
    /// sub-diff must be visible in the merged result.
    /// </summary>
    /// <remarks>
    /// This reproduces the exact integration path: a
    /// staged change to <c>fileA.txt</c> (caught by tree-to-index) and an
    /// unstaged change to <c>fileB.txt</c> (caught only by index-to-workdir).
    /// A stale <c>_deltas</c> snapshot left in place after
    /// <c>DiffTransform.Merge</c> mutated the generator's delta list would
    /// make <see cref="GitDiff.TreeToWorkdirWithIndexAsync"/>
    /// return only <c>fileA.txt</c>.
    /// </remarks>
    [Fact]
    public async Task TreeToWorkdirWithIndex_StagedAndUnstagedChanges_BothReported()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeRegression_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            GitOid rootOid;
            await using (GitRepository repo = await GitRepository.InitAsync(tempDir, isBare: false, Context, cancellationToken: ct))
            {
                string workdir = repo.Workdir!;
                await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "line1\nline2\nline3\n", ct);
                await File.WriteAllTextAsync(Path.Combine(workdir, "fileB.txt"), "b-original\n", ct);
                GitIndex index = await repo.GetIndexAsync(ct);
                await index.AddByPathAsync("fileA.txt", ct);
                await index.AddByPathAsync("fileB.txt", ct);
                await index.WriteAsync(ct);
                GitOid treeOid = await index.WriteTreeAsync(ct);
                rootOid = await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = new GitSignature("t", "t@t", new GitTime(1700000000, 0)),
                    Committer = new GitSignature("t", "t@t", new GitTime(1700000000, 0)),
                    Message = "init\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
                await repo.SetHeadAsync("refs/heads/main", ct);
            }

            await using GitRepository repo2 = await GitRepository.OpenAsync(tempDir, Context, cancellationToken: ct);
            string workdir2 = repo2.Workdir!;

            // Unstaged change to fileB.txt (different size so stat-based
            // change detection always catches it, independent of timestamp
            // resolution — matches the pattern in DiffTreeToWorkdirZeroStatTests).
            await File.WriteAllTextAsync(Path.Combine(workdir2, "fileB.txt"), "b-modified-different-size\n", ct);

            // Staged change to fileA.txt.
            await File.WriteAllTextAsync(Path.Combine(workdir2, "fileA.txt"), "line1\nSTAGED\nline3\n", ct);
            GitIndex idx2 = await repo2.GetIndexAsync(ct);
            await idx2.AddByPathAsync("fileA.txt", ct);
            await idx2.WriteAsync(ct);

            // Resolve HEAD tree for the tree-to-index side of the merge.
            GitReference? head = await repo2.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(head);
            GitDirectReference direct = Assert.IsType<GitDirectReference>(head);
            Commit? rootCommit = await repo2.ObjectLookupAsync<Commit>(rootOid, ct);
            Assert.NotNull(rootCommit);
            GitTree? headTree = await repo2.ObjectLookupAsync<GitTree>(rootCommit!.Tree, ct);
            Assert.NotNull(headTree);

            using GitDiff diff = await repo2.DiffTreeToWorkdirWithIndexAsync(headTree, cancellationToken: ct);

            // Both files must appear in the merged result. A stale snapshot
            // would keep only fileA.txt (the tree-to-index side) and drop
            // fileB.txt (the index-to-workdir side).
            Assert.Equal(2, diff.DeltaCount);
            var paths = diff.Deltas.Select(d => d.Path.ToUtf8String()).ToHashSet();
            Assert.Contains("fileA.txt", paths);
            Assert.Contains("fileB.txt", paths);
            Assert.All(diff.Deltas, d => Assert.Equal(GitDeltaStatus.Modified, d.Status));
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException) { }
        }
    }
}
