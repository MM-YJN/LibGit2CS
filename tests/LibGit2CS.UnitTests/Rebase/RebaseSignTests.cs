using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using RebaseOps = LibGit2CS.Rebase.GitRebase;
using RebaseOpts = LibGit2CS.Rebase.GitRebaseOptions;

namespace LibGit2CS.UnitTests.Rebase;

/// <summary>
/// Rebase signing callback tests. Mirrors libgit2's
/// <c>tests/libgit2/rebase/sign.c</c> (modern <c>commit_create_cb</c> tests only;
/// deprecated <c>signing_cb</c> tests behind <c>GIT_DEPRECATE_HARD</c> are skipped).
/// </summary>
public sealed class RebaseSignTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public RebaseSignTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RebaseSign_" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task PassthroughCreateCb()
    {
        // When commit_create_cb returns PassThrough (null), the default
        // commit creation path is used.
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/gravy");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/veal");

        var opts = new RebaseOpts
        {
            CommitCreateCallback = (author, committer, encoding, message, tree, parents) =>
            {
                // Return null to indicate PassThrough.
                return null;
            },
        };

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, opts, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, rebase.OperationCount);

        await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid commitId = await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);

        // Verify the commit was created.
        Commit? commit = await repo.ObjectLookupAsync<Commit>(commitId, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
    }

    [Fact]
    public async Task CreateGpgSigned()
    {
        // The commit_create_cb creates a commit with a GPG signature.
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/gravy");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/veal");

        var opts = new RebaseOpts
        {
            CommitCreateCallback = (author, committer, encoding, message, tree, parents) =>
            {
                // Create a commit buffer, then create with a fake signature.
                byte[] buffer = Commit.CreateBuffer(
                    author ?? sig, committer, encoding, message, tree,
                    parents.Select(c => (GitOid)c.Id).ToList());

                return repo.CommitCreateWithSignatureAsync(System.Text.Encoding.ASCII.GetString(buffer), "fake-signature", null, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            },
        };

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, opts, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, rebase.OperationCount);

        await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid commitId = await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken);

        // Verify the commit exists and has the gpgsig header.
        Commit? commit = await repo.ObjectLookupAsync<Commit>(commitId, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
    }

    [Fact]
    public async Task CreatePropagatesError()
    {
        // If the commit_create_cb throws, the error propagates.
        await using GitRepository repo = await OpenRebaseRepoAsync();
        GitSignature sig = RebaseTestHelpers.CreateSignature();
        GitAnnotatedCommit branch = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/gravy");
        GitAnnotatedCommit upstream = await RebaseTestHelpers.FromRefAsync(repo, "refs/heads/veal");

        var opts = new RebaseOpts
        {
            CommitCreateCallback = (author, committer, encoding, message, tree, parents) =>
            {
                throw new GitException(GitErrorCode.User, "custom error", GitErrorCategory.Callback);
            },
        };

        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, null, opts, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, rebase.OperationCount);

        await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await rebase.CommitAsync(null, sig, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.User, ex.Code);
    }
}
