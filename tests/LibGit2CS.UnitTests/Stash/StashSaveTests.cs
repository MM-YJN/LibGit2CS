using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Stash;

namespace LibGit2CS.UnitTests.Stash;

public sealed class StashSaveTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public StashSaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StashSaveTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitOid> WriteCommitWithFile(string fileName, string content, GitOid? parent = null)
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync();
        await idx.AddByPathAsync(fileName);
        await idx.WriteAsync();
        GitOid treeOid = await idx.WriteTreeAsync();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is not null ? [parent.Value] : [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = $"add {fileName}\n",
            UpdateRef = "refs/heads/master",
        });
    }

    private void ModifyWorkdirFile(string fileName, string content)
    {
        File.WriteAllText(Path.Combine(_repo.Workdir!, fileName), content);
    }

    private static async Task<List<GitStashEntry>> ToListAsync(IAsyncEnumerable<GitStashEntry> source)
    {
        var list = new List<GitStashEntry>();
        await foreach (GitStashEntry entry in source)
        {
            list.Add(entry);
        }

        return list;
    }

    // ── Default save ─────────────────────────────────────────────────────

    [Fact]
    public async Task Save_Default_CreatesStashAndResetsWorkdir()
    {
        await WriteCommitWithFile("file.txt", "hello\n");

        // Modify the workdir file.
        ModifyWorkdirFile("file.txt", "hello modified\n");

        GitOid stashOid = await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        // The stash commit should exist.
        Assert.False(stashOid.IsZero);

        // refs/stash should exist.
        GitReference? stashRef = await _repo.ReferenceLookupAsync("refs/stash", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(stashRef);

        // The workdir should be reset to the base.
        string workdirContent = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("hello\n", workdirContent);
    }

    // ── Save with custom message ─────────────────────────────────────────

    [Fact]
    public async Task Save_WithMessage_UsesMessageInReflog()
    {
        await WriteCommitWithFile("file.txt", "hello\n");
        ModifyWorkdirFile("file.txt", "modified\n");

        await _repo.StashSaveAsync(TestSig(), "my custom message", GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        List<GitStashEntry> entries = await ToListAsync(_repo.StashForEachAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(entries);
        Assert.Contains("my custom message", entries[0].Message);
    }

    // ── Save with no changes → error ────────────────────────────────────

    [Fact]
    public async Task Save_NoChanges_ThrowsNotFound()
    {
        await WriteCommitWithFile("file.txt", "hello\n");

        await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Save on bare repo → error ───────────────────────────────────────

    [Fact]
    public async Task Save_BareRepo_Throws()
    {
        string bareDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StashBare_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await using GitRepository bareRepo = await GitRepository.InitAsync(bareDir, isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
            {
                await Assert.ThrowsAsync<GitException>(async () =>
                    await bareRepo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(bareDir, recursive: true);
            }
            catch (IOException) { }
        }
    }

    // ── Save with IncludeUntracked ──────────────────────────────────────

    [Fact]
    public async Task Save_IncludeUntracked_StashesUntrackedFile()
    {
        await WriteCommitWithFile("file.txt", "hello\n");
        ModifyWorkdirFile("file.txt", "modified\n");

        // Create an untracked file.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "untracked.txt"), "untracked\n", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.IncludeUntracked, cancellationToken: TestContext.Current.CancellationToken);

        // The untracked file should be gone after stash.
        Assert.False(File.Exists(Path.Combine(_repo.Workdir!, "untracked.txt")));
    }

    // ── Save with KeepAll ───────────────────────────────────────────────

    [Fact]
    public async Task Save_KeepAll_PreservesWorkdir()
    {
        await WriteCommitWithFile("file.txt", "hello\n");
        ModifyWorkdirFile("file.txt", "modified\n");

        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.KeepAll, cancellationToken: TestContext.Current.CancellationToken);

        // Workdir should still have the modified content.
        string content = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("modified\n", content);
    }

    // ── Save with KeepIndex ─────────────────────────────────────────────

    [Fact]
    public async Task Save_KeepIndex_ResetsToIndexCommit()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await WriteCommitWithFile("file.txt", "hello\n");

        // Stage a modification.
        ModifyWorkdirFile("file.txt", "staged\n");
        await idx.AddByPathAsync("file.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Further modify in workdir.
        ModifyWorkdirFile("file.txt", "workdir\n");

        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.KeepIndex, cancellationToken: TestContext.Current.CancellationToken);

        // Workdir should have the staged content.
        string content = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("staged\n", content);
    }

    // ── ForEach on empty repo ───────────────────────────────────────────

    [Fact]
    public async Task ForEach_EmptyRepo_ReturnsEmpty()
    {
        List<GitStashEntry> entries = await ToListAsync(_repo.StashForEachAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(entries);
    }

    // ── ForEach enumerates stash entries ────────────────────────────────

    [Fact]
    public async Task ForEach_AfterSave_ReturnsEntry()
    {
        await WriteCommitWithFile("file.txt", "hello\n");
        ModifyWorkdirFile("file.txt", "modified\n");

        await _repo.StashSaveAsync(TestSig(), "test stash", GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        List<GitStashEntry> entries = await ToListAsync(_repo.StashForEachAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(entries);
        Assert.Equal(0, entries[0].Index);
    }

    // ── Drop ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Drop_RemovesStashEntry()
    {
        await WriteCommitWithFile("file.txt", "hello\n");
        ModifyWorkdirFile("file.txt", "modified\n");
        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        await _repo.StashDropAsync(0, cancellationToken: TestContext.Current.CancellationToken);

        List<GitStashEntry> entries = await ToListAsync(_repo.StashForEachAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(entries);

        // refs/stash should be removed.
        Assert.Null(await _repo.ReferenceLookupAsync("refs/stash", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Drop_EmptyStash_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<GitException>(async () => await _repo.StashDropAsync(0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Drop_NonExistentIndex_ThrowsNotFound()
    {
        await WriteCommitWithFile("file.txt", "hello\n");
        ModifyWorkdirFile("file.txt", "modified\n");
        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () => await _repo.StashDropAsync(5, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Detached HEAD ───────────────────────────────────────────────────

    [Fact]
    public async Task Save_DetachedHead_Works()
    {
        await WriteCommitWithFile("file.txt", "hello\n");

        // Detach HEAD.
        await _repo.DetachHeadAsync(cancellationToken: TestContext.Current.CancellationToken);

        ModifyWorkdirFile("file.txt", "modified\n");
        GitOid stashOid = await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(stashOid.IsZero);
    }

    // ── Same-second modification (zero-stat regression) ────────────────

    [Fact]
    public async Task Save_SameSecondModification_SameSize_StashesChange()
    {
        // Regression test for the zero-stat problem: modify a file's content
        // WITHOUT changing its size, within the same second as the index
        // write. The stat fields match, so diff-based stash detection alone
        // would report Unmodified and skip the stash. EntryNewerThanIndex
        // forces OID recomputation → Modified → stash succeeds.
        await WriteCommitWithFile("file.txt", "hello\n");

        // Modify with same-size content (both 6 bytes: "hello\n" → "world\n").
        ModifyWorkdirFile("file.txt", "world\n");

        GitOid stashOid = await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        // The stash should succeed (not throw "nothing to stash").
        Assert.False(stashOid.IsZero);

        // The workdir should be reset to the base content.
        string workdirContent = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("hello\n", workdirContent);
    }

    [Fact]
    public async Task Save_SameSecondModification_DifferentSize_StashesChange()
    {
        // Baseline: a different-size modification should always be detected
        // (the FileSize stat check catches it without needing the racy-git
        // check). This confirms the diff-based stash path works for the
        // non-racy case.
        await WriteCommitWithFile("file.txt", "hello\n");

        ModifyWorkdirFile("file.txt", "hello world\n");

        GitOid stashOid = await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(stashOid.IsZero);

        string workdirContent = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("hello\n", workdirContent);
    }
}
