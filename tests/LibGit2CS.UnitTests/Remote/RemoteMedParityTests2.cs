using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Remote;

/// <summary>
/// Regression tests for the remote parity behaviors in
/// libgit2 1.9.4:
/// <list type="bullet">
/// <item>FETCH_HEAD read/parse errors match C.</item>
/// <item>FETCH_HEAD remote-URL sanitization reformats
/// structurally (git_net_url_parse + git_net_url_fmt).</item>
/// <item>FETCH_HEAD is appended per-spec (sorted batches in
/// spec order), not globally sorted.</item>
/// <item>a failed remote unpack fails the whole push.</item>
/// <item>an unresolvable push src (or non-refs/ dst) is an
/// error.</item>
/// <item>deletion pushes delete the local tracking ref.</item>
/// <item>remote create validates name/URL and rejects
/// duplicates.</item>
/// <item>insteadOf/pushInsteadOf is applied at create and
/// lookup.</item>
/// <item>proxy config keys are read
/// (remote.&lt;name&gt;.proxy, http.&lt;url&gt;.proxy, http.proxy).</item>
/// </list>
/// </summary>
public sealed class RemoteMedParityTests2 : IDisposable
{
    private const string Sha1Oid = "5e1c8e7f3a2b4c6d8e9f0a1b2c3d4e5f6a7b8c9d";
    private readonly string _tempDir;

    public RemoteMedParityTests2()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_RemoteMed2_" + Guid.NewGuid().ToString("N")[..8]);
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

    private async Task<GitRepository> InitRepoAsync(string name, bool bare, GitContext? ctx = null, CancellationToken ct = default)
    {
        string repoPath = Path.Combine(_tempDir, name);
        return await GitRepository.InitAsync(repoPath, isBare: bare, ctx ?? new GitContext(), cancellationToken: ct);
    }

    private static async Task<GitOid> CommitFileAsync(GitRepository repo, GitOid? parent, string fileName, string content, string message, CancellationToken ct)
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), ct);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync(fileName, blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await treeBld.WriteAsync(ct);

        List<GitOid> parents = parent is { } p ? [p] : [];
        if (parents.Count == 0 &&
            await repo.ReferenceResolveAsync("refs/heads/main", ct) is GitDirectReference tipRef)
        {
            parents.Add(tipRef.Target);
        }

        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parents,
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = "refs/heads/main",
        }, ct);
    }

    private static async Task WriteFetchHeadAsync(string gitdir, string content, CancellationToken ct)
    {
        string path = Path.Combine(gitdir, GitFetchHead.FileName);
        await File.WriteAllTextAsync(path, content, ct);
    }

    private static async Task<List<GitFetchHeadEntry>> ReadFetchHeadAsync(string gitdir, CancellationToken ct)
    {
        return await GitFetchHead.ReadAsync(gitdir, GitHashAlgorithmKind.Sha1, cancellationToken: ct)
            .ToListAsync(cancellationToken: ct);
    }

    // ── FETCH_HEAD read strictness ───────────────────────────

    [Fact]
    public async Task FetchHead_EmptyLine_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = Path.Combine(_tempDir, "fh-empty");
        Directory.CreateDirectory(dir);
        await WriteFetchHeadAsync(dir, $"{Sha1Oid}\t\turl\n\n", ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => ReadFetchHeadAsync(dir, ct));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("empty line in FETCH_HEAD line 2", ex.Message);
        Assert.Equal(GitErrorCategory.FetchHead, ex.Category);
    }

    [Fact]
    public async Task FetchHead_InvalidMergeFlag_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = Path.Combine(_tempDir, "fh-flag");
        Directory.CreateDirectory(dir);
        await WriteFetchHeadAsync(dir, $"{Sha1Oid}\tnot-merge\tbranch 'x' of url\n", ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => ReadFetchHeadAsync(dir, ct));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid for-merge entry in FETCH_HEAD line 1", ex.Message);
    }

    [Fact]
    public async Task FetchHead_OidTooLong_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = Path.Combine(_tempDir, "fh-oid");
        Directory.CreateDirectory(dir);
        await WriteFetchHeadAsync(dir, $"{Sha1Oid}extra\t\turl\n", ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => ReadFetchHeadAsync(dir, ct));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid object ID in FETCH_HEAD line 1", ex.Message);
    }

    [Fact]
    public async Task FetchHead_NoTrailingEol_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = Path.Combine(_tempDir, "fh-eol");
        Directory.CreateDirectory(dir);
        await WriteFetchHeadAsync(dir, $"{Sha1Oid}\t\turl", ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => ReadFetchHeadAsync(dir, ct));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("no EOL at line 1", ex.Message);
    }

    [Fact]
    public async Task FetchHead_InvalidDescription_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = Path.Combine(_tempDir, "fh-desc");
        Directory.CreateDirectory(dir);
        await WriteFetchHeadAsync(dir, $"{Sha1Oid}\t\tbranch 'x of url\n", ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => ReadFetchHeadAsync(dir, ct));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("invalid description in FETCH_HEAD line 1", ex.Message);
    }

    [Fact]
    public async Task FetchHead_OldFormatNoTabs_IsMerge()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = Path.Combine(_tempDir, "fh-old");
        Directory.CreateDirectory(dir);
        // Compat with old git clients: a bare OID line (no tabs) is a merge
        // entry (fetchhead.c:180-184).
        await WriteFetchHeadAsync(dir, $"{Sha1Oid}\n", ct);

        List<GitFetchHeadEntry> entries = await ReadFetchHeadAsync(dir, ct);
        GitFetchHeadEntry entry = Assert.Single(entries);
        Assert.True(entry.IsMerge);
        Assert.Null(entry.RefName);
    }

    [Fact]
    public async Task FetchHead_ValidLines_ParseAndYield()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = Path.Combine(_tempDir, "fh-ok");
        Directory.CreateDirectory(dir);
        await WriteFetchHeadAsync(dir,
            $"{Sha1Oid}\t\tbranch 'main' of https://example.com/repo\n" +
            $"{Sha1Oid}\tnot-for-merge\ttag 'v1' of https://example.com/repo\n", ct);

        List<GitFetchHeadEntry> entries = await ReadFetchHeadAsync(dir, ct);
        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].IsMerge);
        Assert.Equal("refs/heads/main", entries[0].RefName);
        Assert.Equal("https://example.com/repo", entries[0].RemoteUrl);
        Assert.False(entries[1].IsMerge);
        Assert.Equal("refs/tags/v1", entries[1].RefName);
    }

    // ── FETCH_HEAD remote-URL sanitization ───────────────────

    [Fact]
    public void SanitizeRemoteUrl_NoPath_ReformatsWithTrailingSlash()
    {
        // C (fetchhead.c:40-65): git_net_url_parse + git_net_url_fmt — a
        // hierarchical URL without a path formats with "/".
        Assert.Equal("http://example.com/", GitFetchHead.SanitizeRemoteUrl("http://user@example.com"));
        Assert.Equal("http://example.com/", GitFetchHead.SanitizeRemoteUrl("http://example.com"));
    }

    [Fact]
    public void SanitizeRemoteUrl_StripsUserInfo()
    {
        Assert.Equal("https://github.com/repo.git", GitFetchHead.SanitizeRemoteUrl("https://user:pass@github.com/repo.git"));
        Assert.Equal("https://host:8080/repo", GitFetchHead.SanitizeRemoteUrl("https://user@host:8080/repo"));
    }

    [Fact]
    public void SanitizeRemoteUrl_ScpStyle_Unchanged()
    {
        // C: git_net_url_parse treats "git@github.com:user/repo.git" as a
        // relative path (no scheme) — nothing to strip, original returned.
        Assert.Equal("git@github.com:user/repo.git", GitFetchHead.SanitizeRemoteUrl("git@github.com:user/repo.git"));
    }

    [Fact]
    public void SanitizeRemoteUrl_DefaultPort_Omitted()
    {
        Assert.Equal("http://example.com/repo", GitFetchHead.SanitizeRemoteUrl("http://example.com:80/repo"));
        Assert.Equal("https://example.com/repo", GitFetchHead.SanitizeRemoteUrl("https://example.com:443/repo"));
    }

    // ── FETCH_HEAD per-spec ordering ─────────────────────────

    [Fact]
    public async Task Fetch_MultiRefspec_FetchheadAppendedPerSpec()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository server = await InitRepoAsync("fetchhead-server", bare: true, ct: ct);
        GitOid a = await CommitFileAsync(server, null, "f.txt", "one\n", "one\n", ct);
        await CommitFileAsync(server, a, "f.txt", "two\n", "two\n", ct);
        await server.SetHeadAsync("refs/heads/main", ct);

        // Second branch on the server.
        GitOid mainTip = (await server.ReferenceResolveAsync("refs/heads/main", ct) as GitDirectReference)!.Target;
        await server.ReferenceCreateAsync("refs/heads/feature", mainTip, force: false, logMessage: "branch", cancellationToken: ct);

        await using GitRepository client = await InitRepoAsync("fetchhead-client", bare: false, ct: ct);
        await client.Config.SetStringAsync("remote.origin.url", FixtureLoader.TestFileUrl(server.Path), ct);
        GitRemote remote = await client.RemoteLookupAsync("origin", ct);

        // Two non-wildcard fetch specs: main, then feature. C appends each
        // spec's sorted FETCH_HEAD batch in spec order (remote.c:2109-2157);
        await remote.FetchAsync(
            refspecs: [
                "+refs/heads/main:refs/remotes/origin/main",
                "+refs/heads/feature:refs/remotes/origin/feature",
            ],
            cancellationToken: ct);

        string fetchHead = await File.ReadAllTextAsync(Path.Combine(client.Path, "FETCH_HEAD"), ct);
        string[] lines = fetchHead.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        // Spec order: spec1's batch (main, merge) before spec2's batch
        // (feature, merge — a non-wildcard spec marks its src head as the
        // merge entry).
        // puts "feature" (f < m) before "main".
        Assert.Contains("\t\tbranch 'main' of ", lines[0]);
        Assert.DoesNotContain("not-for-merge", lines[0]);
        Assert.Contains("\t\tbranch 'feature' of ", lines[1]);
        Assert.DoesNotContain("not-for-merge", lines[1]);
    }

    // ── failed remote unpack fails the push ─────────────────

    [Fact]
    public async Task Push_UnpackFailure_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var ctx = new GitContext();
        var mock = new MockTransport { PushResult = new GitPushResult { UnpackOk = false, Status = [] } };
        ctx.Transports.Register("httptest", _ => mock);

        await using GitRepository repo = await InitRepoAsync("push-unpack", bare: true, ctx, ct);
        GitRemote remote = await repo.RemoteCreateAsync("origin", "httptest://host/repo.git", ct);

        // C (push.c:537-540, git_push_finish): a failed remote unpack makes
        // the whole push fail with GIT_ERROR_NET "unpacking the sent packfile
        // failed on the remote".
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await remote.PushAsync([":refs/heads/x"], cancellationToken: ct));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("unpacking the sent packfile failed on the remote", ex.Message);
        Assert.Equal(GitErrorCategory.Net, ex.Category);

        await remote.DisposeAsync();
    }

    // ── push spec resolution errors ──────────────────────────

    [Fact]
    public async Task Push_NonexistentSrc_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await InitRepoAsync("push-nonexistent-src", bare: true, ct: ct);
        await using GitRepository target = await InitRepoAsync("push-nonexistent-tgt", bare: true, ct: ct);

        GitRemote remote = await source.RemoteCreateAsync("origin", FixtureLoader.TestFileUrl(target.Path), ct);

        // C (push.c:109-115, check_lref): the src is resolved via
        // git_revparse_single, whose GIT_ENOTFOUND for a nonexistent ref
        // propagates.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await remote.PushAsync(["refs/heads/nonexistent:refs/heads/x"], cancellationToken: ct));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("revspec 'refs/heads/nonexistent' not found", ex.Message);
        Assert.Equal(GitErrorCategory.Reference, ex.Category);

        await remote.DisposeAsync();
    }

    [Fact]
    public async Task Push_InvalidDst_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await InitRepoAsync("push-invalid-dst-src", bare: true, ct: ct);
        GitOid blob = await source.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), ct);
        using GitTreeBuilder tb = source.NewTreeBuilder();
        await tb.InsertAsync("f", blob, GitFileMode.Regular, ct);
        GitOid tree = await tb.WriteAsync(ct);
        await source.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "m\n",
            UpdateRef = "refs/heads/master",
        }, ct);
        await using GitRepository target = await InitRepoAsync("push-invalid-dst-tgt", bare: true, ct: ct);

        GitRemote remote = await source.RemoteCreateAsync("origin", FixtureLoader.TestFileUrl(target.Path), ct);

        // C (push.c:117-120, check_rref): the dst must start with "refs/".
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await remote.PushAsync(["refs/heads/master:notaref"], cancellationToken: ct));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("not a valid reference 'notaref'", ex.Message);

        await remote.DisposeAsync();
    }

    // ── deletion push removes the tracking ref ───────────────

    [Fact]
    public async Task Push_Deletion_DeletesTrackingRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository source = await InitRepoAsync("push-deletion-src", bare: true, ct: ct);
        GitOid blob = await source.ObjectWriteAsync(GitObjectType.Blob, "x\n"u8.ToArray(), ct);
        using GitTreeBuilder tb = source.NewTreeBuilder();
        await tb.InsertAsync("f", blob, GitFileMode.Regular, ct);
        GitOid tree = await tb.WriteAsync(ct);
        await source.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Parents = [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = "m\n",
            UpdateRef = "refs/heads/master",
        }, ct);
        await using GitRepository target = await InitRepoAsync("push-deletion-tgt", bare: true, ct: ct);

        GitRemote remote = await source.RemoteCreateAsync("origin", FixtureLoader.TestFileUrl(target.Path), ct);

        // Push master — the tracking ref refs/remotes/origin/master appears.
        await remote.PushAsync(["refs/heads/master:refs/heads/master"], cancellationToken: ct);
        Assert.NotNull(await source.ReferenceResolveAsync("refs/remotes/origin/master", ct));

        // Delete it on the remote — C (push.c:203-214) deletes the local
        // tracking ref.
        // recreate the tracking ref.
        await remote.PushAsync([":refs/heads/master"], cancellationToken: ct);
        Assert.Null(await source.ReferenceResolveAsync("refs/remotes/origin/master", ct));

        await remote.DisposeAsync();
    }

    // ── remote create validation ─────────────────────────────

    [Fact]
    public async Task RemoteCreate_InvalidName_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("remote-create-name", bare: false, ct: ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.RemoteCreateAsync("not a name", "https://example.com/repo", ct));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        Assert.Equal("'not a name' is not a valid remote name.", ex.Message);
        Assert.Equal(GitErrorCategory.Config, ex.Category);
    }

    [Fact]
    public async Task RemoteCreate_Duplicate_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("remote-create-dup", bare: false, ct: ct);

        await repo.RemoteCreateAsync("origin", "https://example.com/repo", ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.RemoteCreateAsync("origin", "https://example.com/repo", ct));
        Assert.Equal(GitErrorCode.Exists, ex.Code);
        Assert.Equal("remote 'origin' already exists", ex.Message);
        Assert.Equal(GitErrorCategory.Config, ex.Category);
    }

    [Fact]
    public async Task RemoteCreate_EmptyUrl_Throws()
    {
        // git_remote_create (3-arg) maps the canonicalize failure to GIT_ERROR (-1) with the GIT_ERROR_INVALID class (remote.c:332-333).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("remote-create-url", bare: false, ct: ct);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.RemoteCreateAsync("origin", "", ct));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Equal("cannot set empty URL", ex.Message);
    }

    // ── insteadOf at create and lookup ───────────────────────

    [Fact]
    public async Task RemoteCreate_InsteadoOf_RewritesUrl()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("insteadof-create", bare: false, ct: ct);

        // url.<base>.insteadOf: base replaces the matching prefix.
        await repo.Config.SetStringAsync("url.https://new.example.com/.insteadof", "https://old.example.com/", ct);

        GitRemote remote = await repo.RemoteCreateAsync("origin", "https://old.example.com/repo.git", ct);

        // C (remote.c:253-260): the in-memory url is the insteadOf-rewritten
        // url; the config stores the canonical original.
        Assert.Equal("https://new.example.com/repo.git", remote.Url);
        Assert.Equal("https://old.example.com/repo.git", await repo.Config.GetStringAsync("remote.origin.url", ct));

        await remote.DisposeAsync();
    }

    [Fact]
    public async Task RemoteLookup_InsteadoOf_RewritesUrl()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository repo = await InitRepoAsync("insteadof-lookup", bare: false, ct: ct);

        await repo.Config.SetStringAsync("url.https://new.example.com/.insteadof", "https://old.example.com/", ct);
        await repo.Config.SetStringAsync("remote.origin.url", "https://old.example.com/repo.git", ct);
        await repo.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", ct);

        GitRemote remote = await repo.RemoteLookupAsync("origin", ct);

        // C (remote.c:508-535): lookup applies insteadOf to the fetch URL.
        Assert.Equal("https://new.example.com/repo.git", remote.Url);

        await remote.DisposeAsync();
    }

    // ── proxy config resolution ──────────────────────────────

    [Fact]
    public async Task Connect_Proxy_RemoteNameConfig_Wins()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var ctx = new GitContext();
        var mock = new MockTransport();
        ctx.Transports.Register("http", _ => mock);

        await using GitRepository repo = await InitRepoAsync("proxy-remote-name", bare: false, ctx, ct);
        await repo.Config.SetStringAsync("remote.origin.url", "http://host/repo.git", ct);
        await repo.Config.SetStringAsync("remote.origin.proxy", "http://proxy1:8080", ct);
        GitRemote remote = await repo.RemoteLookupAsync("origin", ct);

        // GIT_PROXY_AUTO opts into the config/env resolution; the default (None) connects directly.
        await remote.ConnectAsync(GitDirection.Fetch, new GitRemoteConnectOptions { Proxy = new GitProxyConfig { Type = GitProxyType.Auto } }, ct);

        // C (remote.c:1090-1095): remote.<name>.proxy is the first source.
        Assert.NotNull(mock.ReceivedOptions);
        Assert.Equal(GitProxyType.Specified, mock.ReceivedOptions!.Proxy!.Type);
        Assert.Equal("http://proxy1:8080", mock.ReceivedOptions.Proxy.Url);

        await remote.DisposeAsync();
    }

    [Fact]
    public async Task Connect_Proxy_HttpUrlKey_WalksPathUp()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var ctx = new GitContext();
        var mock = new MockTransport();
        ctx.Transports.Register("http", _ => mock);

        await using GitRepository repo = await InitRepoAsync("proxy-http-url", bare: false, ctx, ct);
        await repo.Config.SetStringAsync("remote.origin.url", "http://host/repo.git", ct);
        // The full-url key is absent; the trimmed-host key matches
        // (http.<url>.proxy with the path walked up — remote.c:1097-1116).
        await repo.Config.SetStringAsync("http.http://host.proxy", "http://proxy2:8080", ct);
        GitRemote remote = await repo.RemoteLookupAsync("origin", ct);

        await remote.ConnectAsync(GitDirection.Fetch, new GitRemoteConnectOptions { Proxy = new GitProxyConfig { Type = GitProxyType.Auto } }, ct);

        Assert.NotNull(mock.ReceivedOptions);
        Assert.Equal(GitProxyType.Specified, mock.ReceivedOptions!.Proxy!.Type);
        Assert.Equal("http://proxy2:8080", mock.ReceivedOptions.Proxy.Url);

        await remote.DisposeAsync();
    }

    [Fact]
    public async Task Connect_Proxy_HttpProxyFallback()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var ctx = new GitContext();
        var mock = new MockTransport();
        ctx.Transports.Register("http", _ => mock);

        await using GitRepository repo = await InitRepoAsync("proxy-http-proxy", bare: false, ctx, ct);
        await repo.Config.SetStringAsync("remote.origin.url", "http://host/repo.git", ct);
        await repo.Config.SetStringAsync("http.proxy", "http://proxy3:8080", ct);
        GitRemote remote = await repo.RemoteLookupAsync("origin", ct);

        await remote.ConnectAsync(GitDirection.Fetch, new GitRemoteConnectOptions { Proxy = new GitProxyConfig { Type = GitProxyType.Auto } }, ct);

        Assert.NotNull(mock.ReceivedOptions);
        Assert.Equal(GitProxyType.Specified, mock.ReceivedOptions!.Proxy!.Type);
        Assert.Equal("http://proxy3:8080", mock.ReceivedOptions.Proxy.Url);

        await remote.DisposeAsync();
    }

    [Fact]
    public async Task Connect_Proxy_NoConfig_IsNone()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var ctx = new GitContext();
        var mock = new MockTransport();
        ctx.Transports.Register("http", _ => mock);

        ctx.Env["http_proxy"] = null;
        ctx.Env["HTTP_PROXY"] = null;

        await using GitRepository repo = await InitRepoAsync("proxy-none", bare: false, ctx, ct);
        await repo.Config.SetStringAsync("remote.origin.url", "http://host/repo.git", ct);
        GitRemote remote = await repo.RemoteLookupAsync("origin", ct);

        await remote.ConnectAsync(GitDirection.Fetch, new GitRemoteConnectOptions { Proxy = new GitProxyConfig { Type = GitProxyType.Auto } }, ct);

        // C: no config/env proxy → direct connection (GIT_PROXY_NONE).
        Assert.NotNull(mock.ReceivedOptions);
        Assert.Equal(GitProxyType.None, mock.ReceivedOptions!.Proxy!.Type);

        await remote.DisposeAsync();
    }

    [Theory]
    [InlineData("http", false, null)]
    [InlineData("http", true, null)]
    [InlineData("https", false, null)]
    [InlineData("https", true, null)]
    [InlineData("http", false, "no_proxy")]
    [InlineData("https", true, "NO_PROXY")]
    public async Task Connect_Proxy_UsesContextEnvironment(string scheme, bool uppercase, string? bypassKey)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var firstContext = new GitContext();
        using var secondContext = new GitContext();
        foreach (GitContext context in new[] { firstContext, secondContext })
        {
            foreach (string key in new[] { "http_proxy", "HTTP_PROXY", "https_proxy", "HTTPS_PROXY", "no_proxy", "NO_PROXY" })
            {
                context.Env[key] = null;
            }
        }

        string proxyKey = uppercase ? scheme.ToUpperInvariant() + "_PROXY" : scheme + "_proxy";
        firstContext.Env[proxyKey] = "http://first-proxy:8080";
        secondContext.Env[proxyKey] = "http://second-proxy:8080";
        if (bypassKey is not null)
        {
            firstContext.Env[bypassKey] = "host";
        }

        var firstTransport = new MockTransport();
        var secondTransport = new MockTransport();
        firstContext.Transports.Register(scheme, _ => firstTransport);
        secondContext.Transports.Register(scheme, _ => secondTransport);
        await using GitRepository firstRepo = await InitRepoAsync("proxy-first", false, firstContext, ct);
        await using GitRepository secondRepo = await InitRepoAsync("proxy-second", false, secondContext, ct);
        await using GitRemote firstRemote = await firstRepo.RemoteCreateAsync("origin", scheme + "://host/repo.git", ct);
        await using GitRemote secondRemote = await secondRepo.RemoteCreateAsync("origin", scheme + "://host/repo.git", ct);
        var options = new GitRemoteConnectOptions { Proxy = new GitProxyConfig { Type = GitProxyType.Auto } };

        await firstRemote.ConnectAsync(GitDirection.Fetch, options, ct);
        await secondRemote.ConnectAsync(GitDirection.Fetch, options, ct);

        Assert.Equal(bypassKey is null ? GitProxyType.Specified : GitProxyType.None, firstTransport.ReceivedOptions!.Proxy!.Type);
        Assert.Equal(bypassKey is null ? "http://first-proxy:8080" : null, firstTransport.ReceivedOptions.Proxy.Url);
        Assert.Equal(GitProxyType.Specified, secondTransport.ReceivedOptions!.Proxy!.Type);
        Assert.Equal("http://second-proxy:8080", secondTransport.ReceivedOptions.Proxy.Url);
    }

    // ── Mock transport ───────────────────────────────────────────────────

    private sealed class MockTransport : IGitTransport
    {
        public GitRemoteConnectOptions? ReceivedOptions { get; private set; }
        public GitPushResult? PushResult { get; set; }
        private bool _connected;

        public GitRemoteCapability Capabilities => GitRemoteCapability.None;

        public GitHashAlgorithmKind OidType => GitHashAlgorithmKind.Sha1;

        public bool IsConnected => _connected;

        public Task ConnectAsync(string url, GitDirection direction, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
        {
            ReceivedOptions = options;
            _connected = true;
            return Task.CompletedTask;
        }

        public void SetConnectOptions(GitRemoteConnectOptions? options) => ReceivedOptions = options;

        public Task<IReadOnlyList<GitRemoteHead>> LsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GitRemoteHead>>([]);

        public Task NegotiateFetchAsync(GitRepository repo, GitFetchNegotiation wants, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DownloadPackAsync(GitRepository repo, GitIndexerProgress stats, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask<IReadOnlyList<GitOid>> ShallowRootsAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<GitOid>>([]);

        public Task<GitPushResult> PushAsync(GitRepository repo, IReadOnlyList<GitPushSpec> specs, GitPackWriter? packWriter, GitRemoteCallbacks? callbacks, bool reportStatus, IReadOnlyList<string>? pushOptions, CancellationToken cancellationToken)
            => Task.FromResult(PushResult ?? new GitPushResult { UnpackOk = true, Status = [] });

        public void Cancel() { }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            _connected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
