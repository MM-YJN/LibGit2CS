using System.Text.RegularExpressions;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Remote;

/// <summary>
/// Golden tests for remote management — ported from libgit2's
/// <c>tests/libgit2/network/remote/{remotes,rename,delete,defaultbranch,isvalidname,tag}.c</c>
/// and <c>tests/libgit2/network/cred.c</c>.
/// </summary>
public sealed class RemoteTests : IDisposable
{
    private readonly string _tempDir;

    public RemoteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RemoteMgmt_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private async ValueTask<GitRepository> CreateRepo(string name, bool bare = false)
    {
        string repoPath = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoPath, isBare: bare, new GitContext());
    }

    private async ValueTask<GitRepository> CreateRepoWithCommit(string name, bool bare = true)
    {
        GitRepository repo = await CreateRepo(name, bare);
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("README.md", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);
        GitSignature sig = TestSig();
        await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "initial\n",
            UpdateRef = "refs/heads/master",
        });
        return repo;
    }

    // ── Create / Lookup / List ─────────────────────────────────────────

    [Fact]
    public async Task Create_WritesConfigAndReturnsRemote()
    {
        await using GitRepository repo = await CreateRepo("test");
        GitRemote remote = await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("origin", remote.Name);
        Assert.Equal("https://example.com/repo.git", remote.Url);
        Assert.Single(remote.RefSpecs);
        Assert.Equal("+refs/heads/*:refs/remotes/origin/*", remote.RefSpecs[0].String);

        // Config should have the entries.
        Assert.Equal("https://example.com/repo.git", await repo.Config.GetStringAsync("remote.origin.url", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("+refs/heads/*:refs/remotes/origin/*",
            await repo.Config.GetStringAsync("remote.origin.fetch", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lookup_LoadsFromConfig()
    {
        await using GitRepository repo = await CreateRepo("test");
        await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        GitRemote remote = await repo.RemoteLookupAsync("origin", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("origin", remote.Name);
        Assert.Equal("https://example.com/repo.git", remote.Url);
        Assert.Single(remote.RefSpecs);
    }

    [Fact]
    public async Task Lookup_ThrowsOnMissing()
    {
        await using GitRepository repo = await CreateRepo("test");

        await Assert.ThrowsAsync<GitException>(async () => await repo.RemoteLookupAsync("nonexistent", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task List_ReturnsAllRemotes()
    {
        await using GitRepository repo = await CreateRepo("test");
        await repo.RemoteCreateAsync("origin", "https://example.com/origin.git", cancellationToken: TestContext.Current.CancellationToken);
        await repo.RemoteCreateAsync("upstream", "https://example.com/upstream.git", cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> names = await repo.RemoteListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("origin", names);
        Assert.Contains("upstream", names);
        Assert.Equal(2, names.Count);
    }

    [Fact]
    public async Task List_EmptyRepo_ReturnsEmpty()
    {
        await using GitRepository repo = await CreateRepo("test");
        IReadOnlyList<string> names = await repo.RemoteListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(names);
    }

    // ── SetUrl / SetPushUrl / AddFetch / AddPush ────────────────────────

    [Fact]
    public async Task SetUrl_UpdatesConfig()
    {
        await using GitRepository repo = await CreateRepo("test");
        GitRemote remote = await repo.RemoteCreateAsync("origin", "https://old.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        await remote.SetUrlAsync("https://new.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://new.com/repo.git", remote.Url);
        Assert.Equal("https://new.com/repo.git", await repo.Config.GetStringAsync("remote.origin.url", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetPushUrl_UpdatesConfig()
    {
        await using GitRepository repo = await CreateRepo("test");
        GitRemote remote = await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        await remote.SetPushUrlAsync("https://push.example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://push.example.com/repo.git", remote.PushUrl);
        Assert.Equal("https://push.example.com/repo.git", await repo.Config.GetStringAsync("remote.origin.pushurl", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetPushUrl_Null_DeletesConfig()
    {
        await using GitRepository repo = await CreateRepo("test");
        GitRemote remote = await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);
        await remote.SetPushUrlAsync("https://push.example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        await remote.SetPushUrlAsync(null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await repo.Config.GetStringAsync("remote.origin.pushurl", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddFetch_AppendsToConfig()
    {
        await using GitRepository repo = await CreateRepo("test");
        GitRemote remote = await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        await remote.AddFetchAsync("refs/tags/*:refs/tags/*", cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> specs = await repo.Config.GetMultiAsync("remote.origin.fetch", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("+refs/heads/*:refs/remotes/origin/*", specs);
        Assert.Contains("refs/tags/*:refs/tags/*", specs);
    }

    [Fact]
    public async Task AddPush_WritesToConfig()
    {
        await using GitRepository repo = await CreateRepo("test");
        GitRemote remote = await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        await remote.AddPushAsync("refs/heads/master:refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> specs = await repo.Config.GetMultiAsync("remote.origin.push", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("refs/heads/master:refs/heads/master", specs);
    }

    // ── Delete ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesConfig()
    {
        await using GitRepository repo = await CreateRepo("test");
        await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(await repo.Config.GetStringAsync("remote.origin.url", cancellationToken: TestContext.Current.CancellationToken));

        await repo.RemoteDeleteAsync("origin", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await repo.Config.GetStringAsync("remote.origin.url", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await repo.Config.GetStringAsync("remote.origin.fetch", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_RemovesBranchUpstreamConfig()
    {
        await using GitRepository repo = await CreateRepo("test");
        await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("branch.master.remote", "origin", cancellationToken: TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("branch.master.merge", "refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        await repo.RemoteDeleteAsync("origin", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await repo.Config.GetStringAsync("branch.master.remote", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await repo.Config.GetStringAsync("branch.master.merge", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_RemovesTrackingRefs()
    {
        await using GitRepository source = await CreateRepoWithCommit("source");
        await using GitRepository target = await CreateRepo("target");

        GitRemote remote = await target.RemoteCreateAsync("origin",
            FixtureLoader.TestFileUrl(source.Path), cancellationToken: TestContext.Current.CancellationToken);
        // Fetch to create tracking refs.
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Verify tracking ref exists.
        Assert.NotNull(await target.ReferenceLookupAsync("refs/remotes/origin/master", cancellationToken: TestContext.Current.CancellationToken));

        await target.RemoteDeleteAsync("origin", cancellationToken: TestContext.Current.CancellationToken);

        // Tracking ref should be gone.
        Assert.Null(await target.ReferenceLookupAsync("refs/remotes/origin/master", cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Rename ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Rename_MovesConfigSection()
    {
        await using GitRepository repo = await CreateRepo("test");
        await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> problems = await repo.RemoteRenameAsync("origin", "upstream", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(problems);
        Assert.Null(await repo.Config.GetStringAsync("remote.origin.url", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("https://example.com/repo.git", await repo.Config.GetStringAsync("remote.upstream.url", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("+refs/heads/*:refs/remotes/upstream/*",
            await repo.Config.GetStringAsync("remote.upstream.fetch", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rename_UpdatesBranchConfig()
    {
        await using GitRepository repo = await CreateRepo("test");
        await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("branch.master.remote", "origin", cancellationToken: TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("branch.master.merge", "refs/heads/master", cancellationToken: TestContext.Current.CancellationToken);

        await repo.RemoteRenameAsync("origin", "upstream", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("upstream", await repo.Config.GetStringAsync("branch.master.remote", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rename_RawNonUtf8RemoteValue_NotUpdated()
    {
        // Pin: update_remote_name_cb (remote.c:2380) compares the raw value bytes with strcmp. A branch.*.remote value with a raw 0xE9 byte
        // is NOT "origin" (byte-exact) and must not be rewritten — a string tier would decode it to U+FFFD (also not equal, but through a lossy decode).
        await using GitRepository repo = await CreateRepo("test");
        await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);
        await repo.Config.SetBytesAsync("branch.master.remote", new byte[] { 0x6F, 0x72, 0x69, 0x67, 0x69, 0x6E, 0xE9 }, cancellationToken: TestContext.Current.CancellationToken);

        await repo.RemoteRenameAsync("origin", "upstream", cancellationToken: TestContext.Current.CancellationToken);

        // The raw-byte value survives untouched.
        byte[]? value = await repo.Config.GetBytesAsync("branch.master.remote", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([0x6F, 0x72, 0x69, 0x67, 0x69, 0x6E, 0xE9], value);
    }

    [Fact]
    public async Task Rename_RewritesDefaultFetchRefSpec()
    {
        await using GitRepository repo = await CreateRepo("test");
        await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        await repo.RemoteRenameAsync("origin", "upstream", cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> fetchSpecs = await repo.Config.GetMultiAsync("remote.upstream.fetch", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("+refs/heads/*:refs/remotes/upstream/*", fetchSpecs);
    }

    [Fact]
    public async Task Rename_RawNonUtf8FetchSpec_ReportedAsProblemAndSurvives()
    {
        // Pin: rename_fetch_refspecs (remote.c:2528) compares the raw spec bytes with strcmp — a non-UTF-8 spec never equals the (ASCII)
        // default, is reported as a problem (display-decoded), and survives the section rename byte-exact.
        byte[] rawSpec = [.. "+refs/heads/*:refs/remotes/orig"u8.ToArray(), 0xE9, .. "n/*"u8.ToArray()];
        await using GitRepository repo = await CreateRepo("test");
        await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);
        await repo.Config.DeleteAsync("remote.origin.fetch", TestContext.Current.CancellationToken);
        await repo.Config.SetBytesAsync("remote.origin.fetch", rawSpec, cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> problems = await repo.RemoteRenameAsync("origin", "upstream", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(problems);
        byte[]? value = await repo.Config.GetBytesAsync("remote.upstream.fetch", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(rawSpec, value);
    }

    [Fact]
    public async Task Rename_NonDefaultRefSpec_FailsAsMultivar()
    {
        // C's rename_fetch_refspecs rewrites the default fetchspec with git_config_set_string, which FAILS on a multivar key ("entry is not unique due to being
        // a multivar", GIT_ERROR/GIT_ERROR_CONFIG) — a remote with the default fetchspec plus an extra fetch refspec errors the whole rename (renaming
        // a two-fetchspec remote → -1).
        await using GitRepository repo = await CreateRepo("test");
        await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);
        await repo.Config.SetMultiAsync("remote.origin.fetch", new Regex("^$"), "refs/tags/*:refs/tags/*", cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.RemoteRenameAsync("origin", "upstream", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Config, ex.Category);
        Assert.Contains("multivar", ex.Message);
    }

    [Fact]
    public async Task Rename_DestinationExists_Throws()
    {
        await using GitRepository repo = await CreateRepo("test");
        await repo.RemoteCreateAsync("origin", "https://a.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);
        await repo.RemoteCreateAsync("upstream", "https://b.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () => await repo.RemoteRenameAsync("origin", "upstream", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rename_InvalidName_Throws()
    {
        await using GitRepository repo = await CreateRepo("test");
        await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () => await repo.RemoteRenameAsync("origin", "not a valid/name", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rename_SourceNotFound_Throws()
    {
        await using GitRepository repo = await CreateRepo("test");
        await Assert.ThrowsAsync<GitException>(async () => await repo.RemoteRenameAsync("nonexistent", "newname", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rename_MovesTrackingRefs()
    {
        await using GitRepository source = await CreateRepoWithCommit("source");
        await using GitRepository target = await CreateRepo("target");

        GitRemote remote = await target.RemoteCreateAsync("origin",
            FixtureLoader.TestFileUrl(source.Path), cancellationToken: TestContext.Current.CancellationToken);
        // Fetch to create tracking refs.
        await remote.FetchAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(await target.ReferenceLookupAsync("refs/remotes/origin/master", cancellationToken: TestContext.Current.CancellationToken));

        await target.RemoteRenameAsync("origin", "upstream", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await target.ReferenceLookupAsync("refs/remotes/origin/master", cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await target.ReferenceLookupAsync("refs/remotes/upstream/master", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rename_NewNameCanContainDots()
    {
        await using GitRepository repo = await CreateRepo("test");
        await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> problems = await repo.RemoteRenameAsync("origin", "my.remote", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(problems);
        Assert.Equal("https://example.com/repo.git", await repo.Config.GetStringAsync("remote.my.remote.url", cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── NameIsValid ────────────────────────────────────────────────────

    [Fact]
    public async Task NameIsValid_ValidNames_ReturnTrue()
    {
        Assert.True(GitRemote.NameIsValid("origin"));
        Assert.True(GitRemote.NameIsValid("upstream"));
        Assert.True(GitRemote.NameIsValid("my.remote"));
        Assert.True(GitRemote.NameIsValid("remote1"));
    }

    [Fact]
    public async Task NameIsValid_InvalidNames_ReturnFalse()
    {
        Assert.False(GitRemote.NameIsValid(""));
        Assert.False(GitRemote.NameIsValid(null));
        Assert.False(GitRemote.NameIsValid("not valid/name"));
        Assert.False(GitRemote.NameIsValid("not valid name"));
    }

    // ── DefaultBranch ──────────────────────────────────────────────────

    [Fact]
    public async Task DefaultBranch_ReturnsHeadSymref()
    {
        await using GitRepository source = await CreateRepoWithCommit("source");
        await using GitRepository target = await CreateRepo("target");

        GitRemote remote = await target.RemoteCreateAsync("origin",
            FixtureLoader.TestFileUrl(source.Path), cancellationToken: TestContext.Current.CancellationToken);
        await remote.ConnectAsync(LibGit2CS.Transports.GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        string? defaultBranch = await remote.DefaultBranchAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(defaultBranch);
        Assert.Equal("refs/heads/master", defaultBranch);
        await remote.DisconnectAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DefaultBranch_EmptyRepo_ReturnsNull()
    {
        await using GitRepository source = await CreateRepo("source", bare: true);
        await using GitRepository target = await CreateRepo("target");

        GitRemote remote = await target.RemoteCreateAsync("origin",
            FixtureLoader.TestFileUrl(source.Path), cancellationToken: TestContext.Current.CancellationToken);
        await remote.ConnectAsync(LibGit2CS.Transports.GitDirection.Fetch, null, TestContext.Current.CancellationToken);

        string? defaultBranch = await remote.DefaultBranchAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(defaultBranch);
        await remote.DisconnectAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DefaultBranch_NotConnected_Throws()
    {
        await using GitRepository target = await CreateRepo("target");
        GitRemote remote = await target.RemoteCreateAsync("origin",
            "file:///nonexistent", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () => await remote.DefaultBranchAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Duplicate ──────────────────────────────────────────────────────

    [Fact]
    public async Task Duplicate_CopiesFields()
    {
        await using GitRepository repo = await CreateRepo("test");
        GitRemote remote = await repo.RemoteCreateAsync("origin", "https://example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);
        await remote.SetPushUrlAsync("https://push.example.com/repo.git", cancellationToken: TestContext.Current.CancellationToken);

        GitRemote dup = remote.Duplicate();

        Assert.Equal("origin", dup.Name);
        Assert.Equal("https://example.com/repo.git", dup.Url);
        Assert.Equal("https://push.example.com/repo.git", dup.PushUrl);
        Assert.Equal(remote.RefSpecs.Count, dup.RefSpecs.Count);
    }

    // ── ApplyInsteadOf ──────────────────────────────────────────────────

    [Fact]
    public async Task ApplyInsteadOf_NoMatch_ReturnsOriginalUrl()
    {
        await using GitRepository repo = await CreateRepo("test");

        string? result = await repo.RemoteApplyInsteadOfAsync("https://example.com/repo.git", LibGit2CS.Transports.GitDirection.Fetch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://example.com/repo.git", result);
    }

    [Fact]
    public async Task ApplyInsteadOf_MatchingPrefix_Rewrites()
    {
        await using GitRepository repo = await CreateRepo("test");
        await repo.Config.SetStringAsync("url.https://github.com/.insteadof", "gh:", cancellationToken: TestContext.Current.CancellationToken);

        string? result = await repo.RemoteApplyInsteadOfAsync("gh:user/repo.git", LibGit2CS.Transports.GitDirection.Fetch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://github.com/user/repo.git", result);
    }

    [Fact]
    public async Task ApplyInsteadOf_LongestMatchWins()
    {
        await using GitRepository repo = await CreateRepo("test");
        await repo.Config.SetStringAsync("url.https://github.com/.insteadof", "gh:", cancellationToken: TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("url.https://github.com/org/.insteadof", "gh:org:", cancellationToken: TestContext.Current.CancellationToken);

        string? result = await repo.RemoteApplyInsteadOfAsync("gh:org:repo.git", LibGit2CS.Transports.GitDirection.Fetch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://github.com/org/repo.git", result);
    }

    [Fact]
    public async Task ApplyInsteadOf_PushDirection_UsesPushInsteadOf()
    {
        await using GitRepository repo = await CreateRepo("test");
        await repo.Config.SetStringAsync("url.https://push.github.com/.pushinsteadof", "gh:", cancellationToken: TestContext.Current.CancellationToken);

        string? result = await repo.RemoteApplyInsteadOfAsync("gh:user/repo.git", LibGit2CS.Transports.GitDirection.Push, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://push.github.com/user/repo.git", result);
    }

    [Fact]
    public async Task ApplyInsteadOf_PushWithoutPushInsteadOf_NoRewrite()
    {
        await using GitRepository repo = await CreateRepo("test");
        // insteadof is set but pushinsteadof is not.
        // For push direction, pushinsteadof is used — no match → no rewrite.
        await repo.Config.SetStringAsync("url.https://github.com/.insteadof", "gh:", cancellationToken: TestContext.Current.CancellationToken);

        string? result = await repo.RemoteApplyInsteadOfAsync("gh:user/repo.git", LibGit2CS.Transports.GitDirection.Push, cancellationToken: TestContext.Current.CancellationToken);

        // Push uses pushinsteadof only (not insteadof), so no match → original URL.
        Assert.Equal("gh:user/repo.git", result);
    }

    [Fact]
    public async Task ApplyInsteadOf_NonUtf8Value_NoDecodeMatch()
    {
        // Pin: apply_insteadof (remote.c:3079-3142) runs git__prefixcmp over the raw value bytes — a raw 0xE9 value byte is compared
        // byte-exact against the URL's UTF-8 bytes (C3 A9) and does NOT match (no U+FFFD decode magic). The result is the original URL.
        await using GitRepository repo = await CreateRepo("test");
        await repo.Config.SetBytesAsync("url.https://example.com/.insteadof", new byte[] { 0x63, 0x61, 0x66, 0xE9 }, cancellationToken: TestContext.Current.CancellationToken);

        string? result = await repo.RemoteApplyInsteadOfAsync("caf\u00e9:user/repo.git", LibGit2CS.Transports.GitDirection.Fetch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("caf\u00e9:user/repo.git", result);
    }

    [Fact]
    public async Task ApplyInsteadOf_Utf8Value_StillRewrites()
    {
        // Control: a valid-UTF-8 value ("café:") still matches and rewrites — the byte-domain prefix compare is byte-exact for valid UTF-8,
        // identical to the string tier.
        await using GitRepository repo = await CreateRepo("test");
        await repo.Config.SetStringAsync("url.https://example.com/.insteadof", "caf\u00e9:", cancellationToken: TestContext.Current.CancellationToken);

        string? result = await repo.RemoteApplyInsteadOfAsync("caf\u00e9:user/repo.git", LibGit2CS.Transports.GitDirection.Fetch, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://example.com/user/repo.git", result);
    }

    // ── CanonicalizeUrl ─────────────────────────────────────────────────

    [Fact]
    public async Task CanonicalizeUrl_Empty_Throws()
    {
        Assert.Throws<GitException>(() => GitRemote.CanonicalizeUrl(""));
        Assert.Throws<GitException>(() => GitRemote.CanonicalizeUrl(null!));
    }

    [Fact]
    public async Task CanonicalizeUrl_NormalUrl_Passthrough()
    {
        Assert.Equal("https://example.com/repo.git",
            GitRemote.CanonicalizeUrl("https://example.com/repo.git"));
        Assert.Equal("file:///path/to/repo",
            GitRemote.CanonicalizeUrl("file:///path/to/repo"));
        Assert.Equal("/local/path",
            GitRemote.CanonicalizeUrl("/local/path"));
    }

    // ── DefaultFetchSpec ────────────────────────────────────────────────

    [Fact]
    public async Task DefaultFetchSpec_ReturnsCorrectFormat()
    {
        Assert.Equal("+refs/heads/*:refs/remotes/origin/*",
            GitRemote.DefaultFetchSpec("origin"));
        Assert.Equal("+refs/heads/*:refs/remotes/upstream/*",
            GitRemote.DefaultFetchSpec("upstream"));
    }

    // ── CredentialHelpers ──────────────────────────────────────────────

    [Fact]
    public async Task UserPass_ValidPayload_ReturnsUserPassCredential()
    {
        var payload = new GitUserPassPayload
        {
            Username = "user",
            Password = "pass",
        };

        GitCredential? cred = GitCredentialHelpers.UserPass(payload, null, GitCredentialType.UserPassPlaintext);

        Assert.NotNull(cred);
        Assert.IsType<GitUserPassCredential>(cred);
        Assert.Equal("user", cred.Username);
        Assert.Equal("pass", ((GitUserPassCredential)cred).Password);
    }

    [Fact]
    public async Task UserPass_OnlyUsernameAllowed_ReturnsUsernameCredential()
    {
        var payload = new GitUserPassPayload
        {
            Username = "user",
            Password = "pass",
        };

        GitCredential? cred = GitCredentialHelpers.UserPass(payload, null, GitCredentialType.Username);

        Assert.NotNull(cred);
        Assert.IsType<GitUsernameCredential>(cred);
        Assert.Equal("user", cred.Username);
    }

    [Fact]
    public async Task UserPass_UsesUrlUsernameWhenPayloadHasNone()
    {
        var payload = new GitUserPassPayload
        {
            Password = "pass",
        };

        GitCredential? cred = GitCredentialHelpers.UserPass(payload, "urluser", GitCredentialType.UserPassPlaintext);

        Assert.NotNull(cred);
        Assert.Equal("urluser", cred.Username);
    }

    [Fact]
    public async Task UserPass_NoUsername_ReturnsNull()
    {
        var payload = new GitUserPassPayload
        {
            Password = "pass",
        };

        GitCredential? cred = GitCredentialHelpers.UserPass(payload, null, GitCredentialType.UserPassPlaintext);

        Assert.Null(cred);
    }

    [Fact]
    public async Task UserPass_EmptyPassword_IsAccepted()
    {
        // C (credential_helpers.c:24-52): only a NULL password fails — an
        // empty-string password is accepted (pointer-presence semantics).
        var payload = new GitUserPassPayload
        {
            Username = "user",
            Password = "",
        };

        GitCredential? cred = GitCredentialHelpers.UserPass(payload, null, GitCredentialType.UserPassPlaintext);

        Assert.NotNull(cred);
    }

    [Fact]
    public async Task UserPass_TypeNotAllowed_ReturnsNull()
    {
        var payload = new GitUserPassPayload
        {
            Username = "user",
            Password = "pass",
        };

        // Only SSH key type allowed — UserPass doesn't match.
        GitCredential? cred = GitCredentialHelpers.UserPass(payload, null, GitCredentialType.SshKey);

        Assert.Null(cred);
    }

    [Fact]
    public async Task UserPass_PayloadUsernameTakesPrecedenceOverUrl()
    {
        var payload = new GitUserPassPayload
        {
            Username = "payloaduser",
            Password = "pass",
        };

        GitCredential? cred = GitCredentialHelpers.UserPass(payload, "urluser", GitCredentialType.UserPassPlaintext);

        Assert.NotNull(cred);
        Assert.Equal("payloaduser", cred.Username);
    }
}
