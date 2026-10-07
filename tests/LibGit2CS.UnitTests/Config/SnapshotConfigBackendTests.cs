using LibGit2CS.Config;
using LibGit2CS.Core;

namespace LibGit2CS.UnitTests.Config;

public class SnapshotConfigBackendTests
{
    [Fact]
    public async Task Snapshot_SeesSourceEntries()
    {
        var source = new MemoryConfigBackend("[core]\n\tkey = val\n");
        await source.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        var snap = new SnapshotConfigBackend(source);
        await snap.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("val", (await snap.GetAsync("core.key", cancellationToken: TestContext.Current.CancellationToken))?.Value);
    }

    [Fact]
    public async Task Snapshot_BackendType_PreservesSource()
    {
        // C (config_snapshot.c:152-154 + config_list.c:70-71): the duplicated
        // entry preserves the source's backend_type — it is NOT overridden to
        // "snapshot".
        var source = new MemoryConfigBackend("[core]\n\tkey = val\n");
        await source.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        var snap = new SnapshotConfigBackend(source);
        await snap.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry? entry = await snap.GetAsync("core.key", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal(GitConfigEntry.MemoryBackendType, entry.Value.BackendType);
    }

    [Fact]
    public async Task Snapshot_Multivar_SeesAll()
    {
        var source = new MemoryConfigBackend("[core]\n\tkey = first\n\tkey = second\n");
        await source.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        var snap = new SnapshotConfigBackend(source);
        await snap.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        GitConfigEntry[] entries = (await ToListAsync(snap.EnumerateAsync(cancellationToken: TestContext.Current.CancellationToken))).Where(e => e.Name == "core.key").ToArray();
        Assert.Equal(2, entries.Length);
    }

    [Fact]
    public async Task Snapshot_OfSnapshot_Works()
    {
        var source = new MemoryConfigBackend("[core]\n\tkey = val\n");
        await source.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        var snap1 = new SnapshotConfigBackend(source);
        await snap1.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        var snap2 = new SnapshotConfigBackend(snap1);
        await snap2.OpenAsync(GitConfigLevel.Local, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("val", (await snap2.GetAsync("core.key", cancellationToken: TestContext.Current.CancellationToken))?.Value);
    }

    [Fact]
    public void Snapshot_ReadOnly_IsTrue()
    {
        var source = new MemoryConfigBackend("[core]\n");
        var snap = new SnapshotConfigBackend(source);
        Assert.True(snap.ReadOnly);
    }

    [Fact]
    public async Task Snapshot_Get_BeforeOpen_Throws()
    {
        var source = new MemoryConfigBackend("[core]\n");
        var snap = new SnapshotConfigBackend(source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => snap.GetAsync("x", cancellationToken: TestContext.Current.CancellationToken).AsTask());
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
