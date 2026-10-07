using LibGit2CS.Core;
using LibGit2CS.IntegrationTests.DockerFixture;
using LibGit2CS.IntegrationTests.TestKit.Logger;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Stash;

using Microsoft.Extensions.Logging;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// Docker-based integration tests for the stash engine
/// (<see cref="GitRepository.StashSaveAsync"/>/<see cref="GitRepository.StashApplyAsync"/>/
/// <see cref="GitRepository.StashPopAsync"/>/<see cref="GitRepository.StashForEachAsync"/>)
/// exercised end-to-end against a working tree populated by a real SSH
/// clone from <see cref="SshGitDockerFixture"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these tests exist.</b> The unit-test project covers stash against
/// locally-initialized repos, but the <see cref="LibGit2CS.Stash"/> namespace
/// had <c>0%</c> integration coverage — no end-to-end path through fetch →
/// clone (which writes the index + working tree) → workdir mutation →
/// stash save (index/workdir diff + reset) → apply/pop (checkout merge) →
/// refs/stash + reflog. These tests close that gap by cloning the fixture's
/// seeded repo and stashing workdir edits on top of the checked-out HEAD.
/// </para>
/// <para>
/// <b>Gating.</b> All tests are skipped when Docker is not reachable
/// (<see cref="SshGitDockerFixture.SkipIfDockerNotAvailable"/>).
/// </para>
/// </remarks>
public sealed class StashDockerTests : IDisposable
{
    private readonly LoggerFactory _loggerFactory;
    private readonly AlpineNoKeyImageFixture _alpine;

    public StashDockerTests(AlpineNoKeyImageFixture alpine, ITestOutputHelper testOutputHelper)
    {
        _alpine = alpine;
        _loggerFactory = new LoggerFactory([new XunitTestOutputLoggerProvider(testOutputHelper)]);
    }

    public void Dispose() => _loggerFactory.Dispose();

    private static GitRemoteCallbacks PasswordCallbacks => new()
    {
        Credentials = (_, _, _, _) => Task.FromResult<GitCredential?>(
            new GitUserPassCredential(SshGitDockerFixture.TestUser, SshGitDockerFixture.TestPassword)),
        CertificateCheck = _ => true,
    };

    private static string Url(SshGitDockerContainer fixture)
    {
        return $"ssh://{SshGitDockerFixture.TestUser}@{SshGitDockerFixture.Host}:{fixture.Port}{SshGitDockerFixture.RepoPath}";
    }

    /// <summary>The stasher identity used across all stash tests.</summary>
    private static GitSignature Stasher => new("Stasher", "stasher@example.com", new GitTime(1700000000, 0));

    /// <summary>Collects all stash entries into a list.</summary>
    private static async Task<List<GitStashEntry>> StashListAsync(GitRepository repo, CancellationToken ct)
    {
        var list = new List<GitStashEntry>();
        await foreach (GitStashEntry entry in repo.StashForEachAsync(ct))
        {
            list.Add(entry);
        }

        return list;
    }

    // ── Save + Apply ─────────────────────────────────────────────────────

    /// <summary>
    /// Save + apply over a cloned working tree: modify the tracked
    /// <c>README.md</c>, <see cref="GitRepository.StashSaveAsync"/> captures the change
    /// and resets the workdir to HEAD, then
    /// <see cref="GitRepository.StashApplyAsync"/> restores the stashed content without
    /// dropping the stash. Exercises the index/workdir diff, the stash-commit
    /// creation (parents = HEAD + index), the workdir reset, refs/stash write,
    /// and the apply checkout-merge — all against an object database and
    /// index populated purely by an SSH clone.
    /// </summary>
    [Fact]
    public async Task Stash_SaveAndApply_RestoresWorkdirChanges()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-stash-apply-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            string readmePath = Path.Combine(targetPath, "README.md");
            Assert.True(File.Exists(readmePath), "README.md should be checked out after clone");
            Assert.Equal("hello\n", await File.ReadAllTextAsync(readmePath, ct));

            // Modify the tracked file in the working tree.
            await File.WriteAllTextAsync(readmePath, "hello\nMODIFIED\n", ct);

            GitOid stashOid = await cloned.StashSaveAsync(Stasher, "wip", GitStashFlags.Default, ct);
            Assert.False(stashOid.IsZero, "SaveAsync must return a non-zero stash commit OID");

            // Save resets the workdir to HEAD.
            Assert.Equal("hello\n", await File.ReadAllTextAsync(readmePath, ct));

            // refs/stash must now exist.
            Assert.NotNull(await cloned.ReferenceLookupAsync("refs/stash", ct));

            // Apply restores the stashed change.
            await cloned.StashApplyAsync(0, cancellationToken: ct);
            Assert.Equal("hello\nMODIFIED\n", await File.ReadAllTextAsync(readmePath, ct));

            // Apply does NOT drop the stash — it must still be listed.
            List<GitStashEntry> entries = await StashListAsync(cloned, ct);
            Assert.Single(entries);
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Pop = apply + drop ───────────────────────────────────────────────

    /// <summary>
    /// Pop over a cloned working tree: after save, pop applies the stashed
    /// change AND drops the stash entry. Exercises
    /// <see cref="GitRepository.StashPopAsync"/> (apply + <see cref="GitRepository.StashDropAsync"/>
    /// sequence) and confirms the stash list is empty afterward.
    /// </summary>
    [Fact]
    public async Task Stash_Pop_AppliesAndDropsStash()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-stash-pop-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            string readmePath = Path.Combine(targetPath, "README.md");
            await File.WriteAllTextAsync(readmePath, "hello\nMODIFIED\n", ct);

            await cloned.StashSaveAsync(Stasher, null, GitStashFlags.Default, ct);

            // Sanity: one stash entry after save.
            Assert.Single(await StashListAsync(cloned, ct));

            await cloned.StashPopAsync(0, cancellationToken: ct);

            // Workdir restored to the stashed (modified) content.
            Assert.Equal("hello\nMODIFIED\n", await File.ReadAllTextAsync(readmePath, ct));

            // Pop drops the stash — the list must be empty.
            Assert.Empty(await StashListAsync(cloned, ct));

            // refs/stash must be gone after the only entry is popped.
            Assert.Null(await cloned.ReferenceLookupAsync("refs/stash", ct));
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── IncludeUntracked ────────────────────────────────────────────────

    /// <summary>
    /// Save with <see cref="GitStashFlags.IncludeUntracked"/> over a
    /// cloned working tree: stashing captures both a modified tracked file
    /// and an untracked new file, resets the workdir cleanly (new file
    /// removed), and apply restores both. Exercises the untracked-files
    /// capture path — a distinct branch of the stash save/apply machinery
    /// that the default (tracked-only) tests never reach.
    /// </summary>
    [Fact]
    public async Task Stash_IncludeUntracked_CapturesAndRestoresNewFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-stash-untracked-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            string readmePath = Path.Combine(targetPath, "README.md");
            string newPath = Path.Combine(targetPath, "new.txt");

            // Modify the tracked file AND add an untracked new file.
            await File.WriteAllTextAsync(readmePath, "hello\nMODIFIED\n", ct);
            await File.WriteAllTextAsync(newPath, "I am new\n", ct);

            GitOid stashOid = await cloned.StashSaveAsync(Stasher, "wip-untracked", GitStashFlags.IncludeUntracked, ct);
            Assert.False(stashOid.IsZero);

            // Save resets the workdir: README back to HEAD, new.txt removed.
            Assert.Equal("hello\n", await File.ReadAllTextAsync(readmePath, ct));
            Assert.False(File.Exists(newPath), "untracked new.txt should be removed by IncludeUntracked stash save");

            // Apply restores both the tracked modification and the untracked file.
            await cloned.StashApplyAsync(0, cancellationToken: ct);
            Assert.Equal("hello\nMODIFIED\n", await File.ReadAllTextAsync(readmePath, ct));
            Assert.True(File.Exists(newPath), "untracked new.txt should be restored by apply");
            Assert.Equal("I am new\n", await File.ReadAllTextAsync(newPath, ct));
        }
        finally
        {
            try
            {
                Directory.Delete(targetPath, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }
}
