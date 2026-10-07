using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Refs;

public sealed class BranchTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public BranchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_BranchTests_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitOid> WriteCommit(string refName = "refs/heads/master")
    {
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitSignature sig = TestSig();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "base\n",
            UpdateRef = refName,
        });
    }

    private async Task CreateTag(string name, GitOid target)
    {
        Commit? obj = await _repo.ObjectLookupAsync<Commit>(target, TestContext.Current.CancellationToken);
        await _repo.TagCreateAsync(name, obj!, TestSig(), "tag msg\n");
    }

    private async Task<GitOid> WriteSecondCommit(GitOid parent)
    {
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, "world\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder bld = _repo.NewTreeBuilder();
        await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular);
        GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
        GitSignature sig = TestSig();
        return await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [parent],
            Author = sig,
            Committer = sig,
            Message = "second\n",
            UpdateRef = "refs/heads/master",
        });
    }

    // ── Lookup ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Lookup_Local_Branch_ReturnsRef()
    {
        GitOid commitOid = await WriteCommit();

        GitReference? branch = await _repo.BranchLookupAsync("master", GitBranchType.Local, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(branch);
        Assert.Equal("refs/heads/master", branch.Name);
        Assert.Equal(commitOid, ((GitDirectReference)branch).Target);
    }

    [Fact]
    public async Task Lookup_All_FindsLocalFirst()
    {
        await WriteCommit();

        GitReference? branch = await _repo.BranchLookupAsync("master", GitBranchType.All, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(branch);
        Assert.Equal("refs/heads/master", branch.Name);
    }

    [Fact]
    public async Task Lookup_Remote_Type_DoesNotFindLocal()
    {
        await WriteCommit();

        GitReference? branch = await _repo.BranchLookupAsync("master", GitBranchType.Remote, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(branch);
    }

    [Fact]
    public async Task Lookup_All_FindsRemoteIfNoLocal()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/remotes/origin/master", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? branch = await _repo.BranchLookupAsync("origin/master", GitBranchType.All, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(branch);
        Assert.Equal("refs/remotes/origin/master", branch.Name);
    }

    [Fact]
    public async Task Lookup_Remote_Branch_ReturnsRef()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/remotes/origin/master", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? branch = await _repo.BranchLookupAsync("origin/master", GitBranchType.Remote, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(branch);
        Assert.Equal("refs/remotes/origin/master", branch.Name);
    }

    [Fact]
    public async Task Lookup_Unknown_Branch_ReturnsNull()
    {
        await WriteCommit();

        Assert.Null(await _repo.BranchLookupAsync("nonexistent", GitBranchType.Local, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.BranchLookupAsync("nonexistent", GitBranchType.Remote, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.BranchLookupAsync("nonexistent", GitBranchType.All, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lookup_Invalid_Name_ThrowsInvalidSpec()
    {
        await WriteCommit();
        // Invalid names cause an InvalidSpec error (like git_branch_lookup).
        await Assert.ThrowsAsync<GitException>(async () => await _repo.BranchLookupAsync("inv@{id", GitBranchType.Local, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Name ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Name_Local_Branch_ReturnsShortName()
    {
        await WriteCommit();
        GitReference? branch = await _repo.BranchLookupAsync("master", GitBranchType.Local, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(branch);
        Assert.Equal("master", branch!.ShortName());
    }

    [Fact]
    public async Task Name_Remote_Branch_ReturnsShortName()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/remotes/test/master", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? branch = await _repo.BranchLookupAsync("test/master", GitBranchType.Remote, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(branch);
        Assert.Equal("test/master", branch!.ShortName());
    }

    [Fact]
    public async Task Name_NonBranch_ThrowsInvalidSpec()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/notes/fanout", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? note = await _repo.ReferenceLookupAsync("refs/notes/fanout", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(note);
        GitException ex = Assert.Throws<GitException>(() => note!.ShortName());
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
    }

    // ── NameIsValid ─────────────────────────────────────────────────────

    [Fact]
    public async Task NameIsValid_ValidNames_ReturnTrue()
    {
        Assert.True(GitReference.BranchNameIsValid("master"));
        Assert.True(GitReference.BranchNameIsValid("test/master"));
        Assert.True(GitReference.BranchNameIsValid("feature/branch"));
    }

    [Fact]
    public async Task NameIsValid_InvalidNames_ReturnFalse()
    {
        Assert.False(GitReference.BranchNameIsValid(""));
        Assert.False(GitReference.BranchNameIsValid(null));
        Assert.False(GitReference.BranchNameIsValid("HEAD"));
        Assert.False(GitReference.BranchNameIsValid("-dash"));
    }

    // ── IsHead ──────────────────────────────────────────────────────────

    [Fact]
    public async Task IsHead_CurrentBranch_ReturnsTrue()
    {
        await WriteCommit();
        GitReference? master = await _repo.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(master);
        Assert.True(await master!.IsHeadAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsHead_NonCurrentBranch_ReturnsFalse()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/heads/other", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? other = await _repo.ReferenceLookupAsync("refs/heads/other", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(other);
        Assert.False(await other!.IsHeadAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsHead_UnbornHead_ReturnsFalse()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/heads/other", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        // Remove HEAD → unborn
        await _repo.Refs.DeleteAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);

        GitReference? master = await _repo.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(master);
        Assert.False(await master!.IsHeadAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsHead_NonBranch_ReturnsFalse()
    {
        GitOid commitOid = await WriteCommit();
        await CreateTag("v1", commitOid);

        GitReference? tag = await _repo.ReferenceLookupAsync("refs/tags/v1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(tag);
        Assert.False(await tag!.IsHeadAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsHead_DetachedHead_ReturnsFalseForBranch()
    {
        await WriteCommit();
        await _repo.DetachHeadAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitReference? master = await _repo.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(master);
        Assert.False(await master!.IsHeadAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── IsCheckedOut (main + linked worktrees) ──────────────────────────

    [Fact]
    public async Task IsCheckedOut_CurrentBranch_ReturnsTrue()
    {
        await WriteCommit();
        GitReference? master = await _repo.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(master);
        Assert.True(await master!.IsCheckedOutAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsCheckedOut_NonCurrentBranch_ReturnsFalse()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/heads/other", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? other = await _repo.ReferenceLookupAsync("refs/heads/other", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(other);
        Assert.False(await other!.IsCheckedOutAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsCheckedOut_BareRepo_ReturnsFalse()
    {
        string bareDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_BranchTests_bare_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            GitRepository bare = await GitRepository.InitAsync(bareDir, isBare: true, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
            GitOid blobOid = await bare.ObjectWriteAsync(GitObjectType.Blob, "data\n"u8.ToArray(), TestContext.Current.CancellationToken);
            using GitTreeBuilder bld = bare.NewTreeBuilder();
            await bld.InsertAsync("file.txt", blobOid, GitFileMode.Regular, cancellationToken: TestContext.Current.CancellationToken);
            GitOid treeOid = await bld.WriteAsync(CancellationToken.None);
            GitSignature sig = TestSig();
            await bare.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Author = sig,
                Committer = sig,
                Message = "init\n",
                UpdateRef = "refs/heads/master",
            }, cancellationToken: TestContext.Current.CancellationToken);

            GitReference? master = await bare.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(master);
            // In bare repo, HEAD is symbolic but no worktree → not "checked out"
            Assert.False(await master!.IsCheckedOutAsync(cancellationToken: TestContext.Current.CancellationToken));
            await bare.DisposeAsync();
        }
        finally
        {
            try
            {
                Directory.Delete(bareDir, recursive: true);
            }
            catch (IOException) { }
        }
    }

    // ── IsCheckedOut (real worktree enumeration) ───────────────────

    [Fact]
    public async Task IsCheckedOut_WithLinkedWorktree_ReturnsTrue()
    {
        await WriteCommit();
        string wtPath = Path.Combine(_tempDir, "wt1");
        await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? topic = await _repo.ReferenceLookupAsync("refs/heads/topic", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(topic);
        // The branch is checked out in a linked worktree → true.
        Assert.True(await topic!.IsCheckedOutAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsCheckedOut_WithWorktreeOnOtherBranch_MainBranchNotCheckedOut()
    {
        await WriteCommit();
        string wtPath = Path.Combine(_tempDir, "wt1");
        await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        // master is HEAD in the main worktree → checked out.
        GitReference? master = await _repo.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(master);
        Assert.True(await master!.IsCheckedOutAsync(cancellationToken: TestContext.Current.CancellationToken));

        // Create another branch that's NOT in any worktree.
        var head = await _repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken) as GitDirectReference;
        await _repo.ReferenceCreateAsync("refs/heads/unused", head!.Target, cancellationToken: TestContext.Current.CancellationToken);
        GitReference? unused = await _repo.ReferenceLookupAsync("refs/heads/unused", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(unused);
        Assert.False(await unused!.IsCheckedOutAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsCheckedOut_AfterPruningWorktree_ReturnsFalse()
    {
        await WriteCommit();
        string wtPath = Path.Combine(_tempDir, "wt1");
        LibGit2CS.Repository.Worktree wt = await _repo.WorktreeAddAsync("topic", wtPath, cancellationToken: TestContext.Current.CancellationToken);

        // topic is checked out → true.
        GitReference? topic = await _repo.ReferenceLookupAsync("refs/heads/topic", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(topic);
        Assert.True(await topic!.IsCheckedOutAsync(cancellationToken: TestContext.Current.CancellationToken));

        // Delete the worktree and prune.
        Directory.Delete(wtPath, recursive: true);
        wt.Prune();

        // topic is no longer checked out in any worktree.
        Assert.False(await topic!.IsCheckedOutAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Create ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_LocalBranch_WritesRef()
    {
        GitOid commitOid = await WriteCommit();

        GitReference branch = await _repo.BranchCreateAsync("feature", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("refs/heads/feature", branch.Name);
        Assert.Equal(commitOid, ((GitDirectReference)branch).Target);

        // Ref file exists on disk
        string refPath = Path.Combine(_tempDir, ".git", "refs", "heads", "feature");
        Assert.True(File.Exists(refPath));
        Assert.Equal($"{commitOid}\n", await File.ReadAllTextAsync(refPath, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_ExistingBranch_NoForce_ThrowsExists()
    {
        GitOid commitOid = await WriteCommit();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.BranchCreateAsync("master", commitOid, force: false, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Exists, ex.Code);
    }

    [Fact]
    public async Task Create_ExistingBranch_Force_Overwrites()
    {
        GitOid commitOid = await WriteCommit();
        GitOid secondCommit = await WriteSecondCommit(commitOid);
        await _repo.BranchCreateAsync("feature", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        // Force overwrite "feature" (not the current HEAD)
        GitReference branch = await _repo.BranchCreateAsync("feature", secondCommit, force: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(secondCommit, ((GitDirectReference)branch).Target);
    }

    [Fact]
    public async Task Create_ForceOverCurrentHead_Throws()
    {
        GitOid commitOid = await WriteCommit();

        // master is current HEAD; force-create over it should fail — C
        // returns a bare -1 (GIT_ERROR), branch.c:99-105.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.BranchCreateAsync("master", commitOid, force: true, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    [Fact]
    public async Task Create_InvalidName_ThrowsInvalidSpec()
    {
        GitOid commitOid = await WriteCommit();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await _repo.BranchCreateAsync("inv@{id", commitOid, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public async Task Create_InvalidName_Head_Throws()
    {
        GitOid commitOid = await WriteCommit();

        await Assert.ThrowsAsync<GitException>(async () => await _repo.BranchCreateAsync("HEAD", commitOid, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_InvalidName_DashPrefix_Throws()
    {
        GitOid commitOid = await WriteCommit();

        await Assert.ThrowsAsync<GitException>(async () => await _repo.BranchCreateAsync("-dash", commitOid, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_NestedNamespace_Succeeds()
    {
        GitOid commitOid = await WriteCommit();

        GitReference branch = await _repo.BranchCreateAsync("feature/sub", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("refs/heads/feature/sub", branch.Name);
    }

    [Fact]
    public async Task Create_CreateFromAnnotated_WritesRef()
    {
        GitOid commitOid = await WriteCommit();
        Commit? commit = await _repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken);
        var annotated = GitAnnotatedCommit.FromCommit(commit!);

        GitReference branch = await _repo.BranchCreateFromAnnotatedAsync("annotated", annotated, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("refs/heads/annotated", branch.Name);
        Assert.Equal(commitOid, ((GitDirectReference)branch).Target);
    }

    [Fact]
    public async Task Create_NameVsNamespace_SucceedsAfterDelete()
    {
        GitOid commitOid = await WriteCommit();

        // Create nested, delete, then create flat with same prefix
        GitReference b1 = await _repo.BranchCreateAsync("level_one/level_two", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await b1.DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitReference b2 = await _repo.BranchCreateAsync("level_one", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("refs/heads/level_one", b2.Name);
    }

    [Fact]
    public async Task Create_NameVsNamespace_FailWhenSiblingExists()
    {
        GitOid commitOid = await WriteCommit();

        // Create nested + alternate, then try flat (should fail: dir is occupied)
        GitReference b1 = await _repo.BranchCreateAsync("level_one/level_two", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.BranchCreateAsync("level_one/alternate", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        // Creating "level_one" as a branch should fail because the directory
        // is occupied by "level_one/level_two" and "level_one/alternate"
        await Assert.ThrowsAsync<GitException>(async () => await _repo.BranchCreateAsync("level_one", commitOid, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Delete ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_LocalBranch_RemovesRef()
    {
        GitOid commitOid = await WriteCommit();
        GitReference branch = await _repo.BranchCreateAsync("temp", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        await branch.DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await _repo.ReferenceLookupAsync("refs/heads/temp", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_HeadBranch_Throws()
    {
        await WriteCommit();
        GitReference? master = await _repo.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(master);

        // C returns a bare -1 (GIT_ERROR), branch.c:206-210.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await master!.DeleteAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    [Fact]
    public async Task Delete_DetachedHead_CanDeleteBranch()
    {
        await WriteCommit();
        await _repo.DetachHeadAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitReference? master = await _repo.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(master);

        // Should succeed — HEAD is detached, not pointing to master
        await master!.DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await _repo.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_NonBranch_Throws()
    {
        GitOid commitOid = await WriteCommit();
        await CreateTag("v1", commitOid);

        GitReference? tag = await _repo.ReferenceLookupAsync("refs/tags/v1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(tag);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await tag!.DeleteAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Delete_RemoteBranch_RemovesRef()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/remotes/origin/master", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference? remote = await _repo.ReferenceLookupAsync("refs/remotes/origin/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(remote);
        await remote!.DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await _repo.ReferenceLookupAsync("refs/remotes/origin/master", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_RemovesConfigSection()
    {
        GitOid commitOid = await WriteCommit();
        GitReference branch = await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.remote", "origin", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.merge", "refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(await _repo.Config.GetStringAsync("branch.trackme.remote", cancellationToken: TestContext.Current.CancellationToken));

        await branch.DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await _repo.Config.GetStringAsync("branch.trackme.remote", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.Config.GetStringAsync("branch.trackme.merge", cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Move ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Move_LocalBranch_RenamesRef()
    {
        GitOid commitOid = await WriteCommit();
        GitReference branch = await _repo.BranchCreateAsync("oldname", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference renamed = await branch.MoveAsync("newname", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("refs/heads/newname", renamed.Name);

        Assert.Null(await _repo.ReferenceLookupAsync("refs/heads/oldname", cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await _repo.ReferenceLookupAsync("refs/heads/newname", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Move_ToNamespace_Succeeds()
    {
        GitOid commitOid = await WriteCommit();
        GitReference branch = await _repo.BranchCreateAsync("br2", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference renamed = await branch.MoveAsync("somewhere/newname", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("refs/heads/somewhere/newname", renamed.Name);
    }

    [Fact]
    public async Task Move_FromNamespace_ToFlat_Succeeds()
    {
        GitOid commitOid = await WriteCommit();
        GitReference branch = await _repo.BranchCreateAsync("somewhere/br2", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitReference renamed = await branch.MoveAsync("br2", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("refs/heads/br2", renamed.Name);
    }

    [Fact]
    public async Task Move_Collision_NoForce_ThrowsExists()
    {
        GitOid commitOid = await WriteCommit();
        GitReference branch = await _repo.BranchCreateAsync("br2", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await branch.MoveAsync("master", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Exists, ex.Code);
    }

    [Fact]
    public async Task Move_Force_OverwritesExisting()
    {
        GitOid commitOid = await WriteCommit();
        GitOid secondCommit = await WriteSecondCommit(commitOid);
        GitReference branch = await _repo.BranchCreateAsync("br2", secondCommit, cancellationToken: TestContext.Current.CancellationToken);

        // Force move "br2" to "master" (overwrite master)
        GitReference renamed = await branch.MoveAsync("master", force: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("refs/heads/master", renamed.Name);
    }

    [Fact]
    public async Task Move_InvalidName_ThrowsInvalidSpec()
    {
        GitOid commitOid = await WriteCommit();
        GitReference branch = await _repo.BranchCreateAsync("br2", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await branch.MoveAsync("Inv@{id", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public async Task Move_NonBranch_Throws()
    {
        GitOid commitOid = await WriteCommit();
        await CreateTag("v1", commitOid);

        GitReference? tag = await _repo.ReferenceLookupAsync("refs/tags/v1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(tag);
        await Assert.ThrowsAsync<GitException>(async () => await tag!.MoveAsync("newname", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Move_MovesConfigSection()
    {
        GitOid commitOid = await WriteCommit();
        GitReference branch = await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.remote", ".", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.merge", "refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        await branch.MoveAsync("moved", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await _repo.Config.GetStringAsync("branch.trackme.remote", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.Config.GetStringAsync("branch.trackme.merge", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(".", await _repo.Config.GetStringAsync("branch.moved.remote", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("refs/heads/master", await _repo.Config.GetStringAsync("branch.moved.merge", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Move_HeadBranch_UpdatesHead()
    {
        await WriteCommit();
        GitReference? master = await _repo.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(master);

        await master!.MoveAsync("master2", cancellationToken: TestContext.Current.CancellationToken);

        // HEAD should now point to master2
        GitReference? head = await _repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.True(head is GitSymbolicReference);
        Assert.Equal("refs/heads/master2", ((GitSymbolicReference)head).TargetName);
    }

    // ── ForEach (iterator) ──────────────────────────────────────────────

    [Fact]
    public async Task ForEach_Local_Branches_ReturnsAllLocal()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.BranchCreateAsync("br2", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.BranchCreateAsync("br3", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        var branches = new List<(GitReference Branch, GitBranchType Type)>();
        await foreach ((GitReference Branch, GitBranchType Type) b in _repo.BranchForEachAsync(GitBranchType.Local, cancellationToken: TestContext.Current.CancellationToken))
        {
            branches.Add(b);
        }
        var names = branches.Select(b => b.Branch.Name).OrderBy(n => n).ToList();
        Assert.Contains("refs/heads/master", names);
        Assert.Contains("refs/heads/br2", names);
        Assert.Contains("refs/heads/br3", names);
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public async Task ForEach_Remote_Branches_ReturnsAllRemote()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/remotes/origin/master", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateAsync("refs/remotes/origin/feature", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        var branches = new List<(GitReference Branch, GitBranchType Type)>();
        await foreach ((GitReference Branch, GitBranchType Type) b in _repo.BranchForEachAsync(GitBranchType.Remote, cancellationToken: TestContext.Current.CancellationToken))
        {
            branches.Add(b);
        }
        Assert.Equal(2, branches.Count);
        Assert.All(branches, b => Assert.Equal(GitBranchType.Remote, b.Type));
    }

    [Fact]
    public async Task ForEach_All_Branches_ReturnsLocalAndRemote()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.BranchCreateAsync("br2", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.ReferenceCreateAsync("refs/remotes/origin/master", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        var branches = new List<(GitReference Branch, GitBranchType Type)>();
        await foreach ((GitReference Branch, GitBranchType Type) b in _repo.BranchForEachAsync(GitBranchType.All, cancellationToken: TestContext.Current.CancellationToken))
        {
            branches.Add(b);
        }
        Assert.True(branches.Count >= 3); // master, br2, origin/master
    }

    // ── SetUpstream ─────────────────────────────────────────────────────

    [Fact]
    public async Task SetUpstream_LocalUpstream_SetsConfig()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        GitReference? branch = await _repo.BranchLookupAsync("trackme", GitBranchType.Local, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(branch);

        await branch!.SetUpstreamAsync("master", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(".", await _repo.Config.GetStringAsync("branch.trackme.remote", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("refs/heads/master", await _repo.Config.GetStringAsync("branch.trackme.merge", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetUpstream_RemoteUpstream_SetsConfig()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/remotes/origin/master", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        // The remote must have a URL — git_remote_list only lists remotes with remote.<name>.url.
        await _repo.Config.SetStringAsync("remote.origin.url", "file:///tmp/origin.git", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", cancellationToken: TestContext.Current.CancellationToken);

        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        GitReference? branch = await _repo.BranchLookupAsync("trackme", GitBranchType.Local, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(branch);

        await branch!.SetUpstreamAsync("origin/master", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("origin", await _repo.Config.GetStringAsync("branch.trackme.remote", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("refs/heads/master", await _repo.Config.GetStringAsync("branch.trackme.merge", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetUpstream_Null_UnsetsConfig()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        GitReference? branch = await _repo.BranchLookupAsync("trackme", GitBranchType.Local, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(branch);
        await _repo.Config.SetStringAsync("branch.trackme.remote", ".", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.merge", "refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        await branch!.SetUpstreamAsync(null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await _repo.Config.GetStringAsync("branch.trackme.remote", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.Config.GetStringAsync("branch.trackme.merge", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetUpstream_NonBranch_Throws()
    {
        GitOid commitOid = await WriteCommit();
        await CreateTag("v1", commitOid);
        GitReference? tag = await _repo.ReferenceLookupAsync("refs/tags/v1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(tag);

        await Assert.ThrowsAsync<GitException>(async () => await tag!.SetUpstreamAsync("master", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetUpstream_UnknownUpstream_ThrowsNotFound()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        GitReference? branch = await _repo.BranchLookupAsync("trackme", GitBranchType.Local, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(branch);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await branch!.SetUpstreamAsync("nonexistent", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── Upstream (read) ─────────────────────────────────────────────────

    [Fact]
    public async Task Upstream_LocalUpstream_ResolvesRef()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.remote", ".", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.merge", "refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        GitReference? upstream = await _repo.BranchUpstreamAsync("refs/heads/trackme", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(upstream);
        Assert.Equal("refs/heads/master", upstream.Name);
    }

    [Fact]
    public async Task Upstream_NoUpstream_ThrowsNotFound()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await _repo.BranchUpstreamAsync("refs/heads/trackme", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task UpstreamRemote_ReturnsConfigValue()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.remote", "origin", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("origin", await _repo.BranchUpstreamRemoteAsync("refs/heads/trackme", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpstreamMerge_ReturnsConfigValue()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.merge", "refs/heads/main", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("refs/heads/main", await _repo.BranchUpstreamMergeAsync("refs/heads/trackme", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpstreamName_RemoteUpstream_ResolvesName()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.ReferenceCreateAsync("refs/remotes/origin/master", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.remote", "origin", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.merge", "refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        string name = await _repo.BranchUpstreamNameAsync("refs/heads/trackme", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("refs/remotes/origin/master", name);
    }

    [Fact]
    public async Task UpstreamName_LocalUpstream_ReturnsMergeValue()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.remote", ".", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.merge", "refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        string name = await _repo.BranchUpstreamNameAsync("refs/heads/trackme", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("refs/heads/master", name);
    }

    // ── Remote (upstream Remote object) ────────────────────────────────

    [Fact]
    public async Task Remote_ConfigUpstream_ReturnsRemote()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.remote", "origin", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.merge", "refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        await using LibGit2CS.Remote.GitRemote? remote = await _repo.BranchRemoteAsync("refs/heads/trackme", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(remote);
        Assert.Equal("origin", remote!.Name);
        Assert.Equal("https://example.com/repo.git", remote.Url);
    }

    [Fact]
    public async Task Remote_NoUpstream_ReturnsNull()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await _repo.BranchRemoteAsync("refs/heads/trackme", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Remote_LocalUpstream_ReturnsNull()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.remote", ".", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.merge", "refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        // Local upstream ("." remote) has no Remote object.
        Assert.Null(await _repo.BranchRemoteAsync("refs/heads/trackme", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Remote_StaleConfig_ReturnsNull()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.remote", "origin", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.Config.SetStringAsync("branch.trackme.merge", "refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        // Remove the remote section but leave branch.<name>.remote pointing at it.
        await _repo.Config.DeleteSectionAsync("remote.origin", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await _repo.BranchRemoteAsync("refs/heads/trackme", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Remote_RefspecFallback_ReturnsRemote()
    {
        GitOid commitOid = await WriteCommit();
        await _repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.BranchCreateAsync("trackme", commitOid, cancellationToken: TestContext.Current.CancellationToken);
        // No branch.trackme.remote config — RemoteName falls back to refspec match.

        await using LibGit2CS.Remote.GitRemote? remote = await _repo.BranchRemoteAsync("refs/heads/trackme", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(remote);
        Assert.Equal("origin", remote!.Name);
    }

    [Fact]
    public async Task Remote_NullArguments_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await _repo.BranchRemoteAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
    }
}
