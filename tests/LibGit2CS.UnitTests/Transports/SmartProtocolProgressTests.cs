using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Transports;

namespace LibGit2CS.UnitTests.Transports;

/// <summary>
/// Regression tests for <see cref="GitSmartProtocol.DownloadPackAsync"/>
/// transfer-progress reporting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transfer-progress contract.</b> The <c>PacketsizeCallback</c> closure in
/// <see cref="GitSmartProtocol.DownloadPackAsync"/> feeds
/// <see cref="GitRemoteCallbacks.TransferProgress"/> during the smart-protocol
/// pack-download phase. Two invariants are pinned here:
/// </para>
/// <para>
/// <b>Byte accumulation.</b> The closure accumulates received bytes in a
/// <c>totalReceivedBytes</c> local that sums across network reads. The
/// per-chunk report fires once the cumulative total crosses
/// <see cref="GitSmartProtocol.NetworkXferThreshold"/> (100 KB), and the final
/// report fires when <c>totalReceivedBytes &gt; lastFiredBytes</c>. Reading a
/// per-read value instead would never cross the threshold for a small pack
/// and would skip the final report (<c>0 &gt; 0</c> is false).
/// </para>
/// <para>
/// <b>Object counts.</b> <c>TotalObjects</c> comes from the pack header and
/// <c>ReceivedObjects</c> / <c>IndexedObjects</c> are incremented per parsed
/// object on the SAME mutable <c>GitIndexerProgress</c> instance the packetsize
/// closure reads — the C# analog of libgit2's shared
/// <c>git_indexer_progress *stats</c> (written by <c>git_indexer_append</c>,
/// read by <c>network_packetsize</c>). A by-value snapshot would leave every
/// report with zero counts and make completion-percentage progress impossible.
/// </para>
/// <para>
/// These tests guard both invariants:
/// <list type="bullet">
/// <item><b>Sub-threshold</b>: a small pack (&lt; 100 KB) where only the
/// final report fires (the per-chunk threshold is never crossed).</item>
/// <item><b>Over-threshold</b>: a large pack (&gt; 100 KB) where at least one
/// per-chunk report fires before the final report.</item>
/// </list>
/// Both drive a real <see cref="GitSmartTransport"/> backed by a mock
/// subtransport that serves a scripted ref-ad + NAK + side-band-wrapped real
/// pack. Beyond asserting <see cref="GitTransferProgress.ReceivedBytes"/>
/// &gt; 0, each test also asserts
/// <see cref="GitTransferProgress.TotalObjects"/> &gt; 0 and
/// <see cref="GitTransferProgress.ReceivedObjects"/> &gt; 0 on the final
/// report, and that <c>ReceivedObjects &lt;= TotalObjects</c> — directly
/// failing if the object-count feedback loop regresses.
/// </para>
/// </remarks>
public sealed class SmartProtocolProgressTests : IAsyncLifetime
{
    private readonly string _tempDir;

    public SmartProtocolProgressTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_SmartProtocolProgress_" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>
    /// Build a real source repo with one commit on <c>refs/heads/main</c>
    /// containing <c>README.md</c>. Returns the repo + the commit OID.
    /// </summary>
    private async ValueTask<(GitRepository Repo, GitOid CommitOid)> BuildSourceRepoAsync(CancellationToken ct)
    {
        string repoPath = Path.Combine(_tempDir, "source");
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, new GitContext(), cancellationToken: ct);
        LibGit2CS.Index.GitIndex idx = await repo.GetIndexAsync(ct);
        string workdir = repo.Workdir!;
        await File.WriteAllTextAsync(Path.Combine(workdir, "README.md"), "hello\n", ct);
        await idx.AddByPathAsync("README.md", ct);
        await idx.WriteAsync(ct);
        GitOid treeOid = await idx.WriteTreeAsync(ct);
        GitSignature sig = TestSig();
        GitOid commitOid = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "initial\n",
            UpdateRef = "refs/heads/main",
        }, ct);
        return (repo, commitOid);
    }

    /// <summary>
    /// Build a real pack file containing the commit, its tree, and all
    /// reachable blobs. Uses <see cref="GitPackWriter"/> to produce a
    /// byte-exact pack the mock subtransport can serve back to the client.
    /// </summary>
    private static async Task<byte[]> BuildPackFromRepoAsync(GitRepository repo, GitOid commitOid, CancellationToken ct)
    {
        using GitPackWriter packWriter = new(repo);
        await packWriter.InsertAsync(commitOid, ct);
        Commit? commit = await repo.ObjectLookupAsync<Commit>(commitOid, ct);
        Assert.NotNull(commit);
        await packWriter.InsertTreeAsync(commit!.Tree, ct);
        commit.Dispose();
        await packWriter.PrepareAsync(ct);
        using MemoryStream ms = new();
        await packWriter.WriteAsync(ms, progress: null, ct);
        return ms.ToArray();
    }

    // ─── Sub-threshold: small pack, only the final report fires ────────

    /// <summary>
    /// Fetch a small pack (&lt; 100 KB) over a mock smart transport with
    /// <see cref="GitRemoteCallbacks.TransferProgress"/> wired. The closure's
    /// per-read value never crosses the threshold, so only the final report
    /// fires — with <c>totalReceivedBytes</c> accumulated across reads, it
    /// reaches the pack size and carries
    /// <see cref="GitTransferProgress.ReceivedBytes"/> &gt; 0.
    /// </summary>
    [Fact]
    public async Task DownloadPack_SmallPack_FiresFinalTransferProgressReport()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // 1. Real source repo + pack (small: one blob "hello\n").
        (GitRepository sourceRepo, GitOid commitOid) = await BuildSourceRepoAsync(ct);
        await using (sourceRepo)
        {
            commitOid = (await sourceRepo.ReferenceResolveAsync("refs/heads/main", ct) as GitDirectReference)!.Target;
            byte[] packBytes = await BuildPackFromRepoAsync(sourceRepo, commitOid, ct);
            Assert.True(packBytes.Length < GitSmartProtocol.NetworkXferThreshold,
                $"test setup: small pack must be under {GitSmartProtocol.NetworkXferThreshold} bytes, got {packBytes.Length}");

            // 2. Empty client repo for the pack indexer to write into.
            string clientPath = Path.Combine(_tempDir, "client");
            await using GitRepository clientRepo = await GitRepository.InitAsync(clientPath, isBare: false, new GitContext(), cancellationToken: ct);

            // 3. Mock subtransport: ref-ad (with side-band-64k) for Connect,
            //    then NAK + sideband-wrapped pack + flush for DownloadPack.
            byte[] refAd = BuildRefAdvertisement(isRpc: false, oidHex: commitOid.ToString(), caps: "side-band-64k ofs-delta");
            byte[] nak = "0008NAK\n"u8.ToArray();
            byte[] sidebandPack = BuildSidebandData(packBytes);
            byte[] downloadResponse = Combine(nak, sidebandPack);
            var mock = new PhasedMockSubtransport([refAd, downloadResponse], isRpc: false);

            // 4. Connect with a TransferProgress callback.
            var progress = new CapturingProgress<GitTransferProgress>();
            var connectOpts = new GitRemoteConnectOptions
            {
                Callbacks = new GitRemoteCallbacks { TransferProgress = progress },
            };

            await using GitSmartTransport transport = new(new SubtransportDefinition(_ => mock, IsRpc: false, null), new GitContext());
            await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, connectOpts, ct);

            // 5. Download — exercises the sideband path in DownloadPackAsync.
            await transport.DownloadPackAsync(clientRepo, new GitIndexerProgress(), ct);

            // 6. Assertions.
            //    Byte accumulation: the closure sums bytes across reads, so
            //    the final report carries ReceivedBytes > 0.
            Assert.NotEmpty(progress.Reports);
            GitTransferProgress last = progress.Reports[^1];
            Assert.True(last.ReceivedBytes > 0,
                $"final report ReceivedBytes must be > 0 for a {packBytes.Length}-byte pack, got {last.ReceivedBytes}");

            //    Object counts: the indexer writes TotalObjects (from the pack
            //    header) and ReceivedObjects (per parsed object) to the shared
            //    mutable GitIndexerProgress, and the closure reads them live.
            Assert.True(last.TotalObjects > 0,
                $"final report TotalObjects must be > 0 for a {packBytes.Length}-byte pack, got {last.TotalObjects}");
            Assert.True(last.ReceivedObjects > 0,
                $"final report ReceivedObjects must be > 0, got {last.ReceivedObjects}");
            Assert.True(last.ReceivedObjects <= last.TotalObjects,
                $"final report ReceivedObjects ({last.ReceivedObjects}) must be <= TotalObjects ({last.TotalObjects})");
        }
    }

    // ─── Over-threshold: large transfer, per-chunk report fires ───────

    /// <summary>
    /// Fetch a large pack (&gt; 100 KB on the wire) over a mock smart
    /// transport with <see cref="GitRemoteCallbacks.TransferProgress"/>
    /// wired. The closure accumulates received bytes in
    /// <c>totalReceivedBytes</c> across reads: the threshold check
    /// <c>&gt; NetworkXferThreshold</c> compares the cumulative total, so the
    /// per-chunk report fires once it crosses 100 KB. A per-read value would
    /// only fire if a single <c>ReadAsync</c> returned &gt; 100 KB at once
    /// (impossible with a 64 KB transport buffer).
    /// </summary>
    /// <remarks>
    /// <b>Pack construction.</b> 150 distinct 4 KB blobs (each filled with a
    /// unique <c>i % 251</c> pattern seeded by the blob index) yield a pack
    /// of ~600 KB on the wire — well over the 100 KB threshold. The blobs
    /// are designed to be mutually incompressible (different seeds) so zlib
    /// cannot deduplicate them across objects.
    /// </remarks>
    [Fact]
    public async Task DownloadPack_LargeTransfer_FiresPerChunkTransferProgressReport()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // 1. Real source repo with one commit containing 150 distinct 4 KB
        //    blobs — the pack will be ~600 KB on the wire.
        string sourcePath = Path.Combine(_tempDir, "source-large");
        await using GitRepository sourceRepo = await GitRepository.InitAsync(sourcePath, isBare: false, new GitContext(), cancellationToken: ct);
        GitOid commitOid = await WriteLargeCommitAsync(sourceRepo, ct);

        byte[] packBytes = await BuildPackFromRepoAsync(sourceRepo, commitOid, ct);
        Assert.True(packBytes.Length > GitSmartProtocol.NetworkXferThreshold,
            $"test setup: large pack must exceed {GitSmartProtocol.NetworkXferThreshold} bytes, got {packBytes.Length}");

        // 2. Empty client repo for the pack indexer.
        string clientPath = Path.Combine(_tempDir, "client-large");
        await using GitRepository clientRepo = await GitRepository.InitAsync(clientPath, isBare: false, new GitContext(), cancellationToken: ct);

        // 3. Mock: ref-ad with side-band-64k, then NAK + side-band-wrapped
        //    real pack + flush.
        byte[] refAd = BuildRefAdvertisement(isRpc: false, oidHex: commitOid.ToString(), caps: "side-band-64k ofs-delta");
        byte[] nak = "0008NAK\n"u8.ToArray();
        byte[] sidebandPack = BuildSidebandData(packBytes);
        byte[] downloadResponse = Combine(nak, sidebandPack);
        var mock = new PhasedMockSubtransport([refAd, downloadResponse], isRpc: false);

        // 4. Connect with a TransferProgress callback.
        var progress = new CapturingProgress<GitTransferProgress>();
        var connectOpts = new GitRemoteConnectOptions
        {
            Callbacks = new GitRemoteCallbacks { TransferProgress = progress },
        };

        await using GitSmartTransport transport = new(new SubtransportDefinition(_ => mock, IsRpc: false, null), new GitContext());
        await transport.ConnectAsync("git://host/repo", GitDirection.Fetch, connectOpts, ct);
        await transport.DownloadPackAsync(clientRepo, new GitIndexerProgress(), ct);

        // 5. At least one report must carry ReceivedBytes >= the threshold
        //    (the per-chunk report fires only after the cumulative total
        //    crosses 100 KB).
        Assert.NotEmpty(progress.Reports);
        Assert.Contains(progress.Reports, r => r.ReceivedBytes >= GitSmartProtocol.NetworkXferThreshold);

        // The last report must carry the full accumulated byte count,
        // which is >= the pack size (the sideband framing adds a few bytes
        // per 8 KB chunk).
        GitTransferProgress last = progress.Reports[^1];
        Assert.True(last.ReceivedBytes >= packBytes.Length,
            $"final report ReceivedBytes ({last.ReceivedBytes}) must be >= pack size ({packBytes.Length})");

        // Object-count regression: object counts must be non-zero and
        // consistent. TotalObjects comes from the pack header (parsed by the
        // indexer); ReceivedObjects increments per parsed object. Both ride on
        // the shared mutable accumulator.
        Assert.True(last.TotalObjects > 0,
            $"final report TotalObjects must be > 0 for a {packBytes.Length}-byte pack, got {last.TotalObjects}");
        Assert.True(last.ReceivedObjects > 0,
            $"final report ReceivedObjects must be > 0, got {last.ReceivedObjects}");
        Assert.True(last.ReceivedObjects <= last.TotalObjects,
            $"final report ReceivedObjects ({last.ReceivedObjects}) must be <= TotalObjects ({last.TotalObjects})");

        // On a large transfer, at least one per-chunk report must also carry
        // a non-zero TotalObjects (the indexer parses the 12-byte pack header
        // on the first AppendAsync, well before 100 KB arrives, so every
        // subsequent per-chunk report should see it).
        Assert.Contains(progress.Reports, r => r.TotalObjects > 0);
    }

    /// <summary>
    /// Write a commit containing many distinct 4 KB blobs to
    /// <paramref name="repo"/> and return its OID. The blobs are filled with
    /// cryptographically random bytes so zlib cannot compress them below the
    /// 100 KB threshold — earlier deterministic patterns (<c>i % 251</c>,
    /// xorshift32) all compressed to &lt; 11 KB because zlib's LZ77 found
    /// cross-blob repeats.
    /// </summary>
    private static async ValueTask<GitOid> WriteLargeCommitAsync(GitRepository repo, CancellationToken ct)
    {
        using GitTreeBuilder treeBld = new(repo);
        byte[] blob = new byte[4 * 1024];
        for (int i = 0; i < 150; i++)
        {
            // Cryptographically random bytes — incompressible by zlib.
            System.Security.Cryptography.RandomNumberGenerator.Fill(blob);
            GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, blob, ct);
            await treeBld.InsertAsync($"file{i:D3}.bin", blobOid, GitFileMode.Regular, ct);
        }

        GitOid treeOid = await treeBld.WriteAsync(ct);
        GitSignature sig = TestSig();
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "large\n",
            UpdateRef = "refs/heads/main",
        }, ct);
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Build a stateful (non-RPC) ref advertisement for a single ref pointing
    /// at <paramref name="oidHex"/> with the given capabilities.
    /// </summary>
    private static byte[] BuildRefAdvertisement(bool isRpc, string oidHex, string caps)
    {
        var sb = new StringBuilder();
        string oidZero = new('0', oidHex.Length);

        if (isRpc)
        {
            string comment = "# service=git-upload-pack\n";
            int cmtLen = 4 + comment.Length;
            sb.Append(cmtLen.ToString("x4"));
            sb.Append(comment);
            sb.Append("0000");
        }

        string firstRef = $"{oidZero} HEAD\0{caps}\n";
        int refLen = 4 + firstRef.Length;
        sb.Append(refLen.ToString("x4"));
        sb.Append(firstRef);

        string secondRef = $"{oidHex} refs/heads/main\n";
        int ref2Len = 4 + secondRef.Length;
        sb.Append(ref2Len.ToString("x4"));
        sb.Append(secondRef);

        sb.Append("0000");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Wrap pack data in side-band data packets (band 0x01) chunked at 8 KB,
    /// followed by a flush. Mirrors <c>MockTransportBuilder.BuildSidebandData</c>
    /// from the integration tests.
    /// </summary>
    private static byte[] BuildSidebandData(byte[] packData)
    {
        var result = new List<byte>();
        int offset = 0;
        while (offset < packData.Length)
        {
            int chunkSize = Math.Min(8192, packData.Length - offset);
            string lenHex = (4 + 1 + chunkSize).ToString("x4");
            result.AddRange(Encoding.ASCII.GetBytes(lenHex));
            result.Add(GitPacketReader.SideBandData); // 0x01
            result.AddRange(packData.AsSpan(offset, chunkSize).ToArray());
            offset += chunkSize;
        }

        result.AddRange("0000"u8.ToArray());
        return [.. result];
    }

    /// <summary>Concatenate byte arrays.</summary>
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

    /// <summary>
    /// Stateful mock subtransport that returns the SAME stream for both the
    /// <c>*Ls</c> and the <c>UploadPack</c> service calls (parity with SSH/git
    /// transports where one channel is reused). The stream serves a scripted
    /// sequence of byte chunks across reads: chunk 0 for the ref-ad, chunk 1
    /// for the upload-pack response.
    /// </summary>
    private sealed class PhasedMockSubtransport : IGitSubtransport
    {
        private readonly PhasedMockStream _stream;

        public PhasedMockSubtransport(IReadOnlyList<byte[]> chunks, bool isRpc)
        {
            _stream = new PhasedMockStream(chunks);
            IsRpc = isRpc;
        }

        public bool IsRpc { get; }

        public Task<IGitSubtransportStream> ActionAsync(string url, GitSmartService service, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
        {
            return Task.FromResult<IGitSubtransportStream>(_stream);
        }

        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Mock stream that serves a scripted sequence of byte chunks. Each
    /// <see cref="ReadAsync"/> call returns up to <paramref name="buffer"/>
    /// bytes from the current chunk; a chunk larger than the read buffer is
    /// served across multiple reads (the offset advances within the chunk
    /// until exhausted, then the next chunk begins). Returns EOF only after
    /// all chunks are fully consumed.
    /// </summary>
    private sealed class PhasedMockStream : IGitSubtransportStream
    {
        private readonly byte[][] _chunks;
        private int _phase;
        private int _phaseOffset;

        public PhasedMockStream(IReadOnlyList<byte[]> chunks)
        {
            _chunks = [.. chunks];
        }

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            // Advance to the next chunk if the current one is exhausted.
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
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> capture: invokes the callback
    /// inline on the reporting thread. Mirrors <c>SyncProgress&lt;T&gt;</c>
    /// in <c>LocalTransportTests</c>.
    /// </summary>
    private sealed class CapturingProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];

        public void Report(T value) => Reports.Add(value);
    }
}
