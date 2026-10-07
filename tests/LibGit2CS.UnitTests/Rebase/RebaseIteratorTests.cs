using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitRebaseOperationType = LibGit2CS.Rebase.GitRebaseOperationType;
using RebaseOps = LibGit2CS.Rebase.GitRebase;
using RebaseOpts = LibGit2CS.Rebase.GitRebaseOptions;

namespace LibGit2CS.UnitTests.Rebase;

/// <summary>
/// Rebase iterator tests. Mirrors libgit2's <c>tests/libgit2/rebase/iterator.c</c>.
/// </summary>
public sealed class RebaseIteratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public RebaseIteratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RebaseIter_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static readonly GitOid[] s_expectedOids =
    [
        RebaseTestHelpers.Oid("da9c51a23d02d931a486f45ad18cda05cf5d2b94"),
        RebaseTestHelpers.Oid("8d1f13f93c4995760ac07d129246ac1ff64c0be9"),
        RebaseTestHelpers.Oid("3069cc907e6294623e5917ef6de663928c1febfb"),
        RebaseTestHelpers.Oid("588e5d2f04d49707fe4aab865e1deacaf7ef6787"),
        RebaseTestHelpers.Oid("b146bd7608eac53d9bf9e1a6963543588b555c64"),
    ];

    private static readonly GitOid[] s_expectedCommitOids =
    [
        RebaseTestHelpers.Oid("776e4c48922799f903f03f5f6e51da8b01e4cce0"),
        RebaseTestHelpers.Oid("ba1f9b4fd5cf8151f7818be2111cc0869f1eb95a"),
        RebaseTestHelpers.Oid("948b12fe18b84f756223a61bece4c307787cd5d4"),
        RebaseTestHelpers.Oid("d9d5d59d72c9968687f9462578d79878cd80e781"),
        RebaseTestHelpers.Oid("9cf383c0a125d89e742c5dec58ed277dd07588b3"),
    ];

    private static void AssertOperations(RebaseOps rebase, ulong expectedCurrent)
    {
        Assert.Equal(5, rebase.OperationCount);
        Assert.Equal(expectedCurrent, rebase.CurrentOperation);

        for (int i = 0; i < 5; i++)
        {
            LibGit2CS.Rebase.GitRebaseOperation op = rebase[i];
            Assert.Equal(GitRebaseOperationType.Pick, op.Type);
            Assert.Equal(s_expectedOids[i], op.Id);
            Assert.Null(op.Exec);
        }
    }

    private async Task TestIterator(bool inMemory)
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();

        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        var opts = new RebaseOpts { InMemory = inMemory };
        RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, opts);
        AssertOperations(rebase, RebaseOps.NoOperation);

        if (!inMemory)
        {
            rebase.Dispose();
            rebase = await RebaseOps.OpenAsync(repo, cancellationToken: TestContext.Current.CancellationToken);
        }

        // Step 1.
        LibGit2CS.Rebase.GitRebaseOperation op = await rebase.NextAsync();
        GitOid commitId = await rebase.CommitAsync(null, sig);
        AssertOperations(rebase, 0);
        Assert.Equal(s_expectedCommitOids[0], commitId);

        // Step 2.
        op = await rebase.NextAsync();
        commitId = await rebase.CommitAsync(null, sig);
        AssertOperations(rebase, 1);
        Assert.Equal(s_expectedCommitOids[1], commitId);

        // Step 3.
        op = await rebase.NextAsync();
        commitId = await rebase.CommitAsync(null, sig);
        AssertOperations(rebase, 2);
        Assert.Equal(s_expectedCommitOids[2], commitId);

        if (!inMemory)
        {
            rebase.Dispose();
            rebase = await RebaseOps.OpenAsync(repo, cancellationToken: TestContext.Current.CancellationToken);
        }

        // Step 4.
        op = await rebase.NextAsync();
        commitId = await rebase.CommitAsync(null, sig);
        AssertOperations(rebase, 3);
        Assert.Equal(s_expectedCommitOids[3], commitId);

        // Step 5.
        op = await rebase.NextAsync();
        commitId = await rebase.CommitAsync(null, sig);
        AssertOperations(rebase, 4);
        Assert.Equal(s_expectedCommitOids[4], commitId);

        // IterOver.
        await Assert.ThrowsAsync<GitException>(async () => await rebase.NextAsync());
        AssertOperations(rebase, 4);

        rebase.Dispose();
    }

    [Fact]
    public async Task Iterates()
    {
        await TestIterator(inMemory: false);
    }

    [Fact]
    public async Task IteratesInMemory()
    {
        await TestIterator(inMemory: true);
    }
}
