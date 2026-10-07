using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Repository;

// SetHeadAsync /
// SetHeadDetachedAsync / SetHeadDetachedFromAnnotatedAsync wrote the RAW
// target OID into HEAD. C's detach (repository.c:3545-3587) does
// git_object_lookup(GIT_OBJECT_ANY) + git_object_peel(GIT_OBJECT_COMMIT)
// and creates HEAD at the PEELED commit — so detaching at an annotated
// tag wrote the tag-object OID where C writes the commit.
public sealed class SetHeadPeelRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public SetHeadPeelRegressionTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_SetHeadPeel_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<(GitRepository Repo, GitOid CommitOid, GitOid TagOid)> CreateRepoWithTagAsync()
    {
        string repoPath = Path.Combine(_tempDir, "repo");
        GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(repoPath, "f.txt"), "x\n",
            cancellationToken: TestContext.Current.CancellationToken);
        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);
        GitOid treeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
        var sig = new GitSignature("T", "t@x.com", new GitTime(100, 0));
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "init\n",
            UpdateRef = "refs/heads/main",
        }, TestContext.Current.CancellationToken);

        // Annotated tag → tag OBJECT OID (different from the commit OID).
        Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
        using (commit)
        {
            GitOid tagOid = await repo.TagCreateAsync(
                "v1", commit, sig, "release v1\n",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEqual(commitOid, tagOid);
            return (repo, commitOid, tagOid);
        }
    }

    private static async Task<GitOid> HeadTargetAsync(GitRepository repo)
    {
        GitReference head = (await repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!;
        return ((GitDirectReference)head).Target;
    }

    [Fact]
    public async Task SetHead_AnnotatedTag_WritesPeeledCommit()
    {
        (GitRepository repo, GitOid commitOid, GitOid tagOid) = await CreateRepoWithTagAsync();
        await using (repo)
        {
            await repo.SetHeadAsync("refs/tags/v1", TestContext.Current.CancellationToken);

            // C: HEAD holds the peeled commit, never the tag object OID.
            Assert.Equal(commitOid, await HeadTargetAsync(repo));
            Assert.NotEqual(tagOid, await HeadTargetAsync(repo));
        }
    }

    [Fact]
    public async Task SetHeadDetached_TagOid_WritesPeeledCommit()
    {
        (GitRepository repo, GitOid commitOid, GitOid tagOid) = await CreateRepoWithTagAsync();
        await using (repo)
        {
            await repo.SetHeadDetachedAsync(tagOid, TestContext.Current.CancellationToken);

            Assert.Equal(commitOid, await HeadTargetAsync(repo));
        }
    }

    [Fact]
    public async Task SetHeadDetachedFromAnnotated_WritesPeeledCommit()
    {
        (GitRepository repo, GitOid commitOid, GitOid tagOid) = await CreateRepoWithTagAsync();
        await using (repo)
        {
            await repo.SetHeadDetachedFromAnnotatedAsync("refs/tags/v1", tagOid, TestContext.Current.CancellationToken);

            Assert.Equal(commitOid, await HeadTargetAsync(repo));
        }
    }

    [Fact]
    public async Task SetHeadDetached_NonPeelable_Throws()
    {
        string repoPath = Path.Combine(_tempDir, "repo2");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoPath, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        // A blob cannot be peeled to a commit — C fails detach() via
        // check_type_combination (object.c:396-420) with GIT_EINVALIDSPEC.
        GitOid blobOid = await repo.ObjectWriteAsync(
            GitObjectType.Blob, "data\n"u8.ToArray(),
            TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.SetHeadDetachedAsync(blobOid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
    }
}
