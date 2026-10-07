using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Revwalk;

// Regression coverage against libgit2 1.9.4.
// parity behaviors for Revwalk:
//   A date-based reflog spec with a pre-epoch timestamp must be
//         treated as time-based, not a position:
//         reflog[(int)negative] must not return the
//         wrong entry or throw ArgumentOutOfRangeException on
//         an empty reflog (C: (size_t) cast → time-based branch, and
//         GIT_ENOTFOUND for empty reflogs, revparse.c:214-269).
//   A commit with more than 65535 parents must fail the parse with
//         'commit has more than 2^16 parents' (commit_list.c:147-151),
//         not truncate OutDegree (ushort) and drop parents from the walk.
public sealed class RevwalkMediumRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public RevwalkMediumRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RevwalkMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private static GitSignature Sig() => new("t", "t@t", new GitTime(1700000000, 0));

    private async Task<GitRepository> InitRepoWithCommitAsync()
    {
        string repoPath = Path.Combine(_tempDir, "repo_" + Guid.NewGuid().ToString("N")[..8]);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "one\n", cancellationToken: TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = Sig(),
            Committer = Sig(),
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);
        return repo;
    }

    // ── Pre Epoch Reflog Date Empty Reflog Throws Not Found ──────────

    [Fact]
    public async Task PreEpochReflogDate_EmptyReflog_ThrowsNotFound()
    {
        // A negative identifier (a pre-epoch timestamp: C's date path passes
        // (size_t)seconds, which wraps negative → huge → time-based branch,
        // revparse.c:371-376) must NOT be treated as a position; the parse
        // fails with GIT_ENOTFOUND, not ArgumentOutOfRangeException.
        // (The date parser does not yield negative timestamps, so the private
        // lookup is pinned directly, as in PackIndexerMemoryRetentionTests.)
        await using GitRepository repo = await InitRepoWithCommitAsync();

        // Guarantee an empty reflog for the branch.
        string logPath = Path.Combine(repo.Path, "logs", "refs", "heads", "main");
        if (File.Exists(logPath))
        {
            File.Delete(logPath);
        }

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await InvokeRetrieveOidFromReflogAsync(repo, "refs/heads/main", -1));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task PreEpochReflogDate_NonEmptyReflog_UsesTimeBranch()
    {
        // Control: a negative identifier on a NON-empty reflog takes the
        // time-based branch and falls back to the OLDEST entry (C's
        // time-branch fallback, revparse.c:236-255) — never a position
        // lookup.
        await using GitRepository repo = await InitRepoWithCommitAsync();
        GitOid headOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target;

        GitOid oid = await InvokeRetrieveOidFromReflogAsync(repo, "refs/heads/main", -1);
        Assert.Equal(headOid, oid);
    }

    /// <summary>
    /// Invokes the private <c>RetrieveOidFromReflogAsync(repo, refName,
    /// identifier, ct)</c> via reflection (the entry point is private and the
    /// date parser masks negative identifiers).
    /// </summary>
    private static async Task<GitOid> InvokeRetrieveOidFromReflogAsync(GitRepository repo, string refName, long identifier)
    {
        System.Reflection.MethodInfo method = typeof(GitRevParser).GetMethod(
            "RetrieveOidFromReflogAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("RetrieveOidFromReflogAsync missing");
        object result = method.Invoke(
            null, [repo, (RefNameKey)refName, identifier, TestContext.Current.CancellationToken])!;
        return await (Task<GitOid>)result;
    }

    // ── Many Parents Exceeding Uint16 Fails Like C ───────────────────

    [Fact]
    public async Task ManyParents_ExceedingUint16_FailsLikeC()
    {
        // A crafted commit with 70000 parent lines: C's commit_quick_parse
        // fails with 'commit has more than 2^16 parents' (commit_list.c:147-
        // 151), and no parents are dropped from the walk.
        await using GitRepository repo = await InitRepoWithCommitAsync();
        GitOid commitOid = ((GitDirectReference)(await repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target;
        Commit? headCommit = await repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        GitTree? tree = await repo.ObjectLookupAsync<GitTree>(headCommit!.Tree, TestContext.Current.CancellationToken);

        const int parentCount = 70000;
        var sb = new StringBuilder();
        sb.Append("tree ").Append(tree!.Id).Append('\n');
        for (int i = 0; i < parentCount; i++)
        {
            sb.Append("parent ").Append(i.ToString("x40")).Append('\n');
        }

        sb.Append("author t <t@t> 1700000000 +0000\n");
        sb.Append("committer t <t@t> 1700000000 +0000\n");
        sb.Append("\nmessage\n");

        GitOid oid = await repo.ObjectWriteAsync(GitObjectType.Commit, Encoding.ASCII.GetBytes(sb.ToString()), TestContext.Current.CancellationToken);

        using GitRevWalker walk = repo.NewRevWalker();
        await walk.PushAsync(oid, TestContext.Current.CancellationToken);

        // C fails at the parse with the specific
        // 'commit has more than 2^16 parents' message, not later with a
        // missing-parent error.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
        {
            await foreach (GitOid _ in walk.WalkAsync(TestContext.Current.CancellationToken))
            {
                // The first parse must fail.
            }
        });
        Assert.Contains("more than 2^16 parents", ex.Message);
    }
}
