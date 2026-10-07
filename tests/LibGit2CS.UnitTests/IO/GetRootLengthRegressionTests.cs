using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

// GetRootLengthInternal
// treated POSIX filenames with drive/UNC shapes as rooted paths. C's
// git_fs_path_root (fs_path.c) gates the UNC and trailing-backslash branches
// behind GIT_WIN32, requires a real separator for the drive branch (offset 2
// for "C:" + a following '/'), and returns -1 (unrooted) for everything
// else. Returning the whole path length for "\\server" (no
// second separator), searching only for '\\' in UNC names, or treating
// "x:\\y" as a rooted drive path on POSIX would collapse AttrPath rel-paths like
// "\server" to "" and skip the base join where C joins.
public sealed class GetRootLengthRegressionTests
{
    private static AttrPath Init(string path, string baseDir)
    {
        var ap = new AttrPath();
        ap.Init(path, baseDir, AttrPath.DirFlag.False);
        return ap;
    }

    [Fact]
    public void DoubleBackslashServer_IsNotRooted_JoinsBase()
    {
        // "\\server" (UNC-shaped but no second separator): C returns -1, so
        // with a base the path is joined and the rel path keeps the whole
        // name. Rooting the whole path would give rel "".
        AttrPath ap = Init("\\\\server", "/repo");
        Assert.Equal("\\\\server", ap.Path.ToUtf8String());
    }

    [Fact]
    public void DoubleBackslashServerShare_IsNotRooted_OnPosix()
    {
        // "\\server\share": on POSIX the UNC branch is WIN32-gated, so C
        // returns -1 and the rel path keeps the whole name; on Windows it
        // is a genuine UNC root (git_fs_path_root returns the offset of the
        // share separator, 8) and the rel path is "\share" — git_attr_path__init
        // skips only '/' when computing the relative path (attr_file.c:589-590),
        // so the leading '\' is retained.
        AttrPath ap = Init("\\\\server\\share", "/repo");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("\\share", ap.Path.ToUtf8String());
        }
        else
        {
            Assert.Equal("\\\\server\\share", ap.Path.ToUtf8String());
        }
    }

    [Fact]
    public void DriveColonBackslashPath_IsNotRooted_OnPosix()
    {
        // "x:\\y": dos_drive_prefix_length is 2, but '\\' is only a root
        // separator under GIT_WIN32 — on POSIX the path is unrooted. On
        // Windows the drive prefix (2) plus the trailing-backslash root
        // check (fs_path.c) roots at 2, and git_attr_path__init skips only
        // '/' (attr_file.c:589-590), so the rel path is "\\y".
        AttrPath ap = Init("x:\\\\y", "/repo");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("\\\\y", ap.Path.ToUtf8String());
        }
        else
        {
            Assert.Equal("x:\\\\y", ap.Path.ToUtf8String());
        }
    }

    [Fact]
    public void PosixAbsolute_WithBase_DoesNotJoin()
    {
        // Control: "/abs/x" is rooted at offset 0 in C (git_fs_path_root
        // returns 0), so the base is ignored and the rel path is "abs/x".
        AttrPath ap = Init("/abs/x", "/repo");
        Assert.Equal("abs/x", ap.Path.ToUtf8String());
    }

    [Fact]
    public void DriveColonSlash_WithBase_DoesNotJoin()
    {
        // Control: "C:/x" has drive prefix 2 and a '/' at offset 2 → rooted
        // in C on every platform; rel path "x".
        AttrPath ap = Init("C:/x", "/repo");
        Assert.Equal("x", ap.Path.ToUtf8String());
    }

    [Fact]
    public void DigitDrive_IsRooted_LikeC()
    {
        // Control: C's dos_drive_prefix_length has no isalpha requirement —
        // "1:/x" roots on every platform ("x").
        AttrPath ap = Init("1:/x", "/repo");
        Assert.Equal("x", ap.Path.ToUtf8String());
    }

    [Fact]
    public void ColonWithoutSeparator_IsNotRooted()
    {
        // Control: "a:b/c" has a drive-ish prefix but no separator after the
        // colon — C returns -1, so the base join happens (rel "a:b/c").
        AttrPath ap = Init("a:b/c", "/repo");
        Assert.Equal("a:b/c", ap.Path.ToUtf8String());
    }

    [Fact]
    public void DoubleBackslashServer_WithoutBase_KeepsName()
    {
        // No base: the rel path must keep the whole name (C: root -1 → 0).
        AttrPath ap = Init("\\\\server", string.Empty);
        Assert.Equal("\\\\server", ap.Path.ToUtf8String());
    }

    [Fact]
    public void Dirname_DoubleSlashDrive_TrimsToDrivePrefix()
    {
        // C dirname("C:/x") keeps the drive prefix "C:" — a root
        // length of 3 returns the same string here ("C:"), but the root
        // length itself must match C (2, not 3), which matters for the
        // rooted/not-rooted classification in AttrPath/JoinUnrooted.
        Assert.Equal("C:", PathByteHelpers.Dirname("C:/x"u8).ToUtf8String());
    }

    [Fact]
    public void Dirname_Posix_Unchanged()
    {
        // Controls: unchanged by the root-length fix.
        Assert.Equal("/foo", PathByteHelpers.Dirname("/foo/bar"u8).ToUtf8String());
        Assert.Equal("/", PathByteHelpers.Dirname("/foo"u8).ToUtf8String());
        Assert.Equal(".", PathByteHelpers.Dirname("file.txt"u8).ToUtf8String());
    }
}
