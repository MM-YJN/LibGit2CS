using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.Config;

/// <summary> The config boundary is byte-primary with a UTF-8 display tier. These tests pin the byte-parity surface
/// (<c>ValueBytes</c>/<c>GetBytesAsync</c>/<c>SetBytesAsync</c>/ <c>GetMultiBytesAsync</c>), the UTF-8 value egress (<c>SetStringAsync</c> writes <c>C3
/// A9</c>), and the <c>GetPathAsync</c> → <c>GitPath</c> shape over non-UTF-8 values. </summary>
public sealed class ConfigByteBoundaryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GitSystemDirs _dirs = new();

    public ConfigByteBoundaryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigByteBoundary_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── 1: non-UTF-8 config file read → set-bytes → write is byte-exact ──

    [Fact]
    public async Task NonUtf8File_ReadSetBytesWrite_ByteExact()
    {
        // A file with a raw 0xE9 value byte and an untouched sibling line.
        byte[] initial =
        [
            .. "[core]\n"u8.ToArray(),
            .. "\tname = caf"u8.ToArray(), 0xE9, .. "\n"u8.ToArray(),
            .. "\tuntouched = x\n"u8.ToArray(),
        ];
        await File.WriteAllBytesAsync(ConfigPath, initial, cancellationToken: TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(ConfigPath, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        // Read: raw bytes round-trip exactly.
        GitConfigEntry? entry = await backend.GetAsync("core.name", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, entry.Value.ValueBytes.GetValueOrDefault().ToArray());

        // Set-bytes: invalid-UTF-8 replacement written verbatim.
        byte[] replacement = [0xFF, 0xFE, 0xE9];
        await backend.SetBytesAsync("core.name", replacement, cancellationToken: TestContext.Current.CancellationToken);

        byte[] onDisk = await File.ReadAllBytesAsync(ConfigPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ContainsSubsequence(onDisk, [.. "\tname = "u8.ToArray(), 0xFF, 0xFE, 0xE9, (byte)'\n']), $"replacement missing: {Convert.ToHexString(onDisk)}");
        Assert.True(ContainsSubsequence(onDisk, "\tuntouched = x\n"u8.ToArray()), "untouched sibling line must survive byte-exact");
    }

    // ── 2: SetStringAsync("café") writes C3 A9 ─────────────────────

    [Fact]
    public async Task SetString_Cafe_WritesUtf8C3A9()
    {
        var backend = new FileConfigBackend(ConfigPath, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetAsync("core.name", "café", cancellationToken: TestContext.Current.CancellationToken);

        byte[] onDisk = await File.ReadAllBytesAsync(ConfigPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ContainsSubsequence(onDisk, "café"u8.ToArray()), $"expected UTF-8 café in {Convert.ToHexString(onDisk)}");
        Assert.False(onDisk.Contains((byte)0xE9), "must not contain the old single-byte 0xE9");

        // Round-trip through the string tier.
        var backend2 = new FileConfigBackend(ConfigPath, null, _dirs);
        await backend2.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("café", (await backend2.GetAsync("core.name", cancellationToken: TestContext.Current.CancellationToken))?.Value);
    }

    // ── GetBytesAsync surface ────────────────────────────────────────────────

    [Fact]
    public async Task GetBytes_ExistingKey_RawBytes()
    {
        byte[] value = [0xFF, 0xFE];
        await File.WriteAllBytesAsync(ConfigPath, [.. "[core]\n\tkey = "u8.ToArray(), .. value, .. "\n"u8.ToArray()], cancellationToken: TestContext.Current.CancellationToken);

        using GitContext ctx = NewContext();
        GitConfiguration config = await GitConfiguration.OpenAsync(ConfigPath, ctx, cancellationToken: TestContext.Current.CancellationToken);
        await using (config.ConfigureAwait(false))
        {
            byte[]? bytes = await config.GetBytesAsync("core.key", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.Equal(value, bytes);
            // Display tier shows U+FFFD for the invalid bytes.
            Assert.Equal("\uFFFD\uFFFD", await config.GetStringAsync("core.key", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false));
        }
    }

    [Fact]
    public async Task GetBytes_LoneVariable_EmptyArray()
    {
        await File.WriteAllTextAsync(ConfigPath, "[core]\n\tflag\n", cancellationToken: TestContext.Current.CancellationToken);

        using GitContext ctx = NewContext();
        GitConfiguration config = await GitConfiguration.OpenAsync(ConfigPath, ctx, cancellationToken: TestContext.Current.CancellationToken);
        await using (config.ConfigureAwait(false))
        {
            byte[]? bytes = await config.GetBytesAsync("core.flag", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.NotNull(bytes);
            Assert.Empty(bytes);
        }
    }

    [Fact]
    public async Task GetBytes_MissingKey_Null()
    {
        using GitContext ctx = NewContext();
        GitConfiguration config = await GitConfiguration.OpenAsync(ConfigPath, ctx, cancellationToken: TestContext.Current.CancellationToken);
        await using (config.ConfigureAwait(false))
        {
            Assert.Null(await config.GetBytesAsync("core.nope", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false));
        }
    }

    // ── GetMultiBytesAsync surface ───────────────────────────────────────────

    [Fact]
    public async Task GetMultiBytes_RawValues_AllBytes()
    {
        byte[] v1 = [0xE9];
        byte[] v2 = "plain"u8.ToArray();
        await File.WriteAllBytesAsync(ConfigPath,
            [.. "[multi]\n\titem = "u8.ToArray(), .. v1, .. "\n\titem = "u8.ToArray(), .. v2, .. "\n"u8.ToArray()],
            cancellationToken: TestContext.Current.CancellationToken);

        using GitContext ctx = NewContext();
        GitConfiguration config = await GitConfiguration.OpenAsync(ConfigPath, ctx, cancellationToken: TestContext.Current.CancellationToken);
        await using (config.ConfigureAwait(false))
        {
            IReadOnlyList<byte[]> bytes = await config.GetMultiBytesAsync("multi.item", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.Equal(2, bytes.Count);
            Assert.Equal(v1, bytes[0]);
            Assert.Equal(v2, bytes[1]);
        }
    }

    // ── 3: GetPathAsync round-trips a non-UTF-8 path as GitPath ────

    [Fact]
    public async Task GetPath_NonUtf8Value_RawBytes()
    {
        // A config path value containing a raw 0xE9 byte (invalid UTF-8).
        byte[] pathBytes = [0x2F, 0x74, 0x6D, 0x70, 0x2F, 0x63, 0x61, 0x66, 0xE9]; // /tmp/caf<E9>
        await File.WriteAllBytesAsync(ConfigPath, [.. "[core]\n\tattributesfile = "u8.ToArray(), .. pathBytes, .. "\n"u8.ToArray()], cancellationToken: TestContext.Current.CancellationToken);

        using GitContext ctx = NewContext();
        GitConfiguration config = await GitConfiguration.OpenAsync(ConfigPath, ctx, cancellationToken: TestContext.Current.CancellationToken);
        await using (config.ConfigureAwait(false))
        {
            GitPath? path = await config.GetPathAsync("core.attributesfile", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.NotNull(path);
            Assert.Equal(pathBytes, path.Value.Span.ToArray());
            // The filesystem-boundary string shows the U+FFFD replacement.
            Assert.Equal("/tmp/caf\uFFFD", path.Value.ToFileSystemString());
        }
    }

    [Fact]
    public async Task GetPath_TildeExpansion_StillWorks()
    {
        // An isolated HOME that exists — the C behavior requires an existing
        // home directory for ~ expansion.
        using GitContext ctx = NewContext();
        string home = ctx.Dirs.FindHomeDir()!;
        if (string.IsNullOrEmpty(home) || !Directory.Exists(home))
        {
            return; // homedir unavailable — the C behavior is GIT_ENOTFOUND; skip
        }

        await File.WriteAllTextAsync(ConfigPath, "[core]\n\tattributesfile = ~/.gitattributes\n", cancellationToken: TestContext.Current.CancellationToken);

        GitConfiguration config = await GitConfiguration.OpenAsync(ConfigPath, ctx, cancellationToken: TestContext.Current.CancellationToken);
        await using (config.ConfigureAwait(false))
        {
            GitPath? path = await config.GetPathAsync("core.attributesfile", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.NotNull(path);
            Assert.Equal(PathHelpers.Join(home, ".gitattributes"), path.Value.ToFileSystemString());
        }
    }

    // ── Byte-domain parsers (ASCII-only, byte-exact over raw values) ────────

    [Fact]
    public void TryParseBool_ByteOverload_AsciiOnly()
    {
        Assert.True(ConfigurationValueParser.TryParseBool("true"u8, out bool t) && t);
        Assert.True(ConfigurationValueParser.TryParseBool("YES"u8, out bool y) && y);
        Assert.True(ConfigurationValueParser.TryParseBool("1"u8, out bool one) && one);
        Assert.True(ConfigurationValueParser.TryParseBool("false"u8, out bool f) && !f);
        Assert.True(ConfigurationValueParser.TryParseBool("0"u8, out bool zero) && !zero);
        Assert.True(ConfigurationValueParser.TryParseBool(ReadOnlySpan<byte>.Empty, out bool empty) && !empty);
        // Non-ASCII bytes are not recognized (byte-exact, no decode).
        Assert.False(ConfigurationValueParser.TryParseBool([0xC3, 0xA9], out _));
    }

    [Fact]
    public void TryParseInt64_ByteOverload_MatchesString()
    {
        Assert.True(ConfigurationValueParser.TryParseInt64("1k"u8, out long k) && k == 1024);
        Assert.True(ConfigurationValueParser.TryParseInt64("-0x10"u8, out long hex) && hex == -16);
        Assert.True(ConfigurationValueParser.TryParseInt64("  42"u8, out long ws) && ws == 42);
        Assert.False(ConfigurationValueParser.TryParseInt64("abc"u8, out _));
        Assert.False(ConfigurationValueParser.TryParseInt64(ReadOnlySpan<byte>.Empty, out _));
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
