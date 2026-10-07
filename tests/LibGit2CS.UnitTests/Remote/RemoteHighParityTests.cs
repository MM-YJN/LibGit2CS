using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Remote;

/// <summary>
/// Regression tests for the remote behaviors (libgit2 1.9.4).
/// </summary>
public sealed class RemoteHighParityTests : IDisposable
{
    private readonly string _tempDir;

    public RemoteHighParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RemoteHigh_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private async Task<GitRepository> InitRepoAsync(string name, CancellationToken ct)
    {
        string repoPath = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: ct);
    }

    [Fact]
    public async Task Lookup_PushUrlOnlyRemote_Succeeds()
    {
        // A remote with only remote.<n>.pushurl (no url) looks up fine — url is NULL, pushurl is set.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("r", ct);

        GitConfiguration cfg = repo.Config;
        await cfg.SetStringAsync("remote.deploy.pushurl", "ssh://host/repo", ct);

        await using GitRemote remote = await repo.RemoteLookupAsync("deploy", ct);
        Assert.Null(remote.Url);
        Assert.Equal("ssh://host/repo", remote.PushUrl);
    }

    [Fact]
    public async Task Lookup_NeitherUrlNorPushUrl_ThrowsNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("no-url", ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.RemoteLookupAsync("ghost", ct));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Lookup_EmptyUrlCountsAsPresent_NoThrow()
    {
        // C: optional_setting_found |= found even for an empty url value, so
        // an empty url key suppresses the NotFound error (url stays NULL).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("empty-url", ct);

        GitConfiguration cfg = repo.Config;
        await cfg.SetStringAsync("remote.empty.url", "", ct);

        await using GitRemote remote = await repo.RemoteLookupAsync("empty", ct);
        Assert.Null(remote.Url);
    }

    [Fact]
    public async Task Delete_RemoteWithPushRefspec_DeletesMatchingLocalBranches()
    {
        // remove_remote_tracking iterates ALL refspecs (fetch AND push); a push refspec +refs/heads/*:refs/heads/* deletes matching LOCAL
        // branches on remote delete. C-verified: after git_remote_delete, refs/heads/master lookup is GIT_ENOTFOUND.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("delete-pushrefspec", ct);

        GitConfiguration cfg = repo.Config;
        await cfg.SetStringAsync("remote.mirror.url", "ssh://h/m", ct);
        await cfg.SetStringAsync("remote.mirror.push", "+refs/heads/*:refs/heads/*", ct);

        // Create a local branch (a commit + ref).
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), ct);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("f", blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await treeBld.WriteAsync(ct);
        var sig = new GitSignature("t", "t@example.com", new GitTime(1700000000, 0));
        GitOid commit = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "c\n",
            UpdateRef = "refs/heads/master",
        }, ct);
        Assert.NotNull(await repo.ReferenceLookupAsync("refs/heads/master", ct));

        await repo.RemoteDeleteAsync("mirror", ct);

        // The push refspec matches refs/heads/master → the local branch is gone.
        Assert.Null(await repo.ReferenceLookupAsync("refs/heads/master", ct));
    }

    [Fact]
    public async Task Delete_RemoteWithFetchRefspec_DeletesTrackingRefs()
    {
        // Sanity: the fetch side still works — remote.<n>.fetch destinations
        // are removed.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("delete-fetchrefspec", ct);

        GitConfiguration cfg = repo.Config;
        await cfg.SetStringAsync("remote.origin.url", "ssh://h/o", ct);
        await cfg.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", ct);

        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), ct);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("f", blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await treeBld.WriteAsync(ct);
        var sig = new GitSignature("t", "t@example.com", new GitTime(1700000000, 0));
        GitOid commit = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "c\n",
            UpdateRef = "refs/remotes/origin/main",
        }, ct);
        Assert.NotNull(await repo.ReferenceLookupAsync("refs/remotes/origin/main", ct));

        await repo.RemoteDeleteAsync("origin", ct);

        Assert.Null(await repo.ReferenceLookupAsync("refs/remotes/origin/main", ct));
    }
}
