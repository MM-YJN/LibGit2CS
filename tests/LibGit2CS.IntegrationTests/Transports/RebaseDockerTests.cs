using LibGit2CS.Core;
using LibGit2CS.IntegrationTests.DockerFixture;
using LibGit2CS.IntegrationTests.TestKit.Logger;
using LibGit2CS.Objects;
using LibGit2CS.Rebase;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

using Microsoft.Extensions.Logging;

namespace LibGit2CS.IntegrationTests.Transports;

/// <summary>
/// Docker-based integration tests for the rebase engine
/// (<see cref="GitRebase.InitAsync"/> → <see cref="GitRebase.NextAsync"/> →
/// <see cref="GitRebase.CommitAsync"/> → <see cref="GitRebase.FinishAsync"/>)
/// exercised end-to-end over history fetched from a real OpenSSH+git
/// container via <see cref="SshGitDockerFixture"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these tests exist.</b> The unit-test project covers rebase against
/// extracted golden fixtures, but the <see cref="LibGit2CS.Rebase"/> namespace
/// had <c>0%</c> integration coverage — no end-to-end path through fetch →
/// fork-point computation (<see cref="LibGit2CS.Revwalk.GitRevWalker"/>) →
/// operation-list build → patch replay → per-step commit → state-file
/// write/cleanup. These tests close that gap by cloning a repo whose
/// <c>main</c> and <c>feature</c> branches diverge (seeded via server-side
/// <c>git</c>), then rebasing the fetched <c>feature</c> onto
/// <c>origin/main</c>.
/// </para>
/// <para>
/// <b>Setup.</b> The fixture's seeded <c>main</c> (one commit, README.md) is
/// grown into two divergent branches server-side: <c>feature</c> adds two
/// commits (feature.txt, feature2.txt) off the seed; <c>main</c> adds one
/// commit (main.txt). The client clones with
/// <see cref="GitCloneOptions.BranchName"/> = <c>"feature"</c> so HEAD sits on
/// <c>feature</c>, then rebases onto <c>refs/remotes/origin/main</c>.
/// </para>
/// <para>
/// <b>Gating.</b> All tests are skipped when Docker is not reachable
/// (<see cref="SshGitDockerFixture.SkipIfDockerNotAvailable"/>).
/// </para>
/// </remarks>
public sealed class RebaseDockerTests : IDisposable
{
    private readonly LoggerFactory _loggerFactory;
    private readonly AlpineNoKeyImageFixture _alpine;

    public RebaseDockerTests(AlpineNoKeyImageFixture alpine, ITestOutputHelper testOutputHelper)
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
    /// Seeds two divergent branches on the fixture's repo: <c>feature</c>
    /// (two commits off the seed) and an advanced <c>main</c> (one commit off
    /// the seed). After this, <c>merge-base(feature, main) == seed</c>, so
    /// rebasing <c>feature</c> onto <c>main</c> replays feature's two commits.
    /// </summary>
    private static Task SeedDivergentBranchesAsync(SshGitDockerContainer fixture, CancellationToken ct)
        => fixture.ExecAsync(
            $"su {SshGitDockerFixture.TestUser} -c 'cd {SshGitDockerFixture.RepoPath} && " +
            "git config user.email t@t && git config user.name T && " +
            "git checkout -b feature && " +
            "echo feature1 > feature.txt && git add feature.txt && git commit -m feature1 && " +
            "echo feature2 > feature2.txt && git add feature2.txt && git commit -m feature2 && " +
            "git checkout main && " +
            "echo main > main.txt && git add main.txt && git commit -m main-c2'", ct);

    // ── Merge (on-disk) rebase: replays feature onto main ───────────────

    /// <summary>
    /// On-disk rebase over fetched history: with <c>feature</c> (two
    /// commits) and <c>main</c> (one commit) diverging from the seed,
    /// <see cref="GitRebase.InitAsync"/> builds a 2-operation pick list,
    /// the <see cref="GitRebase.NextAsync"/>/<see cref="GitRebase.CommitAsync"/>
    /// loop replays both commits onto <c>origin/main</c>, and
    /// <see cref="GitRebase.FinishAsync"/> advances <c>refs/heads/feature</c>
    /// to the rebased tip. The post-rebase working tree must contain main's
    /// file (proving feature is now on top of main) plus both of feature's
    /// files.
    /// </summary>
    /// <remarks>
    /// Exercises the merge rebase path —
    /// <see cref="GitRebase"/>'s <c>InitMergeAsync</c> (state files under
    /// <c>.git/rebase-merge/</c>), <c>NextMergeAsync</c> (per-step checkout +
    /// 3-way merge against the onto commit), <c>CommitAsync</c> (write the
    /// replayed commit), and <c>FinishAsync</c> (move the branch ref + clean
    /// up state) — all against an object database populated by an SSH fetch.
    /// This is the path that was entirely cold under integration coverage.
    /// </remarks>
    [Fact]
    public async Task Rebase_Merge_ReplaysFeatureOntoMain()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);
        await SeedDivergentBranchesAsync(fixture, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions
        {
            FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks },
            BranchName = "feature",
        };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-rebase-merge-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            // HEAD is on feature (pre-rebase tip). origin/main is the onto.
            GitReference? origHead = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(origHead);
            GitOid origFeatureTip = ((GitDirectReference)origHead).Target;

            GitReference? ontoRef = await cloned.ReferenceResolveAsync("refs/remotes/origin/main", ct);
            Assert.NotNull(ontoRef);
            GitOid ontoOid = ((GitDirectReference)ontoRef).Target;
            using GitAnnotatedCommit upstream = await cloned.AnnotatedCommitFromRefAsync(ontoRef, ct);

            var sig = GitSignature.Create("Rebaser", "rebaser@rebaser.rb", new GitTime(1405694510, 0));
            using GitRebase rebase = await cloned.RebaseInitAsync(branch: null, upstream, onto: null, new GitRebaseOptions(), ct);

            // Two feature commits diverge from the seed (merge base) → 2 picks.
            Assert.Equal(2, rebase.OperationCount);

            List<GitOid> rebased = await RunPickLoopAsync(rebase, sig, ct);
            GitOid rebasedTip = rebased[^1];
            await rebase.FinishAsync(sig, ct);

            // The rebased tip is a fresh commit (different from the original).
            Assert.NotEqual(origFeatureTip, rebasedTip);

            // HEAD now resolves to the rebased tip.
            GitReference? newHead = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(newHead);
            GitOid newTip = ((GitDirectReference)newHead).Target;
            Assert.Equal(rebasedTip, newTip);

            // The first replayed commit's parent is the onto commit (origin/main)
            // — proves feature was replayed on top of main, not its original base.
            Commit rebasedFirst = (await cloned.ObjectLookupAsync<Commit>(rebasedTip, ct))
                ?? throw new InvalidOperationException("rebased tip missing");
            GitOid replayParent = rebasedFirst.Parents[0];
            Commit replayParentCommit = (await cloned.ObjectLookupAsync<Commit>(replayParent, ct))
                ?? throw new InvalidOperationException("rebased parent missing");
            Assert.Equal(ontoOid, replayParentCommit.Parents[0]);

            // Working tree reflects the rebase: main's file is now present
            // (came in via the onto commit) plus both of feature's files.
            Assert.True(File.Exists(Path.Combine(targetPath, "main.txt")), "main.txt should be present after rebasing onto main");
            Assert.True(File.Exists(Path.Combine(targetPath, "feature.txt")));
            Assert.True(File.Exists(Path.Combine(targetPath, "feature2.txt")));
            Assert.Equal("main\n", await File.ReadAllTextAsync(Path.Combine(targetPath, "main.txt"), ct));
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

    // ── In-memory rebase: computes without touching the workdir ─────────

    /// <summary>
    /// In-memory rebase over fetched history: same divergent setup, but
    /// <see cref="GitRebaseOptions.InMemory"/> = <c>true</c> computes the
    /// replayed commits without writing state files, rewinding HEAD, or
    /// touching the working tree. The replayed commits' parent chain must
    /// still anchor on the onto commit, and HEAD must be unchanged.
    /// </summary>
    /// <remarks>
    /// Exercises the distinct in-memory path (<c>NextInMemoryAsync</c> +
    /// <c>CommitInMemoryAsync</c>) — no state files, no checkout, no ref
    /// update — which the on-disk test above never reaches.
    /// </remarks>
    [Fact]
    public async Task Rebase_InMemory_ReplaysFeatureOntoMain()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SshGitDockerContainer fixture = await _alpine.StartContainerAsync(_loggerFactory, ct);
        await SeedDivergentBranchesAsync(fixture, ct);

        string url = Url(fixture);
        var cloneOpts = new GitCloneOptions
        {
            FetchOptions = new GitFetchOptions { RemoteCallbacks = PasswordCallbacks },
            BranchName = "feature",
        };

        string targetPath = Path.Combine(Path.GetTempPath(), "libgit2cs-rebase-inmem-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using GitRepository cloned = await GitClone.RunAsync(url, targetPath, cloneOpts, new GitContext(), ct);

            GitReference? origHead = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(origHead);
            GitOid origFeatureTip = ((GitDirectReference)origHead).Target;

            GitReference? ontoRef = await cloned.ReferenceResolveAsync("refs/remotes/origin/main", ct);
            Assert.NotNull(ontoRef);
            GitOid ontoOid = ((GitDirectReference)ontoRef).Target;
            using GitAnnotatedCommit upstream = await cloned.AnnotatedCommitFromRefAsync(ontoRef, ct);

            var sig = GitSignature.Create("Rebaser", "rebaser@rebaser.rb", new GitTime(1405694510, 0));
            using GitRebase rebase = await cloned.RebaseInitAsync(branch: null, upstream, onto: null, new GitRebaseOptions { InMemory = true }, ct);

            Assert.Equal(2, rebase.OperationCount);

            List<GitOid> rebased = await RunPickLoopAsync(rebase, sig, ct);
            // FinishAsync is a no-op for in-memory rebase, but call it for parity.
            await rebase.FinishAsync(sig, ct);

            Assert.Equal(2, rebased.Count);

            // The first replayed commit sits directly on the onto commit.
            Commit firstReplayed = (await cloned.ObjectLookupAsync<Commit>(rebased[0], ct))
                ?? throw new InvalidOperationException("first replayed commit missing");
            Assert.Equal(ontoOid, firstReplayed.Parents[0]);

            // The second replayed commit sits on the first.
            Commit secondReplayed = (await cloned.ObjectLookupAsync<Commit>(rebased[1], ct))
                ?? throw new InvalidOperationException("second replayed commit missing");
            Assert.Equal(rebased[0], secondReplayed.Parents[0]);

            // In-memory rebase must NOT move HEAD or write state.
            GitReference? headAfter = await cloned.ReferenceResolveAsync("HEAD", ct);
            Assert.NotNull(headAfter);
            Assert.Equal(origFeatureTip, ((GitDirectReference)headAfter).Target);
            Assert.False(Directory.Exists(Path.Combine(cloned.Path, "rebase-merge")),
                "in-memory rebase must not write rebase-merge/ state");
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

    /// <summary>
    /// Drives the rebase <see cref="GitRebase.NextAsync"/> +
    /// <see cref="GitRebase.CommitAsync"/> loop to completion, returning the
    /// list of replayed commit OIDs in pick order. The loop ends when
    /// <see cref="GitRebase.NextAsync"/> throws
    /// <see cref="GitErrorCode.IterOver"/>.
    /// </summary>
    private static async Task<List<GitOid>> RunPickLoopAsync(GitRebase rebase, GitSignature sig, CancellationToken ct)
    {
        var oids = new List<GitOid>();
        while (true)
        {
            try
            {
                await rebase.NextAsync(ct).ConfigureAwait(false);
            }
            catch (GitException ex) when (ex.Code == GitErrorCode.IterOver)
            {
                break;
            }

            oids.Add(await rebase.CommitAsync(author: null, sig, cancellationToken: ct).ConfigureAwait(false));
        }

        return oids;
    }
}
