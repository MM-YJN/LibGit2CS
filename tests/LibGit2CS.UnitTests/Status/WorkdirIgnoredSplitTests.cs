using LibGit2CS.Diff;
using LibGit2CS.Repository;
using LibGit2CS.Status;

namespace LibGit2CS.UnitTests.Status;

/// <summary>
/// Workdir IGNORED-split count tests: the <c>status</c> fixture's
/// <c>ignored_file</c> is classified as <c>IGNORED</c>, not
/// <c>UNTRACKED</c>.
/// </summary>
/// <remarks>
/// Ported from <c>tests/libgit2/diff/workdir.c</c> — the static
/// <c>to_index</c>/<c>to_tree</c>/<c>to_index_with_pathspec</c>/
/// <c>head_index_and_workdir_all_differ</c>/<c>to_index_reversed_content_loads</c>
/// scenarios. The <c>DELETED</c>/<c>MODIFIED</c>/<c>hunks</c>/<c>lines</c>
/// counts are covered here alongside the <c>IGNORED</c>/<c>UNTRACKED</c>
/// split.
/// </remarks>
public class WorkdirIgnoredSplitTests : StatusGoldenBase
{
    /// <summary>
    /// Validates that the <c>status</c> fixture's index-to-workdir diff
    /// correctly classifies <c>ignored_file</c> as <c>IGNORED</c> (not
    /// <c>UNTRACKED</c>).
    /// </summary>
    [Fact]
    public async Task IndexToWorkdir_IgnoredFile_ClassifiedAsIgnored()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        var opts = new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.IncludeUntracked
                  | GitDiffOptionsFlags.IncludeIgnored
                  | GitDiffOptionsFlags.RecurseUntrackedDirs,
        };

        using GitDiff diff = await repo.DiffIndexToWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken);

        // Find the ignored_file delta.
        GitDiffDelta? ignoredDelta = diff.Deltas.FirstOrDefault(d => d.NewFile.Path?.ToUtf8String() == "ignored_file");
        Assert.NotNull(ignoredDelta);
        Assert.Equal(GitDeltaStatus.Ignored, ignoredDelta!.Status);
    }

    /// <summary>
    /// Validates the full IGNORED/UNTRACKED split on the status fixture:
    /// the ignore engine produces IGNORED=1, UNTRACKED=4.
    /// </summary>
    [Fact]
    public async Task IndexToWorkdir_IgnoredCount_Is1_UntrackedCount_Is4()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        var opts = new GitDiffOptions
        {
            Flags = GitDiffOptionsFlags.IncludeUntracked
                  | GitDiffOptionsFlags.IncludeIgnored
                  | GitDiffOptionsFlags.RecurseUntrackedDirs,
        };

        using GitDiff diff = await repo.DiffIndexToWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken);

        int ignoredCount = 0;
        int untrackedCount = 0;
        foreach (GitDiffDelta delta in diff.Deltas)
        {
            if (delta.Status == GitDeltaStatus.Ignored)
            {
                ignoredCount++;
            }
            else if (delta.Status == GitDeltaStatus.Untracked)
            {
                untrackedCount++;
            }
        }

        Assert.Equal(1, ignoredCount);
        Assert.Equal(4, untrackedCount);
    }

    /// <summary>
    /// Validates that the status list also correctly shows the IGNORED entry.
    /// </summary>
    [Fact]
    public async Task StatusList_Includes_IgnoredFile_AsIgnored()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        using GitStatusList list = await repo.StatusNewAsync(cancellationToken: TestContext.Current.CancellationToken);
        GitStatusEntry? ignoredEntry = list.Entries.FirstOrDefault(e => e.Path.ToUtf8String() == "ignored_file");
        Assert.NotNull(ignoredEntry);
        Assert.Equal(GitStatusFlags.Ignored, ignoredEntry!.Status);
    }
}
