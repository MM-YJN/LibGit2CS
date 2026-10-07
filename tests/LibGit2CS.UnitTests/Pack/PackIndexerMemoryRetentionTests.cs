using System.Collections;
using System.Reflection;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Pack;

// the indexer
// retained every decompressed object body (IndexEntry.Body) and every
// pending delta body (PendingDelta.DeltaData) for the whole run — O(total
// decompressed size) managed memory on fetch of large or hostile packs
// (CVE-2018-10887/10888 class). Upstream stores only {oid, crc, offset}
// metadata (store_object, indexer.c:478-561), frees each body right after
// hashing (hash_and_save → git__free(obj.data), indexer.c:1162), and
// re-inflates deltas and their bases from the pack on demand
// (resolve_deltas → git_packfile_unpack; read_object_stream drains delta
// streams through a fixed buffer, indexer.c:344-356).
//
// Retention is not black-box observable (the .idx/.pack outputs are
// identical), so this test pins the retention shape via reflection on the
// indexer's private records and, more importantly, proves the on-demand
// re-inflation path still resolves delta chains correctly.
public sealed class PackIndexerMemoryRetentionTests : IDisposable
{
    private readonly string _tempDir;

    public PackIndexerMemoryRetentionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_PackIndexerMem_" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>
    /// Sums the decompressed bodies the indexer still retains in its object
    /// table. Returns 0 when the (fixed) metadata-only shape is in place.
    /// </summary>
    private static long SumRetainedBodies(GitPackIndexer indexer)
    {
        Type indexerType = typeof(GitPackIndexer);
        Type entryType = indexerType.GetNestedType("IndexEntry", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("IndexEntry type missing");
        PropertyInfo? body = entryType.GetProperty(
            "Body", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (body is null)
        {
            return 0; // metadata-only record.
        }

        FieldInfo objectsField = indexerType.GetField(
            "_objects", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_objects field missing");
        var objects = (IEnumerable)objectsField.GetValue(indexer)!;

        long total = 0;
        foreach (object entry in objects)
        {
            if (body.GetValue(entry) is byte[] bytes)
            {
                total += bytes.Length;
            }
        }

        return total;
    }

    [Fact]
    public async Task Indexer_StoresOnlyMetadata_AndResolvesDeltasFromPack()
    {
        // A pack with a large blob (8 MiB), a small base blob, and a REF
        // delta against the small base (all resolvable in-pack).
        byte[] largeBody = new byte[8 * 1024 * 1024];
        new Random(42).NextBytes(largeBody);
        byte[] baseBody = "base content\n"u8.ToArray();
        byte[] suffix = " - suffix"u8.ToArray();
        byte[] deltaData = ThinPackBuilder.MakeCopyAppendDelta(baseBody, suffix);
        byte[] resultBody = [.. baseBody, .. suffix];

        GitOid largeOid = GitObjectDb.HashObject(GitObjectType.Blob, largeBody, GitHashAlgorithmKind.Sha1);
        GitOid baseOid = GitObjectDb.HashObject(GitObjectType.Blob, baseBody, GitHashAlgorithmKind.Sha1);
        GitOid deltaOid = GitObjectDb.HashObject(GitObjectType.Blob, resultBody, GitHashAlgorithmKind.Sha1);

        var builder = new ThinPackBuilder(GitHashAlgorithmKind.Sha1);
        builder.AddFullObject(GitObjectType.Blob, largeBody);
        builder.AddFullObject(GitObjectType.Blob, baseBody);
        builder.AddRefDelta(baseOid, deltaData);
        byte[] packBytes = builder.Build();

        string packDir = Path.Combine(_tempDir, "pack_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(packDir);
        await using var indexer = new GitPackIndexer(packDir, GitHashAlgorithmKind.Sha1);
        var stats = new GitIndexerProgress();
        await indexer.AppendAsync(packBytes, stats, TestContext.Current.CancellationToken);
        await indexer.CommitAsync(stats, TestContext.Current.CancellationToken);

        // The .idx must contain all three objects with correct OIDs —
        // including the resolved delta, which is re-inflated from the pack
        // on demand (not from a retained body).
        string idxPath = Path.ChangeExtension(indexer.PackPath!, ".idx");
        using GitPackIndex idx = await GitPackIndex.OpenAsync(idxPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal(3, idx.ObjectCount);
        Assert.True(idx.FindIndex(largeOid).Found, "large blob missing from idx");
        Assert.True(idx.FindIndex(baseOid).Found, "base blob missing from idx");
        Assert.True(idx.FindIndex(deltaOid).Found, "resolved delta missing from idx");

        // the object table must not retain the decompressed bodies
        // (retaining them would sum the 8 MiB body + base + delta data).
        long retained = SumRetainedBodies(indexer);
        Assert.True(retained < 1024, $"indexer retained {retained} bytes of decompressed object bodies after commit");

        // The metadata records themselves must not carry body members.
        Type indexerType = typeof(GitPackIndexer);
        Type entryType = indexerType.GetNestedType("IndexEntry", BindingFlags.NonPublic)!;
        Assert.NotNull(entryType);
        Assert.Null(entryType.GetProperty("Body", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Type pendingType = indexerType.GetNestedType("PendingDelta", BindingFlags.NonPublic)!;
        Assert.NotNull(pendingType);
        Assert.Null(pendingType.GetProperty("DeltaData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }
}
