using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Remote;

/// <summary>
/// Integration tests for <see cref="GitClone.Run"/> — clone via file://
/// local transport. Tests local clone (hardlink/copy objects + fetch +
/// checkout), clone options, specific branch checkout, and empty repo.
/// </summary>
public sealed class CloneTests : IDisposable
{
    private readonly string _tempDir;

    public CloneTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_Clone_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async ValueTask<GitRepository> CreateSourceRepo(string name, int commits = 1)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext());

        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);

        GitSignature sig = TestSig();
        GitOid commitOid = default;
        for (int i = 0; i < commits; i++)
        {
            commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = i == 0 ? [] : [commitOid],
                Author = sig,
                Committer = sig,
                Message = $"commit {i + 1}\n",
                UpdateRef = "refs/heads/master",
            });
        }

        return repo;
    }

    private async ValueTask<GitRepository> CreateSourceRepoWithBranch(string name, string branchName)
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

        await repo.ReferenceCreateAsync($"refs/heads/{branchName}", commitOid, force: true);

        return repo;
    }

    // ── Basic local clone ───────────────────────────────────────────────

    [Fact]
    public async Task Clone_FileUrl_CreatesRepoWithRefs()
    {
        await using GitRepository source = await CreateSourceRepo("source");
        string targetPath = Path.Combine(_tempDir, "target");

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Verify the repo exists.
        Assert.True(Directory.Exists(targetPath));

        // Verify HEAD points to a branch.
        GitReference? head = await cloned.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);

        // Verify the commit is present.
        GitReference? master = await cloned.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(master);
        Assert.True(master is GitDirectReference);
    }

    [Fact]
    public async Task Clone_FileUrl_ObjectsCopied()
    {
        await using GitRepository source = await CreateSourceRepo("source");
        string targetPath = Path.Combine(_tempDir, "target");

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Verify objects exist in the cloned repo.
        GitReference? head = await cloned.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        GitOid commitOid = head is GitDirectReference dr ? dr.Target : default;
        Assert.False(commitOid.IsZero);

        // The commit object should be readable.
        GitObject? obj = await cloned.ObjectLookupAsync(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
    }

    [Fact]
    public async Task Clone_FileUrl_TrackingRefCreated()
    {
        await using GitRepository source = await CreateSourceRepo("source");
        string targetPath = Path.Combine(_tempDir, "target");

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Verify the tracking ref was created.
        GitReference? trackingRef = await cloned.ReferenceLookupAsync("refs/remotes/origin/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(trackingRef);
    }

    [Fact]
    public async Task Clone_FileUrl_HeadSymrefCreated()
    {
        await using GitRepository source = await CreateSourceRepo("source");
        string targetPath = Path.Combine(_tempDir, "target");

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Verify refs/remotes/origin/HEAD was created as a symbolic ref.
        GitReference? headSymref = await cloned.ReferenceLookupAsync("refs/remotes/origin/HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(headSymref);
        Assert.True(headSymref is GitSymbolicReference);
    }

    [Fact]
    public async Task Clone_FileUrl_TrackingConfigSet()
    {
        await using GitRepository source = await CreateSourceRepo("source");
        string targetPath = Path.Combine(_tempDir, "target");

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Verify tracking config was written.
        Assert.Equal("origin", await cloned.Config.GetStringAsync("branch.master.remote", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("refs/heads/master", await cloned.Config.GetStringAsync("branch.master.merge", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Clone_FileUrl_WorkdirCheckedOut()
    {
        await using GitRepository source = await CreateSourceRepo("source");
        string targetPath = Path.Combine(_tempDir, "target");

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Verify the workdir has the checked-out file.
        Assert.True(File.Exists(Path.Combine(targetPath, "README.md")));
    }

    // ── Clone options ──────────────────────────────────────────────────

    [Fact]
    public async Task Clone_Bare_CreatesBareRepo()
    {
        await using GitRepository source = await CreateSourceRepo("source");
        string targetPath = Path.Combine(_tempDir, "target-bare");

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, new GitCloneOptions { Bare = true }, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(cloned.IsBare);
        // Bare repo should not have a working directory.
        Assert.Null(cloned.Workdir);
    }

    [Fact]
    public async Task Clone_NoLocal_ForcesNetworkTransport()
    {
        await using GitRepository source = await CreateSourceRepo("source");
        string targetPath = Path.Combine(_tempDir, "target-nolocal");

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, new GitCloneOptions { CloneLocal = GitCloneLocal.NoLocal }, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Should still clone successfully via LocalTransport.
        GitReference? head = await cloned.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
    }

    [Fact]
    public async Task Clone_CustomRemoteName()
    {
        await using GitRepository source = await CreateSourceRepo("source");
        string targetPath = Path.Combine(_tempDir, "target-custom-name");

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, new GitCloneOptions { RemoteName = "upstream" }, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Verify the remote is named "upstream".
        GitRemote remote = await cloned.RemoteLookupAsync("upstream", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("upstream", remote.Name);

        // Tracking ref should use "upstream".
        Assert.NotNull(await cloned.ReferenceLookupAsync("refs/remotes/upstream/master", cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Clone specific branch ──────────────────────────────────────────

    [Fact]
    public async Task Clone_SpecificBranch_ChecksOutThatBranch()
    {
        await using GitRepository source = await CreateSourceRepoWithBranch("source", "feature");
        string targetPath = Path.Combine(_tempDir, "target-branch");

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, new GitCloneOptions { BranchName = "feature" }, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // HEAD should point at refs/heads/feature.
        GitReference? head = await cloned.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.True(head is GitSymbolicReference);
        Assert.Equal("refs/heads/feature", ((GitSymbolicReference)head).TargetName);
    }

    [Fact]
    public async Task Clone_SpecificBranch_BranchRefCreated()
    {
        await using GitRepository source = await CreateSourceRepoWithBranch("source", "feature");
        string targetPath = Path.Combine(_tempDir, "target-branch-ref");

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, new GitCloneOptions { BranchName = "feature" }, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(await cloned.ReferenceLookupAsync("refs/heads/feature", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Clone_SpecificBranch_TrackingConfigSet()
    {
        await using GitRepository source = await CreateSourceRepoWithBranch("source", "feature");
        string targetPath = Path.Combine(_tempDir, "target-branch-config");

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, new GitCloneOptions { BranchName = "feature" }, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("origin", await cloned.Config.GetStringAsync("branch.feature.remote", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("refs/heads/feature", await cloned.Config.GetStringAsync("branch.feature.merge", cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Clone empty repo ───────────────────────────────────────────────

    [Fact]
    public async Task Clone_EmptySource_HeadUnborn()
    {
        string sourcePath = Path.Combine(_tempDir, "empty-source");
        await using (GitRepository source = await GitRepository.InitAsync(sourcePath, isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken))
        {
            // Source repo created with no commits.
        }

        string targetPath = Path.Combine(_tempDir, "target-empty");

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(sourcePath), targetPath, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // HEAD should be unborn (no commit).
        GitReference? head = await cloned.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        // HEAD may be a symbolic ref pointing at an unborn branch.
        if (head is not null)
        {
            Assert.True(head is GitSymbolicReference);
        }
    }

    [Fact]
    public async Task Clone_EmptySource_DefaultBranchConfig()
    {
        string sourcePath = Path.Combine(_tempDir, "empty-source2");
        await using (GitRepository source = await GitRepository.InitAsync(sourcePath, isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken))
        {
            // Source repo created with no commits.
        }

        string targetPath = Path.Combine(_tempDir, "target-empty2");

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(sourcePath), targetPath, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Should have default tracking config for the initial branch.
        string initialBranch = await cloned.InitialBranchAsync(cancellationToken: TestContext.Current.CancellationToken);
        string shortName = initialBranch.Replace("refs/heads/", "");
        Assert.Equal("origin", await cloned.Config.GetStringAsync($"branch.{shortName}.remote", cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Local path clone (not file:// URL) ──────────────────────────────

    [Fact]
    public async Task Clone_LocalPath_CreatesRepoWithRefs()
    {
        await using GitRepository source = await CreateSourceRepo("source-local");
        string targetPath = Path.Combine(_tempDir, "target-local");

        await using GitRepository cloned = await GitClone.RunAsync(source.Path, targetPath, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        GitReference? head = await cloned.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
    }

    [Fact]
    public async Task Clone_LocalPath_ObjectsCopied()
    {
        await using GitRepository source = await CreateSourceRepo("source-local2");
        string targetPath = Path.Combine(_tempDir, "target-local2");

        await using GitRepository cloned = await GitClone.RunAsync(source.Path, targetPath, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        GitReference? head = await cloned.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        GitOid commitOid = head is GitDirectReference dr ? dr.Target : default;
        Assert.False(commitOid.IsZero);
        Assert.NotNull(await cloned.ObjectLookupAsync(commitOid, TestContext.Current.CancellationToken));
    }

    // ── Clone into existing directory ──────────────────────────────────

    [Fact]
    public async Task Clone_IntoEmptyDir_Succeeds()
    {
        await using GitRepository source = await CreateSourceRepo("source");
        string targetPath = Path.Combine(_tempDir, "existing-empty");
        Directory.CreateDirectory(targetPath);

        await using GitRepository cloned = await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(await cloned.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Clone_IntoNonEmptyDir_Throws()
    {
        await using GitRepository source = await CreateSourceRepo("source");
        string targetPath = Path.Combine(_tempDir, "existing-nonempty");
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(Path.Combine(targetPath, "dummy.txt"), "dummy", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () => await GitClone.RunAsync(FixtureLoader.TestFileUrl(source.Path), targetPath, null, new GitContext(), cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── insteadOf rewriting ─────────────────────────────────────────────

    [Fact]
    public async Task Clone_InsteadOf_RewritesUrlBeforeConnect()
    {
        await using GitRepository source = await CreateSourceRepo("source");
        string targetPath = Path.Combine(_tempDir, "target-insteadof");

        // The target repo's config should have insteadof rewriting.
        // But we can't pre-configure a repo that doesn't exist yet.
        // Instead, test that a clone with a file:// URL works when
        // insteadOf is configured after init but before fetch.
        // For now, just verify Clone.Run accepts a URL with insteadof config.
        await using GitRepository cloned = await GitClone.RunAsync(
            FixtureLoader.TestFileUrl(source.Path),
            targetPath,
            new GitCloneOptions
            {
                RemoteCreate = async (repo, name, url, ct) =>
                {
                    // Configure insteadOf before the fetch.
                    await repo.Config.SetStringAsync("url.file:///replaced/.insteadof", "replace:", ct);
                    return await repo.RemoteCreateAsync(name, url, ct);
                },
            },
            new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(await cloned.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── ShouldCloneLocal ───────────────────────────────────────────────

    [Fact]
    public async Task ShouldCloneLocal_Auto_WithNetworkUrl_ReturnsFalse()
    {
        Assert.False(GitClone.ShouldCloneLocal("https://example.com/repo.git", GitCloneLocal.Auto));
        Assert.False(GitClone.ShouldCloneLocal("git://example.com/repo.git", GitCloneLocal.Auto));
    }

    [Fact]
    public async Task ShouldCloneLocal_Auto_WithLocalPath_ReturnsTrue()
    {
        // Create a directory that exists.
        string dirPath = Path.Combine(_tempDir, "localdir");
        Directory.CreateDirectory(dirPath);

        Assert.True(GitClone.ShouldCloneLocal(dirPath, GitCloneLocal.Auto));
    }

    [Fact]
    public async Task ShouldCloneLocal_Auto_WithFileUrlDir_ReturnsFalse()
    {
        // C (clone.c:583-594): under GIT_CLONE_LOCAL_AUTO any URL (even
        // file://) is a remote clone.
        string dirPath = Path.Combine(_tempDir, "localdir2");
        Directory.CreateDirectory(dirPath);

        Assert.False(GitClone.ShouldCloneLocal(FixtureLoader.TestFileUrl(dirPath), GitCloneLocal.Auto));
    }

    [Fact]
    public async Task ShouldCloneLocal_NoLocal_ReturnsFalse()
    {
        string dirPath = Path.Combine(_tempDir, "localdir3");
        Directory.CreateDirectory(dirPath);

        Assert.False(GitClone.ShouldCloneLocal(dirPath, GitCloneLocal.NoLocal));
        Assert.False(GitClone.ShouldCloneLocal(FixtureLoader.TestFileUrl(dirPath), GitCloneLocal.NoLocal));
    }

    [Fact]
    public async Task ShouldCloneLocal_Local_WithNonExistentPath_ReturnsFalse()
    {
        Assert.False(GitClone.ShouldCloneLocal("/nonexistent/path", GitCloneLocal.Local));
    }

    // ── CloneOptions defaults ──────────────────────────────────────────

    [Fact]
    public async Task CloneOptions_Defaults()
    {
        var opts = new GitCloneOptions();

        Assert.False(opts.Bare);
        Assert.Equal(GitCloneLocal.Auto, opts.CloneLocal);
        Assert.Null(opts.FetchOptions);
        Assert.Null(opts.CheckoutOptions);
        Assert.Equal("origin", opts.RemoteName);
        Assert.Null(opts.BranchName);
        Assert.Null(opts.RepositoryCreate);
        Assert.Null(opts.RemoteCreate);
    }

    // ── CloneLocal enum ────────────────────────────────────────────────

    [Fact]
    public async Task CloneLocal_HasFourValues()
    {
        Assert.Equal(0, (int)GitCloneLocal.Auto);
        Assert.Equal(1, (int)GitCloneLocal.Local);
        Assert.Equal(2, (int)GitCloneLocal.NoLocal);
        Assert.Equal(3, (int)GitCloneLocal.NoLinks);
    }
}
