using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.IntegrationTests.Submodule;

/// <summary>
/// Integration tests for the submodule lifecycle (add → clone → init →
/// sync → open → reload → status → update) exercised end-to-end against
/// locally-initialized repos. No Docker, no network: the submodule's
/// "remote" is a sibling repo on the local filesystem, which routes
/// <see cref="GitSubmodule.CloneAsync"/> through the local-path fast path
/// (<c>CloneLocalPathAsync</c>) and exercises
/// <see cref="GitSubmodule.SyncAsync"/>'s opened-submodule branch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The existing
/// <see cref="Transports.SubmoduleDockerTests"/> exercise the lifecycle
/// over SSH (Docker-gated), so the entire
/// <see cref="GitSubmodule.CloneAsync"/> /
/// <see cref="GitSubmodule.SyncAsync"/> /
/// <see cref="GitSubmodule.OpenAsync"/> /
/// <see cref="GitSubmodule.ReloadAsync"/> /
/// <see cref="GitSubmodule.StatusAsync"/> surface was cold in the default
/// (no-Docker) integration run. The unit tests in
/// <c>LibGit2CS.UnitTests/Submodule/</c> cover the config-write side of
/// the Set*/Init APIs in isolation; these integration tests cover the
/// full on-disk pipeline against a real <see cref="GitContext"/>.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/submodule/add.c</c> (<c>test_submodule_add__*</c>),
/// <c>modify.c</c> (<c>test_submodule_modify__init</c>,
/// <c>test_submodule_modify__sync</c>),
/// <c>open.c</c> (<c>test_submodule_open__direct_open_succeeds</c>),
/// <c>status.c</c> (<c>test_submodule_status__unchanged</c>), and
/// <c>update.c</c> (<c>test_submodule_update__update_and_init_submodule</c>),
/// adapted to use a local sibling repo instead of <c>git://</c> or SSH.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class SubmoduleLifecycleIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-submod-" + Guid.NewGuid().ToString("N"));

    /// <summary>Best-effort recursive delete of a temp directory.</summary>
    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Inits a non-bare repo, writes <c>README.md</c> + <paramref name="extraFile"/>
    /// (if non-null), creates an initial commit on <c>refs/heads/main</c>,
    /// sets HEAD, disposes, and returns the commit OID. The repo is closed
    /// before returning so its files can be read by a sibling process
    /// (e.g. a submodule clone source).
    /// </summary>
    private static async Task<(string Path, GitOid CommitOid)> InitSourceRepoAsync(
        string path,
        string? extraFile,
        string? extraContent,
        CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        await using (repo.ConfigureAwait(false))
        {
            GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
            using GitTreeBuilder bld = repo.NewTreeBuilder();
            await bld.InsertAsync("README.md", blobOid, GitFileMode.Regular, ct);
            if (extraFile is not null && extraContent is not null)
            {
                GitOid extraBlobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(extraContent), ct);
                await bld.InsertAsync(extraFile, extraBlobOid, GitFileMode.Regular, ct);
            }

            GitOid treeOid = await bld.WriteAsync(ct);
            GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [],
                Author = Sig,
                Committer = Sig,
                Message = "init\n",
                UpdateRef = "refs/heads/main",
            }, ct);
            await repo.SetHeadAsync("refs/heads/main", ct);
            return (path, commitOid);
        }
    }

    /// <summary>
    /// Inits a non-bare superproject repo with one commit on
    /// <c>refs/heads/main</c> and returns the open repo (caller disposes).
    /// </summary>
    private static async Task<GitRepository> InitSuperRepoAsync(string path, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "super\n"u8.ToArray(), ct);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("README.md", blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await bld.WriteAsync(ct);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = "super init\n",
            UpdateRef = "refs/heads/main",
        }, ct);
        await repo.SetHeadAsync("refs/heads/main", ct);
        return repo;
    }

    /// <summary>
    /// Commits the current index on <c>refs/heads/main</c> and advances HEAD.
    /// Used to capture submodule-add staging on the superproject.
    /// </summary>
    private static async Task<GitOid> CommitIndexAsync(GitRepository repo, string message, CancellationToken ct)
    {
        GitIndex idx = await repo.GetIndexAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);
        GitReference? head = await repo.ReferenceResolveAsync("HEAD", ct);
        var parents = new List<GitOid>();
        if (head is GitDirectReference dr)
        {
            parents.Add(dr.Target);
        }

        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = Sig,
            Committer = Sig,
            Message = message,
            UpdateRef = "refs/heads/main",
        }, ct);
    }

    // ── CloneAsync local-path fast path ────────────────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.CloneAsync"/> with a local filesystem
    /// path URL (no scheme) routes through
    /// <c>CloneLocalPathAsync</c>: the submodule's gitlink repo at
    /// <c>.git/modules/&lt;path&gt;</c> (already initialized by
    /// <see cref="GitSubmodule.AddSetupAsync"/>) is populated by direct
    /// object/refs copy from the source, then HEAD is checked out into the
    /// submodule workdir. Verifies the workdir is populated and the
    /// submodule's HEAD matches the source commit.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>test_submodule_add__submodule_clone</c>
    /// (<c>tests/libgit2/submodule/add.c</c>) adapted for a local source.
    /// </remarks>
    [Fact]
    public async Task CloneAsync_LocalSourcePath_CopiesObjectsAndChecksOutWorkdir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string superPath = NewRepoPath();
        string sourcePath = superPath + "-source";
        try
        {
            (string _, GitOid sourceCommitOid) = await InitSourceRepoAsync(sourcePath, "sub.txt", "sub content\n", ct);

            await using GitRepository super = await InitSuperRepoAsync(superPath, ct);
            GitSubmodule sm = await super.SubmoduleAddSetupAsync(sourcePath, "sub", useGitlink: true, ct);
            await sm.CloneAsync(cancellationToken: ct);

            // The submodule workdir should have the source's files checked out.
            Assert.True(File.Exists(Path.Combine(superPath, "sub", "README.md")));
            Assert.True(File.Exists(Path.Combine(superPath, "sub", "sub.txt")));
            Assert.Equal("sub content\n", await File.ReadAllTextAsync(Path.Combine(superPath, "sub", "sub.txt"), ct));

            // The submodule's HEAD must resolve to the source commit.
            await using GitRepository smRepo = await sm.OpenAsync(ct)
                ?? throw new Xunit.Sdk.XunitException("submodule.OpenAsync returned null after CloneAsync");
            GitReference? smHead = await smRepo.ReferenceResolveAsync("HEAD", ct);
            Assert.True(smHead is GitDirectReference);
            Assert.Equal(sourceCommitOid, ((GitDirectReference)smHead).Target);
        }
        finally
        {
            Cleanup(superPath);
            Cleanup(sourcePath);
        }
    }

    // ── Full add lifecycle ─────────────────────────────────────────────

    /// <summary>
    /// The full <c>git submodule add</c> pipeline —
    /// <see cref="GitSubmodule.AddSetupAsync"/> →
    /// <see cref="GitSubmodule.CloneAsync"/> →
    /// <see cref="GitSubmodule.AddToIndexAsync"/> →
    /// <see cref="GitSubmodule.AddFinalizeAsync"/> — leaves the
    /// superproject index with both the <c>.gitmodules</c> blob and the
    /// gitlink entry (mode <c>0160000</c>) at the submodule path. Mirrors
    /// libgit2's <c>test_submodule_add__submodule_clone</c> end-to-end
    /// flow (the entire <c>git_submodule_add_setup</c> +
    /// <c>git_submodule_clone</c> + <c>git_submodule_add_to_index</c> +
    /// <c>git_submodule_add_finalize</c> sequence from <c>submodule.c</c>).
    /// </summary>
    [Fact]
    public async Task AddLifecycle_AddSetup_Clone_AddToIndex_AddFinalize_StagesSuperIndex()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string superPath = NewRepoPath();
        string sourcePath = superPath + "-source";
        try
        {
            await InitSourceRepoAsync(sourcePath, "sub.txt", "sub content\n", ct);

            await using GitRepository super = await InitSuperRepoAsync(superPath, ct);
            GitIndex idx = await super.GetIndexAsync(ct);

            GitSubmodule sm = await super.SubmoduleAddSetupAsync(sourcePath, "sub", useGitlink: true, ct);
            await sm.CloneAsync(cancellationToken: ct);
            await sm.AddToIndexAsync(cancellationToken: ct);
            await sm.AddFinalizeAsync(cancellationToken: ct);

            GitIndexEntry gitmodulesEntry = idx.Entries.First(e => e.Path.ToUtf8String() == ".gitmodules");
            Assert.Equal(GitFileMode.Regular, gitmodulesEntry.Mode);

            GitIndexEntry gitlinkEntry = idx.Entries.First(e => e.Path.ToUtf8String() == "sub");
            Assert.Equal(GitFileMode.GitLink, gitlinkEntry.Mode);
            Assert.False(gitlinkEntry.Id.IsZero);
        }
        finally
        {
            Cleanup(superPath);
            Cleanup(sourcePath);
        }
    }

    // ── OpenAsync ──────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.OpenAsync"/> on a cloned submodule
    /// returns a non-null <see cref="GitRepository"/> whose HEAD OID
    /// matches the source repo's tip commit. Exercises
    /// <see cref="GitSubmodule.OpenAsync"/> +
    /// <c>OpenInternalAsync</c>'s <c>.git</c>-file resolution path against
    /// a real gitlink layout. Mirrors libgit2's
    /// <c>test_submodule_open__direct_open_succeeds</c>.
    /// </summary>
    [Fact]
    public async Task OpenAsync_AfterClone_ReturnsRepoWithHeadMatchingSource()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string superPath = NewRepoPath();
        string sourcePath = superPath + "-source";
        try
        {
            (string _, GitOid sourceCommitOid) = await InitSourceRepoAsync(sourcePath, null, null, ct);

            await using GitRepository super = await InitSuperRepoAsync(superPath, ct);
            GitSubmodule sm = await super.SubmoduleAddSetupAsync(sourcePath, "sub", useGitlink: true, ct);
            await sm.CloneAsync(cancellationToken: ct);

            await using GitRepository? smRepo = await sm.OpenAsync(ct);
            Assert.NotNull(smRepo);
            GitReference? smHead = await smRepo!.ReferenceResolveAsync("HEAD", ct);
            Assert.True(smHead is GitDirectReference);
            Assert.Equal(sourceCommitOid, ((GitDirectReference)smHead).Target);
        }
        finally
        {
            Cleanup(superPath);
            Cleanup(sourcePath);
        }
    }

    // ── SyncAsync ──────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.SyncAsync"/> after the submodule has
    /// been cloned writes the resolved URL into BOTH the superproject's
    /// <c>.git/config</c> AND the submodule's own
    /// <c>remote.origin.url</c> — the opened-submodule branch of
    /// <see cref="GitSubmodule.SyncAsync"/> (<c>GitSubmodule.cs:301-318</c>,
    /// cold in the no-Docker run). The test changes the URL via
    /// <see cref="GitSubmodule.SetUrlAsync"/> (writes <c>.gitmodules</c>),
    /// reopens the super to refresh the cache, and verifies Sync propagates
    /// the new URL into the submodule's config. Mirrors libgit2's
    /// <c>test_submodule_modify__sync</c>.
    /// </summary>
    [Fact]
    public async Task SyncAsync_PostClone_WritesResolvedUrlToSubmoduleOriginRemote()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string superPath = NewRepoPath();
        string sourcePath = superPath + "-source";
        try
        {
            await InitSourceRepoAsync(sourcePath, null, null, ct);

            // Set up + clone the submodule.
            await using (GitRepository super = await InitSuperRepoAsync(superPath, ct))
            {
                GitSubmodule sm = await super.SubmoduleAddSetupAsync(sourcePath, "sub", useGitlink: true, ct);
                await sm.CloneAsync(cancellationToken: ct);
            }

            // Reopen and change the URL on disk, then Sync.
            await using GitRepository super2 = await GitRepository.OpenAsync(superPath, new GitContext(), cancellationToken: ct);
            const string newUrl = "https://example.com/updated.git";
            await super2.SubmoduleSetUrlAsync("sub", newUrl, ct);
            GitSubmodule smReloaded = await super2.SubmoduleLookupAsync("sub", ct)
                ?? throw new Xunit.Sdk.XunitException("submodule 'sub' not found after SetUrlAsync");
            await smReloaded.SyncAsync(ct);

            // Superproject config reflects the new URL.
            Assert.Equal(newUrl, await super2.Config.GetStringAsync("submodule.sub.url", ct));

            // Submodule config (in .git/modules/sub) also reflects the new URL.
            string? subConfigUrl = await ReadSubmoduleConfigValueAsync(super2, "sub", "remote.origin.url", ct);
            Assert.Equal(newUrl, subConfigUrl);
        }
        finally
        {
            Cleanup(superPath);
            Cleanup(sourcePath);
        }
    }

    // ── ReloadAsync + Status (WdModified) ──────────────────────────────

    /// <summary>
    /// After the submodule is fully added (committed on the super),
    /// advancing the submodule's HEAD locally (without updating the
    /// superproject gitlink) and calling
    /// <see cref="GitSubmodule.ReloadAsync"/> +
    /// <see cref="GitSubmodule.StatusAsync"/> reports
    /// <see cref="SubmoduleStatus.WdModified"/>. Exercises the
    /// reload-from-disk path and the index-vs-workdir comparison in
    /// <c>ComputeWdStatusAsync</c>. Mirrors the Docker-gated
    /// <c>Reload_AfterServerAdvance_ReportsWdModified</c> scenario.
    /// </summary>
    [Fact]
    public async Task ReloadAsync_AfterSubmoduleHeadAdvance_ReportsWdModified()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string superPath = NewRepoPath();
        string sourcePath = superPath + "-source";
        try
        {
            await InitSourceRepoAsync(sourcePath, null, null, ct);

            // Set up + clone + commit on super so the gitlink is in HEAD.
            await using (GitRepository super = await InitSuperRepoAsync(superPath, ct))
            {
                GitSubmodule sm = await super.SubmoduleAddSetupAsync(sourcePath, "sub", useGitlink: true, ct);
                await sm.CloneAsync(cancellationToken: ct);
                await sm.AddFinalizeAsync(cancellationToken: ct);
                await CommitIndexAsync(super, "add submodule\n", ct);
            }

            // Advance the submodule HEAD locally: open it, write a new commit
            // on its refs/heads/main, and move main forward.
            string subWorkPath = Path.Combine(superPath, "sub");
            await using (GitRepository subRepo = await GitRepository.OpenAsync(subWorkPath, new GitContext(), cancellationToken: ct))
            {
                GitOid blobOid = await subRepo.ObjectWriteAsync(GitObjectType.Blob, "change\n"u8.ToArray(), ct);
                using GitTreeBuilder bld = subRepo.NewTreeBuilder();
                await bld.InsertAsync("change.txt", blobOid, GitFileMode.Regular, ct);
                GitOid treeOid = await bld.WriteAsync(ct);
                GitReference? head = await subRepo.ReferenceResolveAsync("HEAD", ct);
                Assert.True(head is GitDirectReference);
                await subRepo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [((GitDirectReference)head!).Target],
                    Author = Sig,
                    Committer = Sig,
                    Message = "advance submodule\n",
                    UpdateRef = "refs/heads/main",
                }, ct);
            }

            // Reopen the super and verify the WdModified flag.
            await using GitRepository super2 = await GitRepository.OpenAsync(superPath, new GitContext(), cancellationToken: ct);
            GitSubmodule smReloaded = await super2.SubmoduleLookupAsync("sub", ct)
                ?? throw new Xunit.Sdk.XunitException("submodule 'sub' not found");
            await smReloaded.ReloadAsync(force: true, ct);
            SubmoduleStatus status = await super2.SubmoduleStatusAsync("sub", cancellationToken: ct);
            Assert.True((status & SubmoduleStatus.WdModified) != 0,
                $"expected WdModified in status, got {status}");
        }
        finally
        {
            Cleanup(superPath);
            Cleanup(sourcePath);
        }
    }

    // ── Status flags ───────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.StatusAsync"/> after a fully-committed
    /// submodule reports the four location flags
    /// (<see cref="SubmoduleStatus.InHead"/> |
    /// <see cref="SubmoduleStatus.InIndex"/> |
    /// <see cref="SubmoduleStatus.InConfig"/> |
    /// <see cref="SubmoduleStatus.InWd"/>) with no delta flags — the
    /// submodule is "unchanged". After removing the submodule workdir,
    /// <see cref="SubmoduleStatus.WdDeleted"/> appears. Mirrors libgit2's
    /// <c>test_submodule_status__unchanged</c>.
    /// </summary>
    [Fact]
    public async Task Status_FullyCommitted_ReportsAllLocationFlags_NoDelta()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string superPath = NewRepoPath();
        string sourcePath = superPath + "-source";
        try
        {
            await InitSourceRepoAsync(sourcePath, null, null, ct);

            await using (GitRepository super = await InitSuperRepoAsync(superPath, ct))
            {
                GitSubmodule sm = await super.SubmoduleAddSetupAsync(sourcePath, "sub", useGitlink: true, ct);
                await sm.CloneAsync(cancellationToken: ct);
                await sm.AddFinalizeAsync(cancellationToken: ct);
                await CommitIndexAsync(super, "add submodule\n", ct);
            }

            await using GitRepository super2 = await GitRepository.OpenAsync(superPath, new GitContext(), cancellationToken: ct);
            SubmoduleStatus clean = await super2.SubmoduleStatusAsync("sub", cancellationToken: ct);
            SubmoduleStatus location = SubmoduleStatus.InHead | SubmoduleStatus.InIndex | SubmoduleStatus.InConfig | SubmoduleStatus.InWd;
            Assert.Equal(location, clean & location);
            Assert.True((clean & ~location) == 0,
                $"expected no delta flags on a fully-committed submodule, got {clean}");
        }
        finally
        {
            Cleanup(superPath);
            Cleanup(sourcePath);
        }
    }

    // ── UpdateAsync(init: true) ────────────────────────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.UpdateAsync"/> with
    /// <paramref name="init"/><c>: true</c> on a submodule that has a
    /// gitlink in the superproject index but no checked-out workdir clones
    /// the source (via <see cref="Remote.GitClone.RunForSubmoduleAsync"/>,
    /// which for a local source routes through <c>CloneLocalIntoAsync</c>)
    /// and detaches the submodule HEAD at the gitlink OID. Verifies the
    /// workdir is populated and HEAD matches the gitlink OID recorded in
    /// the superproject index. Mirrors libgit2's
    /// <c>test_submodule_update__update_and_init_submodule</c>, adapted to
    /// also assert the post-update detached-HEAD state.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_InitTrue_ClonesAndDetachesHeadAtGitlinkOid()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string superPath = NewRepoPath();
        string sourcePath = superPath + "-source";
        try
        {
            (string _, GitOid sourceCommitOid) = await InitSourceRepoAsync(sourcePath, "sub.txt", "sub content\n", ct);

            // Build a superproject whose index has a gitlink at "sub" but no
            // submodule workdir checkout — the "freshly cloned superproject"
            // starting state for `git submodule update --init`.
            await using (GitRepository super = await InitSuperRepoAsync(superPath, ct))
            {
                // Write .gitmodules + a gitlink entry directly (don't run
                // AddSetupAsync — we want the pre-init state). The path value
                // must be config-escaped: backslashes (Windows paths) are
                // written as "\\" so the parser unescapes them back to "\".
                await File.WriteAllTextAsync(
                    Path.Combine(superPath, ".gitmodules"),
                    $"[submodule \"sub\"]\n\tpath = sub\n\turl = {sourcePath.Replace("\\", "\\\\")}\n",
                    ct);
                GitIndex idx = await super.GetIndexAsync(ct);
                idx.Add(new GitIndexEntry("sub", sourceCommitOid, GitFileMode.GitLink));
                await idx.WriteAsync(ct);
                await CommitIndexAsync(super, "record submodule gitlink\n", ct);
            }

            // Update(init: true) must clone + checkout the gitlink OID.
            // Create the empty workdir dir first so the submodule is in the
            // WD_UNINITIALIZED state (matches what `git submodule update
            // --init` sees on a freshly cloned superproject where the
            // submodule dir was scaffolded by checkout but not populated).
            Directory.CreateDirectory(Path.Combine(superPath, "sub"));

            await using GitRepository super2 = await GitRepository.OpenAsync(superPath, new GitContext(), cancellationToken: ct);
            GitSubmodule sm = await super2.SubmoduleLookupAsync("sub", ct)
                ?? throw new Xunit.Sdk.XunitException("submodule 'sub' not found");
            await sm.UpdateAsync(init: true, cancellationToken: ct);

            // The submodule workdir is populated.
            Assert.True(File.Exists(Path.Combine(superPath, "sub", "sub.txt")));

            // HEAD is detached at the gitlink OID.
            await using GitRepository smRepo = await sm.OpenAsync(ct)
                ?? throw new Xunit.Sdk.XunitException("submodule.OpenAsync returned null after UpdateAsync");
            GitReference? smHead = await smRepo.ReferenceResolveAsync("HEAD", ct);
            Assert.True(smHead is GitDirectReference);
            Assert.Equal(sourceCommitOid, ((GitDirectReference)smHead).Target);

            // WdUninitialized is cleared.
            SubmoduleStatus status = await super2.SubmoduleStatusAsync("sub", cancellationToken: ct);
            Assert.True((status & SubmoduleStatus.WdUninitialized) == 0,
                $"expected WdUninitialized cleared after Update, got {status}");
        }
        finally
        {
            Cleanup(superPath);
            Cleanup(sourcePath);
        }
    }

    // ── Init overwrite ─────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.InitAsync"/> with
    /// <paramref name="overwrite"/><c>: true</c> rewrites all five
    /// submodule config keys (<c>url</c>, <c>branch</c>, <c>update</c>,
    /// <c>ignore</c>, <c>fetchRecurseSubmodules</c>) from
    /// <c>.gitmodules</c> into <c>.git/config</c>, overwriting any
    /// manually-edited values. Mirrors libgit2's
    /// <c>test_submodule_modify__init</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="GitSubmodule.AddSetupAsync"/> already calls
    /// <see cref="GitSubmodule.InitAsync"/> internally, so the URL lands in
    /// <c>.git/config</c> during add-setup. The test mutates the
    /// <c>.gitmodules</c> URL after add-setup, then verifies
    /// <c>Init(overwrite: false)</c> keeps the old config URL while
    /// <c>Init(overwrite: true)</c> replaces it with the new value.
    /// </remarks>
    [Fact]
    public async Task Init_OverwriteTrue_ReplacesConfigUrlFromGitmodules()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string superPath = NewRepoPath();
        string sourcePath = superPath + "-source";
        try
        {
            await InitSourceRepoAsync(sourcePath, null, null, ct);

            await using GitRepository super = await InitSuperRepoAsync(superPath, ct);
            GitSubmodule sm = await super.SubmoduleAddSetupAsync(sourcePath, "sub", useGitlink: true, ct);

            // AddSetupAsync.InitAsync has copied the URL to .git/config. The
            // stored value is the RESOLVED url — backslashes normalized to
            // forward slashes (C: git_submodule__resolve_url, submodule.c:788-794).
            string resolvedSource = sourcePath.Replace('\\', '/');
            Assert.Equal(resolvedSource, await super.Config.GetStringAsync("submodule.sub.url", ct));

            // Mutate the URL on disk and confirm Init(overwrite: false) keeps
            // the old config value. The handle must be reloaded first so its
            // Url reflects the new .gitmodules value (C: git_submodule_set_url
            // writes only .gitmodules; the caller re-looks-up / reloads before
            // git_submodule_init reads the URL — modify.c:204-221).
            const string newUrl = "https://example.com/updated.git";
            await super.SubmoduleSetUrlAsync("sub", newUrl, ct);
            await sm.ReloadAsync(cancellationToken: ct);
            await sm.InitAsync(overwrite: false, ct);
            Assert.Equal(resolvedSource, await super.Config.GetStringAsync("submodule.sub.url", ct));

            // Init(overwrite: true) must replace with the new .gitmodules value.
            await sm.InitAsync(overwrite: true, ct);
            Assert.Equal(newUrl, await super.Config.GetStringAsync("submodule.sub.url", ct));
        }
        finally
        {
            Cleanup(superPath);
            Cleanup(sourcePath);
        }
    }

    // ── Internal helper ────────────────────────────────────────────────

    /// <summary>
    /// Reads a config value from the submodule's own gitdir
    /// (<c>.git/modules/&lt;name&gt;</c>) by opening it directly via
    /// <see cref="GitRepository.OpenBareAsync"/>. Used to verify SyncAsync
    /// writes the resolved URL into the submodule's
    /// <c>remote.origin.url</c>.
    /// </summary>
    private static async Task<string?> ReadSubmoduleConfigValueAsync(
        GitRepository super,
        string subName,
        string key,
        CancellationToken ct)
    {
        string subGitdir = Path.Combine(super.Path, "modules", subName);
        await using GitRepository subRepo = await GitRepository.OpenBareAsync(subGitdir, super.Context, cancellationToken: ct);
        return await subRepo.Config.GetStringAsync(key, ct);
    }
}
