using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Repository;

public sealed class RepositoryWriteTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly GitRepository _repo;

    public RepositoryWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RepoWriteTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _repo = GitRepository.InitAsync(_tempDir, isBare: false, new GitContext()).GetAwaiter().GetResult();
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

    private async Task<GitOid> WriteCommit(string message = "base\n")
    {
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "hello"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitSignature sig = TestSig();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = message,
            UpdateRef = "refs/heads/master",
        });
    }

    [Fact]
    public async Task SetBare_SetsCoreBareTrue()
    {
        await _repo.SetBareAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await _repo.Config.GetBoolAsync("core.bare", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetBare_RemovesCoreWorktree()
    {
        await _repo.Config.SetStringAsync("core.worktree", "/some/path", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(await _repo.Config.GetEntryAsync("core.worktree", cancellationToken: TestContext.Current.CancellationToken));

        await _repo.SetBareAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await _repo.Config.GetEntryAsync("core.worktree", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetHead_ToBranch_SetsSymbolicHead()
    {
        await WriteCommit();

        await _repo.SetHeadAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        GitReference? head = await _repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.True(head!.IsSymbolic);
        Assert.Equal("refs/heads/master", ((GitSymbolicReference)head).TargetName);
    }

    [Fact]
    public async Task SetHead_ToUnbornBranch_SetsSymbolicHead()
    {
        await _repo.SetHeadAsync("refs/heads/new-branch", cancellationToken: TestContext.Current.CancellationToken);

        GitReference? head = await _repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.True(head!.IsSymbolic);
        Assert.Equal("refs/heads/new-branch", ((GitSymbolicReference)head).TargetName);
    }

    [Fact]
    public async Task SetHead_ToTag_DetachesHead()
    {
        GitOid commitOid = await WriteCommit();
        Commit commit = (await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
        GitSignature tagger = TestSig();

        await _repo.TagCreateAsync("v1", commit, tagger, "tag msg\n", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.SetHeadAsync("refs/tags/v1", cancellationToken: TestContext.Current.CancellationToken);

        GitReference? head = await _repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.False(head!.IsSymbolic);
    }

    [Fact]
    public async Task SetHeadDetached_WritesDirectHead()
    {
        GitOid commitOid = await WriteCommit();

        await _repo.SetHeadDetachedAsync(commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? head = await _repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.False(head!.IsSymbolic);
        Assert.Equal(commitOid, ((GitDirectReference)head).Target);
    }

    [Fact]
    public async Task DetachHead_ConvertsSymbolicToDirect()
    {
        GitOid commitOid = await WriteCommit();

        // HEAD is symbolic → refs/heads/master.
        GitReference? head = await _repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(head!.IsSymbolic);

        await _repo.DetachHeadAsync(cancellationToken: TestContext.Current.CancellationToken);

        // HEAD should now be direct.
        head = await _repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(head!.IsSymbolic);
        Assert.Equal(commitOid, ((GitDirectReference)head).Target);
    }

    [Fact]
    public async Task Commit_Create_WithUpdateRef_UpdatesBranch()
    {
        GitOid firstOid = await WriteCommit("first\n");

        // Write a second commit on top.
        GitSignature sig = TestSig();
        Commit firstCommit = (await _repo.ObjectLookupAsync<Commit>(firstOid, TestContext.Current.CancellationToken))!;
        GitOid secondOid = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = firstCommit.Tree,
            Parents = [firstOid],
            Author = sig,
            Committer = sig,
            Message = "second\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // The branch should now point at the second commit.
        GitReference? master = await _repo.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(secondOid, ((GitDirectReference)master!).Target);

        // A reflog entry should have been written.
        GitRefLog? log = await _repo.ReferenceReadLogAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(log);
        Assert.True(log!.EntryCount >= 2);
    }

    [Fact]
    public async Task Tag_Create_Annotated_CreatesRefAndObject()
    {
        GitOid commitOid = await WriteCommit();
        Commit commit = (await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
        GitSignature tagger = TestSig();

        GitOid tagOid = await _repo.TagCreateAsync("v1.0", commit, tagger, "release\n", cancellationToken: TestContext.Current.CancellationToken);

        // The ref should exist and point at the tag object.
        GitReference? tagRef = await _repo.ReferenceLookupAsync("refs/tags/v1.0", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(tagRef);
        Assert.Equal(tagOid, ((GitDirectReference)tagRef!).Target);

        // The tag object should be readable.
        GitTag? tag = await _repo.ObjectLookupAsync<GitTag>(tagOid, TestContext.Current.CancellationToken);
        Assert.NotNull(tag);
        Assert.Equal("v1.0", tag!.Name);
        Assert.Equal("release\n", tag.Message);
    }

    [Fact]
    public async Task Tag_Create_Lightweight_PointsAtTarget()
    {
        GitOid commitOid = await WriteCommit();
        Commit commit = (await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;

        await _repo.TagCreateAsync("light", commit, null, null, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? tagRef = await _repo.ReferenceLookupAsync("refs/tags/light", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(tagRef);
        Assert.Equal(commitOid, ((GitDirectReference)tagRef!).Target);
    }

    [Fact]
    public async Task Tag_Delete_RemovesRef()
    {
        GitOid commitOid = await WriteCommit();
        Commit commit = (await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
        await _repo.TagCreateAsync("temp", commit, null, null, cancellationToken: TestContext.Current.CancellationToken);

        await _repo.TagDeleteAsync("temp", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await _repo.ReferenceLookupAsync("refs/tags/temp", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tag_Delete_Nonexistent_Throws()
    {
        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await _repo.TagDeleteAsync("nonexistent", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }
}
