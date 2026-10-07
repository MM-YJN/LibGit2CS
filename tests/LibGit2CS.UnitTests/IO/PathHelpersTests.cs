using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

public class PathHelpersTests
{
    [Fact]
    public void IsDotOrDotDot_Dot_ReturnsTrue()
    {
        Assert.True(PathHelpers.IsDotOrDotDot("."));
    }

    [Fact]
    public void IsDotOrDotDot_DotDot_ReturnsTrue()
    {
        Assert.True(PathHelpers.IsDotOrDotDot(".."));
    }

    [Fact]
    public void IsDotOrDotDot_Other_ReturnsFalse()
    {
        Assert.False(PathHelpers.IsDotOrDotDot("foo"));
        Assert.False(PathHelpers.IsDotOrDotDot(".git"));
    }

    [Fact]
    public void IsDirSeparator_ForwardAndBackward_ReturnsTrue()
    {
        Assert.True(PathHelpers.IsDirSeparator('/'));
        Assert.True(PathHelpers.IsDirSeparator('\\'));
    }

    [Fact]
    public void IsDirSeparator_OtherChar_ReturnsFalse()
    {
        Assert.False(PathHelpers.IsDirSeparator('a'));
        Assert.False(PathHelpers.IsDirSeparator(':'));
    }

    [Fact]
    public void Dirname_OfSimplePath_ReturnsParent()
    {
        Assert.Equal("/foo", PathHelpers.Dirname("/foo/bar"));
    }

    [Fact]
    public void Dirname_OfFileName_ReturnsDot()
    {
        Assert.Equal(".", PathHelpers.Dirname("file.txt"));
    }

    [Fact]
    public void Dirname_OfRoot_ReturnsRoot()
    {
        Assert.Equal("/", PathHelpers.Dirname("/foo"));
    }

    [Fact]
    public void Dirname_OfTrailingSlash_IsTrimmed()
    {
        Assert.Equal("/foo", PathHelpers.Dirname("/foo/bar/"));
    }

    [Fact]
    public void Join_TwoComponents_AddsSeparator()
    {
        Assert.Equal("foo/bar", PathHelpers.Join("foo", "bar"));
    }

    [Fact]
    public void Join_FirstHasTrailingSlash_NoDoubleSeparator()
    {
        Assert.Equal("foo/bar", PathHelpers.Join("foo/", "bar"));
    }

    [Fact]
    public void Join_SecondHasLeadingSlash_ProducesSingleSeparator()
    {
        // "foo" + "/bar" → "foo/bar" (single separator, not double)
        Assert.Equal("foo/bar", PathHelpers.Join("foo", "/bar"));
    }

    [Fact]
    public void Join_SecondStartsWithSlash_JoinsWithSingleSeparator()
    {
        // git's joinpath joins "foo" + "/bar" → "foo/bar" (does not treat as absolute)
        Assert.Equal("foo/abs", PathHelpers.Join("foo", "/abs"));
    }

    [Fact]
    public void ToDir_NoTrailingSlash_AppendsSlash()
    {
        Assert.Equal("foo/", PathHelpers.ToDir("foo"));
    }

    [Fact]
    public void ToDir_HasTrailingSlash_Unchanged()
    {
        Assert.Equal("foo/", PathHelpers.ToDir("foo/"));
    }

    [Fact]
    public void SquashSlashes_MultipleSlashes_BecomeSingle()
    {
        Assert.Equal("a/b/c", PathHelpers.SquashSlashes("a//b///c"));
    }

    [Fact]
    public void IsAbsolute_AbsolutePath_ReturnsTrue()
    {
        Assert.True(PathHelpers.IsAbsolute("/foo"));
        // C (fs_path.c:283-311): a leading backslash is a root on Windows
        // only - on POSIX it is a literal path character.
        if (OperatingSystem.IsWindows())
        {
            Assert.True(PathHelpers.IsAbsolute("\\foo"));
        }
        else
        {
            Assert.False(PathHelpers.IsAbsolute("\\foo"));
        }
    }

    [Fact]
    public void IsAbsolute_RelativePath_ReturnsFalse()
    {
        Assert.False(PathHelpers.IsAbsolute("foo"));
        Assert.False(PathHelpers.IsAbsolute("foo/bar"));
        Assert.False(PathHelpers.IsAbsolute(""));
    }

    [Fact]
    public void IsAbsolute_WindowsDriveLetter_ReturnsTrue()
    {
        Assert.True(PathHelpers.IsAbsolute("C:\\foo"));
        Assert.True(PathHelpers.IsAbsolute("C:/foo"));
        Assert.True(PathHelpers.IsAbsolute("c:\\foo"));
        Assert.True(PathHelpers.IsAbsolute("z:/foo"));
    }

    [Fact]
    public void IsAbsolute_BareDriveLetter_ReturnsFalse()
    {
        // "C:foo" (no separator after colon) is a Windows drive-relative path,
        // not an absolute path. Matches git_fs_path_root which requires a
        // separator after the drive letter.
        Assert.False(PathHelpers.IsAbsolute("C:foo"));
        Assert.False(PathHelpers.IsAbsolute("C:"));
    }

    [Fact]
    public void CommonDirLength_SharedPrefix_ReturnsLength()
    {
        // C git_fs_path_common_dirlen returns (dirsep - one) + 1 — the common
        // directory prefix INCLUDING the trailing '/' ("/foo/").
        Assert.Equal("/foo/".Length, PathHelpers.CommonDirLength("/foo/bar", "/foo/baz"));
    }

    [Fact]
    public void CommonDirLength_NoCommonPrefix_ReturnsZero()
    {
        Assert.Equal(0, PathHelpers.CommonDirLength("foo", "bar"));
    }

    [Fact]
    public void MakeRelative_InsideParent_ReturnsRelative()
    {
        // C: git_fs_path_make_relative("/foo/bar", "/foo/") -> "bar" (rc 0)
        Assert.Equal("bar", PathHelpers.MakeRelative("/foo/bar", "/foo/"));
    }

    [Fact]
    public void MakeRelative_OutsideParent_ReturnsDotDotPath()
    {
        // C: git_fs_path_make_relative("/other/path", "/foo/") -> "../other/path"
        Assert.Equal("../other/path", PathHelpers.MakeRelative("/other/path", "/foo/"));
    }

    [Fact]
    public void MakeRelative_Identical_ReturnsEmpty()
    {
        // C: git_fs_path_make_relative("/foo/", "/foo/") -> "" (rc 0)
        Assert.Equal(string.Empty, PathHelpers.MakeRelative("/foo/", "/foo/"));
    }

    [Fact]
    public void MakeRelative_PreservesInputTrailingSlash()
    {
        // C: git_fs_path_make_relative("/foo/bar/", "/foo/") -> "bar/"
        Assert.Equal("bar/", PathHelpers.MakeRelative("/foo/bar/", "/foo/"));
    }

    [Fact]
    public void MakeRelative_NoCommonSegment_ThrowsNotFound()
    {
        // C: git_fs_path_make_relative("foo/bar", "foo") -> GIT_ENOTFOUND,
        // "foo is not a parent of foo/bar"
        GitException ex = Assert.Throws<GitException>(() => PathHelpers.MakeRelative("foo/bar", "foo"));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("foo is not a parent of foo/bar", ex.Message);
    }

    [Fact]
    public void MakePosix_Backslashes_BecomeForward()
    {
        Assert.Equal("foo/bar", PathHelpers.MakePosix("foo\\bar"));
    }
}
