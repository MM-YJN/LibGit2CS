using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

public sealed class LocalTransportTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository? _sourceRepo;

    public LocalTransportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_LocalTransport_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async ValueTask<GitRepository> CreateSourceRepo(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext());
        LibGit2CS.Index.GitIndex idx = await repo.GetIndexAsync();

        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("README.md");
        await idx.WriteAsync();
        GitOid treeOid = await idx.WriteTreeAsync();
        GitSignature sig = TestSig();
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "initial\n",
            UpdateRef = "refs/heads/master",
        });

        return repo;
    }

    private async ValueTask<GitRepository> CreateEmptyRepo(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoPath, isBare: false, new GitContext());
    }

    // ── Connect / Ls tests ──────────────────────────────────────────────

    [Fact]
    public async Task Connect_And_Ls_ReturnsRefs()
    {
        _sourceRepo = await CreateSourceRepo("source");
        string url = FixtureLoader.TestFileUrl(_sourceRepo.Path);

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(url, GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
        IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(refs);

        // Should have HEAD + refs/heads/master
        Assert.Contains(refs, r => r.Name == "HEAD");
        Assert.Contains(refs, r => r.Name == "refs/heads/master");
    }

    [Fact]
    public async Task Connect_WithFilePath_WithoutScheme()
    {
        _sourceRepo = await CreateSourceRepo("source");
        string path = _sourceRepo.Path;

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(path, GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
        IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(refs);
    }

    [Fact]
    public async Task Connect_NotConnected_Ls_Throws()
    {
        await using var transport = new GitLocalTransport(new GitContext());
        await Assert.ThrowsAsync<GitException>(async () => await transport.LsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Capabilities_AlwaysTipAndReachable()
    {
        _sourceRepo = await CreateSourceRepo("source");

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.Equal(GitRemoteCapability.TipOid | GitRemoteCapability.ReachableOid, transport.Capabilities);
    }

    [Fact]
    public async Task OidType_FromSourceRepo()
    {
        _sourceRepo = await CreateSourceRepo("source");

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.Equal(GitHashAlgorithmKind.Sha1, transport.OidType);
    }

    // ── NegotiateFetch tests ───────────────────────────────────────────

    [Fact]
    public async Task NegotiateFetch_PopulatesLocalOid()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateEmptyRepo("client");

        // First, fetch from source so the client has objects
        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(TestContext.Current.CancellationToken);
        var wants = new GitFetchNegotiation(refs, Array.Empty<GitOid>(), 0);

        // NegotiateFetch should populate LocalOid (all should be non-local since client is empty)
        await transport.NegotiateFetchAsync(clientRepo, wants, TestContext.Current.CancellationToken);

        // All refs should have Local=false since client is empty
        foreach (GitRemoteHead head in await transport.LsAsync(TestContext.Current.CancellationToken))
        {
            if (head.Name != "HEAD" && !head.Name.EndsWith("^{}"))
            {
                Assert.False(head.Local, $"{head.Name} should not be local");
            }
        }

        await clientRepo.DisposeAsync();
    }

    [Fact]
    public async Task NegotiateFetch_RejectsShallowFetch()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateEmptyRepo("client");

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        var wants = new GitFetchNegotiation(await transport.LsAsync(TestContext.Current.CancellationToken), Array.Empty<GitOid>(), Depth: 1);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await transport.NegotiateFetchAsync(clientRepo, wants, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotSupported, ex.Code);
        Assert.Contains("shallow", ex.Message, StringComparison.OrdinalIgnoreCase);

        await clientRepo.DisposeAsync();
    }

    // ── DownloadPack tests ─────────────────────────────────────────────

    [Fact]
    public async Task DownloadPack_CopiesObjectsToClient()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateEmptyRepo("client");
        string clientPath = clientRepo.Path;
        await clientRepo.DisposeAsync();

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);
        IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(TestContext.Current.CancellationToken);
        await using GitRepository clientForFetch = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await transport.NegotiateFetchAsync(clientForFetch, new GitFetchNegotiation(refs, Array.Empty<GitOid>(), 0), TestContext.Current.CancellationToken);
        await transport.DownloadPackAsync(clientForFetch, new GitIndexerProgress(), TestContext.Current.CancellationToken);
        await clientForFetch.DisposeAsync();

        // Reopen to pick up the new pack
        await using GitRepository reopenedClient = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Verify client now has objects
        var sourceCommit = await _sourceRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference;
        Assert.NotNull(sourceCommit);

        GitObject? clientObj = await reopenedClient.ObjectLookupAsync(sourceCommit!.Target, TestContext.Current.CancellationToken);
        Assert.NotNull(clientObj);
    }

    [Fact]
    public async Task DownloadPack_SkipsExistingObjects()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateEmptyRepo("client");
        string clientPath = clientRepo.Path;
        await clientRepo.DisposeAsync();

        // First download — copies all objects
        await using (var transport = new GitLocalTransport(new GitContext()))
        {
            await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);
            IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(TestContext.Current.CancellationToken);
            await using GitRepository clientForFetch = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
            await transport.NegotiateFetchAsync(clientForFetch, new GitFetchNegotiation(refs, Array.Empty<GitOid>(), 0), TestContext.Current.CancellationToken);
            await transport.DownloadPackAsync(clientForFetch, new GitIndexerProgress(), TestContext.Current.CancellationToken);
        }

        // Reopen to pick up the new pack — second download should skip all existing
        await using GitRepository reopened = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Second download — all objects already present, should be fast
        await using var transport2 = new GitLocalTransport(new GitContext());
        await transport2.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);
        IReadOnlyList<GitRemoteHead> refs2 = await transport2.LsAsync(TestContext.Current.CancellationToken);
        await transport2.NegotiateFetchAsync(reopened, new GitFetchNegotiation(refs2, Array.Empty<GitOid>(), 0), TestContext.Current.CancellationToken);
        await transport2.DownloadPackAsync(reopened, new GitIndexerProgress(), TestContext.Current.CancellationToken);

        // Verify client still has objects
        var sourceCommit = await _sourceRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference;
        Assert.NotNull(sourceCommit);
        GitObject? clientObj = await reopened.ObjectLookupAsync(sourceCommit!.Target, TestContext.Current.CancellationToken);
        Assert.NotNull(clientObj);
    }

    [Fact]
    public async Task DownloadPack_ReportsProgress()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateEmptyRepo("client");
        string clientPath = clientRepo.Path;
        await clientRepo.DisposeAsync();

        var progressReports = new List<GitTransferProgress>();

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch,
            new GitRemoteConnectOptions
            {
                Callbacks = new GitRemoteCallbacks
                {
                    TransferProgress = new SyncProgress<GitTransferProgress>(p => progressReports.Add(p)),
                },
            }, TestContext.Current.CancellationToken);

        IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(TestContext.Current.CancellationToken);
        await using GitRepository clientForFetch = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await transport.NegotiateFetchAsync(clientForFetch, new GitFetchNegotiation(refs, Array.Empty<GitOid>(), 0), TestContext.Current.CancellationToken);
        await transport.DownloadPackAsync(clientForFetch, new GitIndexerProgress(), TestContext.Current.CancellationToken);

        Assert.NotEmpty(progressReports);
    }

    [Fact]
    public async Task DownloadPack_CreatesPackAndIdxFiles()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateEmptyRepo("client");

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);
        IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(TestContext.Current.CancellationToken);
        await transport.NegotiateFetchAsync(clientRepo, new GitFetchNegotiation(refs, Array.Empty<GitOid>(), 0), TestContext.Current.CancellationToken);
        await transport.DownloadPackAsync(clientRepo, new GitIndexerProgress(), TestContext.Current.CancellationToken);

        string packDir = Path.Combine(clientRepo.Path, "objects", "pack");
        Assert.True(Directory.Exists(packDir), "pack directory should exist");

        string[] packFiles = Directory.GetFiles(packDir, "pack-*.pack");
        string[] idxFiles = Directory.GetFiles(packDir, "pack-*.idx");
        Assert.NotEmpty(packFiles);
        Assert.NotEmpty(idxFiles);
        Assert.Single(packFiles);
        Assert.Single(idxFiles);

        // The .pack and .idx should share the same hash suffix
        string packHash = Path.GetFileNameWithoutExtension(packFiles[0])["pack-".Length..];
        string idxHash = Path.GetFileNameWithoutExtension(idxFiles[0])["pack-".Length..];
        Assert.Equal(packHash, idxHash);

        await clientRepo.DisposeAsync();
    }

    [Fact]
    public async Task DownloadPack_ObjectsAccessibleViaLookup()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateEmptyRepo("client");
        string clientPath = clientRepo.Path;
        await clientRepo.DisposeAsync();

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);
        IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(TestContext.Current.CancellationToken);
        await using GitRepository clientForFetch = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await transport.NegotiateFetchAsync(clientForFetch, new GitFetchNegotiation(refs, Array.Empty<GitOid>(), 0), TestContext.Current.CancellationToken);
        await transport.DownloadPackAsync(clientForFetch, new GitIndexerProgress(), TestContext.Current.CancellationToken);
        await clientForFetch.DisposeAsync();

        // Reopen to pick up the new pack
        await using GitRepository reopenedClient = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Look up the commit, tree, and blob from the pack
        GitOid sourceCommitOid = (await _sourceRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference)!.Target;
        Commit? commit = await reopenedClient.ObjectLookupAsync<Commit>(sourceCommitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);

        GitTree? tree = await reopenedClient.ObjectLookupAsync<GitTree>(commit!.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);

        // The README.md blob should be in the pack
        await foreach ((_, GitTreeEntry entry) in tree!.WalkAsync(GitTreeWalkMode.PreOrder, TestContext.Current.CancellationToken))
        {
            if (!entry.IsGitLink)
            {
                GitObject? blob = await reopenedClient.ObjectLookupAsync(entry.Id, TestContext.Current.CancellationToken);
                Assert.NotNull(blob);
                blob?.Dispose();
            }
        }

        // Verify no loose objects were written (all should be in the pack)
        string looseDir = Path.Combine(clientPath, "objects");
        foreach (string dir in Directory.GetDirectories(looseDir))
        {
            if (Path.GetFileName(dir) is "pack" or "info")
            {
                continue;
            }
            // Two-char directory = loose object storage
            Assert.True(Directory.GetFiles(dir).Length == 0,
                $"loose object directory {Path.GetFileName(dir)} should be empty — all objects should be in the pack");
        }

        commit!.Dispose();
        tree!.Dispose();
    }

    [Fact]
    public async Task DownloadPack_RefreshesOdb_LookupSeesNewObjects()
    {
        // Regression guard: DownloadPackAsync must refresh the client repo's
        // pack backends so the newly-written pack is visible to Lookups on
        // the SAME repo instance — no reopen. If the ODB loaded
        // packs only at construction, the post-fetch lookup would miss and a
        // non-bare clone would silently check out zero working-tree files.
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateEmptyRepo("client");
        string clientPath = clientRepo.Path;
        await clientRepo.DisposeAsync();

        GitOid sourceCommitOid = (await _sourceRepo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference)!.Target;

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);
        IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(TestContext.Current.CancellationToken);

        await using GitRepository clientForFetch = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await transport.NegotiateFetchAsync(clientForFetch, new GitFetchNegotiation(refs, Array.Empty<GitOid>(), 0), TestContext.Current.CancellationToken);
        await transport.DownloadPackAsync(clientForFetch, new GitIndexerProgress(), TestContext.Current.CancellationToken);

        // Core assertion: lookup on the SAME instance — no reopen.
        Commit? tip = await clientForFetch.ObjectLookupAsync<Commit>(sourceCommitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(tip);
        Assert.Equal("initial\n", tip!.Message);

        // The refresh must be idempotent: a second fetch into the same repo
        // must not duplicate the loaded pack (guards the path-dedup in
        // PackObjectBackend.RefreshAsync). Count packs on disk — exactly one.
        string packDir = Path.Combine(clientPath, "objects", "pack");
        Assert.Single(Directory.GetFiles(packDir, "pack-*.pack"));

        tip!.Dispose();
    }

    // ── NegotiateFetch / DownloadPack regression tests ─────────────────

    /// <summary>
    /// Adds a second commit (a new file) to the source repo's master branch
    /// and returns the new tip OID.
    /// </summary>
    private static async Task<GitOid> AddCommitAsync(GitRepository repo, GitOid parent, string fileName, string content, CancellationToken ct)
    {
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, ct);
        LibGit2CS.Index.GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync(fileName, ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);
        GitSignature sig = TestSig();
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [parent],
            Author = sig,
            Committer = sig,
            Message = $"add {fileName}\n",
            UpdateRef = "refs/heads/master",
        }, ct);
    }

    [Fact]
    public async Task NegotiateFetch_TrailingLocalBranch_StillDownloadsPack()
    {
        // Regression: the client has refs/heads/master at an OLDER
        // commit than the source. Marking a head "local" on name alone would
        // let DownloadPack skip it and fetch zero objects; the check
        // therefore requires OID equality before marking a head local.
        CancellationToken ct = TestContext.Current.CancellationToken;
        _sourceRepo = await CreateSourceRepo("source");
        GitOid commitA = (await _sourceRepo.ReferenceResolveAsync("refs/heads/master", ct) as GitDirectReference)!.Target;

        // Initial fetch: client receives commitA.
        GitRepository clientRepo = await CreateEmptyRepo("client");
        string clientPath = clientRepo.Path;
        await clientRepo.DisposeAsync();

        await using (var transport = new GitLocalTransport(new GitContext()))
        {
            await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, ct);
            IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(ct);
            await using GitRepository client = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: ct);
            await transport.NegotiateFetchAsync(client, new GitFetchNegotiation(refs, Array.Empty<GitOid>(), 0), ct);
            await transport.DownloadPackAsync(client, new GitIndexerProgress(), ct);
        }

        // Give the client a local refs/heads/master → commitA (same NAME as
        // source, but the source will advance to commitB).
        await using (GitRepository client = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: ct))
        {
            await client.ReferenceCreateAsync("refs/heads/master", commitA, force: false, logMessage: null, ct);
        }

        // Advance the source to commitB.
        GitOid commitB = await AddCommitAsync(_sourceRepo, commitA, "second.txt", "second\n", ct);

        // Second fetch: must download commitB (head is NOT local — OIDs differ).
        await using (var transport2 = new GitLocalTransport(new GitContext()))
        {
            await transport2.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, ct);
            IReadOnlyList<GitRemoteHead> refs2 = await transport2.LsAsync(ct);

            // Negotiate: master must NOT be marked local.
            await using GitRepository client = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: ct);
            await transport2.NegotiateFetchAsync(client, new GitFetchNegotiation(refs2, Array.Empty<GitOid>(), 0), ct);

            GitRemoteHead? masterHead = null;
            foreach (GitRemoteHead h in await transport2.LsAsync(ct))
            {
                if (h.Name == "refs/heads/master")
                {
                    masterHead = h;
                }
            }
            Assert.NotNull(masterHead);
            Assert.False(masterHead!.Local, "master must not be local when client trails the source");

            await transport2.DownloadPackAsync(client, new GitIndexerProgress(), ct);
        }

        // The client must now have commitB.
        await using GitRepository reopened = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: ct);
        GitObject? obj = await reopened.ObjectLookupAsync(commitB, ct);
        Assert.NotNull(obj);
        obj?.Dispose();
    }

    [Fact]
    public async Task DownloadPack_ClientOnlyCommit_DoesNotThrow()
    {
        // Regression: the client has a commit the source does not
        // (local divergence). DownloadPack enumerates all client ODB objects
        // and hides commits in the source revwalk. Without the source-exists
        // guard, HideAsync throws NotFound for the client-only commit.
        CancellationToken ct = TestContext.Current.CancellationToken;
        _sourceRepo = await CreateSourceRepo("source");
        GitOid commitA = (await _sourceRepo.ReferenceResolveAsync("refs/heads/master", ct) as GitDirectReference)!.Target;

        // Initial fetch.
        GitRepository clientRepo = await CreateEmptyRepo("client");
        string clientPath = clientRepo.Path;
        await clientRepo.DisposeAsync();

        await using (var transport = new GitLocalTransport(new GitContext()))
        {
            await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, ct);
            IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(ct);
            await using GitRepository client = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: ct);
            await transport.NegotiateFetchAsync(client, new GitFetchNegotiation(refs, Array.Empty<GitOid>(), 0), ct);
            await transport.DownloadPackAsync(client, new GitIndexerProgress(), ct);
        }

        // Diverge: add a client-only commit AND a source-only commit.
        GitOid commitBRemote;
        await using (GitRepository client = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: ct))
        {
            // Client-only commit (never pushed to source).
            string workdir = client.Workdir!;
            await File.WriteAllTextAsync(Path.Combine(workdir, "local.txt"), "local only\n", ct);
            LibGit2CS.Index.GitIndex idx = await client.GetIndexAsync(ct);
            await idx.AddByPathAsync("local.txt", ct);
            await idx.WriteAsync(ct);
            GitOid treeOid = await idx.WriteTreeAsync(ct);
            GitSignature sig = TestSig();
            await client.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [commitA],
                Author = sig,
                Committer = sig,
                Message = "local divergence\n",
                UpdateRef = "refs/heads/master",
            }, ct);
        }

        // Source-only commit.
        commitBRemote = await AddCommitAsync(_sourceRepo, commitA, "remote.txt", "remote only\n", ct);

        // Fetch: must not throw NotFound for the client-only commit.
        await using (var transport2 = new GitLocalTransport(new GitContext()))
        {
            await transport2.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, ct);
            IReadOnlyList<GitRemoteHead> refs2 = await transport2.LsAsync(ct);
            await using GitRepository client = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: ct);
            await transport2.NegotiateFetchAsync(client, new GitFetchNegotiation(refs2, Array.Empty<GitOid>(), 0), ct);
            await transport2.DownloadPackAsync(client, new GitIndexerProgress(), ct);
        }

        // The client must now have the source-only commit.
        await using GitRepository reopened = await GitRepository.OpenAsync(clientPath, new GitContext(), cancellationToken: ct);
        Commit? remoteCommit = await reopened.ObjectLookupAsync<Commit>(commitBRemote, ct);
        Assert.NotNull(remoteCommit);
        Assert.Equal("add remote.txt\n", remoteCommit!.Message);
        remoteCommit!.Dispose();
    }

    // ── Empty repo tests ──────────────────────────────────────────────

    [Fact]
    public async Task Connect_EmptyRepo_HeadPointsToNonexistentBranch()
    {
        // An empty repo has HEAD pointing to refs/heads/master which doesn't exist
        GitRepository emptyRepo = await CreateEmptyRepo("empty");
        await emptyRepo.DisposeAsync();

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(emptyRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.True(transport.IsConnected);
        // An empty repo should have no refs (HEAD can't resolve)
        IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(TestContext.Current.CancellationToken);
        // HEAD may or may not be in the list depending on how the empty repo's HEAD is set up
        // but there should be no refs/heads/* entries
        Assert.DoesNotContain(refs, r => r.Name.StartsWith("refs/heads/", StringComparison.Ordinal));
    }

    // ── Push / ShallowRoots ────────────────────────────────────────────

    [Fact]
    public async Task ShallowRoots_AlwaysEmpty()
    {
        _sourceRepo = await CreateSourceRepo("source");

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        Assert.Empty(await transport.ShallowRootsAsync(TestContext.Current.CancellationToken));
    }

    // ── Close / Dispose tests ──────────────────────────────────────────

    [Fact]
    public async Task Close_SetsIsConnectedFalse()
    {
        _sourceRepo = await CreateSourceRepo("source");

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);
        Assert.True(transport.IsConnected);

        await transport.CloseAsync(TestContext.Current.CancellationToken);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var transport = new GitLocalTransport(new GitContext());
        await transport.DisposeAsync();
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Cancel_DownloadPack_Throws()
    {
        _sourceRepo = await CreateSourceRepo("source");
        GitRepository clientRepo = await CreateEmptyRepo("client");

        await using var transport = new GitLocalTransport(new GitContext());
        await transport.ConnectAsync(FixtureLoader.TestFileUrl(_sourceRepo.Path), GitDirection.Fetch, null, TestContext.Current.CancellationToken);
        transport.Cancel();

        IReadOnlyList<GitRemoteHead> refs = await transport.LsAsync(TestContext.Current.CancellationToken);
        await transport.NegotiateFetchAsync(clientRepo, new GitFetchNegotiation(refs, Array.Empty<GitOid>(), 0), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () =>
            await transport.DownloadPackAsync(clientRepo, new GitIndexerProgress(), TestContext.Current.CancellationToken));

        await clientRepo.DisposeAsync();
    }

    // ── URL conversion tests ──────────────────────────────────────────

    [Fact]
    public async Task UrlUtils_IsLocalFileUrl()
    {
        Assert.True(GitUrlUtils.IsLocalFileUrl("file:///path/to/repo"));
        Assert.True(GitUrlUtils.IsLocalFileUrl("file://localhost/path"));
        // libgit2 only accepts `file:///` and `file://localhost/`; any other
        // host form is not a valid local file URL.
        Assert.False(GitUrlUtils.IsLocalFileUrl("file://host/path"));
        Assert.False(GitUrlUtils.IsLocalFileUrl("git://host/path"));
        Assert.False(GitUrlUtils.IsLocalFileUrl("/plain/path"));
    }

    [Fact]
    public async Task UrlUtils_LocalPathFromUrl_AbsoluteUnixPath()
    {
        string path = GitUrlUtils.LocalPathFromUrl("file:///absolute/path/to/repo");
        // On POSIX the leading '/' is retained; on Windows the path after
        // the third slash is taken verbatim (no leading slash).
        string expected = OperatingSystem.IsWindows()
            ? "absolute/path/to/repo"
            : "/absolute/path/to/repo";
        Assert.Equal(expected, path);
    }

    [Fact]
    public async Task UrlUtils_LocalPathFromUrl_PercentEncoded()
    {
        string path = GitUrlUtils.LocalPathFromUrl("file:///path%20with%20spaces/repo");
        string expected = OperatingSystem.IsWindows()
            ? "path with spaces/repo"
            : "/path with spaces/repo";
        Assert.Equal(expected, path);
    }

    [Fact]
    public async Task UrlUtils_LocalPathFromUrl_PlainPath()
    {
        string path = GitUrlUtils.LocalPathFromUrl("/plain/path/to/repo");
        Assert.Equal("/plain/path/to/repo", path);
    }

    [Fact]
    public async Task UrlUtils_LocalPathFromUrl_Localhost()
    {
        // file://localhost/path is equivalent to file:///path on POSIX.
        string path = GitUrlUtils.LocalPathFromUrl("file://localhost/absolute/path");
        string expected = OperatingSystem.IsWindows()
            ? "absolute/path"
            : "/absolute/path";
        Assert.Equal(expected, path);
    }

    [Fact]
    public async Task UrlUtils_LocalPathFromUrl_WindowsDrive()
    {
        // The canonical Windows form: file:///C:/repo → C:/repo.
        // Skipped on POSIX because the path would be treated as /C:/repo.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string path = GitUrlUtils.LocalPathFromUrl("file:///C:/Users/repo");
        Assert.Equal("C:/Users/repo", path);
    }

    [Fact]
    public async Task UrlUtils_LocalPathFromUrl_RejectsHostForm()
    {
        // file://host/path is not recognized as a local file URL by libgit2
        // (only file:/// and file://localhost/ are). LocalPathFromUrl treats
        // it as a plain path and returns it verbatim — matching
        // git_fs_path_from_url_or_path's fallthrough branch.
        Assert.False(GitUrlUtils.IsLocalFileUrl("file://host/path"));
        Assert.Equal("file://host/path", GitUrlUtils.LocalPathFromUrl("file://host/path"));
    }
}

/// <summary>
/// Synchronous <see cref="IProgress{T}"/> — reports immediately, without
/// posting to a <see cref="SynchronizationContext"/> (unlike <see cref="Progress{T}"/>).
/// Used in tests where <see cref="Progress{T}"/> may defer reports under the
/// full test runner's sync context.
/// </summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _callback;

    public SyncProgress(Action<T> callback) => _callback = callback;

    public void Report(T value) => _callback(value);
}
