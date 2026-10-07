using LibGit2CS.Blame;
using LibGit2CS.Core;
using LibGit2CS.IntegrationTests.DockerFixture;
using LibGit2CS.IntegrationTests.TestKit.Logger;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

using Microsoft.Extensions.Logging;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// Docker-based integration tests for the blame engine
/// (<see cref="GitBlame.FileAsync"/>) exercised end-to-end over history
/// fetched from a real OpenSSH+git container via
/// <see cref="SshGitDockerFixture"/>. Blame is a local operation; the Docker
/// fixture's value here is producing a real multi-commit history on a single
/// file via server-side <c>git</c>
/// (<see cref="SshGitDockerFixture.ExecAsync"/>) that the client fetches over
/// SSH and then attributes line-by-line.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these tests exist.</b> The unit-test project covers the blame
/// algorithm against extracted golden fixtures, but the
/// <see cref="LibGit2CS.Blame"/> namespace had <c>0%</c> integration
/// coverage — no end-to-end path through fetch → object-db population →
/// scoreboard walk → per-commit diff → hunk attribution. These tests close
/// that gap.
/// </para>
/// <para>
/// <b>Gating.</b> All tests are skipped when Docker is not reachable
/// (<see cref="SshGitDockerFixture.SkipIfDockerNotAvailable"/>).
/// </para>
/// </remarks>
public sealed class BlameDockerTests : IDisposable
{
    private readonly LoggerFactory _loggerFactory;
    private readonly AlpineNoKeyImageFixture _alpine;

    public BlameDockerTests(AlpineNoKeyImageFixture alpine, ITestOutputHelper testOutputHelper)
    {
        _alpine = alpine;
        _loggerFactory = new LoggerFactory([new XunitTestOutputLoggerProvider(testOutputHelper)]);
    }

    public void Dispose() => _loggerFactory.Dispose();

    /// <summary>
    /// Password-auth callbacks with a permissive <c>CertificateCheck</c>
    /// (hostkey verification is covered by
    /// <see cref="SshTransportDockerTests"/>).
    /// </summary>
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

    /// <summary>
    /// Seeds <c>notes.txt</c> across three child commits on <c>main</c>
    /// (one appended line per commit) so each final line originates in a
    /// distinct fetched commit. Returns nothing — the OIDs are recovered
    /// client-side after clone by walking HEAD's first-parent chain.
    /// </summary>
    private static Task SeedNotesHistoryAsync(SshGitDockerContainer fixture, CancellationToken ct)
        => fixture.ExecAsync(
            $"su {SshGitDockerFixture.TestUser} -c 'cd {SshGitDockerFixture.RepoPath} && " +
            "git config user.email t@t && git config user.name T && " +
            "echo line-one > notes.txt && git add notes.txt && git commit -m one && " +
            "echo line-two >> notes.txt && git add notes.txt && git commit -m two && " +
            "echo line-three >> notes.txt && git add notes.txt && git commit -m three'", ct);

    // ── Blame over multi-commit fetched history ─────────────────────────

    /// <summary>
    /// Blame attributes each line of <c>notes.txt</c> to the fetched
    /// commit that introduced it: with three appends across three commits,
    /// <see cref="GitBlame.FileAsync"/> yields three single-line hunks whose
    /// <see cref="BlameHunk.FinalCommitId"/> matches the corresponding commit
    /// in the cloned history. Exercises the full scoreboard walk
    /// (<c>BlameScoreboard</c>), per-commit blob diffing, hunk
    /// materialization, and signature/summary loading — all against an object
    /// database populated purely by an SSH fetch.
    /// </summary>
    [Fact]
    public async Task Blame_MultiCommitHistory_AttributesLinesToOriginCommits()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);
        await SeedNotesHistoryAsync(fixture, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-blame-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // Recover the three commit OIDs from the fetched history by walking
            // HEAD's first-parent chain: HEAD = c3 -> c2 -> c1.
            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(head);
            GitOid cThree = ((GitDirectReference)head).Target;
            Commit commitThree = (await cloned.ObjectLookupAsync<Commit>(cThree, ct))
                ?? throw new InvalidOperationException("HEAD commit missing after clone");
            GitOid cTwo = commitThree.Parents[0];
            Commit commitTwo = (await cloned.ObjectLookupAsync<Commit>(cTwo, ct))
                ?? throw new InvalidOperationException("parent commit missing after clone");
            GitOid cOne = commitTwo.Parents[0];

            using GitBlame blame = await cloned.BlameFileAsync("notes.txt", cancellationToken: ct);

            // One hunk per originating commit, in line order.
            Assert.Equal(3, blame.HunkCount);
            Assert.Equal(3, blame.LineCount);

            BlameHunk hOne = blame.GetHunk(0);
            Assert.Equal(cOne, hOne.FinalCommitId);
            Assert.Equal(1, hOne.FinalStartLineNumber);
            Assert.Equal(1, hOne.LinesInHunk);
            Assert.Equal("one", hOne.Summary);
            Assert.Equal("T", hOne.FinalSignature.Name);
            Assert.Equal("notes.txt", hOne.OrigPath.ToUtf8String());

            BlameHunk hTwo = blame.GetHunk(1);
            Assert.Equal(cTwo, hTwo.FinalCommitId);
            Assert.Equal(2, hTwo.FinalStartLineNumber);
            Assert.Equal(1, hTwo.LinesInHunk);
            Assert.Equal("two", hTwo.Summary);

            BlameHunk hThree = blame.GetHunk(2);
            Assert.Equal(cThree, hThree.FinalCommitId);
            Assert.Equal(3, hThree.FinalStartLineNumber);
            Assert.Equal(1, hThree.LinesInHunk);
            Assert.Equal("three", hThree.Summary);
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

    // ── Blame with a restricted line range ───────────────────────────────

    /// <summary>
    /// Blame honors <see cref="GitBlameOptions.MinLine"/>/
    /// <see cref="GitBlameOptions.MaxLine"/>: restricting to line 2 only
    /// yields a single hunk attributed to the commit that introduced that
    /// line. Exercises the scoreboard's line-range clipping path, which the
    /// default whole-file blame never reaches.
    /// </summary>
    [Fact]
    public async Task Blame_LineRange_RestrictsToRequestedLines()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);
        await SeedNotesHistoryAsync(fixture, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-blame-range-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(head);
            GitOid cThree = ((GitDirectReference)head).Target;
            Commit commitThree = (await cloned.ObjectLookupAsync<Commit>(cThree, ct))
                ?? throw new InvalidOperationException("HEAD commit missing after clone");
            GitOid cTwo = commitThree.Parents[0];

            using GitBlame blame = await cloned.BlameFileAsync("notes.txt",
                new GitBlameOptions { MinLine = 2, MaxLine = 2 }, ct);

            // Only line 2 is in range → one hunk, attributed to c2.
            Assert.Equal(1, blame.HunkCount);
            BlameHunk h = blame.GetHunk(0);
            Assert.Equal(cTwo, h.FinalCommitId);
            Assert.Equal(2, h.FinalStartLineNumber);
            Assert.Equal(1, h.LinesInHunk);
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

    // ── GetHunkByLine binary search ──────────────────────────────────────

    /// <summary>
    /// <see cref="GitBlame.GetHunkByLine"/> returns the hunk containing a
    /// given 1-based line number and returns null outside the file's line
    /// range. Exercises the binary-search lookup path (cold under whole-file
    /// blame, which only iterates hunks by index).
    /// </summary>
    [Fact]
    public async Task Blame_GetHunkByLine_ReturnsContainingHunk()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);
        await SeedNotesHistoryAsync(fixture, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions { FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks } };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-blame-byline-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            GitReference? head = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(head);
            GitOid cThree = ((GitDirectReference)head).Target;
            Commit commitThree = (await cloned.ObjectLookupAsync<Commit>(cThree, ct))
                ?? throw new InvalidOperationException("HEAD commit missing after clone");
            GitOid cTwo = commitThree.Parents[0];
            Commit commitTwo = (await cloned.ObjectLookupAsync<Commit>(cTwo, ct))
                ?? throw new InvalidOperationException("parent commit missing after clone");
            GitOid cOne = commitTwo.Parents[0];

            using GitBlame blame = await cloned.BlameFileAsync("notes.txt", cancellationToken: ct);

            // In-range lookups return the hunk attributed to the right commit.
            Assert.Equal(cOne, blame.GetHunkByLine(1)!.FinalCommitId);
            Assert.Equal(cTwo, blame.GetHunkByLine(2)!.FinalCommitId);
            Assert.Equal(cThree, blame.GetHunkByLine(3)!.FinalCommitId);

            // Out-of-range lookups return null (before the first line, after
            // the last line).
            Assert.Null(blame.GetHunkByLine(0));
            Assert.Null(blame.GetHunkByLine(4));
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
