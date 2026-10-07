using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;

using GitFileMode = LibGit2CS.Objects.GitFileMode;
using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.IntegrationTests.Submodule;

/// <summary>
/// End-to-end tests for the submodule parity behaviors (stale oids,
/// default ignore, combined flags, WD_UNTRACKED, name != path)
/// in libgit2 1.9.4.
/// Expectations C-verified against libgit2 1.9.4.
/// </summary>
public sealed class SubmoduleParityIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public SubmoduleParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SubmoduleParityInt_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static async Task<GitOid> SetupSubmoduleAsync(GitRepository repo, string name, string path)
    {
        string subDir = Path.Combine(repo.Workdir!, path);
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

        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, ".gitmodules"),
            $"[submodule \"{name}\"]\n    path = {path}\n    url = https://example.com/{name}.git\n",
            cancellationToken: TestContext.Current.CancellationToken);
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry(path, subOid, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "add submodule\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
        return subOid;
    }

    private static async Task CommitInSubmoduleAsync(GitRepository repo, string path)
    {
        string subDir = Path.Combine(repo.Workdir!, path);
        await using GitRepository subRepo = await GitRepository.OpenAsync(subDir, new GitContext(), TestContext.Current.CancellationToken);
        GitIndex subIdx = await subRepo.GetIndexAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(subDir, "sub.txt"), "changed\n", cancellationToken: TestContext.Current.CancellationToken);
        await subIdx.AddByPathAsync("sub.txt", TestContext.Current.CancellationToken);
        await subIdx.WriteAsync(TestContext.Current.CancellationToken);
        GitOid subTree = await subIdx.WriteTreeAsync(TestContext.Current.CancellationToken);
        // C (commit.c:109-117): with update_ref, parent[0] must equal the
        // ref tip — chain onto the submodule's current tip (SetupSubmoduleAsync
        // already committed the init commit on refs/heads/master).
        GitReference? tip = await subRepo.ReferenceLookupAsync("refs/heads/master", TestContext.Current.CancellationToken);
        GitOid[] parents = tip is GitDirectReference direct ? [direct.Target] : [];
        await subRepo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = subTree,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "sub commit\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Status_MovedHead_Dirty_Untracked_CombinedLikeC()
    {
        // End-to-end: a submodule whose HEAD moved, with a dirty
        // tracked file and an untracked file, reports WD_MODIFIED |
        // WD_WD_MODIFIED | WD_UNTRACKED (C-verified: 0x340f).
        string path = Path.Combine(_tempDir, "r");
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);
        _ = await SetupSubmoduleAsync(repo, "sub", "sub");
        _ = await repo.SubmoduleStatusAsync("sub", cancellationToken: TestContext.Current.CancellationToken);

        await CommitInSubmoduleAsync(repo, "sub");
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "sub", "sub.txt"), "dirty\n", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "sub", "untracked.txt"), "u\n", cancellationToken: TestContext.Current.CancellationToken);

        SubmoduleStatus status = await repo.SubmoduleStatusAsync("sub", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.WdModified);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.WdWdModified);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.WdUntracked);
    }

    [Fact]
    public async Task Status_NameDiffersFromPath_ReactToStagedGitlink()
    {
        // name "lib" at path "libfoo" — the name-keyed status carries
        // index/head bits and reacts to a staged gitlink update.
        string path = Path.Combine(_tempDir, "name-differs-path");
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);
        _ = await SetupSubmoduleAsync(repo, "lib", "libfoo");

        SubmoduleStatus first = await repo.SubmoduleStatusAsync("lib", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual((SubmoduleStatus)0, first & SubmoduleStatus.InIndex);
        Assert.NotEqual((SubmoduleStatus)0, first & SubmoduleStatus.InHead);

        await CommitInSubmoduleAsync(repo, "libfoo");
        await using GitRepository subRepo = await GitRepository.OpenAsync(Path.Combine(repo.Workdir!, "libfoo"), ctx, TestContext.Current.CancellationToken);
        GitOid newHead = Assert.IsType<GitDirectReference>(await subRepo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken)).Target;
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        idx.Add(new GitIndexEntry("libfoo", newHead, GitFileMode.GitLink));
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        SubmoduleStatus status = await repo.SubmoduleStatusAsync("lib", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual((SubmoduleStatus)0, status & SubmoduleStatus.IndexModified);
    }
}
