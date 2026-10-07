using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Refs;

/// <summary>
/// Regression tests for the refs parity behaviors in
/// libgit2 1.9.4.
/// Expected error codes/messages were verified against the C reference
/// (libgit2 1.9.4, refs.c / refdb_fs.c / refdb.c / reflog.c).
/// </summary>
public sealed class RefsParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public RefsParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RefsParity_" + Guid.NewGuid().ToString("N")[..8]);
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

    private string GitDir => Path.Combine(_tempDir, ".git");

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async Task<GitOid> WriteCommit(string refName = "refs/heads/master")
    {
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitSignature sig = TestSig();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "base\n",
            UpdateRef = refName,
        });
    }

    private static GitException Throws(Func<Task> action, GitErrorCode code)
        => ThrowsCore(async () => await action().ConfigureAwait(false), code);

    private static GitException Throws<T>(Func<Task<T>> action, GitErrorCode code)
        => ThrowsCore(async () => { _ = await action().ConfigureAwait(false); }, code);

    private static GitException ThrowsCore(Func<Task> action, GitErrorCode code)
    {
        GitException ex = Assert.ThrowsAsync<GitException>(action).GetAwaiter().GetResult();
        Assert.Equal(code, ex.Code);
        return ex;
    }

    // ── deleting a non-existent ref → GIT_ENOTFOUND ──

    [Fact]
    public async Task DeleteNonexistentRef_ThrowsNotFound()
    {
        GitException ex = Throws(
            () => _repo.Refs.DeleteAsync("refs/heads/does-not-exist", TestContext.Current.CancellationToken),
            GitErrorCode.NotFound);

        Assert.Equal("reference 'refs/heads/does-not-exist' not found", ex.Message);
    }

    // ── lock-file-exists → GIT_ELOCKED ──

    [Fact]
    public async Task LockContention_ThrowsLocked()
    {
        GitOid oid = await WriteCommit();
        string lockPath = Path.Combine(GitDir, "refs", "heads", "master.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await File.WriteAllTextAsync(lockPath, "stale", TestContext.Current.CancellationToken);

        Throws(
            () => _repo.ReferenceCreateAsync("refs/heads/master", oid, force: true, cancellationToken: TestContext.Current.CancellationToken),
            GitErrorCode.Locked);
    }

    // ── git_reference_delete refuses HEAD ──

    [Fact]
    public async Task DeleteHeadReferenceObject_ThrowsError()
    {
        _ = await WriteCommit();
        GitReference? head = await _repo.ReferenceLookupAsync("HEAD", TestContext.Current.CancellationToken);
        Assert.NotNull(head);

        GitException ex = Throws(
            () => _repo.Refs.DeleteAsync(head!, TestContext.Current.CancellationToken),
            GitErrorCode.Error);

        Assert.Equal("cannot delete HEAD", ex.Message);
    }

    [Fact]
    public async Task DeleteHeadByName_StillWorks()
    {
        // The name-based API maps to git_reference_remove, which has no HEAD guard.
        _ = await WriteCommit();
        await _repo.Refs.DeleteAsync("HEAD", TestContext.Current.CancellationToken);
        Assert.Null(await _repo.ReferenceLookupAsync("HEAD", TestContext.Current.CancellationToken));
    }

    // ── core.logallrefupdates=always; no refs/stash rule ──

    [Fact]
    public async Task LogAllRefUpdatesAlways_WritesReflogForArbitraryRef()
    {
        GitOid oid = await WriteCommit();
        await _repo.Config.SetStringAsync("core.logallrefupdates", "always", TestContext.Current.CancellationToken);

        _ = await _repo.ReferenceCreateAsync("refs/foo/bar", oid, logMessage: "create", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(GitDir, "logs", "refs", "foo", "bar")));
    }

    [Fact]
    public async Task LogAllRefUpdatesTrue_NoStashSpecialCase()
    {
        GitOid oid = await WriteCommit();
        await _repo.Config.SetStringAsync("core.logallrefupdates", "true", TestContext.Current.CancellationToken);

        _ = await _repo.ReferenceCreateAsync("refs/stash", oid, logMessage: "stash", cancellationToken: TestContext.Current.CancellationToken);

        // C (refdb.c:325-332) has no refs/stash rule.
        Assert.False(File.Exists(Path.Combine(GitDir, "logs", "refs", "stash")));
    }

    // ── rename of a packed ref removes the old packed entry ──

    [Fact]
    public async Task RenamePackedRef_RemovesOldName()
    {
        GitOid oid = await WriteCommit();
        string packedPath = Path.Combine(GitDir, "packed-refs");
        await File.WriteAllTextAsync(packedPath, "# pack-refs with: peeled fully-peeled sorted \n" + oid + " refs/heads/packed1\n", TestContext.Current.CancellationToken);

        GitReference? packed = await _repo.ReferenceLookupAsync("refs/heads/packed1", TestContext.Current.CancellationToken);
        Assert.NotNull(packed);

        await _repo.ReferenceRenameAsync(packed!, "refs/heads/packed2", cancellationToken: TestContext.Current.CancellationToken);

        // Old name must no longer resolve (C runs the delete tail, which
        // removes the entry from packed-refs).
        Assert.Null(await _repo.ReferenceLookupAsync("refs/heads/packed1", TestContext.Current.CancellationToken));
        GitReference? renamed = await _repo.ReferenceLookupAsync("refs/heads/packed2", TestContext.Current.CancellationToken);
        Assert.NotNull(renamed);
        Assert.Equal(oid, ((GitDirectReference)renamed!).Target);
    }

    // ── create validates the target OID exists ──

    [Fact]
    public async Task CreateRefWithMissingTarget_ThrowsError()
    {
        var missing = GitOid.Parse("0123456789012345678901234567890123456789".AsSpan(), GitHashAlgorithmKind.Sha1);

        GitException ex = Throws(
            () => _repo.ReferenceCreateAsync("refs/heads/dangling", missing, cancellationToken: TestContext.Current.CancellationToken),
            GitErrorCode.Error);

        Assert.Equal("target OID for the reference doesn't exist on the repository", ex.Message);
    }

    [Fact]
    public async Task SetTargetWithMissingOid_ThrowsError()
    {
        GitOid oid = await WriteCommit();
        GitReference? master = await _repo.ReferenceLookupAsync("refs/heads/master", TestContext.Current.CancellationToken);
        Assert.NotNull(master);
        var missing = GitOid.Parse("0123456789012345678901234567890123456789".AsSpan(), GitHashAlgorithmKind.Sha1);

        Throws(
            () => _repo.ReferenceSetTargetAsync(master!, missing, cancellationToken: TestContext.Current.CancellationToken),
            GitErrorCode.Error);
    }

    // ── SetTarget/SetSymbolicTarget type guards ──

    [Fact]
    public async Task SetTargetOnSymbolicRef_ThrowsError()
    {
        _ = await WriteCommit();
        GitReference? head = await _repo.ReferenceLookupAsync("HEAD", TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        GitOid oid = await WriteCommit("refs/heads/other");

        GitException ex = Throws(
            () => _repo.ReferenceSetTargetAsync(head!, oid, cancellationToken: TestContext.Current.CancellationToken),
            GitErrorCode.Error);

        Assert.Equal("cannot set OID on symbolic reference", ex.Message);
    }

    [Fact]
    public async Task SetSymbolicTargetOnDirectRef_ThrowsError()
    {
        GitOid oid = await WriteCommit();
        GitReference? master = await _repo.ReferenceLookupAsync("refs/heads/master", TestContext.Current.CancellationToken);
        Assert.NotNull(master);

        GitException ex = Throws(
            () => _repo.ReferenceSetSymbolicTargetAsync(master!, "refs/heads/other", cancellationToken: TestContext.Current.CancellationToken),
            GitErrorCode.Error);

        Assert.Equal("cannot set symbolic target on a direct reference", ex.Message);
    }

    // ── HEAD reflog entry on branch switch ──

    [Fact]
    public async Task BranchSwitch_WritesHeadReflogEntry()
    {
        _ = await WriteCommit(); // master
        GitOid otherTip = await WriteCommit("refs/heads/other");

        GitRefLog? before = await _repo.ReferenceReadLogAsync("HEAD", TestContext.Current.CancellationToken);
        int countBefore = before?.EntryCount ?? 0;

        await _repo.SetHeadAsync("refs/heads/other", TestContext.Current.CancellationToken);

        GitRefLog? after = await _repo.ReferenceReadLogAsync("HEAD", TestContext.Current.CancellationToken);
        Assert.NotNull(after);
        Assert.Equal(countBefore + 1, after!.EntryCount);
        Assert.Equal(otherTip, after[0].NewId);
    }

    // ── packed-refs parse strictness ──

    [Fact]
    public async Task PackedRefsBlankLine_ThrowsCorrupted()
    {
        GitOid oid = await WriteCommit();
        string packedPath = Path.Combine(GitDir, "packed-refs");
        await File.WriteAllTextAsync(packedPath, "# pack-refs with: peeled fully-peeled sorted \n\n" + oid + " refs/heads/packed1\n", TestContext.Current.CancellationToken);

        GitException ex = Throws(
            () => _repo.ReferenceLookupAsync("refs/heads/packed1", TestContext.Current.CancellationToken),
            GitErrorCode.Error);

        Assert.Equal("corrupted packed references file", ex.Message);
    }

    [Fact]
    public async Task PackedRefsUnterminatedFinalLine_ThrowsCorrupted()
    {
        GitOid oid = await WriteCommit();
        string packedPath = Path.Combine(GitDir, "packed-refs");
        await File.WriteAllTextAsync(packedPath, "# pack-refs with: peeled fully-peeled sorted \n" + oid + " refs/heads/packed1", TestContext.Current.CancellationToken);

        Throws(
            () => _repo.ReferenceLookupAsync("refs/heads/packed1", TestContext.Current.CancellationToken),
            GitErrorCode.Error);
    }

    [Fact]
    public async Task PackedRefsPeelGarbageAtEof_ThrowsCorrupted()
    {
        GitOid oid = await WriteCommit();
        string packedPath = Path.Combine(GitDir, "packed-refs");
        await File.WriteAllTextAsync(packedPath, "# pack-refs with: peeled fully-peeled sorted \n" + oid + " refs/heads/packed1\n^" + oid + "garbage", TestContext.Current.CancellationToken);

        Throws(
            () => _repo.ReferenceLookupAsync("refs/heads/packed1", TestContext.Current.CancellationToken),
            GitErrorCode.Error);
    }

    // ── symbolic-chain nesting limit → GIT_ERROR (-1) ──

    [Fact]
    public async Task SymbolicChainTooDeep_ThrowsError()
    {
        _ = await WriteCommit();
        await _repo.ReferenceCreateSymbolicAsync("refs/a", "refs/b", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateSymbolicAsync("refs/b", "refs/c", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateSymbolicAsync("refs/c", "refs/d", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateSymbolicAsync("refs/d", "refs/e", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateSymbolicAsync("refs/e", "refs/f", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateSymbolicAsync("refs/f", "refs/g", cancellationToken: TestContext.Current.CancellationToken);
        _ = await WriteCommit("refs/g");

        GitException ex = Throws(
            () => _repo.ReferenceResolveAsync("refs/a", TestContext.Current.CancellationToken),
            GitErrorCode.Error);

        Assert.Equal("cannot resolve reference (>5 levels deep)", ex.Message);
    }

    // ── CAS on a missing ref → GIT_ENOTFOUND ──

    [Fact]
    public async Task CreateMatchingOnMissingRef_ThrowsNotFound()
    {
        GitOid oid = await WriteCommit();
        var expectedOld = GitOid.Parse("1111111111111111111111111111111111111111".AsSpan(), GitHashAlgorithmKind.Sha1);

        GitException ex = Throws(
            () => _repo.ReferenceCreateMatchingAsync("refs/heads/cas", oid, expectedOld, cancellationToken: TestContext.Current.CancellationToken),
            GitErrorCode.NotFound);

        Assert.Equal("reference 'refs/heads/cas' not found", ex.Message);
    }

    // ── non-forced create_matching on an existing ref → GIT_EEXISTS ──

    [Fact]
    public async Task CreateMatchingOnExistingMatchingRef_ThrowsExists()
    {
        GitOid oid1 = await WriteCommit();
        GitOid oid2 = await WriteCommit("refs/heads/other");

        // The C EEXISTS check (reference_path_available) runs before the CAS
        // regardless of whether the old value matches.
        Throws(
            () => _repo.ReferenceCreateMatchingAsync("refs/heads/master", oid2, oid1, force: false, cancellationToken: TestContext.Current.CancellationToken),
            GitErrorCode.Exists);
    }

    // ── symbolic ref updates never append a HEAD reflog entry ──

    [Fact]
    public async Task SymbolicRefUpdate_NoHeadReflogEntry()
    {
        _ = await WriteCommit(); // master
        await _repo.ReferenceCreateSymbolicAsync("HEAD", "refs/heads/other", force: true, "checkout: moving to refs/heads/other", TestContext.Current.CancellationToken);

        GitRefLog? before = await _repo.ReferenceReadLogAsync("HEAD", TestContext.Current.CancellationToken);
        int countBefore = before?.EntryCount ?? 0;

        // Retarget the symbolic branch "other" while HEAD points at it. C's
        // maybe_append_head skips symbolic refs entirely — no zero-oid HEAD
        // entry may be appended.
        await _repo.ReferenceCreateSymbolicAsync("refs/heads/other", "refs/heads/master", force: true, "retarget", TestContext.Current.CancellationToken);

        GitRefLog? after = await _repo.ReferenceReadLogAsync("HEAD", TestContext.Current.CancellationToken);
        Assert.Equal(countBefore, after?.EntryCount ?? 0);
    }

    // ── loose file "ref: " alone is corrupted ──

    [Fact]
    public async Task LooseRefHeaderOnly_ThrowsCorrupted()
    {
        string path = Path.Combine(GitDir, "refs", "heads", "bad");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "ref: ", TestContext.Current.CancellationToken);

        GitException ex = Throws(
            () => _repo.ReferenceLookupAsync("refs/heads/bad", TestContext.Current.CancellationToken),
            GitErrorCode.Error);

        Assert.Equal("corrupted loose reference file", ex.Message);
    }

    // ── char 40 must be ASCII whitespace (git__isspace), not Unicode ──

    [Fact]
    public async Task LooseRefNonAsciiWhitespace_ThrowsCorrupted()
    {
        GitOid oid = await WriteCommit();
        string path = Path.Combine(GitDir, "refs", "heads", "nb");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 40 hex chars + U+00A0 (NBSP): accepted by char.IsWhiteSpace, not by git__isspace.
        await File.WriteAllTextAsync(path, oid.ToString() + "\u00A0x", TestContext.Current.CancellationToken);

        Throws(
            () => _repo.ReferenceLookupAsync("refs/heads/nb", TestContext.Current.CancellationToken),
            GitErrorCode.Error);
    }

    // ── HeadAsync: git_repository_head ──

    [Fact]
    public async Task HeadAsync_SymbolicHead_ReturnsBranchRef()
    {
        GitOid oid = await WriteCommit();

        GitReference? head = await _repo.HeadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(head);
        Assert.Equal("refs/heads/master", head!.Name);
        GitDirectReference direct = Assert.IsType<GitDirectReference>(head);
        Assert.Equal(oid, direct.Target);
    }

    [Fact]
    public async Task HeadAsync_DetachedHead_ReturnsHeadDirectRef()
    {
        GitOid oid = await WriteCommit();
        await _repo.SetHeadDetachedAsync(oid, TestContext.Current.CancellationToken);

        GitReference? head = await _repo.HeadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(head);
        Assert.Equal("HEAD", head!.Name);
        GitDirectReference direct = Assert.IsType<GitDirectReference>(head);
        Assert.Equal(oid, direct.Target);
    }

    [Fact]
    public async Task HeadAsync_UnbornHead_ReturnsNull()
    {
        // Freshly-initialized repo: HEAD is symbolic to refs/heads/master but
        // no such ref exists yet — C returns GIT_EUNBORNBRANCH; the managed
        // contract returns null (documented divergence).
        Assert.Null(await _repo.HeadAsync(TestContext.Current.CancellationToken));
    }

    // ── reflog drop out of range → GIT_ENOTFOUND ──

    [Fact]
    public void ReflogDropOutOfRange_ThrowsNotFound()
    {
        const string content =
            "0000000000000000000000000000000000000000 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Test User <test@example.com> 1700000000 +0000\tfirst\n" +
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb Test User <test@example.com> 1700000001 +0000\tsecond\n";
        var reflog = new GitRefLog("refs/heads/master", GitHashAlgorithmKind.Sha1, content);

        GitException ex = Assert.Throws<GitException>(() => reflog.Drop(5));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("no reflog entry at index 5", ex.Message);
    }

    // ── CRLF reflog keeps the trailing '\r' in the message ──

    [Fact]
    public void CrlfReflog_MessageKeepsCarriageReturn()
    {
        const string content =
            "0000000000000000000000000000000000000000 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Test User <test@example.com> 1700000000 +0000\tcrlf-msg\r\n";
        var reflog = new GitRefLog("refs/heads/master", GitHashAlgorithmKind.Sha1, content);

        Assert.Equal(1, reflog.EntryCount);
        Assert.Equal("crlf-msg\r", reflog[0].Message);
    }
}
