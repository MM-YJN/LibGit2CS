using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Config;

/// <summary> Parity tests for the config subsystem. </summary>
public sealed class ConfigMedParityTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigMedParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigMed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private string NewDir()
    {
        string dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static GitContext NewContext()
    {
        GitContext ctx = new();
        ctx.Env["HOME"] = Path.Combine(Path.GetTempPath(), "LibGit2CS_nonexistent_" + Guid.NewGuid().ToString("N"));
        ctx.Env["XDG_CONFIG_HOME"] = null;
        ctx.Dirs.Reset();
        ctx.Dirs.Set(GitSystemDir.System, string.Empty);
        ctx.Dirs.Set(GitSystemDir.ProgramData, string.Empty);
        return ctx;
    }

    private async Task<GitRepository> InitRepoAsync(string? configContent = null)
    {
        string dir = NewDir();
        GitRepository repo = await GitRepository.InitAsync(dir, isBare: false, NewContext());
        if (configContent is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(repo.Path, "config"), configContent, cancellationToken: TestContext.Current.CancellationToken);
            await repo.DisposeAsync();
            return await GitRepository.OpenAsync(Path.Combine(dir, ".git"), NewContext(), TestContext.Current.CancellationToken);
        }

        return repo;
    }

    // ── bool parsing ────────────────────────────────────────────────

    [Fact]
    public void TryParseBool_Null_IsTrue()
    {
        // C (util.c:650): "A missing value means true".
        Assert.True(ConfigurationValueParser.TryParseBool((string?)null, out bool result));
        Assert.True(result);
    }

    [Fact]
    public void TryParseBool_Empty_IsFalse()
    {
        // C (util.c:660): value[0] == '\\0' → false.
        Assert.True(ConfigurationValueParser.TryParseBool("", out bool result));
        Assert.False(result);
    }

    // ── blank line in a multiline value is SKIPPED, not terminating ────

    [Fact]
    public async Task MultilineValue_BlankLine_IsSkipped()
    {
        // C (config_parse.c:345-374) strips a blank continuation line to "" and SKIPS it like a comment-only line ("pretend it didn't
        // exist"); only EOF terminates the value. C-verified via differential probe: the blank line is skipped and the NEXT line is appended, and
        // no `key2` variable exists.
        string config = "[section]\n\tkey = one\\\n\n\tkey2 = after\n";
        await using GitRepository repo = await InitRepoAsync(config);

        string? value = await repo.Config.GetStringAsync("section.key", TestContext.Current.CancellationToken);
        Assert.Equal("one\tkey2 = after", value);

        // The line after the blank continuation was swallowed into `key`.
        string? value2 = await repo.Config.GetStringAsync("section.key2", TestContext.Current.CancellationToken);
        Assert.Null(value2);
    }

    [Fact]
    public async Task MultilineValue_BlankLineAtEof_EndsValue()
    {
        // A blank line immediately before EOF still skips; the value ends only
        // at EOF (C probe "blank-end": value = "line1 ").
        string config = "[section]\n\tkey = one\\\n\n";
        await using GitRepository repo = await InitRepoAsync(config);

        string? value = await repo.Config.GetStringAsync("section.key", TestContext.Current.CancellationToken);
        Assert.Equal("one", value);
    }

    [Fact]
    public async Task MultilineValue_WhitespaceOnlyLine_IsSkipped()
    {
        // A whitespace-only continuation line is stripped to "" and skipped.
        string config = "[section]\n\tkey = one\\\n   \n\tcontinued\n";
        await using GitRepository repo = await InitRepoAsync(config);

        string? value = await repo.Config.GetStringAsync("section.key", TestContext.Current.CancellationToken);
        Assert.Equal("one\tcontinued", value);
    }

    [Fact]
    public async Task MultilineValue_CommentLine_IsSkipped()
    {
        // C: comment-only lines are skipped ("pretend it didn't exist") — the
        // multiline continues; the continuation line's leading whitespace is
        // kept (unescape_line, C-verified "one\tcontinued").
        string config = "[section]\n\tkey = one\\\n\t# a comment\n\tcontinued\n";
        await using GitRepository repo = await InitRepoAsync(config);

        string? value = await repo.Config.GetStringAsync("section.key", TestContext.Current.CancellationToken);
        Assert.Equal("one\tcontinued", value);
    }

    // ── byte-preserving config IO ──────────────────────────────────────

    [Fact]
    public async Task ConfigFile_NonUtf8Bytes_RoundTrip()
    {
        // 0xE9 alone is invalid UTF-8 — C reads/writes raw bytes. The entry carries the raw bytes (ValueBytes) while the string tier is the UTF-8 display
        // decode (U+FFFD for the lone 0xE9 byte). Built from explicit bytes — a string literal would UTF-8-encode the char.
        byte[] raw = [.. "[section]\n\tkey = caf"u8, 0xE9, .. "\n"u8];
        string dir = NewDir();
        string repoPath = Path.Combine(dir, "repo");
        Directory.CreateDirectory(repoPath);
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, NewContext(), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(repo.Path, "config"), raw, cancellationToken: TestContext.Current.CancellationToken);

        await using GitRepository reopened = await GitRepository.OpenAsync(Path.Combine(repoPath, ".git"), NewContext(), TestContext.Current.CancellationToken);
        string? value = await reopened.Config.GetStringAsync("section.key", TestContext.Current.CancellationToken);
        Assert.Equal("caf\uFFFD", value);

        // Write back and verify byte-exactness of the raw 0xE9 value.
        await reopened.Config.SetStringAsync("section.other", "v\u00e9", TestContext.Current.CancellationToken);
        byte[] after = await File.ReadAllBytesAsync(Path.Combine(repoPath, ".git", "config"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains((byte)0xE9, after); // the untouched raw 0xE9 value survives
        Assert.Contains(new byte[] { 0xC3, 0xA9 }, after); // the new value is UTF-8 é
    }

    // ── multivar on a missing key → ENOTFOUND ──────────────────────────

    [Fact]
    public async Task GetMulti_MissingKey_ThrowsNotFound()
    {
        await using GitRepository repo = await InitRepoAsync();

        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await repo.Config.GetMultiAsync("remote.origin.fetch", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    // ── empty include.path errors ─────────────────────────────────────

    [Fact]
    public async Task Include_EmptyPath_Fails()
    {
        string config = "[include]\n\tpath =\n";
        string dir = NewDir();
        string repoPath = Path.Combine(dir, "repo");
        Directory.CreateDirectory(repoPath);
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, NewContext(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "config"), config, cancellationToken: TestContext.Current.CancellationToken);

        // C (config_file.c:564-591): an empty include.path reads the parent
        // directory and the load fails.
        await Assert.ThrowsAsync<GitException>(async () =>
            await GitRepository.OpenAsync(Path.Combine(repoPath, ".git"), NewContext(), TestContext.Current.CancellationToken));
    }

    // ── foreach_match uses an unanchored regex ────────────────────────

    [Fact]
    public async Task Enumerate_RegexSemantics_Unanchored()
    {
        string config = "[core]\n\tautocrlf = true\n[user]\n\tname = X\n";
        await using GitRepository repo = await InitRepoAsync(config);

        // "autocrlf" as an unanchored regex matches core.autocrlf.
        var names = new List<string>();
        await foreach (GitConfigEntry ce in repo.Config.EnumerateAsync("autocrlf", TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            names.Add(ce.Name);
        }

        Assert.Contains("core.autocrlf", names);
    }

    // ── include "~\foo" is a relative path, not home ────────

    [Fact]
    public async Task Include_BackslashPath_NotExpandedFromHome()
    {
        // C (config_file.c:526-527): only "~/" is expanded from home; "~\foo"
        // is treated as a relative path joined to the config file's directory.
        string dir = NewDir();
        string repoPath = Path.Combine(dir, "repo");
        Directory.CreateDirectory(repoPath);
        GitContext ctx = NewContext();
        ctx.Env["HOME"] = NewDir(); // a real home dir
        ctx.Dirs.Reset();
        await using GitRepository repo = await GitRepository.InitAsync(repoPath, isBare: false, ctx, TestContext.Current.CancellationToken);

        string gitDir = repo.Path;
        // The include file at the relative path "~\foo" in the git dir. On
        // Windows the backslash is a path separator, so the file lives in a
        // "~" subdirectory (create it); on POSIX it is a literal filename
        // character in the gitdir.
        string includePath = Path.Combine(gitDir, "~\\foo");
        Directory.CreateDirectory(Path.GetDirectoryName(includePath)!);
        await File.WriteAllTextAsync(includePath, "[user]\n\tname = Included\n", cancellationToken: TestContext.Current.CancellationToken);
        // The config value "~\foo" is written with the backslash escaped as
        // "\\" (git config escape syntax). On Windows the path separator is
        // already a backslash, so the value is "~\\foo" → escaped "~\\\\foo".
        string escaped = "~\\\\foo";
        await File.WriteAllTextAsync(Path.Combine(gitDir, "config"), $"[include]\n\tpath = {escaped}\n", cancellationToken: TestContext.Current.CancellationToken);

        await using GitRepository reopened = await GitRepository.OpenAsync(Path.Combine(repoPath, ".git"), ctx, TestContext.Current.CancellationToken);
        string? name = await reopened.Config.GetStringAsync("user.name", TestContext.Current.CancellationToken);
        Assert.Equal("Included", name);
    }

    // ── writes with no writable backend → GIT_EREADONLY ───────────────

    [Fact]
    public async Task SetString_NoWritableBackend_ThrowsReadOnly()
    {
        using GitContext ctx = NewContext();
        await using var config = new GitConfiguration(ctx);
        await config.AddBackendAsync(new MemoryConfigBackend("[user]\n\tname = B\n"), GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        // C (config.c:715-725): "cannot set '%s': the configuration is
        // read-only", GIT_EREADONLY.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await config.SetStringAsync("user.email", "a@b.c", TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.ReadOnly, ex.Code);
        Assert.Contains("the configuration is read-only", ex.Message);
    }
}
