using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Stash;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Repository;

/// <summary>
/// Integration tests for the stash enumeration iterator
/// (<see cref="GitRepository.StashForEachAsync"/>) exercised
/// end-to-end against locally-initialized repos with real stashes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The existing
/// <see cref="Transports.StashDockerTests"/> exercise
/// <see cref="GitRepository.StashSaveAsync"/> /
/// <see cref="GitRepository.StashApplyAsync"/> over SSH (Docker-gated),
/// but never drain <see cref="GitRepository.StashForEachAsync"/> against a
/// local repo — and the iterator was entirely cold in the default
/// (no-Docker) integration run. The 42-line
/// <see cref="GitRepository.StashForEachAsync"/> <c>MoveNext</c> state
/// machine and the reflog-walk loop inside it had 0% coverage. These tests
/// create real stashes locally, enumerate the stash list to completion,
/// drop entries, and re-enumerate.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/stash/stash.c</c>
/// (<c>test_stash_foreach__with_empty</c>,
/// <c>test_stash_foreach__enumerate_stashed_commits</c>), adapted to build
/// the sandbox from scratch and use the per-repo
/// <see cref="GitRepository.StashForEachAsync"/> iterator directly.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class StashEnumerationIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh temp dir path for a non-bare repo.</summary>
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-stashenum-" + Guid.NewGuid().ToString("N"));

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
    /// Inits a non-bare repo and creates an initial commit on
    /// <c>refs/heads/main</c> with <c>file.txt</c> containing
    /// <paramref name="content"/>, and sets HEAD. Returns the commit OID.
    /// </summary>
    private static async Task<GitOid> InitRepoWithFileAsync(string path, string content, CancellationToken ct)
    {
        GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "file.txt"), content, ct);
        GitIndex idx = await repo.GetIndexAsync(ct);
        await idx.AddByPathAsync("file.txt", ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig,
            Committer = Sig,
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, ct);
        await repo.SetHeadAsync("refs/heads/main", ct);
        await repo.DisposeAsync();
        return commitOid;
    }

    /// <summary>
    /// Reopens the repo, modifies <c>file.txt</c> in the workdir (without
    /// staging), and stashes the workdir change with the given message.
    /// Returns the stash commit OID.
    /// </summary>
    private static async Task<GitOid> ModifyAndStashAsync(string repoPath, string newContent, string message, GitStashFlags flags, CancellationToken ct)
    {
        await using GitRepository repo = await GitRepository.OpenAsync(repoPath, new GitContext(), cancellationToken: ct);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), newContent, ct);
        return await repo.StashSaveAsync(Sig, message, flags, ct);
    }

    // ── ForEach with no stashes ─────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.StashForEachAsync"/> on a fresh repo
    /// with no <c>refs/stash</c> yields nothing. Drains the iterator to
    /// completion and asserts emptiness — exercises the
    /// <c>stash is null</c> early-return path.
    /// </summary>
    [Fact]
    public async Task StashForEach_NoStashes_YieldsEmpty()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithFileAsync(path, "base\n", ct);
        try
        {
            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            List<GitStashEntry> entries = await repo.StashForEachAsync(ct).ToListAsync(ct);
            Assert.Empty(entries);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── ForEach with multiple stashes ───────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.StashForEachAsync"/> after three
    /// <see cref="GitRepository.StashSaveAsync"/> calls yields three
    /// entries in LIFO order (stash@{0} is most recent). The reflog is
    /// appended on each save, so enumeration index 0 is the newest.
    /// Exercises the full reflog-walk loop in
    /// <see cref="GitRepository.StashForEachAsync"/>.
    /// </summary>
    [Fact]
    public async Task StashForEach_ThreeStashes_YieldsAllInLifoOrder()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithFileAsync(path, "base\n", ct);
        try
        {
            await ModifyAndStashAsync(path, "v1\n", "first\n", GitStashFlags.Default, ct);
            await ModifyAndStashAsync(path, "v2\n", "second\n", GitStashFlags.Default, ct);
            await ModifyAndStashAsync(path, "v3\n", "third\n", GitStashFlags.Default, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            List<GitStashEntry> entries = await repo.StashForEachAsync(ct).ToListAsync(ct);

            Assert.Equal(3, entries.Count);
            // LIFO: index 0 = most recent. The stash reflog prepends an
            // "On <branch>: " prefix to the supplied message.
            Assert.Equal(0, entries[0].Index);
            Assert.EndsWith("third", entries[0].Message);
            Assert.Equal(1, entries[1].Index);
            Assert.EndsWith("second", entries[1].Message);
            Assert.Equal(2, entries[2].Index);
            Assert.EndsWith("first", entries[2].Message);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── ForEach after drop ─────────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRepository.StashDropAsync"/> then
    /// <see cref="GitRepository.StashForEachAsync"/> yields the remaining
    /// entries. With three stashes, dropping index 1 leaves two entries;
    /// the dropped message no longer appears. Exercises the
    /// <see cref="GitRepository.StashDropAsync"/> reflog-rewrite path
    /// followed by a full enumeration drain.
    /// </summary>
    [Fact]
    public async Task StashForEach_AfterDrop_YieldsRemaining()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithFileAsync(path, "base\n", ct);
        try
        {
            await ModifyAndStashAsync(path, "v1\n", "first\n", GitStashFlags.Default, ct);
            await ModifyAndStashAsync(path, "v2\n", "second\n", GitStashFlags.Default, ct);
            await ModifyAndStashAsync(path, "v3\n", "third\n", GitStashFlags.Default, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await repo.StashDropAsync(1, ct);

            List<GitStashEntry> entries = await repo.StashForEachAsync(ct).ToListAsync(ct);
            Assert.Equal(2, entries.Count);
            var messages = entries.Select(e => e.Message).ToList();
            Assert.DoesNotContain(messages, m => m.EndsWith("second"));
            Assert.Contains(messages, m => m.EndsWith("third"));
            Assert.Contains(messages, m => m.EndsWith("first"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.StashForEachAsync"/> after dropping the
    /// only stash yields nothing — <see cref="GitRepository.StashDropAsync"/>
    /// removes <c>refs/stash</c> when the reflog becomes empty.
    /// </summary>
    [Fact]
    public async Task StashForEach_AfterDropOnlyStash_YieldsEmpty()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithFileAsync(path, "base\n", ct);
        try
        {
            await ModifyAndStashAsync(path, "v1\n", "only\n", GitStashFlags.Default, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            await repo.StashDropAsync(0, ct);

            List<GitStashEntry> entries = await repo.StashForEachAsync(ct).ToListAsync(ct);
            Assert.Empty(entries);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── ForEach entry content ──────────────────────────────────────────

    /// <summary>
    /// Each <see cref="GitStashEntry"/> yielded by
    /// <see cref="GitRepository.StashForEachAsync"/> carries the commit OID
    /// returned by <see cref="GitRepository.StashSaveAsync"/> — the entry's
    /// <see cref="GitStashEntry.CommitId"/> matches the stash commit. This
    /// exercises the <c>entry.NewId</c> → <c>GitStashEntry.CommitId</c>
    /// mapping in the iterator.
    /// </summary>
    [Fact]
    public async Task StashForEach_EntryCommitId_MatchesStashSaveReturn()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithFileAsync(path, "base\n", ct);
        try
        {
            GitOid stashOid = await ModifyAndStashAsync(path, "v1\n", "stashmsg\n", GitStashFlags.Default, ct);

            await using GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            List<GitStashEntry> entries = await repo.StashForEachAsync(ct).ToListAsync(ct);
            Assert.Single(entries);
            Assert.Equal(stashOid, entries[0].CommitId);
            Assert.Equal(0, entries[0].Index);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// <see cref="GitRepository.StashSaveAsync"/> with
    /// <see cref="GitStashFlags.IncludeUntracked"/> stashes an untracked
    /// file, and the resulting stash appears in
    /// <see cref="GitRepository.StashForEachAsync"/>. After popping, the
    /// untracked file is restored to the workdir. Exercises the
    /// include-untracked stash path + enumeration.
    /// </summary>
    [Fact]
    public async Task StashSave_IncludeUntracked_EnumeratesAndPopsUntrackedFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await InitRepoWithFileAsync(path, "base\n", ct);
        try
        {
            // Add an untracked file in the workdir.
            await using (GitRepository repo = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct))
            {
                await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "untracked.txt"), "u\n", ct);
                await repo.StashSaveAsync(Sig, "untracked-stash\n", GitStashFlags.IncludeUntracked, ct);
            }

            // The stash appears in enumeration.
            await using GitRepository repo2 = await GitRepository.OpenAsync(path, new GitContext(), cancellationToken: ct);
            List<GitStashEntry> entries = await repo2.StashForEachAsync(ct).ToListAsync(ct);
            Assert.Single(entries);

            // Pop the stash; the untracked file should reappear.
            Assert.False(File.Exists(Path.Combine(path, "untracked.txt")));
            await repo2.StashPopAsync(0, options: null, ct);
            Assert.True(File.Exists(Path.Combine(path, "untracked.txt")));
        }
        finally
        {
            Cleanup(path);
        }
    }
}
