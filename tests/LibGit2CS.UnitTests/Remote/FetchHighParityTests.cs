using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Remote;

/// <summary> Regression tests for the fetch behaviors (libgit2 1.9.4): (tagopt not resolved from the configured remote during download)
/// and (opportunistic updates of configured remote-tracking branches when explicit refspecs are fetched). </summary>
public sealed class FetchHighParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository? _sourceRepo;

    public FetchHighParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_FetchHigh_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_sourceRepo is not null)
        {
            await _sourceRepo.DisposeAsync();
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitRepository> CreateSourceRepoAsync(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());
        _sourceRepo = repo;

        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);

        GitOid commit = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "initial\n",
            UpdateRef = "refs/heads/master",
        });

        // A second branch and an annotated tag.
        await repo.ReferenceCreateAsync("refs/heads/feature", commit, force: true);
        Commit commitObj = (await repo.ObjectLookupAsync<Commit>(commit, TestContext.Current.CancellationToken))!;
        await repo.TagCreateAsync("v1", commitObj, TestSig(), "tag msg\n", cancellationToken: TestContext.Current.CancellationToken);

        return repo;
    }

    private async Task<GitRepository> CreateTargetRepoAsync(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());
    }

    [Fact]
    public async Task Fetch_DefaultOptions_ResolvesConfiguredTagoptAll()
    {
        // git_remote_download resolves an UNSPECIFIED caller tagopt to the remote's configured download_tags (fetch.c:106-109: `opts->download_tags !=
        // UNSPECIFIED` overrides, else remote->download_tags). A remote configured with tagopt=--tags fetched with DEFAULT options must negotiate the tag
        // wants
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await CreateSourceRepoAsync("src");
        await using GitRepository target = await CreateTargetRepoAsync("dst");

        GitConfiguration cfg = target.Config;
        await cfg.SetStringAsync("remote.origin.url", source.Path, ct);
        await cfg.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", ct);
        await cfg.SetStringAsync("remote.origin.tagopt", "--tags", ct);

        await using GitRemote remote = await target.RemoteLookupAsync("origin", ct);

        // Default options object (DownloadTags = Unspecified).
        await remote.FetchAsync(options: new GitFetchOptions(), cancellationToken: ct);

        // The annotated tag ref exists and resolves to the tag object.
        GitReference? tagRef = await target.ReferenceLookupAsync("refs/tags/v1", ct);
        Assert.NotNull(tagRef);
        GitDirectReference direct = Assert.IsType<GitDirectReference>(tagRef);
        GitObject? tag = await target.ObjectLookupAsync(direct.Target, ct);
        Assert.NotNull(tag);
        Assert.Equal(GitObjectType.Tag, tag!.Type);
    }

    [Fact]
    public async Task Fetch_ExplicitRefspec_UpdatesConfiguredTrackingBranches()
    {
        // Fetching with EXPLICIT refspecs must ALSO update the configured remote-tracking branches through the passive (configured, dwim'd) refspecs
        // (remote.c:2156-2157 + opportunistic_updates, remote.c:2055-2086).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await CreateSourceRepoAsync("src2");
        await using GitRepository target = await CreateTargetRepoAsync("dst2");

        GitConfiguration cfg = target.Config;
        await cfg.SetStringAsync("remote.origin.url", source.Path, ct);
        await cfg.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", ct);

        await using GitRemote remote = await target.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(refspecs: ["refs/heads/master"], cancellationToken: ct);

        // The configured tracking branch was updated opportunistically.
        GitReference? tracking = await target.ReferenceLookupAsync("refs/remotes/origin/master", ct);
        Assert.NotNull(tracking);

        GitReference? sourceMaster = await source.ReferenceLookupAsync("refs/heads/master", ct);
        Assert.NotNull(sourceMaster);
        Assert.Equal(
            Assert.IsType<GitDirectReference>(sourceMaster).Target,
            Assert.IsType<GitDirectReference>(tracking).Target);

        // The explicit spec was not configured with a destination: the
        // unrelated tracking branch stays absent.
        Assert.Null(await target.ReferenceLookupAsync("refs/remotes/origin/feature", ct));
    }

    [Fact]
    public async Task Fetch_ConfiguredRefspecs_NoOpportunisticDuplication()
    {
        // Sanity: with NO explicit refspecs (configured fetch), the normal
        // tracking updates happen and nothing extra is written.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await CreateSourceRepoAsync("src3");
        await using GitRepository target = await CreateTargetRepoAsync("dst3");

        GitConfiguration cfg = target.Config;
        await cfg.SetStringAsync("remote.origin.url", source.Path, ct);
        await cfg.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", ct);

        await using GitRemote remote = await target.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(cancellationToken: ct);

        Assert.NotNull(await target.ReferenceLookupAsync("refs/remotes/origin/master", ct));
        Assert.NotNull(await target.ReferenceLookupAsync("refs/remotes/origin/feature", ct));
    }
}
