using LibGit2CS.Core;
using LibGit2CS.Repository;

using GitStatusFlags = LibGit2CS.Status.GitStatusFlags;
using GitStatusList = LibGit2CS.Status.GitStatusList;
using GitStatusOptions = LibGit2CS.Status.GitStatusOptions;
using GitStatusShow = LibGit2CS.Status.GitStatusShow;

namespace LibGit2CS.UnitTests.Status;

/// <summary>
/// Status engine tests on the <c>status</c> fixture. Ported from
/// <c>tests/libgit2/status/worktree.c</c> — the read-only, no-index-write
/// subset. Validates the full status pipeline: HEAD→index diff +
/// index→workdir diff + ignore evaluation + paired-foreach merge.
/// </summary>
/// <remarks>
/// Expected data arrays come from <c>status_data.h</c>. The <c>status</c>
/// fixture (16 entries in the pristine copy) is already packaged at
/// <c>Fixtures/diff/status.zip</c>.
/// </remarks>
public class WorktreeStatusTests : StatusGoldenBase
{
    // ━━ entry_paths0 / entry_statuses0 / entry_count0 ━━
    // The pristine "status" fixture: 16 entries (status_data.h:9-51).

    private static readonly string[] s_entryPaths0 =
    [
        "file_deleted",
        "ignored_file",
        "modified_file",
        "new_file",
        "staged_changes",
        "staged_changes_file_deleted",
        "staged_changes_modified_file",
        "staged_delete_file_deleted",
        "staged_delete_modified_file",
        "staged_new_file",
        "staged_new_file_deleted_file",
        "staged_new_file_modified_file",
        "subdir/deleted_file",
        "subdir/modified_file",
        "subdir/new_file",
        "\u8fd9",
    ];

    private static readonly GitStatusFlags[] s_entryStatuses0 =
    [
        GitStatusFlags.WorkdirDeleted,
        GitStatusFlags.Ignored,
        GitStatusFlags.WorkdirModified,
        GitStatusFlags.WorkdirNew,
        GitStatusFlags.IndexModified,
        GitStatusFlags.IndexModified | GitStatusFlags.WorkdirDeleted,
        GitStatusFlags.IndexModified | GitStatusFlags.WorkdirModified,
        GitStatusFlags.IndexDeleted,
        GitStatusFlags.IndexDeleted | GitStatusFlags.WorkdirNew,
        GitStatusFlags.IndexNew,
        GitStatusFlags.IndexNew | GitStatusFlags.WorkdirDeleted,
        GitStatusFlags.IndexNew | GitStatusFlags.WorkdirModified,
        GitStatusFlags.WorkdirDeleted,
        GitStatusFlags.WorkdirModified,
        GitStatusFlags.WorkdirNew,
        GitStatusFlags.WorkdirNew,
    ];

    // ━━ entry_paths5 / entry_statuses5 / entry_count5 ━━
    // Index-only view: 8 staged entries (status_data.h:264-286).

    private static readonly string[] s_entryPaths5 =
    [
        "staged_changes",
        "staged_changes_file_deleted",
        "staged_changes_modified_file",
        "staged_delete_file_deleted",
        "staged_delete_modified_file",
        "staged_new_file",
        "staged_new_file_deleted_file",
        "staged_new_file_modified_file",
    ];

    private static readonly GitStatusFlags[] s_entryStatuses5 =
    [
        GitStatusFlags.IndexModified,
        GitStatusFlags.IndexModified,
        GitStatusFlags.IndexModified,
        GitStatusFlags.IndexDeleted,
        GitStatusFlags.IndexDeleted,
        GitStatusFlags.IndexNew,
        GitStatusFlags.IndexNew,
        GitStatusFlags.IndexNew,
    ];

    // ━━ entry_paths6 / entry_statuses6 / entry_count6 ━━
    // Workdir-only view: 13 entries (status_data.h:294-326).

    private static readonly string[] s_entryPaths6 =
    [
        "file_deleted",
        "ignored_file",
        "modified_file",
        "new_file",
        "staged_changes_file_deleted",
        "staged_changes_modified_file",
        "staged_delete_modified_file",
        "staged_new_file_deleted_file",
        "staged_new_file_modified_file",
        "subdir/deleted_file",
        "subdir/modified_file",
        "subdir/new_file",
        "\u8fd9",
    ];

    private static readonly GitStatusFlags[] s_entryStatuses6 =
    [
        GitStatusFlags.WorkdirDeleted,
        GitStatusFlags.Ignored,
        GitStatusFlags.WorkdirModified,
        GitStatusFlags.WorkdirNew,
        GitStatusFlags.WorkdirDeleted,
        GitStatusFlags.WorkdirModified,
        GitStatusFlags.WorkdirNew,
        GitStatusFlags.WorkdirDeleted,
        GitStatusFlags.WorkdirModified,
        GitStatusFlags.WorkdirDeleted,
        GitStatusFlags.WorkdirModified,
        GitStatusFlags.WorkdirNew,
        GitStatusFlags.WorkdirNew,
    ];

    [Fact]
    public async Task WholeRepository_DefaultOptions_Yields16Entries()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        using GitStatusList list = await repo.StatusNewAsync(cancellationToken: TestContext.Current.CancellationToken);
        StatusCounter.AssertEntries(list, s_entryPaths0, s_entryStatuses0);
    }

    [Fact]
    public async Task ShowIndexAndWorkdir_Explicit_Yields16Entries()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        var opts = new GitStatusOptions
        {
            Show = GitStatusShow.IndexAndWorkdir,
            Flags = GitStatusFlags.IncludeIgnored
                  | GitStatusFlags.IncludeUntracked
                  | GitStatusFlags.RecurseUntrackedDirs,
        };

        using GitStatusList list = await repo.StatusNewAsync(opts, cancellationToken: TestContext.Current.CancellationToken);
        StatusCounter.AssertEntries(list, s_entryPaths0, s_entryStatuses0);
    }

    [Fact]
    public async Task ShowIndexOnly_Yields8StagedEntries()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        var opts = new GitStatusOptions
        {
            Show = GitStatusShow.IndexOnly,
            Flags = GitStatusFlags.IncludeIgnored
                  | GitStatusFlags.IncludeUntracked
                  | GitStatusFlags.RecurseUntrackedDirs,
        };

        using GitStatusList list = await repo.StatusNewAsync(opts, cancellationToken: TestContext.Current.CancellationToken);
        StatusCounter.AssertEntries(list, s_entryPaths5, s_entryStatuses5);
    }

    [Fact]
    public async Task ShowWorkdirOnly_Yields13WorkdirEntries()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        var opts = new GitStatusOptions
        {
            Show = GitStatusShow.WorkdirOnly,
            Flags = GitStatusFlags.IncludeIgnored
                  | GitStatusFlags.IncludeUntracked
                  | GitStatusFlags.RecurseUntrackedDirs,
        };

        using GitStatusList list = await repo.StatusNewAsync(opts, cancellationToken: TestContext.Current.CancellationToken);
        StatusCounter.AssertEntries(list, s_entryPaths6, s_entryStatuses6);
    }

    [Fact]
    public async Task EmptyRepository_YieldsZeroEntries()
    {
        await using GitRepository repo = await OpenRepoFixtureAsync("empty_standard");

        using GitStatusList list = await repo.StatusNewAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, list.EntryCount);
    }

    [Fact]
    public async Task SingleFile_ExistingFile_ReturnsStatus()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        // modified_file is workdir-modified relative to index.
        GitStatusFlags st = await repo.StatusFileAsync("modified_file", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitStatusFlags.WorkdirModified, st);
    }

    [Fact]
    public async Task SingleFile_NewFile_ReturnsWorkdirNew()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        GitStatusFlags st = await repo.StatusFileAsync("new_file", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitStatusFlags.WorkdirNew, st);
    }

    [Fact]
    public async Task SingleFile_StagedNewFile_ReturnsIndexNew()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        GitStatusFlags st = await repo.StatusFileAsync("staged_new_file", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitStatusFlags.IndexNew, st);
    }

    [Fact]
    public async Task SingleFile_Nonexistent_ThrowsNotFound()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        GitException ex = await Assert.ThrowsAsync<GitException>(
            async () => await repo.StatusFileAsync("does_not_exist", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(GitErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task ShouldIgnore_IgnoredFile_ReturnsTrue()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        Assert.True(await repo.StatusShouldIgnoreAsync("ignored_file", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ShouldIgnore_TrackedFile_ReturnsFalse()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        Assert.False(await repo.StatusShouldIgnoreAsync("modified_file", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ShouldIgnore_NewFile_ReturnsFalse()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        Assert.False(await repo.StatusShouldIgnoreAsync("new_file", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ShouldIgnore_GitDirectory_ReturnsTrue()
    {
        await using GitRepository repo = await OpenFixtureRepoAsync("status");

        Assert.True(await repo.StatusShouldIgnoreAsync(".git", cancellationToken: TestContext.Current.CancellationToken));
    }
}
