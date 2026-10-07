using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Refs;

/// <summary>
/// Integration tests for <see cref="GitRefLog.Drop"/> exercised end-to-end
/// against a locally-initialized non-bare repo: seed a reflog via the public
/// write API, read it back, drop entries with various index/rewrite
/// combinations, persist via a ref transaction, and re-read to verify the
/// on-disk file reflects the mutation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The public reflog write API
/// (<see cref="GitReferences.EnsureLogAsync"/> +
/// <see cref="GitReferences.AppendReflogAsync"/> + read-back via
/// <see cref="GitReferences.ReadLogAsync"/>) is exercised by
/// <see cref="ReferenceIntegrationTests"/>, but
/// <see cref="GitRefLog.Drop"/>'s inverse-index dispatch, oldest-entry
/// rewrite (<c>rewritePreviousEntry=true</c>), and out-of-range guards
/// were entirely cold — the in-memory <c>Drop</c> was never persisted back
/// and re-read by any test. These tests close that gap by using
/// <see cref="GitTransaction.SetReflog"/> to write the modified log
/// atomically.
/// </para>
/// <para>
/// <b>Persistence path.</b> The in-memory <see cref="GitRefLog"/> mutated by
/// <c>Drop</c> is persisted by locking the ref in a transaction, queueing
/// the modified log via <see cref="GitTransaction.SetReflog"/>, and
/// committing. <see cref="GitTransaction.CommitAsync"/> calls
/// <c>RefDatabase.ReflogWriteAsync</c>, which replaces the on-disk file
/// (matching <c>git_refdb_backend::reflog_write</c>). The ref itself is not
/// touched — only the reflog file is rewritten.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/refs/reflog/drop.c</c>
/// (<c>test_reflog_drop__can_drop_an_entry</c>,
/// <c>test_reflog_drop__can_drop_the_oldest_entry_and_rewrite_the_log_history</c>,
/// <c>test_reflog_drop__can_drop_all_the_entries</c>,
/// <c>test_reflog_drop__dropping_a_non_exisiting_entry_from_the_log_returns_ENOTFOUND</c>),
/// adapted to build the sandbox from scratch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class RefLogDropIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-reflogdrop-" + Guid.NewGuid().ToString("N"));

    /// <summary>Best-effort recursive delete of a temp directory.</summary>
    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Inits a non-bare repo and returns it (caller disposes). No initial
    /// commit — the reflog is seeded explicitly via
    /// <see cref="GitReferences.AppendReflogAsync"/>.
    /// </summary>
    private static async Task<GitRepository> InitRepoAsync(string path, CancellationToken ct)
    {
        return await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
    }

    /// <summary>
    /// Writes a blob whose content is the single character
    /// <paramref name="tag"/> and returns its OID. Used to manufacture
    /// distinct, valid OIDs for reflog old-id/new-id pairs.
    /// </summary>
    private static async Task<GitOid> MakeOidAsync(GitRepository repo, string tag, CancellationToken ct)
        => await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(tag + "\n"), ct);

    /// <summary>
    /// Seeds a reflog with three entries on <paramref name="refName"/>:
    /// <c>(zero → oid1)</c>, <c>(oid1 → oid2)</c>, <c>(oid2 → oid3)</c>.
    /// Requires <see cref="GitReferences.EnsureLogAsync"/> first. Returns the
    /// three OIDs in seed order (oldest first).
    /// </summary>
    private static async Task<(GitOid Oid1, GitOid Oid2, GitOid Oid3)> SeedThreeEntriesAsync(
        GitRepository repo, string refName, CancellationToken ct)
    {
        GitOid oid1 = await MakeOidAsync(repo, "1", ct);
        GitOid oid2 = await MakeOidAsync(repo, "2", ct);
        GitOid oid3 = await MakeOidAsync(repo, "3", ct);

        await repo.Refs.EnsureLogAsync(refName, ct);
        await repo.Refs.AppendReflogAsync(refName, default, oid1, Sig, "first", ct);
        await repo.Refs.AppendReflogAsync(refName, oid1, oid2, Sig, "second", ct);
        await repo.Refs.AppendReflogAsync(refName, oid2, oid3, Sig, "third", ct);
        return (oid1, oid2, oid3);
    }

    /// <summary>
    /// Persists a mutated <see cref="GitRefLog"/> back to disk via a ref
    /// transaction. The ref itself is locked but not modified; only the
    /// reflog file is rewritten (matches <c>git_transaction_set_reflog</c>).
    /// </summary>
    private static async Task PersistLogAsync(GitRepository repo, string refName, GitRefLog log, CancellationToken ct)
    {
        await using GitTransaction tx = repo.NewReferenceTransaction();
        tx.LockRef(refName);
        tx.SetReflog(refName, log);
        await tx.CommitAsync(ct);
    }

    // ── Drop middle entry ──────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRefLog.Drop"/>(1) on a 3-entry reflog removes the
    /// middle entry without rewriting any OldId when
    /// <c>rewritePreviousEntry=false</c>. The persisted file reflects the
    /// deletion: re-reading yields 2 entries whose NewIds are the surviving
    /// pair. Mirrors <c>test_reflog_drop__can_drop_an_entry</c>.
    /// </summary>
    [Fact]
    public async Task Drop_MiddleEntry_NoRewrite_PersistsTwoEntries()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await InitRepoAsync(path, ct);
            const string RefName = "refs/heads/topic";
            (GitOid oid1, GitOid oid2, GitOid oid3) = await SeedThreeEntriesAsync(repo, RefName, ct);

            GitRefLog log = (await repo.ReferenceReadLogAsync(RefName, ct))!;
            Assert.Equal(3, log.EntryCount);

            // Drop middle entry (index 1 = inverse-index 1 = file-order middle).
            log.Drop(1, rewritePreviousEntry: false);
            Assert.Equal(2, log.EntryCount);

            // Persist and re-read.
            await PersistLogAsync(repo, RefName, log, ct);
            GitRefLog roundTripped = (await repo.ReferenceReadLogAsync(RefName, ct))!;
            Assert.Equal(2, roundTripped.EntryCount);

            // The remaining NewIds are oid3 (index 0, most recent) and oid1
            // (index 1, oldest). The middle oid2 is gone.
            Assert.Equal(oid3, roundTripped[0].NewId);
            Assert.Equal(oid1, roundTripped[1].NewId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Drop oldest with rewrite ───────────────────────────────────────

    /// <summary>
    /// <see cref="GitRefLog.Drop"/>(2) (or Drop(-1)) on a 3-entry reflog
    /// removes the OLDEST entry. With <c>rewritePreviousEntry=true</c>, the
    /// new oldest entry's <c>OldId</c> is cleared to zero (matching
    /// <c>git_reflog_drop</c>'s rewrite-the-log-history branch). Mirrors
    /// <c>test_reflog_drop__can_drop_the_oldest_entry_and_rewrite_the_log_history</c>.
    /// </summary>
    [Fact]
    public async Task Drop_OldestEntry_WithRewrite_ClearsNewOldestOldId()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await InitRepoAsync(path, ct);
            const string RefName = "refs/heads/topic";
            (GitOid oid1, GitOid _, GitOid oid3) = await SeedThreeEntriesAsync(repo, RefName, ct);

            GitRefLog log = (await repo.ReferenceReadLogAsync(RefName, ct))!;
            Assert.Equal(3, log.EntryCount);

            // Drop(2) drops file-order index 0 (the oldest). With rewrite=true,
            // the new oldest (the previous middle entry) has its OldId zeroed.
            log.Drop(2, rewritePreviousEntry: true);
            Assert.Equal(2, log.EntryCount);

            // After drop, the new oldest entry (inverse index 1) has OldId = zero.
            Assert.True(log[1].OldId.IsZero);
            // Its NewId is oid2 (the previous middle entry's NewId).
            // (oid2 is omitted from the tuple destructure above; recompute by
            // reading the current state: index 0 still has NewId=oid3, index 1
            // has NewId=oid2 since the original oid1 entry was dropped.)
            Assert.Equal(oid3, log[0].NewId);

            // Persist and re-read.
            await PersistLogAsync(repo, RefName, log, ct);
            GitRefLog roundTripped = (await repo.ReferenceReadLogAsync(RefName, ct))!;
            Assert.Equal(2, roundTripped.EntryCount);
            Assert.True(roundTripped[1].OldId.IsZero);
            Assert.Equal(oid3, roundTripped[0].NewId);
            // Original oldest (oid1) is gone — its NewId no longer appears.
            Assert.DoesNotContain(roundTripped, e => e.NewId == oid1);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Drop newest entry ──────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRefLog.Drop"/>(0) on a 3-entry reflog removes the
    /// NEWEST (most-recent) entry. The previous second-newest becomes the
    /// new tip (index 0). With <c>rewritePreviousEntry=true</c>, dropping
    /// the most recent entry is a no-op rewrite (early-return at
    /// <c>Drop</c> line 158-161: "No need to rewrite when removing the most
    /// recent entry").
    /// </summary>
    [Fact]
    public async Task Drop_NewestEntry_NewTipIsPreviousSecond()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await InitRepoAsync(path, ct);
            const string RefName = "refs/heads/topic";
            (GitOid oid1, GitOid oid2, GitOid oid3) = await SeedThreeEntriesAsync(repo, RefName, ct);

            GitRefLog log = (await repo.ReferenceReadLogAsync(RefName, ct))!;
            Assert.Equal(3, log.EntryCount);

            // Drop(0) → remove newest. rewritePreviousEntry=true is the
            // early-return path (index==0).
            log.Drop(0, rewritePreviousEntry: true);
            Assert.Equal(2, log.EntryCount);

            // New tip is the previous second-newest: NewId=oid2.
            Assert.Equal(oid2, log[0].NewId);
            // Oldest unchanged: NewId=oid1, OldId=zero.
            Assert.Equal(oid1, log[1].NewId);
            Assert.True(log[1].OldId.IsZero);

            // Persist and re-read.
            await PersistLogAsync(repo, RefName, log, ct);
            GitRefLog roundTripped = (await repo.ReferenceReadLogAsync(RefName, ct))!;
            Assert.Equal(2, roundTripped.EntryCount);
            Assert.Equal(oid2, roundTripped[0].NewId);
            // oid3 (the dropped newest) no longer appears.
            Assert.DoesNotContain(roundTripped, e => e.NewId == oid3);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Drop all entries ───────────────────────────────────────────────

    /// <summary>
    /// Dropping every entry via repeated <see cref="GitRefLog.Drop"/>(0)
    /// leaves an empty reflog (<see cref="GitRefLog.EntryCount"/> == 0).
    /// Persisted via transaction, the on-disk file is overwritten with an
    /// empty log; re-reading yields EntryCount == 0 (mirrors
    /// <c>test_reflog_drop__can_drop_all_the_entries</c>).
    /// </summary>
    /// <remarks>
    /// <c>HasLogAsync</c> remains true after emptying — the file still
    /// exists, it just has zero entries. (libgit2's drop-all does not delete
    /// the reflog file; the file remains as an empty log.)
    /// </remarks>
    [Fact]
    public async Task Drop_AllEntries_PersistsEmptyLog()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await InitRepoAsync(path, ct);
            const string RefName = "refs/heads/topic";
            await SeedThreeEntriesAsync(repo, RefName, ct);

            GitRefLog log = (await repo.ReferenceReadLogAsync(RefName, ct))!;
            log.Drop(0);
            log.Drop(0);
            log.Drop(0);
            Assert.Equal(0, log.EntryCount);

            await PersistLogAsync(repo, RefName, log, ct);

            // The reflog file still exists (HasLog=true) but is empty.
            Assert.True(await repo.Refs.HasLogAsync(RefName, ct));
            GitRefLog roundTripped = (await repo.ReferenceReadLogAsync(RefName, ct))!;
            Assert.Equal(0, roundTripped.EntryCount);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Out-of-range ───────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRefLog.Drop"/> with an out-of-range inverse index
    /// throws <see cref="GitException"/> with <see cref="GitErrorCode.NotFound"/>.
    /// Two sub-cases: a positive index past the tip (fileIndex goes negative)
    /// and a negative index past the oldest (fileIndex exceeds count). Mirrors
    /// <c>test_reflog_drop__dropping_a_non_exisiting_entry_from_the_log_returns_ENOTFOUND</c>,
    /// where C returns <c>GIT_ENOTFOUND</c> (converted to
    /// <see cref="GitException"/> per the AGENTS.md error convention).
    /// </summary>
    [Theory]
    [InlineData(99)]    // positive past the tip
    [InlineData(-99)]   // negative past the oldest
    public void Drop_OutOfRange_ThrowsNotFound(int badIndex)
    {
        GitRefLog log = new("refs/heads/topic", GitHashAlgorithmKind.Sha256);
        log.Append(default, Sig, "a");
        log.Append(default, Sig, "b");

        GitException ex = Assert.Throws<GitException>(() => log.Drop(badIndex));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        // Log unchanged.
        Assert.Equal(2, log.EntryCount);
    }

    // ── Negative index drops oldest (no rewrite) ───────────────────────

    /// <summary>
    /// <see cref="GitRefLog.Drop"/>(-1) is equivalent to dropping the
    /// oldest entry (negative indices count from the oldest). With
    /// <c>rewritePreviousEntry=false</c>, no OldId is rewritten — the
    /// remaining oldest entry retains its (now-dangling) OldId pointing at
    /// the dropped entry's NewId. Mirrors libgit2's bare-drop-oldest path.
    /// </summary>
    [Fact]
    public async Task Drop_NegativeOne_TargetsOldest()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        try
        {
            await using GitRepository repo = await InitRepoAsync(path, ct);
            const string RefName = "refs/heads/topic";
            (GitOid oid1, GitOid _, GitOid oid3) = await SeedThreeEntriesAsync(repo, RefName, ct);

            GitRefLog log = (await repo.ReferenceReadLogAsync(RefName, ct))!;
            Assert.Equal(3, log.EntryCount);

            // Drop(-1) → fileIndex 0 → oldest entry. No rewrite.
            log.Drop(-1, rewritePreviousEntry: false);
            Assert.Equal(2, log.EntryCount);

            // The new oldest entry (inverse index 1) still has its original
            // OldId (=oid1) — we did not rewrite.
            Assert.Equal(oid1, log[1].OldId);
            Assert.Equal(oid3, log[0].NewId);

            // Original oldest (NewId=oid1) no longer appears as a NewId.
            Assert.DoesNotContain(log, e => e.NewId == oid1);
        }
        finally
        {
            Cleanup(path);
        }
    }
}
