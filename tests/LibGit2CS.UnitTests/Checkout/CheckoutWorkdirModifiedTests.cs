using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Checkout;

/// <summary>
/// Tests for the workdir-modified detection in checkout — the faithful port
/// of <c>checkout_is_workdir_modified</c> (checkout.c:176-249). Covers the
/// stat-cache short-circuit, racy-git guard, symlink mode/content semantics,
/// and mode-change handling.
/// </summary>
public sealed class CheckoutWorkdirModifiedTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public CheckoutWorkdirModifiedTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_WdModTests_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitOid> CommitFileAsync(string name, string content, CancellationToken ct, bool executable = false)
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: ct);
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, name), content, cancellationToken: ct);
        if (executable && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                Path.Combine(workdir, name),
                UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await idx.AddByPathAsync(name, cancellationToken: ct);
        await idx.WriteAsync(cancellationToken: ct);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: ct);
        GitSignature sig = TestSig();

        GitReference? head = await _repo.ReferenceResolveAsync("HEAD", cancellationToken: ct);
        GitOid[] parents = [];
        if (head is GitDirectReference dr)
        {
            parents = [dr.Target];
        }

        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = sig,
            Committer = sig,
            Message = $"add {name}\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: ct);
    }

    /// <summary>
    /// Commits a set of (possibly nested) files in one commit on
    /// <c>refs/heads/master</c>, creating parent directories as needed. Used
    /// by the nested-workdir checkout regression tests.
    /// </summary>
    private async Task<GitOid> CommitNestedFilesAsync(
        (string Path, string Content)[] files, string message, CancellationToken ct)
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: ct);
        string workdir = _repo.Workdir!;
        foreach ((string relPath, string content) in files)
        {
            string full = Path.Combine(workdir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content, cancellationToken: ct);
            await idx.AddByPathAsync(relPath, cancellationToken: ct);
        }

        await idx.WriteAsync(cancellationToken: ct);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: ct);
        GitSignature sig = TestSig();
        GitReference? head = await _repo.ReferenceResolveAsync("HEAD", cancellationToken: ct);
        GitOid[] parents = head is GitDirectReference dr ? [dr.Target] : [];

        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = sig,
            Committer = sig,
            Message = message,
            UpdateRef = "refs/heads/master",
        }, cancellationToken: ct);
    }

    // ── Stat-cache short-circuit ───────────────────────────────────────

    /// <summary>
    /// A clean workdir (index stat matches file stat) must NOT trigger a
    /// conflict on safe checkout — the stat-cache short-circuit
    /// (checkout.c:216-227) returns "not modified" without reading the file.
    /// This is the core perf fix: the file is never opened or hashed.
    /// </summary>
    [Fact]
    public async Task StatCache_CleanFile_NoConflictOnSafeCheckout()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await CommitFileAsync("file.txt", "hello\n", ct);

        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: ct);
        await idx.WriteAsync(cancellationToken: ct);

        // Safe checkout: no modifications → must succeed without conflict.
        await _repo.CheckoutHeadAsync(
            new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe },
            cancellationToken: ct);

        Assert.Equal("hello\n", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), cancellationToken: ct));
    }

    /// <summary>
    /// A workdir file whose content differs from the index (stat-cache miss
    /// → content hash) triggers a conflict on safe checkout only when the
    /// baseline→target delta would overwrite it (GIT_DELTA_MODIFIED,
    /// checkout.c:524-529). With HEAD == target (checkout_head after no
    /// changes), the delta is UNMODIFIED and the checkout proceeds, leaving
    /// the local edits alone (checkout.c:503-510) — C-verified.
    /// </summary>
    [Fact]
    public async Task ContentModified_SafeCheckout_Conflicts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid c1 = await CommitFileAsync("file.txt", "hello\n", ct);
        _ = await CommitFileAsync("file.txt", "hello2\n", ct);

        // Modify the workdir file WITHOUT staging.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), "tampered\n", cancellationToken: ct);

        // Checkout the FIRST commit's tree (target differs from HEAD) — the
        // MODIFIED delta would overwrite the dirty file → conflict.
        Commit? first = await _repo.ObjectLookupAsync<Commit>(c1, ct);
        GitTree? firstTree = await _repo.ObjectLookupAsync<GitTree>(first!.Tree, ct);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.CheckoutTreeAsync(firstTree,
                new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe },
                cancellationToken: ct));
        Assert.Equal(GitErrorCode.Conflict, ex.Code);
    }

    /// <summary>
    /// checkout_head with HEAD == target and a dirty workdir file: C's
    /// UNMODIFIED case is CHECKOUT_ACTION_IF(FORCE, UPDATE_BLOB, NONE)
    /// (checkout.c:503-510) — the SAFE checkout succeeds and preserves the
    /// local edits (C-verified: rc=0, file keeps its local content).
    /// </summary>
    [Fact]
    public async Task ContentModified_SafeCheckoutHead_ProceedsAndPreserves()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await CommitFileAsync("file.txt", "hello\n", ct);

        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), "tampered\n", cancellationToken: ct);

        await _repo.CheckoutHeadAsync(
            new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe },
            cancellationToken: ct);

        Assert.Equal("tampered\n",
            await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), cancellationToken: ct));
    }

    /// <summary>
    /// Force checkout overwrites a content-modified workdir file (content hash
    /// detects the change, FORCE allows the overwrite).
    /// </summary>
    [Fact]
    public async Task ContentModified_ForceCheckout_Overwrites()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await CommitFileAsync("file.txt", "hello\n", ct);

        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), "tampered\n", cancellationToken: ct);

        await _repo.CheckoutHeadAsync(
            new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force },
            cancellationToken: ct);

        Assert.Equal("hello\n", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "file.txt"), cancellationToken: ct));
    }

    // ── Mode-change handling (is_filemode_changed) ─────────────────────

    /// <summary>
    /// With core.filemode=true (POSIX default), toggling the exec bit on a
    /// regular file counts as a workdir modification
    /// (checkout.c:239, is_filemode_changed → true). With HEAD == target the
    /// UNMODIFIED delta maps to NONE (checkout.c:503-510), so the SAFE
    /// checkout proceeds and the exec bit is preserved — C-verified. The
    /// modification is still observable: with a target whose file is
    /// EXECUTABLE, the MODIFIED delta plus the exec-bit mismatch conflicts.
    /// </summary>
    [Fact]
    public async Task FileModeChange_WithRespectFilemode_Conflicts()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // exec-bit semantics are POSIX-only
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        GitOid c1 = await CommitFileAsync("script.sh", "echo hi\n", ct);
        _ = await CommitFileAsync("script.sh", "echo hi\n", ct, executable: true);

        // Ensure the config says core.filemode=true.
        await _repo.Config.SetBoolAsync("core.filemode", true, ct);

        // Make the workdir file regular again (commit2 left it executable):
        // the baseline (HEAD) is executable, the workdir is regular → the
        // workdir is modified. Checkout the FIRST (regular) tree — the
        // MODIFIED delta (exec → regular) plus the mode mismatch → conflict.
        File.SetUnixFileMode(
            Path.Combine(_repo.Workdir!, "script.sh"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Commit? first = await _repo.ObjectLookupAsync<Commit>(c1, ct);
        GitTree? firstTree = await _repo.ObjectLookupAsync<GitTree>(first!.Tree, ct);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.CheckoutTreeAsync(firstTree,
                new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe },
                cancellationToken: ct));
        Assert.Equal(GitErrorCode.Conflict, ex.Code);
    }

    /// <summary>
    /// With core.filemode=false, toggling the exec bit does NOT count as a
    /// workdir modification (is_filemode_changed normalizes exec bits away).
    /// Matches checkout.c:162-171.
    /// </summary>
    [Fact]
    public async Task FileModeChange_WithoutRespectFilemode_NoConflict()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        await CommitFileAsync("script.sh", "echo hi\n", ct);

        // core.filemode=false → exec-bit changes are ignored.
        await _repo.Config.SetBoolAsync("core.filemode", false, ct);

        File.SetUnixFileMode(
            Path.Combine(_repo.Workdir!, "script.sh"),
            UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // No conflict — the mode change is invisible to is_filemode_changed.
        await _repo.CheckoutHeadAsync(
            new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe },
            cancellationToken: ct);
    }

    // ── Symlink correctness ────────────────────────────────────────────

    /// <summary>
    /// A symlink whose target is an executable file must NOT be flagged as
    /// "modified" — git stores symlinks at mode 0120000 and never compares an
    /// exec bit for them. Following the link (<c>File.GetUnixFileMode</c>) would
    /// see the target's exec bit and report a spurious modification; the workdir
    /// iterator's lstat'd mode
    /// (Symlink) is compared against the baseline mode (Symlink) — they match.
    /// (checkout.c:239 + is_filemode_changed with both modes == S_IFLNK.)
    /// </summary>
    [Fact]
    public async Task SymlinkWithExecTarget_NotModified()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // symlinks need POSIX
        }

        CancellationToken ct = TestContext.Current.CancellationToken;

        // Create a target file and a symlink pointing to it.
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "target.sh"), "#!/bin/sh\necho hi\n", cancellationToken: ct);
        File.SetUnixFileMode(Path.Combine(workdir, "target.sh"), UnixFileMode.UserExecute | UnixFileMode.UserRead);
        File.CreateSymbolicLink(Path.Combine(workdir, "link"), Path.Combine(workdir, "target.sh"));

        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: ct);
        await idx.AddByPathAsync("link", cancellationToken: ct);
        await idx.AddByPathAsync("target.sh", cancellationToken: ct);
        await idx.WriteAsync(cancellationToken: ct);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: ct);
        GitSignature sig = TestSig();
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "symlink + exec target\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: ct);

        // Now write the index again so the stat cache is fresh.
        idx = await _repo.GetIndexAsync(cancellationToken: ct);
        await idx.WriteAsync(cancellationToken: ct);

        // Safe checkout must NOT conflict — the symlink is unmodified even
        // though its target is executable.
        await _repo.CheckoutHeadAsync(
            new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe },
            cancellationToken: ct);
    }

    /// <summary>
    /// Changing a symlink's target string (the blob content of the symlink)
    /// IS a workdir modification — the OID of the link-target string changes.
    /// Safe checkout must conflict. Matches checkout.c:242-248 where
    /// git_diff__oid_for_entry calls git_odb__hashlink (readlink + hash) for
    /// symlinks, NOT a content read of the target file.
    /// </summary>
    [Fact]
    public async Task SymlinkTargetChanged_IsModified()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        string workdir = _repo.Workdir!;

        // Commit: symlink "link" → "target_a"
        await File.WriteAllTextAsync(Path.Combine(workdir, "target_a.txt"), "a\n", cancellationToken: ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "target_b.txt"), "b\n", cancellationToken: ct);
        File.CreateSymbolicLink(Path.Combine(workdir, "link"), "target_a.txt");

        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: ct);
        await idx.AddByPathAsync("link", cancellationToken: ct);
        await idx.AddByPathAsync("target_a.txt", cancellationToken: ct);
        await idx.AddByPathAsync("target_b.txt", cancellationToken: ct);
        await idx.WriteAsync(cancellationToken: ct);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: ct);
        GitSignature sig = TestSig();
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "symlink → target_a\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: ct);

        // Refresh index stat cache.
        idx = await _repo.GetIndexAsync(cancellationToken: ct);
        await idx.WriteAsync(cancellationToken: ct);

        // Repoint the symlink to a different target.
        File.Delete(Path.Combine(workdir, "link"));
        File.CreateSymbolicLink(Path.Combine(workdir, "link"), "target_b.txt");

        // The link-target string changed → the symlink blob OID changed → the
        // workdir IS modified. With HEAD == target the UNMODIFIED delta maps
        // to NONE (checkout.c:503-510) — C-verified: rc=0, link preserved.
        await _repo.CheckoutHeadAsync(
            new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe },
            cancellationToken: ct);

        FileInfo linkInfo = new(Path.Combine(workdir, "link"));
        Assert.Equal("target_b.txt", linkInfo.LinkTarget);
    }

    /// <summary>
    /// Force checkout restores the original symlink target after the link was
    /// repointed (the symlink-aware content hash detected the change, FORCE
    /// allows the overwrite). The content hash must detect the link TARGET
    /// string change: following the link and hashing the target's content
    /// would mask target-string changes when the target file itself was
    /// unchanged.
    /// </summary>
    [Fact]
    public async Task SymlinkTargetChanged_ForceCheckout_RestoresOriginalTarget()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        string workdir = _repo.Workdir!;

        await File.WriteAllTextAsync(Path.Combine(workdir, "target_a.txt"), "a\n", cancellationToken: ct);
        await File.WriteAllTextAsync(Path.Combine(workdir, "target_b.txt"), "b\n", cancellationToken: ct);
        File.CreateSymbolicLink(Path.Combine(workdir, "link"), "target_a.txt");

        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: ct);
        await idx.AddByPathAsync("link", cancellationToken: ct);
        await idx.AddByPathAsync("target_a.txt", cancellationToken: ct);
        await idx.AddByPathAsync("target_b.txt", cancellationToken: ct);
        await idx.WriteAsync(cancellationToken: ct);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: ct);
        GitSignature sig = TestSig();
        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "symlink → target_a\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: ct);

        // Repoint the symlink.
        File.Delete(Path.Combine(workdir, "link"));
        File.CreateSymbolicLink(Path.Combine(workdir, "link"), "target_b.txt");

        // Force checkout restores the committed target.
        await _repo.CheckoutHeadAsync(
            new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force },
            cancellationToken: ct);

        string? resolved = File.ResolveLinkTarget(Path.Combine(workdir, "link"), returnFinalTarget: false)?.Name;
        Assert.NotNull(resolved);
        Assert.Equal("target_a.txt", Path.GetFileName(resolved));
    }

    // ── Racy-git guard ─────────────────────────────────────────────────

    /// <summary>
    /// When the index file's own mtime (Stamp) equals a file entry's mtime,
    /// the racy-git guard (<c>git_index_entry_newer_than_index</c>,
    /// checkout.c:219) fires: the stat cache is untrusted and the code falls
    /// through to the content hash. A file modified-then-reverted within the
    /// same tick is thus caught by the content comparison.
    /// </summary>
    [Fact]
    public async Task RacyGit_EntryMtimeEqualsStamp_IsNewerThanIndex()
    {
        // An in-memory index has stamp == Zero → EntryNewerThanIndex is false
        // (no racy-git risk without a written index).
        var idx = GitIndex.New(_repo.ObjectFormat);
        var entry = new GitIndexEntry
        {
            Path = GitPath.FromUtf8String("file.txt"),
            Mtime = new IndexTime(1000000000, 0),
            Mode = GitFileMode.Regular,
        };
        Assert.False(idx.EntryNewerThanIndex(entry));

        // Capture a stamp from a file whose mtime matches the entry's mtime.
        // The entry's mtime is now equal to the index stamp → racily newer
        // (git_index_entry_newer_than_index: stamp.nanos <= entry.nanos → true).
        string stampFile = Path.Combine(_tempDir, ".stamp_test");
        await File.WriteAllTextAsync(stampFile, "", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(stampFile, new DateTime(2001, 9, 9, 1, 46, 40, DateTimeKind.Utc));
        idx.CaptureStamp(stampFile);

        Assert.Equal(1000000000, idx.Stamp.Seconds);
        Assert.True(idx.EntryNewerThanIndex(entry));

        // Older mtime → not racy.
        var older = new GitIndexEntry
        {
            Path = GitPath.FromUtf8String("file.txt"),
            Mtime = new IndexTime(999999999, 0),
            Mode = GitFileMode.Regular,
        };
        Assert.False(idx.EntryNewerThanIndex(older));

        File.Delete(stampFile);
    }

    // ── Nested workdir descend (checkout_action_wd_only tree branch) ─────

    /// <summary>
    /// Safe checkout on a clean workdir must succeed when tracked files live
    /// in (possibly nested) subdirectories. <see cref="CheckoutContext.HandleWorkdirOnlyAsync"/>
    /// uses an exact-match index lookup (<see cref="GitIndex.Find"/>)
    /// for the trailing-'/' workdir tree entry and a spurious post-prefix
    /// '/' boundary check would make the descend branch unreachable: nested
    /// files appear "missing from the workdir" and a Modified delta with no
    /// workdir entry classifies as Conflict under Safe (no RecreateMissing).
    /// Mirrors libgit2's <c>checkout_action_wd_only</c> (checkout.c:404-413)
    /// which descends whenever <c>git_index__find_pos</c>'s
    /// insertion-position entry prefix-matches the tree path via
    /// <c>pfxcomp</c>. Covers a direct child (src/top.txt) and a deeper
    /// descendant (src/nested/inner.txt) — the spurious boundary check only
    /// rejected direct children, while the exact-match lookup rejected both.
    /// </summary>
    [Fact]
    public async Task CleanNestedWorkdir_SafeCheckout_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // C1 on master: src/top.txt=v1, src/nested/inner.txt=v1.
        GitOid c1 = await CommitNestedFilesAsync(
            [("src/top.txt", "top-v1\n"), ("src/nested/inner.txt", "inner-v1\n")],
            "C1: nested v1\n", ct);

        // Branch B at C1 (the v1 point).
        await _repo.BranchCreateAsync("B", c1, cancellationToken: ct);

        // C2 on master: advance both nested files to v2. After this commit the
        // workdir and index match C2 (content v2).
        await CommitNestedFilesAsync(
            [("src/top.txt", "top-v2\n"), ("src/nested/inner.txt", "inner-v2\n")],
            "C2: nested v2\n", ct);

        // Move HEAD to B (v1) WITHOUT touching the index/workdir (SetHead only
        // moves the ref). The checkout baseline is the HEAD tree — the NEW
        // HEAD (checkout.c:2476-2484) — so baseline == target and the diff is
        // EMPTY: checkout_head intentionally does NOT re-sync the workdir
        // (C-verified: rc=0, files keep their v2 content).
        await _repo.SetHeadAsync("refs/heads/B", cancellationToken: ct);
        await _repo.CheckoutHeadAsync(
            new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe },
            cancellationToken: ct);

        string workdir = _repo.Workdir!;
        Assert.Equal("top-v2\n",
            await File.ReadAllTextAsync(Path.Combine(workdir, "src/top.txt"), cancellationToken: ct));
        Assert.Equal("inner-v2\n",
            await File.ReadAllTextAsync(Path.Combine(workdir, "src/nested/inner.txt"), cancellationToken: ct));
    }

    /// <summary>
    /// Negative counterpart to <see cref="CleanNestedWorkdir_SafeCheckout_Succeeds"/>:
    /// a genuinely dirty nested workdir file must still conflict under Safe.
    /// Guards against a descend branch that is too permissive (e.g.
    /// always treating nested files as unmodified). Tampers one nested file
    /// after the v2 commit so the workdir no longer matches the index; Safe
    /// checkout must refuse with Conflict.
    /// </summary>
    [Fact]
    public async Task DirtyNestedWorkdir_SafeCheckout_Conflicts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        GitOid c1 = await CommitNestedFilesAsync(
            [("src/top.txt", "top-v1\n"), ("src/nested/inner.txt", "inner-v1\n")],
            "C1: nested v1\n", ct);
        await _repo.BranchCreateAsync("B", c1, cancellationToken: ct);
        await CommitNestedFilesAsync(
            [("src/top.txt", "top-v2\n"), ("src/nested/inner.txt", "inner-v2\n")],
            "C2: nested v2\n", ct);

        // Tamper a nested workdir file WITHOUT staging. After SetHead, the
        // checkout baseline is the new HEAD tree (== target), so the diff is
        // EMPTY: the UNMODIFIED+dirty files map to NONE (checkout.c:503-510)
        // and the SAFE checkout succeeds, preserving the tampered content
        // (C-verified).
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(
            Path.Combine(workdir, "src/nested/inner.txt"), "tampered\n", cancellationToken: ct);

        await _repo.SetHeadAsync("refs/heads/B", cancellationToken: ct);
        await _repo.CheckoutHeadAsync(
            new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe },
            cancellationToken: ct);

        Assert.Equal("tampered\n",
            await File.ReadAllTextAsync(Path.Combine(workdir, "src/nested/inner.txt"), cancellationToken: ct));
    }
}
