using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Refs;
using LibGit2CS.Remote;
using LibGit2CS.Repository;
using LibGit2CS.Submodule;
using LibGit2CS.Transports;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.IntegrationTests.Pack;

/// <summary>
/// End-to-end regression tests for the behaviors in the
/// pack/revwalk/submodule/remote/repository areas against libgit2 1.9.4:
/// (indexer retains
/// every decompressed body), (revparse '^{/pattern}' ReDoS),
/// (zero-OID gitlink on unborn submodule HEAD), (prune-after-
/// disconnect wipes tracking refs), (SetHead writes raw tag OIDs).
/// </summary>
/// <remarks>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </remarks>
public sealed class PackRevparseSubmoduleRemoteIntegrationTests
{
    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    private static string NewRepoPath(string kind)
        => Path.Combine(Path.GetTempPath(), $"libgit2cs-packhigh-{kind}-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<GitOid> CommitFileAsync(GitRepository repo, string workdir, string path, string content, CancellationToken ct)
    {
        string fullPath = Path.Combine(workdir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, ct);
        GitIndex index = await repo.GetIndexAsync(ct);
        await index.AddByPathAsync(path, ct);
        await index.WriteAsync(ct);
        GitOid treeOid = await index.WriteTreeAsync(ct);
        return await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = treeOid,
            Author = Sig,
            Committer = Sig,
            Message = $"add {path}\n",
            UpdateRef = "HEAD",
        }, ct);
    }

    // ── indexer metadata-only entries ──────────────────────────────

    /// <summary>Builds a pack: two full blobs + a REF_DELTA against the second.</summary>
    [SuppressMessage("Security", "CA5350:DoNotUseWeakCryptographicAlgorithms", Justification = "Git pack trailers are defined by SHA-1; this builds a valid pack for the transport mock.")]
    private static byte[] BuildTestPack(byte[] largeBody, byte[] baseBody, GitOid baseOid, byte[] deltaData)
    {
        using var ms = new MemoryStream();
        ms.Write("PACK"u8);
        WriteBE32(ms, 2);           // version
        WriteBE32(ms, 3);           // 3 objects

        // Object 1: full blob (large).
        WriteObjectHeader(ms, GitObjectType.Blob, largeBody.Length);
        ms.Write(ZlibTestHelpers.CompressLooseObject(largeBody));

        // Object 2: full blob (base).
        WriteObjectHeader(ms, GitObjectType.Blob, baseBody.Length);
        ms.Write(ZlibTestHelpers.CompressLooseObject(baseBody));

        // Object 3: REF_DELTA against baseOid.
        WriteObjectHeader(ms, GitObjectType.RefDelta, deltaData.Length);
        ms.Write(baseOid.RawBytes.ToArray());
        ms.Write(ZlibTestHelpers.CompressLooseObject(deltaData));

        // Trailer: SHA-1 of everything so far.
        byte[] trailer = SHA1.HashData(ms.ToArray());
        ms.Write(trailer);
        return ms.ToArray();
    }

    private static void WriteObjectHeader(Stream s, GitObjectType type, long size)
    {
        // Mirrors PackEncoding.WriteObjectHeader: 3-bit type + 4-bit size
        // (little-endian bit layout, continuation bits).
        byte b = (byte)(((int)type << 4) | ((int)size & 0x0F));
        size >>= 4;
        if (size > 0)
        {
            b |= 0x80;
        }

        s.WriteByte(b);
        while (size > 0)
        {
            byte next = (byte)(size & 0x7F);
            size >>= 7;
            if (size > 0)
            {
                next |= 0x80;
            }

            s.WriteByte(next);
        }
    }

    private static void WriteBE32(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    /// <summary>A minimal "copy-all + append" delta (copy base, insert suffix).</summary>
    private static byte[] MakeCopyAppendDelta(ReadOnlySpan<byte> baseBody, ReadOnlySpan<byte> suffix)
    {
        using var ms = new MemoryStream();
        // Source size varint (base len) + target size varint (base+suffix).
        WriteVarint(ms, baseBody.Length);
        WriteVarint(ms, baseBody.Length + suffix.Length);
        // Copy command: 0x80 marker + 0x10 ("size byte 0 present"), then the
        // 8-bit size (offset 0). Length must fit one byte (< 256).
        ms.WriteByte(0x90);
        ms.WriteByte((byte)baseBody.Length);
        // Insert command: length byte, then the bytes.
        ms.WriteByte((byte)suffix.Length);
        ms.Write(suffix);
        return ms.ToArray();
    }

    private static void WriteVarint(Stream s, int value)
    {
        // Git size varint: 7 bits per byte, MSB continuation, but the FIRST
        // byte holds the low 7 bits with bit 7 as continuation (little-endian
        // of the low bits).
        byte b = (byte)(value & 0x7F);
        value >>= 7;
        if (value > 0)
        {
            b |= 0x80;
        }

        s.WriteByte(b);
        while (value > 0)
        {
            byte next = (byte)(value & 0x7F);
            value >>= 7;
            if (value > 0)
            {
                next |= 0x80;
            }

            s.WriteByte(next);
        }
    }

    [Fact]
    public async Task Indexer_LargePack_IndexesAndResolvesDeltas()
    {
        string path = NewRepoPath("large-pack");
        try
        {
            byte[] largeBody = new byte[4 * 1024 * 1024];
            new Random(42).NextBytes(largeBody);
            byte[] baseBody = "base\n"u8.ToArray();
            byte[] suffix = "-suffix\n"u8.ToArray();
            byte[] resultBody = [.. baseBody, .. suffix];
            GitOid largeOid = GitObjectDb.HashObject(GitObjectType.Blob, largeBody, GitHashAlgorithmKind.Sha1);
            GitOid baseOid = GitObjectDb.HashObject(GitObjectType.Blob, baseBody, GitHashAlgorithmKind.Sha1);
            GitOid deltaOid = GitObjectDb.HashObject(GitObjectType.Blob, resultBody, GitHashAlgorithmKind.Sha1);

            byte[] packBytes = BuildTestPack(largeBody, baseBody, baseOid, MakeCopyAppendDelta(baseBody, suffix));

            string packDir = Path.Combine(path, "pack");
            Directory.CreateDirectory(packDir);
            await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
            var stats = new GitIndexerProgress();
            await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken);
            await indexer.CommitAsync(stats, TestContext.Current.CancellationToken);

            // The .idx carries all three objects — the delta resolved by
            // re-inflating base + delta from the pack (bodies are not
            // retained; this also bounds memory on large/hostile fetches).
            string idxPath = Path.ChangeExtension(indexer.PackPath!, ".idx");
            using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
            Assert.Equal(3, idx.ObjectCount);
            Assert.True(idx.FindIndex(largeOid).Found);
            Assert.True(idx.FindIndex(baseOid).Found);
            Assert.True(idx.FindIndex(deltaOid).Found);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── revparse grep ReDoS ────────────────────────────────────────

    [Fact]
    public async Task RevparseGrep_PathologicalPattern_Terminates()
    {
        string path = NewRepoPath("grep-pathological");
        try
        {
            string repoPath = Path.Combine(path, "repo");
            Directory.CreateDirectory(repoPath);
            await using GitRepository repo = await GitRepository.InitAsync(
                repoPath, isBare: false, new GitContext(),
                cancellationToken: TestContext.Current.CancellationToken);

            // Commit with a message ending in a long 'a' run + a non-matching
            // char — "(a+)+$" backtracks catastrophically on unbounded regexes.
            string run = new('a', 40);
            await File.WriteAllTextAsync(
                Path.Combine(repoPath, "f.txt"), "x\n",
                cancellationToken: TestContext.Current.CancellationToken);
            GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
            await index.AddByPathAsync("f.txt", TestContext.Current.CancellationToken);
            await index.WriteAsync(TestContext.Current.CancellationToken);
            GitOid treeOid = await index.WriteTreeAsync(TestContext.Current.CancellationToken);
            await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Author = Sig,
                Committer = Sig,
                Message = $"subject\n{run}!",
                UpdateRef = "HEAD",
            }, TestContext.Current.CancellationToken);

            // Must terminate (the 1 s per-match budget bounds the
            // backtracking; the pattern does not match this message).
            Task<GitObject?> revparse = Task.Run(() =>
                repo.RevparseSingleAsync("HEAD^{/(a+)+$}", TestContext.Current.CancellationToken));

            try
            {
                GitObject? result = await revparse.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                Assert.Null(result);
            }
            catch (GitException ex)
            {
                Assert.True(
                    ex.Code is GitErrorCode.NotFound or GitErrorCode.InvalidSpec,
                    $"unexpected error {ex.Code}: {ex.Message}");
            }
            catch (TimeoutException)
            {
                Assert.Fail("revparse '^{/(a+)+$}' did not terminate — catastrophic backtracking.");
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── zero-OID gitlink ───────────────────────────────────────────

    [Fact]
    public async Task SubmoduleAddToIndex_UnbornHead_Throws()
    {
        string path = NewRepoPath("submodule-unborn");
        try
        {
            string repoPath = Path.Combine(path, "parent");
            await using GitRepository repo = await GitRepository.InitAsync(
                repoPath, isBare: false, new GitContext(),
                cancellationToken: TestContext.Current.CancellationToken);
            string srcPath = Path.Combine(path, "src");
            await using GitRepository src = await GitRepository.InitAsync(
                srcPath, isBare: true, new GitContext(),
                cancellationToken: TestContext.Current.CancellationToken);

            GitSubmodule sm = await repo.SubmoduleAddSetupAsync(
                srcPath, "sub", useGitlink: true,
                cancellationToken: TestContext.Current.CancellationToken);

            // C (submodule.c:1059-1064): "cannot add submodule without HEAD
            // to index".
            GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
                await sm.AddToIndexAsync(writeIndex: true, TestContext.Current.CancellationToken));
            Assert.Equal(GitErrorCategory.Submodule, ex.Category);
            Assert.Contains("without HEAD", ex.Message);

            GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
            Assert.Equal(-1, index.Find("sub"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── prune-before-disconnect ────────────────────────────────────

    [Fact]
    public async Task Fetch_WithPrune_OverSmartTransport_KeepsTrackingRefs()
    {
        string path = NewRepoPath("fetch-prune");
        try
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            GitContext ctx = new();
            string clientPath = Path.Combine(path, "client");
            await using GitRepository client = await GitRepository.InitAsync(clientPath, isBare: false, ctx, cancellationToken: ct);
            GitOid commitOid = await CommitFileAsync(client, clientPath, "f.txt", "hello\n", ct);
            await client.ReferenceCreateAsync(
                "refs/remotes/origin/main", commitOid, force: false,
                logMessage: "setup", cancellationToken: ct);
            await client.Config.SetStringAsync("remote.origin.url", "l01://host/repo", ct);

            byte[] refAd = BuildRefAdvertisement(commitOid.ToString());
            byte[] emptyPack = BuildEmptyPack();
            byte[] downloadResponse = [.. "0008NAK\n"u8.ToArray(), .. BuildSidebandData(emptyPack)];
            var mock = new PhasedMockSubtransport([refAd, downloadResponse]);
            ctx.Transports.Register("l01", _ => new GitSmartTransport(
                new SubtransportDefinition(_ => mock, IsRpc: false, null), ctx));

            GitRemote remote = await client.RemoteLookupAsync("origin", ct);
            await remote.FetchAsync(
                refspecs: ["+refs/heads/main:refs/remotes/origin/main"],
                options: new GitFetchOptions { Prune = GitFetchPrune.Prune },
                cancellationToken: ct);

            // the tracking ref must survive (an empty
            // post-disconnect LsAsync would wipe every tracking ref).
            GitReference? tracking = await client.ReferenceLookupAsync("refs/remotes/origin/main", ct);
            Assert.NotNull(tracking);
            Assert.Equal(commitOid, ((GitDirectReference)tracking!).Target);
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static byte[] BuildRefAdvertisement(string oidHex)
    {
        var sb = new StringBuilder();
        string headRef = $"{oidHex} HEAD\0side-band-64k ofs-delta\n";
        sb.Append((4 + headRef.Length).ToString("x4")).Append(headRef);
        string mainRef = $"{oidHex} refs/heads/main\n";
        sb.Append((4 + mainRef.Length).ToString("x4")).Append(mainRef);
        sb.Append("0000");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [SuppressMessage("Security", "CA5350:DoNotUseWeakCryptographicAlgorithms", Justification = "Git pack trailers are defined by SHA-1; this builds a valid empty pack for the smart-transport mock.")]
    private static byte[] BuildEmptyPack()
    {
        byte[] header = [0x50, 0x41, 0x43, 0x4B, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00];
        byte[] trailer = SHA1.HashData(header);
        return [.. header, .. trailer];
    }

    private static byte[] BuildSidebandData(byte[] packData)
    {
        var result = new List<byte>();
        int offset = 0;
        while (offset < packData.Length)
        {
            int chunkSize = Math.Min(8192, packData.Length - offset);
            string lenHex = (4 + 1 + chunkSize).ToString("x4");
            result.AddRange(Encoding.ASCII.GetBytes(lenHex));
            result.Add(0x01); // Data channel
            result.AddRange(packData.AsSpan(offset, chunkSize).ToArray());
            offset += chunkSize;
        }

        result.AddRange("0000"u8);
        return [.. result];
    }

    /// <summary>A stateful mock subtransport serving one byte[] per service.</summary>
    private sealed class PhasedMockSubtransport : IGitSubtransport
    {
        private readonly byte[][] _phases;
        private int _phase;
        private int _currentPhase;
        private int _offset;
        private readonly List<byte> _received = [];

        public PhasedMockSubtransport(byte[][] phases)
        {
            _phases = phases;
        }

        public Task<IGitSubtransportStream> ActionAsync(string url, GitSmartService service, GitRemoteConnectOptions? options, CancellationToken cancellationToken)
        {
            int phase = Math.Min(_phase, _phases.Length - 1);
            _phase++;
            _offset = 0;
            _currentPhase = phase;
            return Task.FromResult<IGitSubtransportStream>(new MockStream(this));
        }

        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private int ReadData(Memory<byte> buffer)
        {
            byte[] source = _phases[_currentPhase];
            if (_offset >= source.Length)
            {
                return 0;
            }

            int toRead = Math.Min(buffer.Length, source.Length - _offset);
            source.AsSpan(_offset, toRead).CopyTo(buffer.Span);
            _offset += toRead;
            return toRead;
        }

        private void WriteData(ReadOnlyMemory<byte> data) => _received.AddRange(data.ToArray());

        private sealed class MockStream : IGitSubtransportStream
        {
            private readonly PhasedMockSubtransport _owner;

            public MockStream(PhasedMockSubtransport owner)
            {
                _owner = owner;
            }

            public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
                => Task.FromResult(_owner.ReadData(buffer));

            public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
            {
                _owner.WriteData(data);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // ── SetHead peels tags ─────────────────────────────────────────

    [Fact]
    public async Task SetHead_AnnotatedTag_DetachesAtPeeledCommit()
    {
        string path = NewRepoPath("sethead-peeled");
        try
        {
            string repoPath = Path.Combine(path, "repo");
            Directory.CreateDirectory(repoPath);
            await using GitRepository repo = await GitRepository.InitAsync(
                repoPath, isBare: false, new GitContext(),
                cancellationToken: TestContext.Current.CancellationToken);
            GitOid commitOid = await CommitFileAsync(repo, repoPath, "f.txt", "x\n", TestContext.Current.CancellationToken);

            Commit commit = (await repo.ObjectLookupAsync<Commit>(commitOid, TestContext.Current.CancellationToken))!;
            using (commit)
            {
                GitOid tagOid = await repo.TagCreateAsync(
                    "v1", commit, Sig, "release v1\n",
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.NotEqual(commitOid, tagOid);

                // C's detach (repository.c:3564-3585) writes the PEELED
                // commit.
                await repo.SetHeadAsync("refs/tags/v1", TestContext.Current.CancellationToken);
                GitReference head = (await repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!;
                Assert.Equal(commitOid, ((GitDirectReference)head).Target);
                Assert.NotEqual(tagOid, ((GitDirectReference)head).Target);

                await repo.SetHeadDetachedAsync(tagOid, TestContext.Current.CancellationToken);
                head = (await repo.ReferenceResolveAsync("HEAD", TestContext.Current.CancellationToken))!;
                Assert.Equal(commitOid, ((GitDirectReference)head).Target);
            }
        }
        finally
        {
            Cleanup(path);
        }
    }
}
