using LibGit2CS.Blame;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Blame;

/// <summary>
/// Tests the managed exception contracts at the public blame entry point.
/// </summary>
public sealed class BlameErrorTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly GitContext _context = new();

    public BlameErrorTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_BlameErrors_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task Blame_UnresolvedHead_ThrowsReferenceError()
    {
        await using GitRepository repo = await InitRepoAsync("unresolved-head", isBare: true);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.BlameFileAsync("file.txt", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Reference, ex.Category);
        Assert.Equal("reference 'HEAD' not found", ex.Message);
    }

    [Fact]
    public async Task Blame_MissingNewestCommit_ThrowsObjectNotFound()
    {
        await using GitRepository repo = await InitRepoAsync("missing-newest", isBare: true);
        var missing = GitOid.Parse(
            "ffffffffffffffffffffffffffffffffffffffff".AsSpan(),
            GitHashAlgorithmKind.Sha1);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.BlameFileAsync(
                "file.txt",
                new GitBlameOptions { NewestCommit = missing },
                TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(GitErrorCategory.Object, ex.Category);
        Assert.Equal($"Commit {missing} not found", ex.Message);
    }

    [Fact]
    public async Task Blame_MissingPath_ThrowsObjectNotFound()
    {
        (GitRepository repo, GitOid commit) = await CreateRepoWithFilesAsync("missing-path");
        await using (repo)
        {
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await repo.BlameFileAsync(
                    "missing.txt",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(GitErrorCode.NotFound, ex.Code);
            Assert.Equal(GitErrorCategory.Object, ex.Category);
            Assert.Equal($"Blob at path 'missing.txt' not found in commit {commit}", ex.Message);
        }
    }

    [Fact]
    public async Task Blame_NonBlobPath_ThrowsObjectNotFound()
    {
        (GitRepository repo, GitOid commit) = await CreateRepoWithFilesAsync("non-blob-path");
        await using (repo)
        {
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await repo.BlameFileAsync(
                    "dir",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(GitErrorCode.NotFound, ex.Code);
            Assert.Equal(GitErrorCategory.Object, ex.Category);
            Assert.Equal($"Blob at path 'dir' not found in commit {commit}", ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _context.Dispose();

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async ValueTask<GitRepository> InitRepoAsync(string name, bool isBare)
    {
        string path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(path);
        return await GitRepository.InitAsync(
            path,
            isBare,
            _context,
            TestContext.Current.CancellationToken);
    }

    private async ValueTask<(GitRepository Repo, GitOid Commit)> CreateRepoWithFilesAsync(string name)
    {
        GitRepository repo = await InitRepoAsync(name, isBare: false);
        string workdir = repo.Workdir!;
        string nestedDir = Path.Combine(workdir, "dir");
        Directory.CreateDirectory(nestedDir);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(
            Path.Combine(workdir, "file.txt"),
            "one\n",
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(nestedDir, "child.txt"),
            "child\n",
            cancellationToken);

        GitIndex index = await repo.GetIndexAsync(cancellationToken);
        await index.AddByPathAsync("file.txt", cancellationToken);
        await index.AddByPathAsync("dir/child.txt", cancellationToken);
        await index.WriteAsync(cancellationToken);
        GitOid tree = await index.WriteTreeAsync(cancellationToken);

        GitSignature signature = new(
            "Test User",
            "test@example.com",
            new GitTime(1_700_000_000, 0));
        GitOid commit = await repo.CommitCreateAsync(
            new CommitCreateOptions
            {
                Tree = tree,
                Parents = [],
                Author = signature,
                Committer = signature,
                Message = "root\n",
                UpdateRef = "refs/heads/main",
            },
            cancellationToken);
        await repo.SetHeadAsync("refs/heads/main", cancellationToken);
        return (repo, commit);
    }
}
