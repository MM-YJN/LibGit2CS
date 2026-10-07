using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Remote;

/// <summary>
/// Parity regression tests for behaviors in
/// libgit2 1.9.4.
/// C reference: clone.c.
/// </summary>
public sealed class CloneMedParityTests : IDisposable
{
    private readonly string _tempDir;

    public CloneMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CloneMed_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async ValueTask<GitRepository> CreateSourceRepo(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());

        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);

        GitSignature sig = TestSig();
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "initial\n",
            UpdateRef = "refs/heads/master",
        });
        _ = commitOid;
        return repo;
    }

    /// <summary>
    /// Creates a source repo with master (HEAD default) + a feature branch.
    /// </summary>
    private async ValueTask<GitRepository> CreateSourceRepoWithBranches(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());

        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);

        GitSignature sig = TestSig();
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "initial\n",
            UpdateRef = "refs/heads/master",
        });
        await repo.ReferenceCreateAsync("refs/heads/feature", commitOid, force: true);
        return repo;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreationCallbacks_PropagateCancellation(bool forSubmodule, bool cancelInRemote)
    {
        using var context = new GitContext();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using GitRepository source = await CreateSourceRepo("callback-source");
        string targetPath = Path.Combine(_tempDir, "callback-target");
        bool repositoryCalled = false;
        bool remoteCalled = false;
        var options = new GitCloneOptions
        {
            RepositoryCreate = async (path, bare, ctx, ct) =>
            {
                repositoryCalled = true;
                Assert.Equal(cts.Token, ct);
                Assert.Same(context, ctx);
                if (!cancelInRemote)
                {
                    await cts.CancelAsync();
                }

                ct.ThrowIfCancellationRequested();
                return await GitRepository.InitAsync(path, bare, ctx, ct);
            },
            RemoteCreate = async (repo, name, url, ct) =>
            {
                remoteCalled = true;
                Assert.Equal(cts.Token, ct);
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return await repo.RemoteCreateAsync(name, url, ct);
            },
        };

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using GitRepository cloned = forSubmodule
                ? await GitClone.RunForSubmoduleAsync(source.Path, targetPath, options, context, cts.Token)
                : await GitClone.RunAsync(source.Path, targetPath, options, context, cts.Token);
        });

        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.True(repositoryCalled);
        Assert.Equal(cancelInRemote, remoteCalled);
    }

    // ── both clone paths reject a non-empty target repository ───────

    [Fact]
    public async Task CloneInto_NonEmptyTargetRepo_ThrowsInvalid()
    {
        // C (clone.c:426-429): clone_into rejects a non-empty target repo —
        // GIT_ERROR_INVALID "the repository is not empty" (rc=-1).
        await using GitRepository source = await CreateSourceRepo("src9");
        string targetPath = Path.Combine(_tempDir, "target9");

        var opts = new GitCloneOptions
        {
            RepositoryCreate = async (path, bare, ctx, ct) =>
            {
                GitRepository repo = await GitRepository.InitAsync(path, bare, ctx, ct);
                // Add a commit + ref so the repo is NOT empty.
                GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), ct);
                using GitTreeBuilder bld = repo.NewTreeBuilder();
                await bld.InsertAsync("x.txt", blobOid, GitFileMode.Regular, ct);
                GitOid treeOid = await bld.WriteAsync(ct);
                _ = await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = TestSig(),
                    Committer = TestSig(),
                    Message = "pre-existing\n",
                    UpdateRef = "refs/heads/master",
                }, ct);
                return repo;
            },
        };

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, opts, new GitContext(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("not empty", ex.Message);
    }

    [Fact]
    public async Task CloneLocalInto_NonEmptyTargetRepo_ThrowsInvalid()
    {
        // C (clone.c:503-506): clone_local_into has the same check.
        await using GitRepository source = await CreateSourceRepo("src9l");
        string targetPath = Path.Combine(_tempDir, "target9l");

        var opts = new GitCloneOptions
        {
            RepositoryCreate = async (path, bare, ctx, ct) =>
            {
                GitRepository repo = await GitRepository.InitAsync(path, bare, ctx, ct);
                GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), ct);
                using GitTreeBuilder bld = repo.NewTreeBuilder();
                await bld.InsertAsync("x.txt", blobOid, GitFileMode.Regular, ct);
                GitOid treeOid = await bld.WriteAsync(ct);
                _ = await repo.CommitCreateAsync(new CommitCreateOptions
                {
                    Tree = treeOid,
                    Parents = [],
                    Author = TestSig(),
                    Committer = TestSig(),
                    Message = "pre-existing\n",
                    UpdateRef = "refs/heads/master",
                }, ct);
                return repo;
            },
        };

        // A plain local path (not a URL) → AUTO → local-clone path.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitClone.RunAsync(source.Path, targetPath, opts, new GitContext(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("not empty", ex.Message);
    }

    // ── failure cleanup keeps a pre-existing root but removes contents ──

    [Fact]
    public async Task FailedCloneIntoPreexistingDir_KeepsRoot_RemovesGit()
    {
        // C (clone.c:638-640, 666-676): on failure git_futils_rmdir_r is
        // called with GIT_RMDIR_REMOVE_FILES | GIT_RMDIR_SKIP_ROOT when the
        // target dir pre-existed — contents (including .git) removed, root
        // kept.
        string targetPath = Path.Combine(_tempDir, "preexisting");
        Directory.CreateDirectory(targetPath);

        // A local dir that is not a repo → clone fails after init created .git.
        string fakeSource = Path.Combine(_tempDir, "not-a-repo");
        Directory.CreateDirectory(fakeSource);

        await Assert.ThrowsAsync<GitException>(async () =>
            await GitClone.RunAsync(fakeSource, targetPath, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(Directory.Exists(targetPath), "pre-existing root must be kept");
        Assert.Empty(Directory.GetFileSystemEntries(targetPath));
    }

    // ── local clone hard-links objects (never symlinks) ────────────

    [Fact]
    public async Task LocalClone_HardlinksObjectFiles_NoSymlinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // hardlink probe uses Unix file modes
        }

        await using GitRepository source = await CreateSourceRepo("src11");
        string targetPath = Path.Combine(_tempDir, "target11");

        await using GitRepository cloned = await GitClone.RunAsync(source.Path, targetPath, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Every file under the destination objects dir must NOT be a symlink.
        string dstObjects = Path.Combine(cloned.Path, "objects");
        Assert.True(Directory.Exists(dstObjects));
        foreach (string file in Directory.GetFiles(dstObjects, "*", SearchOption.AllDirectories))
        {
            Assert.Null(new FileInfo(file).LinkTarget);
        }

        // Hardlink check via inode sharing: write through the dst path and
        // observe the same bytes at the source path (loose object files).
        string? dstLoose = Directory.GetFiles(dstObjects, "*", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).Length == 38);
        Assert.NotNull(dstLoose);
        string srcLoose = Path.Combine(source.Path, "objects", Path.GetRelativePath(dstObjects, dstLoose));

        File.SetUnixFileMode(dstLoose, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await File.WriteAllTextAsync(dstLoose, "hardlink probe\n", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("hardlink probe\n", await File.ReadAllTextAsync(srcLoose, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── checkout_branch creates refs/remotes/origin/HEAD for the
    //        DEFAULT branch even when it differs from the requested branch ──

    [Fact]
    public async Task Clone_BranchNameNotDefault_StillCreatesRemoteHead()
    {
        // C (clone.c:285-295): update_head_to_branch calls
        // git_remote__default_branch and, if it fits a fetch refspec, calls
        // update_remote_head with the DEFAULT branch — no comparison with
        // the requested branch. Cloning "feature" from a remote whose HEAD
        // is "master" must still create refs/remotes/origin/HEAD →
        // refs/remotes/origin/master.
        await using GitRepository source = await CreateSourceRepoWithBranches("src14");
        string targetPath = Path.Combine(_tempDir, "target14");

        await using GitRepository cloned = await GitClone.RunAsync(
            FixtureLoader.TestFileUrl(source.Path),
            targetPath,
            new GitCloneOptions { BranchName = "feature" },
            new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        GitReference? remoteHead = await cloned.ReferenceLookupAsync("refs/remotes/origin/HEAD", TestContext.Current.CancellationToken);
        Assert.NotNull(remoteHead);
        Assert.True(remoteHead is GitSymbolicReference);
        Assert.Equal("refs/remotes/origin/master", ((GitSymbolicReference)remoteHead).TargetName);
    }
}
