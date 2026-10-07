using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.UnitTests.Revwalk;

public sealed class RevParserTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public RevParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RevParserTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (string path in _extractedPaths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private async ValueTask<GitRepository> OpenTestRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp($"Fixtures/repo/testrepo.zip");
        _extractedPaths.Add(path);
        return await GitRepository.OpenAsync(Path.Combine(path, "testrepo.git"), new GitContext());
    }

    // master tip = a65fedf39aefe402d3bb6e24df4d4f5fe4547750
    private static readonly GitOid s_master = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);

    [Fact]
    public async Task ParseSingle_Head_ResolvesToMasterTip()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        GitObject? obj = await repo.RevparseSingleAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(s_master, obj!.Id);
    }

    [Fact]
    public async Task ParseSingle_BranchName_DwimsToRef()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        GitObject? obj = await repo.RevparseSingleAsync("master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(s_master, obj!.Id);
    }

    [Fact]
    public async Task ParseSingle_FullSha_ResolvesObject()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        GitObject? obj = await repo.RevparseSingleAsync("a65fedf39aefe402d3bb6e24df4d4f5fe4547750", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(s_master, obj!.Id);
    }

    [Fact]
    public async Task ParseSingle_AbbrevSha_ResolvesObject()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        GitObject? obj = await repo.RevparseSingleAsync("a65fedf39", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(s_master, obj!.Id);
    }

    [Fact]
    public async Task ParseSingle_Caret1_ReturnsFirstParent()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        Commit? head = await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.True(head!.Parents.Count > 0);
        GitOid parentId = head.Parents[0];

        GitObject? obj = await repo.RevparseSingleAsync("HEAD^", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(parentId, obj!.Id);
    }

    [Fact]
    public async Task ParseSingle_Caret0_ReturnsCommitItself()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        GitObject? obj = await repo.RevparseSingleAsync("HEAD^0", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(s_master, obj!.Id);
    }

    [Fact]
    public async Task ParseSingle_Tilde1_ReturnsFirstParent()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        Commit? head = await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken);
        GitOid parentId = head!.Parents[0];

        GitObject? obj = await repo.RevparseSingleAsync("HEAD~1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(parentId, obj!.Id);
    }

    [Fact]
    public async Task ParseSingle_Tilde2_ReturnsGrandparent()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        Commit? head = await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken);
        Commit? parent = await repo.ObjectLookupAsync<Commit>(head!.Parents[0], TestContext.Current.CancellationToken);
        GitOid grandparent = parent!.Parents[0];

        GitObject? obj = await repo.RevparseSingleAsync("HEAD~2", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(grandparent, obj!.Id);
    }

    [Fact]
    public async Task ParseSingle_CaretCommit_PeelsToCommit()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        GitObject? obj = await repo.RevparseSingleAsync("HEAD^{commit}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Commit, obj!.Type);
        Assert.Equal(s_master, obj.Id);
    }

    [Fact]
    public async Task ParseSingle_CaretTree_PeelsToTree()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        GitObject? obj = await repo.RevparseSingleAsync("HEAD^{tree}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Tree, obj!.Type);
    }

    [Fact]
    public async Task ParseSingle_ColonPath_ResolvesBlob()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // master's tree should contain "README" or similar.
        var tree = await repo.RevparseSingleAsync("master^{tree}", cancellationToken: TestContext.Current.CancellationToken) as GitTree;
        Assert.NotNull(tree);

        // Try to find a known file. testrepo has "README" at root.
        GitObject? obj = await repo.RevparseSingleAsync("master:README", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Blob, obj!.Type);
    }

    [Fact]
    public async Task ParseSingle_Atlead_Alone_EqualsHead()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        GitObject? obj = await repo.RevparseSingleAsync("@", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(s_master, obj!.Id);
    }

    [Fact]
    public async Task ParseRange_TwoDots_ReturnsFromTo()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        Commit? head = await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken);
        GitOid parentId = head!.Parents[0];

        (GitObject? from, GitObject? to, GitRevSpecFlags flags) = await repo.RevparseRangeAsync($"{parentId}..{s_master}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(from);
        Assert.NotNull(to);
        Assert.Equal(parentId, from!.Id);
        Assert.Equal(s_master, to!.Id);
        Assert.Equal(GitRevSpecFlags.Range, flags);
    }

    [Fact]
    public async Task ParseRange_EmptyRight_DefaultsToHead()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        Commit? head = await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken);
        GitOid parentId = head!.Parents[0];

        (GitObject? from, GitObject? to, GitRevSpecFlags flags) = await repo.RevparseRangeAsync($"{parentId}..", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(from);
        Assert.NotNull(to);
        Assert.Equal(parentId, from!.Id);
        Assert.Equal(s_master, to!.Id);
    }

    [Fact]
    public async Task ParseRange_SingleSpec_ReturnsSingleFlag()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        (GitObject? from, GitObject? to, GitRevSpecFlags flags) = await repo.RevparseRangeAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(from);
        Assert.Null(to);
        Assert.Equal(GitRevSpecFlags.Single, flags);
    }

    [Fact]
    public async Task ParseRange_TripleDot_SetsMergeBaseFlag()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        Commit? head = await repo.ObjectLookupAsync<Commit>(s_master, TestContext.Current.CancellationToken);
        GitOid parentId = head!.Parents[0];

        (GitObject? _, GitObject? _, GitRevSpecFlags flags) = await repo.RevparseRangeAsync($"{parentId}...{s_master}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True((flags & GitRevSpecFlags.MergeBase) != 0);
        Assert.True((flags & GitRevSpecFlags.Range) != 0);
    }

    [Fact]
    public async Task ParseSingle_GrepFindsCommit()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // Find a commit whose message matches a known substring.
        // testrepo's master has a commit with "branch" in the message.
        GitObject? obj = await repo.RevparseSingleAsync("HEAD^{/branch}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.Equal(GitObjectType.Commit, obj!.Type);
    }

    [Fact]
    public async Task ParseSingle_NonexistentSpec_ThrowsNotFound()
    {
        // C (revparse.c:127-128): "revspec '%s' not found", GIT_ENOTFOUND.
        await using GitRepository repo = await OpenTestRepoAsync();
        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () =>
            {
                using GitObject? _ = await repo.RevparseSingleAsync("nonexistent-ref-xyz", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }
}
