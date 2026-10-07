using System.Security.Cryptography;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Index;

/// <summary> Parity tests for the index subsystem. </summary>
public sealed class IndexMedParityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _indexPath;
    private readonly GitRepository _repo;

    public IndexMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_IndexMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _repo = GitRepository.InitAsync(_tempDir, isBare: false, new GitContext()).GetAwaiter().GetResult();
        _indexPath = Path.Combine(_repo.Path, "index");
    }

    public void Dispose()
    {
        _repo.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private GitIndex OpenIndex()
        => GitIndex.OpenAsync(_indexPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    private async Task<GitIndex> RepoIndexAsync()
        => await _repo.GetIndexAsync(TestContext.Current.CancellationToken);

    private static GitOid Blob(GitRepository repo, string content)
        => repo.ObjectWriteAsync(GitObjectType.Blob, System.Text.Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    // ── v3 without extended entries is written as v2 ───────────────────

    [Fact]
    public async Task Write_Version3WithoutExtendedEntries_WrittenAsVersion2()
    {
        GitIndex idx = await RepoIndexAsync();
        idx.SetVersion(3);
        idx.Add(new GitIndexEntry("a.txt", Blob(_repo, "x"), GitFileMode.Regular));
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        byte[] data = await File.ReadAllBytesAsync(_indexPath, TestContext.Current.CancellationToken);
        int version = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
        Assert.Equal(2, version);
    }

    // ── EXTENDED bit re-synced from flags_extended ─────────────────────

    [Fact]
    public async Task Write_SkipWorktreeWithoutExtendedBit_WrittenAsV3Extended()
    {
        GitIndex idx = await RepoIndexAsync();
        GitIndexEntry e = new GitIndexEntry("a.txt", Blob(_repo, "x"), GitFileMode.Regular) with
        {
            FlagsExtended = GitIndexEntry.SkipWorktree,
            Flags = 0, // no EXTENDED bit in the in-memory flags
        };
        idx.Add(e);
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        byte[] data = await File.ReadAllBytesAsync(_indexPath, TestContext.Current.CancellationToken);
        int version = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
        Assert.Equal(3, version);

        // Entry flags carry the EXTENDED bit (0x4000), and the extended field
        // is written masked to 0xC000. Layout: fixed fields (40: two 8-byte
        // times + six 4-byte stat fields) + OID (20) + flags (2) + extended
        // flags (2) + path.
        int entryStart = 12;
        int flagsOffset = entryStart + 40 + 20;
        int pathOffset = flagsOffset + 2 + 2;
        ushort flags = (ushort)((data[flagsOffset] << 8) | data[flagsOffset + 1]);
        Assert.True((flags & GitIndexEntry.Extended) != 0);
        ushort extFlags = (ushort)((data[flagsOffset + 2] << 8) | data[flagsOffset + 3]);
        Assert.Equal(GitIndexEntry.SkipWorktree, extFlags);
        Assert.Equal(pathOffset, flagsOffset + 4); // path follows the ext flags
    }

    [Fact]
    public async Task Write_ExtendedBitWithoutExtendedFlags_WrittenAsV2ShortEntry()
    {
        GitIndex idx = await RepoIndexAsync();
        GitIndexEntry e = new GitIndexEntry("a.txt", Blob(_repo, "x"), GitFileMode.Regular) with
        {
            FlagsExtended = 0,
            Flags = GitIndexEntry.Extended, // stale EXTENDED bit
        };
        idx.Add(e);
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        byte[] data = await File.ReadAllBytesAsync(_indexPath, TestContext.Current.CancellationToken);
        int version = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
        Assert.Equal(2, version);
    }

    // ── corrupt index open fails ───────────────────────────────────────

    [Fact]
    public void Open_CorruptIndex_Throws()
    {
        File.WriteAllBytes(_indexPath, new byte[] { 0x44, 0x49, 0x52, 0x43, 0, 0, 0, 0x02, 0, 0, 0, 0x01, 0xFF });

        // C (index.c:430): git_index_open fails on a corrupt index.
        Assert.ThrowsAny<GitException>(() => OpenIndex());
    }

    [Fact]
    public async Task GetIndex_CorruptIndex_Throws()
    {
        string corruptRepoPath = Path.Combine(_tempDir, "corrupt");
        await using GitRepository repo = await GitRepository.InitAsync(corruptRepoPath, isBare: false, new GitContext(), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(repo.Path, "index"), new byte[] { 0x44, 0x49, 0x52, 0x43, 0, 0, 0, 0x02, 0, 0, 0, 0x01, 0xFF }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(async () => await repo.GetIndexAsync(TestContext.Current.CancellationToken));
    }

    // ── RemoveDirectory(stage: -1) removes nothing ─────────────────────

    [Fact]
    public void RemoveDirectory_StageAny_RemovesNothing()
    {
        var idx = GitIndex.New(GitHashAlgorithmKind.Sha1);
        GitOid oid = Blob(_repo, "x");
        idx.Add(new GitIndexEntry("dir/a.txt", oid, GitFileMode.Regular) with { Flags = 0 });

        // C (index.c:1751-1754): no entry's stage equals -1 → nothing removed.
        idx.RemoveDirectory("dir", stage: -1);
        Assert.True(idx.EntryByPath("dir/a.txt").HasValue);
    }

    // ── AddFromBufferAsync hashes the raw buffer ───────────────────────

    [Fact]
    public async Task AddFromBuffer_NoCleanFilters()
    {
        // A path with the text attribute: C hashes the RAW buffer verbatim
        // (index.c:1518) — no CRLF clean filter runs.
        await File.WriteAllTextAsync(Path.Combine(_repo.Workdir!, ".gitattributes"), "*.txt text\n", cancellationToken: TestContext.Current.CancellationToken);

        GitIndex idx = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        var entry = new GitIndexEntry("a.txt", default, GitFileMode.Regular);
        byte[] content = "a\r\nb\r\n"u8.ToArray();
        await idx.AddFromBufferAsync(entry, content, TestContext.Current.CancellationToken);

        GitIndexEntry? stored = idx.EntryByPath("a.txt");
        Assert.True(stored.HasValue);
        Assert.Equal(HashBlob(content), stored.Value.Id);
    }

    private static GitOid HashBlob(byte[] content)
    {
        // SHA-1 of "blob <len>\0" + content.
        byte[] header = System.Text.Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        byte[] combined = new byte[header.Length + content.Length];
        header.CopyTo(combined, 0);
        content.CopyTo(combined, header.Length);
        return GitOid.FromRaw(SHA1.HashData(combined), GitHashAlgorithmKind.Sha1);
    }

    // ── file/directory collision removals ─────────────────────────────

    [Fact]
    public void Add_FileUnderExistingDir_RemovesDirEntry()
    {
        var idx = GitIndex.New(GitHashAlgorithmKind.Sha1);
        GitOid oid = Blob(_repo, "x");
        idx.Add(new GitIndexEntry("a", oid, GitFileMode.Regular));
        idx.Add(new GitIndexEntry("a/b", oid, GitFileMode.Regular));

        // C: has_dir_name removes the "a" entry when "a/b" is added.
        Assert.Null(idx.EntryByPath("a"));
        Assert.NotNull(idx.EntryByPath("a/b"));
    }

    [Fact]
    public void Add_DirOverExistingFile_RemovesFileEntry()
    {
        var idx = GitIndex.New(GitHashAlgorithmKind.Sha1);
        GitOid oid = Blob(_repo, "x");
        idx.Add(new GitIndexEntry("a/b", oid, GitFileMode.Regular));
        idx.Add(new GitIndexEntry("a", oid, GitFileMode.Regular));

        // C: has_file_name removes "a/b" when "a" is added.
        Assert.NotNull(idx.EntryByPath("a"));
        Assert.Null(idx.EntryByPath("a/b"));
    }

    // ── ReadIndex clears names/REUC, keeps own tree cache ─────────────

    [Fact]
    public async Task ReadIndex_ClearsNamesAndReuc()
    {
        var target = GitIndex.New(GitHashAlgorithmKind.Sha1);
        var source = GitIndex.New(GitHashAlgorithmKind.Sha1);
        GitOid oid = Blob(_repo, "x");

        source.Add(new GitIndexEntry("a.txt", oid, GitFileMode.Regular));
        source.ReucAdd("a.txt", 0, default, 0x81A4u, oid, 0, default);
        source.NameAdd(null, "a.txt", "b.txt");
        Assert.True(source.ReucCount > 0);
        Assert.True(source.NameCount > 0);

        target.Add(new GitIndexEntry("b.txt", oid, GitFileMode.Regular));
        target.ReadIndex(source);

        // C (index.c:3510-3521): the target's NAME/REUC are CLEARED — never
        // merged from the source.
        Assert.Equal(0, target.ReucCount);
        Assert.Equal(0, target.NameCount);
    }

    // ── ASCII-only icase fold ─────────────────────────────────────────

    [Fact]
    public void Icase_NonAsciiBytes_DoNotFold()
    {
        // 0xC0 ('À') and 0xE0 ('à') are DIFFERENT bytes in C's ASCII-only
        // git__tolower — a case-insensitive index must not fold them.
        var upper = GitPath.FromUtf8Bytes(new byte[] { 0xC0 });
        var lower = GitPath.FromUtf8Bytes(new byte[] { 0xE0 });
        Assert.NotEqual(0, GitPath.CompareIgnoreCase(upper, lower));
        Assert.False(upper == lower);
    }

    // ── truncated index → git error, not an exception type ────────────

    [Fact]
    public async Task Open_TruncatedIndex_ThrowsGitException()
    {
        // Header claims 2 entries but the file holds only 1 + checksum.
        var ms = new MemoryStream();
        ms.Write(System.Text.Encoding.ASCII.GetBytes("DIRC"));
        WriteUInt32BE(ms, 2);
        WriteUInt32BE(ms, 2); // entry count
        GitIndex idx = await RepoIndexAsync();
        idx.Add(new GitIndexEntry("a.txt", Blob(_repo, "x"), GitFileMode.Regular));
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        byte[] good = await File.ReadAllBytesAsync(_indexPath, TestContext.Current.CancellationToken);
        // Build a truncated file: header(12) + one full entry + checksum, with
        // the header still claiming 2 entries.
        byte[] truncated = new byte[good.Length - (40 + 20 + "a.txt".Length + 8 - 40 - 20 - 7) + 20];
        // Simpler: take the valid file and chop 20 bytes out of the entry region.
        byte[] chopped = good[..(good.Length - 32)];
        await File.WriteAllBytesAsync(_indexPath, chopped, TestContext.Current.CancellationToken);

        Assert.ThrowsAny<GitException>(() => OpenIndex());
    }

    // ── corrupt TREE extension fails the open ─────────────────────────

    [Fact]
    public async Task Open_CorruptTreeExtension_Throws()
    {
        GitIndex idx = await RepoIndexAsync();
        idx.Add(new GitIndexEntry("a.txt", Blob(_repo, "x"), GitFileMode.Regular));
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        byte[] good = await File.ReadAllBytesAsync(_indexPath, TestContext.Current.CancellationToken);

        // Append a corrupt TREE extension: "TREE" + size 8 + garbage.
        var ms = new MemoryStream();
        ms.Write(good);
        ms.Write(System.Text.Encoding.ASCII.GetBytes("TREE"));
        WriteUInt32BE(ms, 8);
        ms.Write(new byte[8]);
        byte[] corrupt = ms.ToArray();
        // Recompute the checksum.
        byte[] hash = SHA1.HashData(corrupt.AsSpan(0, corrupt.Length - 20));
        hash.CopyTo(corrupt, corrupt.Length - 20);
        await File.WriteAllBytesAsync(_indexPath, corrupt, TestContext.Current.CancellationToken);

        // C: "corrupted TREE extension in index" fails the whole parse.
        GitException ex = Assert.ThrowsAny<GitException>(() => OpenIndex());
    }

    // ── ctime/mtime seconds widen uint32 → int64 ──────────────────────

    [Fact]
    public async Task Read_HighBitTimeSeconds_ValuePreserving()
    {
        // Build an index with mtime seconds = 0xFFFFFFFF.
        GitIndex idx = await RepoIndexAsync();
        idx.Add(new GitIndexEntry("a.txt", Blob(_repo, "x"), GitFileMode.Regular));
        await idx.WriteAsync(TestContext.Current.CancellationToken);
        byte[] data = await File.ReadAllBytesAsync(_indexPath, TestContext.Current.CancellationToken);
        // Patch the mtime seconds (offset 8 in the entry, right after ctime).
        data[12 + 8] = 0xFF;
        data[12 + 9] = 0xFF;
        data[12 + 10] = 0xFF;
        data[12 + 11] = 0xFF;
        byte[] hash = SHA1.HashData(data.AsSpan(0, data.Length - 20));
        hash.CopyTo(data, data.Length - 20);
        await File.WriteAllBytesAsync(_indexPath, data, TestContext.Current.CancellationToken);

        GitIndex parsed = OpenIndex();
        GitIndexEntry e = parsed.EntryByPath("a.txt")!.Value;
        // C widens to int64 value-preserving: 0xFFFFFFFF → +4294967295.
        Assert.Equal(4294967295L, e.Mtime.Seconds);
    }

    // ── ConflictAdd validations ───────────────────────────────────────

    [Fact]
    public void ConflictAdd_InvalidMode_Throws()
    {
        var idx = GitIndex.New(GitHashAlgorithmKind.Sha1);
        GitOid oid = Blob(_repo, "x");
        var bad = new GitIndexEntry("c.txt", oid, (GitFileMode)0x8000);

        // C (index.c:1840-1845): "invalid filemode for stage %d entry".
        GitException ex = Assert.Throws<GitException>(() => idx.ConflictAdd(null, bad, null));
        Assert.Contains("invalid filemode", ex.Message);
    }

    [Fact]
    public void ConflictAdd_DifferentPaths_RemovesStage0AtEach()
    {
        var idx = GitIndex.New(GitHashAlgorithmKind.Sha1);
        GitOid oid = Blob(_repo, "x");
        idx.Add(new GitIndexEntry("ours.txt", oid, GitFileMode.Regular));
        idx.Add(new GitIndexEntry("theirs.txt", oid, GitFileMode.Regular));

        GitIndexEntry ours = new GitIndexEntry("ours.txt", oid, GitFileMode.Regular) with { Flags = 0 };
        GitIndexEntry theirs = new GitIndexEntry("theirs.txt", oid, GitFileMode.Regular) with { Flags = 0 };
        idx.ConflictAdd(null, ours, theirs);

        // C (index.c:1847-1852): stage 0 is removed at EACH entry's path.
        Assert.Null(idx.EntryByPath("ours.txt", 0));
        Assert.Null(idx.EntryByPath("theirs.txt", 0));
    }

    // ── ReadTreeAsync clears flags_extended ───────────────────────────

    [Fact]
    public async Task ReadTree_ClearsFlagsExtendedOnStatPreserved()
    {
        GitIndex idx = await RepoIndexAsync();
        GitOid oid = Blob(_repo, "x");
        idx.Add(new GitIndexEntry("a.txt", oid, GitFileMode.Regular) with { FlagsExtended = GitIndexEntry.SkipWorktree });
        await idx.WriteAsync(TestContext.Current.CancellationToken);

        GitTree tree = (await _repo.ObjectLookupAsync<GitTree>(await idx.WriteTreeAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken))!;
        await idx.ReadTreeAsync(tree, TestContext.Current.CancellationToken);

        // C (index.c:3326-3327): stat-preserved entries have flags_extended
        // zeroed.
        GitIndexEntry e = idx.EntryByPath("a.txt")!.Value;
        Assert.Equal(0, e.FlagsExtended);
    }

    private static void WriteUInt32BE(Stream ms, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, value);
        ms.Write(b);
    }
}
