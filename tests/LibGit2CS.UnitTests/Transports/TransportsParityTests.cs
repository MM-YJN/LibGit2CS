using System.Buffers;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;
using LibGit2CS.Transports;

using RemoteType = LibGit2CS.Remote.GitRemote;

namespace LibGit2CS.UnitTests.Transports;

/// <summary>
/// Regression tests for the transports/remote parity behaviors in
/// libgit2 1.9.4
/// <list type="bullet">
/// <item>band-3 error packets during pack download are silently
/// dropped by C.</item>
/// <item>FETCH_HEAD merge-entry determination (branch upstream
/// via config, not a HEAD/symref heuristic).</item>
/// <item>tag auto-following (AUTO) creates local tags.</item>
/// <item>the push command's capability string starts with a
/// space.</item>
/// <item>the no-refspec fetch wants HEAD and writes a merge
/// entry.</item>
/// <item>refspec DWIM (shorthand expansion).</item>
/// <item>update-tips FF policy — non-FF updates are silently
/// skipped.</item>
/// </list>
/// </summary>
public sealed class TransportsParityTests : IAsyncLifetime
{
    private readonly string _tempDir;

    public TransportsParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_TransportsParity_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private static GitSignature TestSig()
        => new("Test User", "test@example.com", new GitTime(1700000000, 0));

    private static async Task<GitOid> CommitFileAsync(GitRepository repo, GitOid? parent, string fileName, string content, string message, CancellationToken ct)
    {
        GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), ct);
        using GitTreeBuilder treeBld = repo.NewTreeBuilder();
        await treeBld.InsertAsync(fileName, blobOid, GitFileMode.Regular, ct);
        GitOid treeOid = await treeBld.WriteAsync(ct);

        // C (commit.c:113-117): a commit created with update_ref must have
        // the current tip as its first parent — chain onto it when no
        // explicit parent was given.
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

    private async Task<GitRepository> CreateServerAsync(string name, CancellationToken ct)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: ct);
        GitOid a = await CommitFileAsync(repo, null, "f.txt", "one\n", "one\n", ct);
        await CommitFileAsync(repo, a, "f.txt", "two\n", "two\n", ct);
        await repo.SetHeadAsync("refs/heads/main", ct);
        return repo;
    }

    private async Task<GitRepository> CreateClientAsync(string name, CancellationToken ct)
    {
        string repoPath = Path.Combine(_tempDir, name);
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: ct);
        await CommitFileAsync(repo, null, "f.txt", "one\n", "one\n", ct);
        await repo.SetHeadAsync("refs/heads/main", ct);
        return repo;
    }

    private static async Task<RemoteType> CreateRemoteAsync(GitRepository client, string url, CancellationToken ct)
        => await client.RemoteCreateAsync("origin", url, ct);

    // ── push command capability string starts with a space ──

    [Fact]
    public void PushPktline_FirstCapability_HasLeadingSpace()
    {
        // C (smart_protocol.c:834-848): "Core git always starts their
        // capabilities string with a space".
        var specs = new List<GitPushSpec>
        {
            new()
            {
                RefSpec = GitRefSpec.Parse("refs/heads/main:refs/heads/main", isFetch: false),
                Loid = GitOid.Parse("1111111111111111111111111111111111111111".AsSpan(), GitHashAlgorithmKind.Sha1),
                Roid = GitOid.Parse("2222222222222222222222222222222222222222".AsSpan(), GitHashAlgorithmKind.Sha1),
            },
        };

        var buffer = new ArrayBufferWriter<byte>();
        GitSmartProtocol.GeneratePushPktline(buffer, specs, reportStatus: true, hasPushOptions: false, GitHashAlgorithmKind.Sha1);

        byte[] bytes = buffer.WrittenSpan.ToArray();
        string pkt = Encoding.ASCII.GetString(bytes);
        string firstLine = pkt[..pkt.IndexOf('\n')];

        Assert.Contains("\0 report-status side-band-64k", firstLine);
        Assert.DoesNotContain("\0report-status", firstLine);
    }

    // ── FETCH_HEAD merge entry from the branch upstream ──

    [Fact]
    public async Task Fetch_FetchheadMergeEntry_FromUpstreamConfig()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository server = await CreateServerAsync("server", ct);
        await using GitRepository client = await CreateClientAsync("client", ct);

        // Configure the upstream: branch.main.remote/merge + fetch spec.
        await client.Config.SetStringAsync("remote.origin.url", FixtureLoader.TestFileUrl(server.Path), ct);
        await client.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", ct);
        await client.Config.SetStringAsync("branch.main.remote", "origin", ct);
        await client.Config.SetStringAsync("branch.main.merge", "refs/heads/main", ct);

        RemoteType remote = await client.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(cancellationToken: ct);

        // C (remote.c:1536-1598): the head matching the local branch's
        // upstream is the merge entry (empty merge flag in FETCH_HEAD).
        string fetchHead = await File.ReadAllTextAsync(Path.Combine(client.Path, "FETCH_HEAD"), ct);
        string[] lines = fetchHead.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        // Merge entry: "<oid>\t\tbranch 'main' of <url>" (the empty merge
        // flag means merge; "not-for-merge" would mean no merge entry).
        Assert.Contains("\t\tbranch 'main' of ", lines[0]);
        Assert.DoesNotContain("not-for-merge", lines[0]);
    }

    // ── update-tips FF policy ──

    [Fact]
    public async Task Fetch_NonFastForwardUpdate_SkippedSilently()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository server = await CreateServerAsync("server", ct);
        await using GitRepository client = await CreateClientAsync("client", ct);

        await client.Config.SetStringAsync("remote.origin.url", FixtureLoader.TestFileUrl(server.Path), ct);
        // Non-forced spec (no '+'): C silently skips non-FF updates.
        await client.Config.SetStringAsync("remote.origin.fetch", "refs/heads/*:refs/remotes/origin/*", ct);

        // First fetch: origin/main = server tip (B).
        RemoteType remote = await client.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(cancellationToken: ct);
        GitReference? tracking = await client.ReferenceResolveAsync("refs/remotes/origin/main", ct);
        Assert.NotNull(tracking);
        GitOid serverTip = Assert.IsType<GitDirectReference>(tracking).Target;

        // Divergence: the client rewrites origin/main to a commit that is
        // NOT an ancestor of the server's new tip. The commit itself must
        // not update the ref (C's git_commit_create refuses tip != parent).
        GitOid local = await CommitFileAsync(client, null, "local.txt", "local\n", "local\n", ct);
        await client.ReferenceSetTargetAsync(tracking!, local, logMessage: "rewrite", cancellationToken: ct);

        // Server moves on (new commit on top of B).
        GitOid newTip = await CommitFileAsync(server, serverTip, "f.txt", "three\n", "three\n", ct);

        // Non-forced fetch: C silently skips the non-FF update
        // (remote.c:1873-1880) — the tracking ref stays at the local commit.
        await remote.FetchAsync(cancellationToken: ct);
        GitReference? after = await client.ReferenceResolveAsync("refs/remotes/origin/main", ct);
        Assert.NotNull(after);
        Assert.Equal(local, Assert.IsType<GitDirectReference>(after).Target);
        Assert.NotEqual(newTip, Assert.IsType<GitDirectReference>(after).Target);
    }

    // ── tag auto-following (AUTO) ──

    [Fact]
    public async Task Fetch_AutoTag_CreatesLocalTagForKnownObject()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository server = await CreateServerAsync("server", ct);
        await using GitRepository client = await CreateClientAsync("client", ct);

        await client.Config.SetStringAsync("remote.origin.url", FixtureLoader.TestFileUrl(server.Path), ct);
        await client.Config.SetStringAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*", ct);

        // Tag the server's main tip; the client already has that object.
        GitReference? main = await server.ReferenceResolveAsync("refs/heads/main", ct);
        Assert.NotNull(main);
        GitOid tip = Assert.IsType<GitDirectReference>(main).Target;
        Commit? tipCommit = await server.ObjectLookupAsync<Commit>(tip, ct);
        Assert.NotNull(tipCommit);
        await server.TagCreateAsync("v1", tipCommit!, TestSig(), "v1\n", cancellationToken: ct);
        tipCommit!.Dispose();

        // The advertised OID for the annotated tag is the TAG OBJECT (the
        // local transport peels only for the "^{}" entry).
        GitReference? serverTag = await server.ReferenceResolveAsync("refs/tags/v1", ct);
        Assert.NotNull(serverTag);
        GitOid tagObjectOid = Assert.IsType<GitDirectReference>(serverTag).Target;

        RemoteType remote = await client.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(cancellationToken: ct);

        // C (remote.c:1836-1904): in AUTO mode a local tag is created for
        // any advertised tag whose object is already in the local ODB.
        GitReference? tag = await client.ReferenceResolveAsync("refs/tags/v1", ct);
        Assert.NotNull(tag);
        Assert.Equal(tagObjectOid, Assert.IsType<GitDirectReference>(tag).Target);
    }

    // ── refspec DWIM (shorthand expansion) ──

    [Fact]
    public async Task Fetch_ShorthandRefspec_DwimsAgainstAdvertisedHeads()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository server = await CreateServerAsync("server", ct);
        await using GitRepository client = await CreateClientAsync("client", ct);

        await client.Config.SetStringAsync("remote.origin.url", FixtureLoader.TestFileUrl(server.Path), ct);

        // Fetch with a shorthand refspec: "main:remotes/origin/main" DWIMs to
        // "refs/heads/main:refs/remotes/origin/main" against the advertised
        // heads (refspec.c:377-435).
        // fetch nothing.
        RemoteType remote = await client.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(refspecs: ["main:remotes/origin/main"], cancellationToken: ct);

        GitReference? tracking = await client.ReferenceResolveAsync("refs/remotes/origin/main", ct);
        Assert.NotNull(tracking);
        GitReference? serverMain = await server.ReferenceResolveAsync("refs/heads/main", ct);
        Assert.NotNull(serverMain);
        Assert.Equal(Assert.IsType<GitDirectReference>(serverMain).Target, Assert.IsType<GitDirectReference>(tracking).Target);
    }

    // ── no-refspec fetch wants HEAD ──

    [Fact]
    public async Task Fetch_NoRefspec_WantsHeadAndWritesMergeEntry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GitRepository server = await CreateServerAsync("server", ct);
        await using GitRepository client = await CreateClientAsync("client", ct);

        await client.Config.SetStringAsync("remote.origin.url", FixtureLoader.TestFileUrl(server.Path), ct);

        // With no refspecs, C parses "HEAD" and DWIMs it against the
        // advertised refs (fetch.c:121-130); FETCH_HEAD then contains a
        // merge entry for HEAD (remote.c:1536-1598).
        RemoteType remote = await client.RemoteLookupAsync("origin", ct);
        await remote.FetchAsync(refspecs: [], cancellationToken: ct);

        string fetchHead = await File.ReadAllTextAsync(Path.Combine(client.Path, "FETCH_HEAD"), ct);
        string[] lines = fetchHead.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        // The HEAD merge entry: "<oid>\t\t<url>" (empty merge flag).
        Assert.DoesNotContain("not-for-merge", lines[0]);
        Assert.Contains("\t\tfile://", lines[0]);
    }

    // ── sideband band-3 errors are silently dropped ──

    /// <summary>
    /// A band-3 "fatal: ..." packet arriving mid-pack must be silently
    /// dropped (C smart_protocol.c:721-758 has no GIT_PKT_ERR arm in the
    /// sideband demux loop), and the download must complete with the full
    /// pack".
    /// </summary>
    [Fact]
    public async Task DownloadPack_SidebandBand3Error_DroppedLikeC()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // 1. Real source repo + a small pack.
        string sourcePath = Path.Combine(_tempDir, "band3-source");
        await using GitRepository sourceRepo = await GitRepository.InitAsync(sourcePath, isBare: false, new GitContext(), cancellationToken: ct);

        GitOid commitOid = await CommitFileAsync(sourceRepo, null, "f.txt", "hello\n", "one\n", ct);
        await sourceRepo.SetHeadAsync("refs/heads/main", ct);

        byte[] packBytes;
        using (GitPackWriter packWriter = sourceRepo.NewPackWriter())
        {
            using GitRevWalker walk = sourceRepo.NewRevWalker();
            walk.Sort = GitSortMode.Time;
            await walk.PushHeadAsync(ct);
            await packWriter.InsertWalkAsync(walk, ct);
            using var ms = new MemoryStream();
            await packWriter.WriteAsync(ms, null, ct);
            packBytes = ms.ToArray();
        }

        // 2. Empty client repo for the pack indexer.
        string clientPath = Path.Combine(_tempDir, "band3-client");
        await using GitRepository clientRepo = await GitRepository.InitAsync(clientPath, isBare: false, new GitContext(), cancellationToken: ct);

        // 3. Mock: ref-ad, then NAK + sideband pack with a band-3 error
        //    packet injected between the data chunks (the pack itself
        //    stays complete).
        byte[] refAd = BuildBand3RefAdvertisement(commitOid.ToString());
        byte[] nak = "0008NAK\n"u8.ToArray();

        // Split the sideband pack into two data chunks with the band-3
        // error between them.
        int half = packBytes.Length / 2;
        byte[] firstHalf = BuildSidebandChunk(packBytes.AsSpan(0, half).ToArray());
        byte[] secondHalf = BuildSidebandChunk(packBytes.AsSpan(half).ToArray());
        byte[] band3Error = BuildBand3Error("fatal: nope");
        byte[] downloadResponse = Combine(nak, firstHalf, band3Error, secondHalf, "0000"u8.ToArray());

        var mock = new Band3MockSubtransport([refAd, downloadResponse]);

        await using GitSmartTransport transport = new(new SubtransportDefinition(_ => mock, IsRpc: false, null), new GitContext());
        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, null, ct);

        // 4. The download must complete: the band-3 packet is dropped and
        //    the (complete) pack is indexed.
        await transport.DownloadPackAsync(clientRepo, new GitIndexerProgress(), ct);

        // The pack landed in the client repo.
        Commit? tip = await clientRepo.ObjectLookupAsync<Commit>(commitOid, ct);
        Assert.NotNull(tip);
        tip!.Dispose();
    }

    private static byte[] BuildBand3RefAdvertisement(string oidHex)
    {
        var sb = new StringBuilder();
        string oidZero = new('0', oidHex.Length);
        string firstRef = $"{oidZero} HEAD\0side-band-64k ofs-delta\n";
        int refLen = 4 + firstRef.Length;
        sb.Append(refLen.ToString("x4"));
        sb.Append(firstRef);
        sb.Append("0000");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildBand3Error(string message)
    {
        // "<len>\x03<message>\n" — the band-3 error sideband packet.
        byte[] body = Encoding.ASCII.GetBytes("\x03" + message + "\n");
        int len = 4 + body.Length;
        byte[] result = new byte[4 + body.Length];
        Encoding.ASCII.GetBytes(len.ToString("x4")).CopyTo(result, 0);
        body.CopyTo(result, 4);
        return result;
    }

    private static byte[] BuildSidebandChunk(byte[] packData)
    {
        var result = new List<byte>();
        string lenHex = (4 + 1 + packData.Length).ToString("x4");
        result.AddRange(Encoding.ASCII.GetBytes(lenHex));
        result.Add(GitPacketReader.SideBandData);
        result.AddRange(packData);
        return [.. result];
    }

    private static byte[] Combine(params byte[][] arrays)
    {
        int total = arrays.Sum(a => a.Length);
        byte[] result = new byte[total];
        int offset = 0;
        foreach (byte[] a in arrays)
        {
            a.CopyTo(result, offset);
            offset += a.Length;
        }

        return result;
    }

    private sealed class Band3MockSubtransport : IGitSubtransport
    {
        private readonly Band3MockStream _stream;

        public Band3MockSubtransport(IReadOnlyList<byte[]> chunks)
        {
            _stream = new Band3MockStream(chunks);
        }

        public static bool IsRpc => false;

        public Task<IGitSubtransportStream> ActionAsync(string url, GitSmartService service, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
            => Task.FromResult<IGitSubtransportStream>(_stream);

        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Band3MockStream : IGitSubtransportStream
    {
        private readonly byte[][] _chunks;
        private int _phase;
        private int _phaseOffset;

        public Band3MockStream(IReadOnlyList<byte[]> chunks)
        {
            _chunks = [.. chunks];
        }

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            while (_phase < _chunks.Length && _phaseOffset >= _chunks[_phase].Length)
            {
                _phase++;
                _phaseOffset = 0;
            }

            if (_phase >= _chunks.Length)
            {
                return Task.FromResult<int>(0);
            }

            byte[] chunk = _chunks[_phase];
            int toRead = Math.Min(buffer.Length, chunk.Length - _phaseOffset);
            chunk.AsSpan(_phaseOffset, toRead).CopyTo(buffer.Span);
            _phaseOffset += toRead;
            return Task.FromResult<int>(toRead);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
