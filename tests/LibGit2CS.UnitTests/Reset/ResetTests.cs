using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;
using GitResetMode = LibGit2CS.Reset.GitResetMode;

namespace LibGit2CS.UnitTests.Reset;

public sealed class ResetTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public ResetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ResetTests_" + Guid.NewGuid().ToString("N")[..8]);
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
        catch (IOException)
        {
        }
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
        GitSignature sig = TestSig();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is not null ? [parent.Value] : [],
            Author = sig,
            Committer = sig,
            Message = $"add {fileName}\n",
            UpdateRef = "refs/heads/master",
        });
    }

    private static async Task<GitDirectReference?> ResolveHeadCommitAsync(GitRepository repo)
    {
        GitReference? head = await repo.ReferenceResolveAsync("HEAD");
        if (head is GitDirectReference direct)
        {
            return direct;
        }

        if (head is GitSymbolicReference sym)
        {
            return await sym.TargetAsync() as GitDirectReference;
        }

        return null;
    }

    // ── Soft reset ──────────────────────────────────────────────────────

    [Fact]
    public async Task Soft_MovesHead_KeepsIndexAndWorkdir()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid commit1 = await WriteCommitWithFile("file1.txt", "content1\n");
        await WriteCommitWithFile("file2.txt", "content2\n", commit1);

        // Reset soft to commit1.
        Commit c1 = (await _repo.ObjectLookupAsync<Commit>(commit1, TestContext.Current.CancellationToken))!;
        await _repo.ResetAsync(c1, GitResetMode.Soft, cancellationToken: TestContext.Current.CancellationToken);

        // HEAD should point to commit1 via master.
        GitDirectReference? headCommit = await ResolveHeadCommitAsync(_repo);
        Assert.Equal(commit1, headCommit!.Target);

        // Index should still have both files (unchanged).
        Assert.NotNull(idx.EntryByPath("file1.txt"));
        Assert.NotNull(idx.EntryByPath("file2.txt"));

        // Workdir should still have both files.
        Assert.True(File.Exists(Path.Combine(_repo.Workdir!, "file1.txt")));
        Assert.True(File.Exists(Path.Combine(_repo.Workdir!, "file2.txt")));
    }

    [Fact]
    public async Task Soft_WithConflicts_Throws()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid commit1 = await WriteCommitWithFile("file1.txt", "content1\n");

        // Create a conflict in the index.
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ours\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid theirOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "theirs\n"u8.ToArray(), TestContext.Current.CancellationToken);
        idx.ConflictAdd(
            null,
            new GitIndexEntry("conflict.txt", blobOid, GitFileMode.Regular).WithStage(2),
            new GitIndexEntry("conflict.txt", theirOid, GitFileMode.Regular).WithStage(3));

        Commit c = (await _repo.ObjectLookupAsync<Commit>(commit1, TestContext.Current.CancellationToken))!;
        await Assert.ThrowsAsync<GitException>(async () => await _repo.ResetAsync(c, GitResetMode.Soft, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Mixed reset ─────────────────────────────────────────────────────

    [Fact]
    public async Task Mixed_MovesHeadAndReplacesIndex()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid commit1 = await WriteCommitWithFile("file1.txt", "content1\n");
        await WriteCommitWithFile("file2.txt", "content2\n", commit1);

        // Reset mixed to commit1.
        Commit c1 = (await _repo.ObjectLookupAsync<Commit>(commit1, TestContext.Current.CancellationToken))!;
        await _repo.ResetAsync(c1, GitResetMode.Mixed, cancellationToken: TestContext.Current.CancellationToken);

        // HEAD should point to commit1.
        GitDirectReference? headCommit = await ResolveHeadCommitAsync(_repo);
        Assert.Equal(commit1, headCommit!.Target);

        // Index should only have file1 (matching commit1's tree).
        Assert.NotNull(idx.EntryByPath("file1.txt"));
        Assert.Null(idx.EntryByPath("file2.txt"));

        // Workdir should still have both files (mixed doesn't touch workdir).
        Assert.True(File.Exists(Path.Combine(_repo.Workdir!, "file1.txt")));
        Assert.True(File.Exists(Path.Combine(_repo.Workdir!, "file2.txt")));
    }

    // ── Hard reset ──────────────────────────────────────────────────────

    [Fact]
    public async Task Hard_MovesHeadReplacesIndexAndWorkdir()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid commit1 = await WriteCommitWithFile("file1.txt", "content1\n");
        await WriteCommitWithFile("file2.txt", "content2\n", commit1);

        // Reset hard to commit1.
        Commit c1 = (await _repo.ObjectLookupAsync<Commit>(commit1, TestContext.Current.CancellationToken))!;
        await _repo.ResetAsync(c1, GitResetMode.Hard, cancellationToken: TestContext.Current.CancellationToken);

        // HEAD should point to commit1.
        GitDirectReference? headCommit = await ResolveHeadCommitAsync(_repo);
        Assert.Equal(commit1, headCommit!.Target);

        // Index should only have file1.
        Assert.NotNull(idx.EntryByPath("file1.txt"));
        Assert.Null(idx.EntryByPath("file2.txt"));

        // Workdir should only have file1.
        Assert.True(File.Exists(Path.Combine(_repo.Workdir!, "file1.txt")));
        Assert.False(File.Exists(Path.Combine(_repo.Workdir!, "file2.txt")));
    }

    [Fact]
    public async Task Hard_RevertsModifiedFile()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid commit1 = await WriteCommitWithFile("file1.txt", "original\n");

        // Modify the file in workdir and stage it.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file1.txt"), "modified\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file1.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Hard reset to commit1.
        Commit c1 = (await _repo.ObjectLookupAsync<Commit>(commit1, TestContext.Current.CancellationToken))!;
        await _repo.ResetAsync(c1, GitResetMode.Hard, cancellationToken: TestContext.Current.CancellationToken);

        // File should be back to original.
        Assert.Equal("original\n", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file1.txt"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Hard_BareRepo_Throws()
    {
        string bareDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ResetTests_bare_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await using GitRepository bare = await GitRepository.InitAsync(bareDir, isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
            GitOid blobOid = await bare.ObjectWriteAsync(GitObjectType.Blob, "data\n"u8.ToArray(), TestContext.Current.CancellationToken);
            using GitTreeBuilder bld = bare.NewTreeBuilder();
            await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
            GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
            GitSignature sig = TestSig();
            GitOid commitOid = await bare.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Author = sig,
                Committer = sig,
                Message = "init\n",
                UpdateRef = "refs/heads/master",
            }, cancellationToken: TestContext.Current.CancellationToken);

            Commit commit = (await bare.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
            await Assert.ThrowsAsync<GitException>(async () => await bare.ResetAsync(commit, GitResetMode.Hard, cancellationToken: TestContext.Current.CancellationToken));
            await bare.DisposeAsync();
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

    // ── Default reset (pathspec) ────────────────────────────────────────

    [Fact]
    public async Task Default_ResetsSpecificFile()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        // Create commit with two files.
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "file1.txt"), "content1\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workdir, "file2.txt"), "content2\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file1.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file2.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitSignature sig = TestSig();
        GitOid commit1 = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "two files\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Modify file1 in the index.
        await File.WriteAllTextAsync(Path.Combine(workdir, "file1.txt"), "modified\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("file1.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Verify it's modified.
        GitIndexEntry? entry = idx.EntryByPath("file1.txt");
        Assert.NotNull(entry);

        // Reset just file1 to the commit.
        Commit c1 = (await _repo.ObjectLookupAsync<Commit>(commit1, TestContext.Current.CancellationToken))!;
        await _repo.ResetDefaultAsync(c1, ["file1.txt"], cancellationToken: TestContext.Current.CancellationToken);

        // Index should have file1 reset to the original blob.
        GitIndexEntry? resetEntry = idx.EntryByPath("file1.txt");
        Assert.NotNull(resetEntry);
        GitTree originalTree = (await _repo.ObjectLookupAsync<GitTree>(
            (await _repo.ObjectLookupAsync<Commit>(commit1, TestContext.Current.CancellationToken))!.Tree,
            TestContext.Current.CancellationToken))!;
        GitTreeEntry? originalEntry = await originalTree.EntryByPathAsync("file1.txt", TestContext.Current.CancellationToken);
        Assert.NotNull(originalEntry);
        Assert.Equal(originalEntry!.Value.Id, resetEntry!.Value.Id);
    }

    [Fact]
    public async Task Default_NullTarget_RemovesFromIndex()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        await WriteCommitWithFile("file1.txt", "content1\n");

        // Add a new file to the index.
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "extra\n"u8.ToArray(), TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("extra.txt", blobOid, GitFileMode.Regular));
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(idx.EntryByPath("extra.txt"));

        // Reset with null target → remove extra.txt from index.
        await _repo.ResetDefaultAsync(null, ["extra.txt"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(idx.EntryByPath("extra.txt"));
    }

    [Fact]
    public async Task Default_StagedNewFile_IsUnstaged()
    {
        // `git reset HEAD -- <newly-staged-file>` must remove the path from the index (C computes the diff with GIT_DIFF_REVERSE so "in index, not in
        // tree" surfaces as DELETED).
        GitOid commit1 = await WriteCommitWithFile("file1.txt", "content1\n");
        await WriteCommitWithFile("file2.txt", "content2\n", commit1);

        // Stage a NEW file (in the index, absent from the tree).
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "extra\n"u8.ToArray(), TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("extra.txt", blobOid, GitFileMode.Regular));
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(idx.EntryByPath("extra.txt"));

        Commit c1 = (await _repo.ObjectLookupAsync<Commit>(commit1, TestContext.Current.CancellationToken))!;
        await _repo.ResetDefaultAsync(c1, ["extra.txt"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(idx.EntryByPath("extra.txt"));
    }

    [Fact]
    public async Task Default_RmCachedFile_IsRestoredFromTree()
    {
        // `git reset HEAD -- <rm-cached-file>` must restore the path from the target tree ("in tree, not in index" surfaces as ADDED under
        // GIT_DIFF_REVERSE).
        GitOid commit1 = await WriteCommitWithFile("file1.txt", "content1\n");
        GitOid commit2 = await WriteCommitWithFile("file2.txt", "content2\n", commit1);

        // Remove file2 from the index only (the target tree still has it).
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        idx.Remove("file2.txt", 0);
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(idx.EntryByPath("file2.txt"));

        Commit c2 = (await _repo.ObjectLookupAsync<Commit>(commit2, TestContext.Current.CancellationToken))!;
        await _repo.ResetDefaultAsync(c2, ["file2.txt"], cancellationToken: TestContext.Current.CancellationToken);

        GitIndexEntry? restored = idx.EntryByPath("file2.txt");
        Assert.NotNull(restored);
        Assert.Equal(GitFileMode.Regular, restored!.Value.Mode);
    }

    // ── FromAnnotated ───────────────────────────────────────────────────

    [Fact]
    public async Task FromAnnotated_Hard_Works()
    {
        GitOid commit1 = await WriteCommitWithFile("file1.txt", "content1\n");
        await WriteCommitWithFile("file2.txt", "content2\n", commit1);

        Commit c1 = (await _repo.ObjectLookupAsync<Commit>(commit1, TestContext.Current.CancellationToken))!;
        var annotated = GitAnnotatedCommit.FromCommit(c1);

        await _repo.ResetFromAnnotatedAsync(annotated, GitResetMode.Hard, cancellationToken: TestContext.Current.CancellationToken);

        // HEAD should point to commit1.
        GitDirectReference? headCommit = await ResolveHeadCommitAsync(_repo);
        Assert.Equal(commit1, headCommit!.Target);
    }

    // ── Detached HEAD ───────────────────────────────────────────────────

    [Fact]
    public async Task Soft_DetachedHead_MovesHead()
    {
        GitOid commit1 = await WriteCommitWithFile("file1.txt", "content1\n");
        await WriteCommitWithFile("file2.txt", "content2\n", commit1);

        // Detach HEAD at commit2.
        await _repo.DetachHeadAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Reset soft to commit1.
        Commit c1 = (await _repo.ObjectLookupAsync<Commit>(commit1, TestContext.Current.CancellationToken))!;
        await _repo.ResetAsync(c1, GitResetMode.Soft, cancellationToken: TestContext.Current.CancellationToken);

        // HEAD (detached) should now point to commit1.
        GitReference? head = await _repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.True(head is GitDirectReference);
        Assert.Equal(commit1, ((GitDirectReference)head).Target);
    }

    // ── Hard reset cleans up merge state ────────────────────────────────

    [Fact]
    public async Task Hard_ClearsMergeState()
    {
        GitOid commit1 = await WriteCommitWithFile("file1.txt", "content1\n");

        // Simulate a merge state by writing MERGE_HEAD.
        string mergeHeadPath = Path.Combine(_repo.Path, "MERGE_HEAD");
        await File.WriteAllTextAsync(mergeHeadPath, $"{commit1}\n", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(mergeHeadPath));

        // Hard reset should clean up merge state.
        Commit c1 = (await _repo.ObjectLookupAsync<Commit>(commit1, TestContext.Current.CancellationToken))!;
        await _repo.ResetAsync(c1, GitResetMode.Hard, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(mergeHeadPath));
    }

    // Regression: a hard reset from a conflicted (mid-merge) index must restore
    // the workdir to the target tree — NOT leave conflict-marker files behind.
    // Before the checkout engine fix, the tree checkout's create-conflicts pass
    // wrote merge markers over the target content because the index conflicts
    // were loaded unconditionally (libgit2 gates marker writing on the target
    // being an index — checkout.c:974-991). See CheckoutContext.LoadUpdateConflictsAsync.
    [Fact]
    public async Task Hard_FromConflictedIndex_RestoresWorkdirWithoutMarkers()
    {
        GitOid commit1 = await WriteCommitWithFile("merge.txt", "base\n");

        // Simulate a mid-merge conflicted index: stage 1/2/3 for "merge.txt".
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid ancestorOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "base\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid oursOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "base\nours\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid theirsOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "base\ntheirs\n"u8.ToArray(), TestContext.Current.CancellationToken);
        idx.ConflictAdd(
            new GitIndexEntry("merge.txt", ancestorOid, GitFileMode.Regular).WithStage(1),
            new GitIndexEntry("merge.txt", oursOid, GitFileMode.Regular).WithStage(2),
            new GitIndexEntry("merge.txt", theirsOid, GitFileMode.Regular).WithStage(3));
        await idx.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Simulate the conflict-marker workdir file a merge would have produced.
        string markerContent = "<<<<<<< ours\nbase\nours\n=======\nbase\ntheirs\n>>>>>>> theirs\n";
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "merge.txt"), markerContent, cancellationToken: TestContext.Current.CancellationToken);

        // MERGE_HEAD present ⇒ repository in merge state.
        string mergeHeadPath = Path.Combine(_repo.Path, "MERGE_HEAD");
        await File.WriteAllTextAsync(mergeHeadPath, $"{commit1}\n", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(idx.HasConflicts);

        // Hard reset to the pre-merge commit (the merge-abort flow).
        Commit c1 = (await _repo.ObjectLookupAsync<Commit>(commit1, TestContext.Current.CancellationToken))!;
        await _repo.ResetAsync(c1, GitResetMode.Hard, cancellationToken: TestContext.Current.CancellationToken);

        // Workdir must hold the target tree content — never the markers.
        string workdirContent = await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "merge.txt"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("base\n", workdirContent);
        Assert.DoesNotContain("<<<<<<<", workdirContent);
        Assert.DoesNotContain(">>>>>>>", workdirContent);

        // Index fully reset and de-conflicted.
        LibGit2CS.Index.GitIndex idxAfter = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(idxAfter.HasConflicts);
        Assert.NotNull(idxAfter.EntryByPath("merge.txt"));

        // Merge state cleared.
        Assert.False(File.Exists(mergeHeadPath));

        // HEAD back at the pre-merge commit.
        GitDirectReference? head = await ResolveHeadCommitAsync(_repo);
        Assert.Equal(commit1, head!.Target);
    }

    // ── No-op reset ─────────────────────────────────────────────────────

    [Fact]
    public async Task Soft_ResetToCurrentHead_IsNoOp()
    {
        await WriteCommitWithFile("file1.txt", "content1\n");

        GitDirectReference? headCommit = await ResolveHeadCommitAsync(_repo);
        Commit currentCommit = (await _repo.ObjectLookupAsync<Commit>(headCommit!.Target, TestContext.Current.CancellationToken))!;

        // Reset to current HEAD — should be a no-op.
        await _repo.ResetAsync(currentCommit, GitResetMode.Soft, cancellationToken: TestContext.Current.CancellationToken);

        GitDirectReference? headAfterCommit = await ResolveHeadCommitAsync(_repo);
        Assert.Equal(headCommit!.Target, headAfterCommit!.Target);
    }

    // ── Mixed reset on bare repo ────────────────────────────────────────

    [Fact]
    public async Task Mixed_BareRepo_Throws()
    {
        string bareDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ResetTests_bare2_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await using GitRepository bare = await GitRepository.InitAsync(bareDir, isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
            GitOid blobOid = await bare.ObjectWriteAsync(GitObjectType.Blob, "data\n"u8.ToArray(), TestContext.Current.CancellationToken);
            using GitTreeBuilder bld = bare.NewTreeBuilder();
            await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
            GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
            GitSignature sig = TestSig();
            GitOid commitOid = await bare.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Author = sig,
                Committer = sig,
                Message = "init\n",
                UpdateRef = "refs/heads/master",
            }, cancellationToken: TestContext.Current.CancellationToken);

            Commit commit = (await bare.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
            await Assert.ThrowsAsync<GitException>(async () => await bare.ResetAsync(commit, GitResetMode.Mixed, cancellationToken: TestContext.Current.CancellationToken));
            await bare.DisposeAsync();
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

    // ── Default reset with no matching paths ────────────────────────────

    [Fact]
    public async Task Default_NoMatchingPaths_IsNoOp()
    {
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid commit1 = await WriteCommitWithFile("file1.txt", "content1\n");

        Commit c1 = (await _repo.ObjectLookupAsync<Commit>(commit1, TestContext.Current.CancellationToken))!;
        // Reset a path that doesn't exist — should be a no-op.
        await _repo.ResetDefaultAsync(c1, ["nonexistent.txt"], cancellationToken: TestContext.Current.CancellationToken);

        // Index should still have file1.
        Assert.NotNull(idx.EntryByPath("file1.txt"));
    }
}
