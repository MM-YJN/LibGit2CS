using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;
using SubmoduleApi = LibGit2CS.Submodule.GitSubmodule;

namespace LibGit2CS.UnitTests.Submodule;

/// <summary>
/// Regression tests for the submodule parity behaviors
/// in
/// libgit2 1.9.4.
/// Expectations C-verified against libgit2 1.9.4.
/// </summary>
public sealed class SubmoduleMedParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public SubmoduleMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SubmoduleMedParity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
        await WriteCommit();
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
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

    private async Task<GitOid> WriteCommit()
    {
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("README.md", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);

        // C (commit.c:113-117): a commit created with update_ref must have
        // the current tip as its first parent — chain onto it.
        List<GitOid> parentChain = [];
        if (await _repo.ReferenceResolveAsync("refs/heads/master", TestContext.Current.CancellationToken) is GitDirectReference tipRef)
        {
            parentChain.Add(tipRef.Target);
        }

        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parentChain,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "init\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    private void WriteGitmodules(string content)
    {
        File.WriteAllText(Path.Combine(_repo.Workdir!, ".gitmodules"), content);
    }

    /// <summary>
    /// Creates a real submodule repo at the parent workdir &lt;path&gt; (with
    /// its own .git), writes .gitmodules, and commits the gitlink into the
    /// parent index + HEAD.
    /// </summary>
    private async Task<GitOid> SetupSubmoduleAsync(string name, string path)
    {
        string subDir = Path.Combine(_repo.Workdir!, path);
        Directory.CreateDirectory(subDir);
        await using GitRepository subRepo = await GitRepository.InitAsync(subDir, isBare: false, new GitContext());
        GitIndex subIdx = await subRepo.GetIndexAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(subDir, "sub.txt"), "sub content\n", cancellationToken: TestContext.Current.CancellationToken);
        await subIdx.AddByPathAsync("sub.txt", TestContext.Current.CancellationToken);
        await subIdx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid subTree = await subIdx.WriteTreeAsync(TestContext.Current.CancellationToken);
        GitOid subOid = await subRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = subTree,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "sub init\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        await StageGitlinkAsync(name, path, subOid);
        return subOid;
    }

    /// <summary>
    /// Writes .gitmodules and stages the gitlink at &lt;path&gt; in the parent
    /// index (no HEAD commit).
    /// </summary>
    private async Task StageGitlinkAsync(string name, string path, GitOid oid)
    {
        WriteGitmodules($"[submodule \"{name}\"]\n    path = {path}\n    url = https://example.com/{name}.git\n");
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry(path, oid, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);
    }

    private async Task<GitOid> CommitInSubmoduleAsync(string path, string fileName, string content)
    {
        string subDir = Path.Combine(_repo.Workdir!, path);
        await using GitRepository subRepo = await GitRepository.OpenAsync(subDir, new GitContext(), TestContext.Current.CancellationToken);
        GitIndex subIdx = await subRepo.GetIndexAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(subDir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        await subIdx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await subIdx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid subTree = await subIdx.WriteTreeAsync(TestContext.Current.CancellationToken);

        List<GitOid> parentChain = [];
        if (await subRepo.ReferenceResolveAsync("refs/heads/master", TestContext.Current.CancellationToken) is GitDirectReference subTip)
        {
            parentChain.Add(subTip.Target);
        }

        return await subRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = subTree,
            Parents = parentChain,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "sub commit\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    // ── WD_UNINITIALIZED / WD_DELETED / IN_WD ───────────────────────

    [Fact]
    public async Task Status_BareDirectory_NoGit_ReportsWdUninitializedWithoutInWd()
    {
        // (submodule.c:2156-2171, 2377-2382): a directory WITHOUT .git is
        // WD_SCANNED only (no IN_WD) — the status is WD_UNINITIALIZED without
        // the InWd location bit.
        _ = await SetupSubmoduleAsync("sub", "sub");

        // Remove the .git from the submodule workdir — keep the bare dir.
        Directory.Delete(Path.Combine(_repo.Workdir!, "sub", ".git"), recursive: true);

        SubmoduleStatus status = await _repo.SubmoduleStatusAsync("sub", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((SubmoduleStatus)0, status & SubmoduleStatus.InWd);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.WdUninitialized);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.InIndex);
    }

    [Fact]
    public async Task Status_UnbornSubmoduleHead_ReportsWdDeleted()
    {
        // a checked-out submodule whose HEAD cannot be resolved (unborn)
        // has IN_WD set → falls to WD_DELETED (submodule.c:2377-2382).
        string subDir = Path.Combine(_repo.Workdir!, "sub");
        Directory.CreateDirectory(subDir);
        await using GitRepository unborn = await GitRepository.InitAsync(subDir, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // A real commit oid for the gitlink (from a scratch repo).
        GitOid gitlinkOid = await CreateCommitOidAsync();
        await StageGitlinkAsync("sub", "sub", gitlinkOid);

        SubmoduleStatus status = await _repo.SubmoduleStatusAsync("sub", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.InWd);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.WdDeleted);
        Assert.Equal((SubmoduleStatus)0, status & SubmoduleStatus.WdUninitialized);
    }

    private async Task<GitOid> CreateCommitOidAsync()
    {
        string srcDir = Path.Combine(_tempDir, "scratch-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(srcDir);
        await using GitRepository src = await GitRepository.InitAsync(srcDir, isBare: false, new GitContext());
        GitIndex idx = await src.GetIndexAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(srcDir, "f.txt"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid tree = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        return await src.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "c\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    // ── dotted .gitmodules names + lookup-by-path ───────────────

    [Fact]
    public async Task Lookup_DottedName_And_LookupByPath()
    {
        // (submodule.c:2110-2118): `submodule.my.name.path` names the
        // submodule "my.name"; the path→name search (submodule.c:357-396)
        // resolves a lookup by PATH to the configured name.
        WriteGitmodules("[submodule \"my.name\"]\n    path = mypath\n    url = https://example.com/my.name.git\n");

        SubmoduleApi sm = await SubmoduleApi.LookupAsync(_repo, "my.name", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("my.name", sm.Name);
        Assert.Equal("mypath", sm.Path?.ToUtf8String());
        Assert.Equal("https://example.com/my.name.git", sm.Url);

        // lookup by the PATH resolves the "my.name" entry.
        SubmoduleApi byPath = await SubmoduleApi.LookupAsync(_repo, "mypath", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("my.name", byPath.Name);
    }

    // ── config value quirks ─────────────────────────────────────────

    [Fact]
    public async Task Config_BooleanAliases_ParsedLikeC()
    {
        // (submodule.c:33-55, 1948-1988): update=true→CHECKOUT,
        // ignore=false→NONE, fetchRecurseSubmodules=1→YES (C-verified).
        WriteGitmodules(
            "[submodule \"bsub\"]\n    path = bsub\n    url = https://example.com/b.git\n" +
            "    update = true\n    ignore = false\n    fetchRecurseSubmodules = 1\n");

        SubmoduleApi sm = await SubmoduleApi.LookupAsync(_repo, "bsub", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SubmoduleUpdateStrategy.Checkout, sm.UpdateStrategy);
        Assert.Equal(SubmoduleIgnore.None, sm.Ignore);
        Assert.Equal(SubmoduleRecurse.Yes, sm.FetchRecurse);
    }

    [Fact]
    public async Task Config_InvalidUpdateValue_LookupFails()
    {
        // (submodule.c:2061-2068): an invalid `update` value makes
        // submodule_read_config FAIL — the lookup errors out (C-verified:
        // "invalid value for submodule 'update' property: '!custom'").
        WriteGitmodules("[submodule \"isub\"]\n    path = isub\n    url = https://example.com/i.git\n    update = !custom\n");

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await SubmoduleApi.LookupAsync(_repo, "isub", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("invalid value for submodule 'update' property: '!custom'", ex.Message);
    }

    [Fact]
    public async Task Config_DashPrefixedUrl_Ignored()
    {
        // (submodule.c:2003-2049, looks_like_command_line_option): a
        // `-`-prefixed url value is ignored (C-verified: url == NULL).
        WriteGitmodules("[submodule \"dsub\"]\n    path = dsub\n    url = -f\n");

        SubmoduleApi sm = await SubmoduleApi.LookupAsync(_repo, "dsub", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(sm.Url);
    }

    // ── lookup errors ───────────────────────────────────────────────

    [Fact]
    public async Task Lookup_WorkdirOnlySubmodule_ThrowsExists()
    {
        // (submodule.c:403-425): a repo at <workdir>/<name>/.git that was
        // never added reports GIT_EEXISTS "submodule '<name>' has not been
        // added yet" (C-verified).
        string subDir = Path.Combine(_repo.Workdir!, "subdir");
        Directory.CreateDirectory(subDir);
        await using GitRepository sub = await GitRepository.InitAsync(subDir, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await SubmoduleApi.LookupAsync(_repo, "subdir", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Exists, ex.Code);
        Assert.Contains("submodule 'subdir' has not been added yet", ex.Message);
    }

    [Fact]
    public async Task Lookup_MissingSubmodule_ThrowsNotFound()
    {
        // (submodule.c:97-105, submodule_set_lookup_error): a missing
        // submodule reports GIT_ENOTFOUND "no submodule named '<name>'".
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await SubmoduleApi.LookupAsync(_repo, "nonexistent", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Contains("no submodule named 'nonexistent'", ex.Message);
    }

    // ── git_submodule_init ─────────────────────────────────────────

    [Fact]
    public async Task Init_WritesResolvedUrl_AndDeletesCheckoutUpdateKey()
    {
        // (submodule.c:1498-1540): init writes submodule.NAME.url (the
        // RESOLVED url) and deletes/omits submodule.NAME.update for
        // update=checkout; branch is NOT copied (C-verified).
        await _repo.Config.SetStringAsync("remote.origin.url", "https://example.com/group/super.git", TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", TestContext.Current.CancellationToken);
        WriteGitmodules(
            "[submodule \"rel\"]\n    path = rel\n    url = ../sub.git\n    update = checkout\n    branch = main\n");

        SubmoduleApi sm = await SubmoduleApi.LookupAsync(_repo, "rel", cancellationToken: TestContext.Current.CancellationToken);
        await sm.InitAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://example.com/group/sub.git", await _repo.Config.GetStringAsync("submodule.rel.url", TestContext.Current.CancellationToken));
        Assert.Null(await _repo.Config.GetStringAsync("submodule.rel.update", TestContext.Current.CancellationToken));
        Assert.Null(await _repo.Config.GetStringAsync("submodule.rel.branch", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Init_UpdateRebase_WritesUpdateKey()
    {
        // update=rebase keeps the update key with its string form.
        WriteGitmodules("[submodule \"rel2\"]\n    path = rel2\n    url = https://example.com/rel2.git\n    update = rebase\n");

        SubmoduleApi sm = await SubmoduleApi.LookupAsync(_repo, "rel2", cancellationToken: TestContext.Current.CancellationToken);
        await sm.InitAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://example.com/rel2.git", await _repo.Config.GetStringAsync("submodule.rel2.url", TestContext.Current.CancellationToken));
        Assert.Equal("rebase", await _repo.Config.GetStringAsync("submodule.rel2.update", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Init_NoUrl_Throws()
    {
        // (submodule.c:1505-1509): "no URL configured for submodule 'x'".
        WriteGitmodules("[submodule \"nourl\"]\n    path = nourl\n");

        SubmoduleApi sm = await SubmoduleApi.LookupAsync(_repo, "nourl", cancellationToken: TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await sm.InitAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("no URL configured for submodule 'nourl'", ex.Message);
    }

    // ── git_submodule_sync ─────────────────────────────────────────

    [Fact]
    public async Task Sync_OnlyIfExists_AndUpdatesTrackingRemote()
    {
        // (submodule.c:1542-1585): submodule.NAME.url is updated ONLY if
        // it already exists; the submodule repo's HEAD-tracking remote url is
        // updated with the resolved url (lookup_head_remote_key), falling
        // back to remote.origin.url.
        string subDir = Path.Combine(_repo.Workdir!, "sy");
        Directory.CreateDirectory(subDir);
        await using (GitRepository sub = await GitRepository.InitAsync(subDir, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken))
        {
            GitIndex sIdx = await sub.GetIndexAsync(TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(subDir, "f.txt"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
            await sIdx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
            await sIdx.WriteAsync(TestContext.Current.CancellationToken);
            GitOid sTree = await sIdx.WriteTreeAsync(TestContext.Current.CancellationToken);
            await sub.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = sTree,
                Author = TestSig(),
                Committer = TestSig(),
                Message = "c\n",
                UpdateRef = "refs/heads/master",
            }, TestContext.Current.CancellationToken);

            // master tracks origin.
            await sub.Config.SetStringAsync("branch.master.remote", "origin", TestContext.Current.CancellationToken);
            await sub.Config.SetStringAsync("branch.master.merge", "refs/heads/master", TestContext.Current.CancellationToken);
            await sub.Config.SetStringAsync("remote.origin.url", "https://old/origin.git", TestContext.Current.CancellationToken);
            await sub.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", TestContext.Current.CancellationToken);
        }

        WriteGitmodules("[submodule \"sy\"]\n    path = sy\n    url = ../upstream/sy.git\n");

        SubmoduleApi sm = await SubmoduleApi.LookupAsync(_repo, "sy", cancellationToken: TestContext.Current.CancellationToken);

        // 1) No pre-existing submodule.sy.url → sync must NOT create it.
        await sm.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Null(await _repo.Config.GetStringAsync("submodule.sy.url", TestContext.Current.CancellationToken));

        // 2) Pre-existing key → updated to the resolved url.
        await _repo.Config.SetStringAsync("submodule.sy.url", "https://old/config.git", TestContext.Current.CancellationToken);
        await sm.SyncAsync(TestContext.Current.CancellationToken);

        string resolved = await SubmoduleApi.ResolveUrlAsync(_repo, "../upstream/sy.git", TestContext.Current.CancellationToken);
        Assert.Equal(resolved, await _repo.Config.GetStringAsync("submodule.sy.url", TestContext.Current.CancellationToken));

        // 3) The submodule repo's origin remote was updated (HEAD tracks origin).
        await using GitRepository sub2 = await GitRepository.OpenAsync(subDir, new GitContext(), TestContext.Current.CancellationToken);
        Assert.Equal(resolved, await sub2.Config.GetStringAsync("remote.origin.url", TestContext.Current.CancellationToken));
    }

    // ── git_submodule_add_setup ────────────────────────────────────

    [Fact]
    public async Task AddSetup_ExistingSubmodule_ThrowsExists()
    {
        // (submodule.c:837-845): re-adding an existing submodule fails
        // with GIT_EEXISTS "attempt to add submodule '<path>' that already
        // exists".
        WriteGitmodules("[submodule \"test\"]\n    path = test\n    url = https://example.com/test.git\n");

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await SubmoduleApi.AddSetupAsync(_repo, "https://example.com/test.git", "test", useGitlink: false, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Exists, ex.Code);
        Assert.Contains("attempt to add submodule 'test' that already exists", ex.Message);
    }

    [Fact]
    public async Task AddSetup_OccupiedIndexFile_ThrowsExists()
    {
        // (submodule.c:131-170, is_path_occupied): a path that collides
        // with an index file fails with GIT_EEXISTS.
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "occupied.txt"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("occupied.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await SubmoduleApi.AddSetupAsync(_repo, "https://example.com/x.git", "occupied.txt", useGitlink: false, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Exists, ex.Code);
        Assert.Contains("File 'occupied.txt' already exists in the index", ex.Message);
    }

    [Fact]
    public async Task AddSetup_Gitlink_CreatesOriginRemote()
    {
        // (submodule.c:724-770, 896-900): submodule_repo_init passes the
        // RESOLVED url as origin_url — the new submodule repo's .git/config
        // contains [remote "origin"].
        _ = await SubmoduleApi.AddSetupAsync(_repo, "https://example.com/x/sub.git", "newsub", useGitlink: true, cancellationToken: TestContext.Current.CancellationToken);

        // Open the submodule via its workdir .git gitlink file.
        await using GitRepository sub = await GitRepository.OpenAsync(Path.Combine(_repo.Workdir!, "newsub"), new GitContext(), TestContext.Current.CancellationToken);
        Assert.Equal("https://example.com/x/sub.git", await sub.Config.GetStringAsync("remote.origin.url", TestContext.Current.CancellationToken));
    }

    // ── git_submodule_update uses the resolved config url ──────────

    [Fact]
    public async Task Update_RelativeUrl_ClonesFromResolvedConfigUrl()
    {
        // (submodule.c:1405-1449): the clone uses the URL read back from
        // .git/config (the resolved url) — a relative .gitmodules url
        // (../sourcerepo) clones from the correct location after init.
        string sourceDir = Path.Combine(_tempDir, "sourcerepo");
        Directory.CreateDirectory(sourceDir);
        await using GitRepository source = await GitRepository.InitAsync(sourceDir, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        GitIndex srcIdx = await source.GetIndexAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "sub.txt"), "sub content\n", cancellationToken: TestContext.Current.CancellationToken);
        await srcIdx.AddByPathAsync("sub.txt", TestContext.Current.CancellationToken);
        await srcIdx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid srcTree = await srcIdx.WriteTreeAsync(TestContext.Current.CancellationToken);
        GitOid subOid = await source.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = srcTree,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "sub init\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        // Relative url: resolves against the parent workdir (no remote) to
        // <tempDir>/sourcerepo.
        WriteGitmodules("[submodule \"sub\"]\n    path = sub\n    url = ./sourcerepo\n");
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("sub", subOid, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        // Empty workdir dir → WD_UNINITIALIZED.
        Directory.CreateDirectory(Path.Combine(_repo.Workdir!, "sub"));

        SubmoduleApi sm = await SubmoduleApi.LookupAsync(_repo, "sub", cancellationToken: TestContext.Current.CancellationToken);
        await sm.UpdateAsync(init: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_repo.Workdir!, "sub", "sub.txt")));
    }

    [Fact]
    public async Task Update_Uninitialized_NotInitialized_MessageExact()
    {
        // (submodule.c:1418): the error message is exactly "submodule is
        // not initialized" (no name suffix).
        GitOid subOid = await CreateCommitOidAsync();
        await StageGitlinkAsync("sub", "sub", subOid);
        Directory.CreateDirectory(Path.Combine(_repo.Workdir!, "sub"));

        SubmoduleApi sm = await SubmoduleApi.LookupAsync(_repo, "sub", cancellationToken: TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await sm.UpdateAsync(init: false, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("submodule is not initialized", ex.Message);
    }

    // ── reload refreshes index/HEAD oids ───────────────────────────

    [Fact]
    public async Task Reload_RefreshesIndexAndHeadOids()
    {
        // (submodule.c:1753-1756): git_submodule_reload re-runs
        // submodule_update_index / submodule_update_head — the cached
        // index/HEAD oids must reflect the current index and HEAD tree.
        GitOid subOid = await SetupSubmoduleAsync("sub", "sub");

        // Commit the gitlink into HEAD so both oids are populated.
        GitIndex commitIdx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await commitIdx.WriteTreeAsync(TestContext.Current.CancellationToken);
        List<GitOid> parents = [];
        if (await _repo.ReferenceResolveAsync("refs/heads/master", TestContext.Current.CancellationToken) is GitDirectReference tip)
        {
            parents.Add(tip.Target);
        }

        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "add submodule\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        SubmoduleApi sm = await SubmoduleApi.LookupAsync(_repo, "sub", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(subOid, sm.IndexId);
        Assert.Equal(subOid, sm.HeadId);

        GitOid newHead = await CommitInSubmoduleAsync("sub", "sub.txt", "changed\n");
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("sub", newHead, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        // Without the reload leaves IndexId at the stale subOid.
        await sm.ReloadAsync(force: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(newHead, sm.IndexId);
        Assert.Equal(subOid, sm.HeadId); // HEAD tree still holds the old oid
    }
}
