using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using RebaseOps = LibGit2CS.Rebase.GitRebase;

namespace LibGit2CS.UnitTests.Rebase;

/// <summary>
/// Rebase abort tests. Mirrors libgit2's <c>tests/libgit2/rebase/abort.c</c>.
/// </summary>
public sealed class RebaseAbortTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public RebaseAbortTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RebaseAbort_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static async Task EnsureAborted(GitRepository repo, GitAnnotatedCommit branch)
    {
        Assert.Equal(RepositoryState.None, repo.State);

        // HEAD should point at the original branch.
        GitReference? head = await repo.ReferenceLookupAsync("HEAD");
        Assert.NotNull(head);

        if (branch.Ref is null)
        {
            // Detached: HEAD is a direct ref pointing at branch.Id.
            var directHead = head as GitDirectReference;
            Assert.NotNull(directHead);
            Assert.Equal(branch.Id, directHead!.Target);
        }
        else
        {
            // Symbolic HEAD pointing at the original branch.
            var symHead = head as GitSymbolicReference;
            Assert.NotNull(symHead);
            Assert.Equal(branch.Ref, symHead!.TargetName);

            var branchRef = (await repo.ReferenceLookupAsync(branch.Ref!)) as GitDirectReference;
            Assert.NotNull(branchRef);
            Assert.Equal(branch.Id, branchRef!.Target);
        }
    }

    private static async Task TestAbort(GitRepository repo, GitAnnotatedCommit branch)
    {
        RebaseOps rebase = await RebaseOps.OpenAsync(repo, cancellationToken: TestContext.Current.CancellationToken);
        await rebase.AbortAsync();
        await EnsureAborted(repo, branch);
        rebase.Dispose();
    }

    [Fact]
    public async Task Merge()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit onto = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        // Init rebase: branch=beef, upstream=null, onto=master.
        using (RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, null, onto, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(RepositoryState.RebaseMerge, repo.State);
        }

        await TestAbort(repo, branch);
    }

    [Fact]
    public async Task MergeImmediatelyAfterInit()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit onto = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, null, onto, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RepositoryState.RebaseMerge, repo.State);

        await rebase.AbortAsync(cancellationToken: TestContext.Current.CancellationToken);
        await EnsureAborted(repo, branch);
        rebase.Dispose();
    }

    [Fact]
    public async Task MergeById()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        GitAnnotatedCommit branch = await repo.AnnotatedCommitLookupAsync(RebaseTestHelpers.Oid("b146bd7608eac53d9bf9e1a6963543588b555c64"), TestContext.Current.CancellationToken);
        GitAnnotatedCommit onto = await repo.AnnotatedCommitLookupAsync(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), TestContext.Current.CancellationToken);

        using (RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, null, onto, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(RepositoryState.RebaseMerge, repo.State);
        }

        await TestAbort(repo, branch);
    }

    [Fact]
    public async Task MergeByIdImmediatelyAfterInit()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        GitAnnotatedCommit branch = await repo.AnnotatedCommitLookupAsync(RebaseTestHelpers.Oid("b146bd7608eac53d9bf9e1a6963543588b555c64"), TestContext.Current.CancellationToken);
        GitAnnotatedCommit onto = await repo.AnnotatedCommitLookupAsync(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), TestContext.Current.CancellationToken);

        RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, null, onto, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RepositoryState.RebaseMerge, repo.State);

        await rebase.AbortAsync(cancellationToken: TestContext.Current.CancellationToken);
        await EnsureAborted(repo, branch);
        rebase.Dispose();
    }

    [Fact]
    public async Task DetachedHead()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        GitAnnotatedCommit branch = await repo.AnnotatedCommitLookupAsync(RebaseTestHelpers.Oid("b146bd7608eac53d9bf9e1a6963543588b555c64"), TestContext.Current.CancellationToken);
        GitAnnotatedCommit onto = await repo.AnnotatedCommitLookupAsync(RebaseTestHelpers.Oid("efad0b11c47cb2f0220cbd6f5b0f93bb99064b00"), TestContext.Current.CancellationToken);

        using (RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, null, onto, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(RepositoryState.RebaseMerge, repo.State);
        }

        await TestAbort(repo, branch);
    }

    [Fact]
    public async Task OldStyleHeadFile()
    {
        await using GitRepository repo = await OpenRebaseRepoAsync();
        Assert.Equal(RepositoryState.None, repo.State);

        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/beef");
        GitAnnotatedCommit onto = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/master");

        using (RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, null, onto, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(RepositoryState.RebaseMerge, repo.State);
        }

        // Rename orig-head to head (old git compat).
        string origHeadPath = Path.Combine(repo.Path, "rebase-merge", "orig-head");
        string headPath = Path.Combine(repo.Path, "rebase-merge", "head");
        if (File.Exists(origHeadPath))
        {
            File.Move(origHeadPath, headPath);
        }

        await TestAbort(repo, branch);
    }
}
