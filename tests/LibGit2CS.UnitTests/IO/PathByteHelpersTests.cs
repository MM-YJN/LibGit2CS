using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

public class PathByteHelpersTests
{
    private static ReadOnlySpan<byte> B(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static string S(GitPath p) => p.ToUtf8String();

    [Fact]
    public void IsDotOrDotDot_DotAndDotDot_True()
    {
        Assert.True(PathByteHelpers.IsDotOrDotDot(B(".")));
        Assert.True(PathByteHelpers.IsDotOrDotDot(B("..")));
    }

    [Fact]
    public void IsDotOrDotDot_Other_False()
    {
        Assert.False(PathByteHelpers.IsDotOrDotDot(B("foo")));
        Assert.False(PathByteHelpers.IsDotOrDotDot(B(".git")));
    }

    [Fact]
    public void IsDirSeparator_ForwardAndBackward_True()
    {
        Assert.True(PathByteHelpers.IsDirSeparator((byte)'/'));
        Assert.True(PathByteHelpers.IsDirSeparator((byte)'\\'));
    }

    [Fact]
    public void IsDirSeparator_Other_False()
    {
        Assert.False(PathByteHelpers.IsDirSeparator((byte)'a'));
    }

    [Fact]
    public void MakePosix_Backslashes_BecomeForward()
    {
        Assert.Equal("foo/bar", S(PathByteHelpers.MakePosix(B("foo\\bar"))));
    }

    [Fact]
    public void MakePosix_PreservesNonUtf8Bytes()
    {
        GitPath p = PathByteHelpers.MakePosix(new byte[] { 0x80, (byte)'\\', 0xFF });
        Assert.Equal(new byte[] { 0x80, (byte)'/', 0xFF }, p.Span.ToArray());
    }

    [Fact]
    public void Join_TwoComponents_AddsSeparator()
    {
        Assert.Equal("foo/bar", S(PathByteHelpers.Join(B("foo"), B("bar"))));
    }

    [Fact]
    public void Join_FirstHasTrailingSlash_NoDoubleSeparator()
    {
        Assert.Equal("foo/bar", S(PathByteHelpers.Join(B("foo/"), B("bar"))));
    }

    [Fact]
    public void Join_SecondHasLeadingSlash_SingleSeparator()
    {
        Assert.Equal("foo/bar", S(PathByteHelpers.Join(B("foo"), B("/bar"))));
        Assert.Equal("foo/abs", S(PathByteHelpers.Join(B("foo"), B("/abs"))));
    }

    [Fact]
    public void Join_FirstEmpty_ReturnsSecond()
    {
        Assert.Equal("bar", S(PathByteHelpers.Join(B(""), B("bar"))));
    }

    [Fact]
    public void Join_SecondEmpty_AppendsSeparator()
    {
        // C git_str_join (str.c:760-807): a non-empty 'a' that does not end
        // with the separator still gets one inserted — join("foo","") = "foo/".
        Assert.Equal("foo/", S(PathByteHelpers.Join(B("foo"), B(""))));
        Assert.Equal("foo/", S(PathByteHelpers.Join(B("foo/"), B(""))));
    }

    [Fact]
    public void Dirname_SimplePath_ReturnsParent()
    {
        Assert.Equal("/foo", S(PathByteHelpers.Dirname(B("/foo/bar"))));
    }

    [Fact]
    public void Dirname_FileName_ReturnsDot()
    {
        Assert.Equal(".", S(PathByteHelpers.Dirname(B("file.txt"))));
    }

    [Fact]
    public void Dirname_Root_ReturnsRoot()
    {
        Assert.Equal("/", S(PathByteHelpers.Dirname(B("/foo"))));
    }

    [Fact]
    public void Dirname_TrailingSlash_IsTrimmed()
    {
        Assert.Equal("/foo", S(PathByteHelpers.Dirname(B("/foo/bar/"))));
    }

    [Fact]
    public void SquashSlashes_MultipleSlashes_BecomeSingle()
    {
        Assert.Equal("a/b/c", S(PathByteHelpers.SquashSlashes(B("a//b///c"))));
    }

    [Fact]
    public void SquashSlashes_MixedForwardBack_NormalizedToForward()
    {
        Assert.Equal("a/b", S(PathByteHelpers.SquashSlashes(B("a/\\b"))));
    }

    [Fact]
    public void CommonDirLength_SharedPrefix_ReturnsLength()
    {
        // C git_fs_path_common_dirlen returns (dirsep - one) + 1 — the common
        // directory prefix INCLUDING the trailing '/' ("/foo/").
        Assert.Equal("/foo/".Length, PathByteHelpers.CommonDirLength(B("/foo/bar"), B("/foo/baz")));
    }

    [Fact]
    public void CommonDirLength_NoCommonPrefix_ReturnsZero()
    {
        Assert.Equal(0, PathByteHelpers.CommonDirLength(B("foo"), B("bar")));
    }

    [Fact]
    public void CommonDirLength_ByteFaithful_NonUtf8Handled()
    {
        // Non-UTF-8 byte 0xFF participates in comparison like any other byte:
        // it matches here, then 'x'/'y' diverge after the common '/' separator.
        byte[] a = [0xFF, (byte)'/', (byte)'x'];
        byte[] b = [0xFF, (byte)'/', (byte)'y'];
        Assert.Equal(2, PathByteHelpers.CommonDirLength(a, b));
    }
}
