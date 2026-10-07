using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;
using RebaseOps = LibGit2CS.Rebase.GitRebase;

namespace LibGit2CS.UnitTests.Rebase;

/// <summary>
/// Regression tests for the rebase parity behaviors (notes rewrite ref,
/// commit message trim) in
/// libgit2 1.9.4.
/// Expectations C-verified against libgit2 1.9.4 (rebase.c:1218-1247,
/// 1032-1035; commit.c:595-607).
/// </summary>
public sealed class RebaseNotesMessageParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public RebaseNotesMessageParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RebaseParity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
        await _repo.Config.SetBoolAsync("core.autocrlf", false);
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

    private async Task<GitOid> CommitFileAsync(string fileName, string content, string message, string refName)
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);

        // C (commit.c:113-117): a commit created with update_ref must have
        // the current tip as its first parent — chain onto it.
        List<GitOid> parentChain = [];
        if (await _repo.ReferenceResolveAsync(refName, TestContext.Current.CancellationToken) is GitDirectReference tipRef)
        {
            parentChain.Add(tipRef.Target);
        }

        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parentChain,
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = refName,
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// master: c1 (f.txt); feature: c2 (g.txt) — c2's raw message starts
    /// with two blank lines and carries a note in refs/notes/commits.
    /// </summary>
    private async Task<(GitOid c1, GitOid c2)> SetupAsync()
    {
        GitOid c1 = await CommitFileAsync("f.txt", "f\n", "base\n", "refs/heads/master");

        // Move HEAD to the feature branch (clean index/workdir at c1).
        await _repo.BranchCreateAsync("feature", c1, force: false, TestContext.Current.CancellationToken);
        await _repo.SetHeadAsync("refs/heads/feature", TestContext.Current.CancellationToken);
        await _repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        GitOid c2 = await CommitFileAsync("g.txt", "g\n", "\n\nfeature message\n", "refs/heads/feature");
        _ = await _repo.NotesCreateAsync(null, TestSig(), TestSig(), c2, "a note on c2", allowOverwrite: false, TestContext.Current.CancellationToken);
        return (c1, c2);
    }

    private async Task<GitOid> RunRebaseAndFinishAsync()
    {
        GitAnnotatedCommit branch = await _repo.AnnotatedCommitFromRefAsync(
            (await _repo.ReferenceLookupAsync("refs/heads/feature", TestContext.Current.CancellationToken))!);
        GitAnnotatedCommit upstream = await _repo.AnnotatedCommitFromRefAsync(
            (await _repo.ReferenceLookupAsync("refs/heads/master", TestContext.Current.CancellationToken))!);

        using RebaseOps rebase = await RebaseOps.InitAsync(_repo, branch, upstream, onto: null, options: null, cancellationToken: TestContext.Current.CancellationToken);
        _ = await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitOid newCommit = await rebase.CommitAsync(null, TestSig(), cancellationToken: TestContext.Current.CancellationToken);
        await rebase.FinishAsync(cancellationToken: TestContext.Current.CancellationToken);
        return newCommit;
    }

    [Fact]
    public async Task Finish_WithoutNotesRewriteref_DoesNotCopyNotes()
    {
        // with no notes.rewriteref config, C's notes_ref_lookup returns
        // GIT_ENOTFOUND and rebase_copy_notes copies NOTHING — refs/notes/commits
        // keeps pointing at the original note commit (no hardcoded default ref
        // rewriting the note to the new commit).
        _ = await SetupAsync();
        GitReference? notesBefore = await _repo.ReferenceLookupAsync("refs/notes/commits", TestContext.Current.CancellationToken);
        Assert.NotNull(notesBefore);
        GitOid notesBeforeOid = ((GitDirectReference)notesBefore!).Target;

        _ = await RunRebaseAndFinishAsync();

        GitReference? notesAfter = await _repo.ReferenceLookupAsync("refs/notes/commits", TestContext.Current.CancellationToken);
        Assert.NotNull(notesAfter);
        Assert.Equal(notesBeforeOid, ((GitDirectReference)notesAfter).Target);
    }

    [Fact]
    public async Task Commit_TrimsLeadingNewlinesFromMessage()
    {
        // C uses git_commit_message(current_commit) which trims leading
        // newlines (commit.c:595-607) — not the untrimmed RawMessage.
        _ = await SetupAsync();
        GitOid newCommit = await RunRebaseAndFinishAsync();

        Commit? replayed = await _repo.ObjectLookupAsync<Commit>(newCommit, TestContext.Current.CancellationToken);
        Assert.NotNull(replayed);
        Assert.Equal("feature message\n", replayed!.RawMessage);
    }
}
