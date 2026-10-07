using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Refs;

public sealed class ReferencesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _extractedPaths = [];

    public ReferencesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RefsTests_" + Guid.NewGuid().ToString("N")[..8]);
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
            catch (IOException)
            {
            }
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Lookup_HEAD_ReturnsSymbolic()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitReference? head = await repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(head);
        Assert.True(head!.IsSymbolic);
        Assert.IsType<GitSymbolicReference>(head);
        Assert.Equal("refs/heads/master", ((GitSymbolicReference)head).TargetName);
    }

    [Fact]
    public async Task Lookup_DirectRef_ReturnsDirect()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitReference? master = await repo.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(master);
        Assert.False(master!.IsSymbolic);
        Assert.IsType<GitDirectReference>(master);
        Assert.Equal(
            GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1),
            ((GitDirectReference)master).Target);
    }

    [Fact]
    public async Task Lookup_PackedOnly_ReturnsDirect()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitReference? packed = await repo.ReferenceLookupAsync("refs/heads/packed", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(packed);
        Assert.False(packed!.IsSymbolic);
        Assert.Equal(
            GitOid.Parse("41bc8c69075bbdb46c5c6f0566cc8cc5b46e8bd9".AsSpan(), GitHashAlgorithmKind.Sha1),
            ((GitDirectReference)packed).Target);
    }

    [Fact]
    public async Task Lookup_LooseShadowsPacked()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // packed-test exists both as loose (4a202b...) and packed (5b5b02...).
        // Loose should win.
        GitReference? ref_ = await repo.ReferenceLookupAsync("refs/heads/packed-test", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(ref_);
        Assert.Equal(
            GitOid.Parse("4a202b346bb0fb0db7eff3cffeb3c70babbd2045".AsSpan(), GitHashAlgorithmKind.Sha1),
            ((GitDirectReference)ref_!).Target);
    }

    [Fact]
    public async Task Lookup_Nonexistent_ReturnsNull()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        Assert.Null(await repo.ReferenceLookupAsync("refs/heads/does-not-exist", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lookup_InvalidName_ThrowsInvalidSpec()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.ReferenceLookupAsync("refs/heads/with space", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public async Task Resolve_HEAD_ReturnsDirect()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitReference? head = await repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(head);
        Assert.False(head!.IsSymbolic);
        Assert.Equal(
            GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1),
            ((GitDirectReference)head).Target);
    }

    [Fact]
    public async Task Resolve_NestedSymbolic_Resolves()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        // HEAD_TRACKER → HEAD → refs/heads/master
        GitReference? resolved = await repo.ReferenceResolveAsync("HEAD_TRACKER", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Assert.False(resolved!.IsSymbolic);
        Assert.Equal(
            GitOid.Parse("a65fedf39aefe402d3bb6e24df4d4f5fe4547750".AsSpan(), GitHashAlgorithmKind.Sha1),
            ((GitDirectReference)resolved).Target);
    }

    [Fact]
    public async Task Resolve_DirectRef_ReturnsDirect()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitReference? master = await repo.ReferenceResolveAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(master);
        Assert.False(master!.IsSymbolic);
    }

    [Fact]
    public async Task Resolve_DanglingSymbolic_ReturnsNull()
    {
        // Create an empty bare repo and point HEAD at a nonexistent branch.
        string extractedPath = ExtractRepo("empty_bare.zip");
        string gitDir = Path.Combine(extractedPath, "empty_bare.git");

        // empty_bare.git/HEAD is "ref: refs/heads/master" but master doesn't exist.
        await using GitRepository repo = await GitRepository.OpenAsync(gitDir, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // Lookup returns the symbolic ref (no resolution).
        GitReference? head = await repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.True(head!.IsSymbolic);

        // Resolve returns null (dangling chain).
        Assert.Null(await repo.ReferenceResolveAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lookup_HEAD_OnUnbornBranch_ReturnsSymbolic()
    {
        string extractedPath = ExtractRepo("empty_bare.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(extractedPath, "empty_bare.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        GitReference? head = await repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(head);
        Assert.True(head!.IsSymbolic);
        Assert.Equal("refs/heads/master", ((GitSymbolicReference)head).TargetName);
    }

    [Fact]
    public async Task SymbolicReference_Target_Cascades()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        var head = await repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken) as GitSymbolicReference;

        Assert.NotNull(head);
        GitReference? target = await head!.TargetAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(target);
        Assert.False(target!.IsSymbolic);
    }

    [Fact]
    public async Task List_ReturnsAllRefs()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        var names = new List<string>();
        await foreach (string n in repo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            names.Add(n);
        }
        names = names.OrderBy(n => n).ToList();

        // Should include all loose + packed refs (deduped).
        Assert.Contains("refs/heads/master", names);
        Assert.Contains("refs/heads/packed", names);
        Assert.Contains("refs/heads/packed-test", names);
        Assert.Contains("refs/tags/test", names);
        Assert.Contains("refs/remotes/test/master", names);
        Assert.Contains("refs/notes/fanout", names);
    }

    [Fact]
    public async Task List_WithGlob_Filters()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        var names = new List<string>();
        await foreach (string n in repo.Refs.ListNamesAsync("refs/heads/*", cancellationToken: TestContext.Current.CancellationToken))
        {
            names.Add(n);
        }
        names = names.OrderBy(n => n).ToList();

        Assert.All(names, n => Assert.StartsWith("refs/heads/", n));
        Assert.Contains("refs/heads/master", names);
        Assert.Contains("refs/heads/packed", names);
        Assert.DoesNotContain("refs/tags/test", names);
    }

    [Fact]
    public async Task List_Glob_Star_CrossesSlash()
    {
        // libgit2 matches ref-name globs with wildmatch(glob, name, 0): a single
        // '*' crosses '/'. So "refs/remotes/*" must match "refs/remotes/test/master".
        await using GitRepository repo = await OpenTestRepoAsync();

        var names = new List<string>();
        await foreach (string n in repo.Refs.ListNamesAsync("refs/remotes/*", cancellationToken: TestContext.Current.CancellationToken))
        {
            names.Add(n);
        }

        Assert.Contains("refs/remotes/test/master", names);
    }

    [Fact]
    public async Task List_Glob_RootStar_MatchesDeepRefs()
    {
        // Regression for fetch negotiation: GitSmartProtocol.NegotiateFetchAsync
        // enumerates local tips with PushGlobAsync("refs/*"). Because every concrete
        // ref is two-or-more segments deep, a matcher where '*' does not cross '/'
        // matches NONE of them, starving negotiation of "have" lines. Under libgit2
        // semantics '*' crosses '/', so "refs/*" must yield refs of every depth.
        await using GitRepository repo = await OpenTestRepoAsync();

        var names = new List<string>();
        await foreach (string n in repo.Refs.ListNamesAsync("refs/*", cancellationToken: TestContext.Current.CancellationToken))
        {
            names.Add(n);
        }

        Assert.NotEmpty(names);
        Assert.Contains("refs/heads/master", names);
        Assert.Contains("refs/remotes/test/master", names);
        Assert.Contains("refs/notes/fanout", names);
        Assert.Contains("refs/tags/test", names);
        Assert.Contains("refs/blobs/annotated_tag_to_blob", names);
    }

    [Fact]
    public async Task HasLog_HEAD_ReturnsTrue()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        Assert.True(await repo.Refs.HasLogAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HasLog_RefWithLog_ReturnsTrue()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        Assert.True(await repo.Refs.HasLogAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HasLog_RefWithoutLog_ReturnsFalse()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        Assert.False(await repo.Refs.HasLogAsync("refs/heads/packed", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadLog_HEAD_HasEntries()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitRefLog? log = await repo.ReferenceReadLogAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(log);
        Assert.True(log!.EntryCount > 0);
    }

    [Fact]
    public async Task ReadLog_NoLogFile_CreatesEmptyReflog()
    {
        // C (refdb_fs.c:2149-2155): git_reflog_read creates the missing log
        // file and returns an empty reflog.
        await using GitRepository repo = await OpenTestRepoAsync();

        GitRefLog reflog = (await repo.ReferenceReadLogAsync("refs/heads/packed", cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal(0, reflog.EntryCount);
        Assert.True(await repo.Refs.HasLogAsync("refs/heads/packed", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Predicates_IsBranch_IsTag_Etc()
    {
        await using GitRepository repo = await OpenTestRepoAsync();

        GitReference? master = await repo.ReferenceLookupAsync("refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(master);
        Assert.True(master!.IsBranch);
        Assert.False(master.IsTag);

        GitReference? tag = await repo.ReferenceLookupAsync("refs/tags/test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(tag);
        Assert.True(tag!.IsTag);
        Assert.False(tag.IsBranch);
    }

    [Fact]
    public async Task Lookup_PeeledRef_HasPeelOid()
    {
        string extractedPath = ExtractRepo("peeled.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(extractedPath, "peeled.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        GitReference? tagRef = await repo.ReferenceLookupAsync("refs/tags/tag-inside-tags", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(tagRef);
        GitDirectReference direct = Assert.IsType<GitDirectReference>(tagRef);
        Assert.NotNull(direct.Peel);
        Assert.Equal(
            GitOid.Parse("0df1a5865c8abfc09f1f2182e6a31be550e99f07".AsSpan(), GitHashAlgorithmKind.Sha1),
            direct.Peel.Value);
    }

    [Fact]
    public async Task Lookup_DetachedHEAD_ReturnsDirect()
    {
        // Create a detached HEAD by writing a raw OID.
        string extractedPath = ExtractRepo("empty_bare.zip");
        string gitDir = Path.Combine(extractedPath, "empty_bare.git");
        await File.WriteAllTextAsync(Path.Combine(gitDir, "HEAD"),
            "0000000000000000000000000000000000000000\n", cancellationToken: TestContext.Current.CancellationToken);

        await using GitRepository repo = await GitRepository.OpenAsync(gitDir, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        GitReference? head = await repo.ReferenceLookupAsync("HEAD", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(head);
        Assert.False(head!.IsSymbolic);
    }

    [Fact]
    public async Task List_EmptyRepo_ReturnsEmpty()
    {
        string extractedPath = ExtractRepo("empty_bare.zip");
        await using GitRepository repo = await GitRepository.OpenAsync(Path.Combine(extractedPath, "empty_bare.git"), new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        var names = new List<string>();
        await foreach (string n in repo.Refs.ListNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            names.Add(n);
        }
        Assert.Empty(names);
    }

    private async ValueTask<GitRepository> OpenTestRepoAsync()
    {
        string extractedPath = ExtractRepo("testrepo.zip");
        return await GitRepository.OpenAsync(Path.Combine(extractedPath, "testrepo.git"), new GitContext());
    }

    private string ExtractRepo(string zipFileName)
    {
        string path = FixtureLoader.ExtractTreeToTemp($"Fixtures/repo/{zipFileName}");
        _extractedPaths.Add(path);
        return path;
    }
}
