using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Worktree;

/// <summary>
/// Parity regression tests for behaviors in
/// libgit2 1.9.4.
/// C reference: worktree.c:431-511.
/// </summary>
public sealed class WorktreeLockParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public WorktreeLockParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_WorktreeLockParity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "f.txt"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = new GitSignature("t", "t@example.com", new GitTime(1700000000, 0)),
            Committer = new GitSignature("t", "t@example.com", new GitTime(1700000000, 0)),
            Message = "c1\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
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

    private async ValueTask<LibGit2CS.Repository.Worktree> AddWorktreeAsync(string name)
    {
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync(name, Path.Combine(_tempDir, name), cancellationToken: TestContext.Current.CancellationToken);
        return wt;
    }

    // ── lock/unlock consult the `locked` FILE, not the cached flag ──

    [Fact]
    public async Task Lock_WhenLockFileExistsOutOfBand_ThrowsLocked()
    {
        // C (worktree.c:431-443): git_worktree_lock calls
        // git_worktree_is_locked — it reads the `locked` FILE on every call.
        // A lock created out-of-band (e.g. `git worktree lock`) must be
        // respected even though this Worktree instance never locked it.
        using LibGit2CS.Repository.Worktree wt = await AddWorktreeAsync("wt1");
        Assert.False(wt.IsLocked);

        // git CLI writes the lock file directly.
        string lockedPath = Path.Combine(wt.GitdirPath, "locked");
        await File.WriteAllTextAsync(lockedPath, "external lock\n", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await wt.LockAsync("mine", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Locked, ex.Code);
    }

    [Fact]
    public async Task Unlock_LockFileRemovedOutOfBand_ReturnsFalse()
    {
        // C (worktree.c:462-472): git_worktree_unlock returns 1 when the
        // worktree is NOT locked (no error). The cached flag goes stale when
        // the file is removed out-of-band.
        using LibGit2CS.Repository.Worktree wt = await AddWorktreeAsync("wt2");
        await wt.LockAsync("reason", TestContext.Current.CancellationToken);
        Assert.True(wt.IsLocked);

        // External removal (e.g. `git worktree unlock`).
        File.Delete(Path.Combine(wt.GitdirPath, "locked"));

        Assert.False(wt.Unlock());
        Assert.False(File.Exists(Path.Combine(wt.GitdirPath, "locked")));
    }

    [Fact]
    public async Task Unlock_NotLocked_ReturnsFalse_NoThrow()
    {
        // C: git_worktree_unlock on an unlocked worktree returns 1 — NOT an
        // error.
        using LibGit2CS.Repository.Worktree wt = await AddWorktreeAsync("wt3");
        Assert.False(wt.Unlock());
    }

    // ── the lock reason is the RAW file contents ───────────────────

    [Fact]
    public async Task GetLockReason_ReturnsRawContents_NotTrimmed()
    {
        // C (worktree.c:489-511): git_worktree__is_locked reads the `locked`
        // file verbatim — a trailing newline (as written by `git worktree
        // lock --reason`) is preserved.
        using LibGit2CS.Repository.Worktree wt = await AddWorktreeAsync("wt4");
        string lockedPath = Path.Combine(wt.GitdirPath, "locked");
        await File.WriteAllTextAsync(lockedPath, "my reason\n", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("my reason\n", await wt.GetLockReasonAsync(TestContext.Current.CancellationToken));
    }
}
