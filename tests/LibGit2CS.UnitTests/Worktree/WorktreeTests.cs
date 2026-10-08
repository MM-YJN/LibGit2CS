using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Worktree;

public sealed class WorktreeTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public WorktreeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_WorktreeTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        // Use an explicit initial branch so the repo's HEAD is independent of
        // process-global state (GIT_INIT_BRANCH env var, SystemDirs global config)
        // that other test classes mutate concurrently under xUnit's default
        // class-parallel execution. WriteCommitWithFile writes refs/heads/master,
        // so HEAD must point at refs/heads/master.
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitExtAsync(_tempDir, new GitRepositoryInitOptions
        {
            Flags = GitRepositoryInitFlags.Mkpath,
            InitialHead = "master",
        }, new GitContext());
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

    private async Task<GitOid> WriteCommitWithFile(string fileName, string content)
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync();
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName);
        await idx.WriteAsync();
        GitOid treeOid = await idx.WriteTreeAsync();
        GitSignature sig = TestSig();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = $"add {fileName}\n",
            UpdateRef = "refs/heads/master",
        });
    }

    // --- List ---

    [Fact]
    public async Task List_NoWorktrees_ReturnsEmpty()
    {
        IReadOnlyList<LibGit2CS.Repository.Worktree> list = await _repo.WorktreeListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(list);
    }

    [Fact]
    public async Task List_WithWorktree_ReturnsOne()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");
        await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<LibGit2CS.Repository.Worktree> list = await _repo.WorktreeListAsync(TestContext.Current.CancellationToken);
        Assert.Single(list);
        Assert.Equal("topic", list[0].Name);
    }

    [Fact]
    public async Task List_MultipleWorktrees()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        await _repo.WorktreeAddAsync("wt1", Path.Combine(_tempDir, "wt1"), cancellationToken: TestContext.Current.CancellationToken);
        await _repo.WorktreeAddAsync("wt2", Path.Combine(_tempDir, "wt2"), cancellationToken: TestContext.Current.CancellationToken);
        await _repo.WorktreeAddAsync("wt3", Path.Combine(_tempDir, "wt3"), cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<LibGit2CS.Repository.Worktree> list = await _repo.WorktreeListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public async Task List_SkipsInvalidWorktreeDirs()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        await _repo.WorktreeAddAsync("valid", Path.Combine(_tempDir, "valid"), cancellationToken: TestContext.Current.CancellationToken);

        // Create an invalid worktree dir (missing HEAD).
        string wtBase = Path.Combine(_repo.CommonDir, "worktrees");
        Directory.CreateDirectory(Path.Combine(wtBase, "invalid"));
        await File.WriteAllTextAsync(Path.Combine(wtBase, "invalid", "commondir"), "/some/path\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(wtBase, "invalid", "gitdir"), "/some/path/.git\n", cancellationToken: TestContext.Current.CancellationToken);
        // No HEAD file → not a valid worktree dir.

        IReadOnlyList<LibGit2CS.Repository.Worktree> list = await _repo.WorktreeListAsync(TestContext.Current.CancellationToken);
        Assert.Single(list);
        Assert.Equal("valid", list[0].Name);
    }

    // --- Lookup ---

    [Fact]
    public async Task Lookup_ExistingWorktree_ReturnsIt()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");
        await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        LibGit2CS.Repository.Worktree? wt = await _repo.WorktreeLookupAsync("topic", TestContext.Current.CancellationToken);
        Assert.NotNull(wt);
        Assert.Equal("topic", wt!.Name);
    }

    [Fact]
    public async Task Lookup_NonexistentWorktree_ReturnsNull()
    {
        LibGit2CS.Repository.Worktree? wt = await _repo.WorktreeLookupAsync("nonexistent", TestContext.Current.CancellationToken);
        Assert.Null(wt);
    }

    // --- Add ---

    [Fact]
    public async Task Add_CreatesWorktreeWithAdminFiles()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");

        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("topic", wt.Name);
        Assert.True(Directory.Exists(wtPath));
        Assert.True(File.Exists(Path.Combine(wtPath, ".git")));
        Assert.True(File.Exists(Path.Combine(wt.GitdirPath, "HEAD")));
        Assert.True(File.Exists(Path.Combine(wt.GitdirPath, "commondir")));
        Assert.True(File.Exists(Path.Combine(wt.GitdirPath, "gitdir")));
    }

    [Fact]
    public async Task Add_ChecksOutBranchContent()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");

        await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        // The worktree should have the file checked out.
        Assert.True(File.Exists(Path.Combine(wtPath, "README.md")));
        Assert.Equal("hello\n", await File.ReadAllTextAsync(Path.Combine(wtPath, "README.md"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Add_CreatesNewBranch()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");

        await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? branch = await _repo.BranchLookupAsync("topic", GitBranchType.Local, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(branch);
    }

    [Fact]
    public async Task Add_AlreadyCheckedOut_Throws()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");
        await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        // Trying to add another worktree with the same branch should fail.
        await Assert.ThrowsAsync<GitException>(async () => await _repo.WorktreeAddAsync("topic", Path.Combine(_tempDir, "wt2"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Add_WithLock_CreatesLockedFile()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");

        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", wtPath, new WorktreeAddOptions { Lock = true }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(wt.IsLocked);
        Assert.True(File.Exists(Path.Combine(wt.GitdirPath, "locked")));
    }

    [Fact]
    public async Task Add_WithExplicitRef_UsesThatRef()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        // Create a second branch.
        var head = (await _repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        await _repo.BranchCreateAsync("feature", head!.Target, force: false, cancellationToken: TestContext.Current.CancellationToken);

        string wtPath = Path.Combine(_tempDir, "wt1");
        GitReference featureRef = (await _repo.BranchLookupAsync("feature", GitBranchType.Local, cancellationToken: TestContext.Current.CancellationToken))!;
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("wt1", wtPath, new WorktreeAddOptions { Ref = featureRef }, cancellationToken: TestContext.Current.CancellationToken);

        // The worktree's HEAD should point at refs/heads/feature.
        string headContent = (await File.ReadAllTextAsync(Path.Combine(wt.GitdirPath, "HEAD"), cancellationToken: TestContext.Current.CancellationToken)).Trim();
        Assert.Equal("ref: refs/heads/feature", headContent);
    }

    [Fact]
    public async Task Add_CheckoutExisting_UsesExistingBranch()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        // Create a branch first.
        var head = (await _repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken)) as GitDirectReference;
        await _repo.BranchCreateAsync("existing", head!.Target, force: false, cancellationToken: TestContext.Current.CancellationToken);

        string wtPath = Path.Combine(_tempDir, "wt1");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("existing", wtPath,
            new WorktreeAddOptions { CheckoutExisting = true }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("existing", wt.Name);
    }

    // --- Lock / Unlock ---

    [Fact]
    public async Task Lock_WithReason_StoresReason()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        await wt.LockAsync("on removable media", TestContext.Current.CancellationToken);

        Assert.True(wt.IsLocked);
        Assert.Equal("on removable media", await wt.GetLockReasonAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lock_WithoutReason_StoresEmptyString()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        await wt.LockAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(wt.IsLocked);
        Assert.Equal(string.Empty, await wt.GetLockReasonAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unlock_RemovesLock()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);
        await wt.LockAsync("reason", TestContext.Current.CancellationToken);

        Assert.True(wt.Unlock());

        Assert.False(wt.IsLocked);
        Assert.False(File.Exists(Path.Combine(wt.GitdirPath, "locked")));
    }

    [Fact]
    public async Task Unlock_UnlockedWorktree_ReturnsFalse()
    {
        // C (worktree.c:462-472): git_worktree_unlock on an unlocked
        // worktree returns 1 — NOT an error.
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(wt.Unlock());
    }

    // --- Validate ---

    [Fact]
    public async Task Validate_ValidWorktree_DoesNotThrow()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        wt.Validate();
    }

    [Fact]
    public async Task Validate_InvalidCommonDir_Throws()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        // Delete the commondir file → the gitdir is no longer a valid worktree dir.
        File.Delete(Path.Combine(wt.GitdirPath, "commondir"));

        Assert.Throws<GitException>(() => wt.Validate());
    }

    [Fact]
    public async Task Validate_MissingWorktreeDir_Throws()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        // Delete the worktree directory.
        Directory.Delete(wtPath, recursive: true);

        Assert.Throws<GitException>(() => wt.Validate());
    }

    // --- Name / Path ---

    [Fact]
    public async Task Name_ReturnsWorktreeName()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("myname", Path.Combine(_tempDir, "wt1"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("myname", wt.Name);
    }

    [Fact]
    public async Task Path_ReturnsWorktreePath()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        // The worktree path is the directory (without trailing slash,
        // matching git_fs_path_dirname of the gitlink). Internal paths use
        // forward slashes and resolve symlinks, so canonicalize the
        // expected path before comparing.
        Assert.Equal(PathHelpers.PrettifyDir(wtPath).TrimEnd('/'), wt.Path);
    }

    // --- Prune ---

    [Fact]
    public async Task IsPrunable_ValidWorktree_ReturnsFalse()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", Path.Combine(_tempDir, "wt1"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(wt.IsPrunable());
    }

    [Fact]
    public async Task IsPrunable_ValidWithValidFlag_ReturnsTrue()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", Path.Combine(_tempDir, "wt1"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(wt.IsPrunable(new WorktreePruneOptions { Flags = WorktreePruneFlags.Valid }));
    }

    [Fact]
    public async Task IsPrunable_LockedWorktree_ReturnsFalse()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", Path.Combine(_tempDir, "wt1"), cancellationToken: TestContext.Current.CancellationToken);
        await wt.LockAsync("reason", TestContext.Current.CancellationToken);

        Assert.False(wt.IsPrunable());
    }

    [Fact]
    public async Task IsPrunable_LockedWithLockedFlag_ReturnsTrue()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", Path.Combine(_tempDir, "wt1"), cancellationToken: TestContext.Current.CancellationToken);
        await wt.LockAsync("reason", TestContext.Current.CancellationToken);

        Assert.True(wt.IsPrunable(new WorktreePruneOptions { Flags = WorktreePruneFlags.Valid | WorktreePruneFlags.Locked }));
    }

    [Fact]
    public async Task IsPrunable_MissingWorktreeDir_ReturnsTrue()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        // Delete the worktree directory → worktree becomes invalid → prunable.
        Directory.Delete(wtPath, recursive: true);

        Assert.True(wt.IsPrunable());
    }

    [Fact]
    public async Task Prune_RemovesAdminDir()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", Path.Combine(_tempDir, "wt1"), cancellationToken: TestContext.Current.CancellationToken);
        string gitdirPath = wt.GitdirPath;

        // Make it prunable by deleting the worktree directory.
        Directory.Delete(wt.Path, recursive: true);

        wt.Prune();

        Assert.False(Directory.Exists(gitdirPath));
    }

    [Fact]
    public async Task Prune_WithWorkingTreeFlag_RemovesWorktreeDir()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        wt.Prune(new WorktreePruneOptions { Flags = WorktreePruneFlags.Valid | WorktreePruneFlags.WorkingTree });

        Assert.False(Directory.Exists(wtPath));
    }

    [Fact]
    public async Task Prune_ValidWorktree_Throws()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", Path.Combine(_tempDir, "wt1"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Throws<GitException>(() => wt.Prune());
    }

    // --- Add + Remove + Add cycle ---

    [Fact]
    public async Task Add_Remove_Add_Cycle()
    {
        await WriteCommitWithFile("README.md", "hello\n");
        string wtPath = Path.Combine(_tempDir, "wt1");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        // Remove the worktree dir and prune.
        Directory.Delete(wtPath, recursive: true);
        wt.Prune();

        // Should be able to add again.
        LibGit2CS.Repository.Worktree wt2 = await _repo.WorktreeAddAsync("topic2", wtPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("topic2", wt2.Name);
    }
}
