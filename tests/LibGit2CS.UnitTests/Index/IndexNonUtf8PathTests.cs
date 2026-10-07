using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;

namespace LibGit2CS.UnitTests.Index;

/// <summary> Byte-faithful index path tests. Pins the behavior that the index stores, compares, and round-trips path bytes verbatim
/// (matching libgit2's <c>memcmp</c>/<c>strcmp</c> raw-byte path model), so non-UTF-8 paths survive a write/read cycle and the ASCII-only case fold (not .NET's
/// Unicode-aware <c>OrdinalIgnoreCase</c>) governs ignore-case lookups. </summary>
public sealed class IndexNonUtf8PathTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    public IndexNonUtf8PathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_IndexNonUtf8_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, new GitContext());
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // A byte sequence that is invalid UTF-8 (0xFF is never a valid leading
    // byte); UTF-8.GetString decodes each to U+FFFD, so it cannot round-trip
    // through a decoded string. Used as the proof path throughout.
    private static readonly byte[] s_nonUtf8 = [0xFF, 0xFE, 0x80];

    private ValueTask<GitIndex> NewIndexAsync()
        => _repo.GetIndexAsync(cancellationToken: TestContext.Current.CancellationToken);

    // ===== Non-UTF-8 lookup is byte-wise (in-memory) =====

    [Fact]
    public async Task EntryByPath_NonUtf8_LookupByRawBytes_FindsEntry()
    {
        GitIndex index = await NewIndexAsync();
        GitOid oid = await WriteBlobAsync("x\n");
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        index.Add(new GitIndexEntry { Path = nonUtf8, Id = oid, Mode = GitFileMode.Regular });

        // Looking up by the SAME raw bytes finds the entry.
        GitIndexEntry? found = index.EntryByPath(nonUtf8);
        Assert.NotNull(found);
        Assert.True(nonUtf8.Span.SequenceEqual(found!.Value.Path.Span));
    }

    [Fact]
    public async Task EntryByPath_NonUtf8_LookupByLossyString_DoesNotMatch()
    {
        GitIndex index = await NewIndexAsync();
        GitOid oid = await WriteBlobAsync("x\n");
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        index.Add(new GitIndexEntry { Path = nonUtf8, Id = oid, Mode = GitFileMode.Regular });

        // The lossy decoded string re-encodes to the U+FFFD bytes (EF BF BD),
        // NOT the original 0xFF 0xFE 0x80, so the lookup must miss. This is the
        // proof that a string model could not round-trip it.
        string lossy = nonUtf8.ToUtf8String();
        Assert.NotEqual(s_nonUtf8, Encoding.UTF8.GetBytes(lossy));
        Assert.Null(index.EntryByPath(lossy));
    }

    // ===== v2/v3 disk round-trip preserves non-UTF-8 bytes =====

    [Fact]
    public async Task NonUtf8Path_RoundTrips_V2()
    {
        GitIndex index = await NewIndexAsync();
        GitOid oid = await WriteBlobAsync("body\n");
        index.Add(new GitIndexEntry { Path = GitPath.FromUtf8Bytes(s_nonUtf8), Id = oid, Mode = GitFileMode.Regular });
        index.SetVersion(2);
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitIndex reloaded = await ReloadAsync();
        Assert.Equal(1, reloaded.EntryCount);
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(reloaded.EntryByIndex(0).Path.Span));
        Assert.NotNull(reloaded.EntryByPath(GitPath.FromUtf8Bytes(s_nonUtf8)));
    }

    // ===== v4 disk round-trip exercises prefix-compression byte assembly
    // with a shared NON-ASCII prefix (string-based code rebuilt the path
    // via UTF-8 string concat, corrupting non-UTF-8 shared prefixes). =====

    [Fact]
    public async Task NonUtf8Path_RoundTrips_V4_WithNonAsciiSharedPrefix()
    {
        // Two paths sharing a 4-byte prefix that contains the invalid UTF-8
        // sequence 0xFF 0xFE 0x80 then '/'. v4 strips 1 trailing byte and
        // rebuilds last[0..3] + suffix in the byte domain.
        byte[] p1 = [0xFF, 0xFE, 0x80, (byte)'/', (byte)'a'];
        byte[] p2 = [0xFF, 0xFE, 0x80, (byte)'/', (byte)'b'];

        GitIndex index = await NewIndexAsync();
        GitOid oid = await WriteBlobAsync("body\n");
        index.Add(new GitIndexEntry { Path = GitPath.FromUtf8Bytes(p1), Id = oid, Mode = GitFileMode.Regular });
        index.Add(new GitIndexEntry { Path = GitPath.FromUtf8Bytes(p2), Id = oid, Mode = GitFileMode.Regular });
        index.SetVersion(4);
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitIndex reloaded = await ReloadAsync();
        Assert.Equal(2, reloaded.EntryCount);
        Assert.True(p1.AsSpan().SequenceEqual(reloaded.EntryByIndex(0).Path.Span));
        Assert.True(p2.AsSpan().SequenceEqual(reloaded.EntryByIndex(1).Path.Span));
    }

    // ===== Ignore case: ASCII-only fold (diverges from OrdinalIgnoreCase) =====

    [Fact]
    public async Task IgnoreCase_AsciiLetters_Fold()
    {
        GitIndex index = await NewIndexAsync();
        GitOid oid = await WriteBlobAsync("x\n");
        index.Add(new GitIndexEntry("File.TXT", oid, GitFileMode.Regular));
        index.IgnoreCase = true;

        // ASCII A-Z -> a-z fold matches (same as git__strcasecmp).
        Assert.NotNull(index.EntryByPath("file.txt"));
        Assert.NotNull(index.EntryByPath("FILE.txt"));
    }

    [Fact]
    public async Task IgnoreCase_NonAsciiBytes_DoNotFold()
    {
        // "ä" = C3 A4, "Ä" = C3 84. .NET's OrdinalIgnoreCase folds these
        // (Unicode), but git's git__tolower only folds ASCII A-Z; the non-ASCII
        // bytes pass through unchanged, so the two paths are DISTINCT under an
        // ignore-case index. This pins the parity behavior.
        var lower = GitPath.FromUtf8Bytes((byte[])[0xC3, 0xA4, (byte)'.', (byte)'t', (byte)'x', (byte)'t']);
        var upper = GitPath.FromUtf8Bytes((byte[])[0xC3, 0x84, (byte)'.', (byte)'t', (byte)'x', (byte)'t']);

        GitIndex index = await NewIndexAsync();
        GitOid oid = await WriteBlobAsync("x\n");
        index.Add(new GitIndexEntry { Path = lower, Id = oid, Mode = GitFileMode.Regular });
        index.IgnoreCase = true;

        // The ASCII-fold lookup by the upper-case non-ASCII variant MUST miss
        // (it would have matched under the old OrdinalIgnoreCase model).
        Assert.Null(index.EntryByPath(upper));
        Assert.NotNull(index.EntryByPath(lower));
    }

    // ===== FindPrefix confirm is case-sensitive git__prefixcmp (faithful) =====

    [Fact]
    public async Task FindPrefix_NonUtf8_Prefix_RoundTrips_V2()
    {
        GitIndex index = await NewIndexAsync();
        GitOid oid = await WriteBlobAsync("body\n");
        byte[] full = [0xFF, 0xFE, 0x80, (byte)'/', (byte)'x'];
        index.Add(new GitIndexEntry { Path = GitPath.FromUtf8Bytes(full), Id = oid, Mode = GitFileMode.Regular });
        index.SetVersion(2);
        await index.WriteAsync(cancellationToken: TestContext.Current.CancellationToken);

        GitIndex reloaded = await ReloadAsync();
        var prefix = GitPath.FromUtf8Bytes((byte[])[0xFF, 0xFE, 0x80, (byte)'/']);
        Assert.True(reloaded.FindPrefix(prefix) >= 0);
    }

    // ===== helpers =====

    private async ValueTask<GitOid> WriteBlobAsync(string content)
        => await _repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);

    private async ValueTask<GitIndex> ReloadAsync()
    {
        string indexPath = Path.Combine(_repo.Path, "index");
        return await GitIndex.OpenAsync(indexPath, GitHashAlgorithmKind.Sha1, TestContext.Current.CancellationToken);
    }
}
