using LibGit2CS.Core;
using LibGit2CS.Notes;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Stash;
using LibGit2CS.Submodule;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Submodule;

// Parity tests for bare-repo submodule lookup code, the stash-save message prefix, ensure_clean_index on unborn HEAD, and note message NUL
// truncation. Expectations C-verified against libgit2 1.9.4 (submodule.c:329-332, stash.c:30-44, 971-992, notes.c:334-338).
public sealed class SubstashLowParityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _cleanupDirs = [];

    public SubstashLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SubstashLow_" + Guid.NewGuid().ToString("N")[..8]);
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
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    // ── bare-repo submodule lookup ────────────────────────────────────

    [Fact]
    public async Task Submodule_BareRepoLookup_ThrowsErrorCode()
    {
        // C (submodule.c:329-332): generic -1 (GIT_ERROR) with "cannot get
        // submodules without a working tree".
        await using GitRepository repo = await GitRepository.InitAsync(NewDir(), isBare: true, NewContext(), TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await GitSubmodule.LookupAsync(repo, "sub", TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("cannot get submodules without a working tree", ex.Message);
    }

    // ── stash-save message prefix ─────────────────────────────────────

    [Fact]
    public async Task StashSave_NoCommits_PrefixedMessage()
    {
        // C (stash.c:30-44, 41): create_error prefixes every save failure
        // with "cannot stash changes - ".
        await using GitRepository repo = await GitRepository.InitAsync(NewDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.UnbornBranch, ex.Code);
        Assert.Equal("cannot stash changes - you do not have the initial commit yet.", ex.Message);
    }

    // ── ensure_clean_index on unborn HEAD ─────────────────────────────

    [Fact]
    public async Task StashApply_UnbornHead_Throws()
    {
        // C (stash.c:971-992): git_repository_head_tree fails on an unborn
        // HEAD and the stash apply errors;
        // clean-index check and proceed.
        await using GitRepository repo = await GitRepository.InitAsync(NewDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);
        _ = await CommitFileAsync(repo, "f.txt", "x\n", "m\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "y\n", cancellationToken: TestContext.Current.CancellationToken);
        await repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        // Unborn HEAD: delete the branch ref and detach HEAD to it.
        await repo.Refs.DeleteAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        await repo.ReferenceCreateSymbolicAsync("HEAD", "refs/heads/master", force: true, logMessage: "unborn", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.StashApplyAsync(0, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.UnbornBranch, ex.Code);
    }

    // ── note message NUL truncation ──────────────────────────────────

    [Fact]
    public async Task Note_MessageWithNul_TruncatesAtNul()
    {
        // C (notes.c:334-338): git__strndup is NUL-terminated — the message
        // ends at the first NUL byte.
        await using GitRepository repo = await GitRepository.InitAsync(NewDir(), isBare: false, NewContext(), TestContext.Current.CancellationToken);
        GitOid c1 = await CommitFileAsync(repo, "f.txt", "x\n", "m\n");

        string msg = "before\u0000after";
        _ = await repo.NotesCreateAsync(null, TestSig(), TestSig(), c1, msg, allowOverwrite: false, TestContext.Current.CancellationToken);

        GitNote? note = await repo.NotesReadAsync(null, c1, TestContext.Current.CancellationToken);
        Assert.NotNull(note);
        Assert.Equal("before", note!.Message);
        note.Dispose();
    }
}
