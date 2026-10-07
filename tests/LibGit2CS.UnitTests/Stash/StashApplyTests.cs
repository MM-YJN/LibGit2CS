using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Stash;

namespace LibGit2CS.UnitTests.Stash;

public sealed class StashApplyTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public StashApplyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StashApplyTests_" + Guid.NewGuid().ToString("N")[..8]);
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
        GitIndex idx = await _repo.GetIndexAsync();
        await idx.AddByPathAsync(fileName);
        await idx.WriteAsync();
        GitOid treeOid = await idx.WriteTreeAsync();

        // C (commit.c:113-117): a commit created with update_ref must have
        // the current tip as its first parent — chain onto it when no
        // explicit parent was given.
        List<GitOid> parents = parent is not null ? [parent.Value] : [];
        if (parents.Count == 0 &&
            await _repo.ReferenceResolveAsync("refs/heads/master", TestContext.Current.CancellationToken) is GitDirectReference tipRef)
        {
            parents.Add(tipRef.Target);
        }

        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
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

    // ── Default apply ───────────────────────────────────────────────────

    [Fact]
    public async Task Apply_Default_RestoresWorkdirChanges()
    {
        await WriteCommitWithFile("file.txt", "hello\n");
        ModifyWorkdirFile("file.txt", "modified\n");

        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        // Workdir is reset; now apply.
        await _repo.StashApplyAsync(0, cancellationToken: TestContext.Current.CancellationToken);

        // Workdir should have the stashed content.
        string content = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("modified\n", content);
    }

    // ── Pop = apply + drop ──────────────────────────────────────────────

    [Fact]
    public async Task Pop_AppliesAndDropsStash()
    {
        await WriteCommitWithFile("file.txt", "hello\n");
        ModifyWorkdirFile("file.txt", "modified\n");

        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        await _repo.StashPopAsync(0, cancellationToken: TestContext.Current.CancellationToken);

        // Workdir should have the stashed content.
        string content = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("modified\n", content);

        // Stash should be gone.
        List<GitStashEntry> entries = await ToListAsync(_repo.StashForEachAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(entries);
    }

    // ── Apply with untracked files ──────────────────────────────────────

    [Fact]
    public async Task Apply_WithUntracked_RestoresUntrackedFiles()
    {
        await WriteCommitWithFile("file.txt", "hello\n");
        ModifyWorkdirFile("file.txt", "modified\n");
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "untracked.txt"), "untracked\n", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.IncludeUntracked, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(_repo.Workdir!, "untracked.txt")));

        await _repo.StashApplyAsync(0, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_repo.Workdir!, "untracked.txt")));
        string untrackedContent = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "untracked.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("untracked\n", untrackedContent);
    }

    // ── Apply fails with uncommitted index changes ──────────────────────

    [Fact]
    public async Task Apply_WithUncommittedIndexChanges_Throws()
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await WriteCommitWithFile("file.txt", "hello\n");
        ModifyWorkdirFile("file.txt", "modified\n");
        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        // Stage a new change.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "other.txt"), "other\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("other.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () => await _repo.StashApplyAsync(0, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Apply on non-existent stash ─────────────────────────────────────

    [Fact]
    public async Task Apply_NoStash_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<GitException>(async () => await _repo.StashApplyAsync(0, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Multiple stashes ───────────────────────────────────────────────

    [Fact]
    public async Task Apply_MultipleStashes_AppliesCorrectIndex()
    {
        await WriteCommitWithFile("file.txt", "hello\n");

        // Stash 0: "first"
        ModifyWorkdirFile("file.txt", "first\n");
        await _repo.StashSaveAsync(TestSig(), "first", GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        // Stash 1: "second"
        ModifyWorkdirFile("file.txt", "second\n");
        await _repo.StashSaveAsync(TestSig(), "second", GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        List<GitStashEntry> entries = await ToListAsync(_repo.StashForEachAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, entries.Count);

        // Apply stash 1 (first — the older stash).
        await _repo.StashApplyAsync(1, cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("first\n", content);
    }

    // ── Apply with progress callback ────────────────────────────────────

    [Fact]
    public async Task Apply_WithProgressCallback_ReportsProgress()
    {
        await WriteCommitWithFile("file.txt", "hello\n");
        ModifyWorkdirFile("file.txt", "modified\n");
        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        var progressValues = new List<GitStashApplyProgress>();
        var progress = new SynchronousProgress<GitStashApplyProgress>(progressValues.Add);
        await _repo.StashApplyAsync(0, new GitStashApplyOptions
        {
            Progress = progress,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(progressValues);
        Assert.Contains(GitStashApplyProgress.LoadingStash, progressValues);
        Assert.Contains(GitStashApplyProgress.Done, progressValues);
    }

    // ── Apply with merge conflict from divergent commit ────────────────
    //
    // Ports test_stash_apply__conflict_commit_with_default
    // (tests/libgit2/stash/apply.c:246-264). A divergent commit to the same
    // line as the stashed change produces a modify/modify conflict. The apply
    // must succeed (not throw), write conflict stages 1/2/3 into the repo
    // index, and leave conflict markers in the workdir file.

    [Fact]
    public async Task Apply_Conflict_Commit_WritesConflictEntries()
    {
        await WriteCommitWithFile("file.txt", "line1\n");

        // Stash a modification.
        ModifyWorkdirFile("file.txt", "stash-mod\n");
        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        // Divergent commit to the same line.
        await WriteCommitWithFile("file.txt", "divergent\n");

        // Apply must succeed — conflict entries are written, not thrown.
        await _repo.StashApplyAsync(0, cancellationToken: TestContext.Current.CancellationToken);

        // The repo index must hold stages 1/2/3 for the conflicted file.
        GitIndex repoIdx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(repoIdx.HasConflicts);

        (GitIndexEntry? ancestor, GitIndexEntry? ours, GitIndexEntry? theirs) = repoIdx.ConflictGet("file.txt");
        Assert.NotNull(ancestor);
        Assert.NotNull(ours);
        Assert.NotNull(theirs);

        // Verify each stage blob resolves to the expected content.
        Assert.Equal("line1\n", await ReadBlobAsync(ancestor!.Value.Id));
        Assert.Equal("divergent\n", await ReadBlobAsync(ours!.Value.Id));
        Assert.Equal("stash-mod\n", await ReadBlobAsync(theirs!.Value.Id));

        // Workdir must contain conflict markers.
        string wdContent = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("<<<<<<<", wdContent);
        Assert.Contains("=======", wdContent);
        Assert.Contains(">>>>>>>", wdContent);
        Assert.Contains("divergent", wdContent);
        Assert.Contains("stash-mod", wdContent);
    }

    // ── Apply with merge conflict: non-conflicting files still applied ──
    //
    // Ports test_stash_apply__conflict_index_with_default
    // (tests/libgit2/stash/apply.c:138-157). When the stash contains changes
    // to multiple files and only one conflicts, the non-conflicting changes
    // must still be applied (cleanly staged in the index or restored in the
    // workdir).

    [Fact]
    public async Task Apply_Conflict_OneFile_NonConflictingFileStillApplied()
    {
        // Initial commit with two files.
        await WriteCommitWithFile("conflict.txt", "base\n");
        await WriteCommitWithFile("clean.txt", "base\n");

        // Stash modifications to both files.
        ModifyWorkdirFile("conflict.txt", "stash-change\n");
        ModifyWorkdirFile("clean.txt", "stash-change\n");
        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        // Divergent commit to conflict.txt only.
        await WriteCommitWithFile("conflict.txt", "divergent\n");

        // Apply — conflict.txt conflicts, clean.txt should still be applied.
        await _repo.StashApplyAsync(0, cancellationToken: TestContext.Current.CancellationToken);

        // conflict.txt is conflicted.
        GitIndex repoIdx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(repoIdx.HasConflicts);
        Assert.True(repoIdx.ConflictGet("conflict.txt") != (null, null, null));

        // clean.txt was applied cleanly to the workdir.
        string cleanContent = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "clean.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("stash-change\n", cleanContent);
    }

    // ── Apply with workdir conflict: dirty workdir → throws ─────────────
    //
    // Ports test_stash_apply__conflict_workdir_with_default
    // (tests/libgit2/stash/apply.c:214-226). A workdir modification to a file
    // the stash also modifies causes a workdir-level conflict (not a merge
    // conflict). The apply must throw GitErrorCode.Conflict and leave the
    // index clean (no conflict entries).

    [Fact]
    public async Task Apply_Conflict_Workdir_ThrowsAndLeavesIndexClean()
    {
        await WriteCommitWithFile("file.txt", "hello\n");
        ModifyWorkdirFile("file.txt", "stashed\n");
        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        // Dirty the workdir with a different change.
        ModifyWorkdirFile("file.txt", "dirty\n");

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await _repo.StashApplyAsync(0, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Conflict, ex.Code);

        // Index must be clean (no conflict entries from the aborted apply).
        GitIndex repoIdx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(repoIdx.HasConflicts);

        // Workdir file is untouched (still the dirty version).
        string wdContent = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("dirty\n", wdContent);
    }

    // ── Apply with untracked conflict: blocking untracked file → throws ─
    //
    // Ports test_stash_apply__conflict_untracked_with_default
    // (tests/libgit2/stash/apply.c:180-194). When the stash includes an
    // untracked file (INCLUDE_UNTRACKED) and an untracked file with the same
    // name exists in the workdir, the checkout cannot create it → throws
    // GitErrorCode.Conflict. The index must stay clean.

    [Fact]
    public async Task Apply_Conflict_Untracked_ThrowsAndLeavesIndexClean()
    {
        await WriteCommitWithFile("file.txt", "hello\n");
        ModifyWorkdirFile("file.txt", "stashed\n");
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "untracked.txt"), "stashed-untracked\n", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.IncludeUntracked, cancellationToken: TestContext.Current.CancellationToken);

        // Create a blocking untracked file with the same name.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "untracked.txt"), "blocking\n", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await _repo.StashApplyAsync(0, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Conflict, ex.Code);

        // Index must be clean.
        GitIndex repoIdx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(repoIdx.HasConflicts);

        // The blocking file is untouched.
        string content = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "untracked.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("blocking\n", content);
    }

    // ── Conflict entry stage numbering is correct ───────────────────────
    //
    // Verifies that the three conflict stages have the correct stage numbers
    // (1 = ancestor, 2 = ours, 3 = theirs) in the index entry list, matching
    // the git index format.

    [Fact]
    public async Task Apply_Conflict_StageNumbers_AreCorrect()
    {
        await WriteCommitWithFile("f", "base\n");

        ModifyWorkdirFile("f", "theirs\n");
        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        await WriteCommitWithFile("f", "ours\n");

        await _repo.StashApplyAsync(0, cancellationToken: TestContext.Current.CancellationToken);

        GitIndex repoIdx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(repoIdx.HasConflicts);

        // Collect all non-zero-stage entries for "f".
        Dictionary<int, GitOid> stages = [];
        foreach (GitIndexEntry e in repoIdx.Entries)
        {
            if (e.Path.ToUtf8String() == "f" && e.Stage > 0)
            {
                stages[e.Stage] = e.Id;
            }
        }

        Assert.Equal(3, stages.Count);
        Assert.True(stages.ContainsKey(1)); // ancestor
        Assert.True(stages.ContainsKey(2)); // ours
        Assert.True(stages.ContainsKey(3)); // theirs

        // Stage 1 = ancestor (base version).
        Assert.Equal("base\n", await ReadBlobAsync(stages[1]));
        // Stage 2 = ours (HEAD's divergent version).
        Assert.Equal("ours\n", await ReadBlobAsync(stages[2]));
        // Stage 3 = theirs (stashed version).
        Assert.Equal("theirs\n", await ReadBlobAsync(stages[3]));
    }

    // ── Conflict markers use stash labels ───────────────────────────────
    //
    // The default stash apply checkout options set the conflict labels to
    // "Updated upstream" (ours) and "Stashed changes" (theirs). The workdir
    // conflict markers must use these labels.

    [Fact]
    public async Task Apply_Conflict_MarkersUseStashLabels()
    {
        await WriteCommitWithFile("f", "base\n");

        ModifyWorkdirFile("f", "stashed\n");
        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, cancellationToken: TestContext.Current.CancellationToken);

        await WriteCommitWithFile("f", "upstream\n");

        await _repo.StashApplyAsync(0, cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "f"), cancellationToken: TestContext.Current.CancellationToken);

        // The default labels set by StashApplyAsync.
        Assert.Contains("<<<<<<< Updated upstream", content);
        Assert.Contains(">>>>>>> Stashed changes", content);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the content of a blob identified by <paramref name="oid"/> as a
    /// UTF-8 string.
    /// </summary>
    private async Task<string> ReadBlobAsync(GitOid oid)
    {
        GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(oid, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        return System.Text.Encoding.UTF8.GetString(blob!.Content.ToArray());
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;
        internal SynchronousProgress(Action<T> callback) => _callback = callback;
        void IProgress<T>.Report(T value) => _callback(value);
    }
}
