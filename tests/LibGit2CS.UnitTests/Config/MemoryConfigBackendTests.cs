using LibGit2CS.Config;
using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Config;

public class MemoryConfigBackendTests
{
    [Fact]
    public async Task FromText_SimpleSection_Parses()
    {
        var backend = new MemoryConfigBackend("[core]\n\tvalue = 42\n");
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? entry = await backend.GetAsync("core.value", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal("42", entry.Value.Value);
        Assert.Equal(GitConfigLevel.Local, entry.Value.Level);
        Assert.Equal(GitConfigEntry.MemoryBackendType, entry.Value.BackendType);
    }

    [Fact]
    public async Task FromText_MultipleSections_ParsesAll()
    {
        var backend = new MemoryConfigBackend(
            "[core]\n\ta = 1\n[remote \"origin\"]\n\turl = git://x\n");
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(await backend.GetAsync("core.a", cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await backend.GetAsync("remote.origin.url", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FromText_Multivar_GetsLast()
    {
        var backend = new MemoryConfigBackend("[core]\n\tkey = first\n\tkey = second\n");
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? entry = await backend.GetAsync("core.key", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal("second", entry.Value.Value);
    }

    [Fact]
    public async Task FromText_Foreach_SeesAllMultivars()
    {
        var backend = new MemoryConfigBackend("[core]\n\tkey = first\n\tkey = second\n");
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry[] entries = (await ToListAsync(backend.EnumerateAsync(cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        GitConfigEntry[] keyEntries = entries.Where(e => e.Name == "core.key").ToArray();
        Assert.Equal(2, keyEntries.Length);
        Assert.Equal("first", keyEntries[0].Value);
        Assert.Equal("second", keyEntries[1].Value);
    }

    [Fact]
    public async Task FromText_Malformed_Throws()
    {
        var backend = new MemoryConfigBackend("[core\n");
        await Assert.ThrowsAsync<GitException>(() => backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FromText_OriginPath_StoredInEntries()
    {
        var backend = new MemoryConfigBackend("[core]\n\tkey = val\n", originPath: "/custom/path");
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? entry = await backend.GetAsync("core.key", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal("/custom/path", entry.Value.Path);
    }

    [Fact]
    public async Task FromText_Utf8Chars_ValueRoundTrips()
    {
        // the string ctor UTF-8-encodes the config text, the entry carries the bytes, and Value is the UTF-8 display decode — 'café' round-trips.
        var backend = new MemoryConfigBackend("[core]\n\tname = café\n");
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? entry = await backend.GetAsync("core.name", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal("café", entry.Value.Value);
    }

    [Fact]
    public async Task FromBytes_RawE9Byte_ValueBytesRoundTrip()
    {
        // Byte ctor: a lone 0xE9 byte (Latin-1 é) parses byte-faithfully into ValueBytes; the display Value decodes with UTF-8 replacement
        // (U+FFFD), NOT a one-byte-per-char mapping.
        byte[] configText = [.. "[core]\n\tname = caf"u8.ToArray(), 0xE9, .. "\n"u8.ToArray()];
        var backend = new MemoryConfigBackend(configText);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? entry = await backend.GetAsync("core.name", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, entry.Value.ValueBytes.GetValueOrDefault().ToArray());
        Assert.Equal("caf\uFFFD", entry.Value.Value);
    }

    [Fact]
    public async Task FromText_Utf8QuotedSubsection_KeyRoundTrips()
    {
        // the quoted subsection bytes are UTF-8 (C3 A9); the fully-qualified key is the UTF-8 display decode, so the lookup key is the natural 'café' form.
        var backend = new MemoryConfigBackend("[remote \"café\"]\n\turl = x\n");
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? entry = await backend.GetAsync("remote.café.url", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal("x", entry.Value.Value);
    }

    [Fact]
    public async Task FromBytes_RawE9QuotedSubsection_ValueBytesRoundTrip()
    {
        // Byte ctor variant: a lone 0xE9 byte in the quoted subsection keeps the byte-faithful key. The string lookup key (UTF-8 display
        // decode, U+FFFD for the 0xE9 byte) can no longer reach the entry — the ConfigList head map is byte-keyed (C's strcmp over the raw name bytes); the
        // byte-key API is the parity surface.
        byte[] configText = [.. "[remote \"caf"u8.ToArray(), 0xE9, .. "\"]\n\turl = x\n"u8.ToArray()];
        var backend = new MemoryConfigBackend(configText);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        byte[] rawName = [.. "remote.caf"u8.ToArray(), 0xE9, .. ".url"u8.ToArray()];
        GitConfigEntry? entry = await backend.GetAsync(rawName, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal("x", entry.Value.Value);
        Assert.Null(await backend.GetAsync("remote.caf\uFFFD.url", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FromValues_KeyValueArray_Parses()
    {
        var backend = new MemoryConfigBackend(["core.a=1", "core.b=2"]);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("1", (await backend.GetAsync("core.a", cancellationToken: TestContext.Current.CancellationToken))?.Value);
        Assert.Equal("2", (await backend.GetAsync("core.b", cancellationToken: TestContext.Current.CancellationToken))?.Value);
    }

    [Fact]
    public async Task FromValues_LoneKey_HasNullValue()
    {
        var backend = new MemoryConfigBackend(["core.flag"]);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? entry = await backend.GetAsync("core.flag", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Null(entry.Value.Value);
    }

    [Fact]
    public async Task FromValues_EmptyKey_Throws()
    {
        var backend = new MemoryConfigBackend(["=val"]);
        await Assert.ThrowsAsync<GitException>(() => backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadOnly_IsTrue()
    {
        var backend = new MemoryConfigBackend("[core]\n");
        Assert.True(backend.ReadOnly);
    }

    [Fact]
    public async Task Get_BeforeOpen_Throws()
    {
        var backend = new MemoryConfigBackend("[core]\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.GetAsync("core.x", cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Enumerate_BeforeOpen_Throws()
    {
        var backend = new MemoryConfigBackend("[core]\n");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ToListAsync(backend.EnumerateAsync(cancellationToken: TestContext.Current.CancellationToken)));
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (T item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
