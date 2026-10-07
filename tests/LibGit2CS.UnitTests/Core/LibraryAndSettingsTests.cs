using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.Core;

public class LibraryInitTests
{
    [Fact]
    public void VersionString_MatchesExpectedFormat()
    {
        Assert.Equal("1.9.4", LibraryInit.VersionString);
    }

    [Fact]
    public void VersionComponents_AreCorrect()
    {
        Assert.Equal(1, LibraryInit.VersionMajor);
        Assert.Equal(9, LibraryInit.VersionMinor);
        Assert.Equal(4, LibraryInit.VersionRevision);
    }

    [Fact]
    public void Features_IncludesCoreCapabilities()
    {
        // The reported set matches the reference build: THREADS | HTTP_PARSER | REGEX | COMPRESSION | SHA1 | NSEC. SHA256 is NOT reported (the C build has
        // GIT_EXPERIMENTAL_ SHA256 off) and SSH is NOT reported (no libssh2 in the build).
        LibraryFeatures features = LibraryInit.Features;

        Assert.True(features.HasFlag(LibraryFeatures.Threads));
        Assert.True(features.HasFlag(LibraryFeatures.Compression));
        Assert.True(features.HasFlag(LibraryFeatures.Sha1));
        Assert.True(features.HasFlag(LibraryFeatures.HttpParser));
        Assert.True(features.HasFlag(LibraryFeatures.Regex));
        Assert.True(features.HasFlag(LibraryFeatures.Nsec));
        Assert.False(features.HasFlag(LibraryFeatures.Sha256));
        Assert.False(features.HasFlag(LibraryFeatures.Ssh));
    }

    [Fact]
    public void VersionPrerelease_IsEmptyForStable()
    {
        Assert.Equal(string.Empty, LibraryInit.VersionPrerelease);
    }
}

public class SettingsTests
{
    [Fact]
    public void StrictObjectCreation_DefaultIsTrueAndIsMutablePerContext()
    {
        // Strictness flags are per-context; each context gets its own isolated settings with the default value of true.
        using var ctx = new GitContext();
        Assert.True(ctx.Settings.StrictObjectCreation);

        ctx.Settings.StrictObjectCreation = false;
        Assert.False(ctx.Settings.StrictObjectCreation);
        ctx.Settings.StrictObjectCreation = true;
        Assert.True(ctx.Settings.StrictObjectCreation);
    }

    [Fact]
    public void StrictHashVerification_DefaultIsTrue()
    {
        using var ctx = new GitContext();
        Assert.True(ctx.Settings.StrictHashVerification);
    }

    [Fact]
    public void OwnerValidation_DefaultIsTrue()
    {
        using var ctx = new GitContext();
        Assert.True(ctx.Settings.OwnerValidation);
    }

    [Fact]
    public void GetSearchPath_System_ReturnsEtc()
    {
        using var ctx = new GitContext();
        Assert.Equal("/etc", ctx.Dirs.Get(GitSystemDir.System));
    }

    [Fact]
    public void LibraryOption_EnumContainsExpectedKeys()
    {
        // Spot-check that the enum matches libgit2's GIT_OPT_* values.
        Assert.Equal(0, (int)LibraryOption.GetMwindowSize);
        Assert.Equal(4, (int)LibraryOption.GetSearchPath);
        Assert.Equal(14, (int)LibraryOption.EnableStrictObjectCreation);
        Assert.Equal(45, (int)LibraryOption.AddSslX509Cert);
    }

    [Fact]
    public void ConfigLevel_Values_MatchLibgit2()
    {
        // Matches git_config_level_t in include/git2/config.h.
        Assert.Equal(1, (int)GitConfigLevel.ProgramData);
        Assert.Equal(2, (int)GitConfigLevel.System);
        Assert.Equal(3, (int)GitConfigLevel.Xdg);
        Assert.Equal(4, (int)GitConfigLevel.Global);
        Assert.Equal(5, (int)GitConfigLevel.Local);
        Assert.Equal(6, (int)GitConfigLevel.Worktree);
        Assert.Equal(7, (int)GitConfigLevel.App);
        Assert.Equal(-1, (int)GitConfigLevel.Highest);
    }
}
