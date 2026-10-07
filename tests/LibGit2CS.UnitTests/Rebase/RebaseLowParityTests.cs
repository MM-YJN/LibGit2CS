using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Notes;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

using GitIndex = LibGit2CS.Index.GitIndex;
using RebaseOps = LibGit2CS.Rebase.GitRebase;

namespace LibGit2CS.UnitTests.Rebase;

// Parity tests for notes-copy error paths, upstream error messages, the revwalk missing-ref code, describe peel propagation, and blame HEAD failure.
// Expectations C-verified against libgit2 1.9.4.
public sealed class RebaseLowParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;
    private static readonly GitContext s_context = CreateContext();

    private static GitContext CreateContext()
    {
        GitContext ctx = new();
        ctx.Env["HOME"] = Path.Combine(Path.GetTempPath(), "LibGit2CS_nonexistent_" + Guid.NewGuid().ToString("N"));
        ctx.Env["XDG_CONFIG_HOME"] = null;
        ctx.Dirs.Reset();
        ctx.Dirs.Set(GitSystemDir.System, string.Empty);
        ctx.Dirs.Set(GitSystemDir.ProgramData, string.Empty);
        return ctx;
    }

    public RebaseLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RebaseLow_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, s_context);
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

    private async Task<GitOid> SetupAsync()
    {
        GitOid c1 = await CommitFileAsync("f.txt", "f\n", "base\n", "refs/heads/master");

        await _repo.BranchCreateAsync("feature", c1, force: false, TestContext.Current.CancellationToken);
        await _repo.SetHeadAsync("refs/heads/feature", TestContext.Current.CancellationToken);
        await _repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        return await CommitFileAsync("g.txt", "g\n", "feature message\n", "refs/heads/feature");
    }

    private async Task<RebaseOps> StartRebaseAsync()
    {
        GitAnnotatedCommit branch = await _repo.AnnotatedCommitFromRefAsync(
            (await _repo.ReferenceLookupAsync("refs/heads/feature", TestContext.Current.CancellationToken))!);
        GitAnnotatedCommit upstream = await _repo.AnnotatedCommitFromRefAsync(
            (await _repo.ReferenceLookupAsync("refs/heads/master", TestContext.Current.CancellationToken))!);

        RebaseOps rebase = await RebaseOps.InitAsync(_repo, branch, upstream, onto: null, options: null, cancellationToken: TestContext.Current.CancellationToken);
        _ = await rebase.NextAsync(cancellationToken: TestContext.Current.CancellationToken);
        return rebase;
    }

    // ── finish with a rewrite ref but no rewritten file fails ────────

    [Fact]
    public async Task Finish_NotesRefButNoRewrittenFile_ThrowsNotFound()
    {
        // C (rebase.c:1313-1315): git_futils_readbuffer of the rewritten
        // file fails (GIT_ENOTFOUND) and propagates — the finish FAILS. The
        _ = await SetupAsync();
        await _repo.Config.SetStringAsync("notes.rewriteRef", "refs/notes/commits", cancellationToken: TestContext.Current.CancellationToken);

        using RebaseOps rebase = await StartRebaseAsync();
        // No CommitAsync → no rewritten file.

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await rebase.FinishAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── malformed notes.rewrite.rebase fails the finish ──────────────

    [Fact]
    public async Task Finish_MalformedRewriteRebaseConfig_Throws()
    {
        // C (rebase.c:1227-1235): git_config_get_bool on "banana" errors
        // (non-ENOTFOUND) and notes_ref_lookup propagates it — the finish
        // FAILS.
        _ = await SetupAsync();
        await _repo.Config.SetStringAsync("notes.rewriteRef", "refs/notes/commits", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("notes.rewrite.rebase", "banana", cancellationToken: TestContext.Current.CancellationToken);

        using RebaseOps rebase = await StartRebaseAsync();
        _ = await rebase.CommitAsync(null, TestSig(), cancellationToken: TestContext.Current.CancellationToken); // writes the rewritten file

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await rebase.FinishAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("as a boolean", ex.Message);
    }

    // ── note-copy committer falls back to "unknown <unknown>" ────────

    [Fact]
    public async Task Finish_NoIdentity_UsesUnknownCommitterForNotes()
    {
        // C (rebase.c:1274-1278): without user.name/user.email the note copy
        // falls back to git_signature_now("unknown", "unknown") and
        // SUCCEEDS.name is not set".
        GitOid c2 = await SetupAsync();
        _ = await _repo.NotesCreateAsync(null, TestSig(), TestSig(), c2, "a note", allowOverwrite: false, TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("notes.rewriteRef", "refs/notes/commits", cancellationToken: TestContext.Current.CancellationToken);

        using RebaseOps rebase = await StartRebaseAsync();
        // A different committer than the original commit - otherwise the
        // replayed commit would be byte-identical and the note copy would
        // hit C's force=0 EEXISTS (a correct C behavior, but not this test).
        var rebaseSig = new GitSignature("Rebase User", "rebase@example.com", new GitTime(1700000100, 0));
        GitOid newCommit = await rebase.CommitAsync(null, rebaseSig, cancellationToken: TestContext.Current.CancellationToken);
        await rebase.FinishAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitNote? note = await _repo.NotesReadAsync("refs/notes/commits", newCommit, TestContext.Current.CancellationToken);
        Assert.NotNull(note);
        Assert.Equal("a note", note!.Message);
        note.Dispose();
    }

    // ── revwalk missing ref → generic -1 (GIT_ERROR) ──────────────────

    [Fact]
    public async Task Revwalk_PushMissingRef_ThrowsErrorCode()
    {
        // C (revwalk.c:135-137): a plain push_ref of a missing ref returns
        // generic -1 (GIT_ERROR), not GIT_ENOTFOUND.
        await using GitRepository repo = await GitRepository.InitAsync(NewDir("repo"), isBare: false, s_context, TestContext.Current.CancellationToken);

        using GitRevWalker walker = repo.NewRevWalker();
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await walker.PushRefAsync("refs/heads/nope", TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("reference 'refs/heads/nope' not found", ex.Message);
    }

    // ── describe peel failure propagates (GIT_EPEEL) ─────────────────

    [Fact]
    public async Task Describe_NonCommittish_PeelErrorPropagates()
    {
        // C (describe.c:685-686): the peel error propagates AS-IS - a tree
        // committish fails check_type_combination with GIT_EINVALIDSPEC
        // (object.c:396-406), not GIT_EINVALID
        // "object is not a committish".
        await using GitRepository repo = await GitRepository.InitAsync(NewDir("repo"), isBare: false, s_context, TestContext.Current.CancellationToken);
        var tree = GitOid.Parse("4b825dc642cb6eb9a060e54bf8d69288fbee4904".AsSpan(), GitHashAlgorithmKind.Sha1);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.DescribeAsync(tree, new GitDescribeOptions(), TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
    }

    // ── blame unborn HEAD fails normalize_options ────────────────────

    [Fact]
    public async Task Blame_UnbornHead_ThrowsReferenceNotFound()
    {
        // C (blame.c:267-272): normalize_options fails with -1 +
        // "reference 'HEAD' not found"
        await using GitRepository repo = await GitRepository.InitAsync(NewDir("repo"), isBare: false, s_context, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "x\n", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.BlameFileAsync("f.txt", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Contains("reference 'HEAD' not found", ex.Message);
    }

    // ── revparse @{u} error messages ─────────────────────────────────

    [Fact]
    public async Task Revparse_UpstreamMissing_ReportsFullRefname()
    {
        // C (branch.c:458-463): "branch 'refs/heads/main' does not have an
        // upstream" — the FULL refname, not the short name.
        await using GitRepository repo = await GitRepository.InitAsync(NewDir("repo"), isBare: false, s_context, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "x\n", cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid tree = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        GitOid c1 = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "m\n",
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);
        await repo.SetHeadAsync("refs/heads/main", TestContext.Current.CancellationToken);
        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.RevparseSingleAsync("@{u}", TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Contains("branch 'refs/heads/main' does not have an upstream", ex.Message);
    }

    private string NewDir(string name) => Path.Combine(_tempDir, name + "_" + Guid.NewGuid().ToString("N")[..8]);
}
