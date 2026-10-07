using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

namespace LibGit2CS.UnitTests.Submodule;

// Regression coverage against libgit2 1.9.4.
// parity behaviors for Submodule:
//   UpdateAsync must propagate a failure to
//         open the submodule repo, not do nothing (C propagates
//         git_submodule_open, submodule.c:1458-1459).
//   git_submodule_name_is_valid: attacker-controlled
//         .gitmodules section names are skipped like C
//         (submodule.c:2121-2125, 435-455).
//   The per-repo SubmoduleCache is invalidated by the Set*
//         APIs so lookups see fresh config (C has no persistent cache).
//   Duplicate submodule paths fail the map load
//         with "duplicated submodule path" (submodule.c:227-232).
//   SubmoduleLookupAsync trims a trailing slash
//         (submodule.c:364-367).
//   ComputeStatusAsync clears the error and reports status without
//         WD_INDEX_MODIFIED when the submodule HEAD's commit
//         object is missing (submodule.c:2402-2403).
//   ResolveRelative keeps excess leading ".." segments
//         as a new relative base (fs_path.c:855-866).
//   CloneLocalPathAsync must not rewrite the gitlink repo's config and .git
//         file and destroy the origin remote AddSetupAsync created.
public sealed class SubmoduleMediumRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public SubmoduleMediumRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SubmoduleMed_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature Sig() => new("t", "t@t", new GitTime(1700000000, 0));

    private async Task<GitRepository> CreateSourceRepoAsync()
    {
        string subDir = Path.Combine(_tempDir, "source_" + Guid.NewGuid().ToString("N")[..8]);
        GitRepository subRepo = await GitRepository.InitAsync(subDir, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(subRepo.Workdir!, "sub.txt"), "sub content\n", cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await subRepo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("sub.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        await subRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig(),
            Committer = Sig(),
            Message = "sub init\n",
            UpdateRef = "HEAD",
        }, TestContext.Current.CancellationToken);
        return subRepo;
    }

    private async Task<GitRepository> CreateParentRepoAsync()
    {
        string repoPath = Path.Combine(_tempDir, "parent_" + Guid.NewGuid().ToString("N")[..8]);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig(),
            Committer = Sig(),
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);
        return repo;
    }

    private async Task<(GitRepository Repo, GitSubmodule Sm)> AddSetupSubmoduleAsync()
    {
        await using GitRepository source = await CreateSourceRepoAsync();
        GitRepository repo = await CreateParentRepoAsync();
        GitSubmodule sm = await repo.SubmoduleAddSetupAsync(source.Path.Replace('\\', '/'), "sub", useGitlink: true, TestContext.Current.CancellationToken);
        return (repo, sm);
    }

    // ── Update Broken Gitdir Fails Like C ────────────────────────────

    [Fact]
    public async Task Update_BrokenGitdir_FailsLikeC()
    {
        (GitRepository repo, GitSubmodule sm) = await AddSetupSubmoduleAsync();
        await using (repo)
        {
            // Commit into the submodule so the gitlink has an index ID.
            GitRepository? smRepo = await sm.OpenAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(smRepo);
            await using (smRepo)
            {
                await File.WriteAllTextAsync(Path.Combine(smRepo!.Workdir!, "c.txt"), "c\n", cancellationToken: TestContext.Current.CancellationToken);
                GitIndex idx = await smRepo.GetIndexAsync(TestContext.Current.CancellationToken);
                await idx.AddByPathAsync("c.txt", TestContext.Current.CancellationToken);
                await idx.WriteAsync(TestContext.Current.CancellationToken);
                GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
                await smRepo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = Sig(),
                    Committer = Sig(),
                    Message = "sub commit\n",
                    UpdateRef = "HEAD",
                }, TestContext.Current.CancellationToken);
            }

            await sm.AddToIndexAsync(cancellationToken: TestContext.Current.CancellationToken);

            // Break the gitlink: point the .git file at a missing modules dir.
            string smPath = Path.Combine(repo.Workdir!, "sub");
            await File.WriteAllTextAsync(
                Path.Combine(smPath, ".git"),
                "gitdir: " + Path.Combine(repo.Path, "modules", "missing-dir") + "\n",
                cancellationToken: TestContext.Current.CancellationToken);

            // C propagates git_submodule_open's failure (submodule.c:1458-
            // 1459), so UpdateAsync must not report success.
            await Assert.ThrowsAsync<GitException>(async () =>
                await sm.UpdateAsync(init: false, cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    // ── Traversal Name From Gitmodules Is Skipped ────────────────────

    [Fact]
    public async Task TraversalName_FromGitmodules_IsSkipped()
    {
        await using GitRepository repo = await CreateParentRepoAsync();
        await File.WriteAllTextAsync(
            Path.Combine(repo.Workdir!, ".gitmodules"),
            "[submodule \"..\"]\n\tpath = evil\n\turl = https://example.com/x.git\n",
            cancellationToken: TestContext.Current.CancellationToken);

        // C skips names failing git_submodule_name_is_valid
        // (submodule.c:2121-2125) — the entry never materializes.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.SubmoduleLookupAsync("..", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── Set Url Is Visible To Immediate Lookup ───────────────────────

    [Fact]
    public async Task SetUrl_IsVisibleToImmediateLookup()
    {
        await using GitRepository repo = await CreateParentRepoAsync();
        await File.WriteAllTextAsync(
            Path.Combine(repo.Workdir!, ".gitmodules"),
            "[submodule \"x\"]\n\tpath = x\n\turl = https://old.example.com/x.git\n",
            cancellationToken: TestContext.Current.CancellationToken);

        // Prime the per-repo cache with a lookup (C has no persistent
        // cache — every lookup re-reads .gitmodules).
        GitSubmodule before = await repo.SubmoduleLookupAsync("x", TestContext.Current.CancellationToken);
        Assert.Equal("https://old.example.com/x.git", before.Url);

        await repo.SubmoduleSetUrlAsync("x", "https://new.example.com/x.git", TestContext.Current.CancellationToken);

        // The Set* must invalidate the cache so the lookup does not return the
        // stale URL until an explicit ReloadAsync.
        GitSubmodule sm = await repo.SubmoduleLookupAsync("x", TestContext.Current.CancellationToken);
        Assert.Equal("https://new.example.com/x.git", sm.Url);
    }

    // ── Duplicated Path Fails The Load ───────────────────────────────

    [Fact]
    public async Task DuplicatedPath_FailsTheLoad()
    {
        await using GitRepository repo = await CreateParentRepoAsync();
        await File.WriteAllTextAsync(
            Path.Combine(repo.Workdir!, ".gitmodules"),
            "[submodule \"a\"]\n\tpath = x\n\turl = https://a.example.com/x.git\n" +
            "[submodule \"b\"]\n\tpath = x\n\turl = https://b.example.com/x.git\n",
            cancellationToken: TestContext.Current.CancellationToken);

        // C's load_submodule_names fails the whole map with "duplicated
        // submodule path 'x'" (submodule.c:227-232).
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.SubmoduleLookupAsync("a", TestContext.Current.CancellationToken));
        Assert.Contains("duplicated submodule path", ex.Message);
    }

    // ── Lookup Trailing Slash Is Trimmed ─────────────────────────────

    [Fact]
    public async Task Lookup_TrailingSlash_IsTrimmed()
    {
        await using GitRepository repo = await CreateParentRepoAsync();
        await File.WriteAllTextAsync(
            Path.Combine(repo.Workdir!, ".gitmodules"),
            "[submodule \"sub\"]\n\tpath = dir/sub\n\turl = https://example.com/sub.git\n",
            cancellationToken: TestContext.Current.CancellationToken);

        // C trims trailing '/' (submodule.c:364-367) — "trailing slash is
        // allowed" per the header.
        GitSubmodule sm = await repo.SubmoduleLookupAsync("dir/sub/", TestContext.Current.CancellationToken);
        Assert.Equal("sub", sm.Name);
    }

    // ── Status Missing Head Commit Reports Without Index Modified ────

    [Fact]
    public async Task Status_MissingHeadCommit_ReportsWithoutIndexModified()
    {
        (GitRepository repo, GitSubmodule sm) = await AddSetupSubmoduleAsync();
        await using (repo)
        {
            GitRepository? smRepo = await sm.OpenAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(smRepo);
            GitOid smHeadOid;
            await using (smRepo)
            {
                await File.WriteAllTextAsync(Path.Combine(smRepo!.Workdir!, "c.txt"), "c\n", cancellationToken: TestContext.Current.CancellationToken);
                GitIndex idx = await smRepo.GetIndexAsync(TestContext.Current.CancellationToken);
                await idx.AddByPathAsync("c.txt", TestContext.Current.CancellationToken);
                await idx.WriteAsync(TestContext.Current.CancellationToken);
                GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
                smHeadOid = await smRepo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = Sig(),
                    Committer = Sig(),
                    Message = "sub commit\n",
                    UpdateRef = "HEAD",
                }, TestContext.Current.CancellationToken);
            }

            await sm.AddToIndexAsync(cancellationToken: TestContext.Current.CancellationToken);

            // Prune the submodule's HEAD commit object.
            string modulesDir = Path.Combine(repo.Path, "modules", "sub");
            string objPath = Path.Combine(modulesDir, "objects", smHeadOid.ToPathString());
            Assert.True(File.Exists(objPath), "precondition: HEAD commit is loose");
            File.Delete(objPath);

            // C clears git_repository_head_tree's failure (submodule.c:2402-
            // 2403) — status reports without WD_INDEX_MODIFIED instead of
            // throwing.
            SubmoduleStatus status = await repo.SubmoduleStatusAsync("sub", SubmoduleIgnore.None, TestContext.Current.CancellationToken);
            Assert.Equal(0, (int)(status & SubmoduleStatus.WdIndexModified));
        }
    }

    // ── Resolve Url Excess Dot Dots Accumulate Like C ────────────────

    [Fact]
    public async Task ResolveUrl_ExcessDotDots_AccumulateLikeC()
    {
        // C's git_fs_path_resolve_relative keeps a leading ".." and advances
        // the base (fs_path.c:855-866), so further ".." accumulate:
        // base git@github.com:org/repo + ../../../x → ../../x.
        await using GitRepository repo = await CreateParentRepoAsync();
        await LibGit2CS.Remote.GitRemote.CreateAsync(repo, "origin", "git@github.com:org/repo", TestContext.Current.CancellationToken);

        string resolved = await GitSubmodule.ResolveUrlAsync(repo, "../../../x", TestContext.Current.CancellationToken);
        Assert.Equal("../x", resolved);

        string resolved2 = await GitSubmodule.ResolveUrlAsync(repo, "../../../../", TestContext.Current.CancellationToken);
        Assert.Equal("../..", resolved2);
    }

    // ── Clone Local Path Preserves Gitlink Config And Gitfile ────────

    [Fact]
    public async Task CloneLocalPath_PreservesGitlinkConfigAndGitfile()
    {
        await using GitRepository source = await CreateSourceRepoAsync();
        GitRepository repo = await CreateParentRepoAsync();
        await using (repo)
        {
            GitSubmodule sm = await repo.SubmoduleAddSetupAsync(source.Path.Replace('\\', '/'), "sub", useGitlink: true, TestContext.Current.CancellationToken);

            await sm.CloneAsync(cancellationToken: TestContext.Current.CancellationToken);

            // C clones INTO the repo created by submodule_repo_init, which
            // recorded origin (submodule.c:740/900) — the config and the
            // gitlink .git file must survive the local object-copy.
            string modulesConfig = Path.Combine(repo.Path, "modules", "sub", "config");
            string configText = await File.ReadAllTextAsync(modulesConfig, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("origin", configText);

            string gitlink = await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "sub", ".git"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.StartsWith("gitdir: ", gitlink, StringComparison.Ordinal);
        }
    }
}
