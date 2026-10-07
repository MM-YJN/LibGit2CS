using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.UnitTests.Status;

/// <summary> Parity tests for status/ignore. </summary>
public sealed class StatusIgnoreMedParityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _cleanupDirs = [];

    public StatusIgnoreMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StatusMed_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static async Task<GitOid> WriteCommit(GitRepository repo, string fileName, string content)
    {
        LibGit2CS.Index.GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid tree = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Author = TestSig(),
            Committer = TestSig(),
            Message = $"add {fileName}\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    // ── ASCII-only icase prefix compare ────────────────────────────────

    [Fact]
    public void ComparePrefixIgnoreCase_NonAsciiBytes_DoNotFold()
    {
        // 0xC0 ('À') vs 0xE0 ('à') — C's ASCII-only git__tolower keeps them
        // distinct.
        var str = GitPath.FromUtf8Bytes(new byte[] { 0xE0 });
        var prefix = GitPath.FromUtf8Bytes(new byte[] { 0xC0 });
        Assert.NotEqual(0, GitPath.ComparePrefixIgnoreCase(str, prefix));
        Assert.Equal(0, GitPath.ComparePrefixIgnoreCase(GitPath.FromUtf8Bytes(new byte[] { 0xE0 }), GitPath.FromUtf8Bytes(new byte[] { 0xE0 })));
    }

    // ── XDG global ignore fallback ─────────────────────────────────────

    [Fact]
    public async Task GlobalExcludes_XdgFallback_Applied()
    {
        // $XDG_CONFIG_HOME/git/ignore (or ~/.config/git/ignore) is the global
        // excludes source when core.excludesfile is unset (attrcache.c:424-432).
        string home = Path.Combine(_tempDir, "home_" + Guid.NewGuid().ToString("N")[..8]);
        string xdgDir = Path.Combine(home, ".config", "git");
        Directory.CreateDirectory(xdgDir);
        await File.WriteAllTextAsync(Path.Combine(xdgDir, "ignore"), "*.log\n", cancellationToken: TestContext.Current.CancellationToken);
        _cleanupDirs.Add(home);

        GitContext ctx = NewContext();
        ctx.Env["HOME"] = home;
        ctx.Dirs.Reset();

        await using GitRepository repo = await GitRepository.InitAsync(NewDir(), isBare: false, ctx, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "app.log"), "x\n", cancellationToken: TestContext.Current.CancellationToken);

        using GitStatusList list = await repo.StatusNewAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitStatusEntry entry = Assert.Single(list.Entries);
        Assert.Equal(GitStatusFlags.Ignored, entry.Status);
    }

    // ── status refreshes the index from disk ───────────────────────────

    [Fact]
    public async Task Status_RefreshesIndexFromDisk()
    {
        await using GitRepository repo = await GitRepository.InitAsync(NewDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);
        await WriteCommit(repo, "a.txt", "a\n");

        // Load the cached index, then modify the index FILE externally via a
        // SECOND repository instance pointing at the same directory.
        _ = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await using (GitRepository writer = await GitRepository.OpenAsync(repo.Workdir!, NewContext(), TestContext.Current.CancellationToken))
        {
            LibGit2CS.Index.GitIndex widx = await writer.GetIndexAsync(TestContext.Current.CancellationToken);
            widx.Add(new LibGit2CS.Index.GitIndexEntry("new.txt", await writer.ObjectWriteAsync(GitObjectType.Blob, "b\n"u8.ToArray(), TestContext.Current.CancellationToken), GitFileMode.Regular));
            await widx.WriteAsync(TestContext.Current.CancellationToken);
        }

        // C (status.c:296-299): git_status_list_new re-reads the index unless
        // NO_REFRESH — the externally-added entry must be visible.
        using GitStatusList list = await repo.StatusNewAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitStatusEntry? entry = list.Entries.FirstOrDefault(e => e.Path.ToUtf8String() == "new.txt");
        Assert.NotNull(entry);
        // In the index, absent on disk → IndexNew (+ WorkdirDeleted).
        Assert.True((entry!.Status & GitStatusFlags.IndexNew) != 0);
    }

    // ── unmatched gitlink in the workdir ───────────────────────────────

    [Fact]
    public async Task Status_UnmatchedConfiguredSubmodule_Untracked()
    {
        await using GitRepository repo = await GitRepository.InitAsync(NewDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);

        // Create the submodule repo inside the parent workdir.
        string subDir = Path.Combine(repo.Workdir!, "sub");
        Directory.CreateDirectory(subDir);
        await using GitRepository subRepo = await GitRepository.InitAsync(subDir, isBare: false, NewContext(), TestContext.Current.CancellationToken);
        await WriteCommit(subRepo, "s.txt", "s\n");
        GitOid subHead = (await subRepo.ReferenceResolveAsync("refs/heads/master", TestContext.Current.CancellationToken) as GitDirectReference)!.Target;

        // .gitmodules + gitlink committed to HEAD, then the gitlink removed
        // from the INDEX — the workdir submodule dir remains as an unmatched
        // new item.
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitmodules"), "[submodule \"sub\"]\n\tpath = sub\n\turl = https://example.com/sub.git\n", cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new LibGit2CS.Index.GitIndexEntry("sub", subHead, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        await WriteCommit(repo, ".gitmodules", "[submodule \"sub\"]\n\tpath = sub\n\turl = https://example.com/sub.git\n");

        idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.RemoveByPath("sub");
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        // C (diff_generate.c:1155-1171): a CONFIGURED submodule with no index
        // entry is UNTRACKED (WT_NEW) — a record IS created (the h2i side
        // yields the IndexDeleted delta in a separate entry).
        using GitStatusList list = await repo.StatusNewAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitStatusEntry? entry = list.Entries.FirstOrDefault(e => e.Path.ToUtf8String() == "sub/");
        Assert.NotNull(entry);
        Assert.True((entry!.Status & GitStatusFlags.WorkdirNew) != 0, "status: " + entry.Status);
    }
}
