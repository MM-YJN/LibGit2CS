using LibGit2CS.Attributes;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Attributes;

/// <summary>
/// Tests for the byte-faithful <see cref="AttributesFile"/>
/// + <see cref="AttributeCache"/>. These pin that a <c>.gitattributes</c>
/// with a non-UTF-8 pattern matches a non-UTF-8 file path byte-exact (the
/// string read via <c>ReadAllTextWithNoBomAsync</c> would
/// U+FFFD-replace the invalid bytes, corrupting the pattern).
/// </summary>
public class AttributeNonUtf8PathTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly GitRepository _repo;

    public AttributeNonUtf8PathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_AttrNonUtf8_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _repo = GitRepository.InitAsync(_tempDir, isBare: false, new GitContext()).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    // A .gitattributes with a non-UTF-8 pattern assigns an attribute to a
    // non-UTF-8 path byte-exact.
    [Fact]
    public async Task NonUtf8Pattern_AssignsAttributeToNonUtf8Path()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // Write .gitattributes with a non-UTF-8 pattern and a -diff assignment.
        byte[] pattern = [0xFF, 0xFE, 0x80];
        byte[] gitattributes = [.. pattern, .. " -diff\n"u8];
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, ".gitattributes"), gitattributes, cancellationToken);

        AttributeCache cache = await _repo.GetAttributeCacheAsync(cancellationToken);
        var ap = new AttrPath();
        ap.Init(GitPath.FromUtf8Bytes(pattern), default, AttrPath.DirFlag.False);
        GitAttrValue value = await cache.LookupOneAsync(ap, "diff", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(value.IsFalse);
    }

    // A non-UTF-8 pattern does NOT match an ASCII path.
    [Fact]
    public async Task NonUtf8Pattern_DoesNotMatchAsciiPath()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        byte[] pattern = [0xFF, 0xFE, 0x80];
        byte[] gitattributes = [.. pattern, .. " -diff\n"u8];
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, ".gitattributes"), gitattributes, cancellationToken);

        AttributeCache cache = await _repo.GetAttributeCacheAsync(cancellationToken);
        var ap = new AttrPath();
        ap.Init(GitPath.FromUtf8String("foo.txt"), default, AttrPath.DirFlag.False);
        GitAttrValue value = await cache.LookupOneAsync(ap, "diff", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(value.Kind is GitAttrValueKind.None);
    }

    // An ASCII pattern matches an ASCII path (parity — no regression).
    [Fact]
    public async Task AsciiPattern_MatchesAsciiPath()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".gitattributes"), "*.txt -diff\n", cancellationToken);

        AttributeCache cache = await _repo.GetAttributeCacheAsync(cancellationToken);
        var ap = new AttrPath();
        ap.Init(GitPath.FromUtf8String("foo.txt"), default, AttrPath.DirFlag.False);
        GitAttrValue value = await cache.LookupOneAsync(ap, "diff", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(value.IsFalse);
    }

    // Byte ParseBuffer agrees with string ParseBuffer on ASCII (parity).
    [Fact]
    public void ByteParseBuffer_AgreesWithStringParseBuffer_OnAscii()
    {
        byte[] content = "*.txt -diff\n"u8.ToArray();
        var byteFile = new AttributesFile();
        byteFile.ParseBuffer(content, null, false, default);

        var strFile = new AttributesFile();
        strFile.ParseBuffer("*.txt -diff\n", null, false, null);

        Assert.Equal(strFile.Rules.Count, byteFile.Rules.Count);
        Assert.Equal("*.txt", byteFile.Rules[0].Match.Pattern.ToUtf8String());
    }

    // The per-directory walk is byte-domain. A .gitattributes staged at a non-UTF-8 index path (<dir>/.gitattributes) resolves for
    // a file inside <dir>/ via the INDEX source — the string DirChain decoded the dir to U+FFFD, re-encoded it for EntryByPath, and missed the entry.
    [Fact]
    public async Task NonUtf8Directory_IndexSourceAttributes_Resolve()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        byte[] dir = [.. "sub"u8.ToArray(), 0xE9];
        byte[] attrPath = [.. dir, .. "/.gitattributes"u8.ToArray()];

        // Stage sub\xE9/.gitattributes with a -diff rule in the repo index.
        GitOid blobOid = await _repo.ObjectWriteAsync(
            LibGit2CS.Objects.GitObjectType.Blob,
            "file.txt -diff\n"u8.ToArray(),
            cancellationToken);
        LibGit2CS.Index.GitIndex index = await _repo.GetIndexAsync(cancellationToken);
        index.Add(new LibGit2CS.Index.GitIndexEntry(
            GitPath.FromUtf8Bytes(attrPath),
            blobOid,
            LibGit2CS.Objects.GitFileMode.Regular));
        await index.WriteAsync(cancellationToken);

        AttributeCache cache = await _repo.GetAttributeCacheAsync(cancellationToken);
        byte[] filePath = [.. dir, .. "/file.txt"u8.ToArray()];
        var ap = new AttrPath();
        ap.Init(GitPath.FromUtf8Bytes(filePath), default, AttrPath.DirFlag.False);
        GitAttrValue value = await cache.LookupOneAsync(ap, "diff", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(value.IsFalse);
    }

    // parity control — an ASCII directory's INDEX-source attributes still resolve.
    [Fact]
    public async Task AsciiDirectory_IndexSourceAttributes_Resolve()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        GitOid blobOid = await _repo.ObjectWriteAsync(
            LibGit2CS.Objects.GitObjectType.Blob,
            "file.txt -diff\n"u8.ToArray(),
            cancellationToken);
        LibGit2CS.Index.GitIndex index = await _repo.GetIndexAsync(cancellationToken);
        index.Add(new LibGit2CS.Index.GitIndexEntry(
            GitPath.FromUtf8String("sub/.gitattributes"),
            blobOid,
            LibGit2CS.Objects.GitFileMode.Regular));
        await index.WriteAsync(cancellationToken);

        AttributeCache cache = await _repo.GetAttributeCacheAsync(cancellationToken);
        var ap = new AttrPath();
        ap.Init(GitPath.FromUtf8String("sub/file.txt"), default, AttrPath.DirFlag.False);
        GitAttrValue value = await cache.LookupOneAsync(ap, "diff", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(value.IsFalse);
    }
}
