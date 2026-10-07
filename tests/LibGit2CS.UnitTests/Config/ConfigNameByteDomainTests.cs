using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.Config;

/// <summary> Config entry NAMES are byte-primary (<see cref="GitConfigEntry.NameBytes"/>) and <see
/// cref="GitConfiguration.EnumerateAsync"/> matches the regex over the raw name bytes — C's <c>all_iter_glob_next</c> (config.c:493-530) runs
/// <c>git_regexp_match</c> over the raw name bytes. A non-UTF-8 subsection byte must be matchable by a byte-counting pattern and must NOT surface as U+FFFD in
/// the match domain. </summary>
public sealed class ConfigNameByteDomainTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GitSystemDirs _dirs = new();

    public ConfigNameByteDomainTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigNameByteDomain_" + Guid.NewGuid().ToString("N")[..8]);
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

    private string ConfigPath => Path.Combine(_tempDir, "config");

    /// <summary>
    /// Isolated context: an empty HOME/XDG/SYSTEM config environment so config
    /// file discovery never touches the real user config (same shape as
    /// <see cref="ConfigMedParityTests.NewContext"/>).
    /// </summary>
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

    // ── 1: EnumerateAsync matches over raw name bytes ─────────────

    [Fact]
    public async Task Enumerate_NonUtf8Subsection_ByteDomainMatch()
    {
        // A subsection containing a raw 0xE9 byte (invalid UTF-8): the name
        // bytes are "section.<E9>.key". C's all_iter_glob_next matches the
        // raw bytes; the managed implementation must too (the display decode
        // would be U+FFFD).
        byte[] subsection = [0xE9];
        await File.WriteAllBytesAsync(ConfigPath,
            [.. "[section \""u8.ToArray(), .. subsection, .. "\"]\n\tkey = v\n"u8.ToArray()],
            cancellationToken: TestContext.Current.CancellationToken);

        using GitContext ctx = NewContext();
        GitConfiguration config = await GitConfiguration.OpenAsync(ConfigPath, ctx, cancellationToken: TestContext.Current.CancellationToken);
        await using (config.ConfigureAwait(false))
        {
            // The byte-counting pattern "section\..\.key" ('.' = one byte)
            // must match the raw 0xE9 byte — a char-domain match over the
            // U+FFFD display decode would fail (U+FFFD is 3 bytes in UTF-8,
            // and the pattern's '.' counts one byte).
            var names = new List<string>();
            await foreach (GitConfigEntry ce in config.EnumerateAsync(@"section\..\.key", TestContext.Current.CancellationToken).ConfigureAwait(false))
            {
                names.Add(ce.Name);
            }

            Assert.Single(names);
            Assert.Equal("section.\uFFFD.key", names[0]);

            // The entry's NameBytes carry the raw byte. The string tier can no longer reach a non-UTF-8 subsection (the ConfigList head map is byte-keyed — C's
            // strcmp over the raw name bytes); the byte-key API is the parity surface.
            byte[] rawName = [.. "section."u8.ToArray(), 0xE9, .. ".key"u8.ToArray()];
            GitConfigEntry? entry = await config.GetEntryAsync(rawName, TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.NotNull(entry);
            Assert.Equal(rawName, entry.Value.NameBytes.ToArray());
            Assert.Null(await config.GetEntryAsync("section.\uFFFD.key", TestContext.Current.CancellationToken).ConfigureAwait(false));
        }
    }

    [Fact]
    public async Task Enumerate_AsciiPattern_StillMatches()
    {
        // Control: ASCII patterns behave identically to the string tier.
        await File.WriteAllTextAsync(ConfigPath, "[core]\n\tautocrlf = true\n[user]\n\tname = X\n", cancellationToken: TestContext.Current.CancellationToken);

        using GitContext ctx = NewContext();
        GitConfiguration config = await GitConfiguration.OpenAsync(ConfigPath, ctx, cancellationToken: TestContext.Current.CancellationToken);
        await using (config.ConfigureAwait(false))
        {
            var names = new List<string>();
            await foreach (GitConfigEntry ce in config.EnumerateAsync("autocrlf", TestContext.Current.CancellationToken).ConfigureAwait(false))
            {
                names.Add(ce.Name);
            }

            Assert.Contains("core.autocrlf", names);
        }
    }

    [Fact]
    public async Task Enumerate_NoPattern_YieldsAll()
    {
        await File.WriteAllTextAsync(ConfigPath, "[core]\n\tautocrlf = true\n[user]\n\tname = X\n", cancellationToken: TestContext.Current.CancellationToken);

        using GitContext ctx = NewContext();
        GitConfiguration config = await GitConfiguration.OpenAsync(ConfigPath, ctx, cancellationToken: TestContext.Current.CancellationToken);
        await using (config.ConfigureAwait(false))
        {
            int count = 0;
            await foreach (GitConfigEntry _ in config.EnumerateAsync(cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false))
            {
                count++;
            }

            Assert.Equal(2, count);
        }
    }

    // ── 2: NameBytes round-trip through the backends ──────────────

    [Fact]
    public async Task NameBytes_FileBackend_RawSubsectionBytes()
    {
        byte[] subsection = [0xFF, 0xFE];
        await File.WriteAllBytesAsync(ConfigPath,
            [.. "[sec \""u8.ToArray(), .. subsection, .. "\"]\n\tkey = v\n"u8.ToArray()],
            cancellationToken: TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(ConfigPath, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        // the string tier can no longer reach a non-UTF-8 subsection (byte-keyed ConfigList); the byte-key API is the parity surface.
        byte[] rawName = [.. "sec."u8.ToArray(), 0xFF, 0xFE, .. ".key"u8.ToArray()];
        GitConfigEntry? entry = await backend.GetAsync(rawName, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.NotNull(entry);
        Assert.Equal(rawName, entry.Value.NameBytes.ToArray());
        Assert.Equal("sec.\uFFFD\uFFFD.key", entry.Value.Name);
        Assert.Null(await backend.GetAsync("sec.\uFFFD\uFFFD.key", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false));
    }

    [Fact]
    public async Task NameBytes_MemoryBackend_Utf8Encode()
    {
        var backend = new MemoryConfigBackend(["caf\u00e9.key=v"]);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? entry = await backend.GetAsync("caf\u00e9.key", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.NotNull(entry);
        Assert.Equal("caf\u00e9.key"u8.ToArray(), entry.Value.NameBytes.ToArray());
        Assert.Equal("caf\u00e9.key", entry.Value.Name);
    }
}
