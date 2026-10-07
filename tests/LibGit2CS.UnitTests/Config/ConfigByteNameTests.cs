using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.Config;

/// <summary> Byte-keyed config names — the <see cref="ConfigList"/> head map is keyed on the raw name bytes (C's
/// <c>strcmp</c>-keyed <c>git_config_list_headmap</c>, config_list.c:29), the byte-key <see cref="GitConfiguration"/> APIs
/// (<c>GetEntryAsync(ReadOnlyMemory{byte})</c>/<c>SetBytesAsync</c>/ <c>DeleteAsync</c>/<c>RenameSectionAsync</c>/<c>DeleteSectionAsync</c>) round-trip
/// non-UTF-8 subsection bytes byte-exact, and the in-place file edit writes the raw subsection bytes (C's <c>config_file_write</c> splices the normalized
/// <c>char *</c> key bytes). </summary>
public sealed class ConfigByteNameTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GitSystemDirs _dirs = new();

    public ConfigByteNameTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigByteName_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── 1: byte-key set → file body byte-exact → re-parse ──────────

    [Fact]
    public async Task SetBytes_NonUtf8Subsection_WritesRawBytes()
    {
        // A non-UTF-8 branch subsection (raw 0xFF 0xFE bytes): the byte-key
        // set writes the raw subsection bytes into the file body (C's
        // config_file_write splices the normalized char* key bytes,
        // config_file.c:1145-1171) and the re-parse finds the entry.
        byte[] nameBytes = [.. "branch."u8.ToArray(), 0xFF, 0xFE, .. ".remote"u8.ToArray()];
        var backend = new FileConfigBackend(ConfigPath, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.SetBytesAsync(nameBytes, Encoding.UTF8.GetBytes("origin"), cancellationToken: TestContext.Current.CancellationToken);

        byte[] onDisk = await File.ReadAllBytesAsync(ConfigPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ContainsSubsequence(onDisk, [.. "[branch \""u8.ToArray(), 0xFF, 0xFE, .. "\"]\n"u8.ToArray()]), $"raw subsection missing: {Convert.ToHexString(onDisk)}");

        var backend2 = new FileConfigBackend(ConfigPath, null, _dirs);
        await backend2.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        GitConfigEntry? entry = await backend2.GetAsync(nameBytes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal("origin", entry.Value.Value);
        Assert.Equal(nameBytes, entry.Value.NameBytes.ToArray());
    }

    // ── 2: byte-key delete under the raw name bytes ────────────────

    [Fact]
    public async Task Delete_NonUtf8Subsection_RemovesRawKey()
    {
        byte[] nameBytes = [.. "branch."u8.ToArray(), 0xE9, .. ".remote"u8.ToArray()];
        await File.WriteAllBytesAsync(ConfigPath,
            [.. "[branch \""u8.ToArray(), 0xE9, .. "\"]\n\tremote = origin\n"u8.ToArray()],
            cancellationToken: TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(ConfigPath, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await backend.DeleteKeyAsync(nameBytes, cancellationToken: TestContext.Current.CancellationToken);

        byte[] onDisk = await File.ReadAllBytesAsync(ConfigPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(ContainsSubsequence(onDisk, [.. "remote = origin"u8.ToArray()]), "key must be deleted");
    }

    // ── 3: byte-key rename section (C's raw-byte suffix splice) ───

    [Fact]
    public async Task RenameSection_NonUtf8Subsection_ByteExact()
    {
        byte[] oldSection = [.. "branch."u8.ToArray(), 0xFF];
        byte[] newSection = [.. "branch."u8.ToArray(), 0xFE];
        await File.WriteAllBytesAsync(ConfigPath,
            [.. "[branch \""u8.ToArray(), 0xFF, .. "\"]\n\tremote = origin\n"u8.ToArray()],
            cancellationToken: TestContext.Current.CancellationToken);

        using GitContext ctx = NewContext();
        GitConfiguration config = await GitConfiguration.OpenAsync(ConfigPath, ctx, cancellationToken: TestContext.Current.CancellationToken);
        await using (config.ConfigureAwait(false))
        {
            await config.RenameSectionAsync(oldSection, newSection, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        byte[] onDisk = await File.ReadAllBytesAsync(ConfigPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ContainsSubsequence(onDisk, [.. "[branch \""u8.ToArray(), 0xFE, .. "\"]\n\tremote = origin\n"u8.ToArray()]), $"renamed subsection + key missing: {Convert.ToHexString(onDisk)}");
        // C's write_on_section (config_file.c:1003-1036) always writes the
        // section header line verbatim, so the old section remains as an
        // EMPTY section — only its key line is removed.
        Assert.True(ContainsSubsequence(onDisk, [.. "[branch \""u8.ToArray(), 0xFF, .. "\"]\n"u8.ToArray()]), "old section header stays (empty)");
        Assert.False(ContainsSubsequence(onDisk, [.. "[branch \""u8.ToArray(), 0xFF, .. "\"]\n\tremote = origin\n"u8.ToArray()]), "old section must have no key line");
    }

    // ── 4: byte-key delete section (anchored prefix) ──────────────

    [Fact]
    public async Task DeleteSection_NonUtf8Subsection_RemovesAllKeys()
    {
        byte[] section = [.. "branch."u8.ToArray(), 0xE9];
        await File.WriteAllBytesAsync(ConfigPath,
            [.. "[branch \""u8.ToArray(), 0xE9, .. "\"]\n\tremote = origin\n\tmerge = refs/heads/main\n"u8.ToArray()],
            cancellationToken: TestContext.Current.CancellationToken);

        using GitContext ctx = NewContext();
        GitConfiguration config = await GitConfiguration.OpenAsync(ConfigPath, ctx, cancellationToken: TestContext.Current.CancellationToken);
        await using (config.ConfigureAwait(false))
        {
            await config.DeleteSectionAsync(section, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        byte[] onDisk = await File.ReadAllBytesAsync(ConfigPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(ContainsSubsequence(onDisk, "remote = origin"u8.ToArray()), "remote key must be deleted");
        Assert.False(ContainsSubsequence(onDisk, "merge = refs/heads/main"u8.ToArray()), "merge key must be deleted");
    }

    // ── 5: string tier cannot reach a non-UTF-8 subsection ────────

    [Fact]
    public async Task StringTier_NonUtf8Subsection_NoMatch()
    {
        // The U+FFFD display decode must NOT match the raw bytes (C's strcmp
        // over the raw name bytes) — the byte-key API is the parity surface.
        byte[] nameBytes = [.. "branch."u8.ToArray(), 0xE9, .. ".remote"u8.ToArray()];
        await File.WriteAllBytesAsync(ConfigPath,
            [.. "[branch \""u8.ToArray(), 0xE9, .. "\"]\n\tremote = origin\n"u8.ToArray()],
            cancellationToken: TestContext.Current.CancellationToken);

        using GitContext ctx = NewContext();
        GitConfiguration config = await GitConfiguration.OpenAsync(ConfigPath, ctx, cancellationToken: TestContext.Current.CancellationToken);
        await using (config.ConfigureAwait(false))
        {
            Assert.Null(await config.GetEntryAsync("branch.\uFFFD.remote", TestContext.Current.CancellationToken).ConfigureAwait(false));
            Assert.NotNull(await config.GetEntryAsync(nameBytes, TestContext.Current.CancellationToken).ConfigureAwait(false));
        }
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
