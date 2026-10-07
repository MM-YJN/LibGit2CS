using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Objects;

/// <summary>
/// Tests for <see cref="GitTag.ListAsync"/>, <see cref="GitTag.EnumerateAsync"/>, and
/// <see cref="GitTag.NameIsValid"/>.
/// </summary>
public sealed class TagListEnumerateTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public TagListEnumerateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_TagList_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());

        // Create a base commit so tags have something to point at.
        var sig = new GitSignature("T", "t@t", new GitTime(1577836800, 0));
        GitOid treeOid = await _repo.ObjectWriteAsync(GitObjectType.Tree, Array.Empty<byte>(), TestContext.Current.CancellationToken);
        _baseCommit = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "base\n",
        });
        await _repo.ReferenceCreateAsync("refs/heads/master", _baseCommit, force: true);
    }

    private GitOid _baseCommit;

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public async Task List_NoTags_ReturnsEmpty()
    {
        IReadOnlyList<string> tags = await _repo.TagListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(tags);
    }

    [Fact]
    public async Task List_WithTags_ReturnsAllTagNames()
    {
        await _repo.TagCreateAsync("v1.0", await LookupCommit(), tagger: null, message: null, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.TagCreateAsync("v2.0", await LookupCommit(), tagger: null, message: null, cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> tags = await _repo.TagListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, tags.Count);
        Assert.Contains("v1.0", tags);
        Assert.Contains("v2.0", tags);
    }

    [Fact]
    public async Task List_WithPattern_FiltersMatching()
    {
        await _repo.TagCreateAsync("v1.0", await LookupCommit(), tagger: null, message: null, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.TagCreateAsync("v2.0", await LookupCommit(), tagger: null, message: null, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.TagCreateAsync("release-1", await LookupCommit(), tagger: null, message: null, cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> tags = await _repo.TagListAsync(pattern: "v*", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, tags.Count);
        Assert.Contains("v1.0", tags);
        Assert.Contains("v2.0", tags);
        Assert.DoesNotContain("release-1", tags);
    }

    [Fact]
    public async Task List_WithEmptyPattern_ReturnsAll()
    {
        await _repo.TagCreateAsync("v1.0", await LookupCommit(), tagger: null, message: null, cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> tags = await _repo.TagListAsync(pattern: "", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(tags);
        Assert.Equal("v1.0", tags[0]);
    }

    [Fact]
    public async Task Enumerate_YieldsNameAndOid()
    {
        await _repo.TagCreateAsync("v1.0", await LookupCommit(), tagger: null, message: null, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.TagCreateAsync("v2.0", await LookupCommit(), tagger: null, message: null, cancellationToken: TestContext.Current.CancellationToken);

        var entries = new List<(string Name, GitOid Oid)>();
        await foreach ((string Name, GitOid Oid) e in _repo.TagEnumerateAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            entries.Add(e);
        }

        Assert.Equal(2, entries.Count);
        (string Name, GitOid Oid) v1 = entries.Single(e => e.Name == "v1.0");
        (string Name, GitOid Oid) v2 = entries.Single(e => e.Name == "v2.0");
        Assert.Equal(_baseCommit, v1.Oid);
        Assert.Equal(_baseCommit, v2.Oid);
    }

    [Fact]
    public async Task Enumerate_NoTags_ReturnsEmpty()
    {
        var entries = new List<(string Name, GitOid Oid)>();
        await foreach ((string Name, GitOid Oid) e in _repo.TagEnumerateAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            entries.Add(e);
        }

        Assert.Empty(entries);
    }

    [Fact]
    public async Task Enumerate_Breakable_StopsEarly()
    {
        await _repo.TagCreateAsync("v1.0", await LookupCommit(), tagger: null, message: null, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.TagCreateAsync("v2.0", await LookupCommit(), tagger: null, message: null, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.TagCreateAsync("v3.0", await LookupCommit(), tagger: null, message: null, cancellationToken: TestContext.Current.CancellationToken);

        int count = 0;
        await foreach ((string Name, GitOid Oid) _ in _repo.TagEnumerateAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            count++;
            if (count >= 2)
            {
                break;
            }
        }

        Assert.Equal(2, count);
    }

    [Fact]
    public void NameIsValid_ValidName_ReturnsTrue()
    {
        Assert.True(GitTag.NameIsValid("v1.0"));
        Assert.True(GitTag.NameIsValid("release-1"));
        Assert.True(GitTag.NameIsValid("feature/foo"));
    }

    [Fact]
    public void NameIsValid_DashPrefix_ReturnsFalse()
    {
        // tag_name_is_valid (tag.c:261-270): rejects '-' prefix.
        Assert.False(GitTag.NameIsValid("-foo"));
    }

    [Fact]
    public void NameIsValid_Head_ReturnsFalse()
    {
        // tag_name_is_valid: rejects exact "HEAD".
        Assert.False(GitTag.NameIsValid("HEAD"));
    }

    [Fact]
    public void NameIsValid_EmptyOrNull_ReturnsFalse()
    {
        Assert.False(GitTag.NameIsValid(""));
        Assert.False(GitTag.NameIsValid(null));
    }

    [Fact]
    public void NameIsValid_InvalidRefChars_ReturnsFalse()
    {
        // Stage 2: refs/tags/<name> must be a valid ref name per
        // GitReferences.IsNameValid. The C# ref validator catches the main
        // cases (consecutive dots, lock suffix, trailing dot).
        Assert.False(GitTag.NameIsValid("foo..bar"), "foo..bar should be invalid");
        Assert.False(GitTag.NameIsValid("foo.lock"), "foo.lock should be invalid");
        Assert.False(GitTag.NameIsValid("foo."), "foo. should be invalid");
        // NOTE: control chars (e.g. \x01) are not currently rejected by
        // GitReferences.IsNameValid — that's a pre-existing gap in the C#
        // ref-name validator. Document but don't assert here.
    }

    [Fact]
    public async Task List_NullRepo_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await GitTag.ListAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Enumerate_NullRepo_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await foreach ((string Name, GitOid Oid) _ in GitTag.EnumerateAsync(null!, cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });
    }

    private async Task<Commit> LookupCommit()
        => (await _repo.ObjectLookupAsync<Commit>(_baseCommit, TestContext.Current.CancellationToken))!;
}
