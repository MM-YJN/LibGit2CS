using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.IO;
using LibGit2CS.Utils;

namespace LibGit2CS.UnitTests.Config;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigurationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_ConfigFacade_" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task Open_SingleFile_Parses()
    {
        string path = WriteConfig("[core]\n\tbare = false\n\trepositoryformatversion = 0\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(await config.GetBoolAsync("core.bare", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, await config.GetIntAsync("core.repositoryformatversion", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetString_ExistingKey_ReturnsValue()
    {
        string path = WriteConfig("[remote \"origin\"]\n\turl = git://example.com\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("git://example.com", await config.GetStringAsync("remote.origin.url", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetString_MissingKey_ReturnsNull()
    {
        string path = WriteConfig("[core]\n\tkey = val\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await config.GetStringAsync("core.nonexistent", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetString_MissingKey_WithDefault_ReturnsDefault()
    {
        string path = WriteConfig("[core]\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("fallback", await config.GetStringAsync("core.missing", "fallback", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetBool_LoneVariable_IsTrue()
    {
        string path = WriteConfig("[core]\n\tauto\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await config.GetBoolAsync("core.auto", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetBool_TrueKeyword_IsTrue()
    {
        string path = WriteConfig("[core]\n\tval = true\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await config.GetBoolAsync("core.val", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetBool_FalseKeyword_IsFalse()
    {
        string path = WriteConfig("[core]\n\tval = false\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(await config.GetBoolAsync("core.val", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetBool_YesOn_AreTrue()
    {
        string path = WriteConfig("[core]\n\ta = yes\n\tb = on\n\tc = 1\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await config.GetBoolAsync("core.a", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await config.GetBoolAsync("core.b", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await config.GetBoolAsync("core.c", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetBool_NoOffZero_AreFalse()
    {
        string path = WriteConfig("[core]\n\ta = no\n\tb = off\n\tc = 0\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(await config.GetBoolAsync("core.a", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await config.GetBoolAsync("core.b", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await config.GetBoolAsync("core.c", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetBool_NonZeroInt_IsTrue()
    {
        string path = WriteConfig("[core]\n\tval = 42\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await config.GetBoolAsync("core.val", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetInt_WithSuffix_Parses()
    {
        string path = WriteConfig("[core]\n\tk = 1k\n\tm = 1m\n\tg = 1g\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1024, await config.GetIntAsync("core.k", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1024 * 1024, await config.GetIntAsync("core.m", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1024 * 1024 * 1024, await config.GetIntAsync("core.g", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetInt64_LargeValue_Parses()
    {
        string path = WriteConfig("[core]\n\tval = 5g\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5L * 1024 * 1024 * 1024, await config.GetInt64Async("core.val", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetInt_MissingKey_ReturnsDefault()
    {
        string path = WriteConfig("[core]\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(99, await config.GetIntAsync("core.missing", 99, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPath_Tilde_Expands()
    {
        string path = WriteConfig("[core]\n\teditor = ~/bin/editor\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        GitPath? result = await config.GetPathAsync("core.editor", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.DoesNotContain("~", result.Value.ToFileSystemString());
    }

    [Fact]
    public async Task GetPath_TildeUser_Throws()
    {
        string path = WriteConfig("[core]\n\tval = ~otheruser/file\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(() => config.GetPathAsync("core.val", cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task GetPath_PlainPath_ReturnedAsIs()
    {
        string path = WriteConfig("[core]\n\tval = /usr/bin/editor\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/usr/bin/editor", (await config.GetPathAsync("core.val", cancellationToken: TestContext.Current.CancellationToken))!.Value.ToFileSystemString());
    }

    [Fact]
    public async Task GetMulti_ReturnsAllValuesAcrossBackends()
    {
        string p1 = WriteConfig("[core]\n\tkey = first\n", "1.cfg");
        string p2 = WriteConfig("[core]\n\tkey = second\n", "2.cfg");
        await using var config = new GitConfiguration(new GitContext());
        await config.AddFileOnDiskAsync(p1, GitConfigLevel.System, cancellationToken: TestContext.Current.CancellationToken);
        await config.AddFileOnDiskAsync(p2, GitConfigLevel.Global, cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> values = await config.GetMultiAsync("core.key", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, values.Count);
        Assert.Contains("first", values);
        Assert.Contains("second", values);
    }

    [Fact]
    public async Task GetMulti_MissingKey_ThrowsNotFound()
    {
        string path = WriteConfig("[core]\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        // C (config_get_multivar): a key matching no entry is GIT_ENOTFOUND.
        GitException ex = await Assert.ThrowsAsync<GitException>(async () =>
            await config.GetMultiAsync("core.missing", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Enumerate_AllEntries()
    {
        string path = WriteConfig("[core]\n\ta = 1\n\tb = 2\n[remote \"x\"]\n\turl = y\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        var names = new List<string>();
        await foreach (GitConfigEntry e in config.EnumerateAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            names.Add(e.Name);
        }

        string[] sorted = names.OrderBy(n => n).ToArray();
        Assert.Equal(["core.a", "core.b", "remote.x.url"], sorted);
    }

    [Fact]
    public async Task Enumerate_WithGlob_Filters()
    {
        string path = WriteConfig("[core]\n\ta = 1\n\tb = 2\n[remote \"x\"]\n\turl = y\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        var names = new List<string>();
        await foreach (GitConfigEntry e in config.EnumerateAsync("core.*", cancellationToken: TestContext.Current.CancellationToken))
        {
            names.Add(e.Name);
        }

        Assert.Equal(2, names.Count);
        Assert.All(names, n => Assert.StartsWith("core.", n));
    }

    [Fact]
    public async Task LevelPrecedence_LocalOverridesGlobal()
    {
        string global = WriteConfig("[core]\n\tval = global\n", "global.cfg");
        string local = WriteConfig("[core]\n\tval = local\n", "local.cfg");
        await using var config = new GitConfiguration(new GitContext());
        await config.AddFileOnDiskAsync(global, GitConfigLevel.Global, cancellationToken: TestContext.Current.CancellationToken);
        await config.AddFileOnDiskAsync(local, GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("local", await config.GetStringAsync("core.val", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Fallback_LocalMissing_FallsToGlobal()
    {
        string global = WriteConfig("[core]\n\tval = global\n", "global.cfg");
        string local = WriteConfig("[other]\n\tkey = val\n", "local.cfg");
        await using var config = new GitConfiguration(new GitContext());
        await config.AddFileOnDiskAsync(global, GitConfigLevel.Global, cancellationToken: TestContext.Current.CancellationToken);
        await config.AddFileOnDiskAsync(local, GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("global", await config.GetStringAsync("core.val", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddBackend_DuplicateLevel_Throws()
    {
        string path1 = WriteConfig("[core]\n", "1.cfg");
        string path2 = WriteConfig("[core]\n", "2.cfg");
        await using var config = new GitConfiguration(new GitContext());
        await config.AddFileOnDiskAsync(path1, GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(() => config.AddFileOnDiskAsync(path2, GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddBackend_DuplicateLevel_WithForce_Replaces()
    {
        string path1 = WriteConfig("[core]\n\tval = first\n", "1.cfg");
        string path2 = WriteConfig("[core]\n\tval = second\n", "2.cfg");
        await using var config = new GitConfiguration(new GitContext());
        await config.AddFileOnDiskAsync(path1, GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        await config.AddFileOnDiskAsync(path2, GitConfigLevel.Local, force: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("second", await config.GetStringAsync("core.val", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Snapshot_PointInTime()
    {
        string path = WriteConfig("[core]\n\tval = original\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await using GitConfiguration snap = await config.SnapshotAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("original", await snap.GetStringAsync("core.val", cancellationToken: TestContext.Current.CancellationToken));

        // Modify the file — snapshot should be unaffected
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "[core]\n\tval = modified\n", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("original", await snap.GetStringAsync("core.val", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenLevel_ExistingLevel_ReturnsScopedConfig()
    {
        string global = WriteConfig("[core]\n\tval = global\n", "g.cfg");
        string local = WriteConfig("[core]\n\tval = local\n", "l.cfg");
        await using var config = new GitConfiguration(new GitContext());
        await config.AddFileOnDiskAsync(global, GitConfigLevel.Global, cancellationToken: TestContext.Current.CancellationToken);
        await config.AddFileOnDiskAsync(local, GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        await using GitConfiguration localOnly = await config.OpenLevelAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("local", await localOnly.GetStringAsync("core.val", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenLevel_MissingLevel_Throws()
    {
        string path = WriteConfig("[core]\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(() => config.OpenLevelAsync(GitConfigLevel.Global, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task GetEntry_ReturnsEntryWithMetadata()
    {
        string path = WriteConfig("[core]\n\tkey = val\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? entry = await config.GetEntryAsync("core.key", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal("core.key", entry.Value.Name);
        Assert.Equal("val", entry.Value.Value);
        Assert.Equal(GitConfigLevel.Local, entry.Value.Level);
        Assert.Equal(GitConfigEntry.FileBackendType, entry.Value.BackendType);
        Assert.Equal(path, entry.Value.Path);
    }

    [Fact]
    public async Task GetEntry_Missing_ReturnsNull()
    {
        string path = WriteConfig("[core]\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await config.GetEntryAsync("core.missing", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetEntry_EmptyByteKey_ThrowsInvalidSpec()
    {
        // C's git_config_get_entry("") fails normalization with
        // GIT_EINVALIDSPEC ("invalid config item name ''"). The byte-key tier
        // must surface the same GitException, not an ArgumentException from
        // the PooledByteBufferWriter capacity ctor (clamped to 1 at the call
        // site so the empty key reaches NormalizeNameBytes).
        string path = WriteConfig("[core]\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        GitException ex = await Assert.ThrowsAsync<GitException>(() => config.GetEntryAsync(ReadOnlyMemory<byte>.Empty, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public async Task Write_EmptyByteKey_ThrowsInvalidSpec()
    {
        // Set/Delete with an empty key must fail normalization with
        // GIT_EINVALIDSPEC before any file write (C: git_config_set_string /
        // git_config_delete_entry on "" → "invalid config item name ''").
        string path = WriteConfig("[core]\n\tkey = val\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        GitException setEx = await Assert.ThrowsAsync<GitException>(() => config.SetBytesAsync(ReadOnlyMemory<byte>.Empty, "v"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.InvalidSpec, setEx.Code);

        GitException deleteEx = await Assert.ThrowsAsync<GitException>(() => config.DeleteAsync(ReadOnlyMemory<byte>.Empty, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.InvalidSpec, deleteEx.Code);

        // No partial write: the file is unchanged and the key still reads.
        Assert.Equal("val", await config.GetStringAsync("core.key", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NormalizeName_LowercasesSectionAndVariable()
    {
        Assert.Equal("core.value", GitConfigurationNormalizeName("Core.VALUE"));
    }

    [Fact]
    public void NormalizeName_PreservesSubsectionCase()
    {
        Assert.Equal("remote.Origin.url", GitConfigurationNormalizeName("remote.Origin.url"));
    }

    [Fact]
    public void NormalizeName_MissingDot_Throws()
    {
        Assert.Throws<GitException>(() => GitConfigurationNormalizeName("noDot"));
    }

    [Fact]
    public void NormalizeName_LeadingDot_Throws()
    {
        Assert.Throws<GitException>(() => GitConfigurationNormalizeName(".value"));
    }

    [Fact]
    public void NormalizeName_TrailingDot_Throws()
    {
        Assert.Throws<GitException>(() => GitConfigurationNormalizeName("core."));
    }

    [Fact]
    public void NormalizeName_NewlineInSubsection_Throws()
    {
        Assert.Throws<GitException>(() => GitConfigurationNormalizeName("sec.sub\nction.val"));
    }

    [Fact]
    public void NormalizeName_LeadingHyphenInSection_Throws()
    {
        Assert.Throws<GitException>(() => GitConfigurationNormalizeName("-foo.bar.baz"));
    }

    [Fact]
    public void NormalizeName_LeadingHyphenInVariable_Throws()
    {
        Assert.Throws<GitException>(() => GitConfigurationNormalizeName("foo.bar.-baz"));
    }

    private static string GitConfigurationNormalizeName(ReadOnlySpan<char> name)
    {
        using var writer = new PooledByteBufferWriter(name.Length);
        GitConfiguration.NormalizeName(writer, name);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    [Fact]
    public async Task GetMapped_ValidValue_ReturnsMapped()
    {
        string path = WriteConfig("[core]\n\teol = lf\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        var map = new GitConfigurationMap<int>([
            new(GitConfigurationMapType.String, "lf", 1),
            new(GitConfigurationMapType.String, "crlf", 2),
        ]);

        Assert.Equal(1, await config.GetMappedAsync("core.eol", map, 0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetMapped_MissingKey_ReturnsDefault()
    {
        string path = WriteConfig("[core]\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        var map = new GitConfigurationMap<int>([
            new(GitConfigurationMapType.String, "lf", 1),
        ]);

        Assert.Equal(42, await config.GetMappedAsync("core.missing", map, 42, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetMapped_UnmappableValue_Throws()
    {
        string path = WriteConfig("[core]\n\teol = garbage\n");
        await using GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);

        var map = new GitConfigurationMap<int>([
            new(GitConfigurationMapType.String, "lf", 1),
        ]);

        await Assert.ThrowsAsync<GitException>(() => config.GetMappedAsync("core.eol", map, 0, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Dispose_MarksDisposed()
    {
        string path = WriteConfig("[core]\n");
        GitConfiguration config = await GitConfiguration.OpenAsync(path, new GitContext(), cancellationToken: TestContext.Current.CancellationToken);
        await config.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => config.GetStringAsync("core.x", cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task EmptyConfig_NoBackends()
    {
        await using var config = new GitConfiguration(new GitContext());
        // "core.missing" is a valid key format (has a dot) — returns null.
        Assert.Null(await config.GetStringAsync("core.missing", cancellationToken: TestContext.Current.CancellationToken));
        var entries = new List<GitConfigEntry>();
        await foreach (GitConfigEntry e in config.EnumerateAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            entries.Add(e);
        }

        Assert.Empty(entries);
    }

    private string WriteConfig(string content, string filename = "config")
    {
        string path = Path.Combine(_tempDir, filename);
        File.WriteAllText(path, content);
        return path;
    }
}
