using LibGit2CS.Repository;
using LibGit2CS.Revwalk;

namespace LibGit2CS.UnitTests.Status;

/// <summary>
/// Workdir describe (<c>-dirty</c> suffix) tests. Ported from the describe
/// workdir scenarios in <c>tests/libgit2/describe/</c>. Validates that
/// <see cref="GitRepository.DescribeWorkdirAsync"/> runs status and appends
/// <see cref="GitDescribeOptions.DirtySuffix"/> when the workdir is dirty.
/// </summary>
public class DescribeWorkdirTests : StatusGoldenBase
{
    [Fact]
    public async Task DescribeWorkdir_DirtyRepo_NoSuffixByDefault()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        // The status fixture has a dirty workdir (many modified/deleted/new files).
        // C's GIT_DESCRIBE_FORMAT_OPTIONS_INIT leaves dirty_suffix NULL, so the
        // DEFAULT describe output has no suffix (describe.c:814-815 appends only
        // when opts.dirty_suffix is set).
        var opts = new GitDescribeOptions { ShowCommitOidAsFallback = true };
        string result = await repo.DescribeWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain("-dirty", result);
    }

    [Fact]
    public async Task DescribeWorkdir_ExplicitDirtySuffix_AppendsSuffix()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        // libgit2's own describe tests pass dirty_suffix = "-dirty" explicitly
        // (describe_helpers.c) — with the explicit value the suffix is appended.
        var opts = new GitDescribeOptions
        {
            ShowCommitOidAsFallback = true,
            DirtySuffix = "-dirty",
        };
        string result = await repo.DescribeWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("-dirty", result);
    }

    [Fact]
    public async Task DescribeWorkdir_NoDirtySuffix_WhenSuffixNull()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        var opts = new GitDescribeOptions
        {
            ShowCommitOidAsFallback = true,
            DirtySuffix = null,
        };

        string result = await repo.DescribeWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain("-dirty", result);
    }

    [Fact]
    public async Task DescribeWorkdir_CustomSuffix()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        var opts = new GitDescribeOptions
        {
            ShowCommitOidAsFallback = true,
            DirtySuffix = " (dirty)",
        };

        string result = await repo.DescribeWorkdirAsync(opts, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(" (dirty)", result);
    }
}
