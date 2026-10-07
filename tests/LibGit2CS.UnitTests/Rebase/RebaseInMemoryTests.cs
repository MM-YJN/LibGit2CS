using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitRebaseOperationType = LibGit2CS.Rebase.GitRebaseOperationType;
using RebaseOps = LibGit2CS.Rebase.GitRebase;
using RebaseOpts = LibGit2CS.Rebase.GitRebaseOptions;

namespace LibGit2CS.UnitTests.Rebase;

/// <summary>
/// In-memory rebase tests. Mirrors libgit2's <c>tests/libgit2/rebase/inmemory.c</c>.
/// </summary>
public sealed class RebaseInMemoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public RebaseInMemoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RebaseInMem_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async ValueTask<GitRepository> OpenRebaseRepoAsync()
    {
        string repoPath = RebaseTestHelpers.OpenRebaseRepo(_extractedPaths);
        GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext());
        await repo.Config.SetBoolAsync("core.autocrlf", false);
        return repo;
    }

    [Fact]
    public async Task NotInRebaseState()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        var opts = new RebaseOpts { InMemory = true };
        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, opts, cancellationToken: TestContext.Current.CancellationToken);

        // In-memory rebase does not change repository state.
        Assert.Equal(RepositoryState.None, repo.State);
    }

    [Fact]
    public async Task CanResolveConflicts()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/asparagus");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        var opts = new RebaseOpts { InMemory = true };
        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, opts, cancellationToken: TestContext.Current.CancellationToken);

        LibGit2CS.Rebase.GitRebaseOperation op = await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitRebaseOperationType.Pick, op.Type);
        Assert.Equal(RebaseTestHelpers.Oid("33f915f9e4dbd9f4b24430e48731a59b45b15500"), op.Id);

        // The in-memory index should have conflicts.
        GitIndex rebaseIndex = rebase.GetInMemoryIndex();
        Assert.True(rebaseIndex.HasConflicts);

        // Commit should fail with Unmerged.
        await Assert.ThrowsAsync<GitException>(async () => await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken));

        // Resolve the conflict: remove conflict entries and add the resolution.
        rebaseIndex.ConflictRemove("asparagus.txt");
        rebaseIndex.Add(new GitIndexEntry("asparagus.txt",
            RebaseTestHelpers.Oid("414dfc71ead79c07acd4ea47fecf91f289afc4b9"),
            GitFileMode.Regular));

        // Now commit should succeed.
        GitOid commitId = await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RebaseTestHelpers.Oid("db7af47222181e548810da2ab5fec0e9357c5637"), commitId);
    }

    [Fact]
    public async Task NoCommonAncestor()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/barley");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        var opts = new RebaseOpts { InMemory = true };
        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, opts, cancellationToken: TestContext.Current.CancellationToken);

        GitOid commitId = default;
        for (int i = 0; i < 5; i++)
        {
            await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
            commitId = await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);
        }

        await rebase.FinishAsync(sig, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RebaseTestHelpers.Oid("71e7ee8d4fe7d8bf0d107355197e0a953dfdb7f3"), commitId);
    }

    [Fact]
    public async Task WithDirectories()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/deep_gravy");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/veal");

        var opts = new RebaseOpts { InMemory = true };
        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, opts, cancellationToken: TestContext.Current.CancellationToken);

        await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid commitId = await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);

        await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        commitId = await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);

        // Should be done (2 operations).
        await Assert.ThrowsAsync<GitException>(async () => await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken));

        // Verify the final tree OID.
        Commit? commit = await repo.ObjectLookupAsync<Commit>(commitId, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        Assert.Equal(RebaseTestHelpers.Oid("a4d6d9c3d57308fd8e320cf2525bae8f1adafa57"), commit.Tree);
    }
}
