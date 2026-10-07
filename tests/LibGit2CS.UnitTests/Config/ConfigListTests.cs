using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Config;

public class ConfigListTests
{
    [Fact]
    public void AppendAndGet_LastWins()
    {
        var list = ConfigList.New();
        list.Append(MakeEntry("core.key", "first"));
        list.Append(MakeEntry("core.key", "second"));

        GitConfigEntry? entry = list.Get(Key("core.key"));
        Assert.NotNull(entry);
        Assert.Equal("second", entry.Value.Value);
    }

    [Fact]
    public void Get_MissingKey_ReturnsNull()
    {
        var list = ConfigList.New();
        Assert.Null(list.Get(Key("nope")));
    }

    [Fact]
    public void GetUnique_NonMultivar_Returns()
    {
        var list = ConfigList.New();
        list.Append(MakeEntry("core.key", "val"));

        GitConfigEntry? entry = list.GetUnique(Key("core.key"));
        Assert.NotNull(entry);
        Assert.Equal("val", entry.Value.Value);
    }

    [Fact]
    public void GetUnique_Multivar_Throws()
    {
        var list = ConfigList.New();
        list.Append(MakeEntry("core.key", "first"));
        list.Append(MakeEntry("core.key", "second"));

        Assert.Throws<GitException>(() => list.GetUnique(Key("core.key")));
    }

    [Fact]
    public void GetUnique_IncludedEntry_Throws()
    {
        var list = ConfigList.New();
        list.Append(MakeEntry("core.key", "val", includeDepth: 1));

        Assert.Throws<GitException>(() => list.GetUnique(Key("core.key")));
    }

    [Fact]
    public void Enumerate_PreservesInsertionOrder()
    {
        var list = ConfigList.New();
        list.Append(MakeEntry("a", "1"));
        list.Append(MakeEntry("b", "2"));
        list.Append(MakeEntry("c", "3"));

        string[] names = list.Enumerate().Select(e => e.Name).ToArray();
        Assert.Equal(["a", "b", "c"], names);
    }

    [Fact]
    public void Duplicate_DeepCopy()
    {
        var original = ConfigList.New();
        original.Append(MakeEntry("a", "1"));
        original.Append(MakeEntry("b", "2"));

        ConfigList copy = original.Duplicate();
        copy.Append(MakeEntry("c", "3"));

        Assert.Equal(2, original.Count);
        Assert.Equal(3, copy.Count);
    }

    [Fact]
    public void Count_TracksEntries()
    {
        var list = ConfigList.New();
        Assert.Equal(0, list.Count);
        list.Append(MakeEntry("a", "1"));
        Assert.Equal(1, list.Count);
        list.Append(MakeEntry("a", "2"));
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void Get_NonUtf8Subsection_ByteExact()
    {
        // the head map is keyed on the raw name bytes — a non-UTF-8 subsection round-trips byte-exact (C's strcmp over the char* name bytes,
        // config_list.c:193-202).
        var list = ConfigList.New();
        byte[] nameBytes = [.. "branch."u8.ToArray(), 0xFF, .. ".remote"u8.ToArray()];
        list.Append(MakeEntryBytes(nameBytes, "origin"));

        GitConfigEntry? entry = list.Get(ConfigNameKey.From(nameBytes));
        Assert.NotNull(entry);
        Assert.Equal("origin", entry.Value.Value);
        Assert.Null(list.Get(Key("branch.\uFFFD.remote")));
    }

    private static ConfigNameKey Key(string name) => ConfigNameKey.From(Encoding.UTF8.GetBytes(name));

    private static GitConfigEntry MakeEntry(string name, string value, int includeDepth = 0)
        => MakeEntryBytes(Encoding.UTF8.GetBytes(name), value, includeDepth);

    private static GitConfigEntry MakeEntryBytes(byte[] nameBytes, string value, int includeDepth = 0)
    {
        return new GitConfigEntry(
            NameBytes: nameBytes,
            ValueBytes: Encoding.UTF8.GetBytes(value),
            BackendType: GitConfigEntry.MemoryBackendType,
            Path: null,
            IncludeDepth: includeDepth,
            Level: GitConfigLevel.Local);
    }
}
