using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Checkout;

/// <summary> Regression tests for the checkout TYPECHANGE action is an incomplete port of <c>checkout.c:530-556</c>. </summary> <remarks> Two C rules
/// were absent. (a) The trailing strip (<c>new_file.mode == TREE</c> → clear UPDATE_BLOB, checkout.c:553-555) made a blob→tree typechange on a clean workdir
/// emit RemoveAndUpdate, whose UPDATE_BLOB then looked up the tree OID as a blob and threw "blob &lt;tree-oid&gt; not found" (the
/// blob lookup in <c>WriteRegularFileAsync</c>). (b)
/// The <c>old_file.mode == TREE</c> sub-branches (wd tree / gitlink with submodule_is_config_only / plain file) were collapsed to a bare UpdateBlob, so SAFE
/// checkouts overwrote workdir files where C conflicts. </remarks>
public sealed class CheckoutTypechangeParityTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public CheckoutTypechangeParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_Typechange_" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>
    /// Commits a tree. When <paramref name="updateRef"/> is null the commit is
    /// created WITHOUT moving HEAD, so a subsequent checkout of a different
    /// tree still diffs against the previous HEAD (the baseline).
    /// </summary>
    private async Task<GitOid> CommitTreeAsync(GitOid treeOid, string message, string? updateRef, CancellationToken ct)
    {
        GitReference? head = await _repo.ReferenceResolveAsync("HEAD", cancellationToken: ct);
        GitOid[] parents = head is GitDirectReference dr ? [dr.Target] : [];

        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = message + "\n",
            UpdateRef = updateRef,
        }, ct);
    }

    private async Task<GitOid> CommitFileAsync(string name, string content, CancellationToken ct)
    {
        GitIndex idx = await _repo.GetIndexAsync(cancellationToken: ct);
        string workdir = _repo.Workdir!;
        string full = Path.Combine(workdir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, cancellationToken: ct);
        await idx.AddByPathAsync(name, cancellationToken: ct);
        await idx.WriteAsync(cancellationToken: ct);
        GitOid treeOid = await idx.WriteTreeAsync(cancellationToken: ct);
        return await CommitTreeAsync(treeOid, "commit " + name, "refs/heads/master", ct);
    }

    /// <summary>
    /// Builds a tree with a <c>dir/file</c> subtree, writing the blob.
    /// </summary>
    private async Task<GitTree> BuildTreeWithDirAsync(string dir, string file, string content, CancellationToken ct)
    {
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, System.Text.Encoding.UTF8.GetBytes(content), ct);

        using var subBuilder = new GitTreeBuilder(_repo);
        await subBuilder.InsertAsync(GitPath.FromUtf8String(file), blobOid, GitFileMode.Regular, ct);
        GitOid subTreeOid = await subBuilder.WriteAsync(ct);

        using var builder = new GitTreeBuilder(_repo);
        await builder.InsertAsync(GitPath.FromUtf8String(dir), subTreeOid, GitFileMode.Tree, ct);
        GitOid treeOid = await builder.WriteAsync(ct);

        GitTree? tree = await _repo.ObjectLookupAsync<GitTree>(treeOid, ct);
        Assert.NotNull(tree);
        return tree!;
    }

    [Fact]
    public async Task CheckoutTree_BlobToTreeTypechange_CleanWorkdir_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Commit 1: file "a".
        await CommitFileAsync("a", "v1", ct);

        // Commit 2 (not checked out): "a" becomes a directory with "a/b".
        GitTree tree2 = await BuildTreeWithDirAsync("a", "b", "inner", ct);
        await CommitTreeAsync(tree2.Id, "make a a dir", updateRef: null, ct);

        // Checkout tree2 over the clean workdir: C removes file "a" and lets
        // the child entries create the directory.
        await _repo.CheckoutTreeAsync(tree2, cancellationToken: ct);

        // File "a" is gone; directory "a/b" exists with the right content.
        Assert.False(File.Exists(Path.Combine(_repo.Workdir!, "a")));
        Assert.Equal("inner", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "a", "b"), ct));
    }

    [Fact]
    public async Task CheckoutTree_BlobToTreeTypechange_Force_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await CommitFileAsync("a", "v1", ct);

        GitTree tree2 = await BuildTreeWithDirAsync("a", "b", "inner", ct);
        await CommitTreeAsync(tree2.Id, "make a a dir", updateRef: null, ct);

        // The workdir file is modified locally; C's old_file==TREE rule does
        // not apply here (old is a blob): a modified workdir conflicts unless
        // FORCE, which removes and updates.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "a"), "LOCAL", cancellationToken: ct);

        await _repo.CheckoutTreeAsync(
            tree2,
            new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force },
            ct);

        Assert.False(File.Exists(Path.Combine(_repo.Workdir!, "a")));
        Assert.Equal("inner", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "a", "b"), ct));
    }

    [Fact]
    public async Task CheckoutTree_TreeToBlobTypechange_WorkdirFile_SafeConflicts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Commit 1: directory "a" with "a/b".
        GitTree tree1 = await BuildTreeWithDirAsync("a", "b", "inner", ct);
        await CommitTreeAsync(tree1.Id, "dir a", "refs/heads/master", ct);
        await _repo.CheckoutTreeAsync(tree1, cancellationToken: ct);

        // Commit 2: "a" becomes a plain file.
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, System.Text.Encoding.UTF8.GetBytes("now a file"), ct);
        using var builder = new GitTreeBuilder(_repo);
        await builder.InsertAsync(GitPath.FromUtf8String("a"), blobOid, GitFileMode.Regular, ct);
        GitOid tree2Oid = await builder.WriteAsync(ct);
        GitTree? tree2 = await _repo.ObjectLookupAsync<GitTree>(tree2Oid, ct);
        Assert.NotNull(tree2);
        await CommitTreeAsync(tree2Oid, "a becomes a file", updateRef: null, ct);

        // Workdir: replace directory "a" with a (modified) file "a".
        Directory.Delete(Path.Combine(_repo.Workdir!, "a"), recursive: true);
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "a"), "LOCAL FILE", cancellationToken: ct);

        // C (checkout.c:537-546): old_file==TREE with a plain-file workdir →
        // SAFE conflicts (FORCE would remove).
        GitException? ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.CheckoutTreeAsync(tree2!, cancellationToken: ct));
        Assert.Equal(GitErrorCode.Conflict, ex.Code);

        // The local file is untouched.
        Assert.Equal("LOCAL FILE", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "a"), ct));
    }

    [Fact]
    public async Task CheckoutTree_TreeToBlobTypechange_WorkdirFile_ForceRemovesOnly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        GitTree tree1 = await BuildTreeWithDirAsync("a", "b", "inner", ct);
        await CommitTreeAsync(tree1.Id, "dir a", "refs/heads/master", ct);
        await _repo.CheckoutTreeAsync(tree1, cancellationToken: ct);

        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, System.Text.Encoding.UTF8.GetBytes("now a file"), ct);
        using var builder = new GitTreeBuilder(_repo);
        await builder.InsertAsync(GitPath.FromUtf8String("a"), blobOid, GitFileMode.Regular, ct);
        GitOid tree2Oid = await builder.WriteAsync(ct);
        GitTree? tree2 = await _repo.ObjectLookupAsync<GitTree>(tree2Oid, ct);
        Assert.NotNull(tree2);

        // C (checkout.c:537-546): old_file==TREE with a plain-file workdir →
        // CHECKOUT_ACTION_IF(FORCE, REMOVE, CONFLICT) — FORCE removes the file
        // but does NOT write the new blob (C-verified via probe: the path is
        // left missing).
        Directory.Delete(Path.Combine(_repo.Workdir!, "a"), recursive: true);
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, "a"), "LOCAL FILE", cancellationToken: ct);

        await _repo.CheckoutTreeAsync(
            tree2!,
            new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force },
            ct);

        Assert.False(File.Exists(Path.Combine(_repo.Workdir!, "a")));
    }

    [Fact]
    public async Task CheckoutTree_TreeToBlobTypechange_WorkdirDir_ForceWritesFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        GitTree tree1 = await BuildTreeWithDirAsync("a", "b", "inner", ct);
        await CommitTreeAsync(tree1.Id, "dir a", "refs/heads/master", ct);
        await _repo.CheckoutTreeAsync(tree1, cancellationToken: ct);

        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, System.Text.Encoding.UTF8.GetBytes("now a file"), ct);
        using var builder = new GitTreeBuilder(_repo);
        await builder.InsertAsync(GitPath.FromUtf8String("a"), blobOid, GitFileMode.Regular, ct);
        GitOid tree2Oid = await builder.WriteAsync(ct);
        GitTree? tree2 = await _repo.ObjectLookupAsync<GitTree>(tree2Oid, ct);
        Assert.NotNull(tree2);

        // C (checkout.c:537-538): old_file==TREE with a TREE workdir →
        // UPDATE_BLOB — the directory is replaced by the new file
        // (C-verified via probe).
        await _repo.CheckoutTreeAsync(
            tree2!,
            new GitCheckoutOptions { Strategy = GitCheckoutStrategy.Force },
            ct);

        Assert.Equal("now a file", await File.ReadAllTextAsync(Path.Combine(_repo.Workdir!, "a"), ct));
    }
}
