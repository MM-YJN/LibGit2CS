using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Remote;

/// <summary> Regression tests for the push behaviors (libgit2 1.9.4): (GitPushUpdate Src/Dst transposed), (push-options error
/// code/category), ("remote is disconnected" error code). </summary>
public sealed class PushHighParityTests : IDisposable
{
    private readonly string _tempDir;

    public PushHighParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PushHigh_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitRepository> CreateBareRepoAsync(string name, string? branchCommit = null)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());
        if (branchCommit is not null)
        {
            await repo.ReferenceCreateAsync("refs/heads/master", GitOid.Parse(branchCommit.AsSpan(), GitHashAlgorithmKind.Sha1), force: true);
        }

        return repo;
    }

    private static async Task<GitOid> CreateCommitAsync(GitRepository repo, string message, string content)
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

    private async Task<GitRepository> CreateSourceRepoAsync(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());
        await CreateCommitAsync(repo, "source commit", "source content\n");
        return repo;
    }

    private async Task<GitRepository> CreateTargetRepoWithOwnCommitAsync(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());
        await CreateCommitAsync(repo, "target commit", "target content\n");
        return repo;
    }

    [Fact]
    public async Task Push_NegotiationCallback_ReceivesCorrectSrcDst()
    {
        // git_push_update.src is the CURRENT (remote) target — roid — and dst is the NEW (local) target — loid (push.c:389-390,
        // remote.h:512-519).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await CreateSourceRepoAsync("src");
        await using GitRepository target = await CreateTargetRepoWithOwnCommitAsync("dst");

        GitOid localOid = Assert.IsType<GitDirectReference>(await source.ReferenceLookupAsync("refs/heads/master", ct)).Target;
        GitOid remoteOid = Assert.IsType<GitDirectReference>(await target.ReferenceLookupAsync("refs/heads/master", ct)).Target;
        Assert.NotEqual(localOid, remoteOid);

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
                        // Copy: the coordinator's list is cleared on dispose.
                        captured = [.. updates];
                        return Task.FromResult(true);
                    },
                },
            },
            cancellationToken: ct);

        Assert.NotNull(captured);
        GitPushUpdate update = Assert.Single(captured);
        // Src = the remote's current target; Dst = the local new target.
        Assert.Equal(remoteOid, update.Src);
        Assert.Equal(localOid, update.Dst);
    }

    [Fact]
    public async Task Push_WithPushOptionsOnNonSmartTransport_FailsLikeC()
    {
        // C checks push-options support for EVERY transport via git_remote_capabilities (push.c:526-531) and fails with GIT_ERROR_INVALID "push-options
        // not supported by remote" (code Error, category Invalid).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await CreateSourceRepoAsync("src2");
        await using GitRepository target = await CreateBareRepoAsync("dst2");

        await using GitRemote remote = await source.RemoteCreateAsync("origin", target.Path, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await remote.PushAsync(
            ["refs/heads/master:refs/heads/master"],
            new GitPushOptions { RemotePushOptions = ["option"] },
            cancellationToken: ct));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Contains("push-options not supported", ex.Message);
    }

    [Fact]
    public async Task Push_DisconnectedRemote_FailsLikeC()
    {
        // git_push_finish on a disconnected remote fails with GIT_ERROR_NET "remote is disconnected" (push.c:517-520) — code Error, category Net. The
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await CreateSourceRepoAsync("src3");
        await using GitRepository target = await CreateBareRepoAsync("dst3");
        await using GitRemote remote = await source.RemoteCreateAsync("origin", target.Path, TestContext.Current.CancellationToken);

        var coordinator = new PushCoordinator(remote);
        IGitTransport transport = source.Context.Transports.Create(target.Path, source.Context);
        await using (transport)
        {
            // Not connected.
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await coordinator.FinishAsync(transport, callbacks: null, pushOptions: null, pbParallelism: 1, cancellationToken: ct));
            Assert.Equal(GitErrorCode.Error, ex.Code);
            Assert.Equal(GitErrorCategory.Net, ex.Category);
        }
    }
}
