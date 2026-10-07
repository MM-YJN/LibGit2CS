using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Checkout;

/// <summary>
/// Regression tests for the merge and checkout parity behaviors in
/// libgit2 1.9.4. Expectations are C-verified by probe where noted.
/// </summary>
public sealed class CheckoutMedParityTests2 : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;
    private string Workdir => _repo.Workdir!;

    public CheckoutMedParityTests2()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CheckoutMed2_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitOid> CommitFileAsync(string name, string content, string refName = "refs/heads/master")
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        string full = Path.Combine(Workdir, name);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(full, content, cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(name, cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);

        List<GitOid> parents = [];
        if (await _repo.ReferenceResolveAsync(refName, TestContext.Current.CancellationToken) is GitDirectReference tipRef)
        {
            parents.Add(tipRef.Target);
        }

        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "commit " + name + "\n",
            UpdateRef = refName,
        }, TestContext.Current.CancellationToken);
    }

    private async Task<GitTree> CommitTreeAsync(GitOid commitId)
    {
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(commitId, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(commit!.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        return tree!;
    }

    private async Task WriteConflictIndexAsync(string path, byte[] ancestor, byte[] ours, byte[] theirs)
    {
        GitOid ancestorOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, ancestor, TestContext.Current.CancellationToken);
        GitOid oursOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, ours, TestContext.Current.CancellationToken);
        GitOid theirsOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, theirs, TestContext.Current.CancellationToken);
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.ConflictAdd(
            new GitIndexEntry(path, ancestorOid, GitFileMode.Regular),
            new GitIndexEntry(path, oursOid, GitFileMode.Regular),
            new GitIndexEntry(path, theirsOid, GitFileMode.Regular));
        await idx.WriteAsync(TestContext.Current.CancellationToken);
    }

    // ── checkout_action_with_wd_blocker / checkout_action_with_wd_dir ──

    /// <summary>
    /// (case 3, blocker): a workdir FILE that path-prefixes the delta
    /// ("a/b" blocks "a/b/c"). C's checkout_action_with_wd_blocker
    /// (checkout.c:564-598) maps MODIFIED to
    /// CHECKOUT_ACTION_IF(FORCE, REMOVE_AND_UPDATE, CONFLICT). SAFE → -13
    /// "1 conflict prevents checkout". C-verified (probe, 1.9.4).
    /// </summary>
    [Fact]
    public async Task BlockerFile_Safe_Conflict()
    {
        GitOid v1 = await CommitFileAsync("a/b/c", "v1\n");
        await CommitFileAsync("a/b/c", "v2\n");
        GitTree target = await CommitTreeAsync(v1); // baseline (HEAD=v2) -> v1 = MODIFIED

        // Replace the workdir "a/b/c" file with a blocker FILE at "a/b".
        File.Delete(Path.Combine(Workdir, "a", "b", "c"));
        Directory.Delete(Path.Combine(Workdir, "a", "b"));
        Directory.CreateDirectory(Path.Combine(Workdir, "a"));
        await File.WriteAllTextAsync(Path.Combine(Workdir, "a", "b"), "BLOCK\n", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.CheckoutTreeAsync(target, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Conflict, ex.Code);
        Assert.Equal("1 conflict prevents checkout", ex.Message);
        Assert.Equal("BLOCK\n", await File.ReadAllTextAsync(Path.Combine(Workdir, "a", "b"), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// (case 3, blocker): FORCE → REMOVE_AND_UPDATE. C removes the
    /// blocking file (git_futils_rmdir_r with GIT_RMDIR_REMOVE_BLOCKERS,
    /// futils.c:784-791) and writes the target. C-verified (probe, 1.9.4:
    /// rc=0, a/b/c = "v2", blocker gone).
    /// </summary>
    [Fact]
    public async Task BlockerFile_Force_RemovesBlockerAndWrites()
    {
        GitOid v1 = await CommitFileAsync("a/b/c", "v1\n");
        await CommitFileAsync("a/b/c", "v2\n");
        GitTree target = await CommitTreeAsync(v1); // baseline (HEAD=v2) -> v1 = MODIFIED

        File.Delete(Path.Combine(Workdir, "a", "b", "c"));
        Directory.Delete(Path.Combine(Workdir, "a", "b"));
        Directory.CreateDirectory(Path.Combine(Workdir, "a"));
        await File.WriteAllTextAsync(Path.Combine(Workdir, "a", "b"), "BLOCK\n", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.CheckoutTreeAsync(target, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        Assert.Equal("v1\n", await File.ReadAllTextAsync(Path.Combine(Workdir, "a", "b", "c"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(Workdir, "a", "b")), "blocker file 'a/b' must be removed");
    }

    /// <summary>
    /// (case 5, dir at path): the delta path "top.txt" has a NON-EMPTY
    /// workdir DIRECTORY at the same path. C's checkout_action_with_wd_dir
    /// (checkout.c:600-653) maps ADDED to
    /// CHECKOUT_ACTION_IF(FORCE, REMOVE_AND_UPDATE, CONFLICT). SAFE → -13
    /// "1 conflict prevents checkout". C-verified (probe, 1.9.4: rc=-13).
    /// </summary>
    [Fact]
    public async Task DirAtAddedPath_NonEmpty_Safe_Conflict()
    {
        await CommitFileAsync("base.txt", "base\n");
        GitOid feature = await CommitFileAsync("top.txt", "x\n", "refs/heads/feature");
        GitTree target = await CommitTreeAsync(feature);

        // HEAD stays at master (baseline without top.txt); create a non-empty
        // dir at the ADDED path (the feature commit left a workdir FILE there).
        File.Delete(Path.Combine(Workdir, "top.txt"));
        Directory.CreateDirectory(Path.Combine(Workdir, "top.txt"));
        await File.WriteAllTextAsync(Path.Combine(Workdir, "top.txt", "inner"), "y\n", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.CheckoutTreeAsync(target, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Conflict, ex.Code);
        Assert.Equal("1 conflict prevents checkout", ex.Message);
        Assert.True(Directory.Exists(Path.Combine(Workdir, "top.txt")));
    }

    /// <summary>
    /// (case 5, empty dir): an EMPTY workdir directory at an ADDED path is
    /// always safe to remove — C's checkout_action_with_wd_dir_empty
    /// (checkout.c:655-667) runs the no_wd action plus REMOVE, so the SAFE
    /// checkout succeeds and writes the file. C-verified (probe, 1.9.4:
    /// rc=0, top.txt = "x").
    /// </summary>
    [Fact]
    public async Task DirAtAddedPath_Empty_Safe_WritesFile()
    {
        await CommitFileAsync("base.txt", "base\n");
        GitOid feature = await CommitFileAsync("top.txt", "x\n", "refs/heads/feature");
        GitTree target = await CommitTreeAsync(feature);

        File.Delete(Path.Combine(Workdir, "top.txt"));
        Directory.CreateDirectory(Path.Combine(Workdir, "top.txt"));

        await _repo.CheckoutTreeAsync(target, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(Workdir, "top.txt")));
        Assert.Equal("x\n", await File.ReadAllTextAsync(Path.Combine(Workdir, "top.txt"), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// (case 5, dir at path): FORCE → REMOVE_AND_UPDATE removes the
    /// non-empty directory recursively and writes the file. C-verified
    /// (checkout.c:600-628 with CHECKOUT_ACTION_IF(FORCE, REMOVE_AND_UPDATE,
    /// CONFLICT); rmdir_r removes the tree with GIT_RMDIR_REMOVE_FILES).
    /// </summary>
    [Fact]
    public async Task DirAtAddedPath_Force_WritesFile()
    {
        await CommitFileAsync("base.txt", "base\n");
        GitOid feature = await CommitFileAsync("top.txt", "x\n", "refs/heads/feature");
        GitTree target = await CommitTreeAsync(feature);

        File.Delete(Path.Combine(Workdir, "top.txt"));
        Directory.CreateDirectory(Path.Combine(Workdir, "top.txt"));
        await File.WriteAllTextAsync(Path.Combine(Workdir, "top.txt", "inner"), "y\n", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.CheckoutTreeAsync(target, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(Workdir, "top.txt")));
        Assert.Equal("x\n", await File.ReadAllTextAsync(Path.Combine(Workdir, "top.txt"), TestContext.Current.CancellationToken));
    }

    // ── ADDED-with-wd ignored-file check (DONT_OVERWRITE_IGNORED) ─────

    /// <summary>
    /// an IGNORED workdir file at an ADDED path. C (checkout.c:511-516):
    /// git_iterator_current_is_ignored → CHECKOUT_ACTION_IF(DONT_OVERWRITE_IGNORED,
    /// CONFLICT, UPDATE_BLOB). Default SAFE silently overwrites the ignored
    /// file. C-verified (probe, 1.9.4: rc=0, ignored.txt = "target").
    /// </summary>
    [Fact]
    public async Task IgnoredFileAtAddedPath_Safe_Overwrites()
    {
        await CommitFileAsync("base.txt", "base\n");
        GitOid feature = await CommitFileAsync("ignored.txt", "target\n", "refs/heads/feature");
        GitTree target = await CommitTreeAsync(feature);

        await File.WriteAllTextAsync(Path.Combine(Workdir, ".gitignore"), "ignored.txt\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(Workdir, "ignored.txt"), "local\n", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.CheckoutTreeAsync(target, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("target\n", await File.ReadAllTextAsync(Path.Combine(Workdir, "ignored.txt"), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// with DONT_OVERWRITE_IGNORED the ignored file at an ADDED path is a
    /// CONFLICT. C-verified (probe, 1.9.4: rc=-13 "1 conflict prevents
    /// checkout").
    /// </summary>
    [Fact]
    public async Task IgnoredFileAtAddedPath_DontOverwriteIgnored_Conflict()
    {
        await CommitFileAsync("base.txt", "base\n");
        GitOid feature = await CommitFileAsync("ignored.txt", "target\n", "refs/heads/feature");
        GitTree target = await CommitTreeAsync(feature);

        await File.WriteAllTextAsync(Path.Combine(Workdir, ".gitignore"), "ignored.txt\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(Workdir, "ignored.txt"), "local\n", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.CheckoutTreeAsync(target, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.DontOverwriteIgnored }, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Conflict, ex.Code);
        Assert.Equal("local\n", await File.ReadAllTextAsync(Path.Combine(Workdir, "ignored.txt"), TestContext.Current.CancellationToken));
    }

    // ── conflict binary detection (ancestor→ours→theirs) ──────────────

    /// <summary>
    /// a conflict whose THEIRS side is binary (ours and ancestor text).
    /// C's checkout_conflict_detect_binary (checkout.c:874-909) flags the
    /// conflict binary when any side is binary (ancestor→ours→theirs order),
    /// and checkout_create_conflicts then writes the OURS side
    /// (checkout.c:2251-2256). Checking only the ours side would fall into
    /// the 3-way merge path and produce garbage/empty output.
    /// </summary>
    [Fact]
    public async Task ConflictBinary_TheirsBinary_WritesOurs()
    {
        await CommitFileAsync("base.txt", "base\n");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        byte[] binary = [0x00, 0x01, 0x02, 0x03, 0xFF, 0x00];
        await WriteConflictIndexAsync(
            "conflict.txt",
            "ancestor\n"u8.ToArray(),
            "ours\n"u8.ToArray(),
            binary);

        await _repo.CheckoutIndexAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.AllowConflicts,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ours\n", await File.ReadAllTextAsync(Path.Combine(Workdir, "conflict.txt"), TestContext.Current.CancellationToken));
    }

    // ── exec-bit handling ─────────────────────────────────────────────

    /// <summary>
    /// with core.filemode=true, a target executable whose workdir copy is
    /// non-executable is removed and rewritten (checkout_action_common,
    /// checkout.c:274-277). The rewritten file carries the exec bit.
    /// </summary>
    [Fact]
    public async Task ExecMismatch_RespectFilemode_RewritesExec()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // no exec bits on Windows
        }

        await CommitFileAsync("f.txt", "one\n");
        // Second commit flips the mode to executable.
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(Workdir, "f.txt"), "one\n", cancellationToken: TestContext.Current.CancellationToken);
        File.SetUnixFileMode(Path.Combine(Workdir, "f.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        await idx.AddByPathAsync("f.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [((GitDirectReference)(await _repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "exec\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        // HEAD is now the exec commit — the checkout target is HEAD, baseline
        // is HEAD → empty diff. Reset the workdir file to non-exec first.
        string fPath = Path.Combine(Workdir, "f.txt");
        File.SetUnixFileMode(fPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await _repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        Assert.True((File.GetUnixFileMode(fPath) & UnixFileMode.UserExecute) != 0,
            "executable target must be written with the exec bit");
    }

    /// <summary>
    /// the exec-mismatch REMOVE is UNCONDITIONAL in C (no
    /// core.filemode gate, checkout.c:274-277), and the file is created with
    /// the target mode via p_open even when core.filemode=false
    /// (blob_content_to_file, checkout.c:1520-1548).
    /// </summary>
    [Fact]
    public async Task ExecMismatch_FilemodeFalse_StillWritesExec()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await _repo.Config.SetBoolAsync("core.filemode", false, TestContext.Current.CancellationToken);

        await CommitFileAsync("f.txt", "one\n");
        await CommitFileAsync("f.txt", "one\n", "refs/heads/feature");

        // feature commit has f.txt executable (chmod before commit).
        string fPath = Path.Combine(Workdir, "f.txt");
        File.SetUnixFileMode(fPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("f.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);

        // Reset the workdir copy to non-exec, then commit the exec mode and
        // checkout with core.filemode=false.
        File.SetUnixFileMode(fPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        GitOid execOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [((GitDirectReference)(await _repo.ReferenceResolveAsync("refs/heads/feature", TestContext.Current.CancellationToken))!).Target],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "exec\n",
            UpdateRef = "refs/heads/feature",
        }, TestContext.Current.CancellationToken);
        GitTree target2 = await CommitTreeAsync(execOid);

        await _repo.CheckoutTreeAsync(target2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True((File.GetUnixFileMode(fPath) & UnixFileMode.UserExecute) != 0,
            "executable target must be written with the exec bit even when core.filemode=false");
    }

    // ── conflicted-file write (result mode, UPDATE_ONLY guard) ────────

    /// <summary>
    /// the merged conflict file is created with result.mode
    /// (git_filebuf_open(..., result.mode), checkout.c:2147-2151) — the
    /// exec bit is preserved (a plain WriteAllBytesAsync would write 0644 and
    /// lose the exec bit).
    /// </summary>
    [Fact]
    public async Task ConflictWrite_PreservesExecMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await CommitFileAsync("base.txt", "base\n");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        GitOid ancestorOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ancestor\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid oursOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "ours\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitOid theirsOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "theirs\n"u8.ToArray(), TestContext.Current.CancellationToken);
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.ConflictAdd(
            new GitIndexEntry("conflict.txt", ancestorOid, GitFileMode.Regular),
            new GitIndexEntry("conflict.txt", oursOid, GitFileMode.Executable),
            new GitIndexEntry("conflict.txt", theirsOid, GitFileMode.Executable));
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        await _repo.CheckoutIndexAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.AllowConflicts,
        }, cancellationToken: TestContext.Current.CancellationToken);

        string conflictPath = Path.Combine(Workdir, "conflict.txt");
        Assert.True((File.GetUnixFileMode(conflictPath) & UnixFileMode.UserExecute) != 0,
            "merged conflict file must be written with the exec bit (result.mode)");
    }

    /// <summary>
    /// an UPDATE_ONLY checkout skips writing a conflict merge result when
    /// the workdir file is absent (checkout_safe_for_update_only,
    /// checkout.c:2126-2128).
    /// </summary>
    [Fact]
    public async Task ConflictWrite_UpdateOnly_SkipsMissingFile()
    {
        await CommitFileAsync("base.txt", "base\n");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        await WriteConflictIndexAsync(
            "conflict.txt",
            "ancestor\n"u8.ToArray(),
            "ours\n"u8.ToArray(),
            "theirs\n"u8.ToArray());

        // No workdir file at conflict.txt.
        await _repo.CheckoutIndexAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.AllowConflicts | GitCheckoutStrategy.UpdateOnly,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(Workdir, "conflict.txt")),
            "UPDATE_ONLY must not create the missing conflict file");
    }

    // ── progress totals ──────────────────────────────────────────────

    /// <summary>
    /// a REMOVE_AND_UPDATE action contributes 2 steps in C
    /// (counts[REMOVE] + counts[UPDATE_BLOB], checkout.c:1356-1364), and the
    /// total includes the conflict passes (checkout.c:2643-2647); counting
    /// each action once would make completed exceed total.
    /// </summary>
    [Fact]
    public async Task ProgressTotals_RemoveAndUpdateCountsTwice()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // exec-bit mismatch (the REMOVE trigger) needs POSIX modes
        }

        await CommitFileAsync("f.txt", "one\n");
        // Second commit makes f.txt executable → MODIFIED delta with an
        // exec-mismatch REMOVE on the non-exec workdir copy.
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        string fPath = Path.Combine(Workdir, "f.txt");
        File.SetUnixFileMode(fPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        await idx.AddByPathAsync("f.txt", cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [((GitDirectReference)(await _repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "exec\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        // Strip the exec bit from the workdir copy.
        File.SetUnixFileMode(fPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var reports = new List<GitCheckoutProgress>();
        await _repo.CheckoutHeadAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.Force,
            Progress = new SynchronousProgress<GitCheckoutProgress>(reports.Add),
        }, TestContext.Current.CancellationToken);

        Assert.NotEmpty(reports);
        GitCheckoutProgress last = reports[^1];
        Assert.Equal(2, last.TotalSteps);
        Assert.Equal(last.TotalSteps, last.CompletedSteps);
        Assert.Equal(0, reports[0].CompletedSteps); // 0 baseline report
        Assert.Equal(2, reports[0].TotalSteps);
    }

    // ── checkout_tree with a conflicted index ────────────────────────

    /// <summary>
    /// git_checkout_tree over a conflicted repo index aborts with
    /// GIT_ECONFLICT "unresolved conflicts exist in the index" unless FORCE
    /// or NO_REFRESH (checkout.c:2424-2434). C-verified (probe, 1.9.4:
    /// rc=-13).
    /// </summary>
    [Fact]
    public async Task CheckoutTree_ConflictedIndex_Safe_Conflict()
    {
        await CommitFileAsync("base.txt", "base\n");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);
        await WriteConflictIndexAsync(
            "conflict.txt",
            "ancestor\n"u8.ToArray(),
            "ours\n"u8.ToArray(),
            "theirs\n"u8.ToArray());

        GitOid headId = ((GitDirectReference)(await _repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target;
        GitTree target = await CommitTreeAsync(headId);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.CheckoutTreeAsync(target, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Conflict, ex.Code);
        Assert.Equal("unresolved conflicts exist in the index", ex.Message);
    }

    /// <summary>
    /// FORCE bypasses the unresolved-conflict abort (checkout.c:2419-2423)
    /// and the tree checkout clears the conflict stages. C-verified (probe,
    /// 1.9.4: rc=0).
    /// </summary>
    [Fact]
    public async Task CheckoutTree_ConflictedIndex_Force_ProceedsAndClears()
    {
        await CommitFileAsync("base.txt", "base\n");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);
        await WriteConflictIndexAsync(
            "conflict.txt",
            "ancestor\n"u8.ToArray(),
            "ours\n"u8.ToArray(),
            "theirs\n"u8.ToArray());

        GitOid headId = ((GitDirectReference)(await _repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target;
        GitTree target = await CommitTreeAsync(headId);

        await _repo.CheckoutTreeAsync(target, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        Assert.False((await _repo.GetIndexAsync(TestContext.Current.CancellationToken)).HasConflicts);
    }

    // ── ThrowIfConflicts only checks ALLOW_CONFLICTS ─────────────────

    /// <summary>
    /// USE_OURS does NOT suppress the GIT_ECONFLICT abort — C only
    /// checks ALLOW_CONFLICTS (checkout.c:1372-1380), so UseOurs/UseTheirs/
    /// SkipUnmerged do not return early and proceed.
    /// </summary>
    [Fact]
    public async Task UseOurs_WithoutAllowConflicts_Conflict()
    {
        await CommitFileAsync("f.txt", "one\n");
        await CommitFileAsync("f.txt", "two\n"); // HEAD = v2
        // Target the FIRST commit's tree: delta MODIFIED v2→v1.
        Commit? firstCommit = await _repo.ObjectLookupAsync<Commit>((await _repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken) is GitDirectReference dr2 ? dr2.Target : default), TestContext.Current.CancellationToken);
        Assert.NotNull(firstCommit);
        Commit? parent = await _repo.ObjectLookupAsync<Commit>(firstCommit!.ParentId(0), TestContext.Current.CancellationToken);
        Assert.NotNull(parent);
        GitTree? target = await _repo.ObjectLookupAsync<GitTree>(parent!.Tree, TestContext.Current.CancellationToken);
        Assert.NotNull(target);

        // Dirty workdir file.
        await File.WriteAllTextAsync(Path.Combine(Workdir, "f.txt"), "dirty\n", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.CheckoutTreeAsync(target, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.UseOurs }, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Conflict, ex.Code);
        Assert.Equal("1 conflict prevents checkout", ex.Message);
    }

    /// <summary>
    /// SKIP_UNMERGED does not suppress the abort either.
    /// </summary>
    [Fact]
    public async Task SkipUnmerged_WithoutAllowConflicts_Conflict()
    {
        await CommitFileAsync("f.txt", "one\n");
        await CommitFileAsync("f.txt", "two\n");
        Commit? headCommit = await _repo.ObjectLookupAsync<Commit>(((GitDirectReference)(await _repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target, TestContext.Current.CancellationToken);
        Commit? parent = await _repo.ObjectLookupAsync<Commit>(headCommit!.ParentId(0), TestContext.Current.CancellationToken);
        GitTree? target = await _repo.ObjectLookupAsync<GitTree>(parent!.Tree, TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(Workdir, "f.txt"), "dirty\n", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.CheckoutTreeAsync(target, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.SkipUnmerged }, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Conflict, ex.Code);
    }

    // ── index entries updated after checkout carry stat data ─────────

    /// <summary>
    /// checkout_update_index fills ctime/mtime/dev/ino/uid/gid/size via
    /// git_index_entry__init_from_stat (index.c:900-916, checkout.c:1642-1647) —
    /// not only FileSize.
    /// </summary>
    [Fact]
    public async Task UpdatedIndexEntry_HasStatData()
    {
        await CommitFileAsync("f.txt", "one\n");
        await CommitFileAsync("f.txt", "two\n"); // HEAD = v2
        Commit? headCommit = await _repo.ObjectLookupAsync<Commit>(((GitDirectReference)(await _repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target, TestContext.Current.CancellationToken);
        Commit? parent = await _repo.ObjectLookupAsync<Commit>(headCommit!.ParentId(0), TestContext.Current.CancellationToken);
        GitTree? target = await _repo.ObjectLookupAsync<GitTree>(parent!.Tree, TestContext.Current.CancellationToken);

        await _repo.CheckoutTreeAsync(target, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        GitIndexEntry? entry = (await _repo.GetIndexAsync(TestContext.Current.CancellationToken)).EntryByPath("f.txt");
        Assert.NotNull(entry);
        Assert.NotEqual(0, entry.Value.Mtime.Seconds);
        Assert.NotEqual(0, entry.Value.Ctime.Seconds);
        Assert.Equal(4u, entry.Value.FileSize); // "one\n"
        if (!OperatingSystem.IsWindows())
        {
            Assert.NotEqual(0u, entry.Value.Ino);
            Assert.NotEqual(0u, entry.Value.Dev);
        }
    }

    // ── merge.conflictstyle config read is strategy-gated ────────────

    /// <summary>
    /// with CONFLICT_STYLE_MERGE in the strategy, the config read is
    /// skipped (checkout.c:2495-2520), so a config value of diff3 does not
    /// override the explicit merge style (reading the config unconditionally
    /// would produce diff3 markers).
    /// </summary>
    [Fact]
    public async Task ConflictStyleStrategy_OverridesConfig()
    {
        await CommitFileAsync("base.txt", "base\n");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);
        await WriteConflictIndexAsync(
            "conflict.txt",
            "ancestor\n"u8.ToArray(),
            "ours\n"u8.ToArray(),
            "theirs\n"u8.ToArray());

        await _repo.Config.SetStringAsync("merge.conflictstyle", "diff3", TestContext.Current.CancellationToken);

        await _repo.CheckoutIndexAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.AllowConflicts | GitCheckoutStrategy.ConflictStyleMerge,
        }, cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(Path.Combine(Workdir, "conflict.txt"), TestContext.Current.CancellationToken);
        Assert.Contains("<<<<<<<", content);
        Assert.DoesNotContain("|||||||", content); // merge style — no ancestor block
    }

    /// <summary>
    /// with no conflict-style strategy bits, the config value diff3
    /// applies (checkout.c:2495-2520).
    /// </summary>
    [Fact]
    public async Task ConfigDiff3_WithoutStrategyBits_UsesDiff3()
    {
        await CommitFileAsync("base.txt", "base\n");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);
        await WriteConflictIndexAsync(
            "conflict.txt",
            "ancestor\n"u8.ToArray(),
            "ours\n"u8.ToArray(),
            "theirs\n"u8.ToArray());

        await _repo.Config.SetStringAsync("merge.conflictstyle", "diff3", TestContext.Current.CancellationToken);

        await _repo.CheckoutIndexAsync(new GitCheckoutOptions
        {
            Strategy = GitCheckoutStrategy.AllowConflicts,
        }, cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(Path.Combine(Workdir, "conflict.txt"), TestContext.Current.CancellationToken);
        Assert.Contains("|||||||", content); // diff3 ancestor block
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;
        internal SynchronousProgress(Action<T> callback) => _callback = callback;
        void IProgress<T>.Report(T value) => _callback(value);
    }
}
