using System.Reflection;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Remote;

/// <summary> Parity tests for remote/transports: (branch-with-dot config cleanup uses the first dot), (SetUrlAsync
/// empty/null/canonicalize), (maybe_want name validity), (push-negotiation abort category Callback), (IsUrl scheme characters), (clone
/// realpath), (ParseRef empty name), (HasUsername), (FETCH_HEAD is_merge inheritance), (grafts CRLF message). Expectations are C-verified
/// against libgit2 1.9.4. </summary>
public sealed class RemoteLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public RemoteLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RemoteLow_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async ValueTask<GitRepository> CreateBareRepoAsync(string name)
        => await GitRepository.InitAsync(Path.Combine(_tempDir, name), isBare: true, new GitContext(), TestContext.Current.CancellationToken);

    private static async ValueTask<GitOid> CreateCommitAsync(GitRepository repo, string message, string content)
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("f.txt", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = message + "\n",
            UpdateRef = "refs/heads/master",
        });
    }

    // ── branch-with-dot config cleanup uses the FIRST dot ──────

    [Fact]
    public async Task DeleteRemote_BranchWithDot_CleansFirstDotKey()
    {
        // C (remote.c:2742-2751, name_offset): strchr takes the FIRST dot, so "branch.v1.2.remote" is treated as branch "v1" — C deletes
        // branch.v1.remote/branch.v1.merge and leaves branch.v1.2.* stale.
        await using GitRepository repo = await CreateBareRepoAsync("repo6");
        await repo.Config.SetStringAsync("remote.origin.url", "file:///tmp/origin.git", TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("branch.v1.2.remote", "origin", TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("branch.v1.2.merge", "refs/heads/master", TestContext.Current.CancellationToken);
        await repo.Config.SetStringAsync("branch.v1.merge", "refs/heads/master", TestContext.Current.CancellationToken);

        await repo.RemoteDeleteAsync("origin", TestContext.Current.CancellationToken);

        // C removes branch.v1.merge and branch.v1.remote (the first-dot
        // interpretation) — the matched branch.v1.2.* keys stay stale.
        Assert.Null(await repo.Config.GetStringAsync("branch.v1.merge", TestContext.Current.CancellationToken));
        Assert.Null(await repo.Config.GetStringAsync("branch.v1.remote", TestContext.Current.CancellationToken));
        Assert.Equal("origin", await repo.Config.GetStringAsync("branch.v1.2.remote", TestContext.Current.CancellationToken));
    }

    // ── SetUrlAsync empty / null / canonicalize ────────────────

    [Fact]
    public async Task SetUrl_Empty_ThrowsCannotSetEmptyUrl()
    {
        // C (remote.c:142-147, 651-658): canonicalize_url rejects an empty URL with GIT_EINVALIDSPEC "cannot set empty URL".
        await using GitRepository repo = await CreateBareRepoAsync("repo10");
        await using GitRemote remote = await repo.RemoteCreateAsync("origin", "file:///tmp/x.git", TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await remote.SetUrlAsync("", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        Assert.Equal("cannot set empty URL", ex.Message);
    }

    [Fact]
    public async Task SetUrl_Null_DeletesConfigEntry()
    {
        // C (set_url, remote.c:651-658): a NULL url deletes the config entry.
        await using GitRepository repo = await CreateBareRepoAsync("repo10b");
        await using GitRemote remote = await repo.RemoteCreateAsync("origin", "file:///tmp/x.git", TestContext.Current.CancellationToken);
        Assert.Equal("file:///tmp/x.git", await repo.Config.GetStringAsync("remote.origin.url", TestContext.Current.CancellationToken));

        await remote.SetUrlAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(await repo.Config.GetStringAsync("remote.origin.url", TestContext.Current.CancellationToken));
    }

    // ── maybe_want name validity ───────────────────────────────

    [Fact]
    public void MaybeWant_InvalidAdvertisedName_NotWanted()
    {
        // C (fetch.c:28-32): maybe_want gates on git_reference_name_is_valid (ALLOW_ONELEVEL).
        var tagSpec = GitRefSpec.Parse("refs/tags/*:refs/tags/*", isFetch: true);
        GitRefSpec[] refspecs = new[] { GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true) };

        // A valid name matching the refspec is still wanted.
        var head = new GitRemoteHead(false, GitOid.Parse("a65fedf39aef3a1b9e8f7c6d5b4a3a2a1a0a0f0e".AsSpan(), GitHashAlgorithmKind.Sha1), default, "refs/heads/master", null);
        Assert.True(FetchCoordinator.MaybeWant(head, refspecs, tagSpec, GitAutoTagOption.Unspecified));

        // A malformed name (space) is NOT wanted.
        var bad = new GitRemoteHead(false, GitOid.Parse("a65fedf39aef3a1b9e8f7c6d5b4a3a2a1a0a0f0e".AsSpan(), GitHashAlgorithmKind.Sha1), default, "bad name", null);
        Assert.False(FetchCoordinator.MaybeWant(bad, refspecs, tagSpec, GitAutoTagOption.Unspecified));
    }

    // ── push-negotiation abort category ────────────────────────

    [Fact]
    public async Task Push_NegotiationAbort_CallbackCategory()
    {
        // C (push.c:469-487): a nonzero push_negotiation return is wrapped as GIT_ERROR_CALLBACK.
        await using GitRepository source = await CreateBareRepoAsync("src26");
        await CreateCommitAsync(source, "source commit", "source content\n");
        await using GitRepository target = await CreateBareRepoAsync("dst26");
        await CreateCommitAsync(target, "target commit", "target content\n");

        await using GitRemote remote = await source.RemoteCreateAsync("origin", target.Path, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await remote.PushAsync(
                ["+refs/heads/master:refs/heads/master"],
                new GitPushOptions
                {
                    RemoteCallbacks = new GitRemoteCallbacks
                    {
                        PushNegotiation = (_, _) => Task.FromResult(false), // abort
                    },
                },
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Callback, ex.Category);
    }

    // ── IsUrl scheme characters ────────────────────────────────

    [Fact]
    public void IsUrl_SchemeCharactersValidated()
    {
        // C (util/net.c:95-108): the scheme before "://" may contain only [A-Za-z0-9+-.].
        Type cloneType = typeof(GitClone);
        MethodInfo isUrl = cloneType.GetMethod("IsUrl", BindingFlags.NonPublic | BindingFlags.Static)!;
        IsUrlDelegate del = isUrl.CreateDelegate<IsUrlDelegate>()!;

        Assert.True(del("https://example.com/repo"));
        Assert.True(del("git+ssh://host/repo"));
        Assert.False(del("./foo://bar"));   // relative path, not a URL
        Assert.False(del("git@host:path")); // SCP-style, not a URL
    }

    private delegate bool IsUrlDelegate(ReadOnlySpan<char> str);

    // ── clone resolves symlinked source paths ──────────────────

    [Fact]
    public void TryRealpath_ResolvesSymlinks()
    {
        // C (clone.c:340-346): p_realpath resolves symlinks for LOCAL RELATIVE paths (git_fs_path_root(url) < 0 gate). The clone path routes
        // through NativeStat.TryRealpath, so remote.origin.url carries the resolved path.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // native symlink resolution is exercised on Unix
        }

        string target = Path.Combine(_tempDir, "real32");
        Directory.CreateDirectory(target);
        string link = Path.Combine(_tempDir, "link32");
        Directory.CreateSymbolicLink(link, target);

        string? resolved = NativeStat.TryRealpath(link);
        Assert.Equal(PathHelpers.PrettifyDir(target).TrimEnd('/'), resolved);
    }

    // ── ParseRef empty name ────────────────────────────────────

    [Fact]
    public void ParseRef_EmptyName_Throws()
    {
        // C (smart_pkt.c:308-310): "<oid> " with an empty name is rejected.
        var state = new GitPacketParseState();
        string oid = new('a', 40);
        // pkt: "<oid> \n" → length 4 + 40 + 1 + 1 = 46 = 0x2e
        byte[] pkt = Encoding.ASCII.GetBytes($"002e{oid} \n");

        Assert.Throws<GitException>(() => GitPacketReader.Parse(pkt, out _, ref state));
    }

    // ── HasUsername ────────────────────────────────────────────

    [Fact]
    public void Credential_EmptyUsername_HasUsernameTrue()
    {
        // C (credential.c:22-28): every type except GIT_CREDENTIAL_DEFAULT has a username — even an EMPTY one.
        using var cred = new GitUsernameCredential(string.Empty);
        Assert.True(cred.HasUsername);
    }

    // ── FETCH_HEAD "oid\t" is_merge inheritance ────────────────

    [Fact]
    public async Task FetchHead_TrailingTabLine_InheritsIsMerge()
    {
        // C (fetchhead.c:192-197, 281): for "<oid>\t" (trailing tab, empty rest) *is_merge is NOT assigned and retains the PREVIOUS line's value.
        string gitDir = Path.Combine(_tempDir, "fh");
        Directory.CreateDirectory(gitDir);
        string oid = new('a', 40);
        string content = $"{oid}\t\tbranch 'master' of file:///tmp/x\n{oid}\t\n";
        await File.WriteAllTextAsync(Path.Combine(gitDir, "FETCH_HEAD"), content, TestContext.Current.CancellationToken);

        List<GitFetchHeadEntry> entries = [];
        await foreach (GitFetchHeadEntry entry in GitFetchHead.ReadAsync(gitDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken))
        {
            entries.Add(entry);
        }

        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].IsMerge);  // explicit merge entry
        Assert.True(entries[1].IsMerge);  // inherited from the previous line
    }

    // ── grafts CRLF message ────────────────────────────────────

    [Fact]
    public async Task Grafts_CrInFirstOid_InvalidGraftOidMessage()
    {
        // C (grafts.c:156-158): a '\r' inside the FIRST OID fails git_parse_advance_oid → "invalid graft OID at line N".
        string path = Path.Combine(_tempDir, "grafts1");
        string oid = new('a', 40);
        await File.WriteAllTextAsync(path, oid[..20] + "\r" + oid[20..] + " " + oid + "\n", TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await Grafts.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Contains("invalid graft OID at line 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grafts_CrAfterSpace_InvalidParentOidMessage()
    {
        string path = Path.Combine(_tempDir, "grafts2");
        string oid = new('a', 40);
        await File.WriteAllTextAsync(path, oid + " " + oid[..20] + "\r" + oid[20..] + "\n", TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await Grafts.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Contains("invalid parent OID at line 1", ex.Message, StringComparison.Ordinal);
    }
}
