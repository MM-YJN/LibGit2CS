using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitDiff = LibGit2CS.Diff.GitDiff;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Tests for <see cref="GitRepository.ApplyAsync"/> in Workdir mode (execute path).
/// Ported from <c>tests/libgit2/apply/workdir.c</c> (11 tests). Applies a diff
/// to the working directory only and validates the result.
/// </summary>
public class ApplyWorkdirTests : ApplyGoldenBase
{
    private const string AOid = "539bd011c4822c560c1d17cab095006b7a10f707";
    private const string BOid = "7c7bf85e978f1d18c0566f702d2cb7766b9c8d4f";

    // Expected workdir entries after DIFF_MODIFY_TWO_FILES.
    private static readonly ApplyTestHelpers.ExpectedEntry[] s_modified =
    [
        new(ApplyTestHelpers.ModeRegular, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
        new(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
        new(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
        new(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
        new(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
        new(ApplyTestHelpers.ModeRegular, "a7b066537e6be7109abfe4ff97b675d4e077da20", 0, "veal.txt"),
    ];

    // Expected workdir entries after DIFF_DELETE_FILE.
    private static readonly ApplyTestHelpers.ExpectedEntry[] s_deleted =
    [
        new(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
        new(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
        new(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
        new(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
        new(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
    ];

    // Expected workdir entries after DIFF_ADD_FILE.
    private static readonly ApplyTestHelpers.ExpectedEntry[] s_added =
    [
        new(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
        new(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
        new(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
        new(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
        new(ApplyTestHelpers.ModeRegular, "6370543fcfedb3e6516ec53b06158f3687dc1447", 0, "newfile.txt"),
        new(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
        new(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
    ];

    /// <summary>
    /// Port of <c>test_apply_workdir__generated_diff</c> (workdir.c:26-63).
    /// </summary>
    [Fact]
    public async Task ApplyWorkdir_GeneratedDiff()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();
        GitTree aTree = await ResolveTreeAsync(repo, AOid);
        GitTree bTree = await ResolveTreeAsync(repo, BOid);

        GitDiff diff = await repo.DiffTreeToTreeAsync(aTree, bTree, cancellationToken: TestContext.Current.CancellationToken);
        await repo.ApplyAsync(diff, GitApplyLocation.Workdir, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexUnchangedAsync(repo);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, s_modified);
    }

    /// <summary>
    /// Port of <c>test_apply_workdir__parsed_diff</c> (workdir.c:65-88).
    /// </summary>
    [Fact]
    public async Task ApplyWorkdir_ParsedDiff()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);
        await repo.ApplyAsync(diff, GitApplyLocation.Workdir, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexUnchangedAsync(repo);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, s_modified);
    }

    /// <summary>
    /// Port of <c>test_apply_workdir__removes_file</c> (workdir.c:90-112).
    /// </summary>
    [Fact]
    public async Task ApplyWorkdir_RemovesFile()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.DeleteFile);
        await repo.ApplyAsync(diff, GitApplyLocation.Workdir, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexUnchangedAsync(repo);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, s_deleted);
    }

    /// <summary>
    /// Port of <c>test_apply_workdir__adds_file</c> (workdir.c:114-138).
    /// </summary>
    [Fact]
    public async Task ApplyWorkdir_AddsFile()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.AddFile);
        await repo.ApplyAsync(diff, GitApplyLocation.Workdir, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexUnchangedAsync(repo);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, s_added);
    }

    /// <summary>
    /// Port of <c>test_apply_workdir__modified_index_with_unmodified_workdir_is_ok</c>
    /// (workdir.c:140-188). Mutate index (change veal.txt, remove asparagus.txt),
    /// apply to workdir. The index is not touched by workdir apply, so the
    /// mutation persists.
    /// </summary>
    [Fact]
    public async Task ApplyWorkdir_ModifiedIndexWithUnmodifiedWorkdirIsOk()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Mutate the index: change veal.txt OID, remove asparagus.txt.
        await ApplyTestHelpers.AddEntryToIndexAsync(repo, "veal.txt",
            "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", ApplyTestHelpers.ModeRegular);
        await ApplyTestHelpers.RemoveFromIndexAsync(repo, "asparagus.txt");

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);
        await repo.ApplyAsync(diff, GitApplyLocation.Workdir, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] indexExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, indexExpected);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, s_modified);
    }

    /// <summary>
    /// Port of <c>test_apply_workdir__application_failure_leaves_workdir_unmodified</c>
    /// (workdir.c:190-217). Mutate veal.txt in workdir, apply fails.
    /// </summary>
    [Fact]
    public async Task ApplyWorkdir_ApplicationFailureLeavesWorkdirUnmodified()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Mutate the workdir: rewrite veal.txt.
        ApplyTestHelpers.RewriteFile(repo, "veal.txt", "This is a modification.\n");

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.ApplyAsync(diff, GitApplyLocation.Workdir, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.ApplyFail, ex.Code);

        ApplyTestHelpers.ExpectedEntry[] workdirExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "8684724651336001c5dbce74bed6736d2443958d", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertWorkdirAsync(repo, workdirExpected);
    }

    /// <summary>
    /// Port of <c>test_apply_workdir__keeps_nonconflicting_changes</c>
    /// (workdir.c:219-244). Remove oyster.txt and rewrite gravy.txt in workdir,
    /// apply. Nonconflicting changes are preserved.
    /// </summary>
    [Fact]
    public async Task ApplyWorkdir_KeepsNonconflictingChanges()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Mutate the workdir: remove oyster.txt, rewrite gravy.txt.
        ApplyTestHelpers.RemoveFile(repo, "oyster.txt");
        ApplyTestHelpers.RewriteFile(repo, "gravy.txt", "Hello, world.\n");

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);
        await repo.ApplyAsync(diff, GitApplyLocation.Workdir, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexUnchangedAsync(repo);

        ApplyTestHelpers.ExpectedEntry[] workdirExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f75ba05f340c51065cbea2e1fdbfe5fe13144c97", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "a7b066537e6be7109abfe4ff97b675d4e077da20", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertWorkdirAsync(repo, workdirExpected);
    }

    /// <summary>
    /// Port of <c>test_apply_workdir__can_apply_nonconflicting_file_changes</c>
    /// (workdir.c:246-278). Append a line to asparagus.txt in the workdir,
    /// apply. The patch still applies cleanly.
    /// </summary>
    [Fact]
    public async Task ApplyWorkdir_CanApplyNonconflictingFileChanges()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Append a line to asparagus.txt in the workdir.
        ApplyTestHelpers.AppendToFile(repo, "asparagus.txt",
            "This line is added in the workdir.\n");

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);
        await repo.ApplyAsync(diff, GitApplyLocation.Workdir, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexUnchangedAsync(repo);

        ApplyTestHelpers.ExpectedEntry[] workdirExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "5db1a0fef164cb66cc0c00d35cc5af979ddc1a64", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "a7b066537e6be7109abfe4ff97b675d4e077da20", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertWorkdirAsync(repo, workdirExpected);
    }

    /// <summary>
    /// Port of <c>test_apply_workdir__change_mode</c> (workdir.c:280-306).
    /// Apply mode-only change (0644→755) to beef.txt. Skipped on Windows.
    /// </summary>
    [Fact]
    public async Task ApplyWorkdir_ChangeMode()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // chmod not supported on Windows
        }

        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.ExecutableFile);
        await repo.ApplyAsync(diff, GitApplyLocation.Workdir, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexUnchangedAsync(repo);

        ApplyTestHelpers.ExpectedEntry[] workdirExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeExecutable, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertWorkdirAsync(repo, workdirExpected);
    }

    /// <summary>
    /// Port of <c>test_apply_workdir__apply_many_changes_one</c>
    /// (workdir.c:308-332).
    /// </summary>
    [Fact]
    public async Task ApplyWorkdir_ApplyManyChangesOne()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.ManyChangesOne);
        await repo.ApplyAsync(diff, GitApplyLocation.Workdir, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexUnchangedAsync(repo);

        ApplyTestHelpers.ExpectedEntry[] workdirExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c9d7d5d58088bc91f6e06f17ca3a205091568d3a", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertWorkdirAsync(repo, workdirExpected);
    }

    /// <summary>
    /// Port of <c>test_apply_workdir__apply_many_changes_two</c>
    /// (workdir.c:334-358).
    /// </summary>
    [Fact]
    public async Task ApplyWorkdir_ApplyManyChangesTwo()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.ManyChangesTwo);
        await repo.ApplyAsync(diff, GitApplyLocation.Workdir, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexUnchangedAsync(repo);

        ApplyTestHelpers.ExpectedEntry[] workdirExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "6b943d65af6d8db74d747284fa4ca7d716ad5bbb", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertWorkdirAsync(repo, workdirExpected);
    }
}
