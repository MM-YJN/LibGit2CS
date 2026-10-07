using System.Buffers.Binary;
using System.Security.Cryptography;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.Objects;
using LibGit2CS.Repository;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Index;

// Parity tests for raw mode on read, the v4 path length check, REUC read strictness, flags namemask verbatim + adjust, checksum
// verified after parsing, the NAME sorted getter, and the extended-flags write mask. Expectations C-verified (index.c:918-923, 2351-2384, 2564, 2638-2643,
// 2954, 2970-2971, 2833-2838, 2150-2151).
public sealed class IndexLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public IndexLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_IndexLow_" + Guid.NewGuid().ToString("N")[..8]);
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

    private string NewIndexPath() => Path.Combine(_tempDir, $"index_{Guid.NewGuid().ToString("N")[..8]}");

    // ── raw 32-bit mode preserved on read ─────────────────────────────

    [Fact]
    public async Task Read_NonCanonicalMode_IsPreservedRaw()
    {
        // C (index.c:2564): entry.mode = ntohl(...) — no normalization on
        // read. 0100646 (0x81A6) round-trips byte-identically.
        uint rawMode = 0x81A6u;
        string path = NewIndexPath();
        byte[] body = BuildIndex(
            version: 2,
            [BuildEntry(path: "a", mode: rawMode, oid: s_zeroOid, flags: 1)],
            extensions: []);
        await File.WriteAllBytesAsync(path, body, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        GitIndexEntry entry = index.EntryByIndex(0);
        Assert.Equal((GitFileMode)0x81A6, entry.Mode);

        // Rewrite: the raw mode must be written back unchanged (serialize
        // the in-memory index directly into a writer).
        using var writer = new PooledByteBufferWriter();
        index.SerializeForWrite(writer);
        int modeOffset = 12 + 16 + 4 + 4; // header(12) + ctime(8) + mtime(8) + dev(4) + ino(4)
        Assert.Equal(rawMode, BinaryPrimitives.ReadUInt32BigEndian(writer.WrittenSpan.Slice(modeOffset, 4)));
    }

    // ── v4 reconstructed path length check ────────────────────────────

    [Fact]
    public async Task Read_V4LongPath_ThrowsUnreasonablePathLength()
    {
        // C (index.c:2638-2643): prefix + suffix + NUL > GIT_PATH_MAX (4096)
        // → "unreasonable path length".
        string longPath = new('x', 4097);
        string path = NewIndexPath();
        byte[] body = BuildIndex(
            version: 4,
            [BuildEntry(path: longPath, mode: 0x81A4u, oid: s_zeroOid, flags: 0, v4: true)],
            extensions: []);
        await File.WriteAllBytesAsync(path, body, cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => GitIndex.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Contains("unreasonable path length", ex.Message);
    }

    // ── REUC read strictness ──────────────────────────────────────────

    [Fact]
    public async Task Read_ReucEmptyMode_Throws()
    {
        // C (index.c:2354-2359): an empty mode field (endptr == buffer)
        // fails "reading reuc entry stage".
        string path = NewIndexPath();
        byte[] reuc = "x\0"u8.ToArray()          // path
            .Concat("\0"u8.ToArray())            // empty mode
            .ToArray();
        byte[] body = BuildIndex(version: 2, [], [("REUC", reuc)]);
        await File.WriteAllBytesAsync(path, body, cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => GitIndex.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Contains("reading reuc entry stage", ex.Message);
    }

    [Fact]
    public async Task Read_ReucTruncatedOid_Throws()
    {
        // C (index.c:2369-2375): fewer than oid_size bytes left for an OID
        // fails "reading reuc entry oid" (was an ArgumentOutOfRangeException).
        string path = NewIndexPath();
        byte[] reuc = "x\0"u8.ToArray()
            .Concat("100644\0"u8.ToArray())
            .Concat("0\0"u8.ToArray())
            .Concat("0\0"u8.ToArray())
            .Concat(new byte[10]) // only 10 of 20 OID bytes
            .ToArray();
        byte[] body = BuildIndex(version: 2, [], [("REUC", reuc)]);
        await File.WriteAllBytesAsync(path, body, cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => GitIndex.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Contains("reading reuc entry oid", ex.Message);
    }

    [Fact]
    public async Task Read_ReucLeadingWhitespaceMode_Parses()
    {
        // C's git__strntol64 (base 8) skips leading whitespace, so
        // " 100644\0" parses (index.c:2354).
        string path = NewIndexPath();
        byte[] reuc = "x\0"u8.ToArray()
            .Concat(" 100644\0"u8.ToArray())
            .Concat("0\0"u8.ToArray())
            .Concat("0\0"u8.ToArray())
            .Concat(s_zeroOid.RawBytes.ToArray())
            .ToArray();
        byte[] body = BuildIndex(version: 2, [], [("REUC", reuc)]);
        await File.WriteAllBytesAsync(path, body, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Single(index.ReucEntries);
        Assert.Equal((uint)GitFileMode.Regular, index.ReucEntries[0].Modes[0]);
    }

    // ── flags namemask ────────────────────────────────────────────────

    [Fact]
    public async Task Add_SetsNameMask_AndWriteIsVerbatim()
    {
        // C (index.c:1377-1379): git_index_add adjusts the namemask to the
        // path length; write_disk_entry writes entry->flags verbatim
        // (index.c:2954).
        string repoDir = Path.Combine(_tempDir, "repo_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoDir);
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Workdir!, "file.txt"), "content\n", cancellationToken: TestContext.Current.CancellationToken);

        GitIndex index = await repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync("file.txt", TestContext.Current.CancellationToken);
        await index.WriteAsync(TestContext.Current.CancellationToken);

        using GitIndex reopened = await GitIndex.OpenAsync(Path.Combine(repoDir, ".git", "index"), GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        GitIndexEntry entry = reopened.EntryByIndex(0);
        // namemask = 8 ("file.txt") — stage 0.
        Assert.Equal(8, entry.Flags & GitIndexEntry.NameMask);
    }

    [Fact]
    public async Task Write_PreservesOnDiskNameMask()
    {
        // A v4 file whose stored namemask differs from the real path length
        // (the v4 path is carried by the varint, so namemask 0 still parses)
        // is re-written verbatim in C (index.c:2954)
        string path = NewIndexPath();
        byte[] body = BuildIndex(
            version: 4,
            [BuildEntry(path: "abcdefgh", mode: 0x81A4u, oid: s_zeroOid, flags: 0, v4: true)],
            extensions: []);
        await File.WriteAllBytesAsync(path, body, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal("abcdefgh", index.EntryByIndex(0).Path.ToUtf8String());
        Assert.Equal(0, index.EntryByIndex(0).Flags & GitIndexEntry.NameMask);

        using var writer = new PooledByteBufferWriter();
        index.SerializeForWrite(writer); // serialize the in-memory index directly
        int flagsOffset = 12 + 40 + 20; // header(12) + 40 fixed + oid(20)
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(writer.WrittenSpan.Slice(flagsOffset, 2)) & GitIndexEntry.NameMask);
    }

    // ── checksum verified after parsing ───────────────────────────────

    [Fact]
    public async Task Read_DoubleCorrupt_ReportsStructuralErrorFirst()
    {
        // C (index.c:2833-2838): the checksum is compared only after the
        // header/entries/extensions parse, so a double-corrupt file reports
        // "incorrect header signature", not the checksum error.
        string path = NewIndexPath();
        byte[] body = BuildIndex(version: 2, [BuildEntry(path: "a", mode: 0x81A4u, oid: s_zeroOid, flags: 1)], extensions: []);
        body[0] = (byte)'X'; // corrupt the DIRC signature
        body[^1] ^= 0xFF;    // corrupt the checksum too
        await File.WriteAllBytesAsync(path, body, cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(
            () => GitIndex.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken));
        Assert.Contains("incorrect header signature", ex.Message);
    }

    // ── NAME getter sorts ─────────────────────────────────────────────

    [Fact]
    public async Task NameEntries_AreSorted()
    {
        // C (index.c:2150-2151): git_index_name_get_byindex sorts the names
        // vector (conflict_name_cmp) before indexing — ancestor-first, then
        // strcmp on ancestors.
        string path = NewIndexPath();
        byte[] names = "b\0\0\0"u8.ToArray()   // ancestor "b"
            .Concat("a\0\0\0"u8.ToArray())     // ancestor "a"
            .ToArray();
        byte[] body = BuildIndex(version: 2, [], [("NAME", names)]);
        await File.WriteAllBytesAsync(path, body, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal(2, index.NameEntries.Count);
        Assert.Equal("a", index.NameEntries[0].Ancestor!.Value.ToUtf8String());
        Assert.Equal("b", index.NameEntries[1].Ancestor!.Value.ToUtf8String());
    }

    // ── extended-flags write mask ─────────────────────────────────────

    [Fact]
    public async Task Write_MasksExtendedFlags_ToExtendedFlagsMask()
    {
        // C (index.c:2969-2971): only flags_extended &
        // GIT_INDEX_ENTRY_EXTENDED_FLAGS (INTENT_TO_ADD|SKIP_WORKTREE =
        // 0x6000, index.h:133-136) is written; in-memory-only bits like
        // UPTODATE (0x0004) are stripped.
        string path = NewIndexPath();
        byte[] body = BuildIndex(
            version: 3,
            [BuildEntry(path: "a", mode: 0x81A4u, oid: s_zeroOid, flags: 0x4001, flagsExtended: 0x6004)],
            extensions: []);
        await File.WriteAllBytesAsync(path, body, cancellationToken: TestContext.Current.CancellationToken);

        using GitIndex index = await GitIndex.OpenAsync(path, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
        Assert.Equal((ushort)0x6004, index.EntryByIndex(0).FlagsExtended);

        using var writer = new PooledByteBufferWriter();
        index.SerializeForWrite(writer); // serialize the in-memory index directly
        int extFlagsOffset = 12 + 40 + 20 + 2; // header + 40 fixed + oid + flags
        Assert.Equal((ushort)0x6000, BinaryPrimitives.ReadUInt16BigEndian(writer.WrittenSpan.Slice(extFlagsOffset, 2)));
    }

    // ── byte-crafting helpers ──────────────────────────────────────────────

    private static readonly GitOid s_zeroOid = GitOid.Parse(
        "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391".AsSpan(), GitHashAlgorithmKind.Sha1);

    private static byte[] BuildIndex(int version, List<byte[]> entries, List<(string Sig, byte[] Data)> extensions)
    {
        using var ms = new MemoryStream();
        WriteBE32(ms, 0x44495243u); // "DIRC"
        WriteBE32(ms, (uint)version);
        WriteBE32(ms, (uint)entries.Count);
        foreach (byte[] entry in entries)
        {
            ms.Write(entry);
        }

        foreach ((string sig, byte[] data) in extensions)
        {
            ms.Write(System.Text.Encoding.ASCII.GetBytes(sig));
            WriteBE32(ms, (uint)data.Length);
            ms.Write(data);
        }

        byte[] body = ms.ToArray();
        byte[] checksum = SHA1.HashData(body);
        return [.. body, .. checksum];
    }

    private static byte[] BuildEntry(string path, uint mode, GitOid oid, ushort flags, ushort flagsExtended = 0, bool v4 = false)
    {
        using var ms = new MemoryStream();
        WriteBE32(ms, 0); // ctime sec
        WriteBE32(ms, 0); // ctime ns
        WriteBE32(ms, 0); // mtime sec
        WriteBE32(ms, 0); // mtime ns
        WriteBE32(ms, 0); // dev
        WriteBE32(ms, 0); // ino
        WriteBE32(ms, mode);
        WriteBE32(ms, 0); // uid
        WriteBE32(ms, 0); // gid
        WriteBE32(ms, 0); // size
        ms.Write(oid.RawBytes);
        WriteBE16(ms, flags);
        if ((flags & 0x4000) != 0)
        {
            WriteBE16(ms, flagsExtended);
        }

        byte[] pathBytes = System.Text.Encoding.ASCII.GetBytes(path);
        if (v4)
        {
            ms.WriteByte(0); // stripLen varint 0 (no prefix)
            ms.Write(pathBytes);
            ms.WriteByte(0); // NUL
        }
        else
        {
            ms.Write(pathBytes);
            int fixedSize = 62 + ((flags & 0x4000) != 0 ? 2 : 0);
            int entrySize = (fixedSize + pathBytes.Length + 8) & ~7;
            while (ms.Length < entrySize)
            {
                ms.WriteByte(0);
            }
        }

        return ms.ToArray();
    }

    private static void WriteBE32(MemoryStream ms, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteBE16(MemoryStream ms, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        ms.Write(buf);
    }
}
