using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

/// <summary>
/// Tests for the byte-faithful <see cref="AttrPath"/>.
/// These pin the zero-copy slice model (Full owns the buffer; Path/Basename
/// slice into it) and the byte-exact round-trip for non-UTF-8 paths. The
/// <see cref="AttrPath"/> stored three independent
/// <c>string</c>s and decoded the path via <c>Encoding.UTF8</c>, corrupting
/// invalid byte sequences (U+FFFD replacement fallback) and allocating a
/// fresh string per field.
/// </summary>
public class AttrPathNonUtf8PathTests
{
    // Non-UTF-8 path round-trips byte-exact through all three fields.
    // 0xFF 0xFE 0x80 is invalid UTF-8 — a string impl would
    // U+FFFD-replace every byte and corrupt the path.
    [Fact]
    public void NonUtf8Path_RoundTripsByteExact()
    {
        byte[] raw = [0xFF, 0xFE, 0x80];
        var ap = new AttrPath();
        ap.Init(GitPath.FromUtf8Bytes(raw), default, AttrPath.DirFlag.False);

        Assert.Equal(raw, ap.Full.Span.ToArray());
        Assert.Equal(raw, ap.Path.Span.ToArray());
        Assert.Equal(raw, ap.Basename.Span.ToArray());
        Assert.False(ap.IsDir);
    }

    // Basename of "dir/0xFF 0xFE 0x80" is the non-UTF-8 segment.
    [Fact]
    public void NonUtf8Basename_IsLastSegment()
    {
        byte[] dirBytes = [0xFF, 0xFE, 0x80];
        byte[] pathBytes = [.. "dir/"u8, .. dirBytes];
        var ap = new AttrPath();
        ap.Init(GitPath.FromUtf8Bytes(pathBytes), default, AttrPath.DirFlag.False);

        Assert.Equal(pathBytes, ap.Full.Span.ToArray());
        Assert.Equal(pathBytes, ap.Path.Span.ToArray());
        Assert.Equal(dirBytes, ap.Basename.Span.ToArray());
    }

    // The byte-overload Init agrees with the string overload on ASCII
    // (parity check — the string overload delegates to the byte overload).
    [Fact]
    public void ByteInit_AgreesWithStringInit_OnAscii()
    {
        var apByte = new AttrPath();
        apByte.Init(GitPath.FromUtf8String("src/foo.txt"), GitPath.FromUtf8String("/repo"), AttrPath.DirFlag.False);

        var apStr = new AttrPath();
        apStr.Init("src/foo.txt", "/repo", AttrPath.DirFlag.False);

        Assert.Equal(apStr.Full.ToUtf8String(), apByte.Full.ToUtf8String());
        Assert.Equal(apStr.Path.ToUtf8String(), apByte.Path.ToUtf8String());
        Assert.Equal(apStr.Basename.ToUtf8String(), apByte.Basename.ToUtf8String());
        Assert.Equal(apStr.IsDir, apByte.IsDir);
    }

    // Zero-copy: Path and Basename are slices into Full's buffer (same
    // backing array). This is the tree.c:435 model applied to attr paths.
    [Fact]
    public void PathAndBasename_AreSlicesIntoFull()
    {
        var ap = new AttrPath();
        ap.Init(GitPath.FromUtf8String("a/b/c.txt"), default, AttrPath.DirFlag.False);

        // Full = "a/b/c.txt" (9 bytes). Path = "a/b/c.txt" (slice 0..9).
        // Basename = "c.txt" (slice 4..9). The slice starts agree.
        Assert.Equal(9, ap.Full.Length);
        Assert.Equal(9, ap.Path.Length);
        Assert.Equal(5, ap.Basename.Length);
        Assert.Equal("c.txt", ap.Basename.ToUtf8String());
    }

    // Containing-dir semantics: with a base dir, Path is relative to it.
    [Fact]
    public void WithBaseDir_PathIsRelative()
    {
        var ap = new AttrPath();
        ap.Init(GitPath.FromUtf8String("sub/file.txt"), GitPath.FromUtf8String("/repo"), AttrPath.DirFlag.False);

        // Full = "/repo/sub/file.txt"; Path = "sub/file.txt"; Basename = "file.txt".
        Assert.Equal("/repo/sub/file.txt", ap.Full.ToUtf8String());
        Assert.Equal("sub/file.txt", ap.Path.ToUtf8String());
        Assert.Equal("file.txt", ap.Basename.ToUtf8String());
    }

    // Trailing-slash stripping: "dir/" yields Basename = "dir" (not "").
    [Fact]
    public void TrailingSlash_StrippedFromFullAndPath()
    {
        var ap = new AttrPath();
        ap.Init(GitPath.FromUtf8String("dir/"), default, AttrPath.DirFlag.True);

        Assert.Equal("dir", ap.Full.ToUtf8String());
        Assert.Equal("dir", ap.Path.ToUtf8String());
        Assert.Equal("dir", ap.Basename.ToUtf8String());
        Assert.True(ap.IsDir);
    }

    // Free resets to empty.
    [Fact]
    public void Free_ClearsAllFields()
    {
        var ap = new AttrPath();
        ap.Init(GitPath.FromUtf8String("foo/bar.txt"), GitPath.FromUtf8String("/repo"), AttrPath.DirFlag.False);
        ap.Free();

        Assert.True(ap.Full.IsEmpty);
        Assert.True(ap.Path.IsEmpty);
        Assert.True(ap.Basename.IsEmpty);
        Assert.False(ap.IsDir);
    }
}
