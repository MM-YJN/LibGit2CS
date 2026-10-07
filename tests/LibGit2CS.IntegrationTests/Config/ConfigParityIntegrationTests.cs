using System.Text.RegularExpressions;

using LibGit2CS.Config;
using LibGit2CS.Core;

namespace LibGit2CS.IntegrationTests.Config;

/// <summary>
/// End-to-end tests for the config parity behaviors
/// (base-0 integer parsing, set/delete error paths for
/// multivar/included/missing keys, writer byte-parity, and lone-variable
/// get_string) in libgit2 1.9.4. All expected
/// outputs are C-verified against libgit2 1.9.4.
/// </summary>
public sealed partial class ConfigParityIntegrationTests : IDisposable
{
    [GeneratedRegex(".*")]
    private static partial Regex AnyRegex();

    [GeneratedRegex("^zz$")]
    private static partial Regex ZzRegex();

    [GeneratedRegex("^9$")]
    private static partial Regex NineRegex();

    private readonly string _tempDir;

    public ConfigParityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigParityInt_" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public async Task GetInt64_BaseZero_MatchesC()
    {
        // git_config_parse_int64 uses git__strntol64(..., base 0).
        string path = WriteFile("i.conf", "[x]\n\ti = 0x10\n\to = 010\n\tb = 09\n\tw =  5\n\tneg = -0x10\n");
        using GitContext ctx = new();
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, ctx, TestContext.Current.CancellationToken);
        Assert.Equal(16, await config.GetInt64Async("x.i", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(8, await config.GetInt64Async("x.o", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, await config.GetInt64Async("x.b", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(5, await config.GetInt64Async("x.w", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(-16, await config.GetInt64Async("x.neg", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetString_LoneVariable_ReturnsEmpty()
    {
        // git_config_get_string maps a lone variable to "".
        string path = WriteFile("lone.conf", "[x]\n\tlone\n");
        using GitContext ctx = new();
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, ctx, TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, await config.GetStringAsync("x.lone", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await config.GetStringAsync("x.nope", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Set_MultivarKey_ThrowsNotUnique_FileUntouched()
    {
        // config_file_set refuses a multivar key before any write.
        const string content = "[x]\n\ta = 1\n\ta = 2\n";
        string path = WriteFile("multi.conf", content);
        using GitContext ctx = new();
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, ctx, TestContext.Current.CancellationToken);
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
        using GitContext ctx = new();
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, ctx, TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await config.DeleteAsync("x.a", TestContext.Current.CancellationToken));
        Assert.Equal("entry is not unique due to being a multivar", ex.Message);
        Assert.Equal(content, await ReadAllAsync(path));
    }

    [Fact]
    public async Task Delete_MissingKey_ThrowsNotFound()
    {
        // "could not find key '%s' to delete".
        string path = WriteFile("d.conf", "[x]\n\ta = 1\n");
        using GitContext ctx = new();
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, ctx, TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await config.DeleteAsync("x.nope", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
        Assert.Equal("could not find key 'x.nope' to delete", ex.Message);

        GitException ex2 = await Assert.ThrowsAsync<GitException>(async () =>
            await config.DeleteMultiAsync("x.nope", AnyRegex(), TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex2.Code);
        Assert.Equal("could not find key 'x.nope' to delete", ex2.Message);
    }

    [Fact]
    public async Task Delete_IncludedKey_ThrowsNotUnique()
    {
        string main = WriteFile("main.conf", "[include]\n\tpath = inc.conf\n[x]\n\ta = 1\n");
        _ = WriteFile("inc.conf", "[x]\n\tk = frominc\n");
        using GitContext ctx = new();
        await using GitConfiguration config = await GitConfiguration.OpenAsync(main, ctx, TestContext.Current.CancellationToken);
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await config.DeleteAsync("x.k", TestContext.Current.CancellationToken));
        Assert.Equal("entry is not unique due to being included", ex.Message);
    }

    [Fact]
    public async Task Set_WritesCallerKeyCase()
    {
        // write_value uses the caller's variable case.
        string path = WriteFile("c.conf", "[Core]\n\tBare = true\n");
        using GitContext ctx = new();
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, ctx, TestContext.Current.CancellationToken);
        await config.SetStringAsync("core.Bare", "false", TestContext.Current.CancellationToken);
        Assert.Equal("[Core]\n\tBare = false\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_NewSection_NoBlankLine_AndTrailingNewline()
    {
        // No blank line before a new section; final line gets a newline.
        string path = WriteFile("b.conf", "[x]\n\ta = 1");
        using GitContext ctx = new();
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, ctx, TestContext.Current.CancellationToken);
        await config.SetStringAsync("sec.c", "v", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = 1\n[sec]\n\tc = v\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_MultilineValue_ReplacesWithSingleLine()
    {
        // Continuation lines are consumed by the parser-driven writer.
        string path = WriteFile("ml.conf", "[x]\n\tkey = a\\\n\tb\n\tother = z\n");
        using GitContext ctx = new();
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, ctx, TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.key", "new", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\tkey = new\n\tother = z\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_HeaderLineVariable_AppendsValue()
    {
        // The header-line variable is matched; the value is appended
        // (the original "[x] key = 1" line stays, exactly like C).
        string path = WriteFile("hd.conf", "[x] key = 1\n");
        using GitContext ctx = new();
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, ctx, TestContext.Current.CancellationToken);
        await config.SetStringAsync("x.key", "2", TestContext.Current.CancellationToken);
        Assert.Equal("[x] key = 1\n\tkey = 2\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_NewSubsection_PreservesCase()
    {
        string path = WriteFile("nc.conf", string.Empty);
        using GitContext ctx = new();
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, ctx, TestContext.Current.CancellationToken);
        await config.SetStringAsync("Branch.Master.remote", "origin", TestContext.Current.CancellationToken);
        Assert.Equal("[Branch \"Master\"]\n\tremote = origin\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task Set_CommentAtEof_FlushedBeforeNewSection()
    {
        string path = WriteFile("t.conf", "[x]\n\ta = 1\n# tail\n");
        using GitContext ctx = new();
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, ctx, TestContext.Current.CancellationToken);
        await config.SetStringAsync("sec.c", "v", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = 1\n# tail\n[sec]\n\tc = v\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task SetMulti_ValueWrittenRaw_AndNoMatchAppends()
    {
        // C passes the multivar value to config_file_write unescaped; a
        // non-matching regexp appends the entry at the section end.
        string path = WriteFile("mv.conf", "[x]\n\ta = 1\n");
        using GitContext ctx = new();
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, ctx, TestContext.Current.CancellationToken);
        await config.SetMultiAsync("x.a", ZzRegex(), "9", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = 1\n\ta = 9\n", await ReadAllAsync(path));

        await config.SetMultiAsync("x.a", NineRegex(), "ta\tb\"c", TestContext.Current.CancellationToken);
        Assert.Equal("[x]\n\ta = 1\n\ta = ta\tb\"c\n", await ReadAllAsync(path));
    }

    [Fact]
    public async Task RenameSection_MultivarRefspecs_RenamesAllValues()
    {
        // git_config_rename_section uses set_multivar/delete_multivar, so
        // multi-valued keys (e.g. remote refspecs) rename without the
        // single-key uniqueness error.
        string path = WriteFile("r.conf", "[remote \"origin\"]\n\turl = https://example.com/r.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n\tfetch = +refs/tags/*:refs/tags/*\n");
        using GitContext ctx = new();
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, ctx, TestContext.Current.CancellationToken);
        await config.RenameSectionAsync("remote.origin", "remote.upstream", TestContext.Current.CancellationToken);

        Assert.Equal("https://example.com/r.git", await config.GetStringAsync("remote.upstream.url", cancellationToken: TestContext.Current.CancellationToken));
        IReadOnlyList<string> fetches = await config.GetMultiAsync("remote.upstream.fetch", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, fetches.Count);
        Assert.Contains("+refs/heads/*:refs/remotes/origin/*", fetches);
        Assert.Contains("+refs/tags/*:refs/tags/*", fetches);
        Assert.Null(await config.GetStringAsync("remote.origin.url", cancellationToken: TestContext.Current.CancellationToken));
    }
}
