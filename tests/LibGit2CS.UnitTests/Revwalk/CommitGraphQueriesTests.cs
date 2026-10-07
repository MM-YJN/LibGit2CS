using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Revwalk;

public sealed class CommitGraphQueriesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public CommitGraphQueriesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_GraphTests_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async ValueTask<GitRepository> OpenTestRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp($"Fixtures/repo/testrepo.zip");
        _extractedPaths.Add(path);
        return await GitRepository.OpenAsync(Path.Combine(path, "testrepo.git"), new GitContext());
    }

    // master tip = a65fedf39aefe402d3bb6e24df4d4f5fe4547750
    // Its first parent = be3563ae3f795b2b4353bcce3a527ad0a4f7f754 (known testrepo commit).
    private static readonly GitOid s_master = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);

    [Fact]
    public async Task DescendantOf_SameCommit_ReturnsFalse()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        Assert.False(await repo.DescendantOfAsync(s_master, s_master, CancellationToken.None));
    }

    [Fact]
    public async Task DescendantOf_AncestorIsReachable_ReturnsTrue()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // master tip's parent must be an ancestor of the master tip.
        Commit? head = await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.True(head!.Parents.Count > 0);

        GitOid parentId = head.Parents[0];

        // commit (master tip) is a descendant of ancestor (parent).
        Assert.True(await repo.DescendantOfAsync(s_master, parentId, CancellationToken.None));
    }

    [Fact]
    public async Task DescendantOf_ReversedArguments_ReturnsFalse()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        Commit? head = await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        GitOid parentId = head!.Parents[0];

        // The parent is NOT a descendant of the child.
        Assert.False(await repo.DescendantOfAsync(parentId, s_master, CancellationToken.None));
    }

    [Fact]
    public async Task AheadBehind_SameCommit_ReturnsZeroZero()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        (int ahead, int behind) = await repo.AheadBehindAsync(s_master, s_master, CancellationToken.None);
        Assert.Equal(0, ahead);
        Assert.Equal(0, behind);
    }

    [Fact]
    public async Task AheadBehind_LocalAheadOfUpstream_ReturnsNonZeroAhead()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        Commit? head = await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.True(head!.Parents.Count > 0);

        GitOid parentId = head.Parents[0];

        // local = master tip (1 ahead, 0 behind vs its parent).
        (int ahead, int behind) = await repo.AheadBehindAsync(s_master, parentId, CancellationToken.None);
        Assert.Equal(1, ahead);
        Assert.Equal(0, behind);
    }

    [Fact]
    public async Task AheadBehind_UpstreamAheadOfLocal_ReturnsNonZeroBehind()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        Commit? head = await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        GitOid parentId = head!.Parents[0];

        // Swapped: local = parent, upstream = master tip → 0 ahead, 1 behind.
        (int ahead, int behind) = await repo.AheadBehindAsync(parentId, s_master, CancellationToken.None);
        Assert.Equal(0, ahead);
        Assert.Equal(1, behind);
    }

    // ── ReachableFromAnyAsync ──────────────────────────────────────────

    [Fact]
    public async Task ReachableFromAny_NullDescendants_Throws()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => repo.ReachableFromAnyAsync(s_master, null!, CancellationToken.None));
    }

    [Fact]
    public async Task ReachableFromAny_EmptyDescendants_ReturnsFalse()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        Assert.False(await repo.ReachableFromAnyAsync(s_master, [], CancellationToken.None));
    }

    [Fact]
    public async Task ReachableFromAny_CommitInDescendants_ReturnsTrue()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        Assert.True(await repo.ReachableFromAnyAsync(s_master, [s_master], CancellationToken.None));
    }

    [Fact]
    public async Task ReachableFromAny_AncestorOfSingleDescendant_ReturnsTrue()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        Commit? head = await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.True(head!.Parents.Count > 0);
        GitOid parentId = head.Parents[0];

        // master tip's parent must be reachable from the master tip.
        Assert.True(await repo.ReachableFromAnyAsync(parentId, [s_master], CancellationToken.None));
    }

    [Fact]
    public async Task ReachableFromAny_NotAncestor_ReturnsFalse()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        Commit? head = await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        GitOid parentId = head!.Parents[0];

        // The parent is NOT reachable from the master tip's parent.
        Assert.False(await repo.ReachableFromAnyAsync(s_master, [parentId], CancellationToken.None));
    }

    [Fact]
    public async Task ReachableFromAny_AncestorOfMultipleDescendants_ReturnsTrue()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        Commit? head = await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        GitOid parentId = head!.Parents[0];

        // haacked tip = 258f0e2a959a364e40ed6603d5d44fbb24765b10 is another
        // testrepo branch whose history contains master's parent, so the
        // multi-descendant walk (not the equality short-circuit) is exercised.
        Assert.True(await repo.ReachableFromAnyAsync(parentId, [s_master, s_haacked], CancellationToken.None));
    }

    // haacked tip = a known descendant of master's parent (see above).
    private static readonly GitOid s_haacked = GitOid.Parse("258f0e2a959a364e40ed6603d5d44fbb24765b10".AsSpan(), GitHashAlgorithmKind.Sha1);
}
