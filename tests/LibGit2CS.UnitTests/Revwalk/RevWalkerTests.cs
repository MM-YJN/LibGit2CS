using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.UnitTests.Revwalk;

public sealed class RevWalkerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public RevWalkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RevWalkerTests_" + Guid.NewGuid().ToString("N")[..8]);
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

    private string ExtractRepo(string zipFileName)
    {
        string path = FixtureLoader.ExtractTreeToTemp($"Fixtures/repo/{zipFileName}");
        _extractedPaths.Add(path);
        return path;
    }

    private async ValueTask<GitRepository> OpenTestRepoAsync()
    {
        string extractedPath = ExtractRepo("testrepo.zip");
        return await GitRepository.OpenAsync(Path.Combine(extractedPath, "testrepo.git"), new GitContext());
    }

    [Fact]
    public async Task Walk_PushHead_NoSort_YieldsCommits()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        using GitRevWalker walker = repo.NewRevWalker();

        await walker.PushHeadAsync(cancellationToken: TestContext.Current.CancellationToken);

        List<GitOid> commits = await walker.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(commits);
        // HEAD → a65fedf39aefe402d3bb6e24df4d4f5fe4547750 should be reachable.
        var headOid = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);
        Assert.Contains(headOid, commits);
    }

    [Fact]
    public async Task Walk_TopologicalSort_ChildrenBeforeParents()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        using GitRevWalker walker = repo.NewRevWalker();

        walker.Sort = GitSortMode.Topological;
        await walker.PushHeadAsync(cancellationToken: TestContext.Current.CancellationToken);

        List<GitOid> commits = await walker.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(commits);

        // Verify topological invariant: every parent appears after its child.
        var positions = commits.Select((oid, idx) => (oid, idx)).ToDictionary(x => x.oid, x => x.idx);
        for (int i = 0; i < commits.Count; i++)
        {
            Commit? commit = await repo.ObjectLookupAsync<Commit>(commits[i], TestContext.Current.CancellationToken);
            Assert.NotNull(commit);
            foreach (GitOid parentId in commit!.Parents)
            {
                if (positions.TryGetValue(parentId, out int parentIdx))
                {
                    Assert.True(parentIdx > i, $"parent {parentId} must come after child at index {i}");
                }
            }
        }
    }

    [Fact]
    public async Task Walk_TimeSort_NewestFirst()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        using GitRevWalker walker = repo.NewRevWalker();

        walker.Sort = GitSortMode.Time;
        await walker.PushHeadAsync(cancellationToken: TestContext.Current.CancellationToken);

        List<GitOid> commits = await walker.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(commits);

        // Verify time-order invariant: timestamps are non-increasing.
        long prevTime = long.MaxValue;
        foreach (GitOid oid in commits)
        {
            Commit? commit = await repo.ObjectLookupAsync<Commit>(oid, TestContext.Current.CancellationToken);
            Assert.NotNull(commit);
            Assert.True(commit!.Time.Seconds <= prevTime, $"time sort: {commit.Time.Seconds} > {prevTime}");
            prevTime = commit.Time.Seconds;
        }
    }

    [Fact]
    public async Task Walk_ReverseSort_FlipsOrder()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitOid[] forward;
        using (GitRevWalker w1 = repo.NewRevWalker())
        {
            w1.Sort = GitSortMode.Time;
            await w1.PushHeadAsync(cancellationToken: TestContext.Current.CancellationToken);
            forward = (await w1.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).ToArray();
        }

        GitOid[] reverse;
        using (GitRevWalker w2 = repo.NewRevWalker())
        {
            w2.Sort = GitSortMode.Time | GitSortMode.Reverse;
            await w2.PushHeadAsync(cancellationToken: TestContext.Current.CancellationToken);
            reverse = (await w2.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).ToArray();
        }

        Assert.Equal(forward, reverse.Reverse());
    }

    [Fact]
    public async Task Walk_Hide_RemovesAncestors()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        var headOid = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);

        int fullCount;
        using (GitRevWalker w1 = repo.NewRevWalker())
        {
            await w1.PushHeadAsync(cancellationToken: TestContext.Current.CancellationToken);
            fullCount = (await w1.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Count;
        }

        // Hide HEAD itself → walk should be empty.
        using GitRevWalker w2 = repo.NewRevWalker();
        await w2.PushHeadAsync(cancellationToken: TestContext.Current.CancellationToken);
        await w2.HideAsync(headOid, TestContext.Current.CancellationToken);
        Assert.Empty(await w2.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(fullCount > 0);
    }

    [Fact]
    public async Task Walk_PushRange_RangeLimitsResults()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        var headOid = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);

        using GitRevWalker walker = repo.NewRevWalker();
        walker.Sort = GitSortMode.Topological;

        // Push the HEAD commit, then hide its first parent's range to get just HEAD.
        Commit? head = await repo.ObjectLookupAsync<Commit>(headOid, TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.True(head!.Parents.Count > 0);

        GitOid parentId = head.Parents[0];

        // HEAD..HEAD would be empty; use parentId..headOid to get just HEAD.
        // Find the short-form: walk from headOid hiding parentId.
        await walker.PushAsync(headOid, TestContext.Current.CancellationToken);
        await walker.HideAsync(parentId, TestContext.Current.CancellationToken);

        List<GitOid> commits = await walker.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(commits);
        Assert.Equal(headOid, commits[0]);
    }

    [Fact]
    public async Task Walk_SimplifyFirstParent_FewerCommits()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        int fullCount;
        using (GitRevWalker w1 = repo.NewRevWalker())
        {
            await w1.PushHeadAsync(cancellationToken: TestContext.Current.CancellationToken);
            fullCount = (await w1.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Count;
        }

        int firstParentCount;
        using (GitRevWalker w2 = repo.NewRevWalker())
        {
            w2.SimplifyFirstParent();
            await w2.PushHeadAsync(cancellationToken: TestContext.Current.CancellationToken);
            firstParentCount = (await w2.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Count;
        }

        Assert.True(firstParentCount <= fullCount);
        Assert.True(firstParentCount > 0);
    }

    [Fact]
    public async Task Walk_NoPushes_YieldsNothing()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        using GitRevWalker walker = repo.NewRevWalker();

        Assert.Empty(await walker.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Walk_PushRef_ResolvesRefName()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        using GitRevWalker walker = repo.NewRevWalker();

        await walker.PushRefAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        List<GitOid> commits = await walker.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(commits);

        var headOid = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);
        Assert.Equal(headOid, commits[0]);
    }

    [Fact]
    public async Task Walk_PushGlob_MatchesMultipleRefs()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        using GitRevWalker walker = repo.NewRevWalker();

        // Glob "heads/*" should match all branches.
        await walker.PushGlobAsync("heads/*", cancellationToken: TestContext.Current.CancellationToken);

        List<GitOid> commits = await walker.WalkAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(commits);

        // master's tip should be present.
        var headOid = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);
        Assert.Contains(headOid, commits);
    }
}
