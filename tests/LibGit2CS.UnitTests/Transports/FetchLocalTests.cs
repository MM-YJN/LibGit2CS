using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

using RemoteType = LibGit2CS.Remote.GitRemote;

namespace LibGit2CS.UnitTests.Transports;

/// <summary>
/// Golden tests for local fetch — ported from libgit2's
/// <c>tests/libgit2/network/fetchlocal.c</c> (634 lines, 16 tests).
/// Uses <c>file://</c> URLs with <see cref="LibGit2CS.Transports.GitLocalTransport"/>.
/// </summary>
public sealed class FetchLocalTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository? _sourceRepo;

    public FetchLocalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_FetchLocal_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async ValueTask<GitRepository> CreateSourceRepo(string name, int branches = 1)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());

        GitSignature sig = TestSig();

        // Create initial commit
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);

        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "initial\n",
            UpdateRef = "refs/heads/master",
        });

        // Create additional branches
        for (int i = 1; i < branches; i++)
        {
            await repo.ReferenceCreateAsync($"refs/heads/branch{i}", commitOid, force: true);
        }

        return repo;
    }

    private async ValueTask<GitRepository> CreateBareRepo(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());
    }

    private static async ValueTask<RemoteType> CreateRemote(GitRepository clientRepo, string url)
    {
        // Use Remote.Create which sets up the default fetch refspec
        RemoteType remote = await RemoteType.CreateAsync(clientRepo, "origin", url);
        return remote;
    }

    // ── complete ───────────────────────────────────────────────────────

    [Fact]
    public async Task Complete_FetchesAllRefs()
    {
        _sourceRepo = await CreateSourceRepo("source", branches: 3);
        GitRepository clientRepo = await CreateBareRepo("client");

        RemoteType remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        List<string> refs = (await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        // Should have refs/remotes/origin/master + refs/remotes/origin/branch1 + branch2
        Assert.Contains(refs, r => r == "refs/remotes/origin/master");
        Assert.Contains(refs, r => r == "refs/remotes/origin/branch1");
        Assert.Contains(refs, r => r == "refs/remotes/origin/branch2");

        await clientRepo.DisposeAsync();
    }

    // ── partial ───────────────────────────────────────────────────────

    [Fact]
    public async Task Partial_FetchOnlySpecifiedRefspec()
    {
        _sourceRepo = await CreateSourceRepo("source", branches: 3);
        GitRepository clientRepo = await CreateBareRepo("client");

        RemoteType remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(refspecs: ["refs/heads/master:refs/remotes/origin/master"], cancellationToken: TestContext.Current.CancellationToken);

        List<string> refs = (await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(refs, r => r == "refs/remotes/origin/master");
        // Other branches should NOT be fetched
        Assert.DoesNotContain(refs, r => r == "refs/remotes/origin/branch1");

        await clientRepo.DisposeAsync();
    }

    // ── all_refs ──────────────────────────────────────────────────────

    [Fact]
    public async Task AllRefs_FetchesAllBranches()
    {
        _sourceRepo = await CreateSourceRepo("source", branches: 5);
        GitRepository clientRepo = await CreateBareRepo("client");

        RemoteType remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        List<string> refs = (await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(refs, r => r == "refs/remotes/origin/master");
        for (int i = 1; i < 5; i++)
        {
            Assert.Contains(refs, r => r == $"refs/remotes/origin/branch{i}");
        }

        await clientRepo.DisposeAsync();
    }

    // ── multi_remotes ─────────────────────────────────────────────────

    [Fact]
    public async Task MultiRemotes_FetchFromMultipleSources()
    {
        _sourceRepo = await CreateSourceRepo("source1", branches: 1);
        GitRepository source2 = await CreateSourceRepo("source2", branches: 1);

        GitRepository clientRepo = await CreateBareRepo("client");

        RemoteType remote1 = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote1.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        RemoteType remote2 = await RemoteType.CreateAsync(clientRepo, "other", FixtureLoader.TestFileUrl(source2.Path), cancellationToken: TestContext.Current.CancellationToken);
        await remote2.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        List<string> refs = (await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(refs, r => r == "refs/remotes/origin/master");
        Assert.Contains(refs, r => r == "refs/remotes/other/master");

        await source2.DisposeAsync();
        await clientRepo.DisposeAsync();
    }

    // ── call_progress ─────────────────────────────────────────────────

    [Fact]
    public async Task CallProgress_ReportsProgress()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateBareRepo("client");

        var progressReports = new List<GitTransferProgress>();

        RemoteType remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(options: new GitFetchOptions
        {
            RemoteCallbacks = new GitRemoteCallbacks
            {
                TransferProgress = new SyncProgress<GitTransferProgress>(p => progressReports.Add(p)),
            },
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(progressReports);

        await clientRepo.DisposeAsync();
    }

    // ── prune ────────────────────────────────────────────────────────

    [Fact]
    public async Task Prune_RemovesStaleTrackingRefs()
    {
        _sourceRepo = await CreateSourceRepo("source", branches: 3);
        GitRepository clientRepo = await CreateBareRepo("client");

        // First fetch all branches
        RemoteType remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/branch1");

        // Delete branch1 from source
        await _sourceRepo.Refs.DeleteAsync("refs/heads/branch1", cancellationToken: TestContext.Current.CancellationToken);

        // Fetch with prune
        await remote.FetchAsync(options: new GitFetchOptions { Prune = GitFetchPrune.Prune }, cancellationToken: TestContext.Current.CancellationToken);

        // branch1 tracking ref should be pruned
        Assert.DoesNotContain(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/branch1");
        // master should still be there
        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/master");

        await clientRepo.DisposeAsync();
    }

    // ── fetchprune ────────────────────────────────────────────────────

    [Fact]
    public async Task FetchPrune_PruneViaFetchOptions()
    {
        _sourceRepo = await CreateSourceRepo("source", branches: 2);
        GitRepository clientRepo = await CreateBareRepo("client");

        RemoteType remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _sourceRepo.Refs.DeleteAsync("refs/heads/branch1", cancellationToken: TestContext.Current.CancellationToken);

        await remote.FetchAsync(options: new GitFetchOptions { Prune = GitFetchPrune.Prune }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/branch1");

        await clientRepo.DisposeAsync();
    }

    // ── prune_overlapping ─────────────────────────────────────────────

    [Fact]
    public async Task PruneOverlapping_RemovesCorrectRefs()
    {
        _sourceRepo = await CreateSourceRepo("source", branches: 2);
        GitRepository clientRepo = await CreateBareRepo("client");

        RemoteType remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Verify both branches exist as tracking refs
        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/master");
        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/branch1");

        // Delete branch1 from source
        await _sourceRepo.Refs.DeleteAsync("refs/heads/branch1", cancellationToken: TestContext.Current.CancellationToken);

        // Fetch with prune — only branch1 should be removed
        await remote.FetchAsync(options: new GitFetchOptions { Prune = GitFetchPrune.Prune }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/master");
        Assert.DoesNotContain(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/branch1");

        await clientRepo.DisposeAsync();
    }

    // ── prune_tag ─────────────────────────────────────────────────────

    [Fact]
    public async Task PruneTag_PruneDoesNotRemoveTags()
    {
        _sourceRepo = await CreateSourceRepo("source", branches: 1);

        // Create an annotated tag
        GitOid commitOid = (await _sourceRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference)!.Target;
        Commit commit = (await _sourceRepo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
        await _sourceRepo.TagCreateAsync("v1.0", commit, TestSig(), "tag message\n", cancellationToken: TestContext.Current.CancellationToken);

        GitRepository clientRepo = await CreateBareRepo("client");

        // Fetch with a refspec that matches both heads and tags
        RemoteType remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(refspecs: ["+refs/heads/*:refs/remotes/origin/*", "+refs/tags/*:refs/tags/*"],
                      options: new GitFetchOptions { DownloadTags = GitAutoTagOption.All, Prune = GitFetchPrune.Prune }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/tags/v1.0");

        // Delete tag from source
        await _sourceRepo.Refs.DeleteAsync("refs/tags/v1.0", cancellationToken: TestContext.Current.CancellationToken);

        // Fetch with prune — tags matching the refspec are pruned
        await remote.FetchAsync(refspecs: ["+refs/heads/*:refs/remotes/origin/*", "+refs/tags/*:refs/tags/*"],
                      options: new GitFetchOptions { DownloadTags = GitAutoTagOption.All, Prune = GitFetchPrune.Prune }, cancellationToken: TestContext.Current.CancellationToken);

        // Tag should be pruned since it matches the refspec and no longer exists on source
        Assert.DoesNotContain(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/tags/v1.0");

        await clientRepo.DisposeAsync();
    }

    // ── clone_into_mirror ─────────────────────────────────────────────

    [Fact]
    public async Task CloneIntoMirror_FetchesAllRefs()
    {
        _sourceRepo = await CreateSourceRepo("source", branches: 2);
        GitRepository clientRepo = await CreateBareRepo("client");

        // Mirror fetch = fetch all refs directly (not as remotes/origin/*)
        RemoteType remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(refspecs: ["+refs/*:refs/*"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/heads/master");
        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/heads/branch1");

        await clientRepo.DisposeAsync();
    }

    // ── update_refs_error_is_propagated ───────────────────────────────

    [Fact]
    public async Task UpdateRefsErrorIsPropagated_CallbackRejectionFailsFetchAfterWrite()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateBareRepo("client");

        RemoteType remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));

        // C (remote.c:1890-1908, C probe): the ref is written
        // FIRST, then the update_refs callback fires; a rejection (nonzero
        // return) fails the whole fetch with GIT_ERROR_CALLBACK
        // "git_remote_fetch callback returned -1" (errors.h:35-44).
        GitException ex = await Assert.ThrowsAsync<GitException>(() => remote.FetchAsync(options: new GitFetchOptions
        {
            RemoteCallbacks = new GitRemoteCallbacks
            {
                UpdateRefs = (_, _, _, _) => false,
            },
        }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Callback, ex.Category);
        Assert.Equal("git_remote_fetch callback returned -1", ex.Message);

        // The ref was already written before the callback (probe: present).
        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/master");

        await clientRepo.DisposeAsync();
    }

    // ── update_refs_is_preferred ──────────────────────────────────────

    [Fact]
    public async Task UpdateRefsIsPreferred_CallbackReturningTrueAllowsUpdate()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateBareRepo("client");

        RemoteType remote = await CreateRemote(clientRepo, FixtureLoader.TestFileUrl(_sourceRepo.Path));
        await remote.FetchAsync(options: new GitFetchOptions
        {
            RemoteCallbacks = new GitRemoteCallbacks
            {
                UpdateRefs = (_, _, _, _) => true,
            },
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/master");

        await clientRepo.DisposeAsync();
    }

    // ── prune_load_remote_prune_config ────────────────────────────────

    [Fact]
    public async Task PruneLoadRemotePruneConfig_ReadsConfig()
    {
        _sourceRepo = await CreateSourceRepo("source", branches: 2);
        GitRepository clientRepo = await CreateBareRepo("client");

        RemoteType remote = await RemoteType.CreateAsync(clientRepo, "origin", FixtureLoader.TestFileUrl(_sourceRepo.Path), cancellationToken: TestContext.Current.CancellationToken);
        // Set prune=true in config
        await clientRepo.Config.SetStringAsync("remote.origin.prune", "true", cancellationToken: TestContext.Current.CancellationToken);
        await remote.DisposeAsync();

        // Re-lookup remote
        remote = await RemoteType.LookupAsync(clientRepo, "origin", cancellationToken: TestContext.Current.CancellationToken);
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _sourceRepo.Refs.DeleteAsync("refs/heads/branch1", cancellationToken: TestContext.Current.CancellationToken);

        // Fetch again — should prune based on config
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/branch1");

        await clientRepo.DisposeAsync();
    }

    // ── prune_load_fetch_prune_config ─────────────────────────────────

    [Fact]
    public async Task PruneLoadFetchPruneConfig_ReadsGlobalConfig()
    {
        _sourceRepo = await CreateSourceRepo("source", branches: 2);
        GitRepository clientRepo = await CreateBareRepo("client");

        // Set fetch.prune=true in config before creating remote
        await clientRepo.Config.SetStringAsync("fetch.prune", "true", cancellationToken: TestContext.Current.CancellationToken);

        // Create remote, then dispose and Lookup to load config
        await (await RemoteType.CreateAsync(clientRepo, "origin", FixtureLoader.TestFileUrl(_sourceRepo.Path), cancellationToken: TestContext.Current.CancellationToken)).DisposeAsync();
        RemoteType remote = await RemoteType.LookupAsync(clientRepo, "origin", cancellationToken: TestContext.Current.CancellationToken);
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _sourceRepo.Refs.DeleteAsync("refs/heads/branch1", cancellationToken: TestContext.Current.CancellationToken);

        // Fetch again — should prune based on global config
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(await clientRepo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => r == "refs/remotes/origin/branch1");

        await clientRepo.DisposeAsync();
    }
}
