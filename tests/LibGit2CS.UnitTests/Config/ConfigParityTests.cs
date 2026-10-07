using System.Text.RegularExpressions;

using LibGit2CS.Config;
using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Config;

/// <summary>
/// Regression tests for the config subsystem parity behaviors
/// in libgit2 1.9.4. Every
/// expectation is C-verified against libgit2 1.9.4 via C probe outputs.
/// </summary>
public sealed class ConfigParityTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigParity_" + Guid.NewGuid().ToString("N")[..8]);

    public ConfigParityTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<string> ReadAllAsync(string path)
        => await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

    // ---------------------------------------------------------------
    // integer parsing is base-0 in C (git__strntol64 with base 0)
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("0x10", 16L)]
    [InlineData("010", 8L)]
    [InlineData(" 5", 5L)]
    [InlineData("-0x10", -16L)]
    [InlineData("+010", 8L)]
    [InlineData("0x10k", 16384L)]
    [InlineData("0x10K", 16384L)]
    public void TryParseInt64_BaseZero_MatchesC(string value, long expected)
    {
        Assert.True(ConfigurationValueParser.TryParseInt64(value, out long result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("09")]     // octal: '9' is not a valid digit
    [InlineData("0x")]     // bare prefix → base 8, then invalid suffix
    [InlineData("0xg")]    // hex prefix with no digits
    [InlineData("")]       // empty
    [InlineData("-")]      // sign only
    [InlineData("999999999999999999999999")] // overflow
    public void TryParseInt64_Invalid_MatchesC(string value)
    {
        Assert.False(ConfigurationValueParser.TryParseInt64(value, out _));
    }

    [Fact]
    public async Task GetInt64_EndToEnd_MatchesC()
    {
        string path = WriteFile("i.conf", "[x]\n\ti = 0x10\n\to = 010\n\tb = 09\n\tw =  5\n\tneg = -0x10\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(16, await config.GetInt64Async("x.i", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(8, await config.GetInt64Async("x.o", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, await config.GetInt64Async("x.b", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(5, await config.GetInt64Async("x.w", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(-16, await config.GetInt64Async("x.neg", cancellationToken: TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------
    // get_string of a lone variable returns "" in C
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetString_LoneVariable_ReturnsEmptyString()
    {
        string path = WriteFile("lone.conf", "[x]\n\tlone\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, await config.GetStringAsync("x.lone", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetString_MissingKey_ReturnsNull()
    {
        string path = WriteFile("m.conf", "[x]\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await config.GetStringAsync("x.nope", cancellationToken: TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------
    // set/delete of a single key on a multivar or included key errors
    // ---------------------------------------------------------------

    [Fact]
    public async Task Set_MultivarKey_ThrowsNotUnique_FileUntouched()
    {
        const string content = "[x]\n\ta = 1\n\ta = 2\n";
        string path = WriteFile("multi.conf", content);
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await config.SetStringAsync("x.a", "3", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("entry is not unique due to being a multivar", ex.Message);
        Assert.Equal(content, await ReadAllAsync(path));
    }

    [Fact]
    public async Task Delete_MultivarKey_ThrowsNotUnique_FileUntouched()
    {
        const string content = "[x]\n\ta = 1\n\ta = 2\n";
        string path = WriteFile("multi.conf", content);
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await config.DeleteAsync("x.a", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("entry is not unique due to being a multivar", ex.Message);
        Assert.Equal(content, await ReadAllAsync(path));
    }

    [Fact]
    public async Task Delete_IncludedKey_ThrowsNotUnique()
    {
        string main = WriteFile("main.conf", "[include]\n\tpath = inc.conf\n[x]\n\ta = 1\n");
        _ = WriteFile("inc.conf", "[x]\n\tk = frominc\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(main, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await config.DeleteAsync("x.k", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.Error, ex.Code);
        Assert.Equal("entry is not unique due to being included", ex.Message);
    }

    [Fact]
    public async Task Delete_MissingKey_ThrowsNotFound()
    {
        string path = WriteFile("d.conf", "[x]\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await config.DeleteAsync("x.nope", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("could not find key 'x.nope' to delete", ex.Message);
    }

    // ---------------------------------------------------------------
    // delete_multivar on a missing key errors
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteMulti_MissingKey_ThrowsNotFound()
    {
        string path = WriteFile("dm.conf", "[x]\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await config.DeleteMultiAsync("x.nope", new Regex(".*"), TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("could not find key 'x.nope' to delete", ex.Message);
    }

    [Fact]
    public async Task DeleteMulti_NoMatch_FileUnchanged()
    {
        const string content = "[x]\n\ta = 1\n";
        string path = WriteFile("dn.conf", content);
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.DeleteMultiAsync("x.a", new Regex("^zz$"), TestContext.Current.CancellationToken);
        Assert.Equal(content, await ReadAllAsync(path));
    }

    // ---------------------------------------------------------------
    // writer byte-level parity with config_file_write
    // ---------------------------------------------------------------

    [Fact]
    public async Task Set_WritesCallerKeyCase()
    {
        string path = WriteFile("c.conf", "[Core]\n\tBare = true\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("core.Bare", "false", TestContext.Current.CancellationToken);
        Assert.Equal("[Core]\n\tBare = false\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_NewSection_NoBlankLineBefore()
    {
        string path = WriteFile("b.conf", "[x]\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("sec.c", "v", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = 1\n[sec]\n\tc = v\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_AppendsTrailingNewline()
    {
        string path = WriteFile("tn.conf", "[x]\n\ta = 1");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.a", "2", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = 2\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_MultilineValue_ReplacesWithSingleLine()
    {
        string path = WriteFile("ml.conf", "[x]\n\tkey = a\\\n\tb\n\tother = z\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.key", "new", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\tkey = new\n\tother = z\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_MultilineUnmatched_Preserved()
    {
        string path = WriteFile("mu.conf", "[x]\n\tkey = a\\\n\tb\n\tother = z\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.other", "q", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\tkey = a\\\n\tb\n\tother = q\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_HeaderLineVariable_AppendsValue()
    {
        string path = WriteFile("hd.conf", "[x] key = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.key", "2", TestContext.Current.CancellationToken);
        Assert.Equal("[x] key = 1\n\tkey = 2\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_NewSubsection_PreservesCase()
    {
        string path = WriteFile("nc.conf", string.Empty);
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("Branch.Master.remote", "origin", TestContext.Current.CancellationToken);
        Assert.Equal("[Branch \"Master\"]\n\tremote = origin\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_SubsectionExisting_PreservesCase()
    {
        string path = WriteFile("s.conf", "[Branch \"Master\"]\n\tremote = r\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("branch.Master.remote", "new", TestContext.Current.CancellationToken);
        Assert.Equal("[Branch \"Master\"]\n\tremote = new\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_CommentBeforeVar_Preserved()
    {
        string path = WriteFile("i.conf", "# c\n[x]\n\t# inner\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.a", "2", TestContext.Current.CancellationToken);
        Assert.Equal("# c\n[x]\n\t# inner\n\ta = 2\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_CommentAtEof_FlushedBeforeNewSection()
    {
        string path = WriteFile("t.conf", "[x]\n\ta = 1\n# tail\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("sec.c", "v", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = 1\n# tail\n[sec]\n\tc = v\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_SpacedValue_Quoted()
    {
        string path = WriteFile("q.conf", "[x]\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.a", "  spaced  ", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = \"  spaced  \"\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_SemicolonValue_Quoted()
    {
        string path = WriteFile("q3.conf", "[x]\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.a", "a;b", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = \"a;b\"\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_QuoteValue_Escaped()
    {
        string path = WriteFile("q2.conf", "[x]\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.a", "a\"b", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = a\\\"b\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_TabValue_Escaped()
    {
        string path = WriteFile("e.conf", "[x]\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.a", "tab\there", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = tab\\there\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_EmptyValue_WrittenQuotedEmpty()
    {
        string path = WriteFile("ev.conf", "[x]\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.a", string.Empty, TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = \"\"\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Delete_LoneVariable_KeepsSectionHeader()
    {
        string path = WriteFile("d.conf", "[x]\n\tlone\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.DeleteAsync("x.lone", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Delete_CommentBeforeVar_Preserved()
    {
        string path = WriteFile("dc.conf", "[x]\n\t# c\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.DeleteAsync("x.a", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\t# c\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Delete_LastEntry_SectionHeaderStays()
    {
        string path = WriteFile("ds.conf", "[x]\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.DeleteAsync("x.a", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_OnLoneLine_Replaces()
    {
        string path = WriteFile("ls.conf", "[x]\n\tlone\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.lone", "v", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\tlone = v\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task SetMulti_Match_Replaces()
    {
        string path = WriteFile("mv.conf", "[x]\n\ta = 1\n\ta = 2\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetMultiAsync("x.a", new Regex("^1$"), "9", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = 9\n\ta = 2\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task SetMulti_NoMatch_AppendsAtSectionEnd()
    {
        string path = WriteFile("nm.conf", "[x]\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetMultiAsync("x.a", new Regex("^zz$"), "9", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = 1\n\ta = 9\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task SetMulti_ValueWrittenRaw()
    {
        // C passes the multivar value to config_file_write unescaped/unquoted.
        string path = WriteFile("mv2.conf", "[x]\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetMultiAsync("x.a", new Regex("^1$"), "ta\tb\"c", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = ta\tb\"c\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task DeleteMulti_Match_Removes()
    {
        string path = WriteFile("mv3.conf", "[x]\n\ta = 1\n\ta = 2\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.DeleteMultiAsync("x.a", new Regex("^2$"), TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = 1\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_NewKey_TwiceSection_AppendsAtFirstSectionEnd()
    {
        string path = WriteFile("tw.conf", "[x]\n\ta = 1\n[y]\n\tb = 2\n[x]\n\tc = 3\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.nk", "9", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = 1\n\tnk = 9\n[y]\n\tb = 2\n[x]\n\tc = 3\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_NewKey_LoneVarInSection_AppendsAtEnd()
    {
        string path = WriteFile("lo.conf", "[x]\n\tlone\n\ta = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.nk", "9", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\tlone\n\ta = 1\n\tnk = 9\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_CommentsOnly_AppendsSection()
    {
        string path = WriteFile("oc.conf", "# only comment\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("sec.c", "v", TestContext.Current.CancellationToken);
        Assert.Equal("# only comment\n[sec]\n\tc = v\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_BlankLines_Preserved()
    {
        string path = WriteFile("bl.conf", "[x]\n\ta = 1\n\n\tb = 2\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.a", "9", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = 9\n\n\tb = 2\n", await ReadAllAsync(path));
    }
}
