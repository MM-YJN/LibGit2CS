using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary> The diff printers emit paths as raw bytes (no UTF-8 decode-then-re-encode round-trip), so non-ASCII paths in
/// NAME_ONLY / NAME_STATUS / RAW / patch-header output are byte-exact vs libgit2 (<c>diff_print.c</c> prints <c>delta-&gt;new_file.path</c> raw bytes). All
/// expectations are byte-level via <see cref="DiffGoldenBase.PrintToBytesAsync"/> — no string decode anywhere. </summary>
public sealed class DiffPrintNonAsciiParityTests : DiffGoldenBase, IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;

    // A byte sequence that is invalid UTF-8 (0xFF is never a valid leading byte); UTF-8.GetString decodes each to U+FFFD, so it cannot round-trip through a
    // decoded string. A decode-then-emit printer would corrupt these bytes.
    private static readonly byte[] s_nonUtf8 = [0xFF, 0xFE, 0x80];

    // "café.txt" as UTF-8 bytes: 63 61 66 C3 A9 2E 74 78 74.
    private static readonly byte[] s_cafeUtf8 = "café.txt"u8.ToArray();

    public DiffPrintNonAsciiParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffPrintNonAscii_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, Context);
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

    // ===== NAME_ONLY =====

    [Fact]
    public async Task NameOnly_Utf8Path_BytesPassThrough()
    {
        GitOid blobOid = await WriteBlobAsync("content\n");
        GitTree treeA = await BuildTreeAsync();
        GitTree treeB = await BuildTreeAsync((GitPath.FromUtf8Bytes(s_cafeUtf8), blobOid));

        using GitDiff diff = await _repo.DiffTreeToTreeAsync(treeA, treeB, cancellationToken: TestContext.Current.CancellationToken);
        byte[] output = await PrintToBytesAsync(diff, GitDiffPrintFormat.NameOnly, TestContext.Current.CancellationToken);

        // "café.txt\n" — the UTF-8 bytes pass through verbatim (no U+FFFD,
        // no UTF-8-decode collapse to 0xE9).
        byte[] expected = [.. s_cafeUtf8, (byte)'\n'];
        Assert.Equal(expected, output);
    }

    [Fact]
    public async Task NameOnly_NonUtf8Path_BytesPassThrough()
    {
        GitOid blobOid = await WriteBlobAsync("content\n");
        GitTree treeA = await BuildTreeAsync();
        GitTree treeB = await BuildTreeAsync((GitPath.FromUtf8Bytes(s_nonUtf8), blobOid));

        using GitDiff diff = await _repo.DiffTreeToTreeAsync(treeA, treeB, cancellationToken: TestContext.Current.CancellationToken);
        byte[] output = await PrintToBytesAsync(diff, GitDiffPrintFormat.NameOnly, TestContext.Current.CancellationToken);

        // Raw FF FE 80 0A on the wire — a lossy string path would emit U+FFFD
        // replacement bytes (EF BF BD) here.
        byte[] expected = [0xFF, 0xFE, 0x80, (byte)'\n'];
        Assert.Equal(expected, output);
    }

    // ===== NAME_STATUS =====

    [Fact]
    public async Task NameStatus_Utf8Path_StatusAndPathBytes()
    {
        GitOid blobOid = await WriteBlobAsync("content\n");
        GitTree treeA = await BuildTreeAsync();
        GitTree treeB = await BuildTreeAsync((GitPath.FromUtf8Bytes(s_cafeUtf8), blobOid));

        using GitDiff diff = await _repo.DiffTreeToTreeAsync(treeA, treeB, cancellationToken: TestContext.Current.CancellationToken);
        byte[] output = await PrintToBytesAsync(diff, GitDiffPrintFormat.NameStatus, TestContext.Current.CancellationToken);

        // "A\tcafé.txt\n" — status char and tab are ASCII, path is raw bytes.
        byte[] expected = [(byte)'A', (byte)'\t', .. s_cafeUtf8, (byte)'\n'];
        Assert.Equal(expected, output);
    }

    [Fact]
    public async Task NameStatus_NonUtf8Rename_BothPathsPrinted()
    {
        GitOid blobOid = await WriteBlobAsync("rename me\n");
        var src = GitPath.FromUtf8Bytes(s_nonUtf8);
        // Distinct bytes that share the same lossy UTF-8 decode (three U+FFFD) —
        // a string compare would collapse them to one path.
        var dst = GitPath.FromUtf8Bytes((byte[])[0xFF, 0xFE, 0x81]);

        GitTree treeA = await BuildTreeAsync((src, blobOid));
        GitTree treeB = await BuildTreeAsync((dst, blobOid));

        using GitDiff diff = await _repo.DiffTreeToTreeAsync(treeA, treeB, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions { Flags = GitDiffFindFlags.Renames }, cancellationToken: TestContext.Current.CancellationToken);

        byte[] output = await PrintToBytesAsync(diff, GitDiffPrintFormat.NameStatus, TestContext.Current.CancellationToken);

        // "R\t<old>%c <new>%c\n" (diff_print.c:194-199) — both raw byte
        // sequences with their mode suffixes (' ' for regular files), so the
        // separator space plus the two suffix spaces give the double space.
        byte[] expected = [(byte)'R', (byte)'\t',
            0xFF, 0xFE, 0x80, (byte)' ', (byte)' ', 0xFF, 0xFE, 0x81, (byte)' ', (byte)'\n'];
        Assert.Equal(expected, output);
    }

    // ===== RAW =====

    [Fact]
    public async Task Raw_NonUtf8Rename_BothPathsPrinted()
    {
        GitOid blobOid = await WriteBlobAsync("rename me\n");
        var src = GitPath.FromUtf8Bytes(s_nonUtf8);
        var dst = GitPath.FromUtf8Bytes((byte[])[0xFF, 0xFE, 0x81]);

        GitTree treeA = await BuildTreeAsync((src, blobOid));
        GitTree treeB = await BuildTreeAsync((dst, blobOid));

        using GitDiff diff = await _repo.DiffTreeToTreeAsync(treeA, treeB, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions { Flags = GitDiffFindFlags.Renames }, cancellationToken: TestContext.Current.CancellationToken);

        byte[] output = await PrintToBytesAsync(diff, GitDiffPrintFormat.Raw, TestContext.Current.CancellationToken);

        // ":<omode> <nmode> <oid>... <oid>... R100\t<old> <new>\n" — the two
        // raw path byte sequences appear verbatim after the tab.
        string text = Encoding.ASCII.GetString(output);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string rawLine = Assert.Single(lines);
        string[] tabFields = rawLine.Split('\t');
        Assert.True(tabFields.Length == 2, $"raw rename line should have 2 tab-fields; got {tabFields.Length}: '{rawLine}'");

        byte[] pathField = output.AsSpan(text.IndexOf('\t') + 1, output.Length - text.IndexOf('\t') - 2).ToArray();
        byte[] expectedPaths = [0xFF, 0xFE, 0x80, (byte)' ', 0xFF, 0xFE, 0x81];
        Assert.Equal(expectedPaths, pathField);
    }

    // ===== PATCH (header quoting) =====

    [Fact]
    public async Task Patch_Utf8Path_HeaderQuotedOctal()
    {
        GitOid blobOid = await WriteBlobAsync("one\n");
        var cafe = GitPath.FromUtf8Bytes(s_cafeUtf8);

        GitTree treeA = await BuildTreeAsync((cafe, blobOid));
        GitOid blobOid2 = await WriteBlobAsync("two\n");
        GitTree treeB = await BuildTreeAsync((cafe, blobOid2));

        using GitDiff diff = await _repo.DiffTreeToTreeAsync(treeA, treeB, cancellationToken: TestContext.Current.CancellationToken);
        byte[] output = await PrintToBytesAsync(diff, GitDiffPrintFormat.Patch, TestContext.Current.CancellationToken);

        // git_str_quote octal-escapes every byte > 0x7E: C3 → \303, A9 → \251,
        // and wraps the whole prefixed path in double quotes. The header lines
        // must contain the escaped form, not raw bytes.
        string text = Encoding.ASCII.GetString(output);
        Assert.Contains("diff --git \"a/caf\\303\\251.txt\" \"b/caf\\303\\251.txt\"", text);
        Assert.Contains("--- \"a/caf\\303\\251.txt\"", text);
        Assert.Contains("+++ \"b/caf\\303\\251.txt\"", text);
    }

    // ===== Prefix validation =====

    [Fact]
    public async Task Patch_NonAsciiPrefix_Throws()
    {
        GitOid blobOid = await WriteBlobAsync("one\n");
        var cafe = GitPath.FromUtf8Bytes(s_cafeUtf8);

        GitTree treeA = await BuildTreeAsync((cafe, blobOid));
        GitOid blobOid2 = await WriteBlobAsync("two\n");
        GitTree treeB = await BuildTreeAsync((cafe, blobOid2));

        var options = new GitDiffOptions { OldPrefix = "café/" };
        using GitDiff diff = await _repo.DiffTreeToTreeAsync(treeA, treeB, options, TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await PrintToBytesAsync(diff, GitDiffPrintFormat.Patch, TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCategory.Config, ex.Category);
        Assert.Contains("must be ASCII", ex.Message);
    }

    // ===== helpers =====

    private async ValueTask<GitOid> WriteBlobAsync(string content)
        => await _repo.ObjectWriteAsync(GitObjectType.Blob, Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);

    private async ValueTask<GitTree> BuildTreeAsync(params (GitPath Name, GitOid Oid)[] entries)
    {
        GitTreeBuilder builder = _repo.NewTreeBuilder();
        foreach ((GitPath name, GitOid oid) in entries)
        {
            await builder.InsertAsync(name, oid, GitFileMode.Regular, TestContext.Current.CancellationToken);
        }

        GitOid treeOid = await builder.WriteAsync(TestContext.Current.CancellationToken);
        return (await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;
    }
}
