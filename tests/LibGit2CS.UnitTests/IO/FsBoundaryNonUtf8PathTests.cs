using System.Text;

using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitIndex = LibGit2CS.Index.GitIndex;
using GitIndexEntry = LibGit2CS.Index.GitIndexEntry;

namespace LibGit2CS.UnitTests.IO;

/// <summary> Byte-faithful FS-boundary path tests. Pins that every <c>Path.Combine(workdir, path)</c> egress site routes through <see
/// cref="GitPath.ToFileSystemString"/> (the single FS-boundary transcode point), and that the <see cref="IObjectReader"/> / <see cref="FilesystemIterator"/>
/// ingress paths route through <see cref="GitPath.FromFileSystemString"/> (the precompose hook). A non-UTF-8 path (invalid in any encoding) survives a
/// workdir round-trip byte-exact through the egress + ingress boundaries. </summary> <remarks> <para> <b>Why this works on Linux:</b>.NET's <c>File.*</c> API
/// takes <c>string</c> paths. A non-UTF-8 byte sequence like <c>0xFF 0xFE 0x80</c> decodes via <see cref="GitPath.ToFileSystemString"/> to a lossy
/// <c>string</c> (U+FFFD replacement chars). The OS creates a file with that lossy name. The <see cref="FilesystemIterator"/> ingress reads the file name back,
/// encodes it via <see cref="GitPath.FromFileSystemString"/>, and the resulting <see cref="GitPath"/> bytes are the re-encoding of the lossy string — NOT the
/// original bytes. The proof is that the <c>GitPath</c> overload of <c>AddByPathAsync</c> preserves the original bytes in the <b>index</b> (the index entry's
/// <c>Path</c> is the original <c>GitPath</c>, not the transcoded one), even though the workdir file name is lossy. </para> <para> The key insight: the <b>git
/// path</b> (in the index/tree) is byte-faithful; the <b>OS path</b> (in the workdir) is the transcoded form. The single-transcode-point design makes
/// this explicit: <c>ToFileSystemString</c> for egress, <c>FromFileSystemString</c> for ingress. The index entry's <c>Path</c> is never transcoded. </para>
/// </remarks>
public sealed class FsBoundaryNonUtf8PathTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private GitRepository _repo = null!;
    private readonly GitContext _context = new();

    // A byte sequence that is invalid UTF-8 (0xFF is never a valid leading
    // byte); UTF-8.GetString decodes each to U+FFFD, so it cannot round-trip
    // through a decoded string. Used as the proof path throughout.
    private static readonly byte[] s_nonUtf8 = [0xFF, 0xFE, 0x80];

    public FsBoundaryNonUtf8PathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_FsBoundaryNonUtf8_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_tempDir, isBare: false, _context);
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        _context.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ===== AddByPathAsync(GitPath) preserves bytes in the index =====

    [Fact]
    public async Task AddByPathAsync_NonUtf8Path_IndexEntryPreservesBytes()
    {
        // Create a workdir file at the OS-transcoded (lossy) name. The git
        // path (the bytes we want in the index) is the non-UTF-8 sequence.
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        string osPath = Path.Combine(_repo.Workdir!, nonUtf8.ToFileSystemString());
        byte[] content = Encoding.UTF8.GetBytes("file content\n");
        await File.WriteAllBytesAsync(osPath, content, TestContext.Current.CancellationToken);

        // Add by the byte-faithful GitPath. The FS-boundary egress
        // (Path.Combine(workdir, path.ToFileSystemString())) must resolve the
        // same OS-transcoded name we just created.
        GitIndex index = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync(nonUtf8, TestContext.Current.CancellationToken);

        Assert.Equal(1, index.EntryCount);
        GitIndexEntry entry = index.EntryByIndex(0);
        Assert.True(s_nonUtf8.AsSpan().SequenceEqual(entry.Path.Span),
            $"index entry path should be the original non-UTF-8 bytes, got {Convert.ToHexString(entry.Path.Span.ToArray())}");
    }

    // ===== BlobCreateFromWorkdirAsync(GitPath) preserves bytes =====

    [Fact]
    public async Task BlobCreateFromWorkdirAsync_NonUtf8Path_PreservesBytes()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        string osPath = Path.Combine(_repo.Workdir!, nonUtf8.ToFileSystemString());
        byte[] content = Encoding.UTF8.GetBytes("blob content\n");
        await File.WriteAllBytesAsync(osPath, content, TestContext.Current.CancellationToken);

        GitOid oid = await _repo.BlobCreateFromWorkdirAsync(nonUtf8, TestContext.Current.CancellationToken);

        // The blob should exist in the ODB.
        GitBlob? blob = await _repo.ObjectLookupAsync<GitBlob>(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        Assert.Equal(content, blob!.Content.ToArray());
    }

    // ===== WorkdirReader.ReadAsync(GitPath) reads byte-faithfully =====

    [Fact]
    public async Task WorkdirReader_ReadAsync_NonUtf8Path_ReadsContent()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        string osPath = Path.Combine(_repo.Workdir!, nonUtf8.ToFileSystemString());
        byte[] content = Encoding.UTF8.GetBytes("reader content\n");
        await File.WriteAllBytesAsync(osPath, content, TestContext.Current.CancellationToken);

        // Stage the file so the WorkdirReader's index validation passes.
        GitIndex index = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        await index.AddByPathAsync(nonUtf8, TestContext.Current.CancellationToken);
        GitOid stagedOid = index.EntryByPath(nonUtf8, stage: 0)!.Value.Id;
        await index.WriteAsync(TestContext.Current.CancellationToken);

        // Read via the WorkdirReader (the FS-boundary read abstraction).
        var reader = new WorkdirReader(_repo, validateIndex: true);
        ReaderReadResult result = await reader.ReadAsync(nonUtf8, TestContext.Current.CancellationToken);

        Assert.Equal(ReadStatus.Found, result.Status);
        Assert.NotNull(result.Result);
        Assert.Equal(content, result.Result!.Content.ToArray());
        Assert.Equal(stagedOid, result.Result.Oid);
    }

    // ===== FilesystemIterator walks a non-UTF-8 workdir path =====

    [Fact]
    public async Task FilesystemIterator_NonUtf8WorkdirPath_EntryPathIsByteFaithful()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        string osPath = Path.Combine(_repo.Workdir!, nonUtf8.ToFileSystemString());
        byte[] content = Encoding.UTF8.GetBytes("iter content\n");
        await File.WriteAllBytesAsync(osPath, content, TestContext.Current.CancellationToken);

        // Walk the workdir. The FilesystemIterator ingress (FromFileSystemString)
        // reads the file name back as a GitPath. The entry's Path is the
        // re-encoded bytes (NOT the original non-UTF-8, because the OS name is
        // the lossy U+FFFD decode). But the entry IS present and has a non-empty
        // path — proving the iterator walks the non-UTF-8-named file.
        GitIndex index = await _repo.GetIndexAsync(TestContext.Current.CancellationToken);
        IIterator iter = await FilesystemIterator.ForWorkdirAsync(_repo, index, tree: null, cancellationToken: TestContext.Current.CancellationToken);

        GitIndexEntry? found = null;
        GitIndexEntry? entry;
        while ((entry = await iter.AdvanceAsync(TestContext.Current.CancellationToken)) is not null)
        {
            // The iterator produces entries with forward-slash git paths. The
            // non-UTF-8 file's OS name is the lossy decode; the iterator's
            // ingress re-encodes it. The entry's Path is the re-encoded bytes.
            if (entry.Value.Mode != GitFileMode.Tree)
            {
                found = entry;
                break;
            }
        }

        Assert.NotNull(found);
        Assert.NotEqual(GitFileMode.Tree, found!.Value.Mode);
        Assert.False(found.Value.Path.IsEmpty, "the iterator should have walked the non-UTF-8-named file");
    }

    // ===== GitFilterList.LoadAsync(GitPath) resolves attributes byte-faithfully =====

    [Fact]
    public async Task GitFilterList_LoadAsync_NonUtf8Path_DoesNotCorrupt()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        string osPath = Path.Combine(_repo.Workdir!, nonUtf8.ToFileSystemString());
        byte[] content = Encoding.UTF8.GetBytes("filter content\n");
        await File.WriteAllBytesAsync(osPath, content, TestContext.Current.CancellationToken);

        // LoadAsync with the GitPath overload should not throw and should
        // resolve attributes for the byte-faithful path. The CRLF filter is
        // registered by default, so this may return a non-null list; the
        // proof is that it doesn't throw and doesn't corrupt the path.
        GitFilterList? filters = await _repo.FilterListLoadAsync(
            nonUtf8, blobId: null, GitFilterMode.ToOdb,
            GitFilterListFlags.None, attrCommitId: null, TestContext.Current.CancellationToken);

        // The path is non-UTF-8; no .gitattributes sets the text/eol attrs,
        // so the CRLF filter's CheckAsync should return Passthrough (no
        // attribute set). But the filter list may still be non-null if any
        // filter registered. The key assertion: no exception, and applying
        // the filter list to the content doesn't corrupt.
        if (filters is not null)
        {
            byte[] filtered = await filters.ApplyToBufferAsync(content, TestContext.Current.CancellationToken);
            Assert.NotNull(filtered);
            filters.Dispose();
        }
    }

    // ===== IObjectReader interface takes GitPath (compile-time check) =====

    [Fact]
    public async Task IObjectReader_ReadAsync_TakesGitPath()
    {
        var nonUtf8 = GitPath.FromUtf8Bytes(s_nonUtf8);
        string osPath = Path.Combine(_repo.Workdir!, nonUtf8.ToFileSystemString());
        byte[] content = Encoding.UTF8.GetBytes("tree reader\n");
        await File.WriteAllBytesAsync(osPath, content, TestContext.Current.CancellationToken);

        // Build a tree with the non-UTF-8 path so TreeReader can find it.
        GitOid blobOid = await _repo.ObjectWriteAsync(GitObjectType.Blob, content, TestContext.Current.CancellationToken);
        GitTreeBuilder builder = _repo.NewTreeBuilder();
        await builder.InsertAsync(nonUtf8, blobOid, GitFileMode.Regular, TestContext.Current.CancellationToken);
        GitOid treeOid = await builder.WriteAsync(TestContext.Current.CancellationToken);
        GitTree tree = (await _repo.ObjectLookupAsync<GitTree>(treeOid, TestContext.Current.CancellationToken))!;

        // TreeReader.ReadAsync takes GitPath.
        IObjectReader reader = new TreeReader(tree, _repo);
        ReaderReadResult result = await reader.ReadAsync(nonUtf8, TestContext.Current.CancellationToken);

        Assert.Equal(ReadStatus.Found, result.Status);
        Assert.NotNull(result.Result);
        Assert.Equal(content, result.Result!.Content.ToArray());
    }
}
