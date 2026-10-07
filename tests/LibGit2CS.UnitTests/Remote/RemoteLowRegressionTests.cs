using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

using LibGit2CS.Checkout;
using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

using GitIndex = LibGit2CS.Index.GitIndex;
using MockNegotiationTransport = LibGit2CS.UnitTests.Transports.MockNegotiationTransport;
using MockTransportBuilder = LibGit2CS.UnitTests.Transports.MockTransportBuilder;

namespace LibGit2CS.UnitTests.Remote;

// Parity cases verified against libgit2 1.9.4:
//  - FETCH_HEAD truncate was gated on updateFetchhead; C truncates
//    unconditionally (remote.c:2139-2143).
//  - push source resolution used ref lookup instead of
//    git_revparse_single (push.c:96-100).
//  - queue_objects swallowed missing source objects instead of
//    failing the push (push.c:309-311).
//  - IsWildcard used 'contains *' while C requires src to END with
//    '*' (refspec.c:350-355).
//  - push update-tips matched raw configured fetch refspecs; C uses
//    the dwimmed active specs (push.c:211-214).
//  - clone reconnected after fetch, causing a second TCP connection
//    and repeated auth callbacks (clone.c:214-223).
//  - public DownloadAsync/UploadAsync required a prior connection;
//    C's git_remote_download/upload self-connect (remote.c:1251-1259).
//  - the port-parse error message embedded the full URL, leaking
//    user:password (net.c:176-179).
public sealed class RemoteLowRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public RemoteLowRegressionTests()
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
        catch (IOException)
        {
        }
    }

    private static GitSignature TestSig() => new("T", "t@x.com", new GitTime(100, 0));

    [SuppressMessage("Security", "CA5350:DoNotUseWeakCryptographicAlgorithms", Justification = "Git pack trailers are defined by SHA-1; this builds a valid empty pack for the smart-transport mock.")]
    private static byte[] BuildEmptyPack()
    {
        byte[] header = [0x50, 0x41, 0x43, 0x4B, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00];
        byte[] trailer = SHA1.HashData(header);
        return [.. header, .. trailer];
    }

    private async Task<(GitRepository Repo, string RepoPath)> CreateRepoAsync(string name, GitContext? ctx = null)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, ctx ?? new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        return (repo, repoPath);
    }

    private static async Task<GitOid> repo_Objects_WriteBlobAsync(GitRepository repo, string content)
        => await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);

    private static async Task<GitOid> CreateCommitAsync(GitRepository repo, string message, string content)
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync("f.txt", blobOid, GitFileMode.Regular);
        GitOid treeOid = await treeBld.WriteAsync(CancellationToken.None);
        GitOid? parent = await repo.ReferenceResolveAsync("refs/heads/master", TestContext.Current.CancellationToken) is GitDirectReference d
            ? d.Target
            : null;
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = parent is { } p ? [p] : [],
            Author = TestSig(),
            Committer = TestSig(),
            Message = message,
            UpdateRef = "refs/heads/master",
        }, TestContext.Current.CancellationToken);
    }

    // ---- FETCH_HEAD is truncated unconditionally ----

    [Fact]
    public async Task Fetch_UpdateFetchheadFalse_TruncatesFetchHead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var ctx = new GitContext();
        (GitRepository client, string clientPath) = await CreateRepoAsync("l06", ctx);
        GitOid commitOid = await CreateCommitAsync(client, "init\n", "hello\n");

        // Prior FETCH_HEAD content — C truncates it even when
        // updateFetchhead=false (remote.c:2139-2143); it must not be left
        // intact.
        string fetchHead = Path.Combine(clientPath, ".git", "FETCH_HEAD");
        await File.WriteAllTextAsync(fetchHead, "stale entry\n", cancellationToken: ct);

        byte[] refAd = MockTransportBuilder.BuildRefAdvertisement(
            [(commitOid.ToString(), "HEAD"), (commitOid.ToString(), "refs/heads/main")],
            capabilities: "side-band-64k ofs-delta");
        byte[] downloadResponse = [.. "0008NAK\n"u8.ToArray(), .. MockTransportBuilder.BuildSidebandData(BuildEmptyPack())];
        var mock = new MockNegotiationTransport(refAd, downloadResponse, packData: [], isRpc: false);
        ctx.Transports.Register("l06s", _ => new GitSmartTransport(
            new SubtransportDefinition(_ => mock, IsRpc: false, null), ctx));

        await client.Config.SetStringAsync("remote.origin.url", "l06s://host/repo", ct);
        await client.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/main:refs/remotes/origin/main", ct);
        GitRemote remote = await client.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(
            refspecs: ["+refs/heads/main:refs/remotes/origin/main"],
            options: new GitFetchOptions { UpdateFetchhead = false },
            cancellationToken: ct);

        string content = await File.ReadAllTextAsync(fetchHead, ct);
        Assert.Equal(string.Empty, content);
    }

    // ---- push source resolves via revparse ----

    [Fact]
    public async Task Push_RevparseSource_Resolves()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (GitRepository source, _) = await CreateRepoAsync("l07src");
        await CreateCommitAsync(source, "c1\n", "a\n");
        GitOid c2 = await CreateCommitAsync(source, "c2\n", "b\n");

        string targetPath = Path.Combine(_tempDir, "l07dst");
        await using GitRepository target = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct);

        await using GitRemote remote = await source.RemoteCreateAsync("origin", targetPath, ct);
        // C's check_lref uses git_revparse_single (push.c:96-100) — a
        // revparse expression source is valid; a plain ref
        // lookup would reject it.
        await remote.PushAsync(["HEAD~0:refs/heads/x"], cancellationToken: ct);

        GitReference? created = await target.ReferenceLookupAsync("refs/heads/x", ct);
        Assert.NotNull(created);
        Assert.Equal(c2, ((GitDirectReference)created).Target);
    }

    // ---- a missing push source object fails the push ----

    [Fact]
    public async Task Push_DanglingSource_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (GitRepository source, _) = await CreateRepoAsync("l08src");
        GitOid c1 = await CreateCommitAsync(source, "c1\n", "a\n");

        // A ref pointing at a missing object (dangling) — written directly
        // because Refs.CreateAsync validates the target's existence.
        var missing = GitOid.Parse("ffffffffffffffffffffffffffffffffffffffff", GitHashAlgorithmKind.Sha1);
        string danglingPath = Path.Combine(_tempDir, "l08src", ".git", "refs", "heads", "dangling");
        Directory.CreateDirectory(Path.GetDirectoryName(danglingPath)!);
        await File.WriteAllTextAsync(danglingPath, missing.ToString() + "\n", cancellationToken: ct);

        string targetPath = Path.Combine(_tempDir, "l08dst");
        await using GitRepository target = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct);

        await using GitRemote remote = await source.RemoteCreateAsync("origin", targetPath, ct);
        // C's queue_objects fails the push on a missing source object
        // (push.c:309-311); silently skipping it would let the push succeed.
        await Assert.ThrowsAsync<GitException>(async () =>
            await remote.PushAsync(["refs/heads/dangling:refs/heads/x"], cancellationToken: ct));
    }

    // ---- IsWildcardByC requires src to end with '*' ----

    [Fact]
    public void RefSpec_IsWildcardByC_RequiresTrailingStar()
    {
        // A mid-pattern glob is a wildcard for Transform/DwimOne but NOT for
        // C's git_refspec_is_wildcard (refspec.c:350-355).
        var mid = GitRefSpec.Parse("refs/heads/*/x:refs/remotes/origin/*/x", isFetch: true);
        Assert.True(mid.IsWildcard);
        Assert.False(mid.IsWildcardByC);

        var trailing = GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: true);
        Assert.True(trailing.IsWildcard);
        Assert.True(trailing.IsWildcardByC);

        var plain = GitRefSpec.Parse("refs/heads/main:refs/remotes/origin/main", isFetch: true);
        Assert.False(plain.IsWildcard);
        Assert.False(plain.IsWildcardByC);
    }

    // ---- push update-tips dwims the configured fetch specs ----

    [Fact]
    public async Task Push_ShorthandFetchSpec_UpdatesTrackingRef()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (GitRepository source, _) = await CreateRepoAsync("l10src");
        GitOid c1 = await CreateCommitAsync(source, "c1\n", "a\n");

        string targetPath = Path.Combine(_tempDir, "l10dst");
        await using GitRepository target = await GitRepository.InitAsync(targetPath, isBare: true, new GitContext(), cancellationToken: ct);
        // Seed the target so refs/heads/master is advertised (the dwim needs
        // the advertised name to expand the shorthand src).
        GitOid seedOid = await repo_Objects_WriteBlobAsync(target, "seed\n");
        await target.ReferenceCreateAsync("refs/heads/master", seedOid, force: true, cancellationToken: ct);

        await using (GitRemote created = await source.RemoteCreateAsync("origin", targetPath, ct))
        {
        }

        // Shorthand-src fetch spec — C dwims "master" to refs/heads/master
        // at upload time (remote.c:2995-2996) and updates the tracking ref
        // (push.c:211-214); a raw "master" src would never match
        // the pushed refs/heads/master.
        await source.Config.SetStringAsync("remote.origin.fetch", "master:refs/remotes/origin/master", ct);
        await using GitRemote remote = await source.RemoteLookupAsync("origin", ct);

        await remote.PushAsync(["+refs/heads/master:refs/heads/master"], cancellationToken: ct);

        GitReference? tracking = await source.ReferenceLookupAsync("refs/remotes/origin/master", ct);
        Assert.NotNull(tracking);
        Assert.Equal(c1, ((GitDirectReference)tracking).Target);
    }

    // ---- clone does not reconnect after the fetch ----

    [Fact]
    public async Task Clone_SmartTransport_SingleConnection()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var ctx = new GitContext();
        (GitRepository source, _) = await CreateRepoAsync("l11src", ctx);
        GitOid c1 = await CreateCommitAsync(source, "c1\n", "a\n");

        // A real pack containing the commit (the clone's fetch must be able
        // to create the tracking ref).
        string packDir = Path.Combine(_tempDir, "l11pack");
        Directory.CreateDirectory(packDir);
        string packPath;
        using (var pb = new LibGit2CS.Pack.GitPackWriter(source))
        {
            await pb.InsertAsync(c1, ct);
            packPath = await pb.WriteToDirectoryAsync(packDir, null, ct);
        }

        byte[] packBytes = await File.ReadAllBytesAsync(packPath, ct);
        byte[] refAd = MockTransportBuilder.BuildRefAdvertisement(
            [(c1.ToString(), "HEAD"), (c1.ToString(), "refs/heads/master")],
            capabilities: "side-band-64k ofs-delta");
        byte[] downloadResponse = [.. "0008NAK\n"u8.ToArray(), .. MockTransportBuilder.BuildSidebandData(packBytes)];
        var mock = new MockNegotiationTransport(refAd, downloadResponse, packData: [], isRpc: false);
        int transportCreations = 0;
        ctx.Transports.Register("l11s", _ =>
        {
            transportCreations++;
            return new GitSmartTransport(
                new SubtransportDefinition(_ => mock, IsRpc: false, null), ctx);
        });

        string targetPath = Path.Combine(_tempDir, "l11clone");
        await using GitRepository cloned = await GitClone.RunAsync(
            "l11s://host/repo", targetPath,
            new GitCloneOptions
            {
                CheckoutOptions = new GitCheckoutOptions { Strategy = GitCheckoutStrategy.None },
            },
            ctx, cancellationToken: ct);

        // the smart transport retains its heads across close, so
        // checkout_branch's LsAsync needs no reconnect — creating a second
        // transport (and a second TCP connection) would be a regression.
        Assert.Equal(1, transportCreations);
    }

    // ---- DownloadAsync self-connects ----

    [Fact]
    public async Task Download_WithoutPriorConnect_SelfConnects()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var ctx = new GitContext();
        (GitRepository client, _) = await CreateRepoAsync("l12", ctx);
        GitOid commitOid = await CreateCommitAsync(client, "init\n", "hello\n");

        byte[] refAd = MockTransportBuilder.BuildRefAdvertisement(
            [(commitOid.ToString(), "HEAD"), (commitOid.ToString(), "refs/heads/main")],
            capabilities: "side-band-64k ofs-delta");
        byte[] downloadResponse = [.. "0008NAK\n"u8.ToArray(), .. MockTransportBuilder.BuildSidebandData(BuildEmptyPack())];
        var mock = new MockNegotiationTransport(refAd, downloadResponse, packData: [], isRpc: false);
        ctx.Transports.Register("l12s", _ => new GitSmartTransport(
            new SubtransportDefinition(_ => mock, IsRpc: false, null), ctx));

        await client.Config.SetStringAsync("remote.origin.url", "l12s://host/repo", ct);
        await client.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/main:refs/remotes/origin/main", ct);
        GitRemote remote = await client.RemoteLookupAsync("origin", ct);

        // C's git_remote_download self-connects (remote.c:1341-1342); the
        // throwing 'the remote is not connected' would be a regression.
        await remote.DownloadAsync(
            refspecs: ["+refs/heads/main:refs/remotes/origin/main"],
            cancellationToken: ct);
    }

    // ---- port-parse error does not leak the URL ----

    [Fact]
    public void SshUrl_PortParseError_DoesNotLeakCredentials()
    {
        // C's url_invalid uses fixed strings (net.c:176-179); embedding the
        // full URL would leak user:password into the exception.
        GitException ex = Assert.Throws<GitException>(() =>
            GitSshUrl.Parse("ssh://user:secret@host:badport/path"));
        Assert.DoesNotContain("secret", ex.Message);
        Assert.DoesNotContain("user", ex.Message);
        Assert.Contains("invalid port", ex.Message);
    }
}
