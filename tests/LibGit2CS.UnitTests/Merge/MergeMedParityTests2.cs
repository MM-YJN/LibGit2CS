using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Merge;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Reset;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Merge;

/// <summary>
/// Regression tests for the merge and checkout parity behaviors,
/// in libgit2 1.9.4.
/// Expectations C-verified against libgit2 1.9.4.
/// </summary>
public sealed class MergeMedParityTests2 : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;
    private string Workdir => _repo.Workdir!;

    public MergeMedParityTests2()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_MergeMed2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
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

    private async Task<GitOid> CommitFileAsync(string name, string content, string refName = "refs/heads/master")
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        string full = Path.Combine(Workdir, name);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(full, content, cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(name, cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);

        List<GitOid> parents = [];
        if (await _repo.ReferenceResolveAsync(refName, TestContext.Current.CancellationToken) is GitDirectReference tipRef)
        {
            parents.Add(tipRef.Target);
        }

        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "commit " + name + "\n",
            UpdateRef = refName,
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Like <see cref="CommitFileAsync(string, string, string)"/> but chains
    /// the new commit onto <paramref name="parent"/> (a real branch point so
    /// the merge base is the parent, not empty).
    /// </summary>
    private async Task<GitOid> CommitFileWithParentAsync(string name, string content, string refName, GitOid parent)
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);
        string full = Path.Combine(Workdir, name);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(full, content, cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(name, cancellationToken: TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);

        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [parent],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "commit " + name + "\n",
            UpdateRef = refName,
        }, TestContext.Current.CancellationToken);
    }

    private async Task<GitAnnotatedCommit> AnnotatedAtRefAsync(string refName)
    {
        GitReference reference = await _repo.ReferenceLookupAsync(refName, TestContext.Current.CancellationToken)
            ?? throw new GitException(GitErrorCode.NotFound, $"branch '{refName}' not found", GitErrorCategory.Reference);
        return await _repo.AnnotatedCommitFromRefAsync(reference);
    }

    // ── git_merge default checkout strategy (no AllowConflicts) ───────

    /// <summary>
    /// the default merge checkout strategy is SAFE|DONT_WRITE_INDEX
    /// (merge.c:3354-3358 + index.c:3885-3893) — NOT AllowConflicts. A
    /// workdir conflict that reaches checkout (not pre-empted by
    /// git_merge__check_result — here an untracked blocker FILE at a path the
    /// merge turns into a directory) aborts with GIT_ECONFLICT "1 conflict
    /// prevents checkout". C-verified (probe, 1.9.4: rc=-13). The default must
    /// not be Safe|AllowConflicts|DontWriteIndex, which would swallow the
    /// conflict.
    /// </summary>
    [Fact]
    public async Task Merge_UntrackedBlocker_Conflict()
    {
        await CommitFileAsync("keep.txt", "k\n");
        GitOid feature = await CommitFileAsync("d/x", "dx\n", "refs/heads/feature");

        // Reset to master: HEAD stays at master with a clean index/workdir,
        // then place an untracked blocker FILE at "d" where the merge wants
        // to create directory "d/".
        Commit? masterCommit = await _repo.ObjectLookupAsync<Commit>(((GitDirectReference)(await _repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target, TestContext.Current.CancellationToken);
        await _repo.ResetAsync(masterCommit!, GitResetMode.Hard, cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(Workdir, "d"), "BLOCKER\n", cancellationToken: TestContext.Current.CancellationToken);

        using GitAnnotatedCommit theirHead = await _repo.AnnotatedCommitLookupAsync(feature, TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.MergeAsync([theirHead], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Conflict, ex.Code);
        Assert.Equal("1 conflict prevents checkout", ex.Message);
    }

    // ── "can only merge a single branch" error code ───────────────────

    /// <summary>
    /// git_merge_analysis_for_ref with != 1 heads returns -1 (GIT_ERROR,
    /// category MERGE) — merge.c:3263-3267, not
    /// GitErrorCode.Invalid (-21).
    /// </summary>
    [Fact]
    public async Task MergeAnalyzeForRef_MultipleHeads_ErrorCode()
    {
        await CommitFileAsync("base.txt", "base\n");
        await CommitFileAsync("one.txt", "1\n", "refs/heads/one");
        await CommitFileAsync("two.txt", "2\n", "refs/heads/two");

        GitReference headRef = (await _repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!;
        using GitAnnotatedCommit one = await _repo.AnnotatedCommitLookupAsync(((GitDirectReference)(await _repo.ReferenceResolveAsync("refs/heads/one", TestContext.Current.CancellationToken))!).Target, TestContext.Current.CancellationToken);
        using GitAnnotatedCommit two = await _repo.AnnotatedCommitLookupAsync(((GitDirectReference)(await _repo.ReferenceResolveAsync("refs/heads/two", TestContext.Current.CancellationToken))!).Target, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.MergeAnalyzeForRefAsync(headRef, [one, two], TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code); // -1, not GIT_EINVALID (-21)
        Assert.Equal("can only merge a single branch", ex.Message);
        Assert.Equal(GitErrorCategory.Merge, ex.Category);
    }

    // ── bare-repo error messages ─────────────────────────────────────

    /// <summary>
    /// git_repository__ensure_not_bare (repository.h:216-229) formats
    /// "cannot %s. This operation is not allowed against bare repositories.".
    /// </summary>
    [Fact]
    public async Task BareRepo_Messages()
    {
        string bareDir = Path.Combine(_tempDir, "bare");
        Directory.CreateDirectory(bareDir);
        await using GitRepository bare = await GitRepository.InitAsync(bareDir, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        // Seed a commit so annotated-commit/commit lookups work on the bare repo.
        GitOid blob = await bare.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes("x\n"), TestContext.Current.CancellationToken);
        using (GitTreeBuilder tb = bare.NewTreeBuilder())
        {
            await tb.InsertAsync("f.txt", blob, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid tree = await tb.WriteAsync(TestContext.Current.CancellationToken);
            await bare.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = tree,
                Author = TestSig(),
                Committer = TestSig(),
                Message = "seed\n",
                UpdateRef = "refs/heads/master",
            }, TestContext.Current.CancellationToken);
        }

        GitOid headId = ((GitDirectReference)(await bare.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target;
        Commit? commit = await bare.ObjectLookupAsync<Commit>(headId, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);

        // merge
        using (GitAnnotatedCommit their = await bare.AnnotatedCommitLookupAsync(headId, TestContext.Current.CancellationToken))
        {
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await bare.MergeAsync([their], cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(GitErrorCode.BareRepo, ex.Code);
            Assert.Equal("cannot merge. This operation is not allowed against bare repositories.", ex.Message);
        }

        // cherry-pick
        GitException cp = await Assert.ThrowsAsync<GitException>(async () =>
            await bare.CherryPickAsync(commit!, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.BareRepo, cp.Code);
        Assert.Equal("cannot cherry-pick. This operation is not allowed against bare repositories.", cp.Message);

        // revert
        GitException rv = await Assert.ThrowsAsync<GitException>(async () =>
            await bare.RevertAsync(commit!, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.BareRepo, rv.Code);
        Assert.Equal("cannot revert. This operation is not allowed against bare repositories.", rv.Message);

        // checkout
        GitException co = await Assert.ThrowsAsync<GitException>(async () =>
            await bare.CheckoutHeadAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.BareRepo, co.Code);
        Assert.Equal("cannot checkout. This operation is not allowed against bare repositories.", co.Message);
    }

    // ── cherry-pick/revert option normalization ──────────────────────

    /// <summary>
    /// cherrypick_normalize_opts (cherrypick.c:87-95) fills the default
    /// strategy/labels ONLY when unset — user-provided labels and strategies
    /// are preserved (AllowConflicts is not ORed in and labels are not
    /// clobbered).
    /// </summary>
    [Fact]
    public async Task CherryPick_CustomLabelsPreserved()
    {
        // base: file1.txt "base"; pick commit changes file1.txt to "pick".
        await CommitFileAsync("file1.txt", "base\n");
        GitOid pick = await CommitFileAsync("file1.txt", "pick\n", "refs/heads/pick");

        // HEAD back on master base via a second conflicting change on master.
        await CommitFileAsync("file1.txt", "master\n");

        Commit? pickCommit = await _repo.ObjectLookupAsync<Commit>(pick, TestContext.Current.CancellationToken);
        Assert.NotNull(pickCommit);

        var opts = new GitCherryPickOptions
        {
            CheckoutOptions = new GitCheckoutOptions
            {
                Strategy = GitCheckoutStrategy.Force,
                OurLabel = "MY-LABEL",
                TheirLabel = "THEIR-LABEL",
            },
        };

        await _repo.CherryPickAsync(pickCommit!, opts, cancellationToken: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(Path.Combine(Workdir, "file1.txt"), TestContext.Current.CancellationToken);
        Assert.Contains("<<<<<<< MY-LABEL", content);
        Assert.Contains(">>>>>>> THEIR-LABEL", content);
        Assert.DoesNotContain("<<<<<<< HEAD", content);
    }

    /// <summary>
    /// with no user strategy, the cherry-pick default (ALLOW_CONFLICTS)
    /// is applied — matches cherrypick.c:87-88.
    /// </summary>
    [Fact]
    public async Task CherryPick_DefaultStrategy_AllowConflicts()
    {
        await CommitFileAsync("file1.txt", "base\n");
        GitOid pick = await CommitFileAsync("file1.txt", "pick\n", "refs/heads/pick");
        await CommitFileAsync("file1.txt", "master\n");

        Commit? pickCommit = await _repo.ObjectLookupAsync<Commit>(pick, TestContext.Current.CancellationToken);
        await _repo.CherryPickAsync(pickCommit!, cancellationToken: TestContext.Current.CancellationToken);

        // Default labels are applied: ours="HEAD", theirs="<7>... summary".
        string content = await File.ReadAllTextAsync(Path.Combine(Workdir, "file1.txt"), TestContext.Current.CancellationToken);
        Assert.Contains("<<<<<<< HEAD", content);
        Assert.Contains(">>>>>>> " + pick.ToString()[..7] + "...", content);
    }

    // ── failed merge/cherry-pick/revert leaves the index untouched ───

    /// <summary>
    /// git_merge writes the repo index only on success via
    /// git_indexwriter_commit (merge.c:3387); a failing checkout leaves the
    /// on-disk index untouched (state files are cleaned, merge.c:3390-3391) —
    /// the merged index is not written to disk before checkout.
    /// </summary>
    [Fact]
    public async Task FailedMerge_LeavesIndexOnDiskUntouched()
    {
        await CommitFileAsync("keep.txt", "k\n");
        GitOid feature = await CommitFileAsync("d/x", "dx\n", "refs/heads/feature");

        // Reset to master (clean index/workdir), then block the merge checkout
        // (see).
        Commit? masterCommit = await _repo.ObjectLookupAsync<Commit>(((GitDirectReference)(await _repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target, TestContext.Current.CancellationToken);
        await _repo.ResetAsync(masterCommit!, GitResetMode.Hard, cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(Workdir, "d"), "BLOCKER\n", cancellationToken: TestContext.Current.CancellationToken);

        using GitAnnotatedCommit theirHead = await _repo.AnnotatedCommitLookupAsync(feature, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.MergeAsync([theirHead], cancellationToken: TestContext.Current.CancellationToken));

        // The on-disk index must not contain the merged "d/x" entry.
        string indexPath = Path.Combine(_repo.Path, "index");
        using GitIndex onDisk = await GitIndex.OpenAsync(indexPath, _repo.ObjectFormat, TestContext.Current.CancellationToken);
        Assert.Null(onDisk.EntryByPath("d/x"));
        Assert.NotNull(onDisk.EntryByPath("keep.txt"));

        // State files were cleaned up.
        Assert.False(File.Exists(Path.Combine(_repo.Path, "MERGE_HEAD")));
        Assert.False(File.Exists(Path.Combine(_repo.Path, "MERGE_MSG")));
    }

    /// <summary>
    /// a successful merge still writes the merged index (with conflict
    /// stages) to disk.
    /// </summary>
    [Fact]
    public async Task SuccessfulConflictMerge_WritesIndex()
    {
        await CommitFileAsync("f.txt", "base\n");
        GitOid baseTip = ((GitDirectReference)(await _repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target;
        GitOid feature = await CommitFileWithParentAsync("f.txt", "theirs\n", "refs/heads/feature", baseTip);
        await CommitFileAsync("f.txt", "ours\n");

        using GitAnnotatedCommit theirHead = await _repo.AnnotatedCommitLookupAsync(feature, TestContext.Current.CancellationToken);
        await _repo.MergeAsync([theirHead], cancellationToken: TestContext.Current.CancellationToken);

        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        Assert.True(idx.HasConflicts);
        Assert.NotNull(idx.EntryByPath("f.txt", stage: 1));
        Assert.NotNull(idx.EntryByPath("f.txt", stage: 2));
        Assert.NotNull(idx.EntryByPath("f.txt", stage: 3));
    }

    /// <summary>
    /// the cherry-pick default strategy is ALLOW_CONFLICTS, so the
    /// blocker-turned conflict is swallowed (checkout.c:1776-1781) and the
    /// operation SUCCEEDS — but the repo index committed by
    /// git_indexwriter_commit (cherrypick.c:209) does NOT contain the
    /// un-checked-out "d/x" entry (C-verified probe, 1.9.4: rc=0, index
    /// lacks d/x). The merged index is written only AFTER a successful
    /// checkout, so a skipped workdir write never leaves d/x on disk.
    /// </summary>
    [Fact]
    public async Task CherryPickBlocker_IndexDoesNotContainSkippedEntry()
    {
        await CommitFileAsync("keep.txt", "k\n");
        GitOid pick = await CommitFileAsync("d/x", "dx\n", "refs/heads/pick");

        // Reset to master (clean index/workdir), then block the checkout.
        Commit? masterCommit = await _repo.ObjectLookupAsync<Commit>(((GitDirectReference)(await _repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!).Target, TestContext.Current.CancellationToken);
        await _repo.ResetAsync(masterCommit!, GitResetMode.Hard, cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(Workdir, "d"), "BLOCKER\n", cancellationToken: TestContext.Current.CancellationToken);

        Commit? pickCommit = await _repo.ObjectLookupAsync<Commit>(pick, TestContext.Current.CancellationToken);
        await _repo.CherryPickAsync(pickCommit!, cancellationToken: TestContext.Current.CancellationToken); // must NOT throw

        string indexPath = Path.Combine(_repo.Path, "index");
        using GitIndex onDisk = await GitIndex.OpenAsync(indexPath, _repo.ObjectFormat, TestContext.Current.CancellationToken);
        Assert.Null(onDisk.EntryByPath("d/x"));
        Assert.NotNull(onDisk.EntryByPath("keep.txt"));
    }
}
