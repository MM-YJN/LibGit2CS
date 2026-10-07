using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Byte-exact + statistical golden tests for diff stats, ported from
/// libgit2's <c>tests/libgit2/diff/stats.c</c> against the
/// <c>diff_format_email</c> fixture. Uses <see cref="GitDiff.Commit"/>
/// (commit-vs-first-parent), matching clar's <c>git_diff__commit</c>. Expected
/// output captured by <c>generate-goldens.sh</c> via <c>git diff --stat</c> /
/// <c>--numstat</c> / <c>--shortstat</c>.
/// </summary>
public sealed class StatsGoldenTests : DiffGoldenBase
{
    private static async Task<GitDiff> CommitDiffAsync(GitRepository repo, string commitOidHex)
    {
        var oid = GitOid.Parse(commitOidHex.AsSpan(), repo.ObjectFormat);
        Commit commit = await repo.ObjectLookupAsync<Commit>(oid, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"commit {commitOidHex} not found");
        return await repo.DiffCommitAsync(commit);
    }

    // stats.c stat: 9264b96 "Modify some content" — 1 file, 5 ins, 3 del.
    [Fact]
    public async Task Stat_Full_9264b96_MatchesGitDiffStat()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        using GitDiff diff = await CommitDiffAsync(repo, "9264b96c6d104d0e07ae33d3007b6a48246c6f92");

        GitDiffStats stats = await diff.GetStatsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(LoadExpected("stat_9264b96_full"), FormatStats(stats, GitDiffStatsFormat.Full));
        Assert.Equal(1, stats.FilesChanged);
        Assert.Equal(5, stats.Insertions);
        Assert.Equal(3, stats.Deletions);
    }

    // stats.c shortstat: summary line only.
    [Fact]
    public async Task Stat_Short_9264b96_MatchesGitDiffShortstat()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        using GitDiff diff = await CommitDiffAsync(repo, "9264b96c6d104d0e07ae33d3007b6a48246c6f92");

        GitDiffStats stats = await diff.GetStatsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(LoadExpected("stat_9264b96_short"), FormatStats(stats, GitDiffStatsFormat.Short));
    }

    // stats.c multiple_hunks: cd471f0 "Multi-hunk changes" — file2 + file3.
    [Fact]
    public async Task Stat_Full_cd471f0_MatchesGitDiffStat()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        using GitDiff diff = await CommitDiffAsync(repo, "cd471f0d8770371e1bc78bcbb38db4c7e4106bd2");

        GitDiffStats stats = await diff.GetStatsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(LoadExpected("stat_cd471f0_full"), FormatStats(stats, GitDiffStatsFormat.Full));
        Assert.Equal(2, stats.FilesChanged);
        Assert.Equal(7, stats.Insertions);
        Assert.Equal(4, stats.Deletions);
    }

    // stats.c numstat: "INS     DEL     path" per file (%-8 format).
    [Fact]
    public async Task Stat_Number_cd471f0_MatchesGitDiffNumstat()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        using GitDiff diff = await CommitDiffAsync(repo, "cd471f0d8770371e1bc78bcbb38db4c7e4106bd2");

        GitDiffStats stats = await diff.GetStatsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(LoadExpected("stat_cd471f0_num"), FormatStats(stats, GitDiffStatsFormat.Number));
    }

    // stats.c binary: "Bin <old> -> <new> bytes" in FULL format.
    [Fact]
    public async Task Stat_Binary_Full_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        using GitDiff diff = await CommitDiffAsync(repo, "8d7523f6fcb2404257889abe0d96f093d9f524f9");

        GitDiffStats stats = await diff.GetStatsAsync(cancellationToken: TestContext.Current.CancellationToken);
        string expected =
            " binary.bin | Bin 3 -> 5 bytes\n" +
            " 1 file changed, 0 insertions(+), 0 deletions(-)\n";

        Assert.Equal(expected, FormatStats(stats, GitDiffStatsFormat.Full));
        Assert.Equal(1, stats.FilesChanged);
        Assert.Equal(0, stats.Insertions);
        Assert.Equal(0, stats.Deletions);
    }

    // stats.c binary_numstat: "-       -       binary.bin" (%-8 format).
    [Fact]
    public async Task Stat_Binary_Numstat_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        using GitDiff diff = await CommitDiffAsync(repo, "8d7523f6fcb2404257889abe0d96f093d9f524f9");

        GitDiffStats stats = await diff.GetStatsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("-       -       binary.bin\n", FormatStats(stats, GitDiffStatsFormat.Number));
    }

    // stats.c mode_change: FULL | INCLUDE_SUMMARY with mode change line.
    [Fact]
    public async Task Stat_ModeChange_FullWithSummary_MatchesGitDiff()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("diff_format_email");
        using GitDiff diff = await CommitDiffAsync(repo, "7ade76dd34bba4733cf9878079f9fd4a456a9189");

        GitDiffStats stats = await diff.GetStatsAsync(cancellationToken: TestContext.Current.CancellationToken);
        string expected =
            " file1.txt.renamed | 0\n" +
            " 1 file changed, 0 insertions(+), 0 deletions(-)\n" +
            " mode change 100644 => 100755 file1.txt.renamed\n";

        Assert.Equal(expected, FormatStats(stats, GitDiffStatsFormat.Full | GitDiffStatsFormat.IncludeSummary));
    }

    // Rename stat path display: "old => new" format with rename-aware MaxName.
    // Uses the renames fixture with FindSimilar to detect serving.txt → sixserving.txt.
    [Fact]
    public async Task Stat_Rename_Full_ShowsArrowPathDisplay()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("renames");
        GitTree oldTree = await ResolveTreeAsync(repo, "31e47d8c1fa36d7f8d537b96158e3f024de0a9f2");
        GitTree newTree = await ResolveTreeAsync(repo, "2bc7f351d20b53f1c72c16c4b036e491c478c49a");

        using GitDiff diff = await repo.DiffTreeToTreeAsync(oldTree, newTree, cancellationToken: TestContext.Current.CancellationToken);
        await diff.FindSimilarAsync(new GitDiffFindOptions { Flags = GitDiffFindFlags.Renames }, cancellationToken: TestContext.Current.CancellationToken);

        GitDiffStats stats = await diff.GetStatsAsync(cancellationToken: TestContext.Current.CancellationToken);
        string output = FormatStats(stats, GitDiffStatsFormat.Full);

        // The rename (serving.txt => sixserving.txt) should appear with " => "
        // separator. The added file (songofseven.txt) should appear normally.
        Assert.Contains(" serving.txt => sixserving.txt | ", output);
        Assert.Contains("songofseven.txt", output);
    }
}
