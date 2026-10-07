using System.Buffers.Binary;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Objects;

/// <summary> Parity tests for ODB object-side items: (loose "body longer than header" message/category),
/// (MemPackBackend prefix lookups are a no-op), ("no write-capable backend" wording), (FreshenAsync refresh-on-miss), (commit-graph parent-count
/// gate before materialization), (0-commit graph rejection), (parse errors map to Error, not Invalid), (peel category Object). Expectations are
/// C-verified against libgit2 1.9.4 (odb_loose.c:318-322, odb_mempack.c:193-211, odb.c:1656-1657, 1011-1024, commit_graph.c:104-108, 148-149, 173-174,
/// object.c:381-394). </summary>
public sealed class ObjectsLowParityTests2 : IDisposable
{
    private readonly string _tempDir;

    public ObjectsLowParityTests2()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ObjectsLow2_" + Guid.NewGuid().ToString("N")[..8]);
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

    private string NewDir()
    {
        string dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── loose "body longer than header" ────────────────────────

    [Fact]
    public async Task Read_HeadWindowBodyLongerThanDeclared_OdbMalformed()
    {
        // C (odb_loose.c:318-322): when the 64-byte head window already
        // decompresses more body bytes than the header declares, the error is
        // GIT_ERROR_ODB "malformed object: body was longer than specified in
        // header" — not the zlib "stream aborted prematurely" message.
        string objectsDir = Path.Combine(NewDir(), "objects");
        Directory.CreateDirectory(objectsDir);

        // Header declares 2 bytes; the body is 6 ("012345"); total 14 <= 64.
        GitOid oid = OdbTestHelpers.WriteLooseObject(objectsDir, GitObjectType.Blob, "012345"u8.ToArray(), declaredSize: 2);

        await using var backend = new LooseObjectBackend(objectsDir, GitHashAlgorithmKind.Sha1);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await backend.ReadAsync(oid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
        Assert.Contains("malformed object: body was longer than specified in header", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_BodyLongerBeyondHeadWindow_ZlibPremature()
    {
        // C: when the overflow is beyond the head window the zstream produces
        // the declared size but is not done → GIT_ERROR_ZLIB "stream aborted
        // prematurely" (odb_loose.c:326-329). The head-window check must not
        // swallow this path.
        string objectsDir = Path.Combine(NewDir(), "objects");
        Directory.CreateDirectory(objectsDir);

        // Header declares 60 bytes; the body is 100 ("x" * 100); total 108 > 64,
        // so the window (56 body bytes) fits but the full body overflows.
        byte[] body = Enumerable.Repeat((byte)'x', 100).ToArray();
        GitOid oid = OdbTestHelpers.WriteLooseObject(objectsDir, GitObjectType.Blob, body, declaredSize: 60);

        await using var backend = new LooseObjectBackend(objectsDir, GitHashAlgorithmKind.Sha1);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await backend.ReadAsync(oid, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Zlib, ex.Category);
    }

    // ── mempack prefix lookups are a no-op ─────────────────────

    [Fact]
    public async Task MemPackBackend_PrefixLookup_NeverMatches()
    {
        // C (odb_mempack.c:193-211): git_mempack_new registers no
        // exists_prefix/read_prefix/foreach — odb_exists_prefix_1 skips the
        // backend entirely (odb.c:1075) — C never matches.
        var mempack = new MemPackBackend();
        var oid = GitOid.FromRaw(Enumerable.Repeat((byte)0x55, 20).ToArray(), GitHashAlgorithmKind.Sha1);
        IObjectWriteBackend writer = mempack;
        IObjectBackend reader = mempack;
        await writer.WriteAsync(oid, GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // Full-length prefix: must NOT match (C has no exists_prefix).
        (bool foundFull, _) = await reader.ExistsPrefixAsync(oid, TestContext.Current.CancellationToken);
        Assert.False(foundFull);

        // Abbreviated prefix: must not match either.
        var shortPrefix = GitOid.Parse("55".AsSpan(), GitHashAlgorithmKind.Sha1);
        (bool foundShort, _) = await reader.ExistsPrefixAsync(shortPrefix, TestContext.Current.CancellationToken);
        Assert.False(foundShort);

        // Exact existence still works (mempack DOES register exists).
        Assert.True(reader.Exists(oid));
    }

    // ── "no write-capable backend" wording ─────────────────────

    [Fact]
    public async Task Write_NoWriteBackend_CUnsupportedMessage()
    {
        // C (odb.c:1656-1657, 1715-1716): git_odb__error_unsupported_in_backend
        // ("write object") → "cannot write object - unsupported in the loaded
        // odb backends" (GIT_ERROR_ODB).
        using var odbContext = new GitContext();
        var odb = new GitObjectDb(odbContext);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await odb.WriteAsync(GitObjectType.Blob, "hello\n"u8.ToArray(), TestContext.Current.CancellationToken));
        Assert.Equal("cannot write object - unsupported in the loaded odb backends", ex.Message);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
    }

    [Fact]
    public async Task OpenWriteStream_NoWriteBackend_CUnsupportedMessage()
    {
        using var odbContext = new GitContext();
        var odb = new GitObjectDb(odbContext);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await odb.OpenWriteStreamAsync(GitObjectType.Blob, 5, TestContext.Current.CancellationToken));
        Assert.Equal("cannot write object - unsupported in the loaded odb backends", ex.Message);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
    }

    // ── FreshenAsync refresh-on-miss ───────────────────────────

    [Fact]
    public async Task Freshen_OidOnlyInNewPack_RefreshesAndFinds()
    {
        // C (odb.c:1011-1024, git_odb__freshen): try once; on a miss refresh
        // the pack backends and retry (consulting only the refreshed
        // backends). An OID present only in a pack written after the ODB was
        // constructed is freshened instead of being reported missing.
        string objectsDir = Path.Combine(NewDir(), "objects");
        string packDir = Path.Combine(objectsDir, "pack");
        Directory.CreateDirectory(packDir);

        // Source repo holding the blob loose.
        string srcPath = Path.Combine(NewDir(), "src");
        await using GitRepository src = await GitRepository.InitAsync(srcPath, isBare: true, new GitContext(), TestContext.Current.CancellationToken);
        GitOid blob = await src.ObjectWriteAsync(GitObjectType.Blob, "payload\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // ODB over the empty objects dir (loose + pack backends).
        using var odbContext = new GitContext();
        var odb = new GitObjectDb(odbContext);
        await odb.AddDefaultBackendsAsync(objectsDir, GitHashAlgorithmKind.Sha1, alternateDepth: 0, TestContext.Current.CancellationToken);
        Assert.False(await odb.FreshenAsync(blob, TestContext.Current.CancellationToken));

        // Write a pack containing the blob AFTER the ODB was constructed.
        using (var pb = new LibGit2CS.Pack.GitPackWriter(src))
        {
            await pb.InsertAsync(blob, TestContext.Current.CancellationToken);
            await pb.WriteToDirectoryAsync(packDir, progress: null, TestContext.Current.CancellationToken);
        }

        Assert.True(await odb.FreshenAsync(blob, TestContext.Current.CancellationToken));
    }

    // ── commit-graph parent-count gate before materializing ────

    [Fact]
    public async Task CommitGraph_EntryWithHugeParentCount_ReturnsNull()
    {
        // C (commit_graph.c:630, commit_list.c:187): get_byindex records
        // parent_count/indices; OIDs are resolved lazily and an entry with
        // >65535 parents is discarded BEFORE any resolution.
        string objectsDir = Path.Combine(NewDir(), "objects");
        string infoDir = Path.Combine(objectsDir, "info");
        Directory.CreateDirectory(infoDir);

        var commitOid = GitOid.FromRaw(Enumerable.Repeat((byte)0x11, 20).ToArray(), GitHashAlgorithmKind.Sha1);
        byte[] graph = BuildCommitGraphWithHugeParentCount(commitOid);
        await File.WriteAllBytesAsync(Path.Combine(infoDir, "commit-graph"), graph, TestContext.Current.CancellationToken);

        CommitGraph? cg = await CommitGraph.OpenAsync(objectsDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.NotNull(cg);
        Assert.Equal(1, cg.NumCommits);

        // The entry must be discarded (null → revwalk falls back to the ODB),
        // not throw NotFound from resolving an out-of-range parent index.
        Assert.Null(cg.FindEntry(commitOid));
    }

    // ── 0-commit graph rejection + Error code ──────────────

    [Fact]
    public async Task CommitGraph_EmptyOidlChunk_Rejected()
    {
        // C (commit_graph.c:148-149): an empty OID Lookup chunk errors
        // ("empty OID Lookup chunk", GIT_ERROR_ODB, code -1).
        string objectsDir = NewDir();
        Directory.CreateDirectory(Path.Combine(objectsDir, "info"));
        byte[] graph = BuildEmptyCommitGraph(emptyChunk: "OIDL");
        await File.WriteAllBytesAsync(Path.Combine(objectsDir, "info", "commit-graph"), graph, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await CommitGraph.OpenAsync(objectsDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    [Fact]
    public async Task CommitGraph_EmptyOidfChunk_Rejected()
    {
        // C (commit_graph.c:131-132): an empty OID Fanout chunk errors with
        // code -1.
        string objectsDir = NewDir();
        Directory.CreateDirectory(Path.Combine(objectsDir, "info"));
        byte[] graph = BuildEmptyCommitGraph(emptyChunk: "OIDF");
        await File.WriteAllBytesAsync(Path.Combine(objectsDir, "info", "commit-graph"), graph, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await CommitGraph.OpenAsync(objectsDir, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
    }

    // ── peel check_type_combination category ───────────────────

    [Fact]
    public async Task Peel_BlobToCommit_ObjectCategory()
    {
        // C (object.c:381-394): peel_error sets GIT_ERROR_OBJECT for
        // check_type_combination failures (code stays GIT_EINVALIDSPEC).
        var blob = GitBlob.Parse(owner: null, GitOid.Parse("a65fedf39aef3a1b9e8f7c6d5b4a3a2a1a0a0f0e".AsSpan(), GitHashAlgorithmKind.Sha1), "hello\n"u8.ToArray());

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await blob.PeelAsync<Commit>(TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
        Assert.Equal(GitErrorCategory.Object, ex.Category);
        Assert.Contains("can not be successfully peeled into a", ex.Message, StringComparison.Ordinal);
    }

    // ── builders ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a commit-graph with 1 commit whose CDAT entry declares an
    /// extra-edge list of 65536 non-flag entries (parent_count 65538), all
    /// pointing out of range. C discards the entry via the uint16 gate in
    /// git_commit_list_parse (commit_list.c:187) without resolving parents.
    /// </summary>
    private static byte[] BuildCommitGraphWithHugeParentCount(GitOid commitOid)
    {
        const int numChunks = 4; // OIDF, OIDL, CDAT, EDGE
        int tableSize = (1 + numChunks) * 12;
        int oidfOff = 8 + tableSize;
        int oidlOff = oidfOff + 4 * 256;
        int cdatOff = oidlOff + 20;
        int edgeOff = cdatOff + 36;
        int edgeEntries = 65536 + 1; // 65536 non-flag + 1 flagged terminator
        int trailerOff = edgeOff + edgeEntries * 4;
        byte[] data = new byte[trailerOff + 20];

        "CGPH"u8.CopyTo(data.AsSpan(0, 4));
        data[4] = 1; // version
        data[5] = 1; // oid version
        data[6] = numChunks;

        WriteGraphChunkEntry(data, 8, 0, "OIDF", oidfOff);
        WriteGraphChunkEntry(data, 8, 1, "OIDL", oidlOff);
        WriteGraphChunkEntry(data, 8, 2, "CDAT", cdatOff);
        WriteGraphChunkEntry(data, 8, 3, "EDGE", edgeOff);
        WriteGraphChunkEntry(data, 8, 4, 0x00000000, trailerOff);

        for (int i = 0x11; i < 256; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(oidfOff + i * 4), 1);
        }

        commitOid.RawBytes.CopyTo(data.AsSpan(oidlOff));

        // CDAT entry: tree OID zeros, parent0 = 0 (valid), parent1 = extra
        // edge at position 0 (0x80000000), generation 0, time 0.
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(cdatOff + 20), 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(cdatOff + 24), 0x80000000);

        for (int i = 0; i < 65536; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(edgeOff + i * 4), 0x7fffffff); // non-flag, out-of-range
        }

        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(edgeOff + 65536 * 4), 0x80000000); // flagged terminator

        return data;
    }

    /// <summary>
    /// Builds a commit-graph with an all-zero fanout and an empty chunk of the
    /// given kind ("OIDF" or "OIDL"). All chunks sit at the first chunk offset;
    /// the EDGE chunk absorbs the gap to the trailer.
    /// </summary>
    private static byte[] BuildEmptyCommitGraph(string emptyChunk)
    {
        const int numChunks = 4;
        int tableSize = (1 + numChunks) * 12;
        int firstChunkOff = 8 + tableSize;
        int trailerOff;
        byte[] data;

        if (emptyChunk == "OIDF")
        {
            // OIDF/OIDL/CDAT all at firstChunkOff (length 0); EDGE's derived
            // length is the gap to the trailer (must be % 4 == 0).
            trailerOff = firstChunkOff + 24;
            data = new byte[trailerOff + 20];
        }
        else
        {
            // OIDF occupies [firstChunkOff, firstChunkOff+1024); OIDL/CDAT/EDGE
            // at the end of it (length 0).
            trailerOff = firstChunkOff + 1024 + 20;
            data = new byte[trailerOff + 20];
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(firstChunkOff + 255 * 4), 0);
        }

        "CGPH"u8.CopyTo(data.AsSpan(0, 4));
        data[4] = 1;
        data[5] = 1;
        data[6] = numChunks;

        int oidfOff = firstChunkOff;
        int oidlOff = emptyChunk == "OIDF" ? firstChunkOff : firstChunkOff + 4 * 256;
        int cdatOff = oidlOff;
        int edgeOff = cdatOff;
        _ = oidfOff; // OIDF offset equals firstChunkOff in both layouts

        WriteGraphChunkEntry(data, 8, 0, "OIDF", oidfOff);
        WriteGraphChunkEntry(data, 8, 1, "OIDL", oidlOff);
        WriteGraphChunkEntry(data, 8, 2, "CDAT", cdatOff);
        WriteGraphChunkEntry(data, 8, 3, "EDGE", edgeOff);
        WriteGraphChunkEntry(data, 8, 4, 0x00000000, trailerOff);

        return data;
    }

    private static void WriteGraphChunkEntry(byte[] data, int tableStart, int index, string id, int offset)
    {
        uint idValue = (uint)((id[0] << 24) | (id[1] << 16) | (id[2] << 8) | id[3]);
        WriteGraphChunkEntry(data, tableStart, index, idValue, offset);
    }

    private static void WriteGraphChunkEntry(byte[] data, int tableStart, int index, uint idValue, int offset)
    {
        int pos = tableStart + index * 12;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(pos), idValue);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(pos + 4), (ulong)offset);
    }
}
