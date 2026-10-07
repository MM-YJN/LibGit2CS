using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Status;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Status;

// Parity tests for the out-of-range show value, detached HEAD at an annotated tag, and the "//foo" pattern. Expectations C-verified against libgit2
// 1.9.4 (status.c:246-249, repository.c:3366-3381, attr_file.c:772-777).
public sealed class StatusIgnoreLowParityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _cleanupDirs = [];

    public StatusIgnoreLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StatusLow_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (string dir in _cleanupDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private string NewDir()
    {
        string dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _cleanupDirs.Add(dir);
        return dir;
    }

    private static GitContext NewContext()
    {
        GitContext ctx = new();
        ctx.Env["HOME"] = Path.Combine(Path.GetTempPath(), "LibGit2CS_nonexistent_" + Guid.NewGuid().ToString("N"));
        ctx.Env["XDG_CONFIG_HOME"] = null;
        ctx.Dirs.Reset();
        ctx.Dirs.Set(LibGit2CS.IO.GitSystemDir.System, string.Empty);
        ctx.Dirs.Set(LibGit2CS.IO.GitSystemDir.ProgramData, string.Empty);
        return ctx;
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private static async Task<GitOid> CommitFileAsync(GitRepository repo, string fileName, string content, string message)
    {
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid tree = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);

        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);
    }

    // ── out-of-range show value ───────────────────────────────────────

    [Fact]
    public async Task Status_OutOfRangeShow_ThrowsInvalid()
    {
        // C (status.c:246-249): show > GIT_STATUS_SHOW_WORKDIR_ONLY →
        // GIT_EINVALID "unknown status 'show' option".
        await using GitRepository repo = await GitRepository.InitAsync(NewDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);
        _ = await CommitFileAsync(repo, "f.txt", "x\n", "m\n");

        var options = new GitStatusOptions { Show = (GitStatusShow)3 };

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.StatusNewAsync(options, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Invalid, ex.Code);
        Assert.Contains("unknown status 'show' option", ex.Message);
    }

    // ── detached HEAD at an annotated tag ─────────────────────────────

    [Fact]
    public async Task Status_DetachedHeadAtAnnotatedTag_Works()
    {
        // C (repository.c:3366-3381): the HEAD object is peeled to a TREE, so
        // status works with a detached HEAD at an annotated tag (a plain
        // Commit lookup would throw on tag objects).
        await using GitRepository repo = await GitRepository.InitAsync(NewDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);
        GitOid c1 = await CommitFileAsync(repo, "f.txt", "x\n", "m\n");
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);
        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        Commit? commit = await repo.ObjectLookupAsync<Commit>(c1, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        _ = await repo.TagCreateAsync("v1", commit!, TestSig(), "tag message\n", cancellationToken: TestContext.Current.CancellationToken);

        // Detach HEAD at the tag.
        GitOid tagOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("refs/tags/v1", TestContext.Current.CancellationToken))!).Target;
        await repo.SetHeadDetachedAsync(tagOid, TestContext.Current.CancellationToken);

        using GitStatusList list = await repo.StatusNewAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(list);
    }

    // ── "//foo" pattern ──────────────────────────────────────────────

    [Fact]
    public async Task Status_DoubleSlashPattern_DoesNotIgnoreTheFile()
    {
        // C (attr_file.c:772-777): `if (slash_count == 1 && pattern == scan)
        // pattern++;` fires ONCE — exactly one leading '/' is consumed.
        // "//foo" keeps "/foo" (FULLPATH), which never matches a
        // workdir-relative path, so the file stays untracked (C-verified:
        // git_ignore_path_is_ignored("c.test") == 0 with "//c.test").
        await using GitRepository repo = await GitRepository.InitAsync(NewDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);
        _ = await CommitFileAsync(repo, "keep.txt", "k\n", "m\n");
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);
        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "foo"), "ignored\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitignore"), "//foo\n", cancellationToken: TestContext.Current.CancellationToken);

        using GitStatusList list = await repo.StatusNewAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The file is UNTRACKED (not ignored) — "//foo" does not match "foo".
        GitStatusEntry? foo = list.Entries.FirstOrDefault(e => e.Path.ToUtf8String() == "foo");
        Assert.NotNull(foo);
        Assert.Equal(LibGit2CS.Diff.GitDeltaStatus.Untracked, foo!.IndexToWorkdir?.Status);
    }
}
