using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitDiff = LibGit2CS.Diff.GitDiff;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Tests for <see cref="GitRepository.ApplyAsync"/> in Index mode (execute path).
/// Ported from <c>tests/libgit2/apply/index.c</c> (9 tests). Applies a diff
/// to the index only and validates the result.
/// </summary>
public class ApplyIndexTests : ApplyGoldenBase
{
    private const string AOid = "539bd011c4822c560c1d17cab095006b7a10f707";
    private const string BOid = "7c7bf85e978f1d18c0566f702d2cb7766b9c8d4f";

    // Expected index entries after DIFF_MODIFY_TWO_FILES.
    private static readonly ApplyTestHelpers.ExpectedEntry[] s_modified =
    [
        new(ApplyTestHelpers.ModeRegular, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
        new(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
        new(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
        new(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
        new(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
        new(ApplyTestHelpers.ModeRegular, "a7b066537e6be7109abfe4ff97b675d4e077da20", 0, "veal.txt"),
    ];

    // Expected index entries after DIFF_DELETE_FILE.
    private static readonly ApplyTestHelpers.ExpectedEntry[] s_deleted =
    [
        new(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
        new(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
        new(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
        new(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
        new(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
    ];

    // Expected index entries after DIFF_ADD_FILE.
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
    /// Port of <c>test_apply_index__generate_diff</c> (index.c:26-64).
    /// </summary>
    [Fact]
    public async Task ApplyIndex_GenerateDiff()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();
        GitTree aTree = await ResolveTreeAsync(repo, AOid);
        GitTree bTree = await ResolveTreeAsync(repo, BOid);

        GitDiff diff = await repo.DiffTreeToTreeAsync(aTree, bTree, cancellationToken: TestContext.Current.CancellationToken);
        await repo.ApplyAsync(diff, GitApplyLocation.Index, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexAsync(repo, s_modified);
        await ApplyTestHelpers.AssertWorkdirUnchangedAsync(repo);
    }

    /// <summary>
    /// Port of <c>test_apply_index__parsed_diff</c> (index.c:66-89).
    /// </summary>
    [Fact]
    public async Task ApplyIndex_ParsedDiff()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);
        await repo.ApplyAsync(diff, GitApplyLocation.Index, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexAsync(repo, s_modified);
        await ApplyTestHelpers.AssertWorkdirUnchangedAsync(repo);
    }

    /// <summary>
    /// Port of <c>test_apply_index__removes_file</c> (index.c:91-113).
    /// </summary>
    [Fact]
    public async Task ApplyIndex_RemovesFile()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.DeleteFile);
        await repo.ApplyAsync(diff, GitApplyLocation.Index, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexAsync(repo, s_deleted);
        await ApplyTestHelpers.AssertWorkdirUnchangedAsync(repo);
    }

    /// <summary>
    /// Port of <c>test_apply_index__adds_file</c> (index.c:115-139).
    /// </summary>
    [Fact]
    public async Task ApplyIndex_AddsFile()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.AddFile);
        await repo.ApplyAsync(diff, GitApplyLocation.Index, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexAsync(repo, s_added);
        await ApplyTestHelpers.AssertWorkdirUnchangedAsync(repo);
    }

    /// <summary>
    /// Port of <c>test_apply_index__modified_workdir_with_unmodified_index_is_ok</c>
    /// (index.c:141-179). Mutate workdir (remove asparagus.txt, rewrite veal.txt),
    /// apply to index. The workdir is not touched by index apply.
    /// </summary>
    [Fact]
    public async Task ApplyIndex_ModifiedWorkdirWithUnmodifiedIndexIsOk()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Mutate the workdir: remove asparagus.txt, rewrite veal.txt.
        ApplyTestHelpers.RemoveFile(repo, "asparagus.txt");
        ApplyTestHelpers.RewriteFile(repo, "veal.txt", "Hello, world.\n");

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);
        await repo.ApplyAsync(diff, GitApplyLocation.Index, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexAsync(repo, s_modified);

        ApplyTestHelpers.ExpectedEntry[] workdirExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f75ba05f340c51065cbea2e1fdbfe5fe13144c97", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertWorkdirAsync(repo, workdirExpected);
    }

    /// <summary>
    /// Port of <c>test_apply_index__application_failure_leaves_index_unmodified</c>
    /// (index.c:181-210). Mutate index (remove veal.txt), apply fails.
    /// </summary>
    [Fact]
    public async Task ApplyIndex_ApplicationFailureLeavesIndexUnmodified()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Mutate the index: remove veal.txt.
        await ApplyTestHelpers.RemoveFromIndexAsync(repo, "veal.txt");

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.ApplyAsync(diff, GitApplyLocation.Index, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.ApplyFail, ex.Code);

        ApplyTestHelpers.ExpectedEntry[] indexExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, indexExpected);
    }

    /// <summary>
    /// Port of <c>test_apply_index__keeps_nonconflicting_changes</c>
    /// (index.c:212-250). Mutate index (change beef.txt, remove bouilli.txt),
    /// apply. Nonconflicting changes are preserved.
    /// </summary>
    [Fact]
    public async Task ApplyIndex_KeepsNonconflictingChanges()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Mutate the index: change beef.txt OID, remove bouilli.txt.
        await ApplyTestHelpers.AddEntryToIndexAsync(repo, "beef.txt",
            "898d12687fb35be271c27c795a6b32c8b51da79e", ApplyTestHelpers.ModeRegular);
        await ApplyTestHelpers.RemoveFromIndexAsync(repo, "bouilli.txt");

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);
        await repo.ApplyAsync(diff, GitApplyLocation.Index, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] indexExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "898d12687fb35be271c27c795a6b32c8b51da79e", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "a7b066537e6be7109abfe4ff97b675d4e077da20", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, indexExpected);
        await ApplyTestHelpers.AssertWorkdirUnchangedAsync(repo);
    }

    /// <summary>
    /// Port of <c>test_apply_index__can_apply_nonconflicting_file_changes</c>
    /// (index.c:252-295). Replace index entry for asparagus.txt with a version
    /// that has a line appended, apply. The patch still applies cleanly.
    /// </summary>
    [Fact]
    public async Task ApplyIndex_CanApplyNonconflictingFileChanges()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Replace the index entry for asparagus.txt with a version that has
        // a new line appended (different OID than HEAD).
        await ApplyTestHelpers.AddEntryToIndexAsync(repo, "asparagus.txt",
            "06d3fefb8726ab1099acc76e02dfb85e034b2538", ApplyTestHelpers.ModeRegular);

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);
        await repo.ApplyAsync(diff, GitApplyLocation.Index, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] indexExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4f2d1645dee99ced096877911de540c65ade2ef8", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "a7b066537e6be7109abfe4ff97b675d4e077da20", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, indexExpected);
        await ApplyTestHelpers.AssertWorkdirUnchangedAsync(repo);
    }

    /// <summary>
    /// Port of <c>test_apply_index__change_mode</c> (index.c:297-321).
    /// Apply mode-only change (0644→755) to beef.txt in the index.
    /// </summary>
    [Fact]
    public async Task ApplyIndex_ChangeMode()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.ExecutableFile);
        await repo.ApplyAsync(diff, GitApplyLocation.Index, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] indexExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeExecutable, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, indexExpected);
        await ApplyTestHelpers.AssertWorkdirUnchangedAsync(repo);
    }
}
