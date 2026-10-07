using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Objects;

public sealed class AnnotatedCommitTests
{
    [Fact]
    public void FromCommit_WrapsExistingCommit_SetsDescriptionToOid()
    {
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, ObjectFixtures.CommitBody, GitHashAlgorithmKind.Sha1);

        using var ann = GitAnnotatedCommit.FromCommit(commit);

        Assert.Same(commit, ann.Commit);
        Assert.Equal(commit.Id, ann.Id);
        Assert.Equal(commit.Id.ToString(), ann.Description);
        Assert.Null(ann.Ref);
        Assert.Null(ann.RemoteUrl);
    }

    [Fact]
    public void FromCommit_NullCommit_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => GitAnnotatedCommit.FromCommit(null!));
    }

    [Fact]
    public async Task Lookup_NonexistentOid_Throws()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        var missing = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff".AsSpan(), GitHashAlgorithmKind.Sha1);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.AnnotatedCommitLookupAsync(missing, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Lookup_ExistingCommit_ReturnsAnnotated()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        // a65fedf39aefe402d3bb6e24df4d4f5fe4547750 is HEAD of testrepo.git
        var headId = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);

        using GitAnnotatedCommit ann = await repo.AnnotatedCommitLookupAsync(headId, TestContext.Current.CancellationToken);

        Assert.Equal(headId, ann.Id);
        Assert.Equal(headId.ToString(), ann.Description);
        Assert.Null(ann.Ref);
    }

    [Fact]
    public async Task Lookup_NonCommitObject_Throws()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        // 1810dff58d8a660512d4832e740f692884338ccd is the tree of HEAD commit
        var treeId = GitOid.Parse("1810dff58d8a660512d4832e740f692884338ccd".AsSpan(), GitHashAlgorithmKind.Sha1);

        // C (object.c:124-128): a wrong-type OID is GIT_ENOTFOUND "the
        // requested type does not match the type in the ODB".
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.AnnotatedCommitLookupAsync(treeId, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("the requested type does not match the type in the ODB", ex.Message);
    }

    [Fact]
    public async Task FromRef_NullRef_Throws()
    {
        await using GitRepository repo = await OpenEmptyRepoAsync();
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await repo.AnnotatedCommitFromRefAsync((GitReference)null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FromHead_TestRepo_ResolvesMasterTip()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        var headId = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);

        using GitAnnotatedCommit ann = await repo.AnnotatedCommitFromHeadAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(headId, ann.Id);
        Assert.Equal("HEAD", ann.Ref);
    }

    [Fact]
    public async Task FromRevspec_TestRepoHead_ResolvesMasterTip()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        var headId = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);

        using GitAnnotatedCommit ann = await repo.AnnotatedCommitFromRevspecAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(headId, ann.Id);
        Assert.Equal("HEAD", ann.Description);
    }

    [Fact]
    public async Task FromRevspec_TestRepoMaster_ResolvesMasterTip()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        var headId = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);

        using GitAnnotatedCommit ann = await repo.AnnotatedCommitFromRevspecAsync("master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(headId, ann.Id);
    }

    [Fact]
    public async Task FromRef_TestRepoMaster_ResolvesMasterTip()
    {
        await using GitRepository repo = await OpenTestRepoAsync();
        var headId = GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1);

        GitReference? master = await repo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(master);

        using GitAnnotatedCommit ann = await repo.AnnotatedCommitFromRefAsync(master!, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(headId, ann.Id);
        Assert.Equal("refs/heads/master", ann.Ref);
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        var commit = Commit.Parse(owner: null, ObjectFixtures.CommitId, ObjectFixtures.CommitBody, GitHashAlgorithmKind.Sha1);
        var ann = GitAnnotatedCommit.FromCommit(commit);

        ann.Dispose();
        ann.Dispose();
    }

    private static async ValueTask<GitRepository> OpenEmptyRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures.repo.empty_bare.zip");
        return await GitRepository.OpenAsync(Path.Combine(path, "empty_bare.git"), new GitContext());
    }

    private static async ValueTask<GitRepository> OpenTestRepoAsync()
    {
        string path = FixtureLoader.ExtractTreeToTemp("Fixtures.repo.testrepo.zip");
        return await GitRepository.OpenAsync(Path.Combine(path, "testrepo.git"), new GitContext());
    }
}
