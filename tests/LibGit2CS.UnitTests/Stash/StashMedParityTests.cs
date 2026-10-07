using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Stash;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.Stash;

/// <summary>
/// Regression tests for the stash parity behaviors (paths "no
/// changes" check, stage_new_file parent entry, reinstate-index
/// equal-trees guard) in
/// libgit2 1.9.4.
/// Expectations C-verified against libgit2 1.9.4.
/// </summary>
public sealed class StashMedParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public StashMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StashMedParity_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitOid> CommitFileAsync(string fileName, string content)
    {
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);

        // C (commit.c:113-117): a commit created with update_ref must have
        // the current tip as its first parent — chain onto it.
        List<GitOid> parents = [];
        if (await _repo.ReferenceResolveAsync("refs/heads/master", TestContext.Current.CancellationToken) is LibGit2CS.Refs.GitDirectReference tipRef)
        {
            parents.Add(tipRef.Target);
        }

        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "c\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    // ── stash-with-paths "no changes" check ────────────────────────

    [Fact]
    public async Task Stash_Paths_MixedChangedUnchanged_ThrowsNotFound()
    {
        // (stash.c:602-648, has_changes_cb): the callback returns
        // GIT_ENOTFOUND on the FIRST CURRENT entry — `stash push -- a b` with
        // b unmodified fails ("cannot stash changes - one of the files does
        // not have any changes to stash.") even though a has changes
        // (C-verified quirk reproduced here).
        _ = await CommitFileAsync("a.txt", "a1\n");
        _ = await CommitFileAsync("b.txt", "b1\n");
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "a.txt"), "a2\n", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.StashSaveWithOptsAsync(new GitStashSaveOptions
            {
                Stasher = TestSig(),
                Flags = GitStashFlags.Default,
                Paths = ["a.txt", "b.txt"],
            }, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Contains("one of the files does not have any changes to stash", ex.Message);
    }

    [Fact]
    public async Task Stash_Paths_AllChanged_Proceeds()
    {
        // positive: with no CURRENT entries the paths stash succeeds.
        _ = await CommitFileAsync("a.txt", "a1\n");
        _ = await CommitFileAsync("b.txt", "b1\n");
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "a.txt"), "a2\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "b.txt"), "b2\n", cancellationToken: TestContext.Current.CancellationToken);

        GitOid stashOid = await _repo.StashSaveWithOptsAsync(new GitStashSaveOptions
        {
            Stasher = TestSig(),
            Flags = GitStashFlags.Default,
            Paths = ["a.txt", "b.txt"],
        }, TestContext.Current.CancellationToken);
        Assert.False(stashOid.IsZero);
    }

    // ── stage_new_file prefers the parent entry ────────────────────

    [Fact]
    public async Task Apply_IndexKeepsParentContent_ForStashedModifiedFile()
    {
        // (stash.c:994-1002, stage_new_file): for a file present in both
        // the stash parent tree and the stash tree, the PARENT entry is
        // staged (theirs == ancestor in merge_indexes) — after a plain
        // `git stash apply` the repo index keeps the pre-apply content while
        // the workdir holds the stashed content.
        _ = await CommitFileAsync("f.txt", "v1\n");
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "f.txt"), "v2\n", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, TestContext.Current.CancellationToken);
        await _repo.StashApplyAsync(0, cancellationToken: TestContext.Current.CancellationToken);

        // Workdir restored to the stashed content.
        Assert.Equal("v2\n", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "f.txt"), TestContext.Current.CancellationToken));

        // Index holds the PARENT (pre-apply) content, not the stash's v2.
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        GitIndexEntry? entry = idx.EntryByPath("f.txt");
        Assert.NotNull(entry);
        GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(entry!.Value.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        Assert.Equal("v1\n", System.Text.Encoding.UTF8.GetString(blob!.Raw.Span));
    }

    // ── reinstate-index with equal trees leaves the index alone ────

    [Fact]
    public async Task Apply_ReinstateIndex_EqualTrees_LeavesIndexUntouched()
    {
        // (stash.c:1082-1104): with REINSTATE_INDEX and the stash parent
        // tree equal to the stash index tree, NEITHER the merge-index branch
        // NOR the stage+merge branch runs — unstashed_index stays NULL and
        // the repo index is left untouched (the untracked file is restored
        // to the workdir but not staged).
        _ = await CommitFileAsync("f.txt", "v1\n");
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "f.txt"), "v2\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "untracked.txt"), "u\n", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.StashSaveAsync(TestSig(), null, GitStashFlags.IncludeUntracked, TestContext.Current.CancellationToken);
        await _repo.StashApplyAsync(0, new GitStashApplyOptions
        {
            Flags = GitStashApplyFlags.ReinstateIndex,
        }, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_repo.Workdir!, "untracked.txt")));
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        Assert.Null(idx.EntryByPath("untracked.txt"));
    }
}
