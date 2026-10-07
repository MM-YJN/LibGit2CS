using LibGit2CS.Core;
using LibGit2CS.IntegrationTests.DockerFixture;
using LibGit2CS.IntegrationTests.TestKit.Logger;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Reset;

using Microsoft.Extensions.Logging;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// Docker-based integration tests for <see cref="GitRepository.ResetAsync"/> in all
/// three modes (<see cref="GitResetMode.Soft"/>, <see cref="GitResetMode.Mixed"/>,
/// <see cref="GitResetMode.Hard"/>) exercised against a working tree and
/// object database populated by a real SSH clone from
/// <see cref="SshGitDockerFixture"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these tests exist.</b> The unit-test project covers reset against
/// locally-initialized repos, but the <see cref="Reset"/> namespace
/// had <c>0%</c> integration coverage — no end-to-end path through fetch →
/// clone (which writes the index + working tree from a fetched pack) → local
/// commits on top of the fetched base → <see cref="GitRepository.ResetAsync"/> moving HEAD,
/// rewriting the index, and checking out the workdir. These tests close that
/// gap.
/// </para>
/// <para>
/// <b>Setup.</b> Each test clones the fixture's seeded repo (one commit,
/// README.md), then builds two local commits on top (fileA.txt, fileB.txt)
/// via the index, so HEAD sits two commits ahead of the clone base. It then
/// resets back to the first local commit and asserts the per-mode
/// HEAD/index/workdir invariants.
/// </para>
/// </remarks>
public sealed class ResetDockerTests : IDisposable
{
    private readonly LoggerFactory _loggerFactory;
    private readonly AlpineNoKeyImageFixture _alpine;

    public ResetDockerTests(AlpineNoKeyImageFixture alpine, ITestOutputHelper testOutputHelper)
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

    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    /// <summary>
    /// Builds two local commits on top of the cloned HEAD: <c>commitA</c>
    /// adds <c>fileA.txt</c>, <c>commitB</c> adds <c>fileB.txt</c>. After this
    /// helper HEAD=<c>commitB</c>, the index/workdir contain both files, and
    /// <c>commitA</c> (the reset target) is returned.
    /// </summary>
    private static async Task<(GitOid commitA, GitOid commitB)> BuildTwoLocalCommitsAsync(GitRepository cloned, string workdir, CancellationToken ct)
    {
        GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
        GitOid baseOid = ((GitDirectReference)head!).Target;

        // commitA: add fileA.txt.
        await File.WriteAllTextAsync(Path.Combine(workdir, "fileA.txt"), "a\n", ct);
        GitIndex idx = await cloned.GetIndexAsync(ct);
        await idx.AddByPathAsync("fileA.txt", ct);
        await idx.WriteAsync(ct);
        GitOid treeA = await idx.WriteTreeAsync(ct);
        GitOid commitA = await cloned.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeA,
            Parents = [baseOid],
            Author = Sig,
            Committer = Sig,
            Message = "A\n",
            UpdateRef = "refs/heads/main",
        }, ct);

        // commitB: add fileB.txt.
        await File.WriteAllTextAsync(Path.Combine(workdir, "fileB.txt"), "b\n", ct);
        idx = await cloned.GetIndexAsync(ct);
        await idx.AddByPathAsync("fileB.txt", ct);
        await idx.WriteAsync(ct);
        GitOid treeB = await idx.WriteTreeAsync(ct);
        GitOid commitB = await cloned.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeB,
            Parents = [commitA],
            Author = Sig,
            Committer = Sig,
            Message = "B\n",
            UpdateRef = "refs/heads/main",
        }, ct);

        return (commitA, commitB);
    }

    // ── Soft: move HEAD, keep index + workdir ─────────────────────────────

    /// <summary>
    /// <see cref="GitResetMode.Soft"/>: after two local commits (A, B),
    /// soft-reset to A moves HEAD to A but leaves the index and working tree
    /// at B (both files staged). Exercises the HEAD-move-only branch of
    /// <see cref="GitRepository.ResetAsync"/> against a fetched object database.
    /// </summary>
    [Fact]
    public async Task Reset_Soft_MovesHead_KeepsIndexAndWorkdir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-reset-soft-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);
            (GitOid commitA, GitOid commitB) = await BuildTwoLocalCommitsAsync(cloned, targetPath, ct);

            Commit target = (await cloned.ObjectLookupAsync<Commit>(commitA, ct))!;
            await cloned.ResetAsync(target, GitResetMode.Soft, cancellationToken: ct);

            // HEAD → commitA.
            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.Equal(commitA, ((GitDirectReference)head!).Target);

            // Index keeps both files (B's tree, unchanged).
            GitIndex idx = await cloned.GetIndexAsync(ct);
            Assert.NotNull(idx.EntryByPath("fileA.txt"));
            Assert.NotNull(idx.EntryByPath("fileB.txt"));

            // Workdir keeps both files.
            Assert.True(File.Exists(Path.Combine(targetPath, "fileA.txt")));
            Assert.True(File.Exists(Path.Combine(targetPath, "fileB.txt")));
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

    // ── Mixed: move HEAD, reset index, keep workdir ───────────────────────

    /// <summary>
    /// <see cref="GitResetMode.Mixed"/>: reset to A moves HEAD to A and
    /// resets the index to A's tree (fileA only — fileB is unstaged) but leaves
    /// the working tree unchanged (both files on disk). Exercises the
    /// HEAD + index-rewrite branch of <see cref="GitRepository.ResetAsync"/>.
    /// </summary>
    [Fact]
    public async Task Reset_Mixed_MovesHead_ResetsIndex_KeepsWorkdir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-reset-mixed-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);
            (GitOid commitA, _) = await BuildTwoLocalCommitsAsync(cloned, targetPath, ct);

            Commit target = (await cloned.ObjectLookupAsync<Commit>(commitA, ct))!;
            await cloned.ResetAsync(target, GitResetMode.Mixed, cancellationToken: ct);

            // HEAD → commitA.
            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.Equal(commitA, ((GitDirectReference)head!).Target);

            // Index reset to A's tree: fileA staged, fileB NOT.
            GitIndex idx = await cloned.GetIndexAsync(ct);
            Assert.NotNull(idx.EntryByPath("fileA.txt"));
            Assert.Null(idx.EntryByPath("fileB.txt"));

            // Workdir unchanged: both files still on disk.
            Assert.True(File.Exists(Path.Combine(targetPath, "fileA.txt")));
            Assert.True(File.Exists(Path.Combine(targetPath, "fileB.txt")));
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

    // ── Hard: move HEAD, reset index + workdir ───────────────────────────

    /// <summary>
    /// <see cref="GitResetMode.Hard"/>: reset to A moves HEAD to A, resets
    /// the index to A's tree, and checks out A's tree into the working
    /// directory (fileB.txt removed from disk). Exercises the full
    /// HEAD + index + forced-checkout branch of <see cref="GitRepository.ResetAsync"/>
    /// — the only mode that touches the working tree.
    /// </summary>
    [Fact]
    public async Task Reset_Hard_MovesHead_ResetsIndexAndWorkdir()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-reset-hard-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);
            (GitOid commitA, _) = await BuildTwoLocalCommitsAsync(cloned, targetPath, ct);

            Commit target = (await cloned.ObjectLookupAsync<Commit>(commitA, ct))!;
            await cloned.ResetAsync(target, GitResetMode.Hard, cancellationToken: ct);

            // HEAD → commitA.
            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.Equal(commitA, ((GitDirectReference)head!).Target);

            // Index reset to A's tree: fileA staged, fileB NOT.
            GitIndex idx = await cloned.GetIndexAsync(ct);
            Assert.NotNull(idx.EntryByPath("fileA.txt"));
            Assert.Null(idx.EntryByPath("fileB.txt"));

            // Workdir reset to A's tree: fileA present, fileB gone.
            Assert.True(File.Exists(Path.Combine(targetPath, "fileA.txt")));
            Assert.False(File.Exists(Path.Combine(targetPath, "fileB.txt")),
                "fileB.txt should be removed from the workdir by a hard reset to A");
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
