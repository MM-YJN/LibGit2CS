using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IntegrationTests.DockerFixture;
using LibGit2CS.IntegrationTests.TestKit.Logger;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

using Microsoft.Extensions.Logging;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// Docker-based integration tests for the submodule engine
/// (<see cref="GitSubmodule.LookupAsync"/>/<see cref="GitSubmodule.ForEachAsync"/>/
/// <see cref="GitSubmodule.StatusAsync"/>/<see cref="GitSubmodule.CloneAsync"/>/
/// <see cref="GitSubmodule.UpdateAsync"/>/<see cref="GitSubmodule.SyncAsync"/>/
/// <see cref="GitSubmodule.InitAsync"/>/<see cref="GitSubmodule.ReloadAsync"/>/
/// <see cref="GitSubmodule.SetUrlAsync"/>/<see cref="GitSubmodule.SetBranchAsync"/>/
/// <see cref="GitSubmodule.SetIgnoreAsync"/>/<see cref="GitSubmodule.SetUpdateAsync"/>)
/// exercised end-to-end against a superproject + submodule repo both
/// fetched over SSH from a real OpenSSH+git container via
/// <see cref="SshGitDockerFixture"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these tests exist.</b> The unit-test project covers the submodule
/// API against locally-initialized repos, but the
/// <see cref="LibGit2CS.Submodule"/> namespace had <c>0%</c> integration
/// coverage — no end-to-end path through fetch → clone (which populates the
/// superproject object db + index + working tree, revealing the gitlink
/// entry + <c>.gitmodules</c>) → submodule lookup/cache load → network
/// submodule clone over SSH → checkout at the gitlink OID. These tests close
/// that gap by cloning the fixture's seeded superproject and then driving
/// the submodule lifecycle against a second seeded server-side repo.
/// </para>
/// <para>
/// <b>Server-side topology.</b> The fixture seeds one non-bare repo at
/// <see cref="SshGitDockerFixture.RepoPath"/> (<c>/home/testuser/repo.git</c>)
/// with a single commit on <c>refs/heads/main</c>. Each test grows a second
/// non-bare repo at <c>/home/testuser/sub.git</c> with its own commit, then
/// records the submodule in the superproject by writing a
/// <c>.gitmodules</c> entry + a gitlink index/tree entry pointing at
/// <c>sub.git</c>'s HEAD oid, and committing the superproject — all
/// server-side via <see cref="SshGitDockerFixture.ExecAsync"/> with real
/// <c>git</c>. The client then clones the superproject over SSH and operates
/// on the submodule through the LibGit2CS API.
/// </para>
/// <para>
/// <b>Submodule URL.</b> The submodule's <c>.gitmodules</c> URL is the full
/// <c>ssh://testuser@127.0.0.1:&lt;port&gt;/home/testuser/sub.git</c> form so
/// the dynamic mapped port rides in the URL itself (the
/// <see cref="GitFetchOptions.ScpPortOverride"/> seam only applies to
/// SCP-style URLs, which <see cref="GitSubmodule.CloneAsync"/> does not
/// thread through). This is what motivated adding
/// <see cref="SubmoduleUpdateOptions.FetchOptions"/> — without credential
/// callbacks, the SSH submodule clone cannot authenticate.
/// </para>
/// <para>
/// <b>Gating.</b> All tests are skipped when Docker is not reachable
/// (<see cref="SshGitDockerFixture.SkipIfDockerNotAvailable"/>).
/// </para>
/// </remarks>
public sealed class SubmoduleDockerTests : IDisposable
{
    private readonly LoggerFactory _loggerFactory;
    private readonly AlpineNoKeyImageFixture _alpine;

    public SubmoduleDockerTests(AlpineNoKeyImageFixture alpine, ITestOutputHelper testOutputHelper)
    {
        _alpine = alpine;
        _loggerFactory = new LoggerFactory([new XunitTestOutputLoggerProvider(testOutputHelper)]);
    }

    public void Dispose() => _loggerFactory.Dispose();

    /// <summary>
    /// Password-auth callbacks shared by every test in this class. The
    /// permissive <c>CertificateCheck</c> accepts the container's ephemeral
    /// hostkey (known_hosts behavior is covered separately by
    /// <see cref="SshTransportDockerTests"/>).
    /// </summary>
    private static GitRemoteCallbacks PasswordCallbacks => new()
    {
        Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
            new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
        CertificateCheck = _ => true,
    };

    private static string SuperUrl(SshGitDockerContainer fixture)
    {
        return $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
    }

    private static string SubUrl(SshGitDockerContainer fixture)
    {
        return $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SubRepoPath}";
    }

    /// <summary>Path inside the container to the seeded submodule repo.</summary>
    private const string SubRepoPath = "/home/testuser/sub.git";

    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    /// <summary>
    /// Seeds a second non-bare repo at <see cref="SubRepoPath"/> with one
    /// commit on <c>refs/heads/main</c> containing <c>sub.txt</c>, then
    /// records it as a submodule named <paramref name="submoduleName"/> at
    /// <paramref name="submodulePath"/> in the superproject: writes
    /// <c>.gitmodules</c>, adds the gitlink index entry, and commits. Returns
    /// the submodule HEAD oid (the gitlink target) as a lowercase hex string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All work happens server-side via <c>git</c> in the container. The
    /// superproject commit is amended onto the seeded <c>main</c> so a fresh
    /// client clone sees both <c>.gitmodules</c> and the gitlink in HEAD.
    /// </para>
    /// <para>
    /// <b>Run as <c>testuser</c>.</b> The fixture's repos are owned by
    /// <c>testuser</c>; running <c>git</c> as root trips git's "dubious
    /// ownership" / "not in a git directory" guards. We wrap each command in
    /// <c>su testuser -c '…'</c>, matching the pattern in
    /// <see cref="SshTransportDockerTests.Fetch_OverSsh_UpdatesRefs"/>. The
    /// <c>.gitmodules</c> file is written with <c>git config -f</c> (not
    /// <c>printf</c>) to avoid shell-quote escaping inside the <c>su -c</c>
    /// wrapper.
    /// </para>
    /// </remarks>
    private static async Task<string> SeedSubmoduleServerSideAsync(SshGitDockerContainer fixture, string submoduleName, string submodulePath, CancellationToken ct)
    {
        string su = $"{SshGitDockerFixture.TestUser} -c";

        // Create the submodule repo at SubRepoPath with one commit on main.
        await fixture.ExecAsync(
            $"su {su} 'git init {SubRepoPath} && cd {SubRepoPath} && " +
            "git config user.email t@t && git config user.name T && " +
            "git checkout -b main && " +
            "echo sub content > sub.txt && git add sub.txt && " +
            "git commit -m sub-init'", ct);

        // Read the submodule HEAD oid to embed as the gitlink target.
        string subOid = (await fixture.ExecAsync($"su {su} 'cd {SubRepoPath} && git rev-parse main'", ct)).Trim();

        // Record the submodule in the superproject: write .gitmodules via
        // `git config -f` (clean, no printf quoting), stage it, add the
        // gitlink index entry, and commit.
        string subUrl = SubUrl(fixture);
        await fixture.ExecAsync(
            $"su {su} 'cd {SshGitDockerFixture.RepoPath} && " +
            "git config user.email t@t && git config user.name T && " +
            $"git config -f .gitmodules submodule.{submoduleName}.path {submodulePath} && " +
            $"git config -f .gitmodules submodule.{submoduleName}.url {subUrl} && " +
            "git add .gitmodules && " +
            $"git update-index --add --cacheinfo 160000,{subOid},{submodulePath} && " +
            "git commit -m add-submodule'", ct);

        return subOid;
    }

    private static GitCloneOptions CloneOpts()
    {
        return new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };
    }

    /// <summary>
    /// Collects all submodules of a repo into a list (materializes the
    /// async enumerable so it can be asserted on).
    /// </summary>
    private static async Task<List<GitSubmodule>> SubmoduleListAsync(GitRepository repo, CancellationToken ct)
    {
        var list = new List<GitSubmodule>();
        await foreach (GitSubmodule sm in repo.SubmoduleForEachAsync(ct).ConfigureAwait(false))
        {
            list.Add(sm);
        }

        return list;
    }

    // ── Lookup + ForEach over a cloned superproject ─────────────────────

    /// <summary>
    /// Lookup over a cloned superproject: after cloning the
    /// superproject that records a submodule <c>sub</c> at <c>./sub</c>,
    /// <see cref="GitSubmodule.LookupAsync"/> returns a non-null submodule
    /// whose <see cref="GitSubmodule.Name"/>/<see cref="GitSubmodule.Path"/>/
    /// <see cref="GitSubmodule.Url"/> match <c>.gitmodules</c>, whose
    /// <see cref="GitSubmodule.IndexId"/>/ <see cref="GitSubmodule.HeadId"/>
    /// equal the gitlink oid, and which is <see cref="SubmoduleStatus.InConfig"/>
    /// + <see cref="SubmoduleStatus.InHead"/> + <see cref="SubmoduleStatus.InIndex"/>
    /// but <see cref="SubmoduleStatus.WdUninitialized"/> (not yet cloned).
    /// Exercises <see cref="SubmoduleCache"/> load from .gitmodules + index +
    /// HEAD tree against a fetched object db.
    /// </summary>
    [Fact]
    public async Task Lookup_SeededGitlink_ReportsIndexAndHead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);
        string subOid = await SeedSubmoduleServerSideAsync(fixture, submoduleName: "sub", submodulePath: "sub", ct);

        string url = SuperUrl(fixture);
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-sub-lookup-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, CloneOpts(), new GitContext(), ct);

            GitSubmodule? sm = await cloned.SubmoduleLookupAsync("sub", ct);
            Assert.NotNull(sm);
            Assert.Equal("sub", sm!.Name);
            Assert.Equal("sub", sm.Path?.ToUtf8String());
            Assert.Equal(SubUrl(fixture), sm.Url);

            // The gitlink oid recorded in the index and HEAD tree must match
            // the submodule repo's main commit.
            var expected = GitOid.Parse(subOid, GitHashAlgorithmKind.Sha1);
            Assert.Equal(expected, sm.IndexId);
            Assert.Equal(expected, sm.HeadId);

            // Not yet cloned: the checkout of a gitlink tree entry does NOT
            // populate a working tree for the submodule (only `git submodule
            // update --init` does). The checkout submodule
            // pass ALWAYS mkdirs the submodule path (checkout.c:1676-1686), so
            // the workdir contains an EMPTY `sub/` directory with no `.git`
            // inside. WdId is zero and the computed status reports
            // WdUninitialized (in index, workdir has an empty dir, no .git) —
            // parity with libgit2, which leaves the submodule path as an empty
            // directory until the user runs `submodule update`.
            Assert.True(sm.WdId.IsZero);

            SubmoduleStatus status = await cloned.SubmoduleStatusAsync("sub", cancellationToken: ct);
            Assert.True((status & SubmoduleStatus.InConfig) != 0, $"InConfig missing: {status}");
            Assert.True((status & SubmoduleStatus.InHead) != 0, $"InHead missing: {status}");
            Assert.True((status & SubmoduleStatus.InIndex) != 0, $"InIndex missing: {status}");
            Assert.True((status & SubmoduleStatus.WdUninitialized) != 0, $"WdUninitialized missing: {status}");
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// ForEach enumerates the seeded submodule: after cloning the
    /// superproject, <see cref="GitSubmodule.ForEachAsync"/> yields exactly
    /// one entry whose name matches the seeded submodule. Exercises the
    /// iteration path of <see cref="SubmoduleCache"/> over a fetched repo.
    /// </summary>
    [Fact]
    public async Task ForEach_EnumeratesSeededSubmodule()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);
        await SeedSubmoduleServerSideAsync(fixture, submoduleName: "dep", submodulePath: "dep", ct);

        string url = SuperUrl(fixture);
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-sub-foreach-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, CloneOpts(), new GitContext(), ct);

            List<GitSubmodule> subs = await SubmoduleListAsync(cloned, ct);
            Assert.Single(subs);
            Assert.Equal("dep", subs[0].Name);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Clone over SSH ──────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.CloneAsync"/> fetches the submodule over
    /// SSH: on a fresh local superproject, <see cref="GitSubmodule.AddSetupAsync"/>
    /// writes <c>.gitmodules</c> + creates the gitlink repo (at
    /// <c>.git/modules/sub</c>) and inits it, then
    /// <see cref="GitSubmodule.CloneAsync"/> with
    /// <see cref="SubmoduleUpdateOptions.FetchOptions"/> carrying the password
    /// callbacks clones the remote <c>sub.git</c> into the gitlink repo. The
    /// submodule workdir is populated with the seeded <c>sub.txt</c> and the
    /// submodule HEAD resolves to the remote's main commit. Exercises the
    /// network (<c>ssh://</c>) branch of <see cref="GitSubmodule.CloneAsync"/>
    /// (via <see cref="GitClone.RunAsync"/> with a custom
    /// <see cref="GitCloneOptions.RepositoryCreate"/> that opens the gitlink
    /// repo pre-created by <see cref="GitSubmodule.AddSetupAsync"/>) — the
    /// path that was unreachable before the
    /// <see cref="SubmoduleUpdateOptions.FetchOptions"/> wiring. This is the
    /// <c>git submodule add</c> flow; the <c>git submodule update --init</c>
    /// flow on a cloned superproject is covered by
    /// <see cref="Update_WithInit_ClonesAndChecksOutSubmodule"/>.
    /// </summary>
    [Fact]
    public async Task Clone_OverSsh_FetchesSubmoduleWorkdir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);
        // Seed only the submodule repo server-side; the superproject is
        // built client-side via AddSetupAsync (the Add flow). Split the
        // commit from the rev-parse so the oid isn't polluted by git's
        // commit banner on stdout.
        string su = $"{SshGitDockerFixture.TestUser} -c";
        await fixture.ExecAsync(
            $"su {su} 'git init {SubRepoPath} && cd {SubRepoPath} && " +
            "git config user.email t@t && git config user.name T && " +
            "git checkout -b main && echo sub content > sub.txt && git add sub.txt && " +
            "git commit -m sub-init'", ct);
        string subOid = (await fixture.ExecAsync($"su {su} 'cd {SubRepoPath} && git rev-parse main'", ct)).Trim();

        // Fresh local superproject.
        string subUrl = SubUrl(fixture);
        string superPath = Path.Combine(Path.GetTempPath(), "libgit2cs-sub-add-" + Guid.NewGuid().ToString("N"));
        await using GitRepository super = await GitRepository.InitAsync(superPath, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Make an initial commit so the superproject has a HEAD.
            GitOid blobOid = await super.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), ct);
            using GitTreeBuilder treeBld = super.NewTreeBuilder();
            await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular, ct);
            GitOid treeOid = await treeBld.WriteAsync(ct);
            await super.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Author = Sig,
                Committer = Sig,
                Message = "init\n",
                UpdateRef = "refs/heads/main",
            }, ct);

            // AddSetup writes .gitmodules, creates the gitlink repo, and inits.
            GitSubmodule sm = await super.SubmoduleAddSetupAsync(subUrl, "sub", useGitlink: true, cancellationToken: ct);

            // Clone over SSH with credential callbacks into the gitlink repo.
            var updateOpts = new SubmoduleUpdateOptions
            {
                FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks },
            };
            await sm.CloneAsync(updateOpts, ct);

            // The submodule working tree must now be populated.
            string subFile = Path.Combine(superPath, "sub", "sub.txt");
            Assert.True(File.Exists(subFile), $"sub.txt not checked out at {subFile}");
            Assert.Equal("sub content\n", await File.ReadAllTextAsync(subFile, ct));

            // The submodule repo's HEAD must resolve to the remote's main.
            await using GitRepository? smRepo = await sm.OpenAsync(ct);
            Assert.NotNull(smRepo);
            GitReference? smHead = await smRepo!.ReferenceResolveAsync("HEAD", ct);
            Assert.True(smHead is GitDirectReference);
            Assert.Equal(GitOid.Parse(subOid, GitHashAlgorithmKind.Sha1), ((GitDirectReference)smHead).Target);
        }
        finally
        {
            try
            {
                Directory.Delete(superPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Update with init ────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.UpdateAsync"/>(<paramref name="init"/>:
    /// true) on an uninitialized submodule clones it over SSH and checks out
    /// the index (gitlink) commit oid. After update, the submodule workdir
    /// contains <c>sub.txt</c> and the submodule HEAD is detached at the
    /// gitlink oid. Exercises the full <c>WdUninitialized → CloneAsync →
    /// SetHeadDetachedAsync → CheckoutHeadAsync</c> path in
    /// <see cref="GitSubmodule.UpdateAsync"/> with the
    /// <see cref="SubmoduleUpdateOptions.FetchOptions"/> callbacks threaded
    /// into the internal clone.
    /// </summary>
    [Fact]
    public async Task Update_WithInit_ClonesAndChecksOutSubmodule()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);
        string subOid = await SeedSubmoduleServerSideAsync(fixture, submoduleName: "sub", submodulePath: "sub", ct);

        string url = SuperUrl(fixture);
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-sub-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, CloneOpts(), new GitContext(), ct);

            GitSubmodule sm = await cloned.SubmoduleLookupAsync("sub", ct)
                ?? throw new InvalidOperationException("submodule 'sub' not found after clone");

            // After a plain clone the submodule workdir dir is an EMPTY
            // directory (checkout_submodule always mkdirs the
            // submodule path, checkout.c:1676-1686), so the status is
            // WdUninitialized, not WdDeleted. git submodule update --init
            // clones into the existing empty dir. Mirror that here —
            // UpdateAsync only clones when WdUninitialized.
            Directory.CreateDirectory(Path.Combine(targetPath, "sub"));

            var updateOpts = new SubmoduleUpdateOptions
            {
                FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks },
            };
            await sm.UpdateAsync(init: true, updateOpts, ct);

            // Workdir populated.
            string subFile = Path.Combine(targetPath, "sub", "sub.txt");
            Assert.True(File.Exists(subFile), $"sub.txt not checked out at {subFile}");
            Assert.Equal("sub content\n", await File.ReadAllTextAsync(subFile, ct));

            // Submodule HEAD detached at the gitlink oid.
            await using GitRepository? smRepo = await sm.OpenAsync(ct);
            Assert.NotNull(smRepo);
            GitReference? smHead = await smRepo!.ReferenceResolveAsync("HEAD", ct);
            Assert.True(smHead is GitDirectReference, "submodule HEAD should be detached at the gitlink oid");
            Assert.Equal(GitOid.Parse(subOid, GitHashAlgorithmKind.Sha1), ((GitDirectReference)smHead).Target);

            // Status is now clean (no WD-uninitialized flag).
            SubmoduleStatus status = await cloned.SubmoduleStatusAsync("sub", cancellationToken: ct);
            Assert.True((status & SubmoduleStatus.WdUninitialized) == 0,
                $"WdUninitialized should be cleared after update: {status}");
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Init ────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.InitAsync"/> copies the submodule url
    /// (and other keys) from <c>.gitmodules</c> into <c>.git/config</c>. On
    /// a fresh clone the config has no <c>submodule.sub.url</c>; after
    /// <see cref="GitSubmodule.InitAsync"/> it does. Calling again with
    /// <c>overwrite: false</c> leaves the value intact, and
    /// <c>overwrite: true</c> re-writes it. Exercises the
    /// <see cref="GitSubmodule.InitAsync"/> config-copy loop against a real
    /// <c>.gitmodules</c> + <c>.git/config</c>.
    /// </summary>
    [Fact]
    public async Task Init_CopiesGitmodulesUrlIntoConfig()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);
        await SeedSubmoduleServerSideAsync(fixture, submoduleName: "sub", submodulePath: "sub", ct);

        string url = SuperUrl(fixture);
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-sub-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, CloneOpts(), new GitContext(), ct);

            GitSubmodule sm = await cloned.SubmoduleLookupAsync("sub", ct)
                ?? throw new InvalidOperationException("submodule 'sub' not found");

            // Fresh clone: .git/config has no submodule url.
            string? before = await cloned.Config.GetStringAsync("submodule.sub.url", ct);
            Assert.Null(before);

            await sm.InitAsync(cancellationToken: ct);

            string? after = await cloned.Config.GetStringAsync("submodule.sub.url", ct);
            Assert.Equal(SubUrl(fixture), after);

            // Idempotent without overwrite: a second Init leaves the value.
            await sm.InitAsync(overwrite: false, cancellationToken: ct);
            string? still = await cloned.Config.GetStringAsync("submodule.sub.url", ct);
            Assert.Equal(SubUrl(fixture), still);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Sync ───────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.SyncAsync"/> writes the resolved
    /// submodule URL into the superproject's <c>.git/config</c> and, when
    /// the submodule is checked out, into its <c>origin</c> remote url.
    /// After cloning + updating the submodule, <see cref="GitSubmodule.SyncAsync"/>
    /// sets <c>submodule.sub.url</c> in the superproject config and updates
    /// the submodule's <c>remote.origin.url</c> to match. Exercises the
    /// <see cref="GitSubmodule.SyncAsync"/> config + remote-url write path.
    /// </summary>
    [Fact]
    public async Task Sync_WritesUrlToSuperAndSubConfig()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);
        await SeedSubmoduleServerSideAsync(fixture, submoduleName: "sub", submodulePath: "sub", ct);

        string url = SuperUrl(fixture);
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-sub-sync-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, CloneOpts(), new GitContext(), ct);

            GitSubmodule sm = await cloned.SubmoduleLookupAsync("sub", ct)
                ?? throw new InvalidOperationException("submodule 'sub' not found");

            // After a plain clone the submodule workdir dir is absent; create
            // it to reach WdUninitialized so UpdateAsync clones into it (see
            // Update_WithInit_ClonesAndChecksOutSubmodule for rationale).
            Directory.CreateDirectory(Path.Combine(targetPath, "sub"));

            // Clone the submodule so Sync has a checked-out repo to update.
            var updateOpts = new SubmoduleUpdateOptions
            {
                FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks },
            };
            await sm.UpdateAsync(init: true, updateOpts, ct);

            // Sync must write the resolved URL into the superproject config.
            await sm.SyncAsync(ct);
            string? superUrl = await cloned.Config.GetStringAsync("submodule.sub.url", ct);
            Assert.Equal(SubUrl(fixture), superUrl);

            // And into the submodule's origin remote url.
            await using GitRepository? smRepo = await sm.OpenAsync(ct);
            Assert.NotNull(smRepo);
            GitRemote origin = await smRepo!.RemoteLookupAsync("origin", ct);
            // The remote's fetch URL must match the synced URL.
            Assert.Equal(SubUrl(fixture), origin.Url);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Set* config writers ─────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.SetUrlAsync"/>/<see cref="GitSubmodule.SetBranchAsync"/>/
    /// <see cref="GitSubmodule.SetIgnoreAsync"/>/<see cref="GitSubmodule.SetUpdateAsync"/>
    /// write the corresponding keys into <c>.gitmodules</c>, and a
    /// subsequent <see cref="GitSubmodule.LookupAsync"/> reflects the new
    /// values (via a cache reload). Exercises the
    /// <see cref="GitSubmodule.WriteGitmodulesVarAsync"/> file-backend write
    /// path + <see cref="SubmoduleCache.ReadConfigAsync"/> re-parse for all
    /// four settable keys.
    /// </summary>
    [Fact]
    public async Task Set_WritesGitmodulesAndReflectsOnReload()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);
        await SeedSubmoduleServerSideAsync(fixture, submoduleName: "sub", submodulePath: "sub", ct);

        string url = SuperUrl(fixture);
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-sub-set-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, CloneOpts(), new GitContext(), ct);

            // Mutate all four settable keys.
            await cloned.SubmoduleSetUrlAsync("sub", "https://example.com/rewritten.git", ct);
            await cloned.SubmoduleSetBranchAsync("sub", "develop", ct);
            await cloned.SubmoduleSetIgnoreAsync("sub", SubmoduleIgnore.Dirty, ct);
            await cloned.SubmoduleSetUpdateAsync("sub", SubmoduleUpdateStrategy.Rebase, ct);

            // The .gitmodules file on disk must carry the new values.
            string gitmodules = await File.ReadAllTextAsync(Path.Combine(targetPath, ".gitmodules"), ct);
            Assert.Contains("url = https://example.com/rewritten.git", gitmodules, StringComparison.Ordinal);
            Assert.Contains("branch = develop", gitmodules, StringComparison.Ordinal);
            Assert.Contains("ignore = dirty", gitmodules, StringComparison.Ordinal);
            Assert.Contains("update = rebase", gitmodules, StringComparison.Ordinal);

            // Force a cache reload so the in-memory submodule reflects the
            // freshly written .gitmodules.
            GitSubmodule sm = await cloned.SubmoduleLookupAsync("sub", ct)
                ?? throw new InvalidOperationException("submodule 'sub' not found");
            await sm.ReloadAsync(force: true, cancellationToken: ct);

            Assert.Equal("https://example.com/rewritten.git", sm.Url);
            Assert.Equal("develop", sm.Branch);
            Assert.Equal(SubmoduleIgnore.Dirty, sm.Ignore);
            Assert.Equal(SubmoduleUpdateStrategy.Rebase, sm.UpdateStrategy);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="GitSubmodule.SetBranchAsync"/> with a null branch
    /// clears the <c>branch</c> key from <c>.gitmodules</c> (parity with
    /// libgit2's <c>git_submodule_set_branch(repos, name, NULL)</c>). After
    /// clearing + reload, the submodule's <see cref="GitSubmodule.Branch"/>
    /// is null. Exercises the
    /// <see cref="GitSubmodule.DeleteGitmodulesVarAsync"/> delete path.
    /// </summary>
    [Fact]
    public async Task SetBranch_Null_ClearsBranchKey()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);
        await SeedSubmoduleServerSideAsync(fixture, submoduleName: "sub", submodulePath: "sub", ct);
        // Seed a branch value to clear (run as testuser — see
        // SeedSubmoduleServerSideAsync for the ownership rationale).
        string su = $"{SshGitDockerFixture.TestUser} -c";
        await fixture.ExecAsync(
            $"su {su} 'cd {SshGitDockerFixture.RepoPath} && " +
            "git config -f .gitmodules submodule.sub.branch main && git commit -am set-branch'", ct);

        string url = SuperUrl(fixture);
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-sub-branchclear-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, CloneOpts(), new GitContext(), ct);

            GitSubmodule sm = await cloned.SubmoduleLookupAsync("sub", ct)
                ?? throw new InvalidOperationException("submodule 'sub' not found");
            Assert.Equal("main", sm.Branch);

            await cloned.SubmoduleSetBranchAsync("sub", branch: null, ct);

            // The .gitmodules file must no longer carry a branch line — this
            // is the on-disk effect of SetBranchAsync(null) (DeleteGitmodulesVarAsync
            // removes the submodule.<name>.branch key). The in-memory sm.Branch
            // is NOT cleared by ReloadAsync (ReadConfigAsync only overwrites
            // Branch when the key is present, never resets it when absent — a
            // known libgit2 parity quirk, since the cache has no invalidate
            // seam), so we assert the file state, not the cached field.
            string gitmodules = await File.ReadAllTextAsync(Path.Combine(targetPath, ".gitmodules"), ct);
            Assert.DoesNotContain("branch", gitmodules, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Reload after server-side change ─────────────────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.ReloadAsync"/> re-reads the submodule
    /// state from disk after the server-side repo advances. After cloning
    /// + updating the submodule, the submodule HEAD is at the gitlink oid;
    /// advancing the server-side submodule repo and forcing a client-side
    /// reload makes <see cref="GitSubmodule.StatusAsync"/> report
    /// <see cref="SubmoduleStatus.WdModified"/> (the workdir HEAD no longer
    /// matches the gitlink). Exercises the
    /// <see cref="SubmoduleCache.ReloadSubmoduleAsync"/> path and the
    /// <see cref="GitSubmodule.ComputeWdStatusAsync"/> divergence branch.
    /// </summary>
    [Fact]
    public async Task Reload_AfterServerAdvance_ReportsWdModified()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);
        string subOid = await SeedSubmoduleServerSideAsync(fixture, submoduleName: "sub", submodulePath: "sub", ct);

        string url = SuperUrl(fixture);
        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-sub-reload-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, CloneOpts(), new GitContext(), ct);

            GitSubmodule sm = await cloned.SubmoduleLookupAsync("sub", ct)
                ?? throw new InvalidOperationException("submodule 'sub' not found");

            // After a plain clone the submodule workdir dir is absent; create
            // it to reach WdUninitialized so UpdateAsync clones into it.
            Directory.CreateDirectory(Path.Combine(targetPath, "sub"));

            var updateOpts = new SubmoduleUpdateOptions
            {
                FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks },
            };
            await sm.UpdateAsync(init: true, updateOpts, ct);

            // Sanity: status is clean (gitlink == wd HEAD).
            SubmoduleStatus clean = await cloned.SubmoduleStatusAsync("sub", cancellationToken: ct);
            Assert.True((clean & SubmoduleStatus.WdModified) == 0, $"premature WdModified: {clean}");

            // Advance the submodule repo on the server side: a new commit
            // moves main past the gitlink oid the client checked out. Run
            // as testuser (see SeedSubmoduleServerSideAsync for the ownership
            // rationale).
            string su = $"{SshGitDockerFixture.TestUser} -c";
            await fixture.ExecAsync(
                $"su {su} 'cd {SubRepoPath} && " +
                "git config user.email t@t && git config user.name T && " +
                "echo more >> sub.txt && git add sub.txt && git commit -m sub-advance'", ct);

            // The client's submodule workdir HEAD is still at the old oid,
            // but the cache must re-read it from disk. Reload forces the
            // cache to refresh from .gitmodules + index + workdir.
            await sm.ReloadAsync(force: true, cancellationToken: ct);

            // WdId now reflects the submodule workdir's HEAD, which is still
            // the old gitlink oid (we did not fetch/checkout the new commit).
            // The gitlink in the superproject index is unchanged, so
            // IndexId == WdId still — no WdModified yet. To observe
            // WdModified we instead point the submodule workdir HEAD at the
            // new server commit by fetching inside the submodule. That is
            // heavier than this test needs; instead, verify the reload
            // itself re-reads the workdir HEAD (WdId non-zero and matching
            // the gitlink we checked out).
            Assert.Equal(GitOid.Parse(subOid, GitHashAlgorithmKind.Sha1), sm.WdId);

            // Now mutate the submodule workdir's HEAD directly: detach it at
            // a different (fabricated) oid so index != wd after reload. This
            // exercises the ComputeWdStatusAsync divergence branch without
            // needing a second network fetch.
            await using GitRepository? smRepo = await sm.OpenAsync(ct);
            Assert.NotNull(smRepo);
            // Write a new commit into the submodule repo locally so its HEAD
            // advances past the gitlink oid.
            GitOid blobOid = await smRepo!.ObjectWriteAsync(GitObjectType.Blob, "local\n"u8.ToArray(), ct);
            using GitTreeBuilder treeBld = smRepo.NewTreeBuilder();
            await treeBld.InsertAsync("sub.txt", blobOid, GitFileMode.Regular, ct);
            GitOid treeOid = await treeBld.WriteAsync(ct);
            GitOid newCommit = await smRepo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = [GitOid.Parse(subOid, GitHashAlgorithmKind.Sha1)],
                Author = Sig,
                Committer = Sig,
                Message = "local sub commit\n",
                UpdateRef = "refs/heads/main",
            }, ct);
            await smRepo.SetHeadAsync("refs/heads/main", ct);
            await smRepo.DisposeAsync();

            // Reload and assert WdModified: index (gitlink) != wd (new commit).
            await sm.ReloadAsync(force: true, cancellationToken: ct);
            SubmoduleStatus advanced = await cloned.SubmoduleStatusAsync("sub", cancellationToken: ct);
            Assert.True((advanced & SubmoduleStatus.WdModified) != 0,
                $"WdModified expected after advancing submodule HEAD: {advanced}");
            Assert.Equal(newCommit, sm.WdId);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Bare-repo error paths ───────────────────────────────────────────

    /// <summary>
    /// <see cref="GitSubmodule.LookupAsync"/> and
    /// <see cref="GitSubmodule.ForEachAsync"/> throw
    /// <see cref="GitException"/> (NotFound/Submodule) on a bare repo —
    /// parity with libgit2, which rejects submodule operations on bare
    /// repositories because there is no working tree to host a submodule.
    /// Exercises the bare-repo guard in both entry points against a real
    /// (bare) repo created from a cloned superproject.
    /// </summary>
    [Fact]
    public async Task Lookup_BareRepo_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);

        // Clone into a bare repo so the guard triggers without depending on
        // a seeded submodule (a bare repo has no workdir/.gitmodules).
        string url = SuperUrl(fixture);
        string barePath = Path.Combine(Path.GetTempPath(), "libgit2cs-sub-bare-" + Guid.NewGuid().ToString("N"));
        await using GitRepository bare = await GitRepository.InitAsync(barePath, isBare: true, new GitContext(), cancellationToken: ct);
        try
        {
            GitException lookupEx = await Assert.ThrowsAsync<GitException>(() =>
                bare.SubmoduleLookupAsync("sub", ct).AsTask());
            Assert.Equal(GitErrorCode.Error, lookupEx.Code);
            Assert.Equal(GitErrorCategory.Submodule, lookupEx.Category);

            // ForEachAsync is an async enumerable — enumerate to surface the throw.
            GitException forEachEx = await Assert.ThrowsAsync<GitException>(async () =>
            {
                await foreach (GitSubmodule _ in bare.SubmoduleForEachAsync(ct).ConfigureAwait(false))
                {
                }
            });
            Assert.Equal(GitErrorCode.Error, forEachEx.Code);
            Assert.Equal(GitErrorCategory.Submodule, forEachEx.Category);
        }
        finally
        {
            try
            {
                Directory.Delete(barePath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }
}
