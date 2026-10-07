using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Pack;

/// <summary>
/// Golden pack parity test: the packbuilder's output must match C libgit2
/// 1.9.4 structurally — object order, types, sizes, delta-base choices, and
/// the DECOMPRESSED content of every object (including delta instructions).
/// The zlib-framed bytes themselves are not compared: the .NET bundled zlib
/// emits a different (valid) DEFLATE stream than the system zlib the C build
/// links (thin-pack byte parity is
/// compressor-dependent). The golden file was produced by a C
/// git_packbuilder probe (git_packbuilder_new + git_packbuilder_insert × 7 +
/// git_packbuilder_write_buf) with fixed signatures (T &lt;t@t&gt;
/// 1700000000 +0000).
/// </summary>
public sealed class PackGoldenBytesTests : IDisposable
{
    private readonly string _tempDir;

    public PackGoldenBytesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackGolden_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static GitSignature Sig() => new("T", "t@t", new GitTime(1700000000, 0));

    /// <summary>A pack object's observable structure: type, size, delta base
    /// (null for whole objects), and decompressed content.</summary>
    private sealed record PackObjectShape(int Type, long Size, long? DeltaBaseOffset, byte[] Body);

    /// <summary>
    /// Scans a raw pack byte stream into its structural shape list.
    /// </summary>
    private static List<PackObjectShape> ScanPack(ReadOnlySpan<byte> pack)
    {
        if (pack.Length < 12 || pack[0] != (byte)'P' || pack[1] != (byte)'A' || pack[2] != (byte)'C' || pack[3] != (byte)'K')
        {
            throw new InvalidDataException("not a pack");
        }

        uint version = (uint)((pack[4] << 24) | (pack[5] << 16) | (pack[6] << 8) | pack[7]);
        Assert.Equal(2u, version);
        int count = (pack[8] << 24) | (pack[9] << 16) | (pack[10] << 8) | pack[11];

        var result = new List<PackObjectShape>();
        int pos = 12;
        for (int i = 0; i < count; i++)
        {
            long offset = pos;
            int b = pack[pos++];
            int type = (b >> 4) & 7;
            long size = b & 15;
            int shift = 4;
            while ((b & 0x80) != 0)
            {
                b = pack[pos++];
                size |= (long)(b & 0x7f) << shift;
                shift += 7;
            }

            long? deltaBase = null;
            if (type == 6) // OFS_DELTA
            {
                b = pack[pos++];
                long baseOffset = b & 127;
                while ((b & 128) != 0)
                {
                    baseOffset += 1;
                    b = pack[pos++];
                    baseOffset = (baseOffset << 7) | (long)(b & 127);
                }

                deltaBase = offset - baseOffset;
            }
            else if (type == 7) // REF_DELTA
            {
                pos += 20;
            }

            // zlib body: decompress and consume exactly the stream.
            using var body = new PooledByteBufferWriter();
            Zlib.DecompressPackObject(body, pack[pos..], out int consumed);
            pos += consumed;

            result.Add(new PackObjectShape(type, size, deltaBase, body.WrittenSpan.ToArray()));
        }

        Assert.Equal(count, result.Count);
        return result;
    }

    [Fact]
    public async Task Write_MatchesCStructurally()
    {
        await using GitRepository repo = await GitRepository.InitAsync(_tempDir, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        // The C probe's object set: 2 large patterned blobs (b1 REF_DELTAs
        // against b0), 2 trees, 2 commits, 1 tag.
        byte[] big0 = new byte[600];
        byte[] big1 = new byte[600];
        for (int i = 0; i < 600; i++)
        {
            big0[i] = (byte)('a' + (i % 26));
            big1[i] = big0[i];
        }

        big0[300] = (byte)'X';
        big1[300] = (byte)'Y';
        big0[599] = (byte)'\n';
        big1[599] = (byte)'\n';
        GitOid b0 = await repo.ObjectWriteAsync(GitObjectType.Blob, big0, TestContext.Current.CancellationToken);
        GitOid b1 = await repo.ObjectWriteAsync(GitObjectType.Blob, big1, TestContext.Current.CancellationToken);

        using GitTreeBuilder tb = repo.NewTreeBuilder();
        await tb.InsertAsync("a.txt", b0, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid t0 = await tb.WriteAsync(CancellationToken.None);
        await tb.InsertAsync("b.txt", b1, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid t1 = await tb.WriteAsync(CancellationToken.None);

        GitTree t1Tree = (await repo.ObjectLookupAsync<GitTree>(t1, TestContext.Current.CancellationToken))!;

        GitSignature sig = Sig();
        GitOid c0 = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = t0,
            Parents = [],
            Author = sig,
            Committer = sig,
            Message = "first commit\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);
        GitOid c1 = await repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = t1,
            Parents = [c0],
            Author = sig,
            Committer = sig,
            Message = "second commit\n",
            UpdateRef = "refs/heads/master",
        }, cancellationToken: TestContext.Current.CancellationToken);

        GitOid tag = await repo.TagCreateAsync("v1", t1Tree, sig, "tag msg\n", cancellationToken: TestContext.Current.CancellationToken);

        // C probe: git_packbuilder_insert(c0), (c1), (t0), (t1), (b0), (b1), (tag).
        using GitPackWriter writer = repo.NewPackWriter();
        await writer.InsertAsync(c0, TestContext.Current.CancellationToken);
        await writer.InsertAsync(c1, TestContext.Current.CancellationToken);
        await writer.InsertAsync(t0, TestContext.Current.CancellationToken);
        await writer.InsertAsync(t1, TestContext.Current.CancellationToken);
        await writer.InsertAsync(b0, TestContext.Current.CancellationToken);
        await writer.InsertAsync(b1, TestContext.Current.CancellationToken);
        await writer.InsertAsync(tag, TestContext.Current.CancellationToken);

        await writer.PrepareAsync(TestContext.Current.CancellationToken);

        using var ms = new MemoryStream();
        await writer.WriteAsync(ms, null, TestContext.Current.CancellationToken);
        byte[] actual = ms.ToArray();

        byte[] golden = FixtureLoader.LoadBytes("Fixtures.pack.golden-basic.pack");
        List<PackObjectShape> goldenShapes = ScanPack(golden);
        List<PackObjectShape> actualShapes = ScanPack(actual);

        Assert.Equal(goldenShapes.Count, actualShapes.Count);
        for (int i = 0; i < goldenShapes.Count; i++)
        {
            Assert.Equal(goldenShapes[i].Type, actualShapes[i].Type);
            Assert.Equal(goldenShapes[i].Size, actualShapes[i].Size);
            Assert.Equal(goldenShapes[i].DeltaBaseOffset, actualShapes[i].DeltaBaseOffset);
            Assert.Equal(goldenShapes[i].Body, actualShapes[i].Body);
        }
    }
}
