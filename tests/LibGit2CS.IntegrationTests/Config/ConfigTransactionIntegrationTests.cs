using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Config;

/// <summary>
/// Integration tests for atomic config transactions
/// (<see cref="GitConfigTransaction"/>) exercised end-to-end against
/// locally-initialized repos.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>ConfigWriteTests</c> cover the
/// lock/commit/rollback flow against a standalone
/// <c>FileConfigBackend</c> created by hand. These integration tests drive
/// the <b>repo-level</b> path: <see cref="GitRepository.Config"/> →
/// <see cref="GitConfiguration.LockAsync"/> → writes →
/// <see cref="GitConfigTransaction.CommitAsync"/>/
/// <see cref="GitConfigTransaction.Rollback"/>, verifying the
/// real on-disk <c>.git/config</c> file is atomically updated (or
/// untouched on rollback). This exercises the
/// <see cref="Config.GitConfigTransaction"/> +
/// <see cref="Config.FileConfigBackend"/> + atomic rename path through
/// the repo's own config backend stack — which the unit tests don't
/// chain together.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors the
/// <c>git_transaction</c>-on-config scenarios from
/// <c>tests/libgit2/config/config_stress.c</c> (atomic write + rollback)
/// and <c>tests/libgit2/config/config_write.c</c>, adapted to build the
/// sandbox from scratch.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class ConfigTransactionIntegrationTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo (caller opens it).</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-configtx-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>Reads the on-disk .git/config file content for direct inspection.</summary>
    private static async Task<string> ReadConfigFileAsync(string repoPath, CancellationToken ct)
    {
        string configPath = Path.Combine(repoPath, ".git", "config");
        return await File.ReadAllTextAsync(configPath, ct);
    }

    // ── Commit path ──────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitConfiguration.LockAsync"/> + writes +
    /// <see cref="GitConfigTransaction.CommitAsync"/> atomically persists
    /// the writes to the on-disk <c>.git/config</c>: a fresh
    /// <see cref="GitRepository"/> opened on the same path sees the
    /// committed values. Mirrors the
    /// <c>test_config_stress__lock_commit</c> scenario.
    /// </summary>
    [Fact]
    public async Task Lock_Commit_PersistsToDisk()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            // Set an initial value outside the transaction.
            await repo.Config.SetStringAsync("user.name", "initial", ct);

            // Lock, mutate, commit.
            using GitConfigTransaction tx = await repo.Config.LockAsync(ct);
            await repo.Config.SetStringAsync("user.name", "committed", ct);
            await repo.Config.SetStringAsync("user.email", "committed@example.com", ct);
            await tx.CommitAsync(ct);

            // A fresh repo sees the committed values.
            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Assert.Equal("committed", await repo2.Config.GetStringAsync("user.name", ct));
            Assert.Equal("committed@example.com", await repo2.Config.GetStringAsync("user.email", ct));

            // The on-disk config file contains the committed values.
            string onDisk = await ReadConfigFileAsync(path, ct);
            Assert.Contains("name = committed", onDisk);
            Assert.Contains("email = committed@example.com", onDisk);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// Multiple writes within a single transaction are committed
    /// atomically — either all persist or none do. Sets three keys and
    /// verifies all three land on disk after commit.
    /// </summary>
    [Fact]
    public async Task Lock_MultipleWrites_Commit_PersistsAllAtomically()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            using GitConfigTransaction tx = await repo.Config.LockAsync(ct);
            await repo.Config.SetStringAsync("core.editor", "vim", ct);
            await repo.Config.SetBoolAsync("core.autocrlf", true, ct);
            await repo.Config.SetIntAsync("core.pager", 25, ct);
            await tx.CommitAsync(ct);

            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            Assert.Equal("vim", await repo2.Config.GetStringAsync("core.editor", ct));
            Assert.True(await repo2.Config.GetBoolAsync("core.autocrlf", defaultValue: false, ct));
            Assert.Equal(25, await repo2.Config.GetIntAsync("core.pager", defaultValue: 0, ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Rollback path ───────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitConfigTransaction.Dispose"/> without
    /// <see cref="GitConfigTransaction.CommitAsync"/> rolls back the
    /// transaction — the on-disk config file is untouched. Mirrors
    /// <c>test_config_stress__lock_rollback</c>.
    /// </summary>
    [Fact]
    public async Task Lock_DisposeWithoutCommit_RollsBack_NoDiskWrite()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        (GitContext ctx, string sandboxDir) = ConfigTestSandbox.Create();
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, cancellationToken: ct);

            // Pre-existing value on disk.
            await repo.Config.SetStringAsync("user.name", "original", ct);
            string preDisk = await ReadConfigFileAsync(path, ct);

            // Lock, mutate, then dispose WITHOUT commit (implicit rollback).
            {
                using GitConfigTransaction tx = await repo.Config.LockAsync(ct);
                await repo.Config.SetStringAsync("user.name", "discarded", ct);
                await repo.Config.SetStringAsync("user.email", "discarded@example.com", ct);
            }

            // On-disk content is unchanged.
            string postDisk = await ReadConfigFileAsync(path, ct);
            Assert.Equal(preDisk, postDisk);
            Assert.Contains("original", postDisk);
            Assert.DoesNotContain("discarded", postDisk);

            // A fresh repo sees the original (pre-transaction) state.
            await using GitRepository repo2 = await GitRepository.OpenAsync(path, ctx, cancellationToken: ct);
            Assert.Equal("original", await repo2.Config.GetStringAsync("user.name", ct));
            Assert.Null(await repo2.Config.GetStringAsync("user.email", ct));
        }
        finally
        {
            await ConfigTestSandbox.CleanupAsync(ctx, sandboxDir);
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitConfigTransaction.Rollback"/> explicitly
    /// discards the transaction; subsequent reads return the pre-transaction
    /// values. The <c>user.email</c> key (which only existed in the
    /// transaction) is gone after rollback.
    /// </summary>
    [Fact]
    public async Task Rollback_Explicit_DiscardsWrites()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        (GitContext ctx, string sandboxDir) = ConfigTestSandbox.Create();
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, cancellationToken: ct);

            await repo.Config.SetStringAsync("user.name", "kept", ct);

            using GitConfigTransaction tx = await repo.Config.LockAsync(ct);
            await repo.Config.SetStringAsync("user.name", "rolledback", ct);
            await repo.Config.SetStringAsync("user.email", "rolledback@example.com", ct);
            tx.Rollback();

            // After rollback, reads return the original value.
            Assert.Equal("kept", await repo.Config.GetStringAsync("user.name", ct));
            Assert.Null(await repo.Config.GetStringAsync("user.email", ct));
        }
        finally
        {
            await ConfigTestSandbox.CleanupAsync(ctx, sandboxDir);
            Cleanup(path);
        }
    }

    // ── Lock contention ──────────────────────────────────────────────────

    /// <summary>
    /// While a transaction is outstanding, a second
    /// <see cref="GitConfiguration.LockAsync"/> on the same backend throws
    /// <see cref="GitException"/> (the config is already locked). After the
    /// first transaction commits, a second lock can be acquired. Mirrors
    /// the lock-contention path in <c>test_config_stress__lock_twice</c>.
    /// </summary>
    [Fact]
    public async Task Lock_AlreadyLocked_Throws_AfterCommit_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            GitConfigTransaction tx = await repo.Config.LockAsync(ct);
            try
            {
                // A second lock on the same config fails (the .lock file is held).
                await Assert.ThrowsAsync<GitException>(
                    () => repo.Config.LockAsync(ct));
            }
            finally
            {
                await tx.CommitAsync(ct);
            }

            // After commit, a fresh lock succeeds.
            using GitConfigTransaction tx2 = await repo.Config.LockAsync(ct);
            await repo.Config.SetStringAsync("user.name", "second-tx", ct);
            await tx2.CommitAsync(ct);

            Assert.Equal("second-tx", await repo.Config.GetStringAsync("user.name", ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitConfigTransaction.CommitAsync"/> is idempotent — a
    /// second <see cref="GitConfigTransaction.CommitAsync"/> after the
    /// first is a no-op (the <c>_committed</c> flag guards it). Mirrors the
    /// <c>git_transaction_commit</c> idempotency.
    /// </summary>
    [Fact]
    public async Task Commit_Twice_SecondCallIsNoOp()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            using GitConfigTransaction tx = await repo.Config.LockAsync(ct);
            await repo.Config.SetStringAsync("user.name", "once", ct);
            await tx.CommitAsync(ct);
            await tx.CommitAsync(ct); // no-op

            Assert.Equal("once", await repo.Config.GetStringAsync("user.name", ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Delete within a transaction ────────────────────────────────────

    /// <summary>
    /// <see cref="GitConfiguration.DeleteAsync"/> within a transaction
    /// deletes the key on commit; on rollback, the key survives. Mirrors
    /// the delete-on-locked-content path in <c>config_write.c</c>.
    /// </summary>
    [Fact]
    public async Task Delete_InTransaction_Commit_Persists_Rollback_Undoes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        (GitContext ctx, string sandboxDir) = ConfigTestSandbox.Create();
        try
        {
            await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, cancellationToken: ct);

            // Pre-existing keys on disk.
            await repo.Config.SetStringAsync("user.name", "alice", ct);
            await repo.Config.SetStringAsync("user.email", "alice@example.com", ct);

            // First transaction: delete user.name and commit.
            {
                using GitConfigTransaction tx = await repo.Config.LockAsync(ct);
                await repo.Config.DeleteAsync("user.name", ct);
                await tx.CommitAsync(ct);
            }

            // After commit, user.name is gone, user.email survives.
            Assert.Null(await repo.Config.GetStringAsync("user.name", ct));
            Assert.Equal("alice@example.com", await repo.Config.GetStringAsync("user.email", ct));

            // Second transaction: attempt to delete user.email but rollback.
            {
                using GitConfigTransaction tx = await repo.Config.LockAsync(ct);
                await repo.Config.DeleteAsync("user.email", ct);
                tx.Rollback();
            }

            // After rollback, user.email is still there.
            Assert.Equal("alice@example.com", await repo.Config.GetStringAsync("user.email", ct));
        }
        finally
        {
            await ConfigTestSandbox.CleanupAsync(ctx, sandboxDir);
            Cleanup(path);
        }
    }
}
