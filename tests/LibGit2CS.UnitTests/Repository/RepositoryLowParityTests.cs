using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Repository;

/// <summary> Parity tests for repository core: (init does not create missing parents without MKDIR/MKPATH), (partial SetIdent falls
/// through to config), (empty-but-set GIT_OBJECT_DIRECTORY is honored), (new repository.h entry points: IsHeadDetachedAsync / IsHeadUnbornAsync /
/// IsShallow / IsEmptyAsync / SetNamespace / Ident). Expectations are C-verified against libgit2 1.9.4 (repository.c:2742-2780, refs.c:439-446,
/// repository.c:1485-1497, 2922-2944, 3068-3081, 3846-3865, 3141-3161). </summary>
public sealed class RepositoryLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public RepositoryLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RepositoryLow_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── init without MKDIR/MKPATH must not create parents ─────

    [Fact]
    public async Task InitExt_NoFlags_MissingParent_Fails()
    {
        // C (repository.c:2742-2780): only MKPATH creates missing parents; with Flags = None the gitdir is not created and the init fails
        // (GIT_MKDIR_VERIFY_DIR) — Directory.CreateDirectory must not create every missing parent.
        string baseDir = Path.Combine(_tempDir, "i9");
        Directory.CreateDirectory(baseDir);
        string path = Path.Combine(baseDir, "missing-parent", "repo");

        await Assert.ThrowsAsync<GitException>(async () =>
            await GitRepository.InitExtAsync(path, new GitRepositoryInitOptions { Flags = GitRepositoryInitFlags.None }, new GitContext(), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(baseDir, "missing-parent")));
    }

    [Fact]
    public async Task InitExt_MkdirOnly_MissingGrandparent_Fails()
    {
        // C: under MKDIR (without MKPATH) the gitdir's PARENT is created
        // non-recursively (GIT_MKDIR_SKIP_LAST) — a missing grandparent
        // still fails.
        string baseDir = Path.Combine(_tempDir, "i9b");
        Directory.CreateDirectory(baseDir);
        string path = Path.Combine(baseDir, "missing-parent", "repo.git");

        await Assert.ThrowsAsync<GitException>(async () =>
            await GitRepository.InitExtAsync(path, new GitRepositoryInitOptions { Flags = GitRepositoryInitFlags.Mkdir }, new GitContext(), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(baseDir, "missing-parent")));
    }

    [Fact]
    public async Task InitExt_Mkpath_MissingParent_Succeeds()
    {
        // Control: MKPATH still creates missing parents recursively.
        string baseDir = Path.Combine(_tempDir, "i9c");
        Directory.CreateDirectory(baseDir);
        string path = Path.Combine(baseDir, "missing-parent", "repo");

        await using GitRepository repo = await GitRepository.InitExtAsync(path, new GitRepositoryInitOptions { Flags = GitRepositoryInitFlags.Mkpath }, new GitContext(), TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(path));
    }

    // ── partial SetIdent falls through to config ───────────────

    [Fact]
    public async Task SetIdent_Partial_FallsThroughToConfig()
    {
        // C (refs.c:439-446, refs_configured_ident): the ident is used ONLY when BOTH name and email are set — a partial SetIdent falls through to
        // git_signature_default, never mixing the ident name with the config email.
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "i10"), isBare: false, new GitContext(), TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("user.name", "Config User", TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("user.email", "config@example.com", TestContext.Current.CancellationToken);

        // Partial ident: only the name is set.
        repo.SetIdent("Ident User", null);
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await repo.ReferenceCreateAsync("refs/heads/test", blob, logMessage: "create", cancellationToken: TestContext.Current.CancellationToken);

        // The reflog signature must use the CONFIG ident, not the mix.
        GitRefLog? reflog = await repo.ReferenceReadLogAsync("refs/heads/test", TestContext.Current.CancellationToken);
        Assert.NotNull(reflog);
        GitRefLogEntry last = reflog[0];
        Assert.Equal("Config User", last.Committer.Name);
        Assert.Equal("config@example.com", last.Committer.Email);
    }

    // ── empty-but-set GIT_OBJECT_DIRECTORY is honored ──────────

    [Fact]
    public async Task Open_EmptyGitObjectDirectory_Honored()
    {
        // C (repository.c:1485-1497, repository_odb_path): git__getenv returns ANY set value — including an empty one — as the objects dir override
        // (no IsNullOrEmpty gate falling back to commondir/objects).
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "i11"), isBare: true, new GitContext(), TestContext.Current.CancellationToken);
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // Reopen with GIT_OBJECT_DIRECTORY set to an empty value — the empty override IS honored (C's git__getenv returns any set value), so the objects dir is
        // no longer <commondir>/objects and the repo's own blob is not found
        // (an IsNullOrEmpty gate would fall back to <commondir>/objects, where
        // the blob WAS found).
        var ctx = new GitContext();
        ctx.Env["GIT_OBJECT_DIRECTORY"] = string.Empty;
        await using GitRepository reopened = await GitRepository.OpenExtAsync(
            repo.Path,
            RepositoryOpenFlags.FromEnv,
            ceilingDirs: null,
            ctx,
            TestContext.Current.CancellationToken);

        Assert.False(await reopened.Objects.ExistsAsync(blob, TestContext.Current.CancellationToken));
    }

    // ── repository.h entry points ──────────────────────────────

    [Fact]
    public async Task HeadState_DetachedAndUnborn()
    {
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "i13"), isBare: false, new GitContext(), TestContext.Current.CancellationToken);

        // Fresh repo: unborn.
        Assert.True(await repo.IsHeadUnbornAsync(TestContext.Current.CancellationToken));
        Assert.False(await repo.IsHeadDetachedAsync(TestContext.Current.CancellationToken));

        // Commit + attach HEAD → neither unborn nor detached.
        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("f", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid tree = await bld.WriteAsync(CancellationToken.None);
        GitOid commit = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "m\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
        await repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        Assert.False(await repo.IsHeadUnbornAsync(TestContext.Current.CancellationToken));
        Assert.False(await repo.IsHeadDetachedAsync(TestContext.Current.CancellationToken));

        // Detach → detached, not unborn.
        await repo.SetHeadDetachedAsync(commit, TestContext.Current.CancellationToken);
        Assert.False(await repo.IsHeadUnbornAsync(TestContext.Current.CancellationToken));
        Assert.True(await repo.IsHeadDetachedAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsShallow_ShallowFilePresence()
    {
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "i13b"), isBare: true, new GitContext(), TestContext.Current.CancellationToken);
        Assert.False(repo.IsShallow);

        await File.WriteAllTextAsync(Path.Combine(repo.Path, "shallow"), "a65fedf39aef3a1b9e8f7c6d5b4a3a2a1a0a0f0e\n", TestContext.Current.CancellationToken);
        Assert.True(repo.IsShallow);
    }

    [Fact]
    public async Task IsEmpty_And_NotEmpty()
    {
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "i13c"), isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        // A fresh repo is empty (HEAD symbolic → initial branch, no refs).
        Assert.True(await repo.IsEmptyAsync(TestContext.Current.CancellationToken));

        GitOid blob = await repo.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await repo.ReferenceCreateAsync("refs/heads/master", blob, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(await repo.IsEmptyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetNamespace_And_Ident()
    {
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "i13d"), isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        Assert.Null(repo.Namespace);
        repo.SetNamespace("my-namespace");
        Assert.Equal("my-namespace", repo.Namespace);

        // Ident getter reflects SetIdent.
        Assert.Null(repo.Ident.Name);
        Assert.Null(repo.Ident.Email);
        repo.SetIdent("N", "e@x");
        Assert.Equal("N", repo.Ident.Name);
        Assert.Equal("e@x", repo.Ident.Email);
    }
}
