using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.IntegrationTests.Remote;

/// <summary>
/// End-to-end tests for the remote/fetch/push behaviors
/// (libgit2 1.9.4), driven
/// through the local transport.
/// </summary>
public sealed class RemoteParityIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public RemoteParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RemoteParity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitRepository> CreateBareRepoAsync(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());
    }

    private static async Task<GitOid> CommitAsync(GitRepository repo, string message, string content)
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("f.txt", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = message + "\n",
            UpdateRef = "refs/heads/master",
        });
    }

    [Fact]
    public async Task Remote_PushUrlOnly_PushWorks()
    {
        // A pushurl-only remote (no url key) looks up and pushes fine
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await CreateBareRepoAsync("src");
        GitOid commit = await CommitAsync(source, "c", "x\n");

        await using GitRepository target = await CreateBareRepoAsync("dst");
        GitConfiguration cfg = source.Config;
        await cfg.SetStringAsync("remote.deploy.pushurl", target.Path, ct);

        await using GitRemote remote = await source.RemoteLookupAsync("deploy", ct);
        Assert.Null(remote.Url);
        Assert.Equal(target.Path, remote.PushUrl);

        await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: ct);

        GitReference? dst = await target.ReferenceLookupAsync("refs/heads/master", ct);
        Assert.NotNull(dst);
        Assert.Equal(commit, Assert.IsType<GitDirectReference>(dst).Target);
    }

    [Fact]
    public async Task Remote_Delete_PushRefspecRemovesLocalBranches()
    {
        // git_remote_delete removes refs matching ALL refspecs (fetch AND push) — a mirror-style push refspec deletes the matching local branches.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await CreateBareRepoAsync("r");
        GitOid commit = await CommitAsync(repo, "c", "x\n");

        GitConfiguration cfg = repo.Config;
        await cfg.SetStringAsync("remote.mirror.url", "ssh://h/m", ct);
        await cfg.SetStringAsync("remote.mirror.push", "+refs/heads/*:refs/heads/*", ct);
        Assert.NotNull(await repo.ReferenceLookupAsync("refs/heads/master", ct));

        await repo.RemoteDeleteAsync("mirror", ct);

        Assert.Null(await repo.ReferenceLookupAsync("refs/heads/master", ct));
    }

    [Fact]
    public async Task Fetch_TagoptAll_DefaultOptions_FetchesTags()
    {
        // remote.<name>.tagopt = --tags with DEFAULT fetch options must negotiate the tag wants.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await CreateBareRepoAsync("src2");
        GitOid commit = await CommitAsync(source, "c", "x\n");
        Commit commitObj = (await source.ObjectLookupAsync<Commit>(commit, ct))!;
        await source.TagCreateAsync("v1", commitObj, TestSig(), "msg\n", cancellationToken: ct);

        await using GitRepository target = await CreateBareRepoAsync("dst2");
        GitConfiguration cfg = target.Config;
        await cfg.SetStringAsync("remote.origin.url", source.Path, ct);
        await cfg.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", ct);
        await cfg.SetStringAsync("remote.origin.tagopt", "--tags", ct);

        await using GitRemote remote = await target.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(options: new GitFetchOptions(), cancellationToken: ct);

        GitReference? tagRef = await target.ReferenceLookupAsync("refs/tags/v1", ct);
        Assert.NotNull(tagRef);
        GitObject? tag = await target.ObjectLookupAsync(Assert.IsType<GitDirectReference>(tagRef).Target, ct);
        Assert.NotNull(tag);
    }

    [Fact]
    public async Task Fetch_ExplicitRefspec_UpdatesTrackingBranches()
    {
        // Fetching with EXPLICIT refspecs also updates the configured remote-tracking branches (opportunistic updates).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await CreateBareRepoAsync("src3");
        GitOid commit = await CommitAsync(source, "c", "x\n");

        await using GitRepository target = await CreateBareRepoAsync("dst3");
        GitConfiguration cfg = target.Config;
        await cfg.SetStringAsync("remote.origin.url", source.Path, ct);
        await cfg.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", ct);

        await using GitRemote remote = await target.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(refspecs: ["refs/heads/master"], cancellationToken: ct);

        GitReference? tracking = await target.ReferenceLookupAsync("refs/remotes/origin/master", ct);
        Assert.NotNull(tracking);
        Assert.Equal(commit, Assert.IsType<GitDirectReference>(tracking).Target);
    }

    [Fact]
    public async Task Push_NegotiationCallback_SeesRemoteOldAndLocalNew()
    {
        // git_push_update.src = current remote target, dst = new local target.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await CreateBareRepoAsync("src4");
        GitOid localOid = await CommitAsync(source, "local", "local\n");

        await using GitRepository target = await CreateBareRepoAsync("dst4");
        GitOid remoteOid = await CommitAsync(target, "remote", "remote\n");

        await using GitRemote remote = await source.RemoteCreateAsync("origin", target.Path, TestContext.Current.CancellationToken);
        IReadOnlyList<GitPushUpdate>? captured = null;
        await remote.PushAsync(
            ["+refs/heads/master:refs/heads/master"],
            new GitPushOptions
            {
                RemoteCallbacks = new GitRemoteCallbacks
                {
                    PushNegotiation = (updates, _) =>
                    {
                        captured = [.. updates];
                        return Task.FromResult(true);
                    },
                },
            },
            cancellationToken: ct);

        GitPushUpdate update = Assert.Single(captured!);
        Assert.Equal(remoteOid, update.Src);
        Assert.Equal(localOid, update.Dst);
    }

    [Fact]
    public async Task Push_PushOptionsOnLocalTransport_FailsLikeC()
    {
        // push-options on a transport without support fails with Error/Invalid "push-options not supported by remote".
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await CreateBareRepoAsync("src5");
        await CommitAsync(source, "c", "x\n");
        await using GitRepository target = await CreateBareRepoAsync("dst5");

        await using GitRemote remote = await source.RemoteCreateAsync("origin", target.Path, TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await remote.PushAsync(
            ["refs/heads/master:refs/heads/master"],
            new GitPushOptions { RemotePushOptions = ["opt"] },
            cancellationToken: ct));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
    }
}
