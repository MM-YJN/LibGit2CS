using System.IO.Compression;
using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Compression;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Objects;

namespace LibGit2CS.UnitTests.Objects;

/// <summary>
/// Regression tests for the ODB object behaviors in
/// libgit2 1.9.4. Every expectation is
/// C-verified against libgit2 1.9.4 (probes against the system library).
/// </summary>
public sealed class OdbMedParityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _objectsDir;

    public OdbMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_OdbMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _objectsDir = Path.Combine(_tempDir, "objects");
        Directory.CreateDirectory(_objectsDir);
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

    // ---------------------------------------------------------------
    // delta type↔string mapping — C uses uppercase "OFS_DELTA"/
    // "REF_DELTA" (object.c git_objects_table).
    // ---------------------------------------------------------------

    [Fact]
    public void TypeToString_DeltaTypes_Uppercase()
    {
        // C: git_object_type2string(GIT_OBJECT_OFS_DELTA) == "OFS_DELTA"
        // (object.c:314-320, git_objects_table entry 6).
        Assert.Equal("OFS_DELTA", GitObjectDb.TypeToString(GitObjectType.OfsDelta));
        Assert.Equal("REF_DELTA", GitObjectDb.TypeToString(GitObjectType.RefDelta));
    }

    // ---------------------------------------------------------------
    // loose-header type name parsed by PREFIX match in C
    // (git_object_stringn2type, object.c:330-343 — the table entry must be
    // a prefix of the header's type name), and the header is parsed from a
    // 64-byte inflate window.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Read_LooseHeaderType_TrailingGarbageAfterType_Accepted()
    {
        // C probe: header "blobX 5\0hello" → read OK, blob, size 5, "hello"
        // ("blob" is a prefix of "blobX" — git__prefixncmp matches).
        GitOid oid = WriteRawLooseObject("blobX 5\0hello"u8.ToArray());

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        RawObjectData? raw = await backend.ReadAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(raw);
        Assert.Equal(GitObjectType.Blob, raw!.Value.Type);
        Assert.Equal(5, raw.Value.Size);
        Assert.Equal("hello"u8.ToArray(), raw.Value.Data);
    }

    [Fact]
    public async Task Read_LooseHeaderType_PartialName_Rejected()
    {
        // C probe: header "comm 5\0hello" → -1 "failed to inflate disk object"
        // ("commit" is NOT a prefix of "comm" — git__prefixncmp("comm", 4,
        // "commit") < 0 → GIT_OBJECT_INVALID → not a loose type).
        GitOid oid = WriteRawLooseObject("comm 5\0hello"u8.ToArray());

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await backend.ReadAsync(oid, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
    }

    [Fact]
    public async Task ReadHeader_LooseHeaderType_PartialName_Rejected()
    {
        // C probe: read_header on "comm 5\0..." → -1 "failed to read loose
        // object header" (GIT_ERROR_ZLIB).
        GitOid oid = WriteRawLooseObject("comm 5\0hello"u8.ToArray());

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await backend.ReadHeaderAsync(oid, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Zlib, ex.Category);
    }

    [Fact]
    public async Task ReadHeader_LooseHeader_HeaderWindow_64Bytes()
    {
        // C parses the header from a 64-byte inflate window; a '\0' beyond
        // that window makes the header unparseable ("failed to parse loose
        // object: invalid header"). Build a header whose size field pushes the
        // '\0' past byte 64.
        string type = "blob";
        string size = new('9', 70); // digits push the NUL past the window
        byte[] header = Encoding.ASCII.GetBytes($"{type} {size}\0");

        GitOid oid = WriteRawLooseObject([.. header, .. "hello"u8.ToArray()]);

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await backend.ReadAsync(oid, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Object, ex.Category);
    }

    // ---------------------------------------------------------------
    // loose-header size: C's git__strntol64 tolerates trailing garbage,
    // skips leading whitespace, and rejects negatives/overflow.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Read_SizeTrailingGarbage_Tolerated()
    {
        // C probe: "blob 5xyz\0hello" → size 5, body "hello".
        GitOid oid = WriteRawLooseObject("blob 5xyz\0hello"u8.ToArray());

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        RawObjectData? raw = await backend.ReadAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(raw);
        Assert.Equal(5, raw!.Value.Size);
        Assert.Equal("hello"u8.ToArray(), raw.Value.Data);
    }

    [Fact]
    public async Task Read_SizeLeadingWhitespace_Tolerated()
    {
        // C probe: "blob  5\0hello" → size 5 (strntol64 skips whitespace).
        GitOid oid = WriteRawLooseObject("blob  5\0hello"u8.ToArray());

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        RawObjectData? raw = await backend.ReadAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(raw);
        Assert.Equal(5, raw!.Value.Size);
    }

    [Fact]
    public async Task Read_SizeNegative_Throws()
    {
        // C probe: "blob -5\0hello" → -1 "failed to parse loose object:
        // invalid header" (size < 0 rejected by parse_header).
        GitOid oid = WriteRawLooseObject("blob -5\0hello"u8.ToArray());

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await backend.ReadAsync(oid, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Object, ex.Category);
    }

    [Fact]
    public async Task Read_SizeOverflow_Throws()
    {
        // C probe: 26-digit size → -1 "failed to parse loose object:
        // invalid header" (strntol64 overflow).
        GitOid oid = WriteRawLooseObject("blob 99999999999999999999999999\0hello"u8.ToArray());

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        await Assert.ThrowsAsync<GitException>(async () =>
            await backend.ReadAsync(oid, TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------
    // short loose-object body: C zero-pads silently
    // (read_loose_standard, odb_loose.c:330-335).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Read_ShortBody_ZeroPadded()
    {
        // C probe: "blob 10\0hi" unpacks to 10 bytes: "hi" + 8 NULs (the ODB
        // read then fails strict hash verification — GIT_EMISMATCH — because
        // the padded content hashes differently; the backend unpack itself
        // succeeds).
        GitOid oid = WriteRawLooseObject("blob 10\0hi"u8.ToArray());

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        RawObjectData? raw = await backend.ReadAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(raw);
        Assert.Equal(10, raw!.Value.Size);
        Assert.Equal(10, raw.Value.Data.Length);
        Assert.Equal("hi"u8.ToArray(), raw.Value.Data[..2]);
        Assert.All(raw.Value.Data[2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Lookup_ShortBody_ThrowsHashMismatch()
    {
        // C: git_odb_read verifies the unpacked object's hash
        // (git_odb__strict_hash_verification = true, odb.c:36, 1375) → the
        // zero-padded body hashes differently → GIT_EMISMATCH.
        GitOid oid = WriteRawLooseObject("blob 10\0hi"u8.ToArray());

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await db.LookupAsync(oid, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Mismatch, ex.Code);
    }

    // ---------------------------------------------------------------
    // trailing garbage after the zlib stream: C rejects ("zlib input had
    // trailing garbage" for small objects; "failed to finish zlib inflation:
    // stream aborted prematurely" for large ones — both GIT_ERROR_ZLIB).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Read_TrailingGarbage_Throws()
    {
        byte[] compressed = ZlibTestHelpers.CompressLooseObject("blob 5\0hello"u8.ToArray());
        GitOid oid = HashRaw("blob 5\0hello"u8.ToArray());
        WriteRawLooseFile([.. compressed, .. "XYZ"u8.ToArray()], oid);

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await backend.ReadAsync(oid, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Zlib, ex.Category);
    }

    [Fact]
    public async Task Read_TrailingGarbage_LargeObject_Throws()
    {
        // Total decompressed > 64 bytes → C's error is "failed to finish zlib
        // inflation: stream aborted prematurely" (git_zstream_done false).
        byte[] bigBody = Encoding.ASCII.GetBytes(new string('a', 200));
        byte[] compressed = ZlibTestHelpers.CompressLooseObject([.. "blob 200\0"u8.ToArray(), .. bigBody]);
        GitOid oid = HashRaw([.. "blob 200\0"u8.ToArray(), .. bigBody]);
        WriteRawLooseFile([.. compressed, .. "garbage"u8.ToArray()], oid);

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await backend.ReadAsync(oid, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Zlib, ex.Category);
    }

    // ---------------------------------------------------------------
    // legacy packlike loose objects (binary type/size header + zlib
    // body) are readable in C (read_loose_packlike).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Read_PacklikeLooseObject_ReturnsData()
    {
        // Packlike header: byte0 = (type << 4) | (size & 15), continuation
        // bits for larger sizes. Blob (3), size 5 → 0x35.
        byte[] body = ZlibTestHelpers.CompressLooseObject("hello"u8.ToArray());
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, "hello"u8.ToArray(), GitHashAlgorithmKind.Sha1);
        WriteRawLooseFile([0x35, .. body], oid);

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        RawObjectData? raw = await backend.ReadAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(raw);
        Assert.Equal(GitObjectType.Blob, raw!.Value.Type);
        Assert.Equal(5, raw.Value.Size);
        Assert.Equal("hello"u8.ToArray(), raw.Value.Data);
    }

    [Fact]
    public async Task Read_PacklikeLooseObject_VarintSize_ReturnsData()
    {
        // Size 300 (0b100101100): c0 = (3 << 4) | (300 & 15) | 0x80 = 0xBC;
        // c1 = (300 >> 4) & 0x7f = 18 (the varint shift starts at 4).
        byte[] bigBody = Encoding.ASCII.GetBytes(new string('x', 300));
        byte[] body = ZlibTestHelpers.CompressLooseObject(bigBody);
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, bigBody, GitHashAlgorithmKind.Sha1);
        WriteRawLooseFile([0xBC, 0x12, .. body], oid);

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        RawObjectData? raw = await backend.ReadAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(raw);
        Assert.Equal(GitObjectType.Blob, raw!.Value.Type);
        Assert.Equal(300, raw.Value.Size);
        Assert.Equal(bigBody, raw.Value.Data);
    }

    [Fact]
    public async Task Read_PacklikeLooseObject_NonLooseType_Throws()
    {
        // Type nibble 0 (GIT_OBJECT__EXT1) → not loose → C: -1 "failed to
        // inflate loose object" (GIT_ERROR_ODB).
        byte[] body = ZlibTestHelpers.CompressLooseObject("hello"u8.ToArray());
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, "hello"u8.ToArray(), GitHashAlgorithmKind.Sha1);
        WriteRawLooseFile([0x05, .. body], oid);

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await backend.ReadAsync(oid, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
    }

    [Fact]
    public async Task ReadHeader_PacklikeLooseObject_ReturnsHeader()
    {
        byte[] body = ZlibTestHelpers.CompressLooseObject("hello"u8.ToArray());
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, "hello"u8.ToArray(), GitHashAlgorithmKind.Sha1);
        WriteRawLooseFile([0x35, .. body], oid);

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        GitObjectHeader? header = await backend.ReadHeaderAsync(oid, TestContext.Current.CancellationToken);

        Assert.NotNull(header);
        Assert.Equal(GitObjectType.Blob, header!.Value.Type);
        Assert.Equal(5, header.Value.Size);
    }

    // ---------------------------------------------------------------
    // loose-object write compression level: C defaults to
    // Z_BEST_SPEED = 1 (odb_loose.c normalize_options).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Write_CompressionLevel_Fastest()
    {
        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        byte[] body = "hello world"u8.ToArray();
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);

        await ((IObjectWriteBackend)backend).WriteAsync(oid, GitObjectType.Blob, body, TestContext.Current.CancellationToken);

        string path = Path.Combine(_objectsDir, oid.ToPathString());
        byte[] onDisk = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        byte[] expected = ZlibTestHelpers.CompressLooseObject(OdbTestHelpers.BuildObjectBytes(GitObjectType.Blob, body, body.Length), CompressionLevel.Fastest);
        Assert.Equal(expected, onDisk);
    }

    // ---------------------------------------------------------------
    // loose-object file mode: C writes 0444 (GIT_OBJECT_FILE_MODE).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Write_FileMode_ReadOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // no Unix file modes on Windows
        }

        await using var backend = new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1);
        byte[] body = "mode test"u8.ToArray();
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);

        await ((IObjectWriteBackend)backend).WriteAsync(oid, GitObjectType.Blob, body, TestContext.Current.CancellationToken);

        string path = Path.Combine(_objectsDir, oid.ToPathString());
        UnixFileMode mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, mode);
    }

    // ---------------------------------------------------------------
    // git_object_lookup type mismatch: C returns GIT_ENOTFOUND
    // ("the requested type does not match the type in the ODB",
    // GIT_ERROR_INVALID — object.c:124-128).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Lookup_T_WrongType_ThrowsNotFound()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "hello"u8.ToArray());

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await db.LookupAsync<Commit>(oid, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal(GitErrorCategory.Invalid, ex.Category);
        Assert.Contains("does not match the type in the ODB", ex.Message);
    }

    // ---------------------------------------------------------------
    // write-stream overrun: C fails immediately at the offending Write
    // (git_odb_stream_write, odb.c:1754-1765, "cannot stream_write() -
    // Invalid length...", GIT_ERROR_ODB).
    // ---------------------------------------------------------------

    [Fact]
    public async Task WriteStream_Overrun_ThrowsAtWrite()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        await using ObjectWriteStream stream = await db.OpenWriteStreamAsync(GitObjectType.Blob, declaredSize: 5, TestContext.Current.CancellationToken);

        GitException ex = Assert.Throws<GitException>(() => stream.Write("abcdef"u8.ToArray()));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Odb, ex.Category);
        Assert.Contains("stream_write()", ex.Message);
    }

    [Fact]
    public async Task WriteStream_ExactSize_Ok()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        byte[] body = "hello"u8.ToArray();
        GitOid expected = GitObjectDb.HashObject(GitObjectType.Blob, body, GitHashAlgorithmKind.Sha1);

        await using ObjectWriteStream stream = await db.OpenWriteStreamAsync(GitObjectType.Blob, body.Length, TestContext.Current.CancellationToken);
        stream.Write(body);

        GitOid oid = await stream.FinalizeAsync(expected, TestContext.Current.CancellationToken);
        Assert.Equal(expected, oid);
        Assert.True(File.Exists(Path.Combine(_objectsDir, oid.ToPathString())));
    }

    // ---------------------------------------------------------------
    // git_odb_exists_prefix prefix-too-short: C GIT_EAMBIGUOUS
    // ("prefix length too short", odb.c:1117).
    // ---------------------------------------------------------------

    [Fact]
    public async Task ExistsPrefix_TooShort_ThrowsAmbiguous()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        var prefix = GitOid.Parse("abcdabcdabcdabcdabcdabcdabcdabcdabcdabcd".AsSpan(), GitHashAlgorithmKind.Sha1);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await db.ExistsPrefixAsync(prefix.WithHexLength(3), TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Ambiguous, ex.Code);
        Assert.Contains("prefix length too short", ex.Message);
    }

    // ---------------------------------------------------------------
    // git_odb_write freshen check: C consults BACKENDS only
    // (odb_freshen_1, odb.c:981-1009 — no cache, no hardcoded objects).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Write_ObjectOnlyInCache_StillWritesToBackend()
    {
        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        const string commitBody =
            "tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904\n" +
            "author A <a@b.c> 1700000000 +0000\n" +
            "committer A <a@b.c> 1700000000 +0000\n" +
            "\n" +
            "msg\n";
        byte[] body = Encoding.ASCII.GetBytes(commitBody);
        GitOid oid = GitObjectDb.HashObject(GitObjectType.Commit, body, GitHashAlgorithmKind.Sha1);

        // 1. Write → loose file exists.
        await db.WriteAsync(GitObjectType.Commit, body, TestContext.Current.CancellationToken);
        string path = Path.Combine(_objectsDir, oid.ToPathString());
        Assert.True(File.Exists(path));

        // 2. Lookup → parsed object enters the in-memory cache.
        Assert.NotNull(await db.LookupAsync(oid, TestContext.Current.CancellationToken));

        // 3. Delete the loose file. The object now exists ONLY in the cache.
        File.Delete(path);

        // 4. Write again. C's git_odb__freshen ignores the cache → the object
        // is re-written to the backend even though the cache holds it.
        await db.WriteAsync(GitObjectType.Commit, body, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(path), "freshen must consult backends only — the cached object must not suppress the write");
    }

    // ---------------------------------------------------------------
    // git_odb_foreach yields duplicates across backends in C
    // (odb.c:1600-1605 — no dedup).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Enumerate_DuplicateAcrossBackends_YieldsTwice()
    {
        GitOid oid = OdbTestHelpers.WriteLooseObject(_objectsDir, GitObjectType.Blob, "dup"u8.ToArray());

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        // Two loose backends over the SAME directory — both enumerate the oid.
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 2);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        var oids = new List<GitOid>();
        await foreach (GitOid found in db.EnumerateAsync(TestContext.Current.CancellationToken))
        {
            oids.Add(found);
        }

        Assert.Equal(2, oids.Count(o => o.Equals(oid)));
    }

    // ---------------------------------------------------------------
    // Commit objects with CRLF line endings fail to parse in C
    // ("failed to parse bad commit object") and must fail here too.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Commit_CRLFBody_ParseFails()
    {
        // C probe: git_odb_write of a CRLF commit body succeeds (raw write),
        // but git_object_lookup fails with "failed to parse bad commit object".
        const string crlfCommit =
            "tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904\r\n" +
            "author A <a@b.c> 1700000000 +0000\r\n" +
            "committer A <a@b.c> 1700000000 +0000\r\n" +
            "\r\n" +
            "msg\r\n";
        byte[] body = Encoding.ASCII.GetBytes(crlfCommit);

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitOid oid = await db.WriteAsync(GitObjectType.Commit, body, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await db.LookupAsync(oid, TestContext.Current.CancellationToken));

        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal(GitErrorCategory.Object, ex.Category);
    }

    [Fact]
    public async Task Commit_LFBody_Parses()
    {
        const string lfCommit =
            "tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904\n" +
            "author A <a@b.c> 1700000000 +0000\n" +
            "committer A <a@b.c> 1700000000 +0000\n" +
            "\n" +
            "msg\n";
        byte[] body = Encoding.ASCII.GetBytes(lfCommit);

        using var odbContext = new GitContext();
        await using var db = new GitObjectDb(odbContext);
        db.AddBackend(new LooseObjectBackend(_objectsDir, GitHashAlgorithmKind.Sha1), priority: 1);

        GitOid oid = await db.WriteAsync(GitObjectType.Commit, body, TestContext.Current.CancellationToken);

        GitObject? obj = await db.LookupAsync(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(obj);
        Assert.IsType<Commit>(obj);
    }

    [Fact]
    public void Parser_CRLF_AdvanceNewline_Rejected()
    {
        // C: git_parse_advance_nl requires the remaining line to be exactly
        // "\n" — a "\r\n" line leaves "\r\n" remaining (the '\r' is part of
        // the line content), so advance_nl fails.
        var parser = new GitObjectParser("abc\r\n"u8.ToArray());
        parser.AdvanceChars(3);

        Assert.False(parser.AdvanceNewline());
        Assert.Equal("\r", Encoding.ASCII.GetString(parser.Line));
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Writes a loose object file whose content is the raw (uncompressed)
    /// header+body, compressed with zlib at the default level. Returns the OID
    /// computed from the header+body bytes.
    /// </summary>
    private GitOid WriteRawLooseObject(byte[] headerAndBody)
    {
        byte[] compressed = ZlibTestHelpers.CompressLooseObject(headerAndBody);
        GitOid oid = HashRaw(headerAndBody);
        string path = Path.Combine(_objectsDir, oid.ToPathString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
#pragma warning disable CA1849
        File.WriteAllBytes(path, compressed);
#pragma warning restore CA1849
        return oid;
    }

    /// <summary>
    /// Writes the given bytes verbatim as a loose object file at the path for
    /// <paramref name="oid"/> (no header derivation — the caller supplies the
    /// exact file content).
    /// </summary>
    private GitOid WriteRawLooseFile(byte[] fileContent, GitOid? oid = null)
    {
        GitOid id = oid ?? HashRaw(fileContent);
        string path = Path.Combine(_objectsDir, id.ToPathString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
#pragma warning disable CA1849
        File.WriteAllBytes(path, fileContent);
#pragma warning restore CA1849
        return id;
    }

    private static GitOid HashRaw(byte[] headerAndBody)
    {
        // OID of a blob whose content is exactly headerAndBody: the hash is
        // over "blob <len>\0" + headerAndBody. Reconstruct by hashing the
        // header+body with the type from the header prefix.
        int space = Array.IndexOf(headerAndBody, (byte)' ');
        GitObjectType type = space switch
        {
            >= 0 when headerAndBody.AsSpan(0, space).StartsWith("commit"u8) => GitObjectType.Commit,
            >= 0 when headerAndBody.AsSpan(0, space).StartsWith("tree"u8) => GitObjectType.Tree,
            >= 0 when headerAndBody.AsSpan(0, space).StartsWith("blob"u8) => GitObjectType.Blob,
            >= 0 when headerAndBody.AsSpan(0, space).StartsWith("tag"u8) => GitObjectType.Tag,
            _ => GitObjectType.Blob,
        };

        return GitObjectDb.HashObject(type, headerAndBody.AsSpan((Array.IndexOf(headerAndBody, (byte)0) + 1)), GitHashAlgorithmKind.Sha1);
    }
}
