using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

namespace LibGit2CS.UnitTests.Submodule;

// Parity cases verified against libgit2 1.9.4:
//  - GetUrlBaseAsync ignored the linked-worktree parent path that C
//    uses as the URL base fallback (submodule.c:2323-2330).
//  - LookupHeadRemoteKeyAsync parsed refs/remotes/<remote>/<branch>
//    naively instead of C's refspec-based git_branch__remote_name
//    (branch.c:557-618).
//  - worktree path canonicalization used lexical Path.GetFullPath
//    instead of C's realpath-based prettify_dir (fs_path.c:379-403).
public sealed class SubmoduleLowRegressionTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public SubmoduleLowRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SubmoduleLow_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "main"), isBare: false, new GitContext());
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

    private static GitSignature TestSig() => new("T", "t@x.com", new GitTime(100, 0));

    private async Task<GitOid> CommitFileAsync(string fileName, string content, string message)
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    // ---- linked-worktree superproject uses the main workdir as base ----

    [Fact]
    public async Task ResolveUrl_LinkedWorktree_NoRemote_UsesMainWorkdir()
    {
        await CommitFileAsync("f.txt", "f\n", "c1\n");

        // Add a linked worktree (no default remote anywhere).
        string wtPath = Path.Combine(_tempDir, "wt");
        using LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("wt", wtPath, cancellationToken: TestContext.Current.CancellationToken);
        await using GitRepository wtRepo = await GitRepository.OpenAsync(wtPath, new GitContext(), TestContext.Current.CancellationToken);

        // C's get_url_base uses wt->parent_path (the main worktree's dir)
        // as the fallback base (submodule.c:2323-2330), not the
        // linked worktree's own workdir.
        string resolved = await GitSubmodule.ResolveUrlAsync(wtRepo, "./x", TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(_repo.Workdir!, "x"), resolved);
    }

    [Fact]
    public async Task ResolveUrl_LinkedWorktree_WithRemote_UsesRemoteUrl()
    {
        // Control: with a default remote the URL base is the remote URL.
        await CommitFileAsync("f.txt", "f\n", "c1\n");
        await _repo.RemoteCreateAsync("origin", "https://example.com/base.git", TestContext.Current.CancellationToken);

        string wtPath = Path.Combine(_tempDir, "wt2");
        using LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("wt2", wtPath, cancellationToken: TestContext.Current.CancellationToken);
        await using GitRepository wtRepo = await GitRepository.OpenAsync(wtPath, new GitContext(), TestContext.Current.CancellationToken);

        string resolved = await GitSubmodule.ResolveUrlAsync(wtRepo, "../x", TestContext.Current.CancellationToken);
        Assert.Equal("https://example.com/x", resolved);
    }

    // ---- remote name comes from the fetch refspec dst ----

    [Fact]
    public async Task ResolveUrl_CustomFetchDst_UsesMatchingRemote()
    {
        await CommitFileAsync("f.txt", "f\n", "c1\n");
        await _repo.RemoteCreateAsync("origin", "https://example.com/base.git", TestContext.Current.CancellationToken);

        // Custom fetch dst: refs/remotes/custom/* — the upstream ref is
        // refs/remotes/custom/master, but the REMOTE is "origin". C's
        // git_branch__remote_name matches the dst refspec (branch.c:557-618);
        // a prefix parse returns "custom" (a nonexistent remote).
        await _repo.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/custom/*", TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.master.remote", "origin", TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.master.merge", "refs/heads/master", TestContext.Current.CancellationToken);
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        string resolved = await GitSubmodule.ResolveUrlAsync(_repo, "../x", TestContext.Current.CancellationToken);
        Assert.Equal("https://example.com/x", resolved);
    }

    [Fact]
    public async Task ResolveUrl_StandardFetchDst_StillWorks()
    {
        // Control: the standard refs/remotes/origin/* dst resolves the same.
        await CommitFileAsync("f.txt", "f\n", "c1\n");
        await _repo.RemoteCreateAsync("origin", "https://example.com/base.git", TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.master.remote", "origin", TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.master.merge", "refs/heads/master", TestContext.Current.CancellationToken);
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        string resolved = await GitSubmodule.ResolveUrlAsync(_repo, "../x", TestContext.Current.CancellationToken);
        Assert.Equal("https://example.com/x", resolved);
    }

    // ---- worktree paths resolve symlinks (realpath) ----

    [Fact]
    public async Task Worktree_StoredGitdir_ResolvesSymlinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // symlink creation needs privileges
        }

        await CommitFileAsync("f.txt", "f\n", "c1\n");

        // The worktree path goes THROUGH a symlink to the main workdir.
        string real = _repo.Workdir!.TrimEnd('/');
        string link = Path.Combine(_tempDir, "link");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (IOException)
        {
            return;
        }

        using LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync(
            "wt3", Path.Combine(link, "wt3"), cancellationToken: TestContext.Current.CancellationToken);

        // C's prettify_dir resolves symlinks (fs_path.c:379-403); the
        // lexical Prettify would store the symlinked path in the
        // admin gitdir file.
        string adminGitdir = await File.ReadAllTextAsync(
            Path.Combine(_repo.Path, "worktrees", "wt3", "gitdir"), TestContext.Current.CancellationToken);
        Assert.StartsWith(real, adminGitdir);
        Assert.DoesNotContain("/link/", adminGitdir);
    }
}
