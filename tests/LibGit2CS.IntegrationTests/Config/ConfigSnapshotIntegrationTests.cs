using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.IntegrationTests.Config;

/// <summary>
/// Integration tests for config snapshots, memory backends, and config
/// enumeration exercised end-to-end against locally-initialized repos.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The existing
/// <see cref="ConfigTransactionIntegrationTests"/> cover lock/commit/rollback
/// but never call <see cref="GitConfiguration.SnapshotAsync"/>,
/// <see cref="GitConfiguration.OpenLevelAsync"/>,
/// <see cref="GitConfiguration.EnumerateAsync"/>, or
/// <see cref="GitConfiguration.GetMultiAsync"/>. As a result
/// <see cref="SnapshotConfigBackend"/>, <see cref="MemoryConfigBackend"/>,
/// and <see cref="ConfigList"/> enumeration paths are entirely cold. These
/// tests close those gaps.
/// </para>
/// <para>
/// <b>InternalsVisibleTo.</b> <see cref="MemoryConfigBackend"/> is
/// <c>internal</c> but accessible from this test project because
/// <c>LibGit2CS.csproj</c> grants
/// <c>InternalsVisibleTo LibGit2CS.IntegrationTests</c>.
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class ConfigSnapshotIntegrationTests
{
    private static string NewRepoPath()
        => Path.Combine(Path.GetTempPath(), "libgit2cs-cfgsnap-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── Snapshot is frozen after live change ───────────────────────────

    /// <summary>
    /// <see cref="GitConfiguration.SnapshotAsync"/> produces a
    /// point-in-time copy: changes to the live config after the snapshot
    /// are NOT visible in the snapshot. Exercises
    /// <see cref="GitConfiguration.SnapshotAsync"/>,
    /// <see cref="SnapshotConfigBackend.OpenAsync"/> (deep-copy),
    /// <see cref="SnapshotConfigBackend.GetAsync"/>, and
    /// <see cref="SnapshotConfigBackend.EnumerateAsync"/>.
    /// </summary>
    [Fact]
    public async Task Snapshot_PointInTime_FrozenAfterLiveChange()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await repo.Config.SetStringAsync("core.editor", "vim", ct);

            // Take a snapshot.
            await using GitConfiguration snap = await repo.Config.SnapshotAsync(ct);
            Assert.Equal("vim", await snap.GetStringAsync("core.editor", ct));

            // Mutate the live config.
            await repo.Config.SetStringAsync("core.editor", "emacs", ct);

            // The snapshot still sees the old value.
            Assert.Equal("vim", await snap.GetStringAsync("core.editor", ct));
            // The live config sees the new value.
            Assert.Equal("emacs", await repo.Config.GetStringAsync("core.editor", ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── OpenLevel scoped to Local ──────────────────────────────────────

    /// <summary>
    /// <see cref="GitConfiguration.OpenLevelAsync"/> returns a
    /// configuration containing only the backend at the given level.
    /// Exercises <see cref="GitConfiguration.OpenLevelAsync"/> and
    /// <see cref="GitConfiguration.FindBackend"/>.
    /// </summary>
    [Fact]
    public async Task OpenLevel_Local_ScopedConfig()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await repo.Config.SetStringAsync("core.editor", "nano", ct);

            await using GitConfiguration localOnly = await repo.Config.OpenLevelAsync(GitConfigLevel.Local, ct);
            Assert.Equal("nano", await localOnly.GetStringAsync("core.editor", ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── MemoryConfigBackend text parse ─────────────────────────────────

    /// <summary>
    /// A <see cref="MemoryConfigBackend"/> created from a config
    /// string parses entries that are readable via
    /// <see cref="GitConfiguration.GetStringAsync"/> and
    /// <see cref="GitConfiguration.GetBoolAsync"/>. Exercises
    /// <see cref="MemoryConfigBackend.OpenAsync"/>,
    /// <see cref="MemoryConfigBackend.GetAsync"/>,
    /// <see cref="MemoryConfigBackend.EnumerateAsync"/>, and
    /// <see cref="MemoryConfigBackend.ParseTextAsync"/>.
    /// </summary>
    [Fact]
    public async Task MemoryBackend_Text_ParsesAndReads()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var config = new GitConfiguration(new GitContext());
        var memBackend = new MemoryConfigBackend("[core]\n\tbare = false\n\teditor = vim\n");
        await config.AddBackendAsync(memBackend, GitConfigLevel.Local, cancellationToken: ct);

        Assert.False(await config.GetBoolAsync("core.bare", defaultValue: true, ct));
        Assert.Equal("vim", await config.GetStringAsync("core.editor", ct));
    }

    // ── MemoryConfigBackend multivar + GetMulti ────────────────────────

    /// <summary>
    /// A <see cref="MemoryConfigBackend"/> with duplicate keys
    /// (multivar) returns all values via <see cref="GitConfiguration.GetMultiAsync"/>
    /// and the last value via <see cref="GitConfiguration.GetStringAsync"/>.
    /// Exercises <see cref="ConfigList"/>'s multivar append branch and
    /// <see cref="GitConfiguration.GetMultiAsync"/>.
    /// </summary>
    [Fact]
    public async Task MemoryBackend_Multivar_LastWinsAndEnumerateAll()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var config = new GitConfiguration(new GitContext());
        var memBackend = new MemoryConfigBackend("[core]\n\tkey = first\n\tkey = second\n");
        await config.AddBackendAsync(memBackend, GitConfigLevel.Local, cancellationToken: ct);

        // GetString returns the last value (last-wins).
        Assert.Equal("second", await config.GetStringAsync("core.key", ct));

        // GetMulti returns all values.
        IReadOnlyList<string> all = await config.GetMultiAsync("core.key", ct);
        Assert.Equal(2, all.Count);
        Assert.Contains("first", all);
        Assert.Contains("second", all);
    }

    // ── Enumerate with glob filter ─────────────────────────────────────

    /// <summary>
    /// <see cref="GitConfiguration.EnumerateAsync"/> with a glob
    /// pattern filters entries to those matching. Exercises the glob path
    /// in <see cref="GitConfiguration.EnumerateAsync"/> and
    /// <see cref="WildMatch.IsMatch"/>.
    /// </summary>
    [Fact]
    public async Task Enumerate_WithGlob_FiltersCorrectly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await repo.Config.SetStringAsync("core.a", "1", ct);
            await repo.Config.SetStringAsync("core.b", "2", ct);
            await repo.Config.SetStringAsync("remote.origin.url", "x", ct);

            var matched = new List<string>();
            await foreach (GitConfigEntry e in repo.Config.EnumerateAsync("core.*", ct))
            {
                matched.Add(e.Name);
            }

            Assert.Contains("core.a", matched);
            Assert.Contains("core.b", matched);
            Assert.DoesNotContain("remote.origin.url", matched);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Snapshot of snapshot ───────────────────────────────────────────

    /// <summary>
    /// Snapshotting a snapshot produces a working copy. Exercises
    /// <see cref="SnapshotConfigBackend.Snapshot"/> (snapshot-of-snapshot).
    /// </summary>
    [Fact]
    public async Task Snapshot_OfSnapshot_Works()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = NewRepoPath();
        await using GitRepository repo = await GitRepository.InitAsync(path, isBare: false, new GitContext(), cancellationToken: ct);
        try
        {
            await repo.Config.SetStringAsync("user.name", "alice", ct);

            await using GitConfiguration snap1 = await repo.Config.SnapshotAsync(ct);
            await using GitConfiguration snap2 = await snap1.SnapshotAsync(ct);

            Assert.Equal("alice", await snap2.GetStringAsync("user.name", ct));
        }
        finally
        {
            Cleanup(path);
        }
    }
}
