using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Repository;

public sealed class RepositoryInitTests : IDisposable
{
    private readonly string _tempDir;

    public RepositoryInitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RepoInitTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Init_CreatesStandardRepo()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");

        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(repo);
        Assert.False(repo.IsBare);
        Assert.NotNull(repo.Workdir);

        // Verify .git directory structure.
        string gitDir = Path.Combine(repoPath, ".git");
        Assert.True(Directory.Exists(gitDir));
        Assert.True(Directory.Exists(Path.Combine(gitDir, "objects")));
        Assert.True(Directory.Exists(Path.Combine(gitDir, "objects", "info")));
        Assert.True(Directory.Exists(Path.Combine(gitDir, "objects", "pack")));
        Assert.True(Directory.Exists(Path.Combine(gitDir, "refs")));
        Assert.True(Directory.Exists(Path.Combine(gitDir, "refs", "heads")));
        Assert.True(Directory.Exists(Path.Combine(gitDir, "refs", "tags")));
        Assert.True(Directory.Exists(Path.Combine(gitDir, "hooks")));
        Assert.True(Directory.Exists(Path.Combine(gitDir, "info")));
        Assert.True(File.Exists(Path.Combine(gitDir, "description")));
        Assert.True(File.Exists(Path.Combine(gitDir, "HEAD")));
        Assert.True(File.Exists(Path.Combine(gitDir, "config")));
    }

    [Fact]
    public async Task Init_HeadPointsToMasterByDefault()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");

        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        string headContent = await File.ReadAllTextAsync(Path.Combine(repoPath, ".git", "HEAD"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ref: refs/heads/master\n", headContent);
    }

    [Fact]
    public async Task Init_WithInitialHead_UsesSpecifiedBranch()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");

        await using GitRepository repo = await GitRepository.InitExtAsync(repoPath, new GitRepositoryInitOptions
        {
            Flags = GitRepositoryInitFlags.Mkpath,
            InitialHead = "main",
        }, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        string headContent = await File.ReadAllTextAsync(Path.Combine(repoPath, ".git", "HEAD"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ref: refs/heads/main\n", headContent);
    }

    [Fact]
    public async Task Init_WithFullRefName_UsesAsIs()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");

        await using GitRepository repo = await GitRepository.InitExtAsync(repoPath, new GitRepositoryInitOptions
        {
            Flags = GitRepositoryInitFlags.Mkpath,
            InitialHead = "refs/heads/develop",
        }, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        string headContent = await File.ReadAllTextAsync(Path.Combine(repoPath, ".git", "HEAD"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ref: refs/heads/develop\n", headContent);
    }

    [Fact]
    public async Task Init_Bare_CreatesBareRepo()
    {
        string repoPath = Path.Combine(_tempDir, "mybare");

        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(repo.IsBare);
        Assert.Null(repo.Workdir);

        // Bare repo: no .git subdirectory; the path IS the gitdir.
        string gitDir = repoPath;
        Assert.True(Directory.Exists(gitDir));
        Assert.True(Directory.Exists(Path.Combine(gitDir, "objects")));
        Assert.False(Directory.Exists(Path.Combine(repoPath, ".git")));

        // Config should have core.bare = true.
        Assert.True(await repo.Config.GetBoolAsync("core.bare", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Init_ConfigHasExpectedKeys()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");

        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, await repo.Config.GetIntAsync("core.repositoryformatversion", cancellationToken: TestContext.Current.CancellationToken));
        // core.filemode is true on POSIX (chmod is supported) and false on
        // Windows. Matches GitRepository.IsFilemodeSupported() which backs
        // the init-time default written by WriteInitConfigAsync.
        Assert.Equal(!OperatingSystem.IsWindows(), await repo.Config.GetBoolAsync("core.filemode", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await repo.Config.GetBoolAsync("core.bare", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await repo.Config.GetBoolAsync("core.logallrefupdates", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Init_BareConfigHasExpectedKeys()
    {
        string repoPath = Path.Combine(_tempDir, "mybare");

        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, await repo.Config.GetIntAsync("core.repositoryformatversion", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await repo.Config.GetBoolAsync("core.bare", cancellationToken: TestContext.Current.CancellationToken));
        // Bare repos don't have logallrefupdates by default.
        Assert.False(await repo.Config.GetBoolAsync("core.logallrefupdates", false, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Init_CanWriteCommitAfterInit()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");

        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Write a blob, tree, and commit — verifying the full write pipeline works.
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);

        var sig = new GitSignature("Test", "test@example.com", new GitTime(100, 0));
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "initial\n",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Commit? commit = await repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        Assert.Equal("initial\n", commit!.Message);
    }

    [Fact]
    public async Task Init_Mkpath_CreatesNestedDirs()
    {
        string repoPath = Path.Combine(_tempDir, "a", "b", "c", "myrepo");

        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(Path.Combine(repoPath, ".git")));
    }

    [Fact]
    public async Task StateCleanup_RemovesStateFiles()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Create state files.
        string gitDir = Path.Combine(repoPath, ".git");
        await File.WriteAllTextAsync(Path.Combine(gitDir, "MERGE_HEAD"), "abc\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(gitDir, "MERGE_MSG"), "merge msg\n", cancellationToken: TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(gitDir, "sequencer"));

        repo.StateCleanup();

        Assert.False(File.Exists(Path.Combine(gitDir, "MERGE_HEAD")));
        Assert.False(File.Exists(Path.Combine(gitDir, "MERGE_MSG")));
        Assert.False(Directory.Exists(Path.Combine(gitDir, "sequencer")));
    }

    [Fact]
    public async Task Message_ReadsMergeMsg()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        string gitDir = Path.Combine(repoPath, ".git");
        await File.WriteAllTextAsync(Path.Combine(gitDir, "MERGE_MSG"), "merge message\n", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("merge message\n", await repo.MessageAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Message_NoFile_ReturnsNull()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await repo.MessageAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveMessage_DeletesMergeMsg()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        string gitDir = Path.Combine(repoPath, ".git");
        await File.WriteAllTextAsync(Path.Combine(gitDir, "MERGE_MSG"), "msg\n", cancellationToken: TestContext.Current.CancellationToken);

        repo.RemoveMessage();

        Assert.False(File.Exists(Path.Combine(gitDir, "MERGE_MSG")));
    }

    [Fact]
    public async Task SetIdent_StoresNameAndEmail()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        repo.SetIdent("Alice", "alice@example.com");

        Assert.Equal("Alice", repo.IdentName);
        Assert.Equal("alice@example.com", repo.IdentEmail);
    }

    [Fact]
    public async Task SetOrigHead_WritesFile()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        var oid = GitOid.Parse("abcdefabcdefabcdefabcdefabcdefabcdefabcd".AsSpan(), GitHashAlgorithmKind.Sha1);
        await repo.SetOrigHeadAsync(oid, cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(Path.Combine(repoPath, ".git", "ORIG_HEAD"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal($"{oid}\n", content);
    }

    [Fact]
    public async Task Init_TemplateFiles_HaveExpectedContent()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        string gitDir = Path.Combine(repoPath, ".git");

        string description = await File.ReadAllTextAsync(Path.Combine(gitDir, "description"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Unnamed repository", description);

        string exclude = await File.ReadAllTextAsync(Path.Combine(gitDir, "info", "exclude"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("File patterns to ignore", exclude);

        string hooksReadme = await File.ReadAllTextAsync(Path.Combine(gitDir, "hooks", "README.sample"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Place appropriately named", hooksReadme);
    }

    [Fact]
    public async Task Init_CanOpenAfterInit()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");
        await using (await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        // Open the repo again.
        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(repo);
        Assert.False(repo.IsBare);
    }

    [Fact]
    public async Task Init_WithDefaultBranchConfig_UsesConfigValue()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");
        string globalConfigDir = Path.Combine(_tempDir, "globalcfg");
        Directory.CreateDirectory(globalConfigDir);
        await File.WriteAllTextAsync(Path.Combine(globalConfigDir, ".gitconfig"),
            "[init]\n\tdefaultBranch = develop\n", cancellationToken: TestContext.Current.CancellationToken);

        using GitContext ctx = new();
        ctx.Dirs.Set(GitSystemDir.Global, globalConfigDir);
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, ctx, cancellationToken: TestContext.Current.CancellationToken);

        string headContent = await File.ReadAllTextAsync(Path.Combine(repoPath, ".git", "HEAD"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ref: refs/heads/develop\n", headContent);
    }

    [Fact]
    public async Task Init_EnvDoesNotOverrideDefaultBranchConfig()
    {
        // C (repo_init_head, repository.c:2811-2817): GIT_INIT_BRANCH is not
        // a libgit2 env var — only init.defaultBranch (global config) counts.
        string repoPath = Path.Combine(_tempDir, "myrepo");
        string globalConfigDir = Path.Combine(_tempDir, "globalcfg");
        Directory.CreateDirectory(globalConfigDir);
        await File.WriteAllTextAsync(Path.Combine(globalConfigDir, ".gitconfig"),
            "[init]\n\tdefaultBranch = develop\n", cancellationToken: TestContext.Current.CancellationToken);

        using GitContext ctx = new();
        ctx.Dirs.Set(GitSystemDir.Global, globalConfigDir);
        ctx.Env["GIT_INIT_BRANCH"] = "feature";
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, ctx, cancellationToken: TestContext.Current.CancellationToken);

        string headContent = await File.ReadAllTextAsync(Path.Combine(repoPath, ".git", "HEAD"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ref: refs/heads/develop\n", headContent);
    }

    [Fact]
    public async Task Init_InvalidDefaultBranch_Throws()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        await repo.Config.SetStringAsync("init.defaultBranch", "invalid..name", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => repo.InitialBranchAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(GitErrorCode.Invalid, ex.Code);
    }

    [Fact]
    public async Task Init_RawNonUtf8DefaultBranch_HeadFileKeepsRawBytes()
    {
        // C's repo_init_head feeds the raw init.defaultBranch char* bytes to git_repository_create_head (repository.c:2793-2829) —
        // a non-UTF-8 branch name must land in the HEAD file byte-exact, not as a re-encoded U+FFFD.
        string repoPath = Path.Combine(_tempDir, "myrepo");
        string globalConfigDir = Path.Combine(_tempDir, "globalcfg");
        Directory.CreateDirectory(globalConfigDir);
        await File.WriteAllBytesAsync(Path.Combine(globalConfigDir, ".gitconfig"),
            [.. "[init]\n\tdefaultBranch = br"u8.ToArray(), 0xE9, .. "anch\n"u8.ToArray()],
            TestContext.Current.CancellationToken);

        using GitContext ctx = new();
        ctx.Dirs.Set(GitSystemDir.Global, globalConfigDir);
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, ctx, cancellationToken: TestContext.Current.CancellationToken);

        byte[] headContent = await File.ReadAllBytesAsync(Path.Combine(repoPath, ".git", "HEAD"), TestContext.Current.CancellationToken);
        byte[] expected = [.. "ref: refs/heads/br"u8.ToArray(), 0xE9, .. "anch\n"u8.ToArray()];
        Assert.True(headContent.AsSpan().SequenceEqual(expected));
    }

    [Fact]
    public async Task InitialBranch_ReadsRepoConfig()
    {
        string repoPath = Path.Combine(_tempDir, "myrepo");
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        await repo.Config.SetStringAsync("init.defaultBranch", "main", cancellationToken: TestContext.Current.CancellationToken);

        string result = await repo.InitialBranchAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("refs/heads/main", result);
    }
}
