using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Checkout;

/// <summary>
/// End-to-end tests for the merge and checkout parity behaviors (dirty-but-
/// unmodified workdir, HEAD-tree checkout baseline) in
/// libgit2 1.9.4. Expectations
/// C-verified against libgit2 1.9.4.
/// </summary>
public sealed class CheckoutParityIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public CheckoutParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_CheckoutParityInt_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static async Task CommitFileAsync(GitRepository repo, string name, string content, CancellationToken ct)
    {
        GitIndex idx = await repo.GetIndexAsync(ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, name), content, cancellationToken: ct);
        await idx.AddByPathAsync(name, ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);

        GitReference? head = await repo.ReferenceResolveAsync("HEAD", ct);
        GitOid[] parents = head is GitDirectReference dr ? [dr.Target] : [];
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "commit " + name + "\n",
            UpdateRef = "refs/heads/master",
        }, ct);
    }

    [Fact]
    public async Task DirtyUnmodifiedWorkdir_CheckoutHead_PreservesLocalEdits()
    {
        // checkout.c:503-510 — UNMODIFIED delta + dirty workdir →
        // CHECKOUT_ACTION_IF(FORCE, UPDATE_BLOB, NONE). C-verified: rc=0,
        // file keeps its local content.
        string path = Path.Combine(_tempDir, "r");
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);

        await CommitFileAsync(repo, "file.txt", "one\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), "LOCAL EDIT\n", cancellationToken: TestContext.Current.CancellationToken);

        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe }, TestContext.Current.CancellationToken);

        Assert.Equal("LOCAL EDIT\n",
            await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StagedChange_CheckoutHead_Preserved()
    {
        // the checkout baseline is the HEAD tree, never the index
        // (checkout.c:2476-2484). HEAD == target → empty diff → the staged
        // change is neither applied nor lost. C-verified: rc=0, workdir and
        // index untouched.
        string path = Path.Combine(_tempDir, "staged-change");
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);

        await CommitFileAsync(repo, "file.txt", "one\n", TestContext.Current.CancellationToken);

        GitIndex idx = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), "two\n", cancellationToken: TestContext.Current.CancellationToken);
        GitOid staged = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes("three\n"), TestContext.Current.CancellationToken);
        await idx.AddFromBufferAsync(new GitIndexEntry("file.txt", GitOid.Empty, GitFileMode.Regular), Encoding.UTF8.GetBytes("three\n"), TestContext.Current.CancellationToken);
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe }, TestContext.Current.CancellationToken);

        Assert.Equal("two\n",
            await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), TestContext.Current.CancellationToken));
        GitIndexEntry? entry = (await repo.GetIndexAsync(TestContext.Current.CancellationToken)).EntryByPath("file.txt");
        Assert.NotNull(entry);
        Assert.Equal(staged, entry.Value.Id);
    }

    [Fact]
    public async Task SetHeadThenCheckoutHead_DoesNotResyncWorkdir()
    {
        // after SetHead, checkout_head diffs the NEW HEAD tree against
        // itself — an intentionally empty diff (checkout.c:2476-2484). The
        // workdir is NOT re-synced. C-verified: rc=0, files keep their
        // pre-switch content.
        string path = Path.Combine(_tempDir, "sethead-nosync");
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);

        await CommitFileAsync(repo, "file.txt", "v1\n", TestContext.Current.CancellationToken);
        GitOid c1 = Assert.IsType<GitDirectReference>(await repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken)).Target;
        await repo.BranchCreateAsync("B", c1, force: false, TestContext.Current.CancellationToken);
        await CommitFileAsync(repo, "file.txt", "v2\n", TestContext.Current.CancellationToken);

        await repo.SetHeadAsync("refs/heads/B", TestContext.Current.CancellationToken);
        await repo.CheckoutHeadAsync(new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Safe }, TestContext.Current.CancellationToken);

        Assert.Equal("v2\n",
            await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckoutTree_BlobToTreeTypechange_CleanWorkdir_Succeeds()
    {
        // A blob→tree typechange over a clean workdir must REMOVE the file (no UPDATE_BLOB — checkout.c:553-555 strips it) and let the child entries
        // create the directory. C-verified via probe: rc=0, "a" becomes a directory containing "a/b".
        string path = Path.Combine(_tempDir, "tree-typechange");
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);

        await CommitFileAsync(repo, "a", "v1\n", TestContext.Current.CancellationToken);

        // Build the target tree "a" → directory with "a/b" WITHOUT moving HEAD
        // (the checkout baseline is HEAD, so it must still hold the blob).
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes("inner\n"), TestContext.Current.CancellationToken);
        using (var sub = new GitTreeBuilder(repo))
        {
            await sub.InsertAsync(LibGit2CS.IO.GitPath.FromUtf8String("b"), blobOid, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid subTreeOid = await sub.WriteAsync(TestContext.Current.CancellationToken);

            using var builder = new GitTreeBuilder(repo);
            await builder.InsertAsync(LibGit2CS.IO.GitPath.FromUtf8String("a"), subTreeOid, GitFileMode.Tree, TestContext.Current.CancellationToken);
            GitOid tree2Oid = await builder.WriteAsync(TestContext.Current.CancellationToken);

            GitReference? head = await repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken);
            GitOid[] parents = head is GitDirectReference dr ? [dr.Target] : [];
            await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = tree2Oid,
                Parents = parents,
                Author = TestSig(),
                Committer = TestSig(),
                Message = "make a a dir\n",
                UpdateRef = null,
            }, TestContext.Current.CancellationToken);

            GitTree? tree2 = await repo.ObjectLookupAsync<GitTree>(tree2Oid, TestContext.Current.CancellationToken);
            Assert.NotNull(tree2);
            await repo.CheckoutTreeAsync(tree2, cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.False(File.Exists(Path.Combine(repo.Workdir!, "a")));
        Assert.Equal("inner\n", await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "a", "b"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckoutTree_TreeToBlobTypechange_WorkdirFile_SafeConflicts()
    {
        // old_file==TREE with a plain-file workdir → CHECKOUT_ACTION_IF(FORCE, REMOVE, CONFLICT) (checkout.c:537-546). SAFE conflicts and preserves the
        // local file (C-verified: err=-13).
        string path = Path.Combine(_tempDir, "workdir-typechange");
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, ctx, TestContext.Current.CancellationToken);

        // Commit 1: directory "a" with "a/b"; checkout.
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes("inner\n"), TestContext.Current.CancellationToken);
        GitOid tree1Oid;
        using (var sub = new GitTreeBuilder(repo))
        {
            await sub.InsertAsync(LibGit2CS.IO.GitPath.FromUtf8String("b"), blobOid, GitFileMode.Regular, TestContext.Current.CancellationToken);
            GitOid subTreeOid = await sub.WriteAsync(TestContext.Current.CancellationToken);
            using var builder = new GitTreeBuilder(repo);
            await builder.InsertAsync(LibGit2CS.IO.GitPath.FromUtf8String("a"), subTreeOid, GitFileMode.Tree, TestContext.Current.CancellationToken);
            tree1Oid = await builder.WriteAsync(TestContext.Current.CancellationToken);
        }

        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree1Oid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "dir a\n",
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
        GitTree? tree1 = await repo.ObjectLookupAsync<GitTree>(tree1Oid, TestContext.Current.CancellationToken);
        await repo.CheckoutTreeAsync(tree1, cancellationToken: TestContext.Current.CancellationToken);

        // Commit 2 (HEAD stays at commit 1): "a" becomes a file.
        GitOid fileBlob = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes("now a file\n"), TestContext.Current.CancellationToken);
        GitOid tree2Oid;
        using (var builder = new GitTreeBuilder(repo))
        {
            await builder.InsertAsync(LibGit2CS.IO.GitPath.FromUtf8String("a"), fileBlob, GitFileMode.Regular, TestContext.Current.CancellationToken);
            tree2Oid = await builder.WriteAsync(TestContext.Current.CancellationToken);
        }

        GitReference? head = await repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken);
        GitOid[] parents = head is GitDirectReference dr ? [dr.Target] : [];
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree2Oid,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = "a becomes file\n",
            UpdateRef = null,
        }, TestContext.Current.CancellationToken);

        // Workdir: replace directory "a" with a (modified) plain file.
        Directory.Delete(Path.Combine(repo.Workdir!, "a"), recursive: true);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "a"), "LOCAL FILE\n", TestContext.Current.CancellationToken);

        GitTree? tree2 = await repo.ObjectLookupAsync<GitTree>(tree2Oid, TestContext.Current.CancellationToken);
        Assert.NotNull(tree2);
        GitException? ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.CheckoutTreeAsync(tree2!, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Conflict, ex.Code);
        Assert.Equal("LOCAL FILE\n", await File.ReadAllTextAsync(Path.Combine(repo.Workdir!, "a"), TestContext.Current.CancellationToken));
    }
}
