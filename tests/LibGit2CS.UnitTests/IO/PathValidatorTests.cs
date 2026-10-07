using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

public sealed class PathValidatorTests
{
    [Fact]
    public async Task IsValid_NormalPath_Accepts()
    {
        Assert.True(await GitPathValidator.IsValidAsync("src/main.cs", PathRejectPresets.WorkdirDefaults, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("README.md", PathRejectPresets.WorkdirDefaults, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("a/b/c/d.txt", PathRejectPresets.WorkdirDefaults, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_EmptyPath_WithEmptyComponentReject_Rejects()
    {
        Assert.False(await GitPathValidator.IsValidAsync("", GitPathRejectFlags.EmptyComponent, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_DotGit_Literal_Rejects()
    {
        Assert.False(await GitPathValidator.IsValidAsync(".git", GitPathRejectFlags.DotGitLiteral, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync(".Git", GitPathRejectFlags.DotGitLiteral, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync(".GIt", GitPathRejectFlags.DotGitLiteral, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync(".GIT", GitPathRejectFlags.DotGitLiteral, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_DotGit_NotGit_Accepts()
    {
        Assert.True(await GitPathValidator.IsValidAsync(".gitignore", GitPathRejectFlags.DotGitLiteral, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync(".github", GitPathRejectFlags.DotGitLiteral, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("git", GitPathRejectFlags.DotGitLiteral, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("foo.txt", GitPathRejectFlags.DotGitLiteral, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_DotGit_InPath_Rejects()
    {
        Assert.False(await GitPathValidator.IsValidAsync("foo/.git", GitPathRejectFlags.DotGitLiteral, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("foo/.Git/bar", GitPathRejectFlags.DotGitLiteral, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync(".git/config", GitPathRejectFlags.DotGitLiteral, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_DotGit_Hfs_Rejects()
    {
        // .git with HFS normalization — basic .git should be rejected.
        Assert.False(await GitPathValidator.IsValidAsync(".git", GitPathRejectFlags.DotGitHfs, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync(".Git", GitPathRejectFlags.DotGitHfs, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_DotGit_Ntfs_Rejects()
    {
        Assert.False(await GitPathValidator.IsValidAsync(".git", GitPathRejectFlags.DotGitNtfs, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync(".Git", GitPathRejectFlags.DotGitNtfs, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync(".git\\foo", GitPathRejectFlags.DotGitNtfs, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync(".git:stream", GitPathRejectFlags.DotGitNtfs, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync(".git ", GitPathRejectFlags.DotGitNtfs, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync(".git.", GitPathRejectFlags.DotGitNtfs, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_DotGit_NtfsShortName_Rejects()
    {
        Assert.False(await GitPathValidator.IsValidAsync("GIT~1", GitPathRejectFlags.DotGitNtfs, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("git~1", GitPathRejectFlags.DotGitNtfs, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_DotGit_Ntfs_NotGit_Accepts()
    {
        Assert.True(await GitPathValidator.IsValidAsync(".gitignore", GitPathRejectFlags.DotGitNtfs, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("gitfoo", GitPathRejectFlags.DotGitNtfs, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_Traversal_Rejects()
    {
        Assert.False(await GitPathValidator.IsValidAsync("../foo", GitPathRejectFlags.Traversal, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("foo/../bar", GitPathRejectFlags.Traversal, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("./foo", GitPathRejectFlags.Traversal, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("foo/./bar", GitPathRejectFlags.Traversal, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_Traversal_NotTraversal_Accepts()
    {
        Assert.True(await GitPathValidator.IsValidAsync("foo/bar", GitPathRejectFlags.Traversal, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("...foo", GitPathRejectFlags.Traversal, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_NulByte_RejectedWithFlags_AcceptedWithout()
    {
        // C (fs_path.c:1711-1712): with NO reject flags every string is
        // valid - even one with an embedded NUL. With reject flags, the NUL
        // fails the byte walk (fs_path.c:1722-1724).
        Assert.False(await GitPathValidator.IsValidAsync("foo\0bar", PathRejectPresets.WorkdirDefaults, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("foo\0bar", GitPathRejectFlags.None, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_NoFlags_AcceptsEverything()
    {
        Assert.True(await GitPathValidator.IsValidAsync(".git", GitPathRejectFlags.None, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("../foo", GitPathRejectFlags.None, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("normal/path", GitPathRejectFlags.None, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_IndexDefaults_RejectsTraversalAndDotGit()
    {
        Assert.False(await GitPathValidator.IsValidAsync("../foo", GitPathRejectFlags.IndexDefaults, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync(".git", GitPathRejectFlags.IndexDefaults, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("foo/.git/bar", GitPathRejectFlags.IndexDefaults, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("src/main.cs", GitPathRejectFlags.IndexDefaults, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_WorkdirDefaults_RejectsDotGit()
    {
        Assert.False(await GitPathValidator.IsValidAsync(".git", PathRejectPresets.WorkdirDefaults, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("foo/.git", PathRejectPresets.WorkdirDefaults, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("src/main.cs", PathRejectPresets.WorkdirDefaults, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_Backslash_WithBackslashReject_Rejects()
    {
        Assert.False(await GitPathValidator.IsValidAsync("foo\\bar", GitPathRejectFlags.Backslash, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_TrailingDot_WithTrailingDotReject_Rejects()
    {
        Assert.False(await GitPathValidator.IsValidAsync("foo.", GitPathRejectFlags.TrailingDot, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("foo/bar.", GitPathRejectFlags.TrailingDot, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("foo.bar", GitPathRejectFlags.TrailingDot, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_TrailingSpace_WithTrailingSpaceReject_Rejects()
    {
        Assert.False(await GitPathValidator.IsValidAsync("foo ", GitPathRejectFlags.TrailingSpace, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("foo/bar ", GitPathRejectFlags.TrailingSpace, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("foo bar", GitPathRejectFlags.TrailingSpace, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_DosPaths_ConRejects()
    {
        Assert.False(await GitPathValidator.IsValidAsync("CON", GitPathRejectFlags.DosPaths, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("con", GitPathRejectFlags.DosPaths, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("CON.txt", GitPathRejectFlags.DosPaths, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("PRN", GitPathRejectFlags.DosPaths, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("AUX", GitPathRejectFlags.DosPaths, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("NUL", GitPathRejectFlags.DosPaths, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_DosPaths_ComWithDigit_Rejects()
    {
        Assert.False(await GitPathValidator.IsValidAsync("COM1", GitPathRejectFlags.DosPaths, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("com9", GitPathRejectFlags.DosPaths, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("LPT1", GitPathRejectFlags.DosPaths, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("COM0", GitPathRejectFlags.DosPaths, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("COM", GitPathRejectFlags.DosPaths, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_DosPaths_NotReserved_Accepts()
    {
        Assert.True(await GitPathValidator.IsValidAsync("CONSOLE", GitPathRejectFlags.DosPaths, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await GitPathValidator.IsValidAsync("config.txt", GitPathRejectFlags.DosPaths, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_NtChars_Rejects()
    {
        Assert.False(await GitPathValidator.IsValidAsync("foo:bar", GitPathRejectFlags.NtChars, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("foo<bar", GitPathRejectFlags.NtChars, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("foo>bar", GitPathRejectFlags.NtChars, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("foo|bar", GitPathRejectFlags.NtChars, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("foo?bar", GitPathRejectFlags.NtChars, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("foo*bar", GitPathRejectFlags.NtChars, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("foo\"bar", GitPathRejectFlags.NtChars, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_NtChars_ControlChars_Rejects()
    {
        Assert.False(await GitPathValidator.IsValidAsync("foo\u0001bar", GitPathRejectFlags.NtChars, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_EmptyComponent_WithReject_Rejects()
    {
        Assert.False(await GitPathValidator.IsValidAsync("foo//bar", GitPathRejectFlags.EmptyComponent, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("//foo", GitPathRejectFlags.EmptyComponent, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await GitPathValidator.IsValidAsync("foo//", GitPathRejectFlags.EmptyComponent, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValid_EmptyComponent_WithoutReject_Accepts()
    {
        Assert.True(await GitPathValidator.IsValidAsync("foo//bar", GitPathRejectFlags.None, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValidLength_NormalPath_Accepts()
    {
        Assert.True(await GitPathValidator.IsValidLengthAsync(new string('a', 100), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsValidLength_VeryLongPath_AcceptsOnNonWindows()
    {
        // On POSIX, no length limit. On Windows, 260 is the default limit.
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(await GitPathValidator.IsValidLengthAsync(new string('a', 500), cancellationToken: TestContext.Current.CancellationToken));
        }
    }
}
