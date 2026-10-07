using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using RebaseOps = LibGit2CS.Rebase.GitRebase;
using RebaseOpts = LibGit2CS.Rebase.GitRebaseOptions;

namespace LibGit2CS.UnitTests.Rebase;

/// <summary>
/// Parity regression tests for behaviors in
/// libgit2 1.9.4.
/// C reference: rebase.c, commit.c.
/// </summary>
public sealed class RebaseMedParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public RebaseMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RebaseMed_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitOid> CommitFileAsync(string fileName, string content, string message, string refName, GitOid? parent = null)
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is not null ? [parent.Value] : [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = refName,
        }, TestContext.Current.CancellationToken);
    }

    // ── rebase open "not supported" uses C's -1, not the invented -39 ──

    [Fact]
    public async Task Open_InteractiveRebase_ThrowsGenericError()
    {
        // C (rebase.c:373-376, 380-383): GIT_REBASE_INTERACTIVE and
        // GIT_REBASE_APPLY return a generic -1 (GIT_ERROR) with class
        // GIT_ERROR_REBASE — NOT the invented GIT_ENOTSUPPORTED code.
        GitOid c1 = await CommitFileAsync("a.txt", "a\n", "c1\n", "refs/heads/master");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        // Fake an interactive rebase state: rebase-merge/ + interactive +
        // the state files OpenAsync reads before dispatching.
        string rebaseMerge = Path.Combine(_repo.Path, "rebase-merge");
        Directory.CreateDirectory(rebaseMerge);
        await File.WriteAllTextAsync(Path.Combine(rebaseMerge, "interactive"), string.Empty, cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(rebaseMerge, "head-name"), "refs/heads/master\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(rebaseMerge, "orig-head"), $"{c1}\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(rebaseMerge, "onto"), $"{c1}\n", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await RebaseOps.OpenAsync(_repo, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("interactive rebase is not supported", ex.Message);
    }

    [Fact]
    public async Task Open_ApplyRebase_ThrowsGenericError()
    {
        GitOid c1 = await CommitFileAsync("a.txt", "a\n", "c1\n", "refs/heads/master");
        await _repo.SetHeadAsync("refs/heads/master", TestContext.Current.CancellationToken);

        // Fake a patch-application rebase: rebase-apply/ + state files.
        string rebaseApply = Path.Combine(_repo.Path, "rebase-apply");
        Directory.CreateDirectory(rebaseApply);
        await File.WriteAllTextAsync(Path.Combine(rebaseApply, "head-name"), "refs/heads/master\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(rebaseApply, "orig-head"), $"{c1}\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(rebaseApply, "onto"), $"{c1}\n", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await RebaseOps.OpenAsync(_repo, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("patch application rebase is not supported", ex.Message);
    }

    // ── rebase dirtiness workdir diff forces submodule-ignore=UNTRACKED ──

    [Fact]
    public async Task Init_SubmoduleWithUntrackedFiles_NotDirty()
    {
        // C (rebase.c:552-555): the index→workdir diff for the dirtiness
        // check runs with diff_opts.ignore_submodules =
        // GIT_SUBMODULE_IGNORE_UNTRACKED — untracked files inside a
        // submodule must NOT make the workdir "dirty".
        string workdir = _repo.Workdir!;

        // Submodule repo inside the parent workdir.
        string subDir = Path.Combine(workdir, "sub");
        Directory.CreateDirectory(subDir);
        GitOid subHead;
        await using (GitRepository subRepo = await GitRepository.InitAsync(subDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken))
        {
            await CommitFileInRepoAsync(subRepo, "s.txt", "s\n", "sub c1\n", "refs/heads/master");
            subHead = ((GitDirectReference)(await subRepo.ReferenceResolveAsync("refs/heads/master", TestContext.Current.CancellationToken))!).Target;
        }

        // Main repo: commit f.txt + .gitmodules + gitlink "sub" → c1.
        await File.WriteAllTextAsync(Path.Combine(workdir, ".gitmodules"), "[submodule \"sub\"]\n\tpath = sub\n\turl = https://example.com/sub.git\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workdir, "f.txt"), "f\n", cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(".gitmodules", TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        idx.Add(new LibGit2CS.Index.GitIndexEntry("sub", subHead, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        GitOid c1 = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "c1 with submodule\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        // Feature branch: c2 modifies f.txt.
        await _repo.BranchCreateAsync("feature", c1, force: false, TestContext.Current.CancellationToken);
        await _repo.SetHeadAsync("refs/heads/feature", TestContext.Current.CancellationToken);
        await _repo.CheckoutHeadAsync(new LibGit2CS.Checkout.GitCheckoutOptions { Strategy = LibGit2CS.Checkout.GitCheckoutStrategy.Force }, TestContext.Current.CancellationToken);
        _ = await CommitFileAsync("f.txt", "f modified\n", "c2\n", "refs/heads/feature", c1);

        // Untracked file inside the submodule workdir.
        await File.WriteAllTextAsync(Path.Combine(subDir, "untracked.txt"), "junk\n", cancellationToken: TestContext.Current.CancellationToken);

        GitAnnotatedCommit branch = await _repo.AnnotatedCommitFromRefAsync(
            (await _repo.ReferenceLookupAsync("refs/heads/feature", TestContext.Current.CancellationToken))!, TestContext.Current.CancellationToken);
        GitAnnotatedCommit upstream = await _repo.AnnotatedCommitFromRefAsync(
            (await _repo.ReferenceLookupAsync("refs/heads/master", TestContext.Current.CancellationToken))!, TestContext.Current.CancellationToken);

        // C: with IGNORE_UNTRACKED the submodule is clean → rebase init
        // proceeds. (A "unstaged changes exist in workdir" throw would fail.)
        using RebaseOps rebase = await RebaseOps.InitAsync(_repo, branch, upstream, onto: null,
            options: new RebaseOpts(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(rebase.OperationCount >= 1);
    }

    private static async Task<GitOid> CommitFileInRepoAsync(GitRepository repo, string fileName, string content, string message, string refName)
    {
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        LibGit2CS.Index.GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = refName,
        }, TestContext.Current.CancellationToken);
    }

    // ── git_commit_summary preserves non-newline whitespace runs ──

    [Fact]
    public async Task CommitSummary_PreservesWhitespaceRuns()
    {
        // C (commit.c:601-655): whitespace runs WITHOUT a newline are copied
        // verbatim ("foo   bar" stays); runs containing a newline collapse to
        // a single space; the summary stops at a newline followed by a
        // whitespace-only line. Expected values probe-verified against
        // libgit2's git_commit_summary.
        (string message, string expected)[] cases =
        [
            ("foo   bar\n\nbody\n", "foo   bar"),
            ("foo\tbar  baz\n", "foo\tbar  baz"),
            ("line1\n  \nline2\n", "line1"),
            ("  leading spaces\n", "  leading spaces"),
            ("one\ntwo\nthree\n", "one two three"),
        ];

        GitOid? prev = null;
        foreach ((string message, string expected) in cases)
        {
            GitOid c = await CommitFileAsync("msg.txt", "x\n", message, "refs/heads/master", prev);
            prev = c;
            Commit? commit = await _repo.ObjectLookupAsync<Commit>(c, TestContext.Current.CancellationToken);
            Assert.NotNull(commit);
            Assert.Equal(expected, commit!.Summary);
        }
    }
}
