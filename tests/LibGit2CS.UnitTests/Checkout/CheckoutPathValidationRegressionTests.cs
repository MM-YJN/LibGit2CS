using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Checkout;

// Checkout validates every delta's path — C's checkout_get_actions calls
// checkout_verify_paths (checkout.c:1288-1310) with
// GIT_PATH_REJECT_WORKDIR_DEFAULTS for every delta, and every write goes
// through checkout_target_fullpath / checkout_merge_path (checkout.c:327-341,
// 2042-2068), so a crafted tree entry like "../evil" or ".git" cannot escape
// the workdir (write or delete).
//
// WorkdirWriter removes a pre-existing entry before writing (mkpath2file,
// checkout.c:1449-1483): on case-insensitive filesystems (core.ignorecase) a
// symlink at the target path is deleted first and a fresh file created, so
// File.WriteAllBytesAsync (O_CREAT|O_TRUNC) never writes through the link to
// the attacker-chosen target.
public sealed class CheckoutPathValidationRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public CheckoutPathValidationRegressionTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_CheckoutPathVal_" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>Builds raw tree object bytes with a single entry.</summary>
    private static byte[] BuildSingleEntryTree(string entryName, GitOid blobOid)
    {
        using var ms = new MemoryStream();
        ms.Write("100644 "u8);
        ms.Write(Encoding.UTF8.GetBytes(entryName));
        ms.WriteByte(0);
        ms.Write(blobOid.RawBytes.ToArray());
        return ms.ToArray();
    }

    private async Task<(GitRepository Repo, string RepoPath, GitOid BlobOid)> CreateRepoWithBlobAsync(string name)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);
        GitOid blobOid = await repo.ObjectWriteAsync(
            GitObjectType.Blob, "payload\n"u8.ToArray(),
            TestContext.Current.CancellationToken);
        return (repo, repoPath, blobOid);
    }

    [Fact]
    public async Task CheckoutTree_TraversalPath_ThrowsAndDoesNotEscape()
    {
        (GitRepository repo, string repoPath, GitOid blobOid) = await CreateRepoWithBlobAsync("repo1");
        await using (repo)
        {
            // A tree whose entry name traverses out of the workdir. GitTree
            // parsing is structure-only (like tree.c:394-443), so this
            // object is accepted by the ODB; only checkout must reject it.
            byte[] rawTree = BuildSingleEntryTree("../evil", blobOid);
            GitOid treeOid = await repo.ObjectWriteAsync(
                GitObjectType.Tree, rawTree, TestContext.Current.CancellationToken);
            GitTree? evilTree = await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken);

            // C: "cannot checkout to invalid path '../evil'", GIT_ERROR_CHECKOUT.
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await repo.CheckoutTreeAsync(
                    evilTree,
                    new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force },
                    TestContext.Current.CancellationToken));
            Assert.Equal(GitErrorCategory.Checkout, ex.Category);
            Assert.Contains("invalid path", ex.Message);

            // The escape target must NOT exist outside the workdir.
            string escapeTarget = Path.Combine(Path.GetDirectoryName(repoPath)!, "evil");
            Assert.False(File.Exists(escapeTarget), $"checkout escaped the workdir and wrote {escapeTarget}");
        }
    }

    [Fact]
    public async Task CheckoutTree_DotGitPath_Throws()
    {
        (GitRepository repo, string repoPath, GitOid blobOid) = await CreateRepoWithBlobAsync("repo2");
        await using (repo)
        {
            // ".git" as a tree entry name must be rejected by checkout
            // (GIT_PATH_REJECT_WORKDIR_DEFAULTS includes DOT_GIT).
            byte[] rawTree = BuildSingleEntryTree(".git", blobOid);
            GitOid treeOid = await repo.ObjectWriteAsync(
                GitObjectType.Tree, rawTree, TestContext.Current.CancellationToken);
            GitTree? evilTree = await repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken);

            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await repo.CheckoutTreeAsync(
                    evilTree,
                    new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force },
                    TestContext.Current.CancellationToken));
            Assert.Equal(GitErrorCategory.Checkout, ex.Category);
            Assert.Contains("invalid path", ex.Message);
        }
    }

    [Fact]
    public async Task ForceCheckout_Ignorecase_DoesNotWriteThroughSymlink()
    {
        (GitRepository repo, string repoPath, GitOid _) = await CreateRepoWithBlobAsync("repo3");
        await using (repo)
        {
            // Commit a regular file.
            await File.WriteAllTextAsync(
                Path.Combine(repoPath, "foo.txt"), "base\n",
                cancellationToken: TestContext.Current.CancellationToken);
            GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
            await index.AddByPathAsync("foo.txt", TestContext.Current.CancellationToken);
            await index.WriteAsync(TestContext.Current.CancellationToken);
            GitOid treeId = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
            var sig = new GitSignature("T", "t@x.com", new GitTime(100, 0));
            await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeId,
                Author = sig,
                Committer = sig,
                Message = "init\n",
                UpdateRef = "HEAD",
            }, TestContext.Current.CancellationToken);

            // Enable the precondition: a case-insensitive filesystem.
            await repo.Config.SetBoolAsync("core.ignorecase", true, TestContext.Current.CancellationToken);

            // A victim file OUTSIDE the repo, and a symlink at the checkout
            // path pointing at it.
            string victimPath = Path.Combine(_tempDir, "victim.txt");
            await File.WriteAllTextAsync(
                victimPath, "victim-original\n",
                cancellationToken: TestContext.Current.CancellationToken);
            string fooPath = Path.Combine(repoPath, "foo.txt");
            File.Delete(fooPath);
            File.CreateSymbolicLink(fooPath, "../victim.txt");

            // Force checkout rewrites foo.txt. C's mkpath2file
            // (should_remove_existing) removes the link first and creates a
            // fresh file, so File.WriteAllBytesAsync never follows it to
            // overwrite the victim.
            await repo.CheckoutTreeAsync(
                null,
                new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force },
                TestContext.Current.CancellationToken);

            // The victim outside the workdir must be untouched.
            string victimContent = await File.ReadAllTextAsync(
                victimPath, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("victim-original\n", victimContent);

            // foo.txt must now be a fresh regular file, not a symlink.
            var fooInfo = new FileInfo(fooPath);
            Assert.Null(fooInfo.LinkTarget);
            string fooContent = await File.ReadAllTextAsync(
                fooPath, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("base\n", fooContent);
        }
    }
}
