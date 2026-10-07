using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

using GitIndex = LibGit2CS.Index.GitIndex;
using MockNegotiationTransport = LibGit2CS.UnitTests.Transports.MockNegotiationTransport;
using MockTransportBuilder = LibGit2CS.UnitTests.Transports.MockTransportBuilder;

namespace LibGit2CS.UnitTests.Remote;

// FetchAsync must prune while the transport still holds the advertised
// heads. If GitSmartTransport.CloseAsync clears its cached _heads/_refs
// (while _haveRefs stays true), a post-close LsAsync inside PruneAsync
// returns an EMPTY head list — with an empty remoteRefNames set every
// tracking candidate would fail the reverse-transform keep-check and be
// deleted. C's git_smart__close (smart.c:370-411) does
// NOT clear t->refs/t->heads, so C's prune-after-disconnect still sees the
// advertised heads. PruneAsync therefore runs BEFORE the
// disconnect, mirroring UpdateTipsAsync.
public sealed class FetchPruneRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public FetchPruneRegressionTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LibGit2CS_FetchPrune_" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>An empty pack (version 2, 0 objects) with a valid trailer.</summary>
    [SuppressMessage("Security", "CA5350:DoNotUseWeakCryptographicAlgorithms", Justification = "Git pack trailers are defined by SHA-1; this builds a valid empty pack for the smart-transport mock.")]
    private static byte[] BuildEmptyPack()
    {
        byte[] header = [0x50, 0x41, 0x43, 0x4B, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00];
        byte[] trailer = SHA1.HashData(header);
        return [.. header, .. trailer];
    }

    [Fact]
    public async Task Fetch_WithPrune_OverSmartTransport_KeepsTrackingRefs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A client repo with one commit, and a tracking ref at the same tip
        // the (mock) server advertises — the fetch is up-to-date.
        GitContext ctx = new();
        string clientPath = Path.Combine(_tempDir, "client");
        await using GitRepository client = await GitRepository.InitAsync(clientPath, isBare: false, ctx, cancellationToken: ct);
        await File.WriteAllTextAsync(
            Path.Combine(clientPath, "f.txt"), "hello\n",
            cancellationToken: ct);
        GitIndex index = await client.GetIndexAsync(ct);
        await index.AddByPathAsync("f.txt", ct);
        await index.WriteAsync(ct);
        GitOid treeOid = await index.WriteTreeAsync(ct);
        var sig = new GitSignature("T", "t@x.com", new GitTime(100, 0));
        GitOid commitOid = await client.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = sig,
            Committer = sig,
            Message = "init\n",
            UpdateRef = "HEAD",
        }, ct);
        await client.ReferenceCreateAsync(
            "refs/remotes/origin/main", commitOid, force: false,
            logMessage: "setup", cancellationToken: ct);

        // Remote config: the custom smart-transport scheme + fetch refspec.
        await client.Config.SetStringAsync("remote.origin.url", "l01://host/repo", ct);
        await client.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/main:refs/remotes/origin/main", ct);

        // Mock server: advertises refs/heads/main at the local tip; the
        // up-to-date negotiation is answered with NAK + an empty pack.
        byte[] refAd = MockTransportBuilder.BuildRefAdvertisement(
            [(commitOid.ToString(), "HEAD"), (commitOid.ToString(), "refs/heads/main")],
            capabilities: "side-band-64k ofs-delta");
        byte[] downloadResponse = [.. "0008NAK\n"u8.ToArray(), .. MockTransportBuilder.BuildSidebandData(BuildEmptyPack())];
        var mock = new MockNegotiationTransport(refAd, downloadResponse, packData: [], isRpc: false);
        ctx.Transports.Register("l01", _ => new GitSmartTransport(
            new SubtransportDefinition(_ => mock, IsRpc: false, null), ctx));

        GitRemote remote = await client.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(
            refspecs: ["+refs/heads/main:refs/remotes/origin/main"],
            options: new GitFetchOptions { Prune = GitFetchPrune.Prune },
            cancellationToken: ct);

        // the tracking ref must survive the prune. An empty
        // post-disconnect LsAsync would make the reverse-transform
        // keep-check fail and delete the ref.
        GitReference? tracking = await client.ReferenceLookupAsync("refs/remotes/origin/main", ct);
        Assert.NotNull(tracking);
        Assert.Equal(commitOid, ((GitDirectReference)tracking!).Target);
    }
}
