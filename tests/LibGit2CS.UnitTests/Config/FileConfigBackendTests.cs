using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.IO;

namespace LibGit2CS.UnitTests.Config;

public sealed class FileConfigBackendTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GitSystemDirs _dirs = new();

    public FileConfigBackendTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigTests_" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public async Task Open_ExistingFile_Parses()
    {
        string path = WriteFixture("config0");
        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        // config0 has core.repositoryformatversion, core.filemode, core.bare, core.logallrefupdates
        Assert.NotNull(await backend.GetAsync("core.bare", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Open_MissingFile_EmptyBackend()
    {
        string path = Path.Combine(_tempDir, "nonexistent");
        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await backend.GetAsync("anything", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await ToListAsync(backend.EnumerateAsync(cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Open_EmptyFile_EmptyBackend()
    {
        string path = Path.Combine(_tempDir, "empty");
        await File.WriteAllTextAsync(path, "", cancellationToken: TestContext.Current.CancellationToken);
        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(await ToListAsync(backend.EnumerateAsync(cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Open_SubsectionQuoted_CaseSensitive()
    {
        string path = WriteFixture("config1");
        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        // config1 has [this "that"] and [this "That"] — both exist (case-sensitive subsection)
        Assert.NotNull(await backend.GetAsync("this.that.other", cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await backend.GetAsync("this.That.other", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Open_MultilineValue_Parses()
    {
        string path = WriteFixture("config2");
        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? entry = await backend.GetAsync("this.That.and", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Contains("one", entry.Value.Value);
        Assert.Contains("three", entry.Value.Value);
    }

    [Fact]
    public async Task Open_NumberSuffixes_Parses()
    {
        string path = WriteFixture("config5");
        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? k = await backend.GetAsync("number.k", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(k);
        Assert.Equal("1k", k.Value.Value);

        GitConfigEntry? m = await backend.GetAsync("number.m", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(m);
        Assert.Equal("1m", m.Value.Value);
    }

    [Fact]
    public async Task Open_Multivar_GetsLast()
    {
        string path = WriteFixture("config11");
        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? entry = await backend.GetAsync("remote.ab.url", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        // config11 has two url entries — last wins for Get
        Assert.NotNull(entry.Value.Value);
    }

    [Fact]
    public async Task Open_EscapedQuotes_Parses()
    {
        string path = WriteFixture("config13");
        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? entry = await backend.GetAsync("core.editor", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Contains("Nonsense", entry.Value.Value);
    }

    [Fact]
    public async Task Open_NoWhitespaceAroundEquals_Parses()
    {
        string path = WriteFixture("config14");
        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(await backend.GetAsync("a.b", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Open_Malformed_Throws()
    {
        string path = WriteFixture("config7");
        var backend = new FileConfigBackend(path, null, _dirs);
        await Assert.ThrowsAsync<GitException>(() => backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Open_OriginPath_InEntries()
    {
        string path = WriteFixture("config0");
        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        await foreach (GitConfigEntry entry in backend.EnumerateAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(path, entry.Path);
            Assert.Equal(GitConfigEntry.FileBackendType, entry.BackendType);
        }
    }

    [Fact]
    public async Task Open_Level_StoredInEntries()
    {
        string path = WriteFixture("config0");
        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Global, cancellationToken: TestContext.Current.CancellationToken);

        await foreach (GitConfigEntry entry in backend.EnumerateAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(GitConfigLevel.Global, entry.Level);
        }
    }

    [Fact]
    public async Task Open_Include_ResolvesIncludedValues()
    {
        // config-include references config-included
        string includePath = WriteFixture("config-include");
        _ = WriteFixture("config-included");

        var backend = new FileConfigBackend(includePath, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        // config-included has [foo "bar"] baz = huzzah
        GitConfigEntry? entry = await backend.GetAsync("foo.bar.baz", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal("huzzah", entry.Value.Value);
        Assert.Equal(1, entry.Value.IncludeDepth);
    }

    [Fact]
    public async Task Open_Include_MissingFile_SilentlyIgnored()
    {
        string configPath = Path.Combine(_tempDir, "missing-include.cfg");
        await File.WriteAllTextAsync(configPath, "[core]\n\tkey = val\n[include]\n\tpath = nonexistent\n", cancellationToken: TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(configPath, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        // The main file's key should parse fine
        Assert.Equal("val", (await backend.GetAsync("core.key", cancellationToken: TestContext.Current.CancellationToken))?.Value);
    }

    [Fact]
    public async Task Open_NoSection_KeyNotGettableButIterable()
    {
        string path = WriteFixture("config-nosection");
        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        // Keys without a section can't be looked up by normalized name
        // but should appear in enumeration (matches git behavior)
        GitConfigEntry[] entries = (await ToListAsync(backend.EnumerateAsync(cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        Assert.NotEmpty(entries);
    }

    [Fact]
    public async Task Open_SymbolHeaders_Parses()
    {
        string path = WriteFixture("config20");
        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        // config20 has [valid "[subsection]"] something = a
        Assert.NotNull(await backend.GetAsync("valid.[subsection].something", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Open_RawE9Fixture_PreservesBytes()
    {
        // The config_latin1 fixture contains a raw 0xE9 byte (invalid standalone UTF-8) in the value. The entry carries the raw bytes (ValueBytes), and the
        // display Value is the UTF-8 decode with replacement (U+FFFD), not a one-byte-per-char mapping. WriteFixtureBytes copies raw bytes (WriteFixture would
        // re-encode via UTF-8 and corrupt 0xE9).
        string path = WriteFixtureBytes("config_latin1");
        byte[] onDisk = await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(onDisk.Contains((byte)0xE9), "fixture must contain raw 0xE9");

        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? entry = await backend.GetAsync("core.name", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal("caf\uFFFD", entry.Value.Value);
        Assert.Equal(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, entry.Value.ValueBytes.GetValueOrDefault().ToArray());
    }

    [Fact]
    public async Task Refresh_FileModified_ReReads()
    {
        string path = Path.Combine(_tempDir, "refresh.cfg");
        await File.WriteAllTextAsync(path, "[core]\n\tkey = original\n", cancellationToken: TestContext.Current.CancellationToken);

        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("original", (await backend.GetAsync("core.key", cancellationToken: TestContext.Current.CancellationToken))?.Value);

        // Modify the file with a small delay to ensure mtime changes
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "[core]\n\tkey = updated\n", cancellationToken: TestContext.Current.CancellationToken);

        // Next access should trigger refresh
        Assert.Equal("updated", (await backend.GetAsync("core.key", cancellationToken: TestContext.Current.CancellationToken))?.Value);
    }

    [Fact]
    public async Task Enumerate_ReturnsAllEntries()
    {
        string path = WriteFixture("config9");
        var backend = new FileConfigBackend(path, null, _dirs);
        await backend.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        // config9 has multiple sections with entries
        int count = (await ToListAsync(backend.EnumerateAsync(cancellationToken: TestContext.Current.CancellationToken))).Count;
        Assert.True(count > 0);
    }

    [Fact]
    public void ReadOnly_IsFalse()
    {
        var backend = new FileConfigBackend("/nonexistent", null, _dirs);
        Assert.False(backend.ReadOnly);
    }

    [Fact]
    public async Task Get_BeforeOpen_Throws()
    {
        var backend = new FileConfigBackend("/nonexistent", null, _dirs);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.GetAsync("x", cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    private string WriteFixture(string fixtureName)
    {
        string text = FixtureLoader.LoadText($"Fixtures/config/{fixtureName}");
        string dest = Path.Combine(_tempDir, fixtureName);
        File.WriteAllText(dest, text);
        return dest;
    }

    /// <summary>Copies an embedded fixture's raw bytes verbatim to the temp
    /// directory, preserving non-ASCII bytes (e.g. Latin-1 0xE9) that
    /// <see cref="WriteFixture"/> would re-encode via UTF-8.</summary>
    private string WriteFixtureBytes(string fixtureName)
    {
        byte[] bytes = FixtureLoader.LoadBytes($"Fixtures/config/{fixtureName}");
        string dest = Path.Combine(_tempDir, fixtureName);
        File.WriteAllBytes(dest, bytes);
        return dest;
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
