using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Stash;
using LibGit2CS.Status;

namespace LibGit2CS.UnitTests.Config;

/// <summary>
/// Regression tests for C configmap-lookup error semantics
/// for invalid config values. Verified against libgit2 1.9.4 with the
/// C configmap probe harness:
/// <list type="bullet">
/// <item>core.logallrefupdates = banana → ref write fails,
/// "failed to map 'banana'" (refdb.c:306-308 → config.c:1415).</item>
/// <item>core.ignorecase = banana → status/stash fail,
/// "failed to parse 'banana' as a boolean" (ignore.c:311, stash.c:492).</item>
/// <item>core.symlinks = banana → checkout fails (checkout.c:2468-2473).</item>
/// <item>merge.conflictstyle = DIFF3 → "unknown style 'DIFF3' given for
/// 'merge.conflictstyle'" (checkout.c:2520-2525, strcmp = case-sensitive).</item>
/// <item>core.repositoryformatversion = banana → open fails,
/// "failed to parse 'banana' as a 32-bit integer" (repository.c:1836-1844).</item>
/// <item>core.filemode/trustctime = banana → diff caps SWALLOW the error
/// (diff_generate.c:515-535) — the diff still works.</item>
/// </list>
/// </summary>
public sealed class ConfigMapErrorParityTests
{
    private static GitSignature TestSig() => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private static async Task<(GitRepository repo, string path)> InitRepoAsync()
    {
        string repoPath = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigMapErr_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
        return (repo, repoPath);
    }

    private static async Task<GitTree> WriteCommitAsync(GitRepository repo)
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "one\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = repo.NewTreeBuilder();
        await bld.InsertAsync("a.txt", blobOid, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "init\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        return (await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;
    }

    private static async Task CleanupAsync(GitRepository repo, string repoPath)
    {
        await repo.DisposeAsync();
        try
        {
            Directory.Delete(repoPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ── core.ignorecase = banana → status fails ──────────────────────────

    [Fact]
    public async Task Status_IgnoreCaseUnmappable_FailsLikeC()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            await WriteCommitAsync(repo);
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "a.txt"), "one\n", cancellationToken: TestContext.Current.CancellationToken);
            await repo.Config.SetStringAsync("core.ignorecase", "banana", TestContext.Current.CancellationToken);

            // C (ignore.c:305-313): git_ignore__push_dir propagates the
            // configmap lookup error → git_status_list_new fails.
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            {
                using GitStatusList list = await repo.StatusNewAsync(cancellationToken: TestContext.Current.CancellationToken);
                _ = list.Entries;
            });
            Assert.Equal(GitErrorCode.Error, ex.Code);
            Assert.Equal(GitErrorCategory.Config, ex.Category);
            Assert.Equal("failed to parse 'banana' as a boolean", ex.Message);
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    // ── core.symlinks = banana → checkout fails ───────────────────────────

    [Fact]
    public async Task Checkout_SymlinksUnmappable_FailsLikeC()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            GitTree tree = await WriteCommitAsync(repo);
            await repo.Config.SetStringAsync("core.symlinks", "banana", TestContext.Current.CancellationToken);

            // C (checkout.c:2468-2473): checkout_data_init propagates the
            // bool configmap error → git_checkout fails.
            GitException ex = await Assert.ThrowsAsync<GitException>(() => repo.CheckoutTreeAsync(
                tree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force },
                TestContext.Current.CancellationToken));
            Assert.Equal(GitErrorCode.Error, ex.Code);
            Assert.Equal(GitErrorCategory.Config, ex.Category);
            Assert.Equal("failed to parse 'banana' as a boolean", ex.Message);
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    // ── merge.conflictstyle case sensitivity + unknown-style error ────────

    [Fact]
    public async Task Checkout_ConflictStyleUppercase_FailsLikeC()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            GitTree tree = await WriteCommitAsync(repo);
            await repo.Config.SetStringAsync("merge.conflictstyle", "DIFF3", TestContext.Current.CancellationToken);

            // C (checkout.c:2520-2525): plain strcmp — "DIFF3" is unknown.
            GitException ex = await Assert.ThrowsAsync<GitException>(() => repo.CheckoutTreeAsync(
                tree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force },
                TestContext.Current.CancellationToken));
            Assert.Equal(GitErrorCode.Error, ex.Code);
            Assert.Equal(GitErrorCategory.Checkout, ex.Category);
            Assert.Equal("unknown style 'DIFF3' given for 'merge.conflictstyle'", ex.Message);
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    [Fact]
    public async Task Checkout_ConflictStyleLowercase_Succeeds()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            GitTree tree = await WriteCommitAsync(repo);
            await repo.Config.SetStringAsync("merge.conflictstyle", "diff3", TestContext.Current.CancellationToken);

            await repo.CheckoutTreeAsync(
                tree, new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force },
                TestContext.Current.CancellationToken);
            Assert.True(File.Exists(Path.Combine(repo.Workdir!, "a.txt")));
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    // ── core.ignorecase = banana → stash save fails ───────────────────────

    [Fact]
    public async Task Stash_IgnoreCaseUnmappable_FailsLikeC()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            await WriteCommitAsync(repo);
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "a.txt"), "changed\n", cancellationToken: TestContext.Current.CancellationToken);
            await repo.Config.SetStringAsync("core.ignorecase", "banana", TestContext.Current.CancellationToken);

            // C (stash.c:487-494): the configmap lookup error fails the save.
            GitException ex = await Assert.ThrowsAsync<GitException>(() =>
                repo.StashSaveAsync(TestSig(), null, GitStashFlags.Default, TestContext.Current.CancellationToken));
            Assert.Equal(GitErrorCode.Error, ex.Code);
            Assert.Equal(GitErrorCategory.Config, ex.Category);
            Assert.Equal("failed to parse 'banana' as a boolean", ex.Message);
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }

    // ── core.repositoryformatversion = banana → open fails ────────────────

    [Fact]
    public async Task Open_RepositoryFormatVersionUnmappable_FailsLikeC()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            await repo.Config.SetStringAsync("core.repositoryformatversion", "banana", TestContext.Current.CancellationToken);
            await repo.DisposeAsync();

            // C (repository.c:1836-1844): git_config_get_int32 fails on the
            // unparseable value → git_repository_open fails.
            GitException ex = await Assert.ThrowsAsync<GitException>(() =>
                GitRepository.OpenAsync(repoPath, new GitContext(), TestContext.Current.CancellationToken));
            Assert.Equal(GitErrorCode.Error, ex.Code);
            Assert.Equal(GitErrorCategory.Config, ex.Category);
            Assert.Equal("failed to parse 'banana' as a 32-bit integer", ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(repoPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ── core.filemode/trustctime = banana → diff caps swallow ─────────────

    [Fact]
    public async Task Diff_FilemodeTrustctimeUnmappable_SwallowedLikeC()
    {
        (GitRepository repo, string repoPath) = await InitRepoAsync();
        try
        {
            GitTree tree = await WriteCommitAsync(repo);
            await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "a.txt"), "changed\n", cancellationToken: TestContext.Current.CancellationToken);
            await repo.Config.SetStringAsync("core.filemode", "banana", TestContext.Current.CancellationToken);
            await repo.Config.SetStringAsync("core.trustctime", "banana", TestContext.Current.CancellationToken);

            // C (diff_generate.c:515-535): set_caps swallows the errors —
            // the caps stay OFF and the diff still runs.
            using GitDiff diff = await repo.DiffTreeToWorkdirAsync(tree, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(diff.DeltaCount >= 1);
        }
        finally
        {
            await CleanupAsync(repo, repoPath);
        }
    }
}
