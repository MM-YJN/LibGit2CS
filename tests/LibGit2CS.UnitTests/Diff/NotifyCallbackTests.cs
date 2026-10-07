using LibGit2CS.Diff;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Tests for the notify and progress callbacks, ported from libgit2's
/// <c>tests/libgit2/diff/notify.c</c> against the <c>status</c> fixture.
/// Verifies that <see cref="GitDiffOptions.Notify"/> is invoked per-delta with
/// the correct matched pathspec, and that positive/negative return values
/// filter/abort the diff as expected.
/// </summary>
public sealed class NotifyCallbackTests : DiffGoldenBase
{
    private const GitDiffOptionsFlags NotifyFlags =
        GitDiffOptionsFlags.IncludeIgnored | GitDiffOptionsFlags.IncludeUntracked;

    // notify.c notify_single_pathspec: pathspec "*_deleted" matches
    // file_deleted + staged_changes_file_deleted (2 files).
    [Fact]
    public async Task Notify_SinglePathspec_MatchesTwoFiles()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        var matched = new List<(string path, string? spec)>();
        var opts = new GitDiffOptions
        {
            Flags = NotifyFlags,
            PathSpecStrings = ["*_deleted"],
            Notify = (delta, spec) =>
            {
                matched.Add((delta.NewFile.Path?.ToUtf8String() ?? delta.OldFile.Path?.ToUtf8String() ?? "", spec?.ToUtf8String()));
                return 0;
            },
        };

        using GitDiff diff = await repo.DiffIndexToWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, diff.DeltaCount);
        Assert.Contains(matched, m => m.path == "file_deleted" && m.spec == "*_deleted");
        Assert.Contains(matched, m => m.path == "staged_changes_file_deleted" && m.spec == "*_deleted");
    }

    // notify.c notify_multiple_pathspec: 4 pathspecs, 8 matched files.
    [Fact]
    public async Task Notify_MultiplePathspec_MatchesEightFiles()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        var matched = new List<(string path, string? spec)>();
        var opts = new GitDiffOptions
        {
            Flags = NotifyFlags,
            PathSpecStrings = ["staged_changes_cant_find_me", "subdir/modified_cant_find_me", "subdir/*", "staged*"],
            Notify = (delta, spec) =>
            {
                matched.Add((delta.NewFile.Path?.ToUtf8String() ?? delta.OldFile.Path?.ToUtf8String() ?? "", spec?.ToUtf8String()));
                return 0;
            },
        };

        using GitDiff diff = await repo.DiffIndexToWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(8, diff.DeltaCount);
        // All staged* matches
        Assert.Contains(matched, m => m.path == "staged_changes_file_deleted" && m.spec == "staged*");
        Assert.Contains(matched, m => m.path == "staged_changes_modified_file" && m.spec == "staged*");
        Assert.Contains(matched, m => m.path == "staged_delete_modified_file" && m.spec == "staged*");
        Assert.Contains(matched, m => m.path == "staged_new_file_deleted_file" && m.spec == "staged*");
        Assert.Contains(matched, m => m.path == "staged_new_file_modified_file" && m.spec == "staged*");
        // All subdir/* matches
        Assert.Contains(matched, m => m.path == "subdir/deleted_file" && m.spec == "subdir/*");
        Assert.Contains(matched, m => m.path == "subdir/modified_file" && m.spec == "subdir/*");
        Assert.Contains(matched, m => m.path == "subdir/new_file" && m.spec == "subdir/*");
    }

    // notify.c notify_catchall: pathspec "*" matches 13 files.
    [Fact]
    public async Task Notify_Catchall_AllFilesMatched()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        var matched = new List<(string path, string? spec)>();
        var opts = new GitDiffOptions
        {
            Flags = NotifyFlags,
            PathSpecStrings = ["*"],
            Notify = (delta, spec) =>
            {
                matched.Add((delta.NewFile.Path?.ToUtf8String() ?? delta.OldFile.Path?.ToUtf8String() ?? "", spec?.ToUtf8String()));
                return 0;
            },
        };

        using GitDiff diff = await repo.DiffIndexToWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(13, diff.DeltaCount);
        // All should have spec = "*"
        Assert.All(matched, m => Assert.Equal("*", m.spec));
    }

    // notify.c notify_cb_can_abort_diff: notify returns -42 → diff aborts.
    [Fact]
    public async Task Notify_ReturnsNegative_AbortsDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        var opts = new GitDiffOptions
        {
            Flags = NotifyFlags,
            PathSpecStrings = ["file_deleted"],
            Notify = (_, _) => -42,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await repo.DiffIndexToWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken));
    }

    // notify.c notify_cb_can_be_used_as_filtering_function: notify returns
    // positive → all deltas skipped → 0 files in diff.
    [Fact]
    public async Task Notify_ReturnsPositive_FiltersAllDeltas()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        var opts = new GitDiffOptions
        {
            Flags = NotifyFlags,
            PathSpecStrings = ["*_deleted"],
            Notify = (_, _) => 42, // skip all
        };

        using GitDiff diff = await repo.DiffIndexToWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, diff.DeltaCount);
    }

    // Progress callback: verify it's called per-path during the walk.
    [Fact]
    public async Task Progress_ReportsPerPath()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        var progressReports = new List<GitDiffProgress>();
        var opts = new GitDiffOptions
        {
            Flags = NotifyFlags,
            PathSpecStrings = ["*_deleted"],
            Progress = new SynchronousProgress<GitDiffProgress>(p => progressReports.Add(p)),
        };

        using GitDiff diff = await repo.DiffIndexToWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken);

        // Progress should have been called at least once per path visited.
        Assert.NotEmpty(progressReports);
        Assert.Equal(2, diff.DeltaCount);
    }

    /// <summary>Simple synchronous IProgress that calls the handler directly.</summary>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
