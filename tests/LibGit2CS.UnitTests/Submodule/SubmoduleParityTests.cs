using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.Submodule;

/// <summary>
/// Regression tests for the submodule parity behaviors (stale oids,
/// default ignore, dropped dirty flags, WD_UNTRACKED, and name != path:
/// name != path) in libgit2 1.9.4.
/// Expectations C-verified against libgit2 1.9.4.
/// </summary>
public sealed class SubmoduleParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public SubmoduleParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SubmoduleParity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
        await WriteCommit();
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

    private async Task<GitOid> WriteCommit()
    {
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        string workdir = _repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello\n", cancellationToken: TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("README.md", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);

        // C (commit.c:113-117): a commit created with update_ref must have
        // the current tip as its first parent — chain onto it.
        List<GitOid> parentChain = [];
        if (await _repo.ReferenceResolveAsync("refs/heads/master", TestContext.Current.CancellationToken) is GitDirectReference tipRef)
        {
            parentChain.Add(tipRef.Target);
        }

        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parentChain,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "init\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    private void WriteGitmodules(string content)
    {
        File.WriteAllText(Path.Combine(_repo.Workdir!, ".gitmodules"), content);
    }

    /// <summary>
    /// Creates a submodule repo AT the parent workdir &lt;path&gt; (a real
    /// repo with its own .git), writes .gitmodules, and commits the gitlink
    /// into the parent index + HEAD.
    /// </summary>
    private async Task<GitOid> SetupSubmoduleAsync(string name, string path)
    {
        string subDir = Path.Combine(_repo.Workdir!, path);
        Directory.CreateDirectory(subDir);
        await using GitRepository subRepo = await GitRepository.InitAsync(subDir, isBare: false, new GitContext());
        GitIndex subIdx = await subRepo.GetIndexAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(subDir, "sub.txt"), "sub content\n", cancellationToken: TestContext.Current.CancellationToken);
        await subIdx.AddByPathAsync("sub.txt", TestContext.Current.CancellationToken);
        await subIdx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid subTree = await subIdx.WriteTreeAsync(TestContext.Current.CancellationToken);
        GitOid subOid = await subRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = subTree,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "sub init\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        WriteGitmodules($"[submodule \"{name}\"]\n    path = {path}\n    url = https://example.com/{name}.git\n");
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry(path, subOid, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);

        // C (commit.c:113-117): chain onto the current tip.
        List<GitOid> parentChain = [];
        if (await _repo.ReferenceResolveAsync("refs/heads/master", TestContext.Current.CancellationToken) is GitDirectReference tipRef)
        {
            parentChain.Add(tipRef.Target);
        }

        await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parentChain,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "add submodule\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);

        return subOid;
    }

    private async Task<GitOid> CommitInSubmoduleAsync(string path, string fileName, string content)
    {
        string subDir = Path.Combine(_repo.Workdir!, path);
        await using GitRepository subRepo = await GitRepository.OpenAsync(subDir, new GitContext(), TestContext.Current.CancellationToken);
        GitIndex subIdx = await subRepo.GetIndexAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(subDir, fileName), content, cancellationToken: TestContext.Current.CancellationToken);
        await subIdx.AddByPathAsync(fileName, TestContext.Current.CancellationToken);
        await subIdx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid subTree = await subIdx.WriteTreeAsync(TestContext.Current.CancellationToken);

        // C (commit.c:113-117): chain onto the current tip.
        List<GitOid> parentChain = [];
        if (await subRepo.ReferenceResolveAsync("refs/heads/master", TestContext.Current.CancellationToken) is GitDirectReference subTip)
        {
            parentChain.Add(subTip.Target);
        }

        return await subRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = subTree,
            Parents = parentChain,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "sub commit\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Status_SubmoduleHeadMoved_ReportsWdModified()
    {
        // C refreshes the wd oid on every status (git_submodule__open,
        // submodule.c:1622) — a commit inside the submodule must surface as
        // WD_MODIFIED on the NEXT status call (C-verified: 0x240f).
        _ = await SetupSubmoduleAsync("sub", "sub");
        SubmoduleStatus first = await _repo.SubmoduleStatusAsync("sub", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((SubmoduleStatus)0, first & SubmoduleStatus.WdModified);

        _ = await CommitInSubmoduleAsync("sub", "sub.txt", "changed\n");

        SubmoduleStatus status = await _repo.SubmoduleStatusAsync("sub", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.WdModified);
    }

    [Fact]
    public async Task Status_StagedGitlinkUpdate_ReportsIndexModified()
    {
        // C refreshes the index oid on every status (submodule_update_index,
        // submodule.c:1670) — a staged gitlink change must surface as
        // INDEX_MODIFIED (C-verified: 0x204f).
        _ = await SetupSubmoduleAsync("sub", "sub");
        SubmoduleStatus first = await _repo.SubmoduleStatusAsync("sub", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((SubmoduleStatus)0, first & SubmoduleStatus.IndexModified);

        GitOid newHead = await CommitInSubmoduleAsync("sub", "sub.txt", "changed\n");
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("sub", newHead, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        SubmoduleStatus status = await _repo.SubmoduleStatusAsync("sub", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.IndexModified);
    }

    [Fact]
    public async Task Status_DefaultIgnore_ReportsUntracked()
    {
        // the submodule's ignore defaults to NONE (submodule.c:1895), so
        // an untracked file in the submodule workdir yields WD_UNTRACKED even
        // without an ignore key in .gitmodules (C-verified).
        _ = await SetupSubmoduleAsync("sub", "sub");
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "sub", "untracked.txt"), "u\n", cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleStatus status = await _repo.SubmoduleStatusAsync("sub", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.WdUntracked);
    }

    [Fact]
    public async Task Status_IgnoreNone_ReportsUntracked()
    {
        // with ignore == NONE the index→workdir diff includes untracked
        // deltas (submodule.c:2396-2397); WD_UNTRACKED must be set.
        _ = await SetupSubmoduleAsync("sub", "sub");
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "sub", "untracked.txt"), "u\n", cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleStatus status = await _repo.SubmoduleStatusAsync("sub", SubmoduleIgnore.None, TestContext.Current.CancellationToken);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.WdUntracked);
    }

    [Fact]
    public async Task Status_MovedHeadAndDirtyContent_CombinesFlags()
    {
        // the oid-based wd flags combine with the dirty diffs — a moved
        // HEAD + dirty tracked file + untracked file reports
        // WD_MODIFIED|WD_WD_MODIFIED|WD_UNTRACKED (C-verified: 0x340f).
        _ = await SetupSubmoduleAsync("sub", "sub");
        _ = await _repo.SubmoduleStatusAsync("sub", cancellationToken: TestContext.Current.CancellationToken);

        _ = await CommitInSubmoduleAsync("sub", "sub.txt", "changed\n");
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "sub", "sub.txt"), "dirty\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "sub", "untracked.txt"), "u\n", cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleStatus status = await _repo.SubmoduleStatusAsync("sub", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.WdModified);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.WdWdModified);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.WdUntracked);
    }

    [Fact]
    public async Task Status_IgnoreUntracked_NoUntrackedFlag()
    {
        _ = await SetupSubmoduleAsync("sub", "sub");
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "sub", "untracked.txt"), "u\n", cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleStatus status = await _repo.SubmoduleStatusAsync("sub", SubmoduleIgnore.Untracked, TestContext.Current.CancellationToken);
        Assert.Equal((SubmoduleStatus)0, status & SubmoduleStatus.WdUntracked);
    }

    [Fact]
    public async Task Status_NameDiffersFromPath_IndexAndHeadFlags()
    {
        // the gitlink at path libfoo feeds the entry keyed by the
        // .gitmodules NAME "lib" (C namemap, submodule.c:513-522) — status of
        // "lib" carries IN_INDEX/IN_HEAD and reacts to a staged gitlink
        // update (C-verified: 0x240f, then 0x204f).
        _ = await SetupSubmoduleAsync("lib", "libfoo");
        SubmoduleStatus first = await _repo.SubmoduleStatusAsync("lib", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual((SubmoduleStatus)0, first & SubmoduleStatus.InIndex);
        Assert.NotEqual((SubmoduleStatus)0, first & SubmoduleStatus.InHead);

        GitOid newHead = await CommitInSubmoduleAsync("libfoo", "sub.txt", "changed\n");
        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("libfoo", newHead, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        SubmoduleStatus status = await _repo.SubmoduleStatusAsync("lib", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.IndexModified);
    }
}
