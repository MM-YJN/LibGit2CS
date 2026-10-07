using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.IO;

public sealed class SystemDirsTests
{
    [Fact]
    public void Get_System_ReturnsEtc()
    {
        using var ctx = new GitContext();
        Assert.Equal("/etc", ctx.Dirs.Get(GitSystemDir.System));
    }

    [Fact]
    public void Get_Template_ReturnsDefaultTemplatePath()
    {
        using var ctx = new GitContext();
        Assert.Equal("/usr/share/git-core/templates", ctx.Dirs.Get(GitSystemDir.Template));
    }

    [Fact]
    public void Get_ProgramData_ReturnsEmptyOnPosix()
    {
        using var ctx = new GitContext();
        Assert.Equal(string.Empty, ctx.Dirs.Get(GitSystemDir.ProgramData));
    }

    [Fact]
    public void Set_OverridesCachedValue()
    {
        using var ctx = new GitContext();
        ctx.Dirs.Set(GitSystemDir.System, "/custom/etc");

        Assert.Equal("/custom/etc", ctx.Dirs.Get(GitSystemDir.System));
    }

    [Fact]
    public void Set_NullResetsToDefault()
    {
        using var ctx = new GitContext();
        ctx.Dirs.Set(GitSystemDir.System, "/custom/etc");
        ctx.Dirs.Set(GitSystemDir.System, null);

        Assert.Equal("/etc", ctx.Dirs.Get(GitSystemDir.System));
    }

    [Fact]
    public void Reset_ClearsAllOverrides()
    {
        using var ctx = new GitContext();
        ctx.Dirs.Set(GitSystemDir.System, "/custom/etc");
        ctx.Dirs.Set(GitSystemDir.Global, "/custom/home");
        ctx.Dirs.Reset();

        Assert.Equal("/etc", ctx.Dirs.Get(GitSystemDir.System));
    }

    [Fact]
    public void ExpandGlobalFile_JoinsDirAndFilename()
    {
        using var ctx = new GitContext();
        ctx.Dirs.Set(GitSystemDir.Global, "/home/test");

        Assert.Equal("/home/test/.gitconfig", ctx.Dirs.ExpandGlobalFile(".gitconfig"));
    }

    [Fact]
    public void ExpandHomedirFile_JoinsDirAndFilename()
    {
        using var ctx = new GitContext();
        ctx.Dirs.Set(GitSystemDir.Home, "/home/test");

        Assert.Equal("/home/test/.gitconfig", ctx.Dirs.ExpandHomedirFile(".gitconfig"));
    }

    [Fact]
    public void ConfigLevel_HasCorrectValues()
    {
        Assert.Equal(0, (int)GitConfigLevel.None);
        Assert.Equal(1, (int)GitConfigLevel.ProgramData);
        Assert.Equal(2, (int)GitConfigLevel.System);
        Assert.Equal(5, (int)GitConfigLevel.Local);
    }
}
