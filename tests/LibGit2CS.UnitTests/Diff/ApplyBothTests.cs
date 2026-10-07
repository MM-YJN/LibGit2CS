using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

using GitDiff = LibGit2CS.Diff.GitDiff;

namespace LibGit2CS.UnitTests.Diff;

/// <summary>
/// Tests for <see cref="GitRepository.ApplyAsync"/> in BOTH mode (execute path).
/// Ported from <c>tests/libgit2/apply/both.c</c> (27 tests). Applies a diff
/// to both the workdir and the index and validates the result.
/// </summary>
public class ApplyBothTests : ApplyGoldenBase
{
    private const string AOid = "539bd011c4822c560c1d17cab095006b7a10f707";
    private const string BOid = "7c7bf85e978f1d18c0566f702d2cb7766b9c8d4f";

    // Expected entries after DIFF_MODIFY_TWO_FILES applied to 539bd01.
    private static readonly ApplyTestHelpers.ExpectedEntry[] s_modifiedTwo =
    [
        new(ApplyTestHelpers.ModeRegular, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
        new(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
        new(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
        new(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
        new(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
        new(ApplyTestHelpers.ModeRegular, "a7b066537e6be7109abfe4ff97b675d4e077da20", 0, "veal.txt"),
    ];

    // Expected entries after DIFF_DELETE_FILE applied to 539bd01 (gravy removed).
    private static readonly ApplyTestHelpers.ExpectedEntry[] s_deleted =
    [
        new(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
        new(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
        new(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
        new(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
        new(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
    ];

    // Expected entries after DIFF_ADD_FILE applied to 539bd01 (newfile added).
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
    /// Port of <c>test_apply_both__generated_diff</c> (both.c:26-64). Generates
    /// a tree-to-tree diff (539bd01 → 7c7bf85), applies to BOTH, validates.
    /// </summary>
    [Fact]
    public async Task ApplyBoth_GeneratedDiff()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();
        GitTree aTree = await ResolveTreeAsync(repo, AOid);
        GitTree bTree = await ResolveTreeAsync(repo, BOid);

        GitDiff diff = await repo.DiffTreeToTreeAsync(aTree, bTree, cancellationToken: TestContext.Current.CancellationToken);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexAsync(repo, s_modifiedTwo);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, s_modifiedTwo);
    }

    /// <summary>
    /// Port of <c>test_apply_both__parsed_diff</c> (both.c:66-89).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_ParsedDiff()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexAsync(repo, s_modifiedTwo);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, s_modifiedTwo);
    }

    /// <summary>
    /// Port of <c>test_apply_both__removes_file</c> (both.c:91-113).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_RemovesFile()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.DeleteFile);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexAsync(repo, s_deleted);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, s_deleted);
    }

    /// <summary>
    /// Port of <c>test_apply_both__adds_file</c> (both.c:115-139).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_AddsFile()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.AddFile);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        await ApplyTestHelpers.AssertIndexAsync(repo, s_added);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, s_added);
    }

    /// <summary>
    /// Port of <c>test_apply_both__application_failure_leaves_index_unmodified</c>
    /// (both.c:141-171). Mutate the index (remove veal.txt), apply fails, verify
    /// rollback leaves index and workdir unmodified.
    /// </summary>
    [Fact]
    public async Task ApplyBoth_ApplicationFailureLeavesIndexUnmodified()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Mutate the index: remove veal.txt. The patch modifies veal.txt, so
        // the preimage won't be found → GIT_EAPPLYFAIL.
        await ApplyTestHelpers.RemoveFromIndexAsync(repo, "veal.txt");

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.ApplyFail, ex.Code);

        // Index should have 5 entries (veal removed, others unchanged).
        ApplyTestHelpers.ExpectedEntry[] indexExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, indexExpected);
        await ApplyTestHelpers.AssertWorkdirUnchangedAsync(repo);
    }

    /// <summary>
    /// Port of <c>test_apply_both__index_must_match_workdir</c>
    /// (both.c:173-205). Append different content to workdir vs index for
    /// asparagus.txt, expect GIT_EAPPLYFAIL.
    /// </summary>
    [Fact]
    public async Task ApplyBoth_IndexMustMatchWorkdir()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Append a line to the workdir file.
        ApplyTestHelpers.AppendToFile(repo, "asparagus.txt", "This is a modification.\n");

        // Set the index entry to a different OID (the appended content hashes
        // differently than what the index thinks).
        await ApplyTestHelpers.AddEntryToIndexAsync(repo, "asparagus.txt",
            "06d3fefb8726ab1099acc76e02dfb85e034b2538", ApplyTestHelpers.ModeRegular);

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.ApplyFail, ex.Code);
    }

    /// <summary>
    /// Port of <c>test_apply_both__index_mode_must_match_workdir</c>
    /// (both.c:207-222). Chmod a workdir file to 0755, apply expects 0644 →
    /// GIT_EAPPLYFAIL. Skipped on platforms that don't support chmod.
    /// </summary>
    [Fact]
    public async Task ApplyBoth_IndexModeMustMatchWorkdir()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // chmod not supported
        }

        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Set asparagus.txt executable in the workdir.
        string fullPath = Path.Combine(repo.Workdir!, "asparagus.txt");
        File.SetUnixFileMode(fullPath, UnixFileMode.OtherExecute | UnixFileMode.GroupExecute | UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.ApplyFail, ex.Code);
    }

    /// <summary>
    /// Port of <c>test_apply_both__application_failure_leaves_workdir_unmodified</c>
    /// (both.c:224-257). Mutate the workdir (rewrite veal.txt), add to index,
    /// apply fails, verify workdir unchanged.
    /// </summary>
    [Fact]
    public async Task ApplyBoth_ApplicationFailureLeavesWorkdirUnmodified()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Mutate the workdir: rewrite veal.txt.
        ApplyTestHelpers.RewriteFile(repo, "veal.txt", "This is a modification.\n");
        await ApplyTestHelpers.AddByPathAsync(repo, "veal.txt");

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);

        GitException ex = await Assert.ThrowsAsync<GitException>(async () => await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken));
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
    /// Port of <c>test_apply_both__keeps_nonconflicting_changes</c>
    /// (both.c:259-311). Mutate index (change beef.txt, remove bouilli.txt) and
    /// workdir (remove oyster.txt, rewrite gravy.txt), then apply. Nonconflicting
    /// changes in both are preserved.
    /// </summary>
    [Fact]
    public async Task ApplyBoth_KeepsNonconflictingChanges()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Mutate the index: change beef.txt OID, remove bouilli.txt.
        await ApplyTestHelpers.AddEntryToIndexAsync(repo, "beef.txt",
            "898d12687fb35be271c27c795a6b32c8b51da79e", ApplyTestHelpers.ModeRegular);
        await ApplyTestHelpers.RemoveFromIndexAsync(repo, "bouilli.txt");

        // Mutate the workdir: remove oyster.txt, rewrite gravy.txt.
        ApplyTestHelpers.RemoveFile(repo, "oyster.txt");
        ApplyTestHelpers.RewriteFile(repo, "gravy.txt", "Hello, world.\n");

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] indexExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "898d12687fb35be271c27c795a6b32c8b51da79e", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "a7b066537e6be7109abfe4ff97b675d4e077da20", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, indexExpected);

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
    /// Port of <c>test_apply_both__can_apply_nonconflicting_file_changes</c>
    /// (both.c:313-351). Append a line to asparagus.txt in both index and
    /// workdir (same content), then apply. The patch still applies cleanly.
    /// </summary>
    [Fact]
    public async Task ApplyBoth_CanApplyNonconflictingFileChanges()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Append a line to asparagus.txt in the workdir and stage it.
        ApplyTestHelpers.AppendToFile(repo, "asparagus.txt",
            "This line is added in the index and the workdir.\n");
        await ApplyTestHelpers.AddByPathAsync(repo, "asparagus.txt");

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] bothExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f8a701c8a1a22c1729ee50faff1111f2d64f96fc", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "a7b066537e6be7109abfe4ff97b675d4e077da20", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, bothExpected);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, bothExpected);
    }

    /// <summary>
    /// Port of <c>test_apply_both__honors_crlf_attributes</c>
    /// (both.c:353-401). Create .gitattributes with text=auto, reset, apply.
    /// The index and workdir get different entries (workdir has .gitattributes).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_HonorsCrlfAttributes()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        // Create .gitattributes with text=auto.
        ApplyTestHelpers.MakeFile(repo, ".gitattributes", "* text=auto\n");

        // Remove asparagus.txt and veal.txt from workdir (they'll be recreated
        // by the hard reset below).
        ApplyTestHelpers.RemoveFile(repo, "asparagus.txt");
        ApplyTestHelpers.RemoveFile(repo, "veal.txt");

        // Hard reset to 539bd01 to get clean workdir + index.
        await ApplyTestHelpers.ResetToAsync(repo, AOid);

        var diff = GitDiff.FromBuffer(ApplyTestData.ModifyTwoFiles);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] indexExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "a7b066537e6be7109abfe4ff97b675d4e077da20", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, indexExpected);

        ApplyTestHelpers.ExpectedEntry[] workdirExpected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "176a458f94e0ea5272ce67c36bf30b6be9caf623", 0, ".gitattributes"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "ffb36e513f5fdf8a6ba850a20142676a2ac4807d", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "a7b066537e6be7109abfe4ff97b675d4e077da20", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertWorkdirAsync(repo, workdirExpected);
    }

    /// <summary>
    /// Port of <c>test_apply_both__rename</c> (both.c:403-426).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_Rename()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.RenameFile);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] expected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "notbeef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, expected);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, expected);
    }

    /// <summary>
    /// Port of <c>test_apply_both__rename_and_modify</c> (both.c:428-451).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_RenameAndModify()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.RenameAndModifyFile);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] expected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "6fa10147f00fe1fab1d5e835529a9dad53db8552", 0, "notbeef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, expected);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, expected);
    }

    /// <summary>
    /// Port of <c>test_apply_both__rename_a_to_b_to_c</c> (both.c:453-476).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_RenameAToBToC()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.RenameAToBToC);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] expected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "notbeef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, expected);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, expected);
    }

    /// <summary>
    /// Port of <c>test_apply_both__rename_a_to_b_to_c_exact</c> (both.c:478-501).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_RenameAToBToCExact()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.RenameAToBToCExact);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] expected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "notbeef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, expected);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, expected);
    }

    /// <summary>
    /// Port of <c>test_apply_both__rename_circular</c> (both.c:503-526).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_RenameCircular()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.RenameCircular);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] expected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, expected);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, expected);
    }

    /// <summary>
    /// Port of <c>test_apply_both__rename_2_to_1</c> (both.c:528-550).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_Rename2To1()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.Rename2To1);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] expected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "2.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, expected);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, expected);
    }

    /// <summary>
    /// Port of <c>test_apply_both__rename_1_to_2</c> (both.c:552-576).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_Rename1To2()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.Rename1To2);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] expected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "1.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "2.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, expected);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, expected);
    }

    /// <summary>
    /// Port of <c>test_apply_both__two_deltas_one_file</c> (both.c:578-601).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_TwoDeltasOneFile()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.TwoDeltasOneFile);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] expected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "0a9fd4415635e72573f0f6b5e68084cfe18f5075", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, expected);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, expected);
    }

    /// <summary>
    /// Port of <c>test_apply_both__two_deltas_one_new_file</c> (both.c:603-627).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_TwoDeltasOneNewFile()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.TwoDeltasOneNewFile);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] expected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "08d4c445cf0078f3d9b604b82f32f4d87e083325", 0, "newfile.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, expected);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, expected);
    }

    /// <summary>
    /// Port of <c>test_apply_both__rename_and_modify_deltas</c> (both.c:629-652).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_RenameAndModifyDeltas()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.RenameAndModifyDeltas);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] expected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "61c686bed39684eee8a2757ceb1291004a21333f", 0, "asdf.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, expected);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, expected);
    }

    /// <summary>
    /// Port of <c>test_apply_both__rename_delta_after_modify_delta</c>
    /// (both.c:654-678).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_RenameDeltaAfterModifyDelta()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.RenameAfterModify);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] expected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "f51658077d85f2264fa179b4d0848268cb3475c3", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "292cb60ce5e25c337c5b6e12957bbbfe1be4bf49", 0, "other.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c8c120f466591bbe3b8867361d5ec3cdd9fda756", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, expected);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, expected);
    }

    /// <summary>
    /// Port of <c>test_apply_both__cant_rename_after_modify_nonexistent_target_path</c>
    /// (both.c:680-689). Expects failure.
    /// </summary>
    [Fact]
    public async Task ApplyBoth_CantRenameAfterModifyNonexistentTargetPath()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.RenameAfterModifyTargetPath);

        await Assert.ThrowsAsync<GitException>(async () => await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Port of <c>test_apply_both__cant_modify_source_path_after_rename</c>
    /// (both.c:691-700). Expects failure.
    /// </summary>
    [Fact]
    public async Task ApplyBoth_CantModifySourcePathAfterRename()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.RenameAndModifySourcePath);

        await Assert.ThrowsAsync<GitException>(async () => await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Port of <c>test_apply_both__readd_deleted_file</c> (both.c:702-725).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_ReaddDeletedFile()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.DeleteAndReaddFile);
        await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken);

        ApplyTestHelpers.ExpectedEntry[] expected =
        [
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "2dc7f8b24ba27f3888368bd180df03ff4c6c6fab", 0, "asparagus.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68f6182f4c85d39e1309d97c7e456156dc9c0096", 0, "beef.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "4b7c5650008b2e747fe1809eeb5a1dde0e80850a", 0, "bouilli.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "c4e6cca3ec6ae0148ed231f97257df8c311e015f", 0, "gravy.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "68af1fc7407fd9addf1701a87eb1c95c7494c598", 0, "oyster.txt"),
            new ApplyTestHelpers.ExpectedEntry(ApplyTestHelpers.ModeRegular, "94d2c01087f48213bd157222d54edfefd77c9bba", 0, "veal.txt"),
        ];
        await ApplyTestHelpers.AssertIndexAsync(repo, expected);
        await ApplyTestHelpers.AssertWorkdirAsync(repo, expected);
    }

    /// <summary>
    /// Port of <c>test_apply_both__cant_remove_file_twice</c> (both.c:727-736).
    /// Expects failure (second delete can't find preimage).
    /// </summary>
    [Fact]
    public async Task ApplyBoth_CantRemoveFileTwice()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.RemoveFileTwice);

        await Assert.ThrowsAsync<GitException>(async () => await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Port of <c>test_apply_both__cant_add_invalid_filename</c> (both.c:738-747).
    /// Attempt to add a file under .git/ — expects failure.
    /// </summary>
    [Fact]
    public async Task ApplyBoth_CantAddInvalidFilename()
    {
        await using GitRepository repo = await OpenMergeRecursiveRepoAsync();

        var diff = GitDiff.FromBuffer(ApplyTestData.AddInvalidFilename);

        await Assert.ThrowsAsync<GitException>(async () => await repo.ApplyAsync(diff, GitApplyLocation.Both, cancellationToken: TestContext.Current.CancellationToken));
    }
}
