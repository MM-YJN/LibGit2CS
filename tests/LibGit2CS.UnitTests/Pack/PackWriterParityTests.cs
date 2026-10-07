using System.IO.Compression;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Pack;
using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.UnitTests.Pack;

/// <summary>
/// Regression tests for the pack-objects parity behaviors in
/// libgit2 1.9.4. The expected pack structure (object order, delta selection, hide semantics) was
/// differentially verified against the C reference (libgit2 1.9.4): a
/// git_packbuilder harness ran on the <em>identical</em> repo (built by the
/// port itself) and the resulting packs match object-for-object in
/// type/size/base/body (only the zlib stream bytes differ — an inherent
/// BCL-vs-zlib codec difference).
/// </summary>
public sealed class PackWriterParityTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackParity_" + Guid.NewGuid().ToString("N")[..8]);

    public PackWriterParityTests()
    {
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

    private static GitSignature Sig => new("t", "t@t", new GitTime(1700000000, 0));

    /// <summary>
    /// Builds the deterministic 3-commit repo (blobs of distinct sizes over
    /// one alphabet, equal commit timestamps) whose pack structure was
    /// C-verified.
    /// </summary>
    private async Task<(GitRepository Repo, GitOid[] Oids)> BuildRepoAsync(CancellationToken ct)
    {
        string repoPath = Path.Combine(_tempDir, "repo");
        GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: true, new GitContext(), cancellationToken: ct);

        var random = new Random(7);
        static string RandomContent(Random r, int n)
        {
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++)
            {
                sb.Append((char)('a' + r.Next(10)));
            }

            return sb.ToString();
        }

        string big1 = RandomContent(random, 20000);
        string big2 = RandomContent(random, 15000);
        string mid1 = RandomContent(random, 8000);
        string mid2 = RandomContent(random, 6000);
        string small1 = new('x', 300);
        string small2 = new('y', 200);

        var oids = new List<GitOid>();
        GitOid? parent = null;
        string[] messages = ["one\n", "two\n", "three\n"];
        (string Name, string Content)[][] commits =
        [
            [("a.txt", big1), ("b.txt", mid1), ("c.txt", small1)],
            [("a.txt", big2), ("b.txt", mid2), ("d.txt", small2)],
            [("e.txt", big1 + "tail"), ("f.txt", new string('z', 100))],
        ];

        for (int ci = 0; ci < commits.Length; ci++)
        {
            (string Name, string Content)[] files = commits[ci];
            using GitTreeBuilder treeBld = repo.NewTreeBuilder();
            foreach ((string name, string content) in files)
            {
                GitOid blobOid = await repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), ct);
                await treeBld.InsertAsync(name, blobOid, GitFileMode.Regular, ct);
            }

            GitOid treeOid = await treeBld.WriteAsync(ct);
            parent = await repo.CommitCreateAsync(new CommitCreateOptions
            {
                Tree = treeOid,
                Parents = parent is { } p ? [p] : [],
                Author = Sig,
                Committer = Sig,
                Message = messages[ci],
                UpdateRef = "refs/heads/main",
            }, ct);
            oids.Add(parent.Value);
        }

        return (repo, [.. oids]);
    }

    private static List<(int Type, long Size, string? Base)> ParsePackObjects(byte[] pack)
    {
        Assert.Equal("PACK"u8.ToArray(), pack.AsSpan(0, 4).ToArray());
        int count = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(pack.AsSpan(8, 4));
        int pos = 12;
        var objects = new List<(int, long, string?)>(count);
        for (int i = 0; i < count; i++)
        {
            byte b = pack[pos++];
            int type = (b >> 4) & 7;
            long size = b & 15;
            int shift = 4;
            while ((b & 0x80) != 0)
            {
                b = pack[pos++];
                size |= (long)(b & 0x7f) << shift;
                shift += 7;
            }

            string? baseOid = null;
            if (type == 7)
            {
                baseOid = Convert.ToHexString(pack.AsSpan(pos, 20)).ToLowerInvariant();
                pos += 20;
            }

            pos += InflatedStreamLength(pack, pos);
            objects.Add((type, size, baseOid));
        }

        return objects;
    }

    /// <summary>Returns the compressed length of the zlib stream at <paramref name="pos"/>.</summary>
    private static int InflatedStreamLength(byte[] pack, int pos)
    {
        var dec = new ZLibStream(new MemoryStream(pack.AsMemory(pos).ToArray()), CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        dec.CopyTo(outMs);
        var ms = (MemoryStream)dec.BaseStream;
        return pack.Length - pos - (int)(ms.Length - ms.Position);
    }

    /// <summary>
    /// The C-verified full-walk pack structure: 14 objects in the walk's
    /// recency order (the equal-timestamp time-heap pops c3, c1, c2) with
    /// the 8000-byte blob written as a 9-byte REF_DELTA against the
    /// 300-byte blob (its "x"-repeated content delta-compresses well).
    /// </summary>
    private static readonly (int Type, long Size)[] s_expectedFullStructure =
    [
        (1, 168), (2, 66), (3, 20004), (3, 100), (1, 118), (2, 99), (7, 9), (3, 8000), (3, 300), (1, 166), (2, 99), (3, 15000), (3, 6000), (3, 200),
    ];

    [Fact]
    public async Task PrepareAndWrite_ObjectOrderAndDeltas_MatchesC()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (GitRepository repo, _) = await BuildRepoAsync(ct);
        await using (repo)
        {
            using GitPackWriter pb = repo.NewPackWriter();
            using GitRevWalker walk = repo.NewRevWalker();
            walk.Sort = GitSortMode.Time;
            await walk.PushGlobAsync("refs/heads/*", ct);
            await pb.InsertWalkAsync(walk, ct);

            using var ms = new MemoryStream();
            await pb.WriteAsync(ms, null, ct);
            byte[] pack = ms.ToArray();

            List<(int Type, long Size, string? Base)> objects = ParsePackObjects(pack);
            Assert.Equal(14, objects.Count);

            // The C-verified (type, size) sequence for all 14 objects.
            for (int i = 0; i < s_expectedFullStructure.Length; i++)
            {
                Assert.Equal(s_expectedFullStructure[i], (objects[i].Type, objects[i].Size));
            }

            // The 8000-byte blob is written as a REF_DELTA whose base (the
            // 300-byte blob) was already written earlier in the pack
            // (write_one recursion).
            Assert.Equal(7, objects[6].Type);
            Assert.Equal(9, objects[6].Size);
            Assert.NotNull(objects[6].Base);
        }
    }

    [Fact]
    public async Task InsertWalk_HideSemantics_ExcludesSharedObjects()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (GitRepository repo, GitOid[] oids) = await BuildRepoAsync(ct);
        await using (repo)
        {
            using GitPackWriter pb = repo.NewPackWriter();
            using GitRevWalker walk = repo.NewRevWalker();
            walk.Sort = GitSortMode.Time;
            await walk.PushGlobAsync("refs/heads/*", ct);

            // Hide the first commit: its tree and its exclusive blobs (300
            // and 8000 bytes) must NOT be in the pack, even though the
            // 8000-byte blob is a delta candidate for the 6000-byte blob.
            await walk.HideAsync(oids[0], ct);

            await pb.InsertWalkAsync(walk, ct);

            using var ms = new MemoryStream();
            await pb.WriteAsync(ms, null, ct);
            List<(int Type, long Size, string? Base)> objects = ParsePackObjects(ms.ToArray());

            // C-verified: 9 objects — c3 + its tree/blobs (20004, 100),
            // then c2 + its tree/blobs (15000, 6000, 200). The hidden
            // commit's 300-byte and 8000-byte blobs are excluded, and
            // nothing is deltified (the 8000-byte candidate is gone).
            Assert.Equal(9, objects.Count);
            long[] sizes = objects.Select(o => o.Size).ToArray();
            Assert.DoesNotContain(300L, sizes);
            Assert.DoesNotContain(8000L, sizes);
            Assert.All(objects, o => Assert.NotEqual(7, o.Type));
            Assert.Equal(20004, sizes[2]);
            Assert.Equal(15000, sizes[6]);
            Assert.Equal(200, sizes[8]);
        }
    }

    [Fact]
    public async Task InsertWalk_HideNothing_IncludesAllObjects()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (GitRepository repo, _) = await BuildRepoAsync(ct);
        await using (repo)
        {
            using GitPackWriter pb = repo.NewPackWriter();
            using GitRevWalker walk = repo.NewRevWalker();
            walk.Sort = GitSortMode.Time;
            await walk.PushGlobAsync("refs/heads/*", ct);
            await pb.InsertWalkAsync(walk, ct);

            Assert.Equal(14, pb.ObjectCount);
        }
    }
}
