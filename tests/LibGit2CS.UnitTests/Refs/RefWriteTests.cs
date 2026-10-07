using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Refs;

public sealed class RefWriteTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public RefWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RefWriteTests_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitOid> WriteCommit()
    {
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "hello"u8.ToArray(), TestContext.Current.CancellationToken);
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
            UpdateRef = "refs/heads/master",
        });
    }

    // ── Reference.Create ────────────────────────────────────────────────

    [Fact]
    public async Task Create_DirectRef_WritesLooseFile()
    {
        GitOid commitOid = await WriteCommit();

        GitReference created = await _repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(created);
        Assert.Equal(commitOid, ((GitDirectReference)created).Target);

        // The loose file should exist on disk.
        string refPath = Path.Combine(_tempDir, ".git", "refs", "heads", "feature");
        Assert.True(File.Exists(refPath));
        Assert.Equal($"{commitOid}\n", await File.ReadAllTextAsync(refPath, cancellationToken: TestContext.Current.CancellationToken));

        // Re-lookup returns the same value.
        GitReference? looked = await _repo.ReferenceLookupAsync("refs/heads/feature", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(looked);
        Assert.Equal(commitOid, ((GitDirectReference)looked).Target);
    }

    [Fact]
    public async Task Create_ExistingRef_NoForce_ThrowsExists()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.ReferenceCreateAsync("refs/heads/feature", GitOid.EmptyTreeSha1, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Exists, ex.Code);
    }

    [Fact]
    public async Task Create_ExistingRef_WithForce_Overwrites()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/heads/feature", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        // C validates the target OID exists (refs.c:402-409), so the
        // overwrite must use a real object (a second commit with a
        // different tree).
        GitOid blob2 = await _repo.ObjectWriteAsync(GitObjectType.Blob, "world\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld2 = _repo.NewTreeBuilder();
        await bld2.InsertAsync("file.txt", blob2, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid tree2 = await bld2.WriteAsync(CancellationToken.None);
        GitSignature sig2 = TestSig();
        GitOid newOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree2,
            Author = sig2,
            Committer = sig2,
            Message = "second\n",
        }, TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateAsync("refs/heads/feature", newOid, force: true, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? looked = await _repo.ReferenceLookupAsync("refs/heads/feature", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(newOid, ((GitDirectReference)looked!).Target);
    }

    [Fact]
    public async Task CreateSymbolic_WritesSymrefFile()
    {
        await WriteCommit();

        await _repo.ReferenceCreateSymbolicAsync("refs/heads/sym", "refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        GitReference? sym = await _repo.ReferenceLookupAsync("refs/heads/sym", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sym);
        Assert.True(sym!.IsSymbolic);
        Assert.Equal("refs/heads/master", ((GitSymbolicReference)sym).TargetName);

        // The file should contain "ref: refs/heads/master\n".
        string refPath = Path.Combine(_tempDir, ".git", "refs", "heads", "sym");
        Assert.Equal("ref: refs/heads/master\n", await File.ReadAllTextAsync(refPath, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_NestedRefPath_CreatesDirectories()
    {
        GitOid commitOid = await WriteCommit();

        await _repo.ReferenceCreateAsync("refs/heads/feature/sub-branch", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        string refPath = Path.Combine(_tempDir, ".git", "refs", "heads", "feature", "sub-branch");
        Assert.True(File.Exists(refPath));
    }

    // ── Reference.Delete ────────────────────────────────────────────────

    [Fact]
    public async Task Delete_LooseRef_RemovesFile()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/heads/temp", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        string refPath = Path.Combine(_tempDir, ".git", "refs", "heads", "temp");
        Assert.True(File.Exists(refPath));

        await _repo.Refs.DeleteAsync("refs/heads/temp", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(refPath));
        Assert.Null(await _repo.ReferenceLookupAsync("refs/heads/temp", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_NonexistentRef_ThrowsNotFound()
    {
        // C (refdb_fs.c:1817-1822): deleting a ref that exists nowhere fails
        // with GIT_ENOTFOUND "reference '%s' not found".
        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                await _repo.Refs.DeleteAsync("refs/heads/nonexistent", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── Reference.Rename ────────────────────────────────────────────────

    [Fact]
    public async Task Rename_LooseRef_MovesFile()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/heads/old-name", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference renamed = await _repo.ReferenceRenameAsync(
            (await _repo.ReferenceLookupAsync("refs/heads/old-name", cancellationToken: TestContext.Current.CancellationToken))!,
            "refs/heads/new-name",
            force: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("refs/heads/new-name", renamed.Name);
        Assert.Equal(commitOid, ((GitDirectReference)renamed).Target);

        // Old file gone, new file exists.
        string oldPath = Path.Combine(_tempDir, ".git", "refs", "heads", "old-name");
        string newPath = Path.Combine(_tempDir, ".git", "refs", "heads", "new-name");
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(newPath));
    }

    [Fact]
    public async Task Rename_ToExisting_NoForce_Throws()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/heads/a", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateAsync("refs/heads/b", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference a = (await _repo.ReferenceLookupAsync("refs/heads/a", cancellationToken: TestContext.Current.CancellationToken))!;
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.ReferenceRenameAsync(
                a,
                "refs/heads/b",
                force: false, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Exists, ex.Code);
    }

    // ── SetHead ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SetHead_Branch_SetsSymbolicHead()
    {
        await WriteCommit();

        await _repo.SetHeadAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        GitReference? head = await _repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.True(head!.IsSymbolic);
        Assert.Equal("refs/heads/master", ((GitSymbolicReference)head).TargetName);
    }

    [Fact]
    public async Task SetHeadDetached_WritesDirectHead()
    {
        GitOid commitOid = await WriteCommit();

        await _repo.SetHeadDetachedAsync(commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? head = await _repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.False(head!.IsSymbolic);
        Assert.Equal(commitOid, ((GitDirectReference)head).Target);
    }

    // ── Reflog ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WritesReflog_WhenLogAllRefUpdates()
    {
        // The init'd repo has core.logallrefupdates=true (non-bare).
        GitOid commitOid = await WriteCommit();

        // WriteCommit updated refs/heads/master with a reflog entry.
        Assert.True(await _repo.Refs.HasLogAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken));

        GitRefLog? log = await _repo.ReferenceReadLogAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(log);
        Assert.True(log!.EntryCount > 0);

        // The most recent entry should point from zero → commitOid.
        GitRefLogEntry entry = log[0];
        Assert.Equal(commitOid, entry.NewId);
    }

    [Fact]
    public async Task Reflog_Append_Directly()
    {
        GitOid commitOid = await WriteCommit();
        GitSignature sig = TestSig();

        await _repo.Refs.EnsureLogAsync("refs/heads/test", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Refs.AppendReflogAsync(
            "refs/heads/test",
            GitOid.Empty,
            commitOid,
            sig,
            "test message", cancellationToken: TestContext.Current.CancellationToken);

        GitRefLog? log = await _repo.ReferenceReadLogAsync("refs/heads/test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(log);
        Assert.Equal(1, log!.EntryCount);
        Assert.Equal(commitOid, log[0].NewId);
        Assert.Equal("test message", log[0].Message);
    }

    [Fact]
    public async Task Reflog_Append_NewlineInMessage_ReplacedWithSpace()
    {
        GitOid commitOid = await WriteCommit();
        GitSignature sig = TestSig();

        await _repo.Refs.EnsureLogAsync("refs/heads/nl", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Refs.AppendReflogAsync(
            "refs/heads/nl",
            GitOid.Empty,
            commitOid,
            sig,
            "line1\nline2", cancellationToken: TestContext.Current.CancellationToken);

        string logPath = Path.Combine(_tempDir, ".git", "logs", "refs", "heads", "nl");
        string content = await File.ReadAllTextAsync(logPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("line1 line2", content);
        Assert.DoesNotContain("line1\nline2", content);
    }

    // ── Transaction ──────────────────────────────────────────────────────

    [Fact]
    public async Task Transaction_SingleRef_Commits()
    {
        await WriteCommit();
        var newOid = GitOid.Parse("abcdefabcdefabcdefabcdefabcdefabcdefabcd".AsSpan(), GitHashAlgorithmKind.Sha1);

        await using GitTransaction tx = _repo.NewReferenceTransaction();
        tx.LockRef("refs/heads/master");
        tx.SetTarget("refs/heads/master", newOid, "test: update");
        await tx.CommitAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitReference? looked = await _repo.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(newOid, ((GitDirectReference)looked!).Target);
    }

    [Fact]
    public async Task Transaction_MultipleRefs_CommitsAtomically()
    {
        GitOid commitOid = await WriteCommit();
        var oid1 = GitOid.Parse("1111111111111111111111111111111111111111".AsSpan(), GitHashAlgorithmKind.Sha1);
        var oid2 = GitOid.Parse("2222222222222222222222222222222222222222".AsSpan(), GitHashAlgorithmKind.Sha1);

        await _repo.ReferenceCreateAsync("refs/heads/a", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateAsync("refs/heads/b", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        await using GitTransaction tx = _repo.NewReferenceTransaction();
        tx.LockRef("refs/heads/a");
        tx.LockRef("refs/heads/b");
        tx.SetTarget("refs/heads/a", oid1, "update a");
        tx.SetTarget("refs/heads/b", oid2, "update b");
        await tx.CommitAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(oid1, ((GitDirectReference)(await _repo.ReferenceLookupAsync("refs/heads/a", cancellationToken: TestContext.Current.CancellationToken))!).Target);
        Assert.Equal(oid2, ((GitDirectReference)(await _repo.ReferenceLookupAsync("refs/heads/b", cancellationToken: TestContext.Current.CancellationToken))!).Target);
    }

    [Fact]
    public async Task Transaction_DisposeWithoutCommit_RollsBack()
    {
        GitOid commitOid = await WriteCommit();
        var newOid = GitOid.Parse("3333333333333333333333333333333333333333".AsSpan(), GitHashAlgorithmKind.Sha1);

        await using (GitTransaction tx = _repo.NewReferenceTransaction())
        {
            tx.LockRef("refs/heads/master");
            tx.SetTarget("refs/heads/master", newOid, "should rollback");
            // Dispose without Commit → rollback.
        }

        // The ref should still have the original value.
        GitReference? looked = await _repo.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(commitOid, ((GitDirectReference)looked!).Target);

        // No .lock file should remain.
        string lockPath = Path.Combine(_tempDir, ".git", "refs", "heads", "master.lock");
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task Transaction_SetTarget_UnlockedRef_Throws()
    {
        GitOid commitOid = await WriteCommit();

        await using GitTransaction tx = _repo.NewReferenceTransaction();
        GitException ex = Assert.Throws<GitException>(() =>
            tx.SetTarget("refs/heads/unlocked", commitOid));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── Lock contention ──────────────────────────────────────────────────

    [Fact]
    public async Task Lock_AlreadyLocked_ThrowsExists()
    {
        GitOid commitOid = await WriteCommit();

        await using GitTransaction tx = _repo.NewReferenceTransaction();
        tx.LockRef("refs/heads/master");

        // A second lock attempt should fail.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await _repo.ReferenceCreateAsync("refs/heads/master", commitOid, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Exists, ex.Code);
    }

    // ── Packed-refs ──────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_PackedRef_RemovesFromPack()
    {
        // Create a ref, then pack it by writing a packed-refs file.
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/heads/packed-test", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        // Manually create a packed-refs file containing the ref, then remove
        // the loose ref — simulating a packed-only ref.
        string packedPath = Path.Combine(_tempDir, ".git", "packed-refs");
        await File.WriteAllTextAsync(packedPath,
            "# pack-refs with: peeled fully-peeled sorted \n" +
            $"{commitOid} refs/heads/packed-test\n", cancellationToken: TestContext.Current.CancellationToken);

        // Remove the loose ref.
        string loosePath = Path.Combine(_tempDir, ".git", "refs", "heads", "packed-test");
        File.Delete(loosePath);

        // Lookup should find it in packed-refs.
        GitReference? looked = await _repo.ReferenceLookupAsync("refs/heads/packed-test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(looked);

        // Delete should remove it from packed-refs.
        await _repo.Refs.DeleteAsync("refs/heads/packed-test", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await _repo.ReferenceLookupAsync("refs/heads/packed-test", cancellationToken: TestContext.Current.CancellationToken));

        // The packed-refs file should no longer contain the ref.
        string packedContent = await File.ReadAllTextAsync(packedPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain("refs/heads/packed-test", packedContent);
    }
}
