using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Merge;

/// <summary> Parity tests for the graph/merge-base error surface: C aborts with an error on a missing commit (graph.c mark_parents / merge.c merge_bases
/// on_error → bare -1 with the ODB message), never silently returning wrong counts or "no merge base". </summary>
public sealed class GraphMergeBaseParityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public GraphMergeBaseParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_GraphMergeBaseParity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private async Task<GitRepository> OpenMergeResolveRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures/merge/merge-resolve.zip");
        _extractedPaths.Add(path);
        return await GitRepository.OpenAsync(Path.Combine(path, "merge-resolve"), new GitContext());
    }

    // master = bd593285fc7fe4ca18ccdbabf027f5d689101452
    private static readonly GitOid s_master = GitOid.Parse("bd593285fc7fe4ca18ccdbabf027f5d689101452".AsSpan(), GitHashAlgorithmKind.Sha1);

    private static readonly GitOid s_missing = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1);

    private static void AssertOdbNotFound(GitException ex, GitOid oid)
    {
        // C: git_commit_list_parse fails → git_odb_read "object not found -
        // no match for id (...)" (GIT_ERROR_ODB); merge.c/graph.c on_error
        // return a bare -1 (GIT_ERROR) at the public boundary.
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
        Assert.Equal($"object not found - no match for id ({oid})", ex.Message);
    }

    // ── ahead/behind must abort on an unparseable commit ───────

    [Fact]
    public async Task AheadBehind_MissingLocal_Throws()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.AheadBehindAsync(s_missing, s_master, CancellationToken.None));
        AssertOdbNotFound(ex, s_missing);
    }

    [Fact]
    public async Task AheadBehind_MissingUpstream_Throws()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.AheadBehindAsync(s_master, s_missing, CancellationToken.None));
        AssertOdbNotFound(ex, s_missing);
    }

    // ── merge-base missing-commit error surface ────────────────

    [Fact]
    public async Task MergeBaseFind_MissingFirst_Throws()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        // C: git_commit_list_parse(walk, one) fails (merge.c:541-542) → -1.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.MergeBaseFindAsync(s_missing, s_master, CancellationToken.None));
        AssertOdbNotFound(ex, s_missing);
    }

    [Fact]
    public async Task MergeBaseFind_MissingSecond_Throws()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        // C: paint_down_to_common parses `two` (merge.c:401-402) → -1.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.MergeBaseFindAsync(s_master, s_missing, CancellationToken.None));
        AssertOdbNotFound(ex, s_missing);
    }

    [Fact]
    public async Task MergeBaseFindMany_MissingCommit_Throws()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.MergeBaseFindManyAsync([s_missing, s_master], CancellationToken.None));
        AssertOdbNotFound(ex, s_missing);
    }

    [Fact]
    public async Task MergeBaseFindAllMany_MissingCommit_Throws()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.MergeBaseFindAllManyAsync([s_missing, s_master], CancellationToken.None));
        AssertOdbNotFound(ex, s_missing);
    }

    // ── no merge base stays null (documented shape) ─

    [Fact]
    public async Task MergeBaseFind_DisjointHistories_ReturnsNull()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        // d6cf6c7 is a valid commit on an unrelated root — no common ancestor.
        var unrelated = GitOid.Parse("d6cf6c7741b3316826af1314042550c97ded1d50".AsSpan(), GitHashAlgorithmKind.Sha1);
        GitOid? mb = await repo.MergeBaseFindAsync(s_master, unrelated, CancellationToken.None);
        Assert.Null(mb);
    }

    // ── <2 commits → GIT_ERROR (-1) with class GIT_ERROR_INVALID ─

    [Fact]
    public async Task MergeBaseFindMany_LessThanTwo_ThrowsErrorInvalid()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.MergeBaseFindManyAsync([s_master], CancellationToken.None));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Equal("at least two commits are required to find an ancestor", ex.Message);
    }

    [Fact]
    public async Task MergeBaseFindAllMany_LessThanTwo_ThrowsErrorInvalid()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.MergeBaseFindAllManyAsync([s_master], CancellationToken.None));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Equal("at least two commits are required to find an ancestor", ex.Message);
    }

    [Fact]
    public async Task MergeBaseOctopus_LessThanTwo_ThrowsErrorInvalid()
    {
        await using GitRepository repo = await OpenMergeResolveRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.MergeBaseOctopusAsync([s_master], CancellationToken.None));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Equal("at least two commits are required to find an ancestor", ex.Message);
    }

    // ── equal-timestamp merge bases — stable paint order ──────

    [Fact]
    public async Task MergeBaseFindAll_EqualTimestampBases_ReturnsBothInStableOrder()
    {
        // Criss-cross merge: two branches B1/B2 (identical committer timestamp)
        // are merged twice (M, N). merge-base(M, N) = {B1, B2}. C's stable
        // insert_by_date (commit_list.c:62-75) preserves the paint order for
        // the equal timestamps, so bases[0] is deterministic via the stable
        // TimSort (not the unstable List<T>.Sort).
        string repoPath = Path.Combine(_tempDir, "crisscross");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Root commit A.
        GitOid a = await CommitFileAsync(repo, "a.txt", "a\n", "A\n", parents: [], time: 1000);

        // Two branches with the SAME timestamp.
        GitOid b1 = await CommitFileAsync(repo, "b1.txt", "b1\n", "B1\n", parents: [a], time: 2000);
        GitOid b2 = await CommitFileAsync(repo, "b2.txt", "b2\n", "B2\n", parents: [a], time: 2000);

        // Two merge commits over the same parents.
        GitOid m = await CommitFileAsync(repo, "m.txt", "m\n", "M\n", parents: [b1, b2], time: 3000);
        GitOid n = await CommitFileAsync(repo, "n.txt", "n\n", "N\n", parents: [b1, b2], time: 3000);

        IReadOnlyList<GitOid> bases = await repo.MergeBaseFindAllAsync(m, n, TestContext.Current.CancellationToken);

        // Both branches are non-redundant merge bases.
        Assert.Equal(2, bases.Count);
        Assert.Contains(b1, bases);
        Assert.Contains(b2, bases);

        // The chosen base (bases[0]) is deterministic — the same on every call.
        IReadOnlyList<GitOid> again = await repo.MergeBaseFindAllAsync(m, n, TestContext.Current.CancellationToken);
        Assert.Equal(bases[0], again[0]);
    }

    private static async Task<GitOid> CommitFileAsync(
        GitRepository repo,
        string path,
        string content,
        string message,
        IReadOnlyList<GitOid> parents,
        long time)
    {
        string workdir = repo.Workdir!;
        string fullPath = Path.Combine(workdir, path);
        await File.WriteAllTextAsync(fullPath, content, cancellationToken: TestContext.Current.CancellationToken);

        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync(path, TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);

        var sig = new GitSignature("t", "t@t", new GitTime(time, 0));
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = sig,
            Committer = sig,
            Message = message,
        }, TestContext.Current.CancellationToken);
    }
}
