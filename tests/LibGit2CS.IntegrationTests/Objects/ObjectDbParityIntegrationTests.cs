using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Objects;

/// <summary>
/// End-to-end tests for the ODB object behaviors
/// (the empty blob is NOT hardcoded in the ODB, and non-loose types are
/// rejected by hash/write) in libgit2 1.9.4. All expectations C-verified
/// against libgit2 1.9.4.
/// </summary>
public sealed class ObjectDbParityIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public ObjectDbParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ObjectDbParityInt_" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public async Task EmptyObjects_NotStored_MissLikeC()
    {
        // git_odb_exists/read/read_header for the empty blob and tree
        // behave like any absent object (only the empty TREE is hardcoded in
        // reads; exists has no hardcoded check at all).
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "r"), isBare: true, ctx, TestContext.Current.CancellationToken);

        Assert.False(await repo.Objects.ExistsAsync(GitOid.EmptyBlobSha1, TestContext.Current.CancellationToken));
        Assert.False(await repo.Objects.ExistsAsync(GitOid.EmptyTreeSha1, TestContext.Current.CancellationToken));
        Assert.Null(await repo.ObjectLookupAsync(GitOid.EmptyBlobSha1, TestContext.Current.CancellationToken));

        GitObjectHeader? blobHeader = await repo.Objects.ReadHeaderAsync(GitOid.EmptyBlobSha1, TestContext.Current.CancellationToken);
        Assert.Null(blobHeader);

        GitObjectHeader? treeHeader = await repo.Objects.ReadHeaderAsync(GitOid.EmptyTreeSha1, TestContext.Current.CancellationToken);
        Assert.NotNull(treeHeader);
        Assert.Equal(GitObjectType.Tree, treeHeader!.Value.Type);
        Assert.Equal(0, treeHeader.Value.Size);

        GitObject? tree = await repo.ObjectLookupAsync(GitOid.EmptyTreeSha1, TestContext.Current.CancellationToken);
        Assert.NotNull(tree);
        Assert.Equal(GitObjectType.Tree, tree!.Type);
    }

    [Fact]
    public async Task EmptyObjects_AfterWrite_Visible()
    {
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "empty-objects"), isBare: true, ctx, TestContext.Current.CancellationToken);

        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);
        GitOid treeOid = await repo.ObjectWriteAsync(GitObjectType.Tree, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);
        Assert.Equal(GitOid.EmptyBlobSha1, blobOid);
        Assert.Equal(GitOid.EmptyTreeSha1, treeOid);

        Assert.True(await repo.Objects.ExistsAsync(GitOid.EmptyBlobSha1, TestContext.Current.CancellationToken));
        Assert.True(await repo.Objects.ExistsAsync(GitOid.EmptyTreeSha1, TestContext.Current.CancellationToken));
        Assert.NotNull(await repo.ObjectLookupAsync(GitOid.EmptyBlobSha1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Commit_WithUnwrittenEmptyTree_ValidLikeC()
    {
        // C: validate_tree_and_parents uses git_object__is_valid (a READ), so
        // a commit may reference the hardcoded empty tree before it is stored.
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "empty-tree-commit"), isBare: true, ctx, TestContext.Current.CancellationToken);

        var sig = new GitSignature("t", "t@example.com", new GitTime(1_700_000_000, 0));
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = GitOid.EmptyTreeSha1,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "empty tree commit\n",
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);

        Assert.False(commitOid.IsZero);
        Commit? commit = await repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        Assert.NotNull(commit);
        Assert.Equal(GitOid.EmptyTreeSha1, commit!.Tree);
    }

    [Fact]
    public async Task WriteAsync_NonLooseType_ThrowsInvalid_FileUntouched()
    {
        // git_odb_write rejects non-loose types with "invalid object type".
        using GitContext ctx = new();
        await using GitRepository repo = await GitRepository.InitAsync(Path.Combine(_tempDir, "nonloose-write"), isBare: true, ctx, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.ObjectWriteAsync(GitObjectType.OfsDelta, "x"u8.ToArray(), TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid object type", ex.Message);

        string objectsDir = Path.Combine(repo.Path, "objects");
        Assert.Empty(Directory.EnumerateFiles(objectsDir, "*", SearchOption.AllDirectories));
    }
}
