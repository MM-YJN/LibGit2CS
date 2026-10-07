using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Stash;
using LibGit2CS.Submodule;

namespace LibGit2CS.UnitTests.Submodule;

/// <summary> Parity tests for stash-rebase-submodule-worktree: (name-sorted submodule foreach), (default update strategy Checkout),
/// (set_fetch_recurse NO deletes the key), (empty stash message is not null). </summary>
public sealed class StashSubmoduleMedParityTests : IDisposable
{
    private readonly string _tempDir;

    public StashSubmoduleMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_StashSubmoduleMed_" + Guid.NewGuid().ToString("N")[..8]);
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

    private string NewDir()
    {
        string dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private static async ValueTask<GitRepository> InitRepoAsync(string dir)
        => await GitRepository.InitAsync(dir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);

    private static async Task<GitOid> CommitAllAsync(GitRepository repo, string message, GitOid? firstParent = null)
    {
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        GitOid tree = await idx.WriteTreeAsync(TestContext.Current.CancellationToken);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = firstParent is { } p ? [p] : [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = message + "\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    // ── submodule foreach is name-sorted ───────────────────────

    [Fact]
    public async Task SubmoduleForEach_NameSorted()
    {
        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);
        string workdir = repo.Workdir!;

        // Config-only submodules (no checkout needed for the cache).
        await File.WriteAllTextAsync(
            Path.Combine(workdir, ".gitmodules"),
            "[submodule \"zeta\"]\n\tpath = zeta\n\turl = https://example.com/z\n" +
            "[submodule \"alpha\"]\n\tpath = alpha\n\turl = https://example.com/a\n",
            TestContext.Current.CancellationToken);

        // C (submodule.c:685-707): the foreach snapshot is sorted ascending by name — Dictionary order is hash-bucket order.
        var names = new List<string>();
        await foreach (GitSubmodule sm in repo.SubmoduleForEachAsync(TestContext.Current.CancellationToken))
        {
            names.Add(sm.Name);
        }

        Assert.Equal(["alpha", "zeta"], names);
    }

    // ── default update strategy is Checkout ────────────────────

    [Fact]
    public async Task Submodule_DefaultUpdateStrategy_Checkout()
    {
        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);
        string workdir = repo.Workdir!;

        await File.WriteAllTextAsync(
            Path.Combine(workdir, ".gitmodules"),
            "[submodule \"sub\"]\n\tpath = sub\n\turl = https://example.com/s\n",
            TestContext.Current.CancellationToken);

        using GitSubmodule sm = await GitSubmodule.LookupAsync(repo, "sub", TestContext.Current.CancellationToken);

        // C (submodule.c:1896 + 1255-1261): no update key → CHECKOUT (1), never the Default sentinel.
        Assert.Equal(SubmoduleUpdateStrategy.Checkout, sm.UpdateStrategy);
    }

    // ── set_fetch_recurse(NO) deletes the key ──────────────────

    [Fact]
    public async Task Submodule_SetFetchRecurseNo_DeletesKey()
    {
        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);
        string workdir = repo.Workdir!;

        string gitmodules = Path.Combine(workdir, ".gitmodules");
        await File.WriteAllTextAsync(
            gitmodules,
            "[submodule \"sub\"]\n\tpath = sub\n\turl = https://example.com/s\n\tfetchRecurseSubmodules = true\n",
            TestContext.Current.CancellationToken);

        await GitSubmodule.SetFetchRecurseAsync(repo, "sub", SubmoduleRecurse.No, TestContext.Current.CancellationToken);

        // C (submodule.c:1159-1172 + config.c:1420-1438): NO maps to the FALSE entry whose string is NULL → the key is DELETED, not written as "false".
        string content = await File.ReadAllTextAsync(gitmodules, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("fetchRecurseSubmodules", content);

        // YES is still written as "true".
        await GitSubmodule.SetFetchRecurseAsync(repo, "sub", SubmoduleRecurse.Yes, TestContext.Current.CancellationToken);
        content = await File.ReadAllTextAsync(gitmodules, TestContext.Current.CancellationToken);
        Assert.Contains("fetchRecurseSubmodules = true", content);
    }

    // ── empty stash message is not null ────────────────────────

    [Fact]
    public async Task Stash_EmptyMessage_NotWip()
    {
        string dir = NewDir();
        await using GitRepository repo = await InitRepoAsync(dir);

        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "v1\n", TestContext.Current.CancellationToken);
        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await idx.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        _ = await CommitAllAsync(repo, "one");

        // Modify the workdir so there is something to stash.
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "f.txt"), "v2\n", TestContext.Current.CancellationToken);

        _ = await repo.StashSaveWithOptsAsync(new GitStashSaveOptions
        {
            Stasher = TestSig(),
            Message = string.Empty,
        }, TestContext.Current.CancellationToken);

        // C (stash.c:518-546): an EMPTY message is non-NULL → "On <branch>: \n", NOT "WIP on <branch>".
        GitStashEntry entry = await repo.StashForEachAsync(TestContext.Current.CancellationToken).FirstAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith("On master:", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("WIP on", entry.Message);
    }
}
