using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;
using RebaseOps = LibGit2CS.Rebase.GitRebase;

namespace LibGit2CS.IntegrationTests.Rebase;

/// <summary>
/// End-to-end tests for the rebase parity behaviors (notes rewrite ref,
/// commit message trim) in
/// libgit2 1.9.4.
/// </summary>
public sealed class RebaseNotesMessageParityIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public RebaseNotesMessageParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RebaseParityInt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
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

    private static async Task<GitOid> CommitFileAsync(GitRepository repo, string fileName, string content, string message, string refName)
    {
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        // C (commit.c:109-117): with update_ref, parent[0] must equal the
        // ref tip — chain onto the current tip (c2 commits on top of the
        // feature branch's c1).
        GitReference? tip = await repo.ReferenceLookupAsync(refName, TestContext.Current.CancellationToken);
        GitOid[] parents = tip is GitDirectReference direct ? [direct.Target] : [];
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = refName,
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RebaseAndFinish_MatchesC()
    {
        string path = Path.Combine(_tempDir, "r");
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);
        await repo.Config.SetBoolAsync("core.autocrlf", false, TestContext.Current.CancellationToken);

        GitOid c1 = await CommitFileAsync(repo, "f.txt", "f\n", "base\n", "refs/heads/master");
        await repo.BranchCreateAsync("feature", c1, force: false, TestContext.Current.CancellationToken);
        await repo.SetHeadAsync("refs/heads/feature", TestContext.Current.CancellationToken);
        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);
        GitOid c2 = await CommitFileAsync(repo, "g.txt", "g\n", "\n\nfeature message\n", "refs/heads/feature");
        _ = await repo.NotesCreateAsync(null, TestSig(), TestSig(), c2, "a note on c2", allowOverwrite: false, TestContext.Current.CancellationToken);
        GitOid notesBefore = ((GitDirectReference)(await repo.ReferenceLookupAsync("refs/notes/commits", TestContext.Current.CancellationToken))!).Target;

        GitAnnotatedCommit branch = await repo.AnnotatedCommitFromRefAsync((await repo.ReferenceLookupAsync("refs/heads/feature", TestContext.Current.CancellationToken))!, TestContext.Current.CancellationToken);
        GitAnnotatedCommit upstream = await repo.AnnotatedCommitFromRefAsync((await repo.ReferenceLookupAsync("refs/heads/master", TestContext.Current.CancellationToken))!, TestContext.Current.CancellationToken);
        using RebaseOps rebase = await RebaseOps.InitAsync(repo, branch, upstream, onto: null, options: null, cancellationToken: TestContext.Current.CancellationToken);
        _ = await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid newCommit = await rebase.CommitAsync(null, TestSig(), cancellationToken: TestContext.Current.CancellationToken);
        await rebase.FinishAsync(cancellationToken: TestContext.Current.CancellationToken);

        // the replayed commit's message has the leading newlines trimmed.
        Commit? replayed = await repo.ObjectLookupAsync<Commit>(newCommit, TestContext.Current.CancellationToken);
        Assert.NotNull(replayed);
        Assert.Equal("feature message\n", replayed!.RawMessage);

        // no notes.rewriteref → the notes ref is untouched.
        GitOid notesAfter = ((GitDirectReference)(await repo.ReferenceLookupAsync("refs/notes/commits", TestContext.Current.CancellationToken))!).Target;
        Assert.Equal(notesBefore, notesAfter);
    }
}
