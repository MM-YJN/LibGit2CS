using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Config;

// Parity cases verified against libgit2 1.9.4:
//  - `[]` empty section header was accepted, producing keys with an
//    empty section component; C rejects it ("unexpected character in
//    header", config_parse.c:191-205).
//  - an extra trailing `]` after a quoted subsection (`[a "b"]]`) was
//    silently swallowed; C re-processes it as a variable and rejects it
//    (config_parse.c:150, 401-404).
//  - onbranch: includeIf with a missing/unreadable HEAD silently
//    yielded no match; C fails the entire config load
//    (config_file.c:680-682).
//  - RenameSectionAsync built its match regex from the raw old section
//    name without regex-escaping; C escapes it (config.c:1629-1632).
public sealed class ConfigLowRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigLowRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigLow_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ---- empty section header is rejected ----

    [Fact]
    public async Task Parser_EmptySectionHeader_Throws()
    {
        // C's parse_section_header reads the char after '[' and rejects a
        // ']' there ("unexpected character in header", config_parse.c:191-205).
        // `[]` must be rejected rather than parsed as an empty section name
        // storing keys like ".x".
        await Assert.ThrowsAsync<GitException>(() => ParseAsync("[]\n\tx = 1\n"));
    }

    [Fact]
    public async Task Parser_NormalSectionHeader_StillParses()
    {
        // Control: a normal header parses.
        List<(string? section, string name, string? value)> entries = await ParseAsync("[core]\n\tx = 1\n");
        Assert.Single(entries);
        Assert.Equal("core.x", entries[0].name);
    }

    // ---- extra trailing ']' after a quoted subsection is rejected ----

    [Fact]
    public async Task Parser_ExtraTrailingBracket_Throws()
    {
        // C's parse_subsection_header returns the offset right after the
        // FIRST ']' following the closing quote (config_parse.c:150); the
        // second ']' is re-processed as a variable and rejected ("invalid
        // configuration key", config_parse.c:401-404). Advancing past both
        // brackets would accept the section.
        await Assert.ThrowsAsync<GitException>(() => ParseAsync("[a \"b\"]]\n"));
    }

    [Fact]
    public async Task Parser_QuotedSubsection_StillParses()
    {
        // Control: a well-formed quoted subsection parses.
        List<(string? section, string name, string? value)> entries = await ParseAsync("[a \"b\"]\n\tx = 1\n");
        Assert.Single(entries);
        Assert.Equal("a.b.x", entries[0].name);
    }

    // ---- onbranch includeIf with a missing HEAD fails the load ----

    [Fact]
    public async Task IncludeIf_OnBranch_MissingHead_FailsConfigLoad()
    {
        // C's conditional_match_onbranch propagates the git_futils_readbuffer
        // failure (config_file.c:680-682), aborting the whole config read.
        // Returning false would load the config minus the
        // conditional include.
        string repoDir = Path.Combine(_tempDir, "repo6");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoDir, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        // Delete HEAD — the repo is now missing its HEAD file.
        File.Delete(Path.Combine(repo.Path, "HEAD"));

        string extra = Path.Combine(_tempDir, "extra6.config");
        await File.WriteAllTextAsync(extra, "[marker]\n\tpresent = yes\n", TestContext.Current.CancellationToken);

        string cfg = $"[includeIf \"onbranch:main\"]\n\tpath = {extra.Replace("\\", "\\\\")}\n";
        string cfgPath = Path.Combine(_tempDir, "inc6.config");
        await File.WriteAllTextAsync(cfgPath, cfg, TestContext.Current.CancellationToken);

        await using GitConfiguration config = new(repo.Context);
        // The load is eager: the missing HEAD fails AddFileOnDiskAsync itself.
        await Assert.ThrowsAsync<GitException>(
            () => config.AddFileOnDiskAsync(cfgPath, GitConfigLevel.Local, repoGitDirPath: repo.Path, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IncludeIf_OnBranch_WithHead_StillApplies()
    {
        // Control: with a valid HEAD the conditional include applies.
        string repoDir = Path.Combine(_tempDir, "repo6b");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoDir, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        // Point HEAD at "main" explicitly (the init default branch varies).
        await File.WriteAllTextAsync(
            Path.Combine(repo.Path, "HEAD"), "ref: refs/heads/main\n",
            TestContext.Current.CancellationToken);

        string extra = Path.Combine(_tempDir, "extra6b.config");
        await File.WriteAllTextAsync(extra, "[marker]\n\tpresent = yes\n", TestContext.Current.CancellationToken);

        string cfg = $"[includeIf \"onbranch:main\"]\n\tpath = {extra.Replace("\\", "\\\\")}\n";
        string cfgPath = Path.Combine(_tempDir, "inc6b.config");
        await File.WriteAllTextAsync(cfgPath, cfg, TestContext.Current.CancellationToken);

        await using GitConfiguration config = new(repo.Context);
        await config.AddFileOnDiskAsync(cfgPath, GitConfigLevel.Local, repoGitDirPath: repo.Path, cancellationToken: TestContext.Current.CancellationToken);

        string? present = await config.GetStringAsync("marker.present", TestContext.Current.CancellationToken);
        Assert.Equal("yes", present);
    }

    // ---- RenameSectionAsync escapes the old section name ----

    [Fact]
    public async Task RenameSection_SubsectionWithRegexMetachar_MovesOnlyExactKeys()
    {
        string repoDir = Path.Combine(_tempDir, "repo7");
        await using GitRepository repo = await GitRepository.InitAsync(
            repoDir, isBare: false, new GitContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        // [foo "a.b"] — the '.' in the subsection is a regex metacharacter.
        // C escapes the old section name (config.c:1629-1632), so only the
        // exact foo.a.b keys move; a pattern like "foo.a.b.*" would also
        // match foo.axb.y ('.' matches 'x').
        string cfgPath = Path.Combine(_tempDir, "cfg7.config");
        await File.WriteAllTextAsync(
            cfgPath,
            "[foo \"a.b\"]\n\tx = 1\n[foo \"axb\"]\n\ty = 2\n",
            TestContext.Current.CancellationToken);

        await using GitConfiguration config = new(repo.Context);
        await config.AddFileOnDiskAsync(cfgPath, GitConfigLevel.Local, repoGitDirPath: repo.Path, cancellationToken: TestContext.Current.CancellationToken);

        await config.RenameSectionAsync("foo.a.b", "bar", TestContext.Current.CancellationToken);

        Assert.Equal("1", await config.GetStringAsync("bar.x", TestContext.Current.CancellationToken));
        Assert.Null(await config.GetStringAsync("foo.a.b.x", TestContext.Current.CancellationToken));
        // The unrelated subsection must NOT have been moved.
        Assert.Equal("2", await config.GetStringAsync("foo.axb.y", TestContext.Current.CancellationToken));
        Assert.Null(await config.GetStringAsync("bar.y", TestContext.Current.CancellationToken));
    }

    // ---- helpers ----

    private static async Task<List<(string? section, string name, string? value)>> ParseAsync(string content)
    {
        // The parser operates on raw bytes; the string input is UTF-8-encoded.
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        var results = new List<(string?, string, string?)>();
        await ConfigParser.ParseAsync(
            path: "test",
            content: bytes,
            onSection: (section, line, _) => Task.CompletedTask,
            onVariable: (section, name, value, line, _) =>
            {
                string? sectionStr = section is { } s ? Encoding.UTF8.GetString(s.Span) : null;
                string nameStr = Encoding.UTF8.GetString(name.Span);
                string? valueStr = value is { } v ? Encoding.UTF8.GetString(v.Span) : null;
                string fqName = sectionStr is null
                    ? nameStr.ToLowerInvariant()
                    : $"{sectionStr}.{nameStr.ToLowerInvariant()}";
                results.Add((sectionStr, fqName, valueStr));
                return Task.CompletedTask;
            },
            onComment: (line, _) => Task.CompletedTask,
            onEof: _ => Task.CompletedTask);
        return results;
    }
}
